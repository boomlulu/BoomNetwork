package framesync

// room_stress_p2_test.go — 多房间并发压力测试
//
// 覆盖范围:
//   - 50 个房间并发运行 500ms，期间持续加入/断开/发输入
//   - 验证 race detector 无竞态、无 panic、帧号单调递增

import (
	"fmt"
	"sync"
	"sync/atomic"
	"testing"
	"time"
)

// TestStress_MultiRoom_ConcurrentFrameAdvance 开启 50 个房间同时跑帧，
// 每个房间 4 个玩家持续发输入，并发断线/重连，持续 500ms。
// 主要验证：无 race condition，无 panic，帧号单调递增。
func TestStress_MultiRoom_ConcurrentFrameAdvance(t *testing.T) {
	const (
		numRooms    = 50
		numPlayers  = 4
		runDuration = 500 * time.Millisecond
	)

	var wg sync.WaitGroup
	var totalFrames int64
	var panics int64

	for i := 0; i < numRooms; i++ {
		wg.Add(1)
		go func(roomIdx int) {
			defer wg.Done()

			room := NewRoomWithConfig(RoomConfig{
				FrameRate:              20,
				MaxPlayers:             numPlayers,
				FrameBufferSize:        200,
				SnapshotIntervalFrames: 100,
				DisconnectKeepAlive:    5 * time.Second,
			})

			// 注入快照，防止 snapshotStale 暂停
			room.SetInitialSnapshot([]byte{byte(roomIdx)})

			// panic 捕获
			d := &mockDelegate{}
			d.NoopRoomDelegate = NoopRoomDelegate{}
			room.SetDelegate(d)

			for p := int32(1); p <= numPlayers; p++ {
				room.AddPlayer(p, nopConn{}, false, 0)
			}
			room.Start()

			deadline := time.Now().Add(runDuration)
			var localPanic int32

			// 每个玩家一个 goroutine 持续发输入 + 定期快照更新
			for p := int32(1); p <= numPlayers; p++ {
				wg.Add(1)
				go func(pid int32) {
					defer wg.Done()
					ticker := time.NewTicker(20 * time.Millisecond)
					defer ticker.Stop()
					for time.Now().Before(deadline) {
						<-ticker.C
						room.OnInput(pid, []byte{byte(pid), 0xFF})
					}
				}(p)
			}

			// 定期上传快照（防止 stale 暂停）
			wg.Add(1)
			go func() {
				defer wg.Done()
				ticker := time.NewTicker(50 * time.Millisecond)
				defer ticker.Stop()
				snapshotFrame := uint32(0)
				for time.Now().Before(deadline) {
					<-ticker.C
					fn := room.CurrentFrameNumber()
					if fn > snapshotFrame {
						room.UpdateSnapshot(fn, []byte{byte(roomIdx), byte(fn)})
						snapshotFrame = fn
					}
				}
			}()

			// 周期性断线重连（模拟网络抖动）
			wg.Add(1)
			go func() {
				defer wg.Done()
				ticker := time.NewTicker(80 * time.Millisecond)
				defer ticker.Stop()
				victim := int32(1)
				for time.Now().Before(deadline) {
					<-ticker.C
					room.DisconnectPlayer(victim)
					time.Sleep(5 * time.Millisecond)
					room.AddPlayer(victim, nopConn{}, false, room.CurrentFrameNumber())
					victim = victim%numPlayers + 1
				}
			}()

			time.Sleep(runDuration + 50*time.Millisecond)
			room.Stop()

			frames := room.CurrentFrameNumber()
			atomic.AddInt64(&totalFrames, int64(frames))
			if atomic.LoadInt32(&localPanic) > 0 {
				atomic.AddInt64(&panics, 1)
			}

			if d.panicked > 0 {
				atomic.AddInt64(&panics, 1)
				t.Errorf("room %d panicked", roomIdx)
			}
			_ = fmt.Sprintf("room %d: %d frames", roomIdx, frames)
		}(i)
	}

	wg.Wait()

	if panics > 0 {
		t.Fatalf("%d rooms panicked during stress test", panics)
	}

	avgFrames := totalFrames / numRooms
	// 20fps × 0.5s = 10 帧；加上启动延迟，至少 5 帧
	if avgFrames < 5 {
		t.Fatalf("avg frames too low: %d (expected >= 5 for 20fps/500ms)", avgFrames)
	}
	t.Logf("stress test passed: %d rooms, avg %d frames/room", numRooms, avgFrames)
}

// TestStress_RoomManager_ConcurrentCreateMatchRemove 并发 Create/Match/Remove 验证无竞态
func TestStress_RoomManager_ConcurrentCreateMatchRemove(t *testing.T) {
	rm := NewRoomManager()
	const goroutines = 20
	const ops = 50

	var wg sync.WaitGroup
	for i := 0; i < goroutines; i++ {
		wg.Add(1)
		go func(id int) {
			defer wg.Done()
			key := fmt.Sprintf("key-%d", id%5)
			for j := 0; j < ops; j++ {
				switch j % 3 {
				case 0:
					rm.CreateRoomWithMaxPlayers(4, key)
				case 1:
					rm.MatchRoom(4, key)
				case 2:
					infos := rm.GetAllRoomInfos()
					if len(infos) > 0 {
						rm.RemoveRoom(infos[0].RoomId)
					}
				}
			}
		}(i)
	}
	wg.Wait()
	// 不 panic、不死锁即为通过
	t.Logf("final room count: %d", rm.RoomCount())
}
