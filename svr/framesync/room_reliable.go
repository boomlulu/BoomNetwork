package framesync

import (
	"github.com/boomlulu/boomnetwork/codec"
)

// === S→C 可靠通道 ===

// SendReliableToPlayer 向指定玩家发送可靠消息（在线时立即发送，离线时只入队等待重连补发）
// inner 消息将被包装为 ExtCmdReliableMsg，赋予单调递增 seq 并存入环形缓冲区。
func (r *Room) SendReliableToPlayer(playerId int32, inner *codec.Message) {
	r.mu.Lock()
	p, ok := r.players[playerId]
	if !ok {
		r.mu.Unlock()
		return
	}
	p.s2cSeq++
	seq := p.s2cSeq
	data := EncodeReliableMsgData(seq, inner)
	reliableMsg := codec.NewExtMessage(ExtCmdReliableMsg, data)
	slot := seq % s2cBufSize
	p.s2cBuf[slot] = cachedS2CMsg{seq: seq, msg: reliableMsg}
	if seq >= s2cBufSize {
		p.s2cBufHead = seq - s2cBufSize + 1
	}
	conn := p.Conn
	state := p.State
	r.mu.Unlock()

	if conn != nil && state == PlayerOnline {
		_ = conn.Send(reliableMsg)
	}
}

// BroadcastReliable 向房间内所有在线玩家（可选排除一个）广播可靠消息。
// PERF-03: 内联 SendReliableToPlayer 逻辑，一次锁内完成 seq 递增、s2cBuf 记录
// 和 (conn, msg) 收集，锁外批量 Send，将 N+1 次 mutex 降为 1 次。
func (r *Room) BroadcastReliable(excludePlayerId int32, inner *codec.Message) {
	type pendingSend struct {
		conn PlayerConn
		msg  *codec.Message
	}
	var sends []pendingSend

	r.mu.Lock()
	for _, p := range r.players {
		if p.ID == excludePlayerId || p.State != PlayerOnline {
			continue
		}
		p.s2cSeq++
		seq := p.s2cSeq
		data := EncodeReliableMsgData(seq, inner)
		reliableMsg := codec.NewExtMessage(ExtCmdReliableMsg, data)
		slot := seq % s2cBufSize
		p.s2cBuf[slot] = cachedS2CMsg{seq: seq, msg: reliableMsg}
		if seq >= s2cBufSize {
			p.s2cBufHead = seq - s2cBufSize + 1
		}
		if p.Conn != nil {
			sends = append(sends, pendingSend{conn: p.Conn, msg: reliableMsg})
		}
	}
	r.mu.Unlock()

	for _, s := range sends {
		_ = s.conn.Send(s.msg)
	}
}

// GetS2CReliableSince 获取 seq > afterSeq 的所有缓冲消息（用于重连补发）
// 返回 stale=true 表示 afterSeq 已超出缓冲区，必须降级到 Snapshot 重连。
func (r *Room) GetS2CReliableSince(playerId int32, afterSeq uint32) (msgs []*codec.Message, stale bool) {
	r.mu.Lock()
	defer r.mu.Unlock()
	p, ok := r.players[playerId]
	if !ok {
		return nil, false
	}
	if p.s2cSeq == 0 || afterSeq >= p.s2cSeq {
		return nil, false // 没有需要补发的消息
	}
	if afterSeq < p.s2cBufHead && p.s2cSeq > 0 {
		return nil, true // stale：请求的起点已被覆盖
	}
	count := p.s2cSeq - afterSeq
	result := make([]*codec.Message, 0, count)
	for seq := afterSeq + 1; seq <= p.s2cSeq; seq++ {
		slot := seq % s2cBufSize
		if p.s2cBuf[slot].seq == seq {
			result = append(result, p.s2cBuf[slot].msg)
		}
	}
	return result, false
}

// GetLastProcessedC2SSeq 获取已处理的最新 C→S reliable seq（用于重连时告知客户端）
func (r *Room) GetLastProcessedC2SSeq(playerId int32) uint32 {
	r.mu.Lock()
	defer r.mu.Unlock()
	p, ok := r.players[playerId]
	if !ok {
		return 0
	}
	return p.lastProcessedC2SSeq
}

// SetLastProcessedC2SSeq 更新已处理的 C→S reliable seq（去重用）
func (r *Room) SetLastProcessedC2SSeq(playerId int32, seq uint32) {
	r.mu.Lock()
	defer r.mu.Unlock()
	if p, ok := r.players[playerId]; ok {
		p.lastProcessedC2SSeq = seq
	}
}
