package framesync

import (
	"fmt"
	"sync"
	"sync/atomic"
)

// RoomManager 房间管理器
// 职责: 创建/查找/销毁房间，不管房间内部逻辑
type RoomManager struct {
	mu     sync.RWMutex
	rooms  map[int32]*Room
	nextID int32
	config RoomConfig
}

// NewRoomManager 创建房间管理器
func NewRoomManager(config ...RoomConfig) *RoomManager {
	cfg := DefaultRoomConfig()
	if len(config) > 0 {
		cfg = config[0]
	}
	return &RoomManager{
		rooms:  make(map[int32]*Room),
		config: cfg,
	}
}

// CreateRoom 创建新房间
func (rm *RoomManager) CreateRoom() *Room {
	id := atomic.AddInt32(&rm.nextID, 1)
	room := NewRoomWithConfig(rm.config)
	room.ID = id

	rm.mu.Lock()
	rm.rooms[id] = room
	rm.mu.Unlock()

	fmt.Printf("[RoomManager] Room %d created\n", id)
	return room
}

// GetRoom 查找房间
func (rm *RoomManager) GetRoom(id int32) *Room {
	rm.mu.RLock()
	defer rm.mu.RUnlock()
	return rm.rooms[id]
}

// RemoveRoom 移除并停止房间
func (rm *RoomManager) RemoveRoom(id int32) {
	rm.mu.Lock()
	room, ok := rm.rooms[id]
	if ok {
		delete(rm.rooms, id)
	}
	rm.mu.Unlock()

	if ok {
		room.Stop()
		fmt.Printf("[RoomManager] Room %d removed\n", id)
	}
}

// RoomCount 房间数量
func (rm *RoomManager) RoomCount() int {
	rm.mu.RLock()
	defer rm.mu.RUnlock()
	return len(rm.rooms)
}

// StopAll 停止所有房间
func (rm *RoomManager) StopAll() {
	rm.mu.Lock()
	rooms := make([]*Room, 0, len(rm.rooms))
	for _, r := range rm.rooms {
		rooms = append(rooms, r)
	}
	rm.rooms = make(map[int32]*Room)
	rm.mu.Unlock()

	for _, r := range rooms {
		r.Stop()
	}
	fmt.Printf("[RoomManager] All %d rooms stopped\n", len(rooms))
}

// AutoAssignRoom 自动分配房间（填满当前房间，满了创建新的）
// playersPerRoom: 每房间人数上限
func (rm *RoomManager) AutoAssignRoom(playersPerRoom int) *Room {
	rm.mu.Lock()
	defer rm.mu.Unlock()

	// 找一个没满的房间
	for _, r := range rm.rooms {
		if r.TotalPlayerCount() < playersPerRoom {
			return r
		}
	}

	// 没有空位，创建新房间
	rm.mu.Unlock()
	room := rm.CreateRoom()
	rm.mu.Lock()
	return room
}
