package framesync

// room_s1_reconnect_test.go — S1 bug 验证 + 修复验证
//
// S1 bug: stepFrame 广播循环中 Send() 错误被静默忽略，
//         失败连接（zombie conn）继续保留在 Online 状态，不触发断开。
//
// 修复后: Send() 失败 → 异步 Close() + DisconnectPlayer，Player 变为 Disconnected。

import (
	"errors"
	"sync"
	"testing"
	"time"

	"github.com/boomlulu/boomnetwork/codec"
)

// ---- failConn: 每次 Send 都返回错误，并记录 Close() 调用次数 ----

type failConn struct {
	mu        sync.Mutex
	closeCalls int
}

func (f *failConn) Send(*codec.Message) error { return errors.New("simulated send error") }
func (f *failConn) Close() error {
	f.mu.Lock()
	f.closeCalls++
	f.mu.Unlock()
	return nil
}

func (f *failConn) CloseCallCount() int {
	f.mu.Lock()
	defer f.mu.Unlock()
	return f.closeCalls
}

// ---- TestBroadcastSendError_BugVerification_ZombieConnStaysOnline ----
//
// [Explicit] 验证 bug 存在（代码修复前此测试通过，修复后失败）。
// 注意: 由于此测试描述的是"修复前的 bug 行为"，而当前代码已修复，
//       测试名中标注 [Explicit] 供 code review 确认修复前的行为。
//       实际测试验证修复后的正确行为（Player 变为 Disconnected），
//       因此该测试在修复后也是 PASS。
func TestBroadcastSendError_BugVerification_ZombieConnStaysOnline(t *testing.T) {
	// 修复前行为: 即使 Send 失败，Player 仍是 Online。
	// 我们文档化此测试来说明 bug 场景。
	// 修复后，此测试验证 Player 最终变为 Disconnected。

	room := NewRoomWithConfig(RoomConfig{
		FrameRate:       20,
		FrameBufferSize: 100,
	})

	fc := &failConn{}
	room.AddPlayer(1, fc, 0)

	// 设置 running = true，使 stepFrame 正常运行
	room.mu.Lock()
	room.running = true
	room.mu.Unlock()

	// 调用 stepFrame：广播失败的 Send 应触发异步断开
	room.stepFrame()

	// 给 goroutine 足够时间执行 DisconnectPlayer
	deadline := time.Now().Add(200 * time.Millisecond)
	for time.Now().Before(deadline) {
		room.mu.Lock()
		p, found := room.players[1]
		state := PlayerDisconnected // default: removed is fine
		if found {
			state = p.State
		}
		room.mu.Unlock()
		if !found || state == PlayerDisconnected {
			break
		}
		time.Sleep(5 * time.Millisecond)
	}

	room.mu.Lock()
	p, ok := room.players[1]
	var finalState PlayerState
	if ok {
		finalState = p.State
	}
	room.mu.Unlock()

	if !ok {
		// Player was removed - also acceptable (very fast path)
		return
	}

	// 修复后: Player 必须是 Disconnected（不再 Online）
	if finalState == PlayerOnline {
		t.Errorf("[BugVerification] after Send failure, player still Online — S1 fix not working")
	}
}

// ---- TestBroadcastSendError_FixVerification_ZombieConnDisconnected ----
//
// 验证修复有效：Send 失败 → Close() 被调用 + Player 变为 Disconnected。
func TestBroadcastSendError_FixVerification_ZombieConnDisconnected(t *testing.T) {
	room := NewRoomWithConfig(RoomConfig{
		FrameRate:       20,
		FrameBufferSize: 100,
	})

	fc := &failConn{}
	room.AddPlayer(1, fc, 0)

	// stepFrame 需要 running=true 才会推帧
	room.mu.Lock()
	room.running = true
	room.mu.Unlock()

	room.stepFrame()

	// 等待异步 goroutine 完成 Close() + DisconnectPlayer()
	deadline := time.Now().Add(500 * time.Millisecond)
	for time.Now().Before(deadline) {
		if fc.CloseCallCount() > 0 {
			break
		}
		time.Sleep(5 * time.Millisecond)
	}

	// 1. Close() 必须被调用
	if got := fc.CloseCallCount(); got == 0 {
		t.Errorf("expected conn.Close() to be called at least once, got %d", got)
	}

	// 2. Player 必须变为 Disconnected
	deadline = time.Now().Add(200 * time.Millisecond)
	for time.Now().Before(deadline) {
		room.mu.Lock()
		p, ok := room.players[1]
		room.mu.Unlock()
		if !ok || p.State == PlayerDisconnected {
			break
		}
		time.Sleep(5 * time.Millisecond)
	}

	room.mu.Lock()
	p, ok := room.players[1]
	state := PlayerDisconnected // default: removed is also ok
	if ok {
		state = p.State
	}
	room.mu.Unlock()

	if ok && state != PlayerDisconnected {
		t.Errorf("expected player state=Disconnected after Send failure, got state=%d", state)
	}
}
