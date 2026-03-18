package session

import (
	"fmt"
	"sync"

	"github.com/boom/boomnetwork/codec"
	"github.com/boom/boomnetwork/transport"
)

// Handler 消息处理函数
// 返回 *codec.Message 作为响应，返回 nil 表示不回复
type Handler func(conn *transport.Conn, msg *codec.Message) *codec.Message

// Router 消息路由器 — 根据 Cmd 分发到对应 Handler
type Router struct {
	mu       sync.RWMutex
	handlers map[byte]Handler
	fallback Handler
}

// NewRouter 创建路由器
func NewRouter() *Router {
	return &Router{
		handlers: make(map[byte]Handler),
	}
}

// On 注册指定 Cmd 的处理函数
func (r *Router) On(cmd byte, handler Handler) {
	r.mu.Lock()
	r.handlers[cmd] = handler
	r.mu.Unlock()
}

// OnFallback 注册未匹配 Cmd 的兜底处理
func (r *Router) OnFallback(handler Handler) {
	r.mu.Lock()
	r.fallback = handler
	r.mu.Unlock()
}

// Dispatch 分发消息，返回响应（可能为 nil）
func (r *Router) Dispatch(conn *transport.Conn, msg *codec.Message) *codec.Message {
	r.mu.RLock()
	h, ok := r.handlers[msg.Cmd]
	fb := r.fallback
	r.mu.RUnlock()

	if ok {
		return h(conn, msg)
	}
	if fb != nil {
		return fb(conn, msg)
	}

	fmt.Printf("[Router] No handler for Cmd=%d\n", msg.Cmd)
	return nil
}

// AsTransportHandler 转换为 transport.Handler，自动处理响应发送和 ClientSeq 回传
func (r *Router) AsTransportHandler() transport.Handler {
	return func(conn *transport.Conn, msg *codec.Message) {
		rsp := r.Dispatch(conn, msg)
		if rsp == nil {
			return
		}
		// 响应消息的 Seq 必须和请求一致，客户端靠这个匹配
		rsp.HasSeq = msg.HasSeq
		rsp.Seq = msg.Seq
		if err := conn.Send(rsp); err != nil {
			fmt.Printf("[Router] Send response to client %d failed: %v\n", conn.ID, err)
		}
	}
}
