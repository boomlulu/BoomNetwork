package main

import (
	"sync"

	"github.com/boomlulu/boomnetwork/framesync"
	"github.com/boomlulu/boomnetwork/transport"
)

// sessionEntry 是单条会话的所有数据（替代 4 个 sync.Map 中的分散信息）
type sessionEntry struct {
	connID   int
	playerId int32
	room     *framesync.Room
	conn     *transport.Conn
}

// SessionStore 统一管理连接→玩家→房间的会话映射
// 替代原来的 connPlayerMap / playerRoomMap / playerConnMap / connContextMap 四个 sync.Map，
// 提供类型安全 + 批量原子操作（单次加锁完成多字段联动更新）。
type SessionStore struct {
	mu       sync.RWMutex
	byConn   map[int]*sessionEntry  // connID → entry（热路径 ByConn）
	byPlayer map[int32]*sessionEntry // playerId → entry
}

func newSessionStore() *SessionStore {
	return &SessionStore{
		byConn:   make(map[int]*sessionEntry),
		byPlayer: make(map[int32]*sessionEntry),
	}
}

// Bind 记录 SessionBind 时建立的 connID↔playerId↔conn 映射（room=nil，稍后 BindToRoom 补充）。
func (s *SessionStore) Bind(connID int, playerId int32, conn *transport.Conn) {
	entry := &sessionEntry{
		connID:   connID,
		playerId: playerId,
		conn:     conn,
	}
	s.mu.Lock()
	s.byConn[connID] = entry
	s.byPlayer[playerId] = entry
	s.mu.Unlock()
}

// BindToRoom 一次原子完成 connID + playerId + room + conn 的联动写入。
// 替代原来分散在 bindMapsToRoom 中的 4 个 sync.Map.Store 调用。
func (s *SessionStore) BindToRoom(connID int, playerId int32, conn *transport.Conn, room *framesync.Room) {
	entry := &sessionEntry{
		connID:   connID,
		playerId: playerId,
		room:     room,
		conn:     conn,
	}
	s.mu.Lock()
	s.byConn[connID] = entry
	s.byPlayer[playerId] = entry
	s.mu.Unlock()
}

// Reconnect 在一次锁内原子完成重连映射更新：
//  1. 找旧 entry（byPlayer[playerId]），保存 oldConnID / oldConn
//  2. 删 byConn[oldConnID]
//  3. 写入新 entry 到 byConn[newConnID] 和 byPlayer[playerId]
//
// 返回旧连接（调用方在锁外 Close），如果找不到旧 entry 则 oldConn=nil。
func (s *SessionStore) Reconnect(newConnID int, playerId int32, newConn *transport.Conn, room *framesync.Room) (oldConnID int, oldConn *transport.Conn) {
	newEntry := &sessionEntry{
		connID:   newConnID,
		playerId: playerId,
		room:     room,
		conn:     newConn,
	}
	s.mu.Lock()
	if old, ok := s.byPlayer[playerId]; ok {
		oldConnID = old.connID
		if old.conn != newConn {
			oldConn = old.conn
		}
		delete(s.byConn, oldConnID)
	}
	s.byConn[newConnID] = newEntry
	s.byPlayer[playerId] = newEntry
	s.mu.Unlock()
	return
}

// Disconnect 处理连接断开：
//   - 删 byConn[connID]（不存在时返回 shouldCleanup=false，代表已被 Reconnect 处理）
//   - CAS 检查 byPlayer[playerId].connID == connID 时才删 byPlayer，防止重连覆盖
//
// 返回 playerId, room, shouldCleanup。
// shouldCleanup=false 时调用方跳过 room.DisconnectPlayer 等清理逻辑。
func (s *SessionStore) Disconnect(connID int) (playerId int32, room *framesync.Room, shouldCleanup bool) {
	s.mu.Lock()
	entry, ok := s.byConn[connID]
	if !ok {
		s.mu.Unlock()
		return 0, nil, false
	}
	delete(s.byConn, connID)
	playerId = entry.playerId
	room = entry.room

	// CAS：只有当 byPlayer 仍指向同一个 entry（即相同 connID）时才删除
	if cur, ok2 := s.byPlayer[playerId]; ok2 && cur.connID == connID {
		delete(s.byPlayer, playerId)
		shouldCleanup = true
	}
	s.mu.Unlock()
	return
}

// ByConn 热路径读操作：从 byConn[connID] 取 playerId 和 room。
func (s *SessionStore) ByConn(connID int) (playerId int32, room *framesync.Room, ok bool) {
	s.mu.RLock()
	entry, found := s.byConn[connID]
	s.mu.RUnlock()
	if !found {
		return 0, nil, false
	}
	return entry.playerId, entry.room, true
}

// ByPlayer 读操作：从 byPlayer[playerId] 取 room 和 conn。
func (s *SessionStore) ByPlayer(playerId int32) (room *framesync.Room, conn *transport.Conn, ok bool) {
	s.mu.RLock()
	entry, found := s.byPlayer[playerId]
	s.mu.RUnlock()
	if !found {
		return nil, nil, false
	}
	return entry.room, entry.conn, true
}

// DeleteByConn 仅删 byConn[connID]，不动 byPlayer。
// 用于 handleFrameInput 中 IsAlive() 失败时的局部清理。
func (s *SessionStore) DeleteByConn(connID int) {
	s.mu.Lock()
	delete(s.byConn, connID)
	s.mu.Unlock()
}

// DeleteByPlayer 仅删 byPlayer[playerId]，不动 byConn。
// 用于 OnPlayerRemoved（Reconciler 驱逐）。
func (s *SessionStore) DeleteByPlayer(playerId int32) {
	s.mu.Lock()
	delete(s.byPlayer, playerId)
	s.mu.Unlock()
}

// RangeConns 读操作，遍历 byConn，对每条 entry 调用 fn(entry.conn)。
// fn 内不得调用 SessionStore 的写方法（否则死锁）。
func (s *SessionStore) RangeConns(fn func(conn *transport.Conn)) {
	s.mu.RLock()
	// 先收集快照，再在锁外执行 fn，避免 fn 内部阻塞持锁
	conns := make([]*transport.Conn, 0, len(s.byConn))
	for _, e := range s.byConn {
		if e.conn != nil {
			conns = append(conns, e.conn)
		}
	}
	s.mu.RUnlock()
	for _, c := range conns {
		fn(c)
	}
}

// ConnByPlayer 读操作，返回 byPlayer[playerId] 的 conn。
func (s *SessionStore) ConnByPlayer(playerId int32) (*transport.Conn, bool) {
	s.mu.RLock()
	entry, ok := s.byPlayer[playerId]
	s.mu.RUnlock()
	if !ok {
		return nil, false
	}
	return entry.conn, true
}
