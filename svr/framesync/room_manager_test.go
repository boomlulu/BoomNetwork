package framesync

import (
	"testing"
)

func TestRoomManager_CreateAndGet(t *testing.T) {
	rm := NewRoomManager()

	room := rm.CreateRoom()
	if room == nil {
		t.Fatal("CreateRoom returned nil")
	}
	if room.ID <= 0 {
		t.Errorf("expected positive ID, got %d", room.ID)
	}

	got := rm.GetRoom(room.ID)
	if got != room {
		t.Error("GetRoom should return the same room")
	}

	if rm.RoomCount() != 1 {
		t.Errorf("expected 1 room, got %d", rm.RoomCount())
	}
}

func TestRoomManager_CreateWithMaxPlayers(t *testing.T) {
	rm := NewRoomManager()

	room := rm.CreateRoomWithMaxPlayers(4, "")
	if room.MaxPlayers() != 4 {
		t.Errorf("expected MaxPlayers=4, got %d", room.MaxPlayers())
	}
}

func TestRoomManager_RemoveRoom(t *testing.T) {
	rm := NewRoomManager()

	room := rm.CreateRoom()
	id := room.ID
	rm.RemoveRoom(id)

	if rm.GetRoom(id) != nil {
		t.Error("room should be removed")
	}
	if rm.RoomCount() != 0 {
		t.Errorf("expected 0 rooms, got %d", rm.RoomCount())
	}
}

func TestRoomManager_MatchRoom(t *testing.T) {
	rm := NewRoomManager()

	// 第一次匹配会创建新房间
	r1 := rm.MatchRoom(2, "demo1")
	if r1 == nil {
		t.Fatal("MatchRoom returned nil")
	}
	if r1.MatchKey != "demo1" {
		t.Errorf("expected MatchKey=demo1, got %q", r1.MatchKey)
	}

	// 不同 key 应创建新房间
	r2 := rm.MatchRoom(2, "demo2")
	if r2.ID == r1.ID {
		t.Error("different matchKey should create different room")
	}

	// 相同 key + maxPlayers 应复用（只要未满）
	r3 := rm.MatchRoom(2, "demo1")
	if r3.ID != r1.ID {
		t.Errorf("same matchKey should reuse room: got %d, want %d", r3.ID, r1.ID)
	}
}

func TestRoomManager_StopAll(t *testing.T) {
	rm := NewRoomManager()
	rm.CreateRoom()
	rm.CreateRoom()
	rm.CreateRoom()

	if rm.RoomCount() != 3 {
		t.Fatalf("expected 3 rooms, got %d", rm.RoomCount())
	}

	rm.StopAll()

	if rm.RoomCount() != 0 {
		t.Errorf("expected 0 rooms after StopAll, got %d", rm.RoomCount())
	}
}
