package main

import (
	"fmt"
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
	Ts     int64  `json:"ts"`               // unix ms
	Dir    string `json:"dir"`              // "rx" / "tx"
	Cmd    byte   `json:"cmd"`              // 协议命令号
	Name   string `json:"name"`             // 命令名（人可读）
	Pid    int32  `json:"pid"`              // 玩家 ID (0=未知)
	Size   int    `json:"size"`             // 数据字节数
	Detail string `json:"detail,omitempty"` // G7: 关键消息解码摘要
}

type msgRing struct {
	mu       sync.Mutex
	ring     [msgRingSize]MsgEntry
	pos      int // 下次写入位置
	len      int // 当前有效条目数
	notifyCh chan MsgEntry // WebSocket Hub 实时通知通道（非 nil 时启用）
}

// SetNotifyCh 设置实时通知通道，Hub 启动时调用；传 nil 可清除
func (r *msgRing) SetNotifyCh(ch chan MsgEntry) {
	r.mu.Lock()
	r.notifyCh = ch
	r.mu.Unlock()
}

func (r *msgRing) Push(e MsgEntry) {
	r.mu.Lock()
	r.ring[r.pos] = e
	r.pos = (r.pos + 1) % msgRingSize
	if r.len < msgRingSize {
		r.len++
	}
	ch := r.notifyCh
	r.mu.Unlock()
	// 非阻塞发送：Hub 消费慢时丢弃实时通知，订阅者可通过重新订阅恢复
	if ch != nil {
		select {
		case ch <- e:
		default:
		}
	}
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

// LogMsg 记录一条网络消息到环形缓冲 + G8 速率统计
func LogMsg(dir string, cmd byte, pid int32, dataSize int) {
	MsgLog.Push(MsgEntry{
		Ts:   time.Now().UnixMilli(),
		Dir:  dir,
		Cmd:  cmd,
		Name: CmdName(cmd),
		Pid:  pid,
		Size: dataSize,
	})
	PlayerRates.Record(pid)
}

// LogMsgWithDetail G7: 关键消息带解码摘要
func LogMsgWithDetail(dir string, cmd byte, pid int32, dataSize int, detail string) {
	MsgLog.Push(MsgEntry{
		Ts:     time.Now().UnixMilli(),
		Dir:    dir,
		Cmd:    cmd,
		Name:   CmdName(cmd),
		Pid:    pid,
		Size:   dataSize,
		Detail: detail,
	})
	PlayerRates.Record(pid)
}

// ===================== Handler 包装器 =====================

// txStats 包装 router handler：计入 TX 游戏流量 + 记录消息日志（G7: 关键消息解码）
func txStats(h session.Handler) session.Handler {
	return func(conn *transport.Conn, msg *codec.Message) *codec.Message {
		// RX 侧关键消息解码
		pid := connPid(conn)
		switch msg.Cmd {
		case framesync.CmdReconnect:
			detail := fmt.Sprintf("lastFrame=%d", decodeMsgUint32(msg.Data, 4))
			LogMsgWithDetail("rx", msg.Cmd, pid, len(msg.Data), detail)
		case framesync.CmdJoinRoom:
			detail := fmt.Sprintf("roomId=%d", decodeMsgInt32(msg.Data, 0))
			LogMsgWithDetail("rx", msg.Cmd, pid, len(msg.Data), detail)
		}

		rsp := h(conn, msg)
		if rsp != nil {
			GameStats.RecordTx(int64(len(rsp.Data)))
			// TX 侧关键消息解码
			switch rsp.Cmd {
			case framesync.CmdStartFrameSync:
				if len(rsp.Data) >= 8 {
					rate := decodeMsgInt32(rsp.Data, 0)
					interval := decodeMsgInt32(rsp.Data, 4)
					LogMsgWithDetail("tx", rsp.Cmd, pid, len(rsp.Data), fmt.Sprintf("rate=%d interval=%dms", rate, interval))
				} else {
					LogMsg("tx", rsp.Cmd, pid, len(rsp.Data))
				}
			case framesync.CmdReconnectRsp:
				if len(rsp.Data) >= 5 {
					status := rsp.Data[0]
					sn := []string{"fail", "ok", "buffer_stale"}
					s := "unknown"
					if int(status) < len(sn) { s = sn[status] }
					LogMsgWithDetail("tx", rsp.Cmd, pid, len(rsp.Data), fmt.Sprintf("status=%s", s))
				} else {
					LogMsg("tx", rsp.Cmd, pid, len(rsp.Data))
				}
			default:
				LogMsg("tx", rsp.Cmd, pid, len(rsp.Data))
			}
		}
		return rsp
	}
}

func decodeMsgInt32(data []byte, offset int) int32 {
	if len(data) < offset+4 { return 0 }
	return int32(data[offset]) | int32(data[offset+1])<<8 | int32(data[offset+2])<<16 | int32(data[offset+3])<<24
}

func decodeMsgUint32(data []byte, offset int) uint32 {
	if len(data) < offset+4 { return 0 }
	return uint32(data[offset]) | uint32(data[offset+1])<<8 | uint32(data[offset+2])<<16 | uint32(data[offset+3])<<24
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

// ===================== G8: Per-player 消息速率 =====================

type playerRate struct {
	mu      sync.Mutex
	counts  map[int32]*[ringSize]rateBucket
}

type rateBucket struct {
	sec   int64
	count int32
}

var PlayerRates = &playerRate{counts: make(map[int32]*[ringSize]rateBucket)}

func (pr *playerRate) Record(pid int32) {
	if pid <= 0 {
		return
	}
	now := time.Now().Unix()
	idx := now % ringSize

	pr.mu.Lock()
	ring, ok := pr.counts[pid]
	if !ok {
		ring = &[ringSize]rateBucket{}
		pr.counts[pid] = ring
	}
	b := &ring[idx]
	if b.sec == now {
		b.count++
	} else {
		*b = rateBucket{sec: now, count: 1}
	}
	pr.mu.Unlock()
}

// Rate5Sec 返回该玩家最近 5 秒的消息/秒
func (pr *playerRate) Rate5Sec(pid int32) float64 {
	now := time.Now().Unix()
	cut := now - 5

	pr.mu.Lock()
	ring, ok := pr.counts[pid]
	pr.mu.Unlock()
	if !ok {
		return 0
	}

	var total int32
	pr.mu.Lock()
	for i := 0; i < ringSize; i++ {
		b := ring[i]
		if b.sec > cut && b.sec <= now {
			total += b.count
		}
	}
	pr.mu.Unlock()
	return float64(total) / 5.0
}

// TopPlayers 返回最近 5 秒消息速率最高的 N 个玩家
func (pr *playerRate) TopPlayers(n int) []PlayerRateInfo {
	now := time.Now().Unix()
	cut := now - 5

	pr.mu.Lock()
	result := make([]PlayerRateInfo, 0, len(pr.counts))
	for pid, ring := range pr.counts {
		var total int32
		for i := 0; i < ringSize; i++ {
			b := ring[i]
			if b.sec > cut && b.sec <= now {
				total += b.count
			}
		}
		if total > 0 {
			result = append(result, PlayerRateInfo{Pid: pid, MsgPer5Sec: total})
		}
	}
	pr.mu.Unlock()

	// 简单冒泡排序（N 通常很小）
	for i := 0; i < len(result) && i < n; i++ {
		for j := i + 1; j < len(result); j++ {
			if result[j].MsgPer5Sec > result[i].MsgPer5Sec {
				result[i], result[j] = result[j], result[i]
			}
		}
	}
	if len(result) > n {
		result = result[:n]
	}
	return result
}

type PlayerRateInfo struct {
	Pid        int32   `json:"pid"`
	MsgPer5Sec int32   `json:"msg_5sec"`
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
	27: "SendEntityState",
	28: "PushEntityState",
}

func CmdName(cmd byte) string {
	if name, ok := cmdNames[cmd]; ok {
		return name
	}
	return "Unknown"
}
