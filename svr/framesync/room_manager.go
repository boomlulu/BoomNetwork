package framesync

import (
	"fmt"
	"sync"
	"sync/atomic"
)

// RoomManager 房间管理器
type RoomManager struct {
	mu     sync.Mutex
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

// CreateRoom 创建新房间（不加锁，调用方负责）
func (rm *RoomManager) createRoomLocked() *Room {
	id := atomic.AddInt32(&rm.nextID, 1)
	room := NewRoomWithConfig(rm.config)
	room.ID = id
	rm.rooms[id] = room
	fmt.Printf("[RoomManager] Room %d created\n", id)
	return room
}

// CreateRoom 创建新房间（公开版，自带锁）
func (rm *RoomManager) CreateRoom() *Room {
	rm.mu.Lock()
	defer rm.mu.Unlock()
	return rm.createRoomLocked()
}

// GetRoom 查找房间
func (rm *RoomManager) GetRoom(id int32) *Room {
	rm.mu.Lock()
	defer rm.mu.Unlock()
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
	rm.mu.Lock()
	defer rm.mu.Unlock()
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

// AutoAssignRoom 自动分配房间（原子操作，无竞态）
func (rm *RoomManager) AutoAssignRoom(playersPerRoom int) *Room {
	rm.mu.Lock()
	defer rm.mu.Unlock()

	for _, r := range rm.rooms {
		if r.PlayerCount() < playersPerRoom {
			return r
		}
	}

	// 在锁内创建，不会有两个请求各创建一个房间
	return rm.createRoomLocked()
}
