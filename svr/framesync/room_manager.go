package framesync

import (
	"log/slog"
	"sync"
	"time"
)

// RoomManager 房间管理器
type RoomManager struct {
	mu         sync.Mutex
	rooms      map[int32]*Room
	matchIndex map[string][]*Room // P2-5: matchKey → []*Room 二级索引，MatchRoom 从 O(n) 降至 O(1)
	nextID     int32
	config     RoomConfig
	maxRooms   int // 0 = unlimited
}

// NewRoomManager 创建房间管理器
func NewRoomManager(config ...RoomConfig) *RoomManager {
	cfg := DefaultRoomConfig()
	if len(config) > 0 {
		cfg = config[0]
	}
	return &RoomManager{
		rooms:      make(map[int32]*Room),
		matchIndex: make(map[string][]*Room),
		config:     cfg,
	}
}

// matchIndexAddLocked 将 room 加入 matchIndex（需在持锁状态下调用）
func (rm *RoomManager) matchIndexAddLocked(room *Room) {
	rm.matchIndex[room.MatchKey] = append(rm.matchIndex[room.MatchKey], room)
}

// matchIndexRemoveLocked 将 room 从 matchIndex 移除（需在持锁状态下调用）
func (rm *RoomManager) matchIndexRemoveLocked(room *Room) {
	key := room.MatchKey
	list := rm.matchIndex[key]
	for i, r := range list {
		if r == room {
			last := len(list) - 1
			list[i] = list[last]
			list[last] = nil
			rm.matchIndex[key] = list[:last]
			if len(rm.matchIndex[key]) == 0 {
				delete(rm.matchIndex, key)
			}
			return
		}
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
	rm.matchIndexAddLocked(room)
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
		rm.matchIndexRemoveLocked(room)
	}
	rm.mu.Unlock()

	if ok {
		room.Stop()
		Metrics.RoomsCurrent.Dec()
		slog.Info("room removed", "roomId", id)
	}
}

// Snapshot 返回当前所有房间的快照切片（供 Reconciler 安全遍历，不持锁）
func (rm *RoomManager) Snapshot() []*Room {
	rm.mu.Lock()
	defer rm.mu.Unlock()
	rooms := make([]*Room, 0, len(rm.rooms))
	for _, r := range rm.rooms {
		rooms = append(rooms, r)
	}
	return rooms
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

// CreateRoomWithMaxPlayers 创建指定人数上限的房间，matchKey 为空表示不限制匹配
func (rm *RoomManager) CreateRoomWithMaxPlayers(maxPlayers int, matchKey string) *Room {
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
	room.MatchKey = matchKey
	rm.rooms[id] = room
	rm.matchIndexAddLocked(room)
	Metrics.RoomsCurrent.Inc()
	slog.Info("room created", "roomId", id, "maxPlayers", maxPlayers, "matchKey", matchKey)
	return room
}

// CleanupEmptyRooms 清理从未有玩家加入或长期空置的房间（兜底路径）
// Reconciler 负责曾有玩家后变空的房间；此方法处理：无玩家 + 未运行 + (曾有过玩家 OR 创建超过 idleTimeout)
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
	rm.mu.Unlock()

	for _, id := range toRemove {
		rm.RemoveRoom(id) // 统一走 canonical 路径（含 Stop + metrics + log）
	}
	if len(toRemove) > 0 {
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
	rm.matchIndex = make(map[string][]*Room)
	rm.mu.Unlock()

	for _, r := range rooms {
		r.Stop()
	}
	Metrics.RoomsCurrent.Sub(float64(len(rooms)))
	slog.Info("all rooms stopped", "count", len(rooms))
}

// MatchRoom 匹配房间：找一个未满且 maxPlayers + matchKey 都匹配的房间，找不到就创建（原子操作）
// P2-5: 使用 matchIndex[matchKey] 二级索引，将查找从 O(全部房间) 降至 O(同 key 房间数)。
func (rm *RoomManager) MatchRoom(maxPlayers int, matchKey string) *Room {
	rm.mu.Lock()
	defer rm.mu.Unlock()

	// 只在同 matchKey 的房间集合中查找，避免全量遍历
	for _, r := range rm.matchIndex[matchKey] {
		if r.MaxPlayers() == maxPlayers && r.TotalPlayerCount() < maxPlayers {
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
	rm.matchIndexAddLocked(room)
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
