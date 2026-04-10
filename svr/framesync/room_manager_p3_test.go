package framesync

// room_manager_p3_test.go — RoomManager 盲区补测
//
// 覆盖范围:
//   GetAllRoomInfos    — 空/多房间快照
//   CleanupEmptyRooms  — 超时驱逐 + 曾有玩家即清 + 跳过活跃房间
//   SetMaxRooms        — 限额创建拒绝

import (
	"testing"
	"time"
)

// ─── GetAllRoomInfos ──────────────────────────────────────────────────────────

func TestGetAllRoomInfos_Empty(t *testing.T) {
	rm := NewRoomManager()
	infos := rm.GetAllRoomInfos()
	if len(infos) != 0 {
		t.Fatalf("want 0 infos, got %d", len(infos))
	}
}

func TestGetAllRoomInfos_MultipleRooms(t *testing.T) {
	rm := NewRoomManager()
	rm.CreateRoom()
	rm.CreateRoom()
	rm.CreateRoom()

	infos := rm.GetAllRoomInfos()
	if len(infos) != 3 {
		t.Fatalf("want 3 infos, got %d", len(infos))
	}
}

func TestGetAllRoomInfos_RoomIds_Unique(t *testing.T) {
	rm := NewRoomManager()
	for i := 0; i < 5; i++ {
		rm.CreateRoom()
	}
	infos := rm.GetAllRoomInfos()
	seen := map[int32]bool{}
	for _, info := range infos {
		if seen[info.RoomId] {
			t.Fatalf("duplicate RoomId %d in GetAllRoomInfos", info.RoomId)
		}
		seen[info.RoomId] = true
	}
}

// ─── CleanupEmptyRooms ────────────────────────────────────────────────────────

func TestCleanupEmptyRooms_NeverHadPlayer_WithinTimeout_NotRemoved(t *testing.T) {
	rm := NewRoomManager()
	rm.CreateRoom()

	// idle timeout 1h — 刚创建的房间未超时
	n := rm.CleanupEmptyRooms(1 * time.Hour)
	if n != 0 {
		t.Fatalf("want 0 removed, got %d", n)
	}
	if rm.RoomCount() != 1 {
		t.Fatalf("room should still exist, count=%d", rm.RoomCount())
	}
}

func TestCleanupEmptyRooms_NeverHadPlayer_AfterTimeout_Removed(t *testing.T) {
	rm := NewRoomManager()
	rm.CreateRoom()

	// idle timeout=0 → 任何房间都已超时
	n := rm.CleanupEmptyRooms(0)
	if n != 1 {
		t.Fatalf("want 1 removed, got %d", n)
	}
	if rm.RoomCount() != 0 {
		t.Fatalf("room should be removed, count=%d", rm.RoomCount())
	}
}

func TestCleanupEmptyRooms_HadPlayer_RemovedImmediately(t *testing.T) {
	rm := NewRoomManager()
	room := rm.CreateRoom()
	room.AddPlayer(1, nopConn{}, 0)
	room.RemovePlayer(1) // 曾有玩家，现在为空

	// idle timeout 1h — 但 HadPlayer=true，应立即清理
	n := rm.CleanupEmptyRooms(1 * time.Hour)
	if n != 1 {
		t.Fatalf("want 1 removed (had player), got %d", n)
	}
	if rm.RoomCount() != 0 {
		t.Fatalf("room should be removed, count=%d", rm.RoomCount())
	}
}

func TestCleanupEmptyRooms_RunningRoom_NotRemoved(t *testing.T) {
	rm := NewRoomManager()
	room := rm.CreateRoom()
	room.Start() // 运行中的房间不应被清理
	defer room.Stop()

	n := rm.CleanupEmptyRooms(0)
	if n != 0 {
		t.Fatalf("running room should not be removed, got %d", n)
	}
	if rm.RoomCount() != 1 {
		t.Fatalf("running room should remain, count=%d", rm.RoomCount())
	}
}

func TestCleanupEmptyRooms_MultipleRooms_OnlyRemovesEligible(t *testing.T) {
	rm := NewRoomManager()

	// r1: 曾有玩家 → 应清理
	r1 := rm.CreateRoom()
	r1.AddPlayer(1, nopConn{}, 0)
	r1.RemovePlayer(1)

	// r2: 正在运行 → 不清理
	r2 := rm.CreateRoom()
	r2.Start()
	defer r2.Stop()

	// r3: 从未有玩家 + 未超时 → 不清理
	rm.CreateRoom()

	n := rm.CleanupEmptyRooms(1 * time.Hour)
	if n != 1 {
		t.Fatalf("want 1 removed, got %d", n)
	}
	if rm.RoomCount() != 2 {
		t.Fatalf("want 2 rooms remaining, got %d", rm.RoomCount())
	}
}

// ─── SetMaxRooms ──────────────────────────────────────────────────────────────

func TestSetMaxRooms_RejectsCreationWhenFull(t *testing.T) {
	rm := NewRoomManager()
	rm.SetMaxRooms(2)

	r1 := rm.CreateRoom()
	r2 := rm.CreateRoom()
	r3 := rm.CreateRoom() // 超过上限

	if r1 == nil || r2 == nil {
		t.Fatal("first two rooms should be created successfully")
	}
	if r3 != nil {
		t.Fatal("third room should be rejected (max=2)")
	}
	if rm.RoomCount() != 2 {
		t.Fatalf("want 2 rooms, got %d", rm.RoomCount())
	}
}

func TestSetMaxRooms_ZeroMeansUnlimited(t *testing.T) {
	rm := NewRoomManager()
	rm.SetMaxRooms(1)
	rm.SetMaxRooms(0) // 0 = 无限制

	for i := 0; i < 10; i++ {
		r := rm.CreateRoom()
		if r == nil {
			t.Fatalf("room %d should be created with unlimited max", i+1)
		}
	}
}

func TestSetMaxRooms_CreateRoomWithMaxPlayers_Rejected(t *testing.T) {
	rm := NewRoomManager()
	rm.SetMaxRooms(1)

	rm.CreateRoomWithMaxPlayers(4, "key")
	r := rm.CreateRoomWithMaxPlayers(4, "key") // 超过上限
	if r != nil {
		t.Fatal("CreateRoomWithMaxPlayers should be rejected when at max")
	}
}
