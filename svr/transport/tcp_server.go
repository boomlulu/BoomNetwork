package transport

import (
	"fmt"
	"log/slog"
	"net"
	"sync"
	"time"

	"github.com/boomlulu/boomnetwork/codec"
)

// Conn 代表一个客户端连接
type Conn struct {
	ID           int
	conn         net.Conn
	writer       *codec.FrameWriter
	mu           sync.Mutex
	rateLimiter  *RateLimiter
	writeTimeout time.Duration // C1 fix: 0=无超时（向后兼容），>0=每次 Send 前设置写截止时间
}

// Send 发送一条消息给该连接。
// C1 fix: 若 writeTimeout > 0，在 Flush 前设置写截止时间，防止慢客户端无限阻塞广播循环。
// 发送完成（成功或失败）后清除截止时间，不影响连接上的后续读操作。
func (c *Conn) Send(msg *codec.Message) error {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.writeTimeout > 0 {
		if err := c.conn.SetWriteDeadline(time.Now().Add(c.writeTimeout)); err != nil {
			return err
		}
		defer c.conn.SetWriteDeadline(time.Time{}) //nolint:errcheck
	}
	if err := c.writer.WriteMessage(msg); err != nil {
		return err
	}
	return c.writer.Flush()
}

// Close 关闭连接
func (c *Conn) Close() error {
	return c.conn.Close()
}

// RemoteAddr 远程地址
func (c *Conn) RemoteAddr() net.Addr {
	return c.conn.RemoteAddr()
}

// Handler 消息处理回调
type Handler func(conn *Conn, msg *codec.Message)

// ServerConfig 服务器配置
type ServerConfig struct {
	ReadTimeout  time.Duration // 读超时，0 表示无限制
	WriteTimeout time.Duration // 写超时，0 表示无限制
}

// DefaultServerConfig 默认配置
func DefaultServerConfig() ServerConfig {
	return ServerConfig{
		ReadTimeout:  60 * time.Second,
		WriteTimeout: 10 * time.Second,
	}
}

// TcpServer TCP 服务器
type TcpServer struct {
	listener      net.Listener
	handler       Handler
	config        ServerConfig
	security      SecurityConfig
	nextID        int
	mu            sync.Mutex
	conns         map[int]*Conn
	maxConns      int // 0 = unlimited
	onDisconnect    func(*Conn)
	onRateLimited   func(*Conn) // 触发限流时回调（断连前通知客户端）
	onRateLimitWarn func(*Conn) // 接近限流时回调（发送警告，不断连）
	wg            sync.WaitGroup
	ipLimiter     *IPRateLimiter // S18: per-IP 连接速率限制
}

// SetMaxConns 设置最大连接数（0 = 不限制）
func (s *TcpServer) SetMaxConns(n int) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.maxConns = n
}

// SetOnRateLimited 设置限流回调
func (s *TcpServer) SetOnRateLimited(fn func(*Conn)) {
	s.onRateLimited = fn
}

// SetOnRateLimitWarn 设置限流警告回调（接近上限时触发，不断连）
func (s *TcpServer) SetOnRateLimitWarn(fn func(*Conn)) {
	s.onRateLimitWarn = fn
}

// SetSecurity 设置安全配置
func (s *TcpServer) SetSecurity(cfg SecurityConfig) {
	s.security = cfg
	codec.MaxMessageSize = cfg.MaxMessageSize
}

// SetOnDisconnect 设置断开连接回调
func (s *TcpServer) SetOnDisconnect(fn func(*Conn)) {
	s.onDisconnect = fn
}

// NewTcpServer 创建 TCP 服务器
func NewTcpServer(handler Handler, configs ...ServerConfig) *TcpServer {
	cfg := DefaultServerConfig()
	if len(configs) > 0 {
		cfg = configs[0]
	}
	return &TcpServer{
		handler:   handler,
		config:    cfg,
		security:  DefaultSecurityConfig(),
		conns:     make(map[int]*Conn),
		ipLimiter: NewIPRateLimiter(10), // S18: 默认每 IP 每秒最多 10 个新连接
	}
}

// Listen 开始监听指定地址
func (s *TcpServer) Listen(addr string) error {
	ln, err := net.Listen("tcp", addr)
	if err != nil {
		return fmt.Errorf("listen %s: %w", addr, err)
	}
	s.listener = ln
	slog.Info("listening", "component", "tcp", "addr", addr)

	go s.acceptLoop()
	return nil
}

// Close 关闭服务器
func (s *TcpServer) Close() {
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
func (s *TcpServer) ConnCount() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return len(s.conns)
}

// Wait 等待所有连接 goroutine 退出（配合 Close 使用实现优雅关闭）
func (s *TcpServer) Wait() {
	s.wg.Wait()
}

func (s *TcpServer) acceptLoop() {
	for {
		raw, err := s.listener.Accept()
		if err != nil {
			return
		}

		// S18: per-IP 连接速率检查（在创建 Conn 之前）
		if !s.ipLimiter.Allow(raw.RemoteAddr()) {
			raw.Close()
			continue
		}

		// TCP NoDelay + KeepAlive
		if tcp, ok := raw.(*net.TCPConn); ok {
			tcp.SetNoDelay(true)
			tcp.SetKeepAlive(true)
			tcp.SetKeepAlivePeriod(30 * time.Second)
		}

		s.mu.Lock()
		if s.maxConns > 0 && len(s.conns) >= s.maxConns {
			s.mu.Unlock()
			slog.Warn("connection rejected: max connections reached", "component", "tcp", "maxConns", s.maxConns)
			raw.Close()
			continue
		}
		s.nextID++
		c := &Conn{
			ID:           s.nextID,
			conn:         raw,
			writer:       codec.NewFrameWriter(raw),
			rateLimiter:  NewRateLimiter(s.security.MaxMessagesPerSec),
			writeTimeout: s.config.WriteTimeout, // C1 fix: 从 ServerConfig 传入写超时
		}
		s.conns[c.ID] = c
		s.mu.Unlock()

		slog.Info("client connected", "component", "tcp", "connId", c.ID, "addr", raw.RemoteAddr())
		s.wg.Add(1)
		go s.handleConn(c)
	}
}

func (s *TcpServer) handleConn(c *Conn) {
	defer func() {
		s.wg.Done()
		s.mu.Lock()
		delete(s.conns, c.ID)
		s.mu.Unlock()
		if s.onDisconnect != nil {
			s.onDisconnect(c)
		}
		c.Close()
		slog.Info("client disconnected", "component", "tcp", "connId", c.ID)
	}()

	reader := codec.NewFrameReader(c.conn)
	if s.security.MaxMessageSize > 0 {
		reader.SetMaxMessageSize(s.security.MaxMessageSize)
	}

	// Connection health: ReadDeadline (60s default) kills silent connections.
	// Combined with TCP KeepAlive (30s), this detects dead peers without
	// a separate heartbeat goroutine. The client sends periodic heartbeats
	// to reset the deadline.
	for {
		// 设置读超时
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
				slog.Error("client rate limited, disconnecting", "component", "tcp", "connId", c.ID)
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
