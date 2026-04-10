package framesync

// room_p3_test.go — 盲区补测
//
// 覆盖范围:
//   Inspector 方法: NewRoom / FrameRate / StartTime / CreatedAt / HadPlayer /
//                   SnapshotStaleFrames / GetDataStoreEntries / GetConfig /
//                   PendingInputsLen / StartedAt / IsDesyncDetected
//   Reliable 通道:  SendReliableToPlayer / BroadcastReliable /
//                   GetS2CReliableSince / GetLastProcessedC2SSeq / SetLastProcessedC2SSeq
//   玩家遍历:       ForEachPlayer (含离线)
//   初始快照:       SetInitialSnapshot
//   tickLoop:       panic 恢复路径 / 帧号溢出保护

import (
	"sync"
	"testing"
	"time"

	"github.com/boomlulu/boomnetwork/codec"
)

// ─── Inspector 方法 ────────────────────────────────────────────────────────────

func TestNewRoom_DefaultConfig(t *testing.T) {
	room := NewRoom(30)
	if room.FrameRate() != 30 {
		t.Fatalf("want FrameRate=30, got %d", room.FrameRate())
	}
	if room.MaxPlayers() != 4 { // Validate() 填充默认值
		t.Fatalf("want MaxPlayers=4, got %d", room.MaxPlayers())
	}
}

func TestFrameRate_ReturnsConfigValue(t *testing.T) {
	room := NewRoomWithConfig(RoomConfig{FrameRate: 60, FrameBufferSize: 120})
	if room.FrameRate() != 60 {
		t.Fatalf("want 60, got %d", room.FrameRate())
	}
}

func TestStartTime_ZeroBeforeStart(t *testing.T) {
	room := newP0Room()
	if room.StartTime() != 0 {
		t.Fatalf("StartTime should be 0 before Start, got %d", room.StartTime())
	}
}

func TestStartTime_NonZeroAfterStart(t *testing.T) {
	room := NewRoomWithConfig(RoomConfig{
		FrameRate:           20,
		FrameBufferSize:     40,
		SnapshotIntervalFrames: 100,
	})
	room.Start()
	defer room.Stop()
	time.Sleep(20 * time.Millisecond) // 等一个 ticker 周期
	if room.StartTime() == 0 {
		t.Fatal("StartTime should be non-zero after Start")
	}
}

func TestCreatedAt_Recent(t *testing.T) {
	before := time.Now()
	room := newP0Room()
	after := time.Now()
	ct := room.CreatedAt()
	if ct.Before(before) || ct.After(after) {
		t.Fatalf("CreatedAt=%v out of range [%v, %v]", ct, before, after)
	}
}

func TestHadPlayer_FalseInitially(t *testing.T) {
	room := newP0Room()
	if room.HadPlayer() {
		t.Fatal("HadPlayer should be false on new room")
	}
}

func TestHadPlayer_TrueAfterAddPlayer(t *testing.T) {
	room := newP0Room()
	room.AddPlayer(1, nopConn{}, 0)
	if !room.HadPlayer() {
		t.Fatal("HadPlayer should be true after AddPlayer")
	}
}

func TestSnapshotStaleFrames_InitiallyZero(t *testing.T) {
	room := newP0Room()
	if room.SnapshotStaleFrames() != 0 {
		t.Fatalf("want 0, got %d", room.SnapshotStaleFrames())
	}
}

func TestSnapshotStaleFrames_ResetsAfterUpdateSnapshot(t *testing.T) {
	room := newP0Room()
	// 直接写入 stale 计数来模拟多帧未收到快照
	room.mu.Lock()
	room.snapshotStaleFrames = 50
	room.mu.Unlock()

	room.UpdateSnapshot(10, []byte{1, 2, 3})
	if room.SnapshotStaleFrames() != 0 {
		t.Fatalf("UpdateSnapshot should reset stale frames, got %d", room.SnapshotStaleFrames())
	}
}

func TestGetDataStoreEntries_Empty(t *testing.T) {
	room := newP0Room()
	entries := room.GetDataStoreEntries()
	if len(entries) != 0 {
		t.Fatalf("want 0 entries, got %d", len(entries))
	}
}

func TestGetDataStoreEntries_AfterSet(t *testing.T) {
	room := newP0Room()
	room.AddPlayer(1, nopConn{}, 0)
	room.SetData(1, 42, []byte("hello"))
	entries := room.GetDataStoreEntries()
	if len(entries) != 1 {
		t.Fatalf("want 1 entry, got %d", len(entries))
	}
	if entries[0].Key != 42 {
		t.Fatalf("want key=42, got %d", entries[0].Key)
	}
}

func TestGetConfig_ReturnsConfig(t *testing.T) {
	cfg := RoomConfig{
		FrameRate:       15,
		MaxPlayers:      8,
		FrameBufferSize: 300,
	}
	room := NewRoomWithConfig(cfg)
	got := room.GetConfig()
	if got.FrameRate != 15 {
		t.Fatalf("want FrameRate=15, got %d", got.FrameRate)
	}
	if got.MaxPlayers != 8 {
		t.Fatalf("want MaxPlayers=8, got %d", got.MaxPlayers)
	}
}

func TestPendingInputsLen_ZeroInitially(t *testing.T) {
	room := newP0Room()
	if room.PendingInputsLen() != 0 {
		t.Fatalf("want 0, got %d", room.PendingInputsLen())
	}
}

func TestPendingInputsLen_AfterOnInput(t *testing.T) {
	room := newP0Room()
	room.AddPlayer(1, nopConn{}, 0)
	setRunning(room, true)
	room.OnInput(1, []byte{0xFF})
	if room.PendingInputsLen() != 1 {
		t.Fatalf("want 1, got %d", room.PendingInputsLen())
	}
}

func TestStartedAt_ZeroBeforeStart(t *testing.T) {
	room := newP0Room()
	if !room.StartedAt().IsZero() {
		t.Fatal("StartedAt should be zero before Start")
	}
}

func TestStartedAt_SetAfterStart(t *testing.T) {
	room := NewRoomWithConfig(RoomConfig{
		FrameRate:           20,
		FrameBufferSize:     40,
		SnapshotIntervalFrames: 100,
	})
	room.Start()
	defer room.Stop()
	if room.StartedAt().IsZero() {
		t.Fatal("StartedAt should be non-zero after Start")
	}
}

func TestIsDesyncDetected_FalseInitially(t *testing.T) {
	room := newP0Room()
	if room.IsDesyncDetected() {
		t.Fatal("IsDesyncDetected should be false initially")
	}
}

// ─── Reliable 消息通道 ─────────────────────────────────────────────────────────

// recordConn 记录 Send 收到的消息，用于验证 Reliable 路径
type recordConn struct {
	mu   sync.Mutex
	msgs []*codec.Message
}

func (r *recordConn) Send(msg *codec.Message) error {
	r.mu.Lock()
	r.msgs = append(r.msgs, msg)
	r.mu.Unlock()
	return nil
}
func (r *recordConn) Close() error { return nil }

func (r *recordConn) count() int {
	r.mu.Lock()
	defer r.mu.Unlock()
	return len(r.msgs)
}

func TestSendReliableToPlayer_Online_ImmediatelySent(t *testing.T) {
	room := newP0Room()
	rc := &recordConn{}
	room.AddPlayer(1, rc, 0)

	inner := codec.NewCoreMessage(0x01, []byte("ping"))
	room.SendReliableToPlayer(1, inner)

	if rc.count() != 1 {
		t.Fatalf("want 1 message sent, got %d", rc.count())
	}
}

func TestSendReliableToPlayer_Offline_Buffered_NotSentImmediately(t *testing.T) {
	room := newP0Room()
	rc := &recordConn{}
	room.AddPlayer(1, rc, 0)
	room.DisconnectPlayer(1)

	inner := codec.NewCoreMessage(0x01, []byte("data"))
	room.SendReliableToPlayer(1, inner)

	// 离线时不立即发送，但应已入队
	if rc.count() != 0 {
		t.Fatalf("offline player should not receive immediate send, got %d", rc.count())
	}
	// 验证 seq 已分配（缓冲区已有消息）
	msgs, stale := room.GetS2CReliableSince(1, 0)
	if stale {
		t.Fatal("should not be stale")
	}
	if len(msgs) != 1 {
		t.Fatalf("want 1 buffered msg, got %d", len(msgs))
	}
}

func TestSendReliableToPlayer_UnknownPlayer_NoOp(t *testing.T) {
	room := newP0Room()
	// 不应 panic
	room.SendReliableToPlayer(99, codec.NewCoreMessage(0x01, nil))
}

func TestGetS2CReliableSince_NoMessages(t *testing.T) {
	room := newP0Room()
	room.AddPlayer(1, nopConn{}, 0)

	msgs, stale := room.GetS2CReliableSince(1, 0)
	if stale || len(msgs) != 0 {
		t.Fatalf("want empty/non-stale, got msgs=%d stale=%v", len(msgs), stale)
	}
}

func TestGetS2CReliableSince_ReturnsMsgsAfterSeq(t *testing.T) {
	room := newP0Room()
	room.AddPlayer(1, nopConn{}, 0)

	for i := 0; i < 5; i++ {
		room.SendReliableToPlayer(1, codec.NewCoreMessage(0x01, []byte{byte(i)}))
	}

	// afterSeq=2 → 应返回 seq 3/4/5
	msgs, stale := room.GetS2CReliableSince(1, 2)
	if stale {
		t.Fatal("should not be stale")
	}
	if len(msgs) != 3 {
		t.Fatalf("want 3 msgs, got %d", len(msgs))
	}
}

func TestGetS2CReliableSince_Stale_WhenSeqOverflowBuffer(t *testing.T) {
	room := newP0Room()
	room.AddPlayer(1, nopConn{}, 0)

	// 发送超过 s2cBufSize(256) 条消息，使旧 seq 被覆盖
	for i := 0; i < s2cBufSize+10; i++ {
		room.SendReliableToPlayer(1, codec.NewCoreMessage(0x01, nil))
	}

	// afterSeq=0 已超出缓冲区 → stale
	_, stale := room.GetS2CReliableSince(1, 0)
	if !stale {
		t.Fatal("want stale=true when afterSeq is too old")
	}
}

func TestGetS2CReliableSince_UnknownPlayer_EmptyNonStale(t *testing.T) {
	room := newP0Room()
	msgs, stale := room.GetS2CReliableSince(99, 0)
	if stale || len(msgs) != 0 {
		t.Fatalf("unknown player: want empty/non-stale, got msgs=%d stale=%v", len(msgs), stale)
	}
}

func TestGetLastProcessedC2SSeq_InitiallyZero(t *testing.T) {
	room := newP0Room()
	room.AddPlayer(1, nopConn{}, 0)
	if seq := room.GetLastProcessedC2SSeq(1); seq != 0 {
		t.Fatalf("want 0, got %d", seq)
	}
}

func TestSetLastProcessedC2SSeq_UpdatesValue(t *testing.T) {
	room := newP0Room()
	room.AddPlayer(1, nopConn{}, 0)
	room.SetLastProcessedC2SSeq(1, 42)
	if seq := room.GetLastProcessedC2SSeq(1); seq != 42 {
		t.Fatalf("want 42, got %d", seq)
	}
}

func TestSetLastProcessedC2SSeq_UnknownPlayer_NoOp(t *testing.T) {
	room := newP0Room()
	room.SetLastProcessedC2SSeq(99, 10) // 不应 panic
	if seq := room.GetLastProcessedC2SSeq(99); seq != 0 {
		t.Fatalf("unknown player should return 0, got %d", seq)
	}
}

func TestBroadcastReliable_SendsToAllOnline(t *testing.T) {
	room := newP0Room()
	rc1, rc2 := &recordConn{}, &recordConn{}
	room.AddPlayer(1, rc1, 0)
	room.AddPlayer(2, rc2, 0)

	inner := codec.NewCoreMessage(0x01, []byte("broadcast"))
	room.BroadcastReliable(-1, inner) // excludeId=-1 → 不排除任何人

	if rc1.count() != 1 || rc2.count() != 1 {
		t.Fatalf("want both players to receive 1 msg: p1=%d p2=%d", rc1.count(), rc2.count())
	}
}

func TestBroadcastReliable_ExcludesTargetPlayer(t *testing.T) {
	room := newP0Room()
	rc1, rc2 := &recordConn{}, &recordConn{}
	room.AddPlayer(1, rc1, 0)
	room.AddPlayer(2, rc2, 0)

	inner := codec.NewCoreMessage(0x01, []byte("exclude"))
	room.BroadcastReliable(1, inner) // 排除 player 1

	if rc1.count() != 0 {
		t.Fatalf("excluded player should not receive message, got %d", rc1.count())
	}
	if rc2.count() != 1 {
		t.Fatalf("other player should receive 1 msg, got %d", rc2.count())
	}
}

// ─── ForEachPlayer（含离线）────────────────────────────────────────────────────

func TestForEachPlayer_IncludesOnlineAndOffline(t *testing.T) {
	room := newP0Room()
	room.AddPlayer(1, nopConn{}, 0)
	room.AddPlayer(2, nopConn{}, 0)
	room.DisconnectPlayer(2) // player 2 变为离线

	seen := map[int32]PlayerState{}
	room.ForEachPlayer(func(info PlayerInfo) {
		seen[info.ID] = info.State
	})

	if len(seen) != 2 {
		t.Fatalf("want 2 players, got %d", len(seen))
	}
	if seen[1] != PlayerOnline {
		t.Errorf("player 1 should be Online, got %v", seen[1])
	}
	if seen[2] != PlayerDisconnected {
		t.Errorf("player 2 should be Disconnected, got %v", seen[2])
	}
}

func TestForEachPlayer_DisconnectTime_SetForOffline(t *testing.T) {
	room := newP0Room()
	room.AddPlayer(1, nopConn{}, 0)
	room.DisconnectPlayer(1)

	room.ForEachPlayer(func(info PlayerInfo) {
		if info.State == PlayerDisconnected && info.DisconnectTime == 0 {
			t.Error("DisconnectTime should be set for disconnected player")
		}
	})
}

func TestForEachPlayer_EmptyRoom_NoPanic(t *testing.T) {
	room := newP0Room()
	called := 0
	room.ForEachPlayer(func(PlayerInfo) { called++ })
	if called != 0 {
		t.Fatalf("want 0 calls, got %d", called)
	}
}

// ─── SetInitialSnapshot ────────────────────────────────────────────────────────

func TestSetInitialSnapshot_SetsData(t *testing.T) {
	room := newP0Room()
	data := []byte{1, 2, 3, 4}
	room.SetInitialSnapshot(data)

	fn, got := room.GetSnapshot()
	if fn != 0 {
		t.Fatalf("want frame 0, got %d", fn)
	}
	if string(got) != string(data) {
		t.Fatalf("snapshot data mismatch: want %v got %v", data, got)
	}
}

func TestSetInitialSnapshot_ResetsStaleCounter(t *testing.T) {
	room := newP0Room()
	room.mu.Lock()
	room.snapshotStaleFrames = 99
	room.mu.Unlock()

	room.SetInitialSnapshot([]byte{0xFF})
	if room.SnapshotStaleFrames() != 0 {
		t.Fatalf("SetInitialSnapshot should reset stale frames, got %d", room.SnapshotStaleFrames())
	}
}

// ─── tickLoop: panic 恢复路径 ──────────────────────────────────────────────────

// panicDelegate 在 OnRoomPaused 时 panic，触发 tickLoop 的 recover 路径
type panicDelegate struct {
	NoopRoomDelegate
	mu       sync.Mutex
	panicked bool
}

func (p *panicDelegate) OnRoomPaused(*Room, FrameSyncPauseReason) {
	panic("injected panic for test")
}
func (p *panicDelegate) OnRoomPanicked(_ *Room, _ []int32) {
	p.mu.Lock()
	p.panicked = true
	p.mu.Unlock()
}
func (p *panicDelegate) wasPanicked() bool {
	p.mu.Lock()
	defer p.mu.Unlock()
	return p.panicked
}

func TestTickLoop_PanicRecovery_CallsOnRoomPanicked(t *testing.T) {
	// 配置极小的快照检查窗口，使 stepFrame 快速触发 snapshotStale → OnRoomPaused → panic
	room := NewRoomWithConfig(RoomConfig{
		FrameRate:              50,
		FrameBufferSize:        50,
		SnapshotIntervalFrames: 1, // 1 帧间隔，staleLimit=3 帧后触发
	})
	d := &panicDelegate{}
	room.SetDelegate(d)
	room.Start()

	deadline := time.Now().Add(2 * time.Second)
	for time.Now().Before(deadline) {
		if d.wasPanicked() {
			break
		}
		time.Sleep(10 * time.Millisecond)
	}

	if !d.wasPanicked() {
		t.Fatal("expected OnRoomPanicked to be called after tickLoop panic recovery")
	}
	// 房间应已停止运行
	if room.IsRunning() {
		t.Error("room should not be running after panic recovery")
	}
}

// ─── tickLoop: 帧号溢出保护 ───────────────────────────────────────────────────

func TestStepFrame_FrameNumberOverflow_StopsRoom(t *testing.T) {
	room := newP0Room()
	d := &mockDelegate{}
	room.SetDelegate(d)
	setRunning(room, true)

	// 将帧号设为 MaxUint32，stepFrame 应主动停止房间
	room.mu.Lock()
	room.frameNumber = ^uint32(0) // math.MaxUint32
	room.stopCh = make(chan struct{})
	room.mu.Unlock()

	room.stepFrame()

	if room.IsRunning() {
		t.Fatal("room should be stopped after frame number overflow")
	}
	if d.stoppedCount() != 1 {
		t.Fatalf("want OnRoomStopped called once, got %d", d.stoppedCount())
	}
}
