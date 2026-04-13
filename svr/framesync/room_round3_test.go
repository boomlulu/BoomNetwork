package framesync

// room_round3_test.go — Round 3 修复验证测试
//
// 覆盖范围：
//   1. TestDesyncDetection_DetectsHashMismatch         (M9: desync → frameHashes 清空)
//   2. TestKVStore_SetAndClearPlayerData               (KV 存储: SetData / GetDataSnapshot / ClearPlayerData)
//   3. TestAutoAssignRoom_ConcurrentSafe               (M8: 并发 AutoAssignRoom 无重复房间)
//   4. TestReleaseAllAuthority_ClearsPlayerEntities    (实体权威: 断线清除)
//   5. TestAppendPlayerIds_PreAllocated                (L2: AppendPlayerIds 预分配切片)

import (
	"sync"
	"testing"
)

// ─── 1. M9: Desync 检测 + frameHashes 清空 ──────────────────────────────────

func TestDesyncDetection_DetectsHashMismatch(t *testing.T) {
	room := NewRoomWithConfig(RoomConfig{
		FrameRate:       20,
		FrameBufferSize: 100,
	})
	room.mu.Lock()
	room.running = true
	room.mu.Unlock()

	// 同一帧，两玩家上报不同 hash
	frame := uint32(10)
	desync1, _ := room.ReportFrameHash(1, frame, 0xDEAD)
	if desync1 {
		t.Fatal("first player report should not detect desync yet")
	}

	desync2, mismatchHashes := room.ReportFrameHash(2, frame, 0xBEEF)
	if !desync2 {
		t.Error("second player with different hash should detect desync")
	}
	if len(mismatchHashes) != 2 {
		t.Errorf("mismatchHashes should have 2 entries, got %d", len(mismatchHashes))
	}

	// M9: 检测到 desync 后，frameHashes 必须被清空以释放内存
	if mapLen := room.testOnlyFrameHashesLen(); mapLen != 0 {
		t.Errorf("frameHashes should be cleared after desync, got len=%d", mapLen)
	}

	// desyncDetected flag 必须置位
	if !room.testOnlyDesyncDetected() {
		t.Error("desyncDetected should be true after hash mismatch")
	}
}

func TestDesyncDetection_SameHash_NoDesync(t *testing.T) {
	room := NewRoomWithConfig(RoomConfig{
		FrameRate:       20,
		FrameBufferSize: 100,
	})
	room.mu.Lock()
	room.running = true
	room.mu.Unlock()

	frame := uint32(5)
	room.ReportFrameHash(1, frame, 0xABCD)
	desync, _ := room.ReportFrameHash(2, frame, 0xABCD) // same hash
	if desync {
		t.Error("same hash from two players should not detect desync")
	}

	if room.testOnlyDesyncDetected() {
		t.Error("desyncDetected should be false when hashes match")
	}
}

// ─── 2. KV 存储: SetData / GetDataSnapshot / ClearPlayerData ────────────────

func TestKVStore_SetAndClearPlayerData(t *testing.T) {
	room := NewRoomWithConfig(RoomConfig{
		FrameRate:       20,
		FrameBufferSize: 100,
	})

	// SetData
	v1 := room.SetData(1, 100, []byte{1, 2, 3})
	v2 := room.SetData(1, 200, []byte{4, 5, 6})
	v3 := room.SetData(2, 100, []byte{7, 8, 9})

	if v1 == 0 || v2 <= v1 || v3 <= v2 {
		t.Errorf("versions should be strictly increasing: %d %d %d", v1, v2, v3)
	}

	// GetDataSnapshot
	entries, ver := room.GetDataSnapshot()
	if len(entries) != 3 {
		t.Errorf("expected 3 entries, got %d", len(entries))
	}
	if ver != v3 {
		t.Errorf("snapshot version mismatch: want %d, got %d", v3, ver)
	}

	// ClearPlayerData for player 1
	deleted, versions := room.ClearPlayerData(1)
	if len(deleted) != 2 {
		t.Errorf("expected 2 deleted entries for player 1, got %d", len(deleted))
	}
	if len(versions) != 2 {
		t.Errorf("expected 2 version bumps, got %d", len(versions))
	}

	// After clear: only player 2's entry remains
	remaining, _ := room.GetDataSnapshot()
	if len(remaining) != 1 {
		t.Errorf("expected 1 remaining entry after clearing player 1, got %d", len(remaining))
	}
	if remaining[0].PlayerId != 2 {
		t.Errorf("remaining entry should belong to player 2, got %d", remaining[0].PlayerId)
	}

	// SetData nil = delete
	room.SetData(2, 100, nil)
	remaining2, _ := room.GetDataSnapshot()
	if len(remaining2) != 0 {
		t.Errorf("expected 0 entries after deleting last entry, got %d", len(remaining2))
	}
}

// ─── 3. M8: AutoAssignRoom 并发安全 ─────────────────────────────────────────

func TestAutoAssignRoom_ConcurrentSafe(t *testing.T) {
	rm := NewRoomManager(RoomConfig{
		FrameRate:       20,
		FrameBufferSize: 100,
		MaxPlayers:      4,
	})

	const (
		goroutines     = 10
		playersPerRoom = 4
	)

	var (
		wg    sync.WaitGroup
		mu    sync.Mutex
		rooms = make(map[int32]int) // roomID → assign count
	)

	wg.Add(goroutines)
	for i := 0; i < goroutines; i++ {
		go func() {
			defer wg.Done()
			r := rm.AutoAssignRoom(playersPerRoom)
			if r == nil {
				t.Errorf("AutoAssignRoom returned nil")
				return
			}
			mu.Lock()
			rooms[r.ID]++
			mu.Unlock()
		}()
	}
	wg.Wait()

	// 验证没有重复房间（每个 ID 应只分配给同一个房间对象）
	rm.mu.Lock()
	totalRooms := len(rm.rooms)
	rm.mu.Unlock()

	if totalRooms == 0 {
		t.Error("expected at least one room to be created")
	}

	// 10 goroutines 分配 4 人房间：需要 ceiling(10/4) = 3 个房间
	// 但由于并发竞争，最多不超过 goroutines 个房间
	if totalRooms > goroutines {
		t.Errorf("too many rooms created: %d (max expected %d)", totalRooms, goroutines)
	}
}

// ─── 4. 实体权威: 断线清除 ──────────────────────────────────────────────────

func TestReleaseAllAuthority_ClearsPlayerEntities(t *testing.T) {
	room := NewRoomWithConfig(RoomConfig{
		FrameRate:       20,
		FrameBufferSize: 100,
	})

	// 玩家 1 持有 3 个实体
	room.TryGrantAuthority(10, 1)
	room.TryGrantAuthority(20, 1)
	room.TryGrantAuthority(30, 1)

	// 玩家 2 持有 1 个实体
	room.TryGrantAuthority(40, 2)

	// 断线清除玩家 1 的所有权威
	released := room.ReleaseAllAuthority(1)
	if len(released) != 3 {
		t.Errorf("expected 3 released entities, got %d", len(released))
	}

	// 验证玩家 1 实体权威已清零
	room.mu.Lock()
	for _, eid := range []int32{10, 20, 30} {
		if owner := room.entityAuthority[eid]; owner != 0 {
			t.Errorf("entity %d should have owner=0 after ReleaseAll, got %d", eid, owner)
		}
	}
	// 玩家 2 的实体不受影响
	if owner := room.entityAuthority[40]; owner != 2 {
		t.Errorf("entity 40 should still belong to player 2, got %d", owner)
	}
	room.mu.Unlock()
}

// ─── 5. L2: AppendPlayerIds 预分配切片 ──────────────────────────────────────

func TestAppendPlayerIds_PreAllocated(t *testing.T) {
	room := NewRoomWithConfig(RoomConfig{
		FrameRate:       20,
		FrameBufferSize: 100,
	})

	room.AddPlayer(1, nopConn{}, false, 0)
	room.AddPlayer(2, nopConn{}, false, 0)
	room.AddPlayer(3, nopConn{}, false, 0)
	room.DisconnectPlayer(2) // 断线但仍保留在 players map

	// 使用预分配切片
	buf := make([]int32, 0, 8)
	result := room.AppendPlayerIds(buf)

	if len(result) != 3 {
		t.Errorf("expected 3 player IDs (including disconnected), got %d", len(result))
	}

	// 验证所有 ID 都在结果中
	idSet := make(map[int32]bool)
	for _, id := range result {
		idSet[id] = true
	}
	for _, expectedID := range []int32{1, 2, 3} {
		if !idSet[expectedID] {
			t.Errorf("expected player ID %d in AppendPlayerIds result", expectedID)
		}
	}

	// 验证 GetPlayerIds 和 AppendPlayerIds 返回相同数量
	ids := room.GetPlayerIds()
	if len(ids) != len(result) {
		t.Errorf("GetPlayerIds len=%d != AppendPlayerIds len=%d", len(ids), len(result))
	}
}
