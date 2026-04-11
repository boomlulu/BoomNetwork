package framesync

import (
	"sync/atomic"
	"time"
)

// IsAlive 返回房间是否仍在 RoomManager 中（原子读，无锁）
// 供 handleFrameInput 热路径替代 roomMgr.GetRoom()，消除每帧 Mutex RLock。
func (r *Room) IsAlive() bool {
	return r.alive.Load()
}

// HadPlayer 是否有过玩家加入（原子读，无锁）
func (r *Room) HadPlayer() bool {
	return r.hadPlayer.Load()
}

// CreatedAt 返回房间创建时间
func (r *Room) CreatedAt() time.Time { return r.createdAt }

// ShouldDestroy 期望状态检查：房间是否空置超过 grace 时长，应当被销毁
func (r *Room) ShouldDestroy(grace time.Duration) bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	return !r.emptyAt.IsZero() && time.Since(r.emptyAt) > grace
}

// EmptyAt 返回房间最近一次变空的时刻（零值表示当前有玩家）
func (r *Room) EmptyAt() time.Time {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.emptyAt
}

// StartTime 开始时间戳（毫秒）
func (r *Room) StartTime() int64 {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.startTime
}

// IsGamePaused 是否处于游戏级暂停
func (r *Room) IsGamePaused() bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.gamePaused
}

// GamePause 设置游戏级暂停。返回 true 表示状态变更（从运行→暂停）。
// NEW-01: 暂停时主动清空 frameHashes，避免暂停期间帧号不递增导致哈希永驻内存。
// 锁顺序规则：先释放 r.mu，再持 r.desyncMu 清空 frameHashes（防死锁）。
func (r *Room) GamePause() bool {
	r.mu.Lock()
	if r.gamePaused {
		r.mu.Unlock()
		return false
	}
	r.gamePaused = true
	r.mu.Unlock()

	// 在 r.mu 释放后单独持 desyncMu 清空，语义不变（暂停后清空）
	r.desyncMu.Lock()
	r.frameHashes = make(map[uint32]map[int32]uint32)
	r.desyncMu.Unlock()
	return true
}

// GameResume 解除游戏级暂停。返回 true 表示状态变更（从暂停→运行）。
func (r *Room) GameResume() bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	if !r.gamePaused {
		return false
	}
	r.gamePaused = false
	return true
}

// GetRoomInfo 获取房间信息快照
// P2-4: 单次加锁同时读取 running + onlineCount，避免 IsRunning() 独立加锁。
func (r *Room) GetRoomInfo() RoomInfo {
	r.mu.Lock()
	running := r.running
	playerCount := int(atomic.LoadInt32(&r.onlineCount))
	r.mu.Unlock()
	return RoomInfo{
		RoomId:      r.ID,
		PlayerCount: playerCount,
		MaxPlayers:  r.config.MaxPlayers,
		Running:     running,
		MatchKey:    r.MatchKey,
	}
}

// GetConfig 获取房间配置（只读）
func (r *Room) GetConfig() RoomConfig {
	return r.config
}

// MaxPlayers 房间最大人数
func (r *Room) MaxPlayers() int {
	return r.config.MaxPlayers
}

// FrameRate 帧率
func (r *Room) FrameRate() int32 {
	return r.frameRate
}

// StartedAt 帧同步启动时间；未启动时返回零值（GM 检视用）
func (r *Room) StartedAt() time.Time {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.startedAt
}

// SetDelegate 注入生命周期委托（幂等，可在任意时刻设置）
func (r *Room) SetDelegate(d RoomDelegate) {
	r.mu.Lock()
	r.delegate = d
	r.mu.Unlock()
}
