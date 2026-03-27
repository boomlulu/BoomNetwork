package framesync

import (
	"log/slog"
	"sync"
	"time"
)

// RoomManager 房间管理器
type RoomManager struct {
	mu       sync.Mutex
	rooms    map[int32]*Room
	nextID   int32
	config   RoomConfig
	maxRooms int // 0 = unlimited
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

// SetMaxRooms 设置最大房间数（0 = 不限制）
func (rm *RoomManager) SetMaxRooms(n int) {
	rm.mu.Lock()
	defer rm.mu.Unlock()
	rm.maxRooms = n
}

// createRoomLocked 创建新房间（不加锁，调用方负责）
func (rm *RoomManager) createRoomLocked() *Room {
	if rm.maxRooms > 0 && len(rm.rooms) >= rm.maxRooms {
		slog.Warn("room creation rejected: max rooms reached", "maxRooms", rm.maxRooms)
		return nil
	}
	rm.nextID++
	id := rm.nextID
	room := NewRoomWithConfig(rm.config)
	room.ID = id
	rm.rooms[id] = room
	Metrics.RoomsCurrent.Inc()
	slog.Info("room created", "roomId", id)
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
		Metrics.RoomsCurrent.Dec()
		slog.Info("room removed", "roomId", id)
	}
}

// RoomCount 房间数量
func (rm *RoomManager) RoomCount() int {
	rm.mu.Lock()
	defer rm.mu.Unlock()
	return len(rm.rooms)
}

// GetAllRoomInfos 获取所有房间信息
func (rm *RoomManager) GetAllRoomInfos() []RoomInfo {
	rm.mu.Lock()
	rooms := make([]*Room, 0, len(rm.rooms))
	for _, r := range rm.rooms {
		rooms = append(rooms, r)
	}
	rm.mu.Unlock()

	infos := make([]RoomInfo, len(rooms))
	for i, r := range rooms {
		infos[i] = r.GetRoomInfo()
	}
	return infos
}

// CreateRoomWithMaxPlayers 创建指定人数上限的房间
func (rm *RoomManager) CreateRoomWithMaxPlayers(maxPlayers int) *Room {
	cfg := rm.config
	cfg.MaxPlayers = maxPlayers
	rm.mu.Lock()
	defer rm.mu.Unlock()
	if rm.maxRooms > 0 && len(rm.rooms) >= rm.maxRooms {
		slog.Warn("room creation rejected: max rooms reached", "maxRooms", rm.maxRooms)
		return nil
	}
	rm.nextID++
	id := rm.nextID
	room := NewRoomWithConfig(cfg)
	room.ID = id
	rm.rooms[id] = room
	Metrics.RoomsCurrent.Inc()
	slog.Info("room created", "roomId", id, "maxPlayers", maxPlayers)
	return room
}

// CleanupEmptyRooms 清理空闲房间
// 清理条件：无玩家 + 未运行 + (曾有过玩家 OR 创建超过 idleTimeout)
func (rm *RoomManager) CleanupEmptyRooms(idleTimeout time.Duration) int {
	now := time.Now()
	rm.mu.Lock()
	var toRemove []int32
	for id, r := range rm.rooms {
		if r.TotalPlayerCount() == 0 && !r.IsRunning() {
			if r.HadPlayer() || now.Sub(r.CreatedAt()) >= idleTimeout {
				toRemove = append(toRemove, id)
			}
		}
	}
	for _, id := range toRemove {
		delete(rm.rooms, id)
	}
	rm.mu.Unlock()

	if len(toRemove) > 0 {
		Metrics.RoomsCurrent.Sub(float64(len(toRemove)))
		slog.Info("cleaned up empty rooms", "count", len(toRemove), "roomIds", toRemove)
	}
	return len(toRemove)
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
	Metrics.RoomsCurrent.Sub(float64(len(rooms)))
	slog.Info("all rooms stopped", "count", len(rooms))
}

// MatchRoom 匹配房间：找一个未满且 maxPlayers + matchKey 都匹配的房间，找不到就创建（原子操作）
func (rm *RoomManager) MatchRoom(maxPlayers int, matchKey string) *Room {
	rm.mu.Lock()
	defer rm.mu.Unlock()

	for _, r := range rm.rooms {
		if r.MatchKey == matchKey && r.MaxPlayers() == maxPlayers && r.TotalPlayerCount() < maxPlayers {
			return r
		}
	}

	// 没有匹配的房间，创建新的
	if rm.maxRooms > 0 && len(rm.rooms) >= rm.maxRooms {
		slog.Warn("room creation rejected: max rooms reached", "maxRooms", rm.maxRooms)
		return nil
	}
	cfg := rm.config
	cfg.MaxPlayers = maxPlayers
	rm.nextID++
	id := rm.nextID
	room := NewRoomWithConfig(cfg)
	room.ID = id
	room.MatchKey = matchKey
	rm.rooms[id] = room
	Metrics.RoomsCurrent.Inc()
	slog.Info("room created by match", "roomId", id, "maxPlayers", maxPlayers, "matchKey", matchKey)
	return room
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
