package framesync

// room_manager_p2_test.go — P2-5 MatchRoom 二级索引单元测试 + Benchmark
//
// 覆盖范围:
//   P2-5  matchIndex 增删一致性 + MatchRoom O(1) 路径验证

import (
	"fmt"
	"testing"
)

// ─── P2-5: matchIndex 一致性 ──────────────────────────────────────────────────

// TestMatchIndex_AddOnCreate 创建房间时索引应包含该房间
func TestMatchIndex_AddOnCreate(t *testing.T) {
	rm := NewRoomManager()
	r := rm.CreateRoomWithMaxPlayers(4, "arena")

	rm.mu.Lock()
	list := rm.matchIndex["arena"]
	rm.mu.Unlock()

	if len(list) != 1 || list[0] != r {
		t.Errorf("matchIndex should contain the new room after create, got %v", list)
	}
}

// TestMatchIndex_RemovedOnDelete 删除房间时索引应移除该条目
func TestMatchIndex_RemovedOnDelete(t *testing.T) {
	rm := NewRoomManager()
	r := rm.CreateRoomWithMaxPlayers(4, "arena")
	rm.RemoveRoom(r.ID)

	rm.mu.Lock()
	list := rm.matchIndex["arena"]
	rm.mu.Unlock()

	if len(list) != 0 {
		t.Errorf("matchIndex should be empty after remove, got %d entries", len(list))
	}
}

// TestMatchIndex_MultipleRoomsSameKey 同一 matchKey 多个房间共存
func TestMatchIndex_MultipleRoomsSameKey(t *testing.T) {
	rm := NewRoomManager()
	r1 := rm.CreateRoomWithMaxPlayers(4, "lobby")
	r2 := rm.CreateRoomWithMaxPlayers(4, "lobby")
	r3 := rm.CreateRoomWithMaxPlayers(4, "lobby")

	rm.mu.Lock()
	list := rm.matchIndex["lobby"]
	rm.mu.Unlock()

	if len(list) != 3 {
		t.Fatalf("expected 3 rooms in index, got %d", len(list))
	}

	// 删除中间那个，验证不影响其他两个
	rm.RemoveRoom(r2.ID)

	rm.mu.Lock()
	list = rm.matchIndex["lobby"]
	rm.mu.Unlock()

	if len(list) != 2 {
		t.Fatalf("after removing r2: expected 2 rooms, got %d", len(list))
	}
	for _, r := range list {
		if r == r2 {
			t.Error("r2 should have been removed from index")
		}
	}
	_ = r1
	_ = r3
}

// TestMatchIndex_DifferentKeys 不同 matchKey 的房间分别索引，不互相干扰
func TestMatchIndex_DifferentKeys(t *testing.T) {
	rm := NewRoomManager()
	rm.CreateRoomWithMaxPlayers(4, "key-A")
	rm.CreateRoomWithMaxPlayers(4, "key-A")
	rm.CreateRoomWithMaxPlayers(4, "key-B")

	rm.mu.Lock()
	countA := len(rm.matchIndex["key-A"])
	countB := len(rm.matchIndex["key-B"])
	rm.mu.Unlock()

	if countA != 2 {
		t.Errorf("key-A: expected 2, got %d", countA)
	}
	if countB != 1 {
		t.Errorf("key-B: expected 1, got %d", countB)
	}
}

// TestMatchIndex_StopAllClearsIndex StopAll 后索引应清空
func TestMatchIndex_StopAllClearsIndex(t *testing.T) {
	rm := NewRoomManager()
	for i := 0; i < 5; i++ {
		rm.CreateRoomWithMaxPlayers(4, "game")
	}
	rm.StopAll()

	rm.mu.Lock()
	indexLen := len(rm.matchIndex)
	rm.mu.Unlock()

	if indexLen != 0 {
		t.Errorf("matchIndex should be empty after StopAll, got %d keys", indexLen)
	}
}

// TestMatchRoom_ReusesExistingRoom 同 key 有空位时复用已有房间
func TestMatchRoom_ReusesExistingRoom(t *testing.T) {
	rm := NewRoomManager()
	r1 := rm.MatchRoom(4, "queue")
	if r1 == nil {
		t.Fatal("first MatchRoom should create room")
	}

	r2 := rm.MatchRoom(4, "queue")
	if r2.ID != r1.ID {
		t.Errorf("second MatchRoom should reuse same room: got %d, want %d", r2.ID, r1.ID)
	}
}

// TestMatchRoom_CreatesNewWhenFull 房间满时创建新房间
func TestMatchRoom_CreatesNewWhenFull(t *testing.T) {
	rm := NewRoomManager()
	r1 := rm.MatchRoom(2, "ranked")

	// 手动加满玩家
	r1.AddPlayer(1, nopConn{})
	r1.AddPlayer(2, nopConn{})

	r2 := rm.MatchRoom(2, "ranked")
	if r2 == nil {
		t.Fatal("MatchRoom should create new room when existing is full")
	}
	if r2.ID == r1.ID {
		t.Error("should create new room when existing is full")
	}
}

// TestMatchRoom_DifferentKeysDontMix 不同 matchKey 不会混搭
func TestMatchRoom_DifferentKeysDontMix(t *testing.T) {
	rm := NewRoomManager()
	rA := rm.MatchRoom(4, "alpha")
	rB := rm.MatchRoom(4, "beta")

	if rA.ID == rB.ID {
		t.Error("different matchKeys should not match to the same room")
	}

	// 再匹配 alpha 应复用 rA
	rA2 := rm.MatchRoom(4, "alpha")
	if rA2.ID != rA.ID {
		t.Error("second alpha match should reuse rA")
	}
}

// TestMatchRoom_IndexConsistency_AfterMatchCreate MatchRoom 创建的房间也进入索引
func TestMatchRoom_IndexConsistency_AfterMatchCreate(t *testing.T) {
	rm := NewRoomManager()
	r := rm.MatchRoom(4, "new-key")

	rm.mu.Lock()
	list := rm.matchIndex["new-key"]
	rm.mu.Unlock()

	if len(list) != 1 || list[0] != r {
		t.Error("MatchRoom-created room should be in matchIndex")
	}
}

// ─── Benchmarks ──────────────────────────────────────────────────────────────

// BenchmarkMatchRoom_IndexPath 1000 个不同 key 的房间，匹配指定 key（O(1) 索引路径）
func BenchmarkMatchRoom_IndexPath(b *testing.B) {
	rm := NewRoomManager()
	const numRooms = 1000

	// 先创建大量房间（不同 key）
	for i := 0; i < numRooms; i++ {
		rm.CreateRoomWithMaxPlayers(4, fmt.Sprintf("key-%d", i))
	}

	b.ReportAllocs()
	b.ResetTimer()

	// 匹配一个存在的 key（索引直接命中，无需遍历全部 1000 个房间）
	for i := 0; i < b.N; i++ {
		_ = rm.MatchRoom(4, fmt.Sprintf("key-%d", i%numRooms))
	}
}

// BenchmarkMatchRoom_SameKey 同一 key 下多个房间，验证索引内扫描开销
func BenchmarkMatchRoom_SameKey(b *testing.B) {
	rm := NewRoomManager()
	const perKey = 50

	// 同 key 50 个满员房间（每次 MatchRoom 都要扫完才新建）
	for i := 0; i < perKey; i++ {
		r := rm.CreateRoomWithMaxPlayers(4, "hot-key")
		for j := int32(1); j <= 4; j++ {
			r.AddPlayer(int32(i*4)+j, nopConn{})
		}
	}

	b.ReportAllocs()
	b.ResetTimer()

	for i := 0; i < b.N; i++ {
		_ = rm.MatchRoom(4, "hot-key")
	}
}
