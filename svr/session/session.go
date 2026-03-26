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
	mu          sync.Mutex // 仅注册阶段使用
	core        map[byte]Handler
	ext         map[uint16]Handler
	gameHandler Handler
	fallback    Handler

	// frozen 快照 — Freeze() 后 Dispatch 直接查这些字段，无锁
	frozen      bool
	fCore       map[byte]Handler
	fExt        map[uint16]Handler
	fGame       Handler
	fFallback   Handler
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

// Freeze 冻结路由表：拷贝到无锁快照，之后 Dispatch 不再加锁。
// 必须在所有 On*/OnExt/OnGame/OnFallback 注册完成后、服务器开始处理消息前调用。
func (r *Router) Freeze() {
	r.mu.Lock()
	defer r.mu.Unlock()

	r.fCore = make(map[byte]Handler, len(r.core))
	for k, v := range r.core {
		r.fCore[k] = v
	}
	r.fExt = make(map[uint16]Handler, len(r.ext))
	for k, v := range r.ext {
		r.fExt[k] = v
	}
	r.fGame = r.gameHandler
	r.fFallback = r.fallback
	r.frozen = true
}

// Dispatch 分发消息，返回响应（可能为 nil）
func (r *Router) Dispatch(conn *transport.Conn, msg *codec.Message) *codec.Message {
	if r.frozen {
		return r.dispatchFrozen(conn, msg)
	}
	// 未冻结时兼容旧路径（不应在生产中出现）
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.dispatchLocked(conn, msg, r.core, r.ext, r.gameHandler, r.fallback)
}

func (r *Router) dispatchFrozen(conn *transport.Conn, msg *codec.Message) *codec.Message {
	return r.dispatchLocked(conn, msg, r.fCore, r.fExt, r.fGame, r.fFallback)
}

func (r *Router) dispatchLocked(conn *transport.Conn, msg *codec.Message,
	core map[byte]Handler, ext map[uint16]Handler, game Handler, fallback Handler) *codec.Message {

	switch msg.CmdType {
	case codec.CmdTypeCore:
		if h, ok := core[msg.Cmd]; ok {
			return h(conn, msg)
		}
	case codec.CmdTypeExtended:
		if h, ok := ext[msg.ExtCmd]; ok {
			return h(conn, msg)
		}
	case codec.CmdTypeGame:
		if game != nil {
			return game(conn, msg)
		}
	}

	if fallback != nil {
		return fallback(conn, msg)
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
