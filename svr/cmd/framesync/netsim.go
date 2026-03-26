package main

import (
	"math/rand"
	"sync"
	"sync/atomic"
	"time"

	"github.com/boom/boomnetwork/codec"
	"github.com/boom/boomnetwork/framesync"
)

// NetSimConfig 网络状态模拟配置
type NetSimConfig struct {
	LatencyMs   int32 `json:"latency_ms"`    // 基础延迟（毫秒）
	JitterMs    int32 `json:"jitter_ms"`     // 抖动范围 0..JitterMs（毫秒）
	LossPercent int32 `json:"loss_percent"`  // 丢包率 0-100
	Enabled     int32 `json:"enabled"`       // 1=启用, 0=关闭（原子操作）
}

func (c *NetSimConfig) IsEnabled() bool {
	return atomic.LoadInt32(&c.Enabled) == 1
}

func (c *NetSimConfig) SetEnabled(v bool) {
	if v {
		atomic.StoreInt32(&c.Enabled, 1)
	} else {
		atomic.StoreInt32(&c.Enabled, 0)
	}
}

// 全局网络模拟配置
var GlobalNetSim = &NetSimConfig{}

// 模拟统计
var (
	simDropped  int64 // 丢弃的消息数
	simDelayed  int64 // 延迟的消息数
	simPending  int64 // 当前排队中的延迟消息数
	simMu       sync.Mutex
	simRand     = rand.New(rand.NewSource(time.Now().UnixNano()))
)

// simPendingMax 超过此阈值时降级为直接发送，防止 timer 积压 OOM
const simPendingMax = 10000

func simRandIntn(n int) int {
	simMu.Lock()
	v := simRand.Intn(n)
	simMu.Unlock()
	return v
}

// simConn 网络模拟层 — 包装 PlayerConn，模拟延迟和丢包
//
// 链路：statsConn → simConn → real conn
// 只模拟 S→C（出站），C→S（入站）不模拟（避免 TCP 乱序）
type simConn struct {
	inner framesync.PlayerConn
	cfg   *NetSimConfig
}

func (sc *simConn) Send(msg *codec.Message) error {
	if !sc.cfg.IsEnabled() {
		return sc.inner.Send(msg)
	}

	// 丢包
	loss := int(atomic.LoadInt32(&sc.cfg.LossPercent))
	if loss > 0 && simRandIntn(100) < loss {
		atomic.AddInt64(&simDropped, 1)
		return nil // 静默丢弃
	}

	// 延迟
	latency := int(atomic.LoadInt32(&sc.cfg.LatencyMs))
	jitter := int(atomic.LoadInt32(&sc.cfg.JitterMs))
	delay := latency
	if jitter > 0 {
		delay += simRandIntn(jitter)
	}

	if delay > 0 {
		// 积压保护：pending timer 过多时降级为直接发送
		if atomic.LoadInt64(&simPending) >= simPendingMax {
			return sc.inner.Send(msg)
		}

		atomic.AddInt64(&simDelayed, 1)
		atomic.AddInt64(&simPending, 1)
		// 必须拷贝 Data（原 buffer 可能被 Room tick 复用）
		dataCopy := make([]byte, len(msg.Data))
		copy(dataCopy, msg.Data)
		msgCopy := &codec.Message{CmdType: msg.CmdType, Cmd: msg.Cmd, ExtCmd: msg.ExtCmd, GameCmd: msg.GameCmd, Data: dataCopy}

		time.AfterFunc(time.Duration(delay)*time.Millisecond, func() {
			atomic.AddInt64(&simPending, -1)
			sc.inner.Send(msgCopy)
		})
		return nil
	}

	return sc.inner.Send(msg)
}
