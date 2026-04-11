package framesync

import (
	"log/slog"

	"github.com/boomlulu/boomnetwork/codec"
)

// SetInitialSnapshot 设置初始快照（frame 0，仅在帧同步开始前调用）
func (r *Room) SetInitialSnapshot(data []byte) {
	r.mu.Lock()
	defer r.mu.Unlock()
	r.snapshotFrame = 0
	r.snapshotData = make([]byte, len(data))
	copy(r.snapshotData, data)
	r.snapshotStaleFrames = 0
}

// UpdateSnapshot 更新房间快照（只接受比当前更新的帧号）
func (r *Room) UpdateSnapshot(frameNumber uint32, data []byte) bool {
	r.mu.Lock()
	if frameNumber <= r.snapshotFrame {
		r.mu.Unlock()
		return false
	}
	r.snapshotFrame = frameNumber
	r.snapshotData = make([]byte, len(data))
	copy(r.snapshotData, data)
	r.snapshotStaleFrames = 0

	wasPaused := r.snapshotPaused
	if wasPaused {
		r.snapshotPaused = false
		slog.Info("snapshot received, resuming frame sync", "roomId", r.ID)
	}
	d := r.delegate // M3: 锁内捕获 delegate，消除锁外读取 r.delegate 的 TOCTOU
	r.mu.Unlock()

	if wasPaused {
		r.broadcast(codec.NewExtMessage(ExtCmdFrameSyncResumed, nil))
		if d != nil {
			d.OnRoomResumed(r)
		}
	}

	slog.Info("snapshot updated", "roomId", r.ID, "frame", frameNumber, "bytes", len(data))
	return true
}

// GetSnapshot 获取最新快照
func (r *Room) GetSnapshot() (frameNumber uint32, data []byte) {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.snapshotFrame, r.snapshotData
}

// IsSnapshotPaused 是否因快照过期而暂停
func (r *Room) IsSnapshotPaused() bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.snapshotPaused
}

// SnapshotFrame 最新快照对应的帧号
func (r *Room) SnapshotFrame() uint32 {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.snapshotFrame
}

// SnapshotSize 最新快照数据大小（字节）
func (r *Room) SnapshotSize() int {
	r.mu.Lock()
	defer r.mu.Unlock()
	return len(r.snapshotData)
}

// SnapshotStaleFrames 自上次快照以来经过的帧数
func (r *Room) SnapshotStaleFrames() uint32 {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.snapshotStaleFrames
}
