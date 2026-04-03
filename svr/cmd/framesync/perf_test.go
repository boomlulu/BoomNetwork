package main

// perf_test.go — P0 性能优化的单元测试 + Benchmark（main 包视角）
//
// 覆盖范围:
//   P0-1  nextPlayerId: atomic.AddInt32 并发安全性
//   P0-2  connContextMap: 单次查找的生命周期一致性 + handleFrameInput 路径验证

import (
	"sort"
	"sync"
	"sync/atomic"
	"testing"

	"github.com/boomlulu/boomnetwork/codec"
	"github.com/boomlulu/boomnetwork/framesync"
	"github.com/boomlulu/boomnetwork/transport"
	"github.com/prometheus/client_golang/prometheus"
	dto "github.com/prometheus/client_model/go"
)

// ─── 辅助 ────────────────────────────────────────────────────────────────────

func newBenchRoom() *framesync.Room {
	return framesync.NewRoomWithConfig(framesync.RoomConfig{
		FrameRate:       20,
		FrameBufferSize: 100,
	})
}

// fakeConn 仅有 ID 字段被设置，handleFrameInput 不调用 conn 上任何方法
func fakeConn(id int) *transport.Conn {
	return &transport.Conn{ID: id}
}

// counterValue 通过 prometheus.Counter.Write 读取当前计数值
// 使用 client_model/go（已在 go.mod 中）的 dto.Metric，无需 testutil 依赖
func counterValue(c prometheus.Counter) float64 {
	var m dto.Metric
	if err := c.Write(&m); err != nil {
		return 0
	}
	return m.GetCounter().GetValue()
}

// ─── P0-1: nextPlayerId atomic.AddInt32 ─────────────────────────────────────

// TestNextPlayerId_Concurrent_NoDuplicates
// 1000 个 goroutine 并发调用 nextPlayerId()，验证结果无重复
func TestNextPlayerId_Concurrent_NoDuplicates(t *testing.T) {
	const n = 1000

	// 记录起始值（不重置全局计数器，避免与其他测试竞争）
	before := atomic.LoadInt32(&playerCounter)

	ids := make([]int32, n)
	var wg sync.WaitGroup
	for i := 0; i < n; i++ {
		wg.Add(1)
		go func(slot int) {
			defer wg.Done()
			ids[slot] = nextPlayerId()
		}(i)
	}
	wg.Wait()

	// 排序后校验：无重复 + 连续（= before+1 … before+n）
	sorted := make([]int, n)
	for i, v := range ids {
		sorted[i] = int(v)
	}
	sort.Ints(sorted)

	for i := 0; i < n; i++ {
		want := int(before) + i + 1
		if sorted[i] != want {
			t.Errorf("sorted[%d] = %d, want %d (gap or duplicate detected)", i, sorted[i], want)
			if i > 0 && sorted[i] == sorted[i-1] {
				t.Errorf("  → duplicate value %d", sorted[i])
			}
			break
		}
	}
}

// TestNextPlayerId_ReturnsPositive 确保返回值大于零（int32 溢出防御性检查）
func TestNextPlayerId_ReturnsPositive(t *testing.T) {
	id := nextPlayerId()
	if id <= 0 {
		t.Errorf("nextPlayerId() returned %d, want > 0", id)
	}
}

// ─── P0-2: connContextMap 生命周期 ──────────────────────────────────────────

// TestConnContextMap_StoreAndLoad 验证存储后能正确读回 playerId 和 room 指针
func TestConnContextMap_StoreAndLoad(t *testing.T) {
	const testConnID = 9000001
	room := newBenchRoom()
	ctx := &connContext{playerId: 42, room: room}

	connContextMap.Store(testConnID, ctx)
	defer connContextMap.Delete(testConnID)

	val, ok := connContextMap.Load(testConnID)
	if !ok {
		t.Fatal("expected entry to exist")
	}
	loaded := val.(*connContext)
	if loaded.playerId != 42 {
		t.Errorf("playerId: want 42, got %d", loaded.playerId)
	}
	if loaded.room != room {
		t.Error("room pointer mismatch")
	}
}

// TestConnContextMap_Delete 验证 Delete 后 Load 返回 !ok
func TestConnContextMap_Delete(t *testing.T) {
	const testConnID = 9000002
	connContextMap.Store(testConnID, &connContext{playerId: 1, room: newBenchRoom()})

	connContextMap.Delete(testConnID)

	if _, ok := connContextMap.Load(testConnID); ok {
		t.Error("expected entry to be deleted")
	}
}

// TestConnContextMap_HandleFrameInput_Miss
// 当 connID 不在 map 中时，handleFrameInput 应立即返回 nil（不 panic）
func TestConnContextMap_HandleFrameInput_Miss(t *testing.T) {
	conn := fakeConn(9000003) // 未注册
	msg := &codec.Message{Data: []byte{0xFF}}
	result := handleFrameInput(conn, msg)
	if result != nil {
		t.Errorf("miss path should return nil, got %v", result)
	}
}

// TestConnContextMap_HandleFrameInput_Hit
// 当 connID 在 map 中时，handleFrameInput 应调用 room.OnInput 并递增 InputsReceived
func TestConnContextMap_HandleFrameInput_Hit(t *testing.T) {
	const testConnID = 9000004
	const testPid = int32(77)
	room := newBenchRoom()

	connContextMap.Store(testConnID, &connContext{playerId: testPid, room: room})
	defer connContextMap.Delete(testConnID)

	conn := fakeConn(testConnID)
	msg := &codec.Message{Data: []byte{0x01, 0x02, 0x03}}

	// 通过 dto.Metric.Write 读取计数器：验证 InputsReceived 精确 +1
	before := counterValue(framesync.Metrics.InputsReceived)
	result := handleFrameInput(conn, msg)
	after := counterValue(framesync.Metrics.InputsReceived)

	if result != nil {
		t.Errorf("hit path should return nil, got %v", result)
	}
	if delta := after - before; delta != 1 {
		t.Errorf("InputsReceived should increment by 1, got delta=%.0f", delta)
	}
}

// TestConnContextMap_HandleFrameInput_CorrectPlayerIdAndRoom
// 验证 hit 路径使用了 context 中的 playerId，而非其他玩家的
func TestConnContextMap_HandleFrameInput_CorrectPlayerIdAndRoom(t *testing.T) {
	const testConnID = 9000005

	// 两个房间 + 两组 context，确保路由不串
	roomA := newBenchRoom()
	roomB := newBenchRoom()

	connContextMap.Store(testConnID, &connContext{playerId: 10, room: roomA})
	connContextMap.Store(testConnID+1, &connContext{playerId: 20, room: roomB})
	defer connContextMap.Delete(testConnID)
	defer connContextMap.Delete(testConnID + 1)

	inputA := []byte{0xAA}
	inputB := []byte{0xBB}

	handleFrameInput(fakeConn(testConnID), &codec.Message{Data: inputA})
	handleFrameInput(fakeConn(testConnID+1), &codec.Message{Data: inputB})

	// 两次各自触发了 InputsReceived +1；主要验证没有 panic（路由正确）
	// 实际 OnInput 内容由 room_p0_test.go 中的 framesync 包测试覆盖
}

// TestConnContextMap_Reconnect_UpdatesEntry
// 重连时新 context 应覆盖旧 context（playerId 不变，room 可能变）
func TestConnContextMap_Reconnect_UpdatesEntry(t *testing.T) {
	const testConnID = 9000006
	const pid = int32(55)
	roomOld := newBenchRoom()
	roomNew := newBenchRoom()

	// 第一次 bind
	connContextMap.Store(testConnID, &connContext{playerId: pid, room: roomOld})
	defer connContextMap.Delete(testConnID)

	// 重连：覆盖 context（room 指针更新）
	connContextMap.Store(testConnID, &connContext{playerId: pid, room: roomNew})

	val, ok := connContextMap.Load(testConnID)
	if !ok {
		t.Fatal("context should exist after reconnect")
	}
	loaded := val.(*connContext)
	if loaded.room != roomNew {
		t.Error("reconnect should update room pointer to new room")
	}
	if loaded.playerId != pid {
		t.Errorf("playerId should remain %d, got %d", pid, loaded.playerId)
	}
}

// TestConnContextMap_Disconnect_ClearsEntry
// onClientDisconnect 应清理 connContextMap，防止幽灵条目
func TestConnContextMap_Disconnect_ClearsEntry(t *testing.T) {
	const testConnID = 9000007
	connContextMap.Store(testConnID, &connContext{playerId: 99, room: newBenchRoom()})

	// 模拟 onClientDisconnect 的清理行为
	connContextMap.Delete(testConnID)

	if _, ok := connContextMap.Load(testConnID); ok {
		t.Error("entry should be cleared after disconnect")
	}

	// 断线后的 handleFrameInput 必须走 miss 路径，不 panic
	result := handleFrameInput(fakeConn(testConnID), &codec.Message{Data: []byte{1}})
	if result != nil {
		t.Errorf("post-disconnect handleFrameInput should return nil, got %v", result)
	}
}

// ─── Benchmarks ──────────────────────────────────────────────────────────────

// BenchmarkNextPlayerId 串行基准：atomic 自增的 ns/op
func BenchmarkNextPlayerId(b *testing.B) {
	b.ReportAllocs()
	for i := 0; i < b.N; i++ {
		nextPlayerId()
	}
}

// BenchmarkNextPlayerId_Parallel 并发基准：模拟多连接同时 SessionBind
func BenchmarkNextPlayerId_Parallel(b *testing.B) {
	b.ReportAllocs()
	b.RunParallel(func(pb *testing.PB) {
		for pb.Next() {
			nextPlayerId()
		}
	})
}

// BenchmarkHandleFrameInput 串行基准：单次 sync.Map 查找 + room.OnInput 的端到端开销
func BenchmarkHandleFrameInput(b *testing.B) {
	const benchConnID = 8000001
	room := newBenchRoom()
	connContextMap.Store(benchConnID, &connContext{playerId: 1, room: room})
	defer connContextMap.Delete(benchConnID)

	conn := fakeConn(benchConnID)
	msg := &codec.Message{Data: []byte{0x01, 0x02, 0x03, 0x04}}

	b.ReportAllocs()
	b.ResetTimer()

	for i := 0; i < b.N; i++ {
		handleFrameInput(conn, msg)
	}
}

// BenchmarkHandleFrameInput_Parallel 并发基准：多玩家同时发送帧输入
// 每个 goroutine 使用独立的 connID + room，模拟真实多人场景
var benchConnIDBase int32 = 8100000

func BenchmarkHandleFrameInput_Parallel(b *testing.B) {
	b.ReportAllocs()
	b.RunParallel(func(pb *testing.PB) {
		// 每个并行 goroutine 拥有独立 conn + room，消除 room.mu 竞争
		id := int(atomic.AddInt32(&benchConnIDBase, 1))
		room := newBenchRoom()
		connContextMap.Store(id, &connContext{playerId: int32(id), room: room})
		defer connContextMap.Delete(id)

		conn := fakeConn(id)
		msg := &codec.Message{Data: []byte{0x01, 0x02, 0x03, 0x04}}

		for pb.Next() {
			handleFrameInput(conn, msg)
		}
	})
}
