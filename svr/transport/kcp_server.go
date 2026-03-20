package transport

import (
	"fmt"
	"net"
	"sync"
	"time"

	"github.com/boom/boomnetwork/codec"
	kcp "github.com/xtaci/kcp-go/v5"
)

// KcpServer KCP 服务器
type KcpServer struct {
	listener     *kcp.Listener
	handler      Handler
	config       ServerConfig
	nextID       int
	mu           sync.Mutex
	conns        map[int]*Conn
	onDisconnect func(*Conn)
}

// SetOnDisconnect 设置断开连接回调
func (s *KcpServer) SetOnDisconnect(fn func(*Conn)) {
	s.onDisconnect = fn
}

// NewKcpServer 创建 KCP 服务器
func NewKcpServer(handler Handler, configs ...ServerConfig) *KcpServer {
	cfg := DefaultServerConfig()
	if len(configs) > 0 {
		cfg = configs[0]
	}
	return &KcpServer{
		handler: handler,
		config:  cfg,
		conns:   make(map[int]*Conn),
	}
}

// Listen 开始监听
func (s *KcpServer) Listen(addr string) error {
	ln, err := kcp.ListenWithOptions(addr, nil, 0, 0)
	if err != nil {
		return fmt.Errorf("kcp listen %s: %w", addr, err)
	}
	s.listener = ln
	fmt.Printf("[KcpServer] Listening on %s\n", addr)

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

func (s *KcpServer) acceptLoop() {
	for {
		raw, err := s.listener.AcceptKCP()
		if err != nil {
			return
		}

		// KCP 参数调优
		raw.SetStreamMode(true)
		raw.SetWriteDelay(false)
		raw.SetNoDelay(1, 10, 2, 1) // nodelay, interval, resend, nc
		raw.SetWindowSize(256, 256)
		raw.SetMtu(1400)

		s.mu.Lock()
		s.nextID++
		c := &Conn{
			ID:     s.nextID,
			conn:   raw,
			writer: codec.NewFrameWriter(raw),
		}
		s.conns[c.ID] = c
		s.mu.Unlock()

		fmt.Printf("[KcpServer] Client %d connected from %s\n", c.ID, raw.RemoteAddr())
		go s.handleConn(c)
	}
}

func (s *KcpServer) handleConn(c *Conn) {
	defer func() {
		s.mu.Lock()
		delete(s.conns, c.ID)
		s.mu.Unlock()
		if s.onDisconnect != nil {
			s.onDisconnect(c)
		}
		c.Close()
		fmt.Printf("[KcpServer] Client %d disconnected\n", c.ID)
	}()

	reader := codec.NewFrameReader(c.conn)

	for {
		if s.config.ReadTimeout > 0 {
			c.conn.SetReadDeadline(time.Now().Add(s.config.ReadTimeout))
		}

		msg, err := reader.ReadMessageCopy()
		if err != nil {
			return
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
	ConnCount() int
	SetOnDisconnect(fn func(*Conn))
}

// 确保 TcpServer 和 KcpServer 都实现 Server 接口
var _ Server = (*TcpServer)(nil)
var _ Server = (*KcpServer)(nil)

// NewServer 根据协议类型创建服务器
func NewServer(proto string, handler Handler, configs ...ServerConfig) Server {
	switch proto {
	case "kcp":
		return NewKcpServer(handler, configs...)
	default:
		return NewTcpServer(handler, configs...)
	}
}

// ResolveAddr 将地址字符串解析为 host:port，兼容 :port 格式
func ResolveAddr(addr string) (string, error) {
	_, _, err := net.SplitHostPort(addr)
	return addr, err
}
