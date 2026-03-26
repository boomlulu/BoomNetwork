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

// Router 消息路由器 — 三层 Cmd 分发
//
//	Core:     按 msg.Cmd (byte) 路由
//	Extended: 按 msg.ExtCmd (uint16) 路由
//	Game:     统一走 gameHandler（服务器透传）
type Router struct {
	mu          sync.RWMutex
	core        map[byte]Handler
	ext         map[uint16]Handler
	gameHandler Handler
	fallback    Handler
}

// NewRouter 创建路由器
func NewRouter() *Router {
	return &Router{
		core: make(map[byte]Handler),
		ext:  make(map[uint16]Handler),
	}
}

// OnCore 注册 Core Cmd 处理函数 (CmdType=0, cmd 0-15)
func (r *Router) OnCore(cmd byte, handler Handler) {
	r.mu.Lock()
	r.core[cmd] = handler
	r.mu.Unlock()
}

// On 是 OnCore 的别名，兼容旧代码
func (r *Router) On(cmd byte, handler Handler) {
	r.OnCore(cmd, handler)
}

// OnExt 注册 Extended Cmd 处理函数 (CmdType=1, extCmd uint16)
func (r *Router) OnExt(extCmd uint16, handler Handler) {
	r.mu.Lock()
	r.ext[extCmd] = handler
	r.mu.Unlock()
}

// OnGame 注册 Game 消息统一处理函数 (CmdType=2, 服务器透传)
func (r *Router) OnGame(handler Handler) {
	r.mu.Lock()
	r.gameHandler = handler
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
	defer r.mu.RUnlock()

	switch msg.CmdType {
	case codec.CmdTypeCore:
		if h, ok := r.core[msg.Cmd]; ok {
			return h(conn, msg)
		}
	case codec.CmdTypeExtended:
		if h, ok := r.ext[msg.ExtCmd]; ok {
			return h(conn, msg)
		}
	case codec.CmdTypeGame:
		if r.gameHandler != nil {
			return r.gameHandler(conn, msg)
		}
	}

	if r.fallback != nil {
		return r.fallback(conn, msg)
	}

	fmt.Printf("[Router] No handler for CmdType=%d Cmd=%d ExtCmd=%d GameCmd=%d\n",
		msg.CmdType, msg.Cmd, msg.ExtCmd, msg.GameCmd)
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
