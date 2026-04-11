package main

import (
	"testing"

	"github.com/boomlulu/boomnetwork/transport"
)

// newConn 返回一个带有指定 ID 的 *transport.Conn 占位对象（仅用于指针比较，不调用任何网络方法）
func newConn(id int) *transport.Conn {
	c := &transport.Conn{}
	c.ID = id
	return c
}

func TestBind_ByConn_Returns_Correct(t *testing.T) {
	s := newSessionStore()
	conn := newConn(10)

	s.Bind(10, 42, conn)

	pid, room, ok := s.ByConn(10)
	if !ok {
		t.Fatal("ByConn: expected ok=true")
	}
	if pid != 42 {
		t.Errorf("ByConn: expected playerId=42, got %d", pid)
	}
	if room != nil {
		t.Errorf("ByConn: expected room=nil after Bind (no room yet), got %v", room)
	}

	_, connOut, ok2 := s.ByPlayer(42)
	if !ok2 {
		t.Fatal("ByPlayer: expected ok=true after Bind")
	}
	if connOut != conn {
		t.Errorf("ByPlayer: expected conn=%p, got %p", conn, connOut)
	}
}

func TestBindToRoom_Consistent(t *testing.T) {
	s := newSessionStore()
	conn := newConn(20)
	// room=nil is fine for a unit test; we only test the map pointers
	s.BindToRoom(20, 99, conn, nil)

	pid, room, ok := s.ByConn(20)
	if !ok {
		t.Fatal("ByConn: expected ok=true after BindToRoom")
	}
	if pid != 99 {
		t.Errorf("ByConn: expected playerId=99, got %d", pid)
	}
	if room != nil {
		t.Error("expected room=nil (we passed nil)")
	}

	roomOut, connOut, ok2 := s.ByPlayer(99)
	if !ok2 {
		t.Fatal("ByPlayer: expected ok=true after BindToRoom")
	}
	if roomOut != nil {
		t.Error("expected room=nil from ByPlayer")
	}
	if connOut != conn {
		t.Errorf("ByPlayer: expected conn=%p, got %p", conn, connOut)
	}
}

func TestReconnect_ReturnsOldConn(t *testing.T) {
	s := newSessionStore()
	oldConn := newConn(1)
	newConn := newConn(2)

	// 初始绑定
	s.BindToRoom(1, 7, oldConn, nil)

	// 重连
	oldConnID, returnedOldConn := s.Reconnect(2, 7, newConn, nil)

	if oldConnID != 1 {
		t.Errorf("Reconnect: expected oldConnID=1, got %d", oldConnID)
	}
	if returnedOldConn != oldConn {
		t.Errorf("Reconnect: expected returnedOldConn=%p, got %p", oldConn, returnedOldConn)
	}

	// 旧 connID 应已从 byConn 中删除
	_, _, ok := s.ByConn(1)
	if ok {
		t.Error("Reconnect: old connID should have been removed from byConn")
	}

	// 新 connID 应在 byConn 中
	pid, _, ok2 := s.ByConn(2)
	if !ok2 {
		t.Fatal("Reconnect: new connID should be in byConn")
	}
	if pid != 7 {
		t.Errorf("Reconnect: expected playerId=7 under new connID, got %d", pid)
	}

	// byPlayer 应指向新 entry
	_, connOut, ok3 := s.ByPlayer(7)
	if !ok3 {
		t.Fatal("Reconnect: byPlayer[7] should still exist")
	}
	if connOut != newConn {
		t.Errorf("Reconnect: byPlayer should point to newConn=%p, got %p", newConn, connOut)
	}
}

// TestDisconnect_AfterReconnect_ShouldCleanupFalse 是最关键的测试：
// 旧连接断开时，byConn[oldConnID] 已被 Reconnect 删除，Disconnect 应返回 shouldCleanup=false，
// 并且 byPlayer[playerId] 应仍然存在（指向新连接）。
func TestDisconnect_AfterReconnect_ShouldCleanupFalse(t *testing.T) {
	s := newSessionStore()
	oldConn := newConn(100)
	newConn := newConn(200)

	// 初始绑定
	s.BindToRoom(100, 55, oldConn, nil)

	// 玩家重连（新 connID=200）
	s.Reconnect(200, 55, newConn, nil)

	// 旧连接的 onDisconnect 触发（byConn[100] 已经不存在了）
	pid, _, shouldCleanup := s.Disconnect(100)

	if shouldCleanup {
		t.Errorf("Disconnect after reconnect: expected shouldCleanup=false, got true (pid=%d)", pid)
	}

	// 关键：byPlayer[55] 必须仍然存在，指向新连接
	_, connOut, ok := s.ByPlayer(55)
	if !ok {
		t.Fatal("Disconnect after reconnect: byPlayer[55] should still exist")
	}
	if connOut != newConn {
		t.Errorf("Disconnect after reconnect: byPlayer[55] should point to newConn=%p, got %p", newConn, connOut)
	}
}

func TestDisconnect_Normal_ShouldCleanupTrue(t *testing.T) {
	s := newSessionStore()
	conn := newConn(300)

	s.BindToRoom(300, 88, conn, nil)

	pid, _, shouldCleanup := s.Disconnect(300)

	if pid != 88 {
		t.Errorf("Disconnect normal: expected playerId=88, got %d", pid)
	}
	if !shouldCleanup {
		t.Error("Disconnect normal: expected shouldCleanup=true")
	}

	// byConn 和 byPlayer 都应已删除
	_, _, ok1 := s.ByConn(300)
	if ok1 {
		t.Error("Disconnect normal: byConn[300] should be gone")
	}
	_, _, ok2 := s.ByPlayer(88)
	if ok2 {
		t.Error("Disconnect normal: byPlayer[88] should be gone")
	}
}

func TestRangeConns_AllVisited(t *testing.T) {
	s := newSessionStore()
	c1 := newConn(1)
	c2 := newConn(2)
	c3 := newConn(3)

	s.Bind(1, 1, c1)
	s.Bind(2, 2, c2)
	s.Bind(3, 3, c3)

	visited := make(map[*transport.Conn]bool)
	s.RangeConns(func(conn *transport.Conn) {
		visited[conn] = true
	})

	if len(visited) != 3 {
		t.Errorf("RangeConns: expected 3 conns visited, got %d", len(visited))
	}
	for _, c := range []*transport.Conn{c1, c2, c3} {
		if !visited[c] {
			t.Errorf("RangeConns: conn %p not visited", c)
		}
	}
}
