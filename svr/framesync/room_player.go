package framesync

import (
	"context"
	"sync/atomic"
	"time"

	"github.com/boomlulu/boomnetwork/codec"
)

// removePlayerLocked 唯一的玩家移除出口（必须在持锁状态下调用）
func (r *Room) removePlayerLocked(id int32) {
	if p, ok := r.players[id]; ok {
		if p.State == PlayerOnline || p.State == PlayerReplaying {
			atomic.AddInt32(&r.onlineCount, -1)
		}
		if p.cancelFn != nil {
			p.cancelFn()
			p.cancelFn = nil
		}
		p.frameCh = nil
	}
	delete(r.players, id)
	if r.hostPlayerId == id {
		r.electHost()
	}
	if len(r.players) == 0 {
		r.emptyAt = time.Now()
	}
}

// AddPlayer 添加或重连玩家。
// startFrame：玩家已有帧号（delivery loop 从此处之后开始追帧）。
// replaying：true 表示玩家处于补帧阶段（PlayerReplaying），不参与实时广播；补帧完成后调用 SetPlayerLive 升级为 PlayerOnline。
// preamble：在帧数据之前先投递的控制消息（快照/StartFrameSync/S2C reliable 等）。
// NEW-04: 返回 error，在锁内做容量检查，消除外部 check-then-act TOCTOU。
// 重连（已存在玩家 ID）不受容量限制，直接替换连接。
func (r *Room) AddPlayer(id int32, conn PlayerConn, replaying bool, startFrame uint32, preamble ...*codec.Message) error {
	initialState := PlayerOnline
	if replaying {
		initialState = PlayerReplaying
	}
	r.mu.Lock()
	isReconnect := false
	var player *Player
	if existing, ok := r.players[id]; ok {
		// 取消旧的 delivery loop（重连时）
		if existing.cancelFn != nil {
			existing.cancelFn()
			existing.cancelFn = nil
		}
		existing.frameCh = nil
		if existing.State == PlayerDisconnected {
			// Disconnected → 重连，才需要恢复计数；Online 和 Replaying 已计入，不重复加
			atomic.AddInt32(&r.onlineCount, 1)
		}
		existing.Conn = conn
		existing.State = initialState
		existing.cursor = startFrame
		isReconnect = true
		player = existing
	} else {
		// NEW-04: 新玩家容量检查在锁内执行，消除 TOCTOU
		if r.config.MaxPlayers > 0 && len(r.players) >= r.config.MaxPlayers {
			r.mu.Unlock()
			return ErrRoomFull
		}
		atomic.AddInt32(&r.onlineCount, 1)
		player = &Player{ID: id, Conn: conn, State: initialState, JoinedAt: time.Now(), cursor: startFrame}
		r.players[id] = player
	}
	r.hadPlayer.Store(true)
	r.emptyAt = time.Time{} // 有玩家，清零空房间计时
	if r.hostPlayerId == 0 {
		r.hostPlayerId = id
	}

	// 创建有界 channel 和 delivery goroutine
	player.frameCh = make(chan *CachedFrame, frameChBuffer)
	ctx, cancel := context.WithCancel(context.Background())
	player.cancelFn = cancel

	// 锁内捕获 channel 引用：避免 Unlock 后 DisconnectPlayer 并发置 nil 造成竞态
	newFrameCh := player.frameCh
	d := r.delegate
	r.mu.Unlock()

	go r.deliveryLoop(ctx, player, conn, newFrameCh, preamble)

	if isReconnect {
		r.LogEvent("INFO", "player reconnected", map[string]any{"player_id": id})
	} else {
		r.LogEvent("INFO", "player joined", map[string]any{"player_id": id})
	}

	if d != nil {
		if isReconnect {
			d.OnPlayerReconnected(r, player)
		} else {
			d.OnPlayerJoined(r, player)
		}
	}
	return nil
}

// SetPlayerLive 将 PlayerReplaying 状态的玩家升级为 PlayerOnline，开始接收实时帧。
func (r *Room) SetPlayerLive(id int32) {
	r.mu.Lock()
	if p, ok := r.players[id]; ok && p.State == PlayerReplaying {
		p.State = PlayerOnline
	}
	r.mu.Unlock()
}

// DisconnectPlayer 标记断线保留（幂等：多次调用安全，delegate 只在状态真正变化时触发）
func (r *Room) DisconnectPlayer(id int32) {
	r.mu.Lock()
	var player *Player
	if p, ok := r.players[id]; ok {
		if p.State == PlayerOnline || p.State == PlayerReplaying {
			// Online 或 Replaying → 断线，递减计数并触发 delegate
			atomic.AddInt32(&r.onlineCount, -1)
			p.State = PlayerDisconnected
			p.DisconnectTime = time.Now()
			p.Conn = nil
			if p.cancelFn != nil {
				p.cancelFn()
				p.cancelFn = nil
			}
			p.frameCh = nil // 阻止 stepFrame / deliveryLoop 继续投递
			player = p      // 触发 OnPlayerDisconnected delegate
		} else {
			// 已是 Disconnected，仍清理字段（幂等），但不触发 delegate
			p.Conn = nil
			if p.cancelFn != nil {
				p.cancelFn()
				p.cancelFn = nil
			}
			p.frameCh = nil
		}
	}
	if r.hostPlayerId == id && r.running {
		r.electHost()
	}
	d := r.delegate
	r.mu.Unlock()

	if player != nil {
		r.LogEvent("INFO", "player disconnected", map[string]any{"player_id": id})
	}

	if d != nil && player != nil {
		d.OnPlayerDisconnected(r, player)
	}
}

// RemovePlayer 彻底移除玩家（通过 removePlayerLocked 统一路径）
func (r *Room) RemovePlayer(id int32) {
	r.mu.Lock()
	r.removePlayerLocked(id)
	d := r.delegate
	r.mu.Unlock()

	r.LogEvent("INFO", "player removed", map[string]any{"player_id": id})

	if d != nil {
		d.OnPlayerRemoved(r, id)
	}
}

// EnqueueEvent 添加帧内事件（在同步中调用，事件随下一帧广播）
func (r *Room) EnqueueEvent(eventType byte, playerId int32) {
	r.mu.Lock()
	r.pendingEvents = append(r.pendingEvents, FrameEvent{EventType: eventType, PlayerId: playerId})
	r.mu.Unlock()
}

// HostPlayerId 获取当前房主
func (r *Room) HostPlayerId() int32 {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.hostPlayerId
}

// setHost 设置房主并入队 HostChanged 事件（需在锁内调用）
func (r *Room) setHost(id int32) {
	r.hostPlayerId = id
	r.pendingEvents = append(r.pendingEvents, FrameEvent{EventType: FrameEventHostChanged, PlayerId: id})
}

// electHost 选举新房主：选第一个在线玩家（需在锁内调用）
func (r *Room) electHost() {
	for _, p := range r.players {
		if p.State == PlayerOnline {
			r.setHost(p.ID)
			return
		}
	}
	r.hostPlayerId = 0 // 无在线玩家
}

// PlayerCount 在线玩家数（原子读，无锁）
func (r *Room) PlayerCount() int {
	return int(atomic.LoadInt32(&r.onlineCount))
}

// TotalPlayerCount 总玩家数（含断线）
func (r *Room) TotalPlayerCount() int {
	r.mu.Lock()
	defer r.mu.Unlock()
	return len(r.players)
}

// GetPlayerIds 获取所有玩家 ID（含断线保留的）
func (r *Room) GetPlayerIds() []int32 {
	r.mu.Lock()
	defer r.mu.Unlock()
	ids := make([]int32, 0, len(r.players))
	for id := range r.players {
		ids = append(ids, id)
	}
	return ids
}

// AppendPlayerIds 将所有玩家 ID（含断线保留的）追加到 dst 并返回。
// 调用方可传入预分配切片避免分配：ids = room.AppendPlayerIds(ids[:0])
func (r *Room) AppendPlayerIds(dst []int32) []int32 {
	r.mu.Lock()
	defer r.mu.Unlock()
	for id := range r.players {
		dst = append(dst, id)
	}
	return dst
}

// ForEachOnlinePlayer 遍历在线玩家（用于广播推送）
// 使用 playerSlicePool 复用临时切片，消除每次调用的匿名 struct slice 分配。
func (r *Room) ForEachOnlinePlayer(fn func(id int32, conn PlayerConn)) {
	sp := playerSlicePool.Get().(*[]*Player)
	players := (*sp)[:0]

	r.mu.Lock()
	for _, p := range r.players {
		if p.State == PlayerOnline && p.Conn != nil {
			players = append(players, p)
		}
	}
	r.mu.Unlock()

	for _, p := range players {
		fn(p.ID, p.Conn)
	}

	// 归还前清零指针，防止 Pool 持有 Player 对象阻碍 GC
	for i := range players {
		players[i] = nil
	}
	// M4: 超大切片不归还 Pool，避免 Pool 长期持有大内存（cap>64 直接丢弃）
	if cap(players) <= 64 {
		*sp = players[:0]
		playerSlicePool.Put(sp)
	}
}

// PlayerInfo GM 用的玩家信息快照
type PlayerInfo struct {
	ID             int32       `json:"id"`
	State          PlayerState `json:"state"` // 0=online, 1=disconnected
	DisconnectTime int64       `json:"disconnect_time,omitempty"` // unix ms, 0=online
	JoinedAt       int64       `json:"joined_at,omitempty"`       // unix ms，加入时间
}

// ForEachPlayer 遍历所有玩家（含离线），用于 GM 查询
func (r *Room) ForEachPlayer(fn func(info PlayerInfo)) {
	r.mu.Lock()
	infos := make([]PlayerInfo, 0, len(r.players))
	for _, p := range r.players {
		info := PlayerInfo{ID: p.ID, State: p.State, JoinedAt: p.JoinedAt.UnixMilli()}
		if p.State == PlayerDisconnected {
			info.DisconnectTime = p.DisconnectTime.UnixMilli()
		}
		infos = append(infos, info)
	}
	r.mu.Unlock()
	for _, info := range infos {
		fn(info)
	}
}

// ReconcilePlayers 收敛玩家期望状态：通过 removePlayerLocked 移除所有超过 keepalive 的断线玩家
// 同时入队帧事件通知同房玩家，返回被移除的玩家 ID（供 Reconciler 回调 delegate）
func (r *Room) ReconcilePlayers() []int32 {
	r.mu.Lock()
	var evicted []int32
	now := time.Now()
	for id, p := range r.players {
		if p.State == PlayerDisconnected && now.Sub(p.DisconnectTime) > r.config.DisconnectKeepAlive {
			r.removePlayerLocked(id)
			evicted = append(evicted, id)
		}
	}
	r.mu.Unlock()

	for _, id := range evicted {
		if r.IsRunning() {
			r.EnqueueEvent(FrameEventPlayerLeft, id)
		} else {
			r.broadcast(codec.NewExtMessage(ExtCmdPlayerLeft, EncodePlayerId(id)))
		}
	}
	return evicted
}
