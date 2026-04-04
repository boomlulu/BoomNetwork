package transport

import (
	"fmt"
	"log/slog"
	"net"
	"sync"
	"time"

	"github.com/boomlulu/boomnetwork/codec"
	kcp "github.com/xtaci/kcp-go/v5"
)

// KcpServer KCP 服务器
type KcpServer struct {
	listener      *kcp.Listener
	handler       Handler
	config        ServerConfig
	security      SecurityConfig
	nextID        int
	mu            sync.Mutex
	conns         map[int]*Conn
	maxConns      int // 0 = unlimited
	onDisconnect    func(*Conn)
	onRateLimited   func(*Conn)
	onRateLimitWarn func(*Conn)
	wg            sync.WaitGroup
	ipLimiter     *IPRateLimiter // S18: per-IP 连接速率限制
}

// SetMaxConns 设置最大连接数（0 = 不限制）
func (s *KcpServer) SetMaxConns(n int) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.maxConns = n
}

// SetOnDisconnect 设置断开连接回调
func (s *KcpServer) SetOnDisconnect(fn func(*Conn)) {
	s.onDisconnect = fn
}

// SetOnRateLimited 设置限流回调
func (s *KcpServer) SetOnRateLimited(fn func(*Conn)) {
	s.onRateLimited = fn
}

// SetOnRateLimitWarn 设置限流警告回调（接近上限时触发，不断连）
func (s *KcpServer) SetOnRateLimitWarn(fn func(*Conn)) {
	s.onRateLimitWarn = fn
}

// SetSecurity 设置安全配置
func (s *KcpServer) SetSecurity(cfg SecurityConfig) {
	s.security = cfg
	codec.MaxMessageSize = cfg.MaxMessageSize
}

// NewKcpServer 创建 KCP 服务器
func NewKcpServer(handler Handler, configs ...ServerConfig) *KcpServer {
	cfg := DefaultServerConfig()
	if len(configs) > 0 {
		cfg = configs[0]
	}
	return &KcpServer{
		handler:   handler,
		config:    cfg,
		security:  DefaultSecurityConfig(),
		conns:     make(map[int]*Conn),
		ipLimiter: NewIPRateLimiter(10), // S18: 默认每 IP 每秒最多 10 个新连接
	}
}

// Listen 开始监听
func (s *KcpServer) Listen(addr string) error {
	ln, err := kcp.ListenWithOptions(addr, nil, 0, 0)
	if err != nil {
		return fmt.Errorf("kcp listen %s: %w", addr, err)
	}
	s.listener = ln
	slog.Info("listening", "component", "kcp", "addr", addr)

	go s.acceptLoop()
	return nil
}

// Close 关闭服务器
func (s *KcpServer) Close() {
	if s.listener != nil {
		s.listener.Close()
	}
	s.mu.Lock()
	for _, c := range s.conns {
		c.Close()
	}
	s.mu.Unlock()
}

// ConnCount 当前连接数
func (s *KcpServer) ConnCount() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return len(s.conns)
}

// Wait 等待所有连接 goroutine 退出（配合 Close 使用实现优雅关闭）
func (s *KcpServer) Wait() {
	s.wg.Wait()
}

func (s *KcpServer) acceptLoop() {
	for {
		raw, err := s.listener.AcceptKCP()
		if err != nil {
			return
		}

		// S18: per-IP 连接速率检查（在创建 Conn 之前）
		if !s.ipLimiter.Allow(raw.RemoteAddr()) {
			raw.Close()
			continue
		}

		// KCP 参数调优
		raw.SetStreamMode(true)
		raw.SetWriteDelay(false)
		raw.SetNoDelay(1, 10, 2, 1) // nodelay, interval, resend, nc
		raw.SetWindowSize(256, 256)
		raw.SetMtu(1400)

		s.mu.Lock()
		if s.maxConns > 0 && len(s.conns) >= s.maxConns {
			s.mu.Unlock()
			slog.Warn("connection rejected: max connections reached", "component", "kcp", "maxConns", s.maxConns)
			raw.Close()
			continue
		}
		s.nextID++
		c := &Conn{
			ID:           s.nextID,
			conn:         raw,
			writer:       codec.NewFrameWriter(raw),
			rateLimiter:  NewRateLimiter(s.security.MaxMessagesPerSec),
			writeTimeout: s.config.WriteTimeout, // C1 fix: KCP 连接同样需要写超时保护
		}
		s.conns[c.ID] = c
		s.mu.Unlock()

		slog.Info("client connected", "component", "kcp", "connId", c.ID, "addr", raw.RemoteAddr())
		s.wg.Add(1)
		go s.handleConn(c)
	}
}

func (s *KcpServer) handleConn(c *Conn) {
	defer func() {
		s.wg.Done()
		s.mu.Lock()
		delete(s.conns, c.ID)
		s.mu.Unlock()
		if s.onDisconnect != nil {
			s.onDisconnect(c)
		}
		c.Close()
		slog.Info("client disconnected", "component", "kcp", "connId", c.ID)
	}()

	reader := codec.NewFrameReader(c.conn)

	// Connection health: ReadDeadline (60s default) kills silent connections.
	// KCP is UDP-based so there's no OS-level KeepAlive, but the read deadline
	// serves the same purpose. Clients must send periodic heartbeats.
	for {
		if s.config.ReadTimeout > 0 {
			c.conn.SetReadDeadline(time.Now().Add(s.config.ReadTimeout))
		}

		msg, err := reader.ReadMessageCopy()
		if err != nil {
			return
		}

		// 速率限制（软着陆：先警告，持续超限才断连）
		if c.rateLimiter != nil {
			switch c.rateLimiter.AllowLevel() {
			case RateLevelDeny:
				slog.Error("client rate limited, disconnecting", "component", "kcp", "connId", c.ID)
				if s.onRateLimited != nil {
					s.onRateLimited(c)
				}
				return
			case RateLevelWarn:
				if s.onRateLimitWarn != nil {
					s.onRateLimitWarn(c)
				}
			}
		}

		s.handler(c, msg)
	}
}

// Conn 的 RemoteAddr 需要适配 KCP（kcp.UDPSession 也实现了 net.Conn）
// 已有的 Conn struct 和 Send 方法完全兼容，无需修改

// Server 接口 — TCP 和 KCP 服务器统一接口
type Server interface {
	Listen(addr string) error
	Close()
	Wait()
	ConnCount() int
	SetOnDisconnect(fn func(*Conn))
	SetOnRateLimited(fn func(*Conn))
	SetOnRateLimitWarn(fn func(*Conn))
	SetSecurity(cfg SecurityConfig)
	SetMaxConns(n int)
}

// 确保 TcpServer 和 KcpServer 都实现 Server 接口
var _ Server = (*TcpServer)(nil)
var _ Server = (*KcpServer)(nil)

// NewServer 根据协议类型创建服务器
func NewServer(proto string, handler Handler, configs ...ServerConfig) Server {
	switch proto {
	case "kcp":
		return NewKcpServer(handler, configs...)
	case "ws":
		return NewWsServer(handler, configs...)
	default:
		return NewTcpServer(handler, configs...)
	}
}

// ResolveAddr 将地址字符串解析为 host:port，兼容 :port 格式
func ResolveAddr(addr string) (string, error) {
	_, _, err := net.SplitHostPort(addr)
	return addr, err
}
