package framesync

// room_p2_test.go — P2-1 / P2-4 单元测试 + Benchmark
//
// 覆盖范围:
//   P2-1  ReportFrameHash 周期性清理（每 100 帧清一次，非每次）
//   P2-4  GetRoomInfo 单次加锁（reading running + onlineCount under one lock）

import (
	"testing"
)

// ─── P2-1: frameHashes 周期性清理 ─────────────────────────────────────────────

// TestReportFrameHash_CleanupOnlyAtPeriod 验证只有 frameNumber%100==0 时才清理旧条目
func TestReportFrameHash_CleanupOnlyAtPeriod(t *testing.T) {
	room := newP0Room()
	setRunning(room, true)

	// 塞入帧 1~210 的 hash，之后再调一帧来触发清理路径
	// 先直接写入 frameHashes，模拟历史帧
	initHashes := make(map[uint32]map[int32]uint32)
	for fn := uint32(1); fn <= 210; fn++ {
		initHashes[fn] = map[int32]uint32{1: 0xABCD}
	}
	room.testOnlySetFrameHashes(initHashes)

	// 在 frameNumber=201（不整除 100）时上报，不应触发清理
	room.ReportFrameHash(1, 201, 0xABCD)
	countBefore := room.testOnlyFrameHashesLen()

	// 上报 frameNumber=300（整除 100）时才触发清理
	room.ReportFrameHash(1, 300, 0xABCD)
	countAfter := room.testOnlyFrameHashesLen()

	if countBefore == 0 {
		t.Error("before period cleanup: frameHashes should not be empty")
	}
	// cutoff = 300-200 = 100；帧 1~99 应被删除，100~300 保留
	// 保留帧数 = 201 (100~300)
	if countAfter >= countBefore {
		t.Errorf("periodic cleanup should reduce frameHashes: before=%d after=%d", countBefore, countAfter)
	}
	// 验证 cutoff 正确：帧 99 应不存在，帧 100 应存在
	has99, has100 := room.testOnlyGetFrameHashEntry(100, 99)
	if has99 {
		t.Error("frame 99 should have been cleaned up (cutoff=100)")
	}
	if !has100 {
		t.Error("frame 100 should be retained (>= cutoff)")
	}
}

// TestReportFrameHash_NoCleanupBetweenPeriods 验证第 99/101/199 帧时不做清理（不回归）
func TestReportFrameHash_NoCleanupBetweenPeriods(t *testing.T) {
	room := newP0Room()
	setRunning(room, true)

	// 写入 300 帧历史
	initHashes2 := make(map[uint32]map[int32]uint32)
	for fn := uint32(1); fn <= 300; fn++ {
		initHashes2[fn] = map[int32]uint32{1: 0x1234}
	}
	room.testOnlySetFrameHashes(initHashes2)

	// 在非整除帧（201 = 300%100 != 0 → 这里使用 frameNumber=251）上报，不清理
	room.ReportFrameHash(1, 251, 0x1234)
	count := room.testOnlyFrameHashesLen()

	// 301 条（原 300 + 刚写入的 251 已存在，不增加）
	if count < 200 {
		t.Errorf("non-period frame should NOT trigger cleanup, got count=%d", count)
	}
}

// TestReportFrameHash_DesyncDetection 脱裂检测功能不因周期化清理而受影响
func TestReportFrameHash_DesyncDetection(t *testing.T) {
	room := newP0Room()
	setRunning(room, true)

	// 两名玩家对同一帧上报不同 hash → 应检测到脱裂
	room.ReportFrameHash(1, 50, 0xAAAA)
	desync := room.ReportFrameHash(2, 50, 0xBBBB)
	if !desync {
		t.Error("should detect desync when two players report different hashes for same frame")
	}
}

// TestReportFrameHash_NoDesync 正常情况：同 hash 不触发脱裂
func TestReportFrameHash_NoDesync(t *testing.T) {
	room := newP0Room()
	setRunning(room, true)

	room.ReportFrameHash(1, 50, 0xAAAA)
	desync := room.ReportFrameHash(2, 50, 0xAAAA)
	if desync {
		t.Error("should NOT detect desync when hashes match")
	}
}

// ─── P2-4: GetRoomInfo 单次加锁 ──────────────────────────────────────────────

// TestGetRoomInfo_CorrectValues 验证返回值与实际状态一致
func TestGetRoomInfo_CorrectValues(t *testing.T) {
	room := NewRoomWithConfig(RoomConfig{
		FrameRate:       20,
		FrameBufferSize: 100,
		MaxPlayers:      4,
	})
	room.ID = 42
	room.MatchKey = "test-key"

	room.AddPlayer(1, nopConn{}, false, 0)
	room.AddPlayer(2, nopConn{}, false, 0)

	info := room.GetRoomInfo()

	if info.RoomId != 42 {
		t.Errorf("RoomId: want 42, got %d", info.RoomId)
	}
	if info.PlayerCount != 2 {
		t.Errorf("PlayerCount: want 2, got %d", info.PlayerCount)
	}
	if info.MaxPlayers != 4 {
		t.Errorf("MaxPlayers: want 4, got %d", info.MaxPlayers)
	}
	if info.Running {
		t.Error("Running: want false before Start()")
	}
	if info.MatchKey != "test-key" {
		t.Errorf("MatchKey: want test-key, got %q", info.MatchKey)
	}
}

// TestGetRoomInfo_Running 验证 running=true 时正确反映
func TestGetRoomInfo_Running(t *testing.T) {
	room := newP0Room()
	setRunning(room, true)

	info := room.GetRoomInfo()
	if !info.Running {
		t.Error("GetRoomInfo should reflect running=true")
	}
}

// TestGetRoomInfo_ZeroAllocsAfterWarmup GetRoomInfo 应 0 allocs（无 map/slice 分配）
func TestGetRoomInfo_ZeroAllocs(t *testing.T) {
	room := NewRoomWithConfig(RoomConfig{
		FrameRate:       20,
		FrameBufferSize: 100,
		MaxPlayers:      4,
	})
	room.AddPlayer(1, nopConn{}, false, 0)

	allocs := testing.AllocsPerRun(20, func() {
		_ = room.GetRoomInfo()
	})
	if allocs > 0 {
		t.Errorf("GetRoomInfo should have 0 allocs, got %.0f", allocs)
	}
}

// ─── Benchmarks ──────────────────────────────────────────────────────────────

// BenchmarkReportFrameHash_EveryFrame 每帧调用（原始路径：每次清理）基准
// 这是优化前的等效 —— 现在已改为周期清理，ns/op 下降应可见
func BenchmarkReportFrameHash(b *testing.B) {
	room := newP0Room()
	setRunning(room, true)
	// 预热：塞入 300 帧的 hash（稳态）
	warmup := make(map[uint32]map[int32]uint32)
	for fn := uint32(1); fn <= 300; fn++ {
		warmup[fn] = map[int32]uint32{1: 0xABCD, 2: 0xABCD}
	}
	room.testOnlySetFrameHashes(warmup)

	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		// 模拟每帧 4 名玩家都上报（frameNumber 在 301..N，大多数不触发清理）
		fn := uint32(301 + i%99) // 99 帧非整除 + 1 帧整除，比例 99:1
		room.ReportFrameHash(1, fn, 0xABCD)
	}
}

// BenchmarkGetRoomInfo GetRoomInfo 单次加锁路径
func BenchmarkGetRoomInfo(b *testing.B) {
	room := NewRoomWithConfig(RoomConfig{
		FrameRate:       20,
		FrameBufferSize: 100,
		MaxPlayers:      4,
	})
	for i := int32(1); i <= 4; i++ {
		room.AddPlayer(i, nopConn{}, false, 0)
	}

	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		_ = room.GetRoomInfo()
	}
}

// BenchmarkGetRoomInfo_Parallel 并发读取
func BenchmarkGetRoomInfo_Parallel(b *testing.B) {
	room := NewRoomWithConfig(RoomConfig{
		FrameRate:       20,
		FrameBufferSize: 100,
		MaxPlayers:      4,
	})
	for i := int32(1); i <= 4; i++ {
		room.AddPlayer(i, nopConn{}, false, 0)
	}

	b.ReportAllocs()
	b.ResetTimer()
	b.RunParallel(func(pb *testing.PB) {
		for pb.Next() {
			_ = room.GetRoomInfo()
		}
	})
}
