package framesync

import (
	"context"
	"log/slog"
	"time"
)

// RoomReconciler 基于期望状态 vs 实际状态的房间生命周期协调器
//
// K8s Reconcile 思路：Controllers act on desired state vs actual state, not on events.
//   - 期望：断线超过 keepalive 的玩家不应存在 → ReconcilePlayers 收敛
//   - 期望：空置超过 emptyGrace 的房间不应存在 → ShouldDestroy 收敛
//
// 替代散落的 edge-triggered 逻辑：30s 销毁 goroutine、tickLoop 内 cleanupTicker。
// 即使中间错过事件，下一轮 Reconcile 也会自动修复。
type RoomReconciler struct {
	manager    *RoomManager
	delegate   RoomDelegate
	emptyGrace time.Duration // 房间空置多久后销毁（默认 30s）
	interval   time.Duration // 协调间隔（默认 5s）
}

// NewRoomReconciler 创建协调器
func NewRoomReconciler(manager *RoomManager, delegate RoomDelegate, emptyGrace, interval time.Duration) *RoomReconciler {
	return &RoomReconciler{
		manager:    manager,
		delegate:   delegate,
		emptyGrace: emptyGrace,
		interval:   interval,
	}
}

// Run 启动协调循环，ctx 取消时退出
func (rec *RoomReconciler) Run(ctx context.Context) {
	ticker := time.NewTicker(rec.interval)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			rec.Reconcile()
		}
	}
}

// Reconcile 执行一次完整的期望状态收敛：
//  1. 各房间玩家收敛：移除超时断线玩家，通知 delegate 清理外层映射
//  2. 房间收敛：销毁空置超过 emptyGrace 的房间
func (rec *RoomReconciler) Reconcile() {
	for _, room := range rec.manager.Snapshot() {
		// Step 1: 玩家期望状态收敛
		evicted := room.ReconcilePlayers()
		for _, id := range evicted {
			rec.delegate.OnPlayerRemoved(room, id)
			slog.Info("reconciler: player evicted", "roomId", room.ID, "playerId", id)
		}

		// Step 2: 房间期望状态收敛
		if room.ShouldDestroy(rec.emptyGrace) {
			rec.manager.RemoveRoom(room.ID)
			slog.Info("reconciler: room destroyed (empty grace expired)", "roomId", room.ID, "emptyGrace", rec.emptyGrace)
		}
	}
}
