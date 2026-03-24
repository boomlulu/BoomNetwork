package main

import (
	"sync"
	"sync/atomic"
	"time"

	"github.com/boom/boomnetwork/codec"
	"github.com/boom/boomnetwork/framesync"
	"github.com/boom/boomnetwork/session"
	"github.com/boom/boomnetwork/transport"
)

// ===================== 流量统计 =====================

const ringSize = 60

type secondBucket struct {
	sec int64
	rx  int64
	tx  int64
}

// TrafficTracker 并发安全的流量统计器（环形缓冲 60×1s 槽）
type TrafficTracker struct {
	rxTotal int64
	txTotal int64
	mu      sync.Mutex
	ring    [ringSize]secondBucket
}

func (t *TrafficTracker) RecordRx(n int64) {
	if n <= 0 {
		return
	}
	atomic.AddInt64(&t.rxTotal, n)
	t.addBucket(n, 0)
}

func (t *TrafficTracker) RecordTx(n int64) {
	if n <= 0 {
		return
	}
	atomic.AddInt64(&t.txTotal, n)
	t.addBucket(0, n)
}

func (t *TrafficTracker) addBucket(rx, tx int64) {
	now := time.Now().Unix()
	idx := now % ringSize
	t.mu.Lock()
	b := &t.ring[idx]
	if b.sec == now {
		b.rx += rx
		b.tx += tx
	} else {
		*b = secondBucket{sec: now, rx: rx, tx: tx}
	}
	t.mu.Unlock()
}

type TrafficSnapshot struct {
	RxTotal int64 `json:"rx_total"`
	TxTotal int64 `json:"tx_total"`
	Rx1Min  int64 `json:"rx_1min"`
	Tx1Min  int64 `json:"tx_1min"`
	Rx5Sec  int64 `json:"rx_5sec"`
	Tx5Sec  int64 `json:"tx_5sec"`
}

func (t *TrafficTracker) Snapshot() TrafficSnapshot {
	now := time.Now().Unix()
	cut1min := now - 60
	cut5sec := now - 5
	var rx1, tx1, rx5, tx5 int64
	t.mu.Lock()
	for i := 0; i < ringSize; i++ {
		b := t.ring[i]
		if b.sec <= cut1min || b.sec > now {
			continue
		}
		rx1 += b.rx
		tx1 += b.tx
		if b.sec > cut5sec {
			rx5 += b.rx
			tx5 += b.tx
		}
	}
	t.mu.Unlock()
	return TrafficSnapshot{
		RxTotal: atomic.LoadInt64(&t.rxTotal),
		TxTotal: atomic.LoadInt64(&t.txTotal),
		Rx1Min:  rx1, Tx1Min: tx1,
		Rx5Sec:  rx5, Tx5Sec: tx5,
	}
}

// ===================== 消息日志 =====================

const msgRingSize = 100

// MsgEntry 单条网络消息记录
type MsgEntry struct {
	Ts   int64  `json:"ts"`   // unix ms
	Dir  string `json:"dir"`  // "rx" / "tx"
	Cmd  byte   `json:"cmd"`  // 协议命令号
	Name string `json:"name"` // 命令名（人可读）
	Pid  int32  `json:"pid"`  // 玩家 ID (0=未知)
	Size int    `json:"size"` // 数据字节数
}

type msgRing struct {
	mu   sync.Mutex
	ring [msgRingSize]MsgEntry
	pos  int // 下次写入位置
	len  int // 当前有效条目数
}

func (r *msgRing) Push(e MsgEntry) {
	r.mu.Lock()
	r.ring[r.pos] = e
	r.pos = (r.pos + 1) % msgRingSize
	if r.len < msgRingSize {
		r.len++
	}
	r.mu.Unlock()
}

// Recent 返回最近 limit 条（时间倒序）
func (r *msgRing) Recent(limit int) []MsgEntry {
	r.mu.Lock()
	defer r.mu.Unlock()
	if limit <= 0 || limit > r.len {
		limit = r.len
	}
	result := make([]MsgEntry, limit)
	for i := 0; i < limit; i++ {
		idx := (r.pos - 1 - i + msgRingSize) % msgRingSize
		result[i] = r.ring[idx]
	}
	return result
}

// ===================== 全局实例 =====================

var (
	GameStats = &TrafficTracker{} // 游戏协议流量 (TCP/KCP)
	GmStats   = &TrafficTracker{} // GM/Admin HTTP 流量
	MsgLog    = &msgRing{}
)

// LogMsg 记录一条网络消息到环形缓冲
func LogMsg(dir string, cmd byte, pid int32, dataSize int) {
	MsgLog.Push(MsgEntry{
		Ts:   time.Now().UnixMilli(),
		Dir:  dir,
		Cmd:  cmd,
		Name: CmdName(cmd),
		Pid:  pid,
		Size: dataSize,
	})
}

// ===================== Handler 包装器 =====================

// txStats 包装 router handler：计入 TX 游戏流量 + 记录消息日志
func txStats(h session.Handler) session.Handler {
	return func(conn *transport.Conn, msg *codec.Message) *codec.Message {
		rsp := h(conn, msg)
		if rsp != nil {
			GameStats.RecordTx(int64(len(rsp.Data)))
			pid := connPid(conn)
			LogMsg("tx", rsp.Cmd, pid, len(rsp.Data))
		}
		return rsp
	}
}

// statsConn 包装 Room 连接：计入 TX 游戏流量 + 记录消息日志
type statsConn struct {
	inner framesync.PlayerConn
	pid   int32
}

func (sc *statsConn) Send(msg *codec.Message) error {
	GameStats.RecordTx(int64(len(msg.Data)))
	LogMsg("tx", msg.Cmd, sc.pid, len(msg.Data))
	return sc.inner.Send(msg)
}

// connPid 从 connPlayerMap 取 playerId
func connPid(conn *transport.Conn) int32 {
	if v, ok := connPlayerMap.Load(conn.ID); ok {
		return v.(int32)
	}
	return 0
}

// ===================== Cmd 名称映射 =====================

var cmdNames = map[byte]string{
	1:  "SessionBind",
	2:  "SessionBindRsp",
	3:  "RequestStart",
	4:  "StartFrameSync",
	5:  "FrameInput",
	6:  "PushFrames",
	7:  "Heartbeat",
	8:  "HeartbeatRsp",
	9:  "Reconnect",
	10: "ReconnectRsp",
	11: "GetRooms",
	12: "GetRoomsRsp",
	13: "CreateRoom",
	14: "CreateRoomRsp",
	15: "JoinRoom",
	16: "JoinRoomRsp",
	17: "LeaveRoom",
	18: "LeaveRoomRsp",
	19: "PlayerJoined",
	20: "PlayerLeft",
	21: "StopFrameSync",
	22: "UploadSnapshot",
	23: "UploadSnapshotRsp",
	24: "PlayerOffline",
	25: "PlayerOnline",
	26: "RoomSnapshot",
}

func CmdName(cmd byte) string {
	if name, ok := cmdNames[cmd]; ok {
		return name
	}
	return "Unknown"
}
