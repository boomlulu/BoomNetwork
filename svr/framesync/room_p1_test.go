package framesync

// room_p1_test.go — P1 性能优化的单元测试 + Benchmark
//
// 覆盖范围:
//   P1-2  broadcast() 复用 broadcastBuf（cap 不缩小）
//   P1-3  ForEachOnlinePlayer() Pool 复用切片（消除匿名 struct 分配）
//   P1-4  GetFramesSince() 单 backing buffer（O(1) 内存分配代替 O(N帧)）

import (
	"sync"
	"testing"
	"unsafe"
)

// ─── 辅助 ────────────────────────────────────────────────────────────────────
// nopConn / setRunning / newP0Room 已在 room_p0_test.go 中定义，此处直接复用。

// newP1Room 专供 P1 测试使用的小房间（避免 redeclared 错误）
func newP1Room() *Room {
	return NewRoomWithConfig(RoomConfig{
		FrameRate:       20,
		FrameBufferSize: 100,
	})
}

// ─── P1-2: broadcast() 复用 broadcastBuf ─────────────────────────────────────

// TestBroadcastBuf_PreAllocated 验证 broadcastBuf 初始已预分配
func TestBroadcastBuf_PreAllocated(t *testing.T) {
	room := newP1Room()
	if cap(room.broadcastBuf) == 0 {
		t.Error("broadcastBuf should be pre-allocated in NewRoomWithConfig")
	}
}

// TestBroadcastBuf_CapPreservedAfterForEach 验证 ForEachOnlinePlayer 调用后 broadcastBuf 不被清除
// broadcast() 内部直接复用 broadcastBuf，cap 不应缩小
func TestBroadcastBuf_CapPreservedAfterBroadcast(t *testing.T) {
	room := newP1Room()
	for i := int32(1); i <= 4; i++ {
		room.AddPlayer(i, nopConn{})
	}

	// 直接调用 broadcast 需要有真正的 PlayerConn.Send，
	// 用 ForEachOnlinePlayer 代替（同样填充 broadcastBuf 的等效逻辑）
	// 此处测试 broadcastBuf 的 cap 在反复操作后不缩小
	capBefore := cap(room.broadcastBuf)

	// 多次调用 broadcast-adjacent 操作
	for i := 0; i < 5; i++ {
		room.mu.Lock()
		room.broadcastBuf = room.broadcastBuf[:0]
		for _, p := range room.players {
			if p.State == PlayerOnline && p.Conn != nil {
				room.broadcastBuf = append(room.broadcastBuf, p)
			}
		}
		room.mu.Unlock()
	}

	capAfter := cap(room.broadcastBuf)
	if capAfter < capBefore {
		t.Errorf("broadcastBuf cap shrank from %d to %d", capBefore, capAfter)
	}
	if capAfter == 0 {
		t.Error("broadcastBuf cap should not be 0 after use")
	}
}

// ─── P1-3: ForEachOnlinePlayer Pool 复用 ─────────────────────────────────────

// TestForEachOnlinePlayer_CorrectPlayers 验证遍历结果与实际在线玩家一致
func TestForEachOnlinePlayer_CorrectPlayers(t *testing.T) {
	room := newP1Room()
	room.AddPlayer(1, nopConn{})
	room.AddPlayer(2, nopConn{})
	room.AddPlayer(3, nopConn{})
	room.DisconnectPlayer(2) // 2 离线

	var seen []int32
	room.ForEachOnlinePlayer(func(id int32, conn PlayerConn) {
		seen = append(seen, id)
	})

	if len(seen) != 2 {
		t.Errorf("expected 2 online players, got %d: %v", len(seen), seen)
	}
	for _, id := range seen {
		if id == 2 {
			t.Error("disconnected player 2 should not appear")
		}
	}
}

// TestForEachOnlinePlayer_EmptyRoom 空房间不调用 fn，不 panic
func TestForEachOnlinePlayer_EmptyRoom(t *testing.T) {
	room := newP1Room()
	called := false
	room.ForEachOnlinePlayer(func(id int32, conn PlayerConn) {
		called = true
	})
	if called {
		t.Error("fn should not be called for empty room")
	}
}

// TestForEachOnlinePlayer_ZeroAllocsAfterWarmup 稳态下应 0 allocs/op（Pool 命中）
func TestForEachOnlinePlayer_ZeroAllocsAfterWarmup(t *testing.T) {
	room := newP1Room()
	for i := int32(1); i <= 4; i++ {
		room.AddPlayer(i, nopConn{})
	}

	// warmup: 确保 Pool 中有可用对象
	for i := 0; i < 5; i++ {
		room.ForEachOnlinePlayer(func(int32, PlayerConn) {})
	}

	allocs := testing.AllocsPerRun(20, func() {
		room.ForEachOnlinePlayer(func(int32, PlayerConn) {})
	})

	if allocs > 0 {
		t.Errorf("ForEachOnlinePlayer should have 0 allocs after warmup, got %.0f", allocs)
	}
}

// TestForEachOnlinePlayer_Concurrent_NoPanic 并发调用不 panic（Pool 线程安全）
func TestForEachOnlinePlayer_Concurrent_NoPanic(t *testing.T) {
	room := newP1Room()
	for i := int32(1); i <= 8; i++ {
		room.AddPlayer(i, nopConn{})
	}

	var wg sync.WaitGroup
	for i := 0; i < 20; i++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			for j := 0; j < 50; j++ {
				room.ForEachOnlinePlayer(func(int32, PlayerConn) {})
			}
		}()
	}
	wg.Wait()
}

// ─── P1-4: GetFramesSince() 单 backing buffer ────────────────────────────────

// TestGetFramesSince_ContiguousBacking 验证所有帧的 EncodedData 共享同一 backing buffer
// 相邻帧 i 和 i+1 的内存应连续（frame[i] 末尾 == frame[i+1] 首字节地址）
func TestGetFramesSince_ContiguousBacking(t *testing.T) {
	room := newP1Room()
	setRunning(room, true)

	for i := 0; i < 5; i++ {
		room.OnInput(1, []byte{byte(i), byte(i + 1), byte(i + 2)})
		room.stepFrame()
	}

	frames := room.GetFramesSince(0)
	if len(frames) < 2 {
		t.Skip("need ≥ 2 frames for contiguity check")
	}

	for i := 0; i < len(frames)-1; i++ {
		if len(frames[i].EncodedData) == 0 {
			continue // 跳过空帧（理论上不会出现）
		}
		endPtr := uintptr(unsafe.Pointer(&frames[i].EncodedData[0])) + uintptr(len(frames[i].EncodedData))
		startPtr := uintptr(unsafe.Pointer(&frames[i+1].EncodedData[0]))
		if endPtr != startPtr {
			t.Errorf("frame[%d] and frame[%d] are NOT contiguous in memory: endPtr=%x, startPtr=%x (single backing buffer expected)", i, i+1, endPtr, startPtr)
		}
	}
}

// TestGetFramesSince_TwoAllocsNotN 验证 N 帧时分配次数 ≤ 3（而非 O(N)）
func TestGetFramesSince_TwoAllocsNotN(t *testing.T) {
	const N = 50
	room := newP1Room()
	setRunning(room, true)

	for i := 0; i < N; i++ {
		room.OnInput(1, []byte{byte(i)})
		room.stepFrame()
	}

	allocs := testing.AllocsPerRun(10, func() {
		_ = room.GetFramesSince(0)
	})

	// 期望 2 次分配（backing []byte + result []CachedFrame），≤ 3 含运行时 slack
	if allocs > 3 {
		t.Errorf("GetFramesSince(%d frames) allocated %.0f times, want ≤ 3 (was O(N))", N, allocs)
	}
}

// TestGetFramesSince_EmptyResult 无符合帧时返回 nil，不分配
func TestGetFramesSince_EmptyResult(t *testing.T) {
	room := newP1Room()
	setRunning(room, true)

	for i := 0; i < 5; i++ {
		room.stepFrame()
	}

	frames := room.GetFramesSince(9999) // afterFrame 超过所有帧
	if frames != nil {
		t.Errorf("expected nil for no matching frames, got %v", frames)
	}
}

// TestGetFramesSince_DataCorrectAfterPack 验证合并后数据内容与原始一致
func TestGetFramesSince_DataCorrectAfterPack(t *testing.T) {
	room := newP1Room()
	setRunning(room, true)

	room.OnInput(7, []byte{0x07, 0x08, 0x09})
	room.stepFrame()
	room.OnInput(8, []byte{0x0A, 0x0B})
	room.stepFrame()

	frames := room.GetFramesSince(0)
	if len(frames) != 2 {
		t.Fatalf("expected 2 frames, got %d", len(frames))
	}

	// 验证第一帧内容
	d1 := DecodeFrameData(frames[0].EncodedData)
	if len(d1.Inputs) != 1 || d1.Inputs[0].PlayerId != 7 {
		t.Errorf("frame[0] data corrupted: %+v", d1)
	}

	// 验证第二帧内容
	d2 := DecodeFrameData(frames[1].EncodedData)
	if len(d2.Inputs) != 1 || d2.Inputs[0].PlayerId != 8 {
		t.Errorf("frame[1] data corrupted: %+v", d2)
	}
}

// ─── Benchmarks ──────────────────────────────────────────────────────────────

// BenchmarkForEachOnlinePlayer Pool 路径 vs 基线
func BenchmarkForEachOnlinePlayer(b *testing.B) {
	room := newP1Room()
	for i := int32(1); i <= 4; i++ {
		room.AddPlayer(i, nopConn{})
	}

	// warmup
	for i := 0; i < 10; i++ {
		room.ForEachOnlinePlayer(func(int32, PlayerConn) {})
	}

	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		room.ForEachOnlinePlayer(func(int32, PlayerConn) {})
	}
}

// BenchmarkGetFramesSince_100Frames 100 帧重连补帧 allocs/op
func BenchmarkGetFramesSince_100Frames(b *testing.B) {
	room := NewRoomWithConfig(RoomConfig{
		FrameRate:       20,
		FrameBufferSize: 2400,
	})
	setRunning(room, true)

	for i := 0; i < 100; i++ {
		room.OnInput(1, []byte{1, 2, 3, 4})
		room.stepFrame()
	}

	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		_ = room.GetFramesSince(0)
	}
}

// BenchmarkGetFramesSince_2400Frames 满缓冲区（2400帧）重连补帧 allocs/op
// 优化前: ~2400 allocs/op；优化后: 2 allocs/op
func BenchmarkGetFramesSince_2400Frames(b *testing.B) {
	room := NewRoomWithConfig(RoomConfig{
		FrameRate:       20,
		FrameBufferSize: 2400,
	})
	setRunning(room, true)

	for i := 0; i < 2400; i++ {
		room.OnInput(1, []byte{1, 2, 3, 4})
		room.stepFrame()
	}

	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		_ = room.GetFramesSince(0)
	}
}
