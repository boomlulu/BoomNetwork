package framesync

// room_p0_test.go — P0 性能优化的单元测试 + Benchmark
//
// 覆盖范围:
//   P0-3  pendingInputs/Events 双缓冲 swap（零 GC 验证）
//   P0-4  onlineCount 原子计数器（O(1) PlayerCount）

import (
	"sync"
	"testing"
)

// ─── 辅助 ────────────────────────────────────────────────────────────────────
// 注: nopConn / newTestRoom 已在 reconciler_test.go 中定义，此处直接复用。

// newP0Room 创建专供 P0 测试使用的小型房间（与 reconciler_test.go 中的
// newTestRoom 语义相同，但名字不同以避免 redeclared 错误）
func newP0Room() *Room {
	return NewRoomWithConfig(RoomConfig{
		FrameRate:       20,
		FrameBufferSize: 100,
	})
}

// setRunning 在持锁前提下设置 running（仅供测试使用）
func setRunning(r *Room, v bool) {
	r.mu.Lock()
	r.running = v
	r.mu.Unlock()
}

// ─── P0-4: onlineCount 原子计数器 ────────────────────────────────────────────

// TestOnlineCount_AddPlayer 验证 AddPlayer 递增计数
func TestOnlineCount_AddPlayer(t *testing.T) {
	room := newP0Room()

	if got := room.PlayerCount(); got != 0 {
		t.Fatalf("initial count should be 0, got %d", got)
	}

	room.AddPlayer(1, nopConn{})
	if got := room.PlayerCount(); got != 1 {
		t.Errorf("after AddPlayer(1): want 1, got %d", got)
	}

	room.AddPlayer(2, nopConn{})
	if got := room.PlayerCount(); got != 2 {
		t.Errorf("after AddPlayer(2): want 2, got %d", got)
	}
}

// TestOnlineCount_DisconnectPlayer 验证 DisconnectPlayer 递减计数
func TestOnlineCount_DisconnectPlayer(t *testing.T) {
	room := newP0Room()
	room.AddPlayer(1, nopConn{})
	room.AddPlayer(2, nopConn{})

	room.DisconnectPlayer(1)
	if got := room.PlayerCount(); got != 1 {
		t.Errorf("after Disconnect(1): want 1, got %d", got)
	}

	room.DisconnectPlayer(2)
	if got := room.PlayerCount(); got != 0 {
		t.Errorf("after Disconnect(2): want 0, got %d", got)
	}
}

// TestOnlineCount_DisconnectPlayer_IdempotentOnMissing 对不存在 ID 不 panic、不误减
func TestOnlineCount_DisconnectPlayer_IdempotentOnMissing(t *testing.T) {
	room := newP0Room()
	room.AddPlayer(1, nopConn{})

	room.DisconnectPlayer(99) // 不存在的玩家
	if got := room.PlayerCount(); got != 1 {
		t.Errorf("spurious Disconnect should not change count, got %d", got)
	}
}

// TestOnlineCount_RemovePlayer_WhenOnline 移除在线玩家，计数 -1
func TestOnlineCount_RemovePlayer_WhenOnline(t *testing.T) {
	room := newP0Room()
	room.AddPlayer(1, nopConn{})
	room.AddPlayer(2, nopConn{})

	room.RemovePlayer(1)
	if got := room.PlayerCount(); got != 1 {
		t.Errorf("after RemovePlayer of online player: want 1, got %d", got)
	}
}

// TestOnlineCount_RemovePlayer_WhenDisconnected 移除已断线玩家，onlineCount 不双减
func TestOnlineCount_RemovePlayer_WhenDisconnected(t *testing.T) {
	room := newP0Room()
	room.AddPlayer(1, nopConn{})
	room.AddPlayer(2, nopConn{})

	room.DisconnectPlayer(1) // count = 1
	room.RemovePlayer(1)     // 移除已断线，count 仍为 1
	if got := room.PlayerCount(); got != 1 {
		t.Errorf("RemovePlayer of disconnected player must not double-decrement: want 1, got %d", got)
	}
}

// TestOnlineCount_Reconnect 断线后重连，计数恢复 +1
func TestOnlineCount_Reconnect(t *testing.T) {
	room := newP0Room()
	room.AddPlayer(1, nopConn{})
	room.AddPlayer(2, nopConn{})

	room.DisconnectPlayer(1) // count = 1
	if got := room.PlayerCount(); got != 1 {
		t.Fatalf("after disconnect: want 1, got %d", got)
	}

	// 重连（AddPlayer 对已断线玩家做的是重连分支）
	room.AddPlayer(1, nopConn{})
	if got := room.PlayerCount(); got != 2 {
		t.Errorf("after reconnect: want 2, got %d", got)
	}
}

// TestOnlineCount_AllPlayersDisconnected 全员断线后计数为 0
func TestOnlineCount_AllPlayersDisconnected(t *testing.T) {
	room := newP0Room()
	for i := int32(1); i <= 4; i++ {
		room.AddPlayer(i, nopConn{})
	}
	for i := int32(1); i <= 4; i++ {
		room.DisconnectPlayer(i)
	}
	if got := room.PlayerCount(); got != 0 {
		t.Errorf("all disconnected: want 0, got %d", got)
	}
}

// TestOnlineCount_Concurrent 并发 Add/Disconnect，最终计数严格正确
func TestOnlineCount_Concurrent(t *testing.T) {
	const numPlayers = 64
	room := newP0Room()

	// 阶段 1: 并发加入
	var wg sync.WaitGroup
	for i := int32(1); i <= numPlayers; i++ {
		wg.Add(1)
		go func(id int32) {
			defer wg.Done()
			room.AddPlayer(id, nopConn{})
		}(i)
	}
	wg.Wait()
	if got := room.PlayerCount(); got != numPlayers {
		t.Fatalf("after concurrent AddPlayer: want %d, got %d", numPlayers, got)
	}

	// 阶段 2: 并发断线一半
	for i := int32(1); i <= numPlayers/2; i++ {
		wg.Add(1)
		go func(id int32) {
			defer wg.Done()
			room.DisconnectPlayer(id)
		}(i)
	}
	wg.Wait()
	if got := room.PlayerCount(); got != numPlayers/2 {
		t.Fatalf("after concurrent DisconnectPlayer: want %d, got %d", numPlayers/2, got)
	}
}

// ─── P0-3: pendingInputs/Events 双缓冲 swap ─────────────────────────────────

// TestPendingInputsBuf_DoubleBufferSwap 验证每次 stepFrame 后：
//  - pendingInputs 被清空（len=0）
//  - pendingInputsBuf 持有原数据（保留 cap，防止 GC 回收）
//  - cap 不跌为 0（无 = nil 赋值）
func TestPendingInputsBuf_DoubleBufferSwap(t *testing.T) {
	room := newP0Room()
	setRunning(room, true)

	input := []byte{0xAA, 0xBB}

	// 帧 1：加 3 条输入
	for i := 0; i < 3; i++ {
		room.OnInput(int32(i+1), input)
	}
	capBefore := cap(room.pendingInputs)
	if capBefore == 0 {
		t.Fatal("pendingInputs should have capacity before stepFrame")
	}

	room.stepFrame()

	// stepFrame 后：pendingInputs 应被清空但保留容量
	if got := len(room.pendingInputs); got != 0 {
		t.Errorf("after stepFrame, pendingInputs len should be 0, got %d", got)
	}
	if cap(room.pendingInputs) == 0 {
		t.Error("after stepFrame, pendingInputs cap must not drop to 0 (double-buffer swap failed)")
	}

	// pendingInputsBuf 应持有交换出去的数据（cap >= 3）
	if cap(room.pendingInputsBuf) < 3 {
		t.Errorf("pendingInputsBuf cap should be >= 3 (holds consumed slice), got %d", cap(room.pendingInputsBuf))
	}

	// 帧 2：再加 2 条输入，再次验证
	room.OnInput(10, input)
	room.OnInput(11, input)
	room.stepFrame()

	if len(room.pendingInputs) != 0 {
		t.Errorf("frame 2: pendingInputs should be empty, got %d", len(room.pendingInputs))
	}
	if cap(room.pendingInputs) == 0 {
		t.Error("frame 2: cap dropped to 0 — double-buffer swap not working")
	}
}

// TestPendingEventsBuf_DoubleBufferSwap 验证 pendingEvents 同样双缓冲复用
func TestPendingEventsBuf_DoubleBufferSwap(t *testing.T) {
	room := newP0Room()
	setRunning(room, true)

	room.EnqueueEvent(FrameEventPlayerJoined, 1)
	room.EnqueueEvent(FrameEventPlayerLeft, 2)

	room.stepFrame()

	if len(room.pendingEvents) != 0 {
		t.Errorf("after stepFrame, pendingEvents len should be 0, got %d", len(room.pendingEvents))
	}
	if cap(room.pendingEvents) == 0 {
		t.Error("pendingEvents cap dropped to 0 — double-buffer swap failed")
	}
	if cap(room.pendingEventsBuf) < 2 {
		t.Errorf("pendingEventsBuf cap should be >= 2, got %d", cap(room.pendingEventsBuf))
	}
}

// TestPendingBuf_CapNeverDropsToZero 连续 50 帧，cap 永不为 0
func TestPendingBuf_CapNeverDropsToZero(t *testing.T) {
	room := newP0Room()
	setRunning(room, true)

	input := []byte{0x01}
	for frame := 0; frame < 50; frame++ {
		if frame%3 == 0 { // 每 3 帧有输入
			room.OnInput(1, input)
		}
		room.stepFrame()
		if cap(room.pendingInputs) == 0 {
			t.Fatalf("frame %d: pendingInputs cap dropped to 0", frame)
		}
		if cap(room.pendingEventsBuf) == 0 {
			t.Fatalf("frame %d: pendingEventsBuf cap dropped to 0", frame)
		}
	}
}

// TestPendingBuf_InputDataCorrectAfterSwap 验证交换不破坏帧数据内容
func TestPendingBuf_InputDataCorrectAfterSwap(t *testing.T) {
	room := newP0Room()
	setRunning(room, true)

	room.OnInput(7, []byte{0x07, 0x08, 0x09})
	room.stepFrame()

	// 读取最新帧内容
	room.mu.Lock()
	pos := (room.frameRingPos - 1 + len(room.frameRing)) % len(room.frameRing)
	data := make([]byte, len(room.frameRing[pos].EncodedData))
	copy(data, room.frameRing[pos].EncodedData)
	room.mu.Unlock()

	decoded, err := DecodeFrameData(data)
	if err != nil {
		t.Fatalf("DecodeFrameData: %v", err)
	}
	if len(decoded.Inputs) != 1 {
		t.Fatalf("expected 1 input, got %d", len(decoded.Inputs))
	}
	if decoded.Inputs[0].PlayerId != 7 {
		t.Errorf("playerId: want 7, got %d", decoded.Inputs[0].PlayerId)
	}
	if len(decoded.Inputs[0].Data) != 3 || decoded.Inputs[0].Data[0] != 0x07 {
		t.Errorf("input data corrupted: %v", decoded.Inputs[0].Data)
	}
}

// ─── Benchmarks ──────────────────────────────────────────────────────────────

// BenchmarkPlayerCount 验证原子读取的 ns/op（无锁路径）
func BenchmarkPlayerCount(b *testing.B) {
	room := newP0Room()
	for i := int32(1); i <= 4; i++ {
		room.AddPlayer(i, nopConn{})
	}

	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		_ = room.PlayerCount()
	}
}

// BenchmarkPlayerCount_Parallel 并发读场景，验证无锁吞吐
func BenchmarkPlayerCount_Parallel(b *testing.B) {
	room := newP0Room()
	for i := int32(1); i <= 4; i++ {
		room.AddPlayer(i, nopConn{})
	}

	b.ReportAllocs()
	b.ResetTimer()
	b.RunParallel(func(pb *testing.PB) {
		for pb.Next() {
			_ = room.PlayerCount()
		}
	})
}

// BenchmarkStepFrame_AllocsPerOp 验证双缓冲后稳态帧的 allocs/op
// 稳态（warmup 后）期望 0 或极低分配（FrameData 结构体依逃逸分析而定）
func BenchmarkStepFrame_AllocsPerOp(b *testing.B) {
	room := NewRoomWithConfig(RoomConfig{
		FrameRate:       20,
		FrameBufferSize: 2400,
	})
	setRunning(room, true)

	inputData := []byte{0x01, 0x02, 0x03, 0x04}

	// warmup: 稳定化所有 buffer（frameBuf、ring slots、pendingInputsBuf）
	for i := 0; i < 20; i++ {
		room.OnInput(1, inputData)
		room.stepFrame()
	}

	b.ReportAllocs()
	b.ResetTimer()

	for i := 0; i < b.N; i++ {
		room.OnInput(1, inputData)
		room.stepFrame()
	}
}

// BenchmarkStepFrame_NoInput 无输入帧的开销下限
func BenchmarkStepFrame_NoInput(b *testing.B) {
	room := NewRoomWithConfig(RoomConfig{
		FrameRate:       20,
		FrameBufferSize: 2400,
	})
	setRunning(room, true)

	// warmup
	for i := 0; i < 10; i++ {
		room.stepFrame()
	}

	b.ReportAllocs()
	b.ResetTimer()

	for i := 0; i < b.N; i++ {
		room.stepFrame()
	}
}
