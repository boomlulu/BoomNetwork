package transport

import (
	"errors"
	"fmt"
	"io"
	"log/slog"
	"net"
	"net/http"
	"sync"
	"time"

	"github.com/boomlulu/boomnetwork/codec"
	"github.com/gorilla/websocket"
)

// ── wsConnAdapter: 将 gorilla/websocket.Conn 适配为 net.Conn ──

// wsConnAdapter 将 WebSocket 连接包装为 net.Conn，使得上层可以直接复用
// FrameReader/FrameWriter 进行消息编解码，与 TCP/KCP 完全一致。
type wsConnAdapter struct {
	ws     *websocket.Conn
	reader io.Reader // 当前 message 的 reader（跨 Read 调用延续）
}

func (a *wsConnAdapter) Read(p []byte) (int, error) {
	for {
		// 如果上一条 message 还有剩余数据，继续读
		if a.reader != nil {
			n, err := a.reader.Read(p)
			if n > 0 {
				return n, nil
			}
			if errors.Is(err, io.EOF) {
				// 当前 message 读完，取下一条
				a.reader = nil
				continue
			}
			return 0, err
		}

		// 取下一条 WebSocket message
		msgType, r, err := a.ws.NextReader()
		if err != nil {
			return 0, err
		}
		if msgType != websocket.BinaryMessage {
			// 忽略非 binary 消息（如 TextMessage / PingMessage）
			continue
		}
		a.reader = r
	}
}

func (a *wsConnAdapter) Write(p []byte) (int, error) {
	err := a.ws.WriteMessage(websocket.BinaryMessage, p)
	if err != nil {
		return 0, err
	}
	return len(p), nil
}

func (a *wsConnAdapter) Close() error {
	return a.ws.Close()
}

func (a *wsConnAdapter) LocalAddr() net.Addr {
	return a.ws.LocalAddr()
}

func (a *wsConnAdapter) RemoteAddr() net.Addr {
	return a.ws.RemoteAddr()
}

func (a *wsConnAdapter) SetDeadline(t time.Time) error {
	if err := a.ws.SetReadDeadline(t); err != nil {
		return err
	}
	return a.ws.SetWriteDeadline(t)
}

func (a *wsConnAdapter) SetReadDeadline(t time.Time) error {
	return a.ws.SetReadDeadline(t)
}

func (a *wsConnAdapter) SetWriteDeadline(t time.Time) error {
	return a.ws.SetWriteDeadline(t)
}

// 编译期断言：wsConnAdapter 实现 net.Conn
var _ net.Conn = (*wsConnAdapter)(nil)

// ── WsServer: WebSocket 服务器 ──

// checkOrigin 根据安全配置返回 Origin 检查函数
func checkOrigin(allowed []string) func(r *http.Request) bool {
	if len(allowed) == 0 {
		return func(r *http.Request) bool { return true } // 向后兼容
	}
	set := make(map[string]struct{}, len(allowed))
	for _, o := range allowed {
		set[o] = struct{}{}
	}
	return func(r *http.Request) bool {
		origin := r.Header.Get("Origin")
		_, ok := set[origin]
		if !ok {
			slog.Warn("ws origin rejected", "origin", origin)
		}
		return ok
	}
}

// WsServer WebSocket 服务器
type WsServer struct {
	httpServer      *http.Server
	handler         Handler
	config          ServerConfig
	security        SecurityConfig
	upgrader        websocket.Upgrader
	nextID          int
	mu              sync.Mutex
	conns           map[int]*Conn
	maxConns        int // 0 = unlimited
	onDisconnect    func(*Conn)
	onRateLimited   func(*Conn)
	onRateLimitWarn func(*Conn)
	wg              sync.WaitGroup
	ipLimiter       *IPRateLimiter
}

// CleanupIPLimiter 清理超过 maxAge 未活跃的 IP 条目，防止公网扫描导致内存泄漏。返回删除条数。
func (s *WsServer) CleanupIPLimiter(maxAge time.Duration) int {
	return s.ipLimiter.Cleanup(maxAge)
}

// SetMaxConns 设置最大连接数（0 = 不限制）
func (s *WsServer) SetMaxConns(n int) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.maxConns = n
}

// SetOnDisconnect 设置断开连接回调
func (s *WsServer) SetOnDisconnect(fn func(*Conn)) {
	s.onDisconnect = fn
}

// SetOnRateLimited 设置限流回调
func (s *WsServer) SetOnRateLimited(fn func(*Conn)) {
	s.onRateLimited = fn
}

// SetOnRateLimitWarn 设置限流警告回调
func (s *WsServer) SetOnRateLimitWarn(fn func(*Conn)) {
	s.onRateLimitWarn = fn
}

// SetSecurity 设置安全配置
func (s *WsServer) SetSecurity(cfg SecurityConfig) {
	s.security = cfg
	s.upgrader.CheckOrigin = checkOrigin(cfg.AllowedOrigins)
}

// NewWsServer 创建 WebSocket 服务器
func NewWsServer(handler Handler, configs ...ServerConfig) *WsServer {
	cfg := DefaultServerConfig()
	if len(configs) > 0 {
		cfg = configs[0]
	}
	sec := DefaultSecurityConfig()
	return &WsServer{
		handler:  handler,
		config:   cfg,
		security: sec,
		upgrader: websocket.Upgrader{
			CheckOrigin: checkOrigin(sec.AllowedOrigins),
		},
		conns:     make(map[int]*Conn),
		ipLimiter: NewIPRateLimiter(10),
	}
}

// Listen 开始监听指定地址
func (s *WsServer) Listen(addr string) error {
	mux := http.NewServeMux()
	mux.HandleFunc("/", s.handleUpgrade)

	s.httpServer = &http.Server{
		Addr:    addr,
		Handler: mux,
	}

	ln, err := net.Listen("tcp", addr)
	if err != nil {
		return fmt.Errorf("ws listen %s: %w", addr, err)
	}

	slog.Info("listening", "component", "ws", "addr", addr)
	go s.httpServer.Serve(ln)
	return nil
}

// Close 关闭服务器
func (s *WsServer) Close() {
	if s.httpServer != nil {
		s.httpServer.Close()
	}
	s.mu.Lock()
	for _, c := range s.conns {
		c.Close()
	}
	s.mu.Unlock()
}

// ConnCount 当前连接数
func (s *WsServer) ConnCount() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return len(s.conns)
}

// Wait 等待所有连接 goroutine 退出
func (s *WsServer) Wait() {
	s.wg.Wait()
}

// stringAddr 将字符串包装为 net.Addr（用于 HTTP RemoteAddr → IPRateLimiter）
type stringAddr string

func (a stringAddr) Network() string { return "tcp" }
func (a stringAddr) String() string  { return string(a) }

func (s *WsServer) handleUpgrade(w http.ResponseWriter, r *http.Request) {
	// per-IP 速率检查
	if !s.ipLimiter.Allow(stringAddr(r.RemoteAddr)) {
		http.Error(w, "rate limited", http.StatusTooManyRequests)
		return
	}

	wsConn, err := s.upgrader.Upgrade(w, r, nil)
	if err != nil {
		slog.Warn("websocket upgrade failed", "component", "ws", "err", err)
		return
	}

	// 将 websocket.Conn 适配为 net.Conn
	adapted := &wsConnAdapter{ws: wsConn}

	s.mu.Lock()
	if s.maxConns > 0 && len(s.conns) >= s.maxConns {
		s.mu.Unlock()
		slog.Warn("connection rejected: max connections reached", "component", "ws", "maxConns", s.maxConns)
		wsConn.Close()
		return
	}
	s.nextID++
	c := &Conn{
		ID:           s.nextID,
		conn:         adapted,
		writer:       codec.NewFrameWriter(adapted),
		rateLimiter:  NewRateLimiter(s.security.MaxMessagesPerSec),
		writeTimeout: s.config.WriteTimeout,
	}
	s.conns[c.ID] = c
	s.mu.Unlock()

	slog.Info("client connected", "component", "ws", "connId", c.ID, "addr", r.RemoteAddr)
	s.wg.Add(1)
	go s.handleConn(c)
}

func (s *WsServer) handleConn(c *Conn) {
	defer func() {
		s.wg.Done()
		s.mu.Lock()
		delete(s.conns, c.ID)
		s.mu.Unlock()
		if s.onDisconnect != nil {
			s.onDisconnect(c)
		}
		c.Close()
		slog.Info("client disconnected", "component", "ws", "connId", c.ID)
	}()

	reader := codec.NewFrameReader(c.conn, s.security.MaxMessageSize)

	for {
		if s.config.ReadTimeout > 0 {
			c.conn.SetReadDeadline(time.Now().Add(s.config.ReadTimeout))
		}

		msg, err := reader.ReadMessageCopy()
		if err != nil {
			return
		}

		// 速率限制（软着陆）
		if c.rateLimiter != nil {
			switch c.rateLimiter.AllowLevel() {
			case RateLevelDeny:
				slog.Error("client rate limited, disconnecting",
					"component", "ws", "connId", c.ID,
					"cmdType", msg.CmdType, "cmd", msg.Cmd, "extCmd", msg.ExtCmd)
				if s.onRateLimited != nil {
					s.onRateLimited(c)
				}
				return
			case RateLevelWarn:
				slog.Warn("client rate limit warn",
					"component", "ws", "connId", c.ID,
					"cmdType", msg.CmdType, "cmd", msg.Cmd, "extCmd", msg.ExtCmd)
				if s.onRateLimitWarn != nil {
					s.onRateLimitWarn(c)
				}
			}
		}

		s.handler(c, msg)
	}
}

// 确保 WsServer 实现 Server 接口
var _ Server = (*WsServer)(nil)
