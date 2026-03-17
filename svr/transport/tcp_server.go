package transport

import (
	"fmt"
	"net"
	"sync"

	"github.com/boom/boomnetwork/codec"
)

// Conn 代表一个客户端连接
type Conn struct {
	ID   int
	conn net.Conn
	mu   sync.Mutex
}

// Send 发送一条消息给该连接
func (c *Conn) Send(msg *codec.Message) error {
	c.mu.Lock()
	defer c.mu.Unlock()
	return codec.WriteMessage(c.conn, msg)
}

// Close 关闭连接
func (c *Conn) Close() error {
	return c.conn.Close()
}

// Handler 消息处理回调
type Handler func(conn *Conn, msg *codec.Message)

// TcpServer TCP 服务器
type TcpServer struct {
	listener net.Listener
	handler  Handler
	nextID   int
	mu       sync.Mutex
	conns    map[int]*Conn
}

// NewTcpServer 创建 TCP 服务器
func NewTcpServer(handler Handler) *TcpServer {
	return &TcpServer{
		handler: handler,
		conns:   make(map[int]*Conn),
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
			return // listener closed
		}

		s.mu.Lock()
		s.nextID++
		c := &Conn{ID: s.nextID, conn: raw}
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
		c.Close()
		fmt.Printf("[Server] Client %d disconnected\n", c.ID)
	}()

	for {
		msg, err := codec.ReadMessage(c.conn)
		if err != nil {
			return
		}
		s.handler(c, msg)
	}
}
