package framesync

import (
	"context"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/boomlulu/boomnetwork/codec"
)

// ---- nopConn ----

type nopConn struct{}

func (nopConn) Send(*codec.Message) error { return nil }

// ---- mockDelegate ----

type mockDelegate struct {
	NoopRoomDelegate
	mu          sync.Mutex
	joined      []int32
	reconnected []int32
	disconnected []int32
	removed     []int32
	started     int
	stopped     int
	paused      int
	resumed     int
	panicked    int
}

func (m *mockDelegate) OnRoomStarted(*Room)                      { m.mu.Lock(); m.started++; m.mu.Unlock() }
func (m *mockDelegate) OnRoomStopped(*Room)                      { m.mu.Lock(); m.stopped++; m.mu.Unlock() }
func (m *mockDelegate) OnRoomPaused(*Room, FrameSyncPauseReason) { m.mu.Lock(); m.paused++; m.mu.Unlock() }
func (m *mockDelegate) OnRoomResumed(*Room)                      { m.mu.Lock(); m.resumed++; m.mu.Unlock() }
func (m *mockDelegate) OnRoomPanicked(*Room, []int32)            { m.mu.Lock(); m.panicked++; m.mu.Unlock() }
func (m *mockDelegate) OnPlayerJoined(_ *Room, p *Player) {
	m.mu.Lock(); m.joined = append(m.joined, p.ID); m.mu.Unlock()
}
func (m *mockDelegate) OnPlayerReconnected(_ *Room, p *Player) {
	m.mu.Lock(); m.reconnected = append(m.reconnected, p.ID); m.mu.Unlock()
}
func (m *mockDelegate) OnPlayerDisconnected(_ *Room, p *Player) {
	m.mu.Lock(); m.disconnected = append(m.disconnected, p.ID); m.mu.Unlock()
}
func (m *mockDelegate) OnPlayerRemoved(_ *Room, id int32) {
	m.mu.Lock(); m.removed = append(m.removed, id); m.mu.Unlock()
}

func (m *mockDelegate) joinedCount() int    { m.mu.Lock(); defer m.mu.Unlock(); return len(m.joined) }
func (m *mockDelegate) removedCount() int   { m.mu.Lock(); defer m.mu.Unlock(); return len(m.removed) }
func (m *mockDelegate) stoppedCount() int   { m.mu.Lock(); defer m.mu.Unlock(); return m.stopped }
func (m *mockDelegate) startedCount() int   { m.mu.Lock(); defer m.mu.Unlock(); return m.started }
func (m *mockDelegate) pausedCount() int    { m.mu.Lock(); defer m.mu.Unlock(); return m.paused }
func (m *mockDelegate) resumedCount() int   { m.mu.Lock(); defer m.mu.Unlock(); return m.resumed }
func (m *mockDelegate) reconnectedCount() int { m.mu.Lock(); defer m.mu.Unlock(); return len(m.reconnected) }

func newTestRoom() *Room {
	return NewRoomWithConfig(RoomConfig{
		FrameRate:           20,
		FrameBufferSize:     100,
		DisconnectKeepAlive: 100 * time.Millisecond,
	})
}

// ===================== RoomDelegate 回调 =====================

func TestDelegate_PlayerJoinedAndReconnected(t *testing.T) {
	d := &mockDelegate{}
	r := newTestRoom()
	r.SetDelegate(d)

	// 首次加入 → OnPlayerJoined
	r.AddPlayer(1, nopConn{})
	if d.joinedCount() != 1 {
		t.Errorf("expected OnPlayerJoined called once, got %d", d.joinedCount())
	}
	if d.reconnectedCount() != 0 {
		t.Errorf("expected no reconnect yet, got %d", d.reconnectedCount())
	}

	// 相同 ID 再次加入 → OnPlayerReconnected
	r.AddPlayer(1, nopConn{})
	if d.reconnectedCount() != 1 {
		t.Errorf("expected OnPlayerReconnected called once, got %d", d.reconnectedCount())
	}
	if d.joinedCount() != 1 {
		t.Errorf("join count should stay 1, got %d", d.joinedCount())
	}
}

func TestDelegate_PlayerDisconnected(t *testing.T) {
	d := &mockDelegate{}
	r := newTestRoom()
	r.SetDelegate(d)
	r.AddPlayer(1, nopConn{})

	r.DisconnectPlayer(1)

	d.mu.Lock()
	n := len(d.disconnected)
	d.mu.Unlock()
	if n != 1 {
		t.Errorf("expected OnPlayerDisconnected called once, got %d", n)
	}
}

func TestDelegate_PlayerRemoved(t *testing.T) {
	d := &mockDelegate{}
	r := newTestRoom()
	r.SetDelegate(d)
	r.AddPlayer(1, nopConn{})
	r.AddPlayer(2, nopConn{})

	r.RemovePlayer(1)

	if d.removedCount() != 1 {
		t.Errorf("expected OnPlayerRemoved once, got %d", d.removedCount())
	}
	// emptyAt 应为零（还有玩家 2）
	if !r.EmptyAt().IsZero() {
		t.Error("emptyAt should be zero while player 2 remains")
	}

	r.RemovePlayer(2)
	if d.removedCount() != 2 {
		t.Errorf("expected OnPlayerRemoved twice, got %d", d.removedCount())
	}
	// 最后一人离开，emptyAt 应已设置
	if r.EmptyAt().IsZero() {
		t.Error("emptyAt should be set after last player removed")
	}
}

func TestDelegate_EmptyAt_ClearedOnAddPlayer(t *testing.T) {
	r := newTestRoom()
	r.AddPlayer(1, nopConn{})
	r.RemovePlayer(1)

	if r.EmptyAt().IsZero() {
		t.Fatal("emptyAt should be set after removing last player")
	}

	// 新玩家加入，emptyAt 应清零
	r.AddPlayer(2, nopConn{})
	if !r.EmptyAt().IsZero() {
		t.Error("emptyAt should be cleared after new player joins")
	}
}

func TestDelegate_RoomStartStop(t *testing.T) {
	d := &mockDelegate{}
	r := newTestRoom()
	r.SetDelegate(d)

	r.Start()
	defer r.Stop()

	if d.startedCount() != 1 {
		t.Errorf("OnRoomStarted expected 1, got %d", d.startedCount())
	}

	r.Stop()
	if d.stoppedCount() != 1 {
		t.Errorf("OnRoomStopped expected 1, got %d", d.stoppedCount())
	}

	// 幂等：重复 Stop 不应再触发
	r.Stop()
	if d.stoppedCount() != 1 {
		t.Errorf("OnRoomStopped should be idempotent, got %d", d.stoppedCount())
	}
}

func TestDelegate_PauseAndResume(t *testing.T) {
	d := &mockDelegate{}
	r := NewRoomWithConfig(RoomConfig{
		FrameRate:              20,
		FrameBufferSize:        100,
		SnapshotIntervalFrames: 10,
	})
	r.SetDelegate(d)

	// 模拟 snapshotPaused 触发（UpdateSnapshot 里 wasPaused=true 时 → OnRoomResumed）
	r.mu.Lock()
	r.snapshotPaused = true
	r.snapshotFrame = 0
	r.mu.Unlock()

	r.UpdateSnapshot(50, []byte{1})

	if d.resumedCount() != 1 {
		t.Errorf("OnRoomResumed expected 1, got %d", d.resumedCount())
	}
}

// ===================== removePlayerLocked（间接测试） =====================

func TestRemovePlayerLocked_ElectsHostOnRemove(t *testing.T) {
	r := newTestRoom()
	r.AddPlayer(1, nopConn{}) // player 1 becomes host
	r.AddPlayer(2, nopConn{})

	if r.HostPlayerId() != 1 {
		t.Fatalf("player 1 should be host, got %d", r.HostPlayerId())
	}

	r.mu.Lock()
	r.running = true // 只有在 running 时才选举
	r.mu.Unlock()

	r.RemovePlayer(1) // should elect player 2

	if r.HostPlayerId() != 2 {
		t.Errorf("player 2 should be elected host, got %d", r.HostPlayerId())
	}
}

// ===================== ReconcilePlayers =====================

func TestReconcilePlayers_EvictsTimedOutPlayers(t *testing.T) {
	d := &mockDelegate{}
	r := newTestRoom() // DisconnectKeepAlive = 100ms
	r.SetDelegate(d)

	r.AddPlayer(1, nopConn{})
	r.AddPlayer(2, nopConn{})
	r.DisconnectPlayer(1)
	r.DisconnectPlayer(2)

	// 尚未超时，不应驱逐
	evicted := r.ReconcilePlayers()
	if len(evicted) != 0 {
		t.Errorf("expected 0 evicted before timeout, got %d", len(evicted))
	}

	// 等待 keepalive 到期
	time.Sleep(150 * time.Millisecond)

	evicted = r.ReconcilePlayers()
	if len(evicted) != 2 {
		t.Errorf("expected 2 evicted after timeout, got %d", len(evicted))
	}

	if r.TotalPlayerCount() != 0 {
		t.Errorf("room should be empty after eviction, got %d", r.TotalPlayerCount())
	}

	// emptyAt 应已设置
	if r.EmptyAt().IsZero() {
		t.Error("emptyAt should be set after all players evicted")
	}
}

func TestReconcilePlayers_KeepsActiveAndOnlinePlayers(t *testing.T) {
	r := newTestRoom()
	r.AddPlayer(1, nopConn{}) // online，不应驱逐
	r.AddPlayer(2, nopConn{})
	r.DisconnectPlayer(2)

	// 等待 keepalive 到期
	time.Sleep(150 * time.Millisecond)

	evicted := r.ReconcilePlayers()

	// 只驱逐 player 2（超时断线），player 1（在线）应保留
	if len(evicted) != 1 || evicted[0] != 2 {
		t.Errorf("expected evict [2], got %v", evicted)
	}
	if r.TotalPlayerCount() != 1 {
		t.Errorf("player 1 should remain, got count %d", r.TotalPlayerCount())
	}
}

// ===================== ShouldDestroy =====================

func TestShouldDestroy_FalseBeforeGrace(t *testing.T) {
	r := newTestRoom()
	r.AddPlayer(1, nopConn{})
	r.RemovePlayer(1)

	if r.ShouldDestroy(500 * time.Millisecond) {
		t.Error("ShouldDestroy should be false before grace expires")
	}
}

func TestShouldDestroy_TrueAfterGrace(t *testing.T) {
	r := newTestRoom()
	r.AddPlayer(1, nopConn{})
	r.RemovePlayer(1)

	time.Sleep(60 * time.Millisecond)

	if !r.ShouldDestroy(50 * time.Millisecond) {
		t.Error("ShouldDestroy should be true after grace expires")
	}
}

func TestShouldDestroy_FalseWhenHasPlayers(t *testing.T) {
	r := newTestRoom()
	r.AddPlayer(1, nopConn{})

	if r.ShouldDestroy(0) {
		t.Error("ShouldDestroy should be false while room has players")
	}
}

func TestShouldDestroy_FalseWhenNeverHadPlayers(t *testing.T) {
	r := newTestRoom()
	// emptyAt 从未被设置（从未有玩家加入）
	if r.ShouldDestroy(0) {
		t.Error("ShouldDestroy should be false for room that never had players (emptyAt is zero)")
	}
}

// ===================== RoomReconciler 集成 =====================

func TestRoomReconciler_EvictsPlayersAndDestroysRoom(t *testing.T) {
	var removedPlayers []int32
	var destroyedRooms []int32
	var mu sync.Mutex

	delegate := &struct {
		NoopRoomDelegate
	}{}
	_ = delegate

	// 用可记录 OnPlayerRemoved 的 delegate
	type trackDelegate struct {
		NoopRoomDelegate
	}

	var callDelegate struct {
		NoopRoomDelegate
		onRemoved   func(*Room, int32)
	}
	callDelegate.onRemoved = func(room *Room, id int32) {
		mu.Lock()
		removedPlayers = append(removedPlayers, id)
		mu.Unlock()
	}

	type fullDelegate struct {
		NoopRoomDelegate
		onRemoved func(*Room, int32)
	}
	fd := &fullDelegate{
		onRemoved: func(room *Room, id int32) {
			mu.Lock()
			removedPlayers = append(removedPlayers, id)
			mu.Unlock()
		},
	}
	fd.NoopRoomDelegate = NoopRoomDelegate{}

	// 使用 mockDelegate
	d := &mockDelegate{}

	mgr := NewRoomManager(RoomConfig{
		FrameRate:           20,
		FrameBufferSize:     100,
		DisconnectKeepAlive: 50 * time.Millisecond,
	})

	room := mgr.CreateRoom()
	room.SetDelegate(d)
	room.AddPlayer(1, nopConn{})
	room.AddPlayer(2, nopConn{})
	room.DisconnectPlayer(1)
	room.DisconnectPlayer(2)

	rec := NewRoomReconciler(mgr, d, 100*time.Millisecond, 10*time.Millisecond)

	// 第一轮：玩家未超时，不应驱逐
	rec.Reconcile()
	if d.removedCount() != 0 {
		t.Errorf("no players should be removed before keepalive expires")
	}

	// 等待 keepalive 到期
	time.Sleep(80 * time.Millisecond)
	rec.Reconcile()

	if d.removedCount() != 2 {
		t.Errorf("expected 2 players removed, got %d", d.removedCount())
	}
	if mgr.GetRoom(room.ID) == nil {
		t.Error("room should still exist: grace not expired yet")
	}

	// 等待 emptyGrace 到期
	time.Sleep(150 * time.Millisecond)
	rec.Reconcile()

	if mgr.GetRoom(room.ID) != nil {
		t.Error("room should be destroyed after emptyGrace expired")
	}
	_ = destroyedRooms
	_ = removedPlayers
}

func TestRoomReconciler_Run_CancelStops(t *testing.T) {
	mgr := NewRoomManager()
	d := &mockDelegate{}
	rec := NewRoomReconciler(mgr, d, 30*time.Second, 10*time.Millisecond)

	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan struct{})
	go func() {
		rec.Run(ctx)
		close(done)
	}()

	cancel()
	select {
	case <-done:
	case <-time.After(500 * time.Millisecond):
		t.Error("Run should exit within 500ms after ctx cancel")
	}
}

func TestRoomReconciler_NewPlayerReconnect_CancelsDestroy(t *testing.T) {
	d := &mockDelegate{}
	mgr := NewRoomManager(RoomConfig{
		FrameRate:           20,
		FrameBufferSize:     100,
		DisconnectKeepAlive: 50 * time.Millisecond,
	})

	room := mgr.CreateRoom()
	room.SetDelegate(d)
	room.AddPlayer(1, nopConn{})
	room.DisconnectPlayer(1)

	rec := NewRoomReconciler(mgr, d, 100*time.Millisecond, 10*time.Millisecond)

	time.Sleep(80 * time.Millisecond)
	rec.Reconcile() // player 1 evicted, emptyAt set

	// 新玩家加入 → emptyAt 清零
	room.AddPlayer(2, nopConn{})

	time.Sleep(150 * time.Millisecond)
	rec.Reconcile() // grace 看起来超了，但 emptyAt 已清零

	if mgr.GetRoom(room.ID) == nil {
		t.Error("room should NOT be destroyed: new player joined after emptyAt was set")
	}
}

// ===================== 性能测试 =====================

// BenchmarkReconcilePlayers_ManyStale 模拟 100 个玩家全部超时的房间
func BenchmarkReconcilePlayers_ManyStale(b *testing.B) {
	r := NewRoomWithConfig(RoomConfig{
		FrameRate:           20,
		FrameBufferSize:     100,
		DisconnectKeepAlive: time.Nanosecond, // 立即到期
	})
	for i := int32(1); i <= 100; i++ {
		r.AddPlayer(i, nopConn{})
		r.DisconnectPlayer(i)
	}
	time.Sleep(time.Millisecond)

	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		// 重新注入玩家（已被驱逐，需重置）
		for j := int32(1); j <= 100; j++ {
			r.mu.Lock()
			r.players[j] = &Player{
				ID:             j,
				State:          PlayerDisconnected,
				DisconnectTime: time.Now().Add(-time.Second),
			}
			r.emptyAt = time.Time{}
			r.mu.Unlock()
		}
		r.ReconcilePlayers()
	}
}

// BenchmarkReconciler_1000Rooms 模拟 1000 个房间的全局协调
func BenchmarkReconciler_1000Rooms(b *testing.B) {
	const numRooms = 1000
	const playersPerRoom = 4

	mgr := NewRoomManager(RoomConfig{
		FrameRate:           20,
		FrameBufferSize:     100,
		DisconnectKeepAlive: time.Nanosecond,
	})
	d := &NoopRoomDelegate{}
	_ = d

	// 创建房间 + 注入断线玩家
	for i := 0; i < numRooms; i++ {
		room := mgr.CreateRoom()
		for j := int32(0); j < playersPerRoom; j++ {
			room.AddPlayer(j+1, nopConn{})
			room.DisconnectPlayer(j + 1)
		}
	}
	time.Sleep(time.Millisecond)

	// 用 noop delegate 的 reconciler
	rec := NewRoomReconciler(mgr, &mockDelegate{}, 10*time.Millisecond, time.Hour)

	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		rec.Reconcile()
	}
}

// ===================== 压力测试 =====================

// TestStress_ConcurrentReconcileAndJoin 并发：Reconciler 运行期间玩家持续加入/离开
func TestStress_ConcurrentReconcileAndJoin(t *testing.T) {
	if testing.Short() {
		t.Skip("skipping stress test in short mode")
	}

	const (
		numRooms   = 50
		numWorkers = 10
		duration   = 2 * time.Second
	)

	mgr := NewRoomManager(RoomConfig{
		FrameRate:           20,
		FrameBufferSize:     100,
		DisconnectKeepAlive: 20 * time.Millisecond,
	})
	d := &mockDelegate{}
	rec := NewRoomReconciler(mgr, d, 50*time.Millisecond, 5*time.Millisecond)

	ctx, cancel := context.WithTimeout(context.Background(), duration)
	defer cancel()

	// 预先创建房间
	rooms := make([]*Room, numRooms)
	for i := range rooms {
		rooms[i] = mgr.CreateRoom()
		rooms[i].SetDelegate(d)
	}

	var ops atomic.Int64

	// Reconciler goroutine
	go rec.Run(ctx)

	// Worker goroutines：随机加入/断线
	var wg sync.WaitGroup
	for w := 0; w < numWorkers; w++ {
		wg.Add(1)
		go func(workerID int) {
			defer wg.Done()
			pid := int32(workerID + 1)
			for {
				select {
				case <-ctx.Done():
					return
				default:
					room := rooms[workerID%numRooms]
					room.AddPlayer(pid, nopConn{})
					time.Sleep(5 * time.Millisecond)
					room.DisconnectPlayer(pid)
					time.Sleep(30 * time.Millisecond)
					ops.Add(1)
				}
			}
		}(w)
	}

	wg.Wait()

	if ops.Load() == 0 {
		t.Error("no operations completed during stress test")
	}
	t.Logf("stress test: %d join/disconnect cycles in %v", ops.Load(), duration)
}
