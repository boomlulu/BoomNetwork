package transport

import (
	"fmt"
	"net"
	"sync"
	"time"

	"github.com/boom/boomnetwork/codec"
)

// Conn 代表一个客户端连接
type Conn struct {
	ID          int
	conn        net.Conn
	writer      *codec.FrameWriter
	mu          sync.Mutex
	rateLimiter *RateLimiter
}

// Send 发送一条消息给该连接
func (c *Conn) Send(msg *codec.Message) error {
	c.mu.Lock()
	defer c.mu.Unlock()
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
	listener     net.Listener
	handler      Handler
	config       ServerConfig
	security     SecurityConfig
	nextID       int
	mu           sync.Mutex
	conns        map[int]*Conn
	onDisconnect func(*Conn)
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
		handler:  handler,
		config:   cfg,
		security: DefaultSecurityConfig(),
		conns:    make(map[int]*Conn),
	}
}

// Listen 开始监听指定地址
func (s *TcpServer) Listen(addr string) error {
	ln, err := net.Listen("tcp", addr)
	if err != nil {
		return fmt.Errorf("listen %s: %w", addr, err)
	}
	s.listener = ln
	fmt.Printf("[Server] Listening on %s\n", addr)

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

func (s *TcpServer) acceptLoop() {
	for {
		raw, err := s.listener.Accept()
		if err != nil {
			return
		}

		// TCP NoDelay + KeepAlive
		if tcp, ok := raw.(*net.TCPConn); ok {
			tcp.SetNoDelay(true)
			tcp.SetKeepAlive(true)
			tcp.SetKeepAlivePeriod(30 * time.Second)
		}

		s.mu.Lock()
		s.nextID++
		c := &Conn{
			ID:          s.nextID,
			conn:        raw,
			writer:      codec.NewFrameWriter(raw),
			rateLimiter: NewRateLimiter(s.security.MaxMessagesPerSec),
		}
		s.conns[c.ID] = c
		s.mu.Unlock()

		fmt.Printf("[Server] Client %d connected from %s\n", c.ID, raw.RemoteAddr())
		go s.handleConn(c)
	}
}

func (s *TcpServer) handleConn(c *Conn) {
	defer func() {
		s.mu.Lock()
		delete(s.conns, c.ID)
		s.mu.Unlock()
		if s.onDisconnect != nil {
			s.onDisconnect(c)
		}
		c.Close()
		fmt.Printf("[Server] Client %d disconnected\n", c.ID)
	}()

	reader := codec.NewFrameReader(c.conn)
	if s.security.MaxMessageSize > 0 {
		reader.SetMaxMessageSize(s.security.MaxMessageSize)
	}

	for {
		// 设置读超时
		if s.config.ReadTimeout > 0 {
			c.conn.SetReadDeadline(time.Now().Add(s.config.ReadTimeout))
		}

		msg, err := reader.ReadMessageCopy()
		if err != nil {
			return
		}

		// 速率限制
		if c.rateLimiter != nil && !c.rateLimiter.Allow() {
			fmt.Printf("[Server] Client %d rate limited, disconnecting\n", c.ID)
			return
		}

		s.handler(c, msg)
	}
}
