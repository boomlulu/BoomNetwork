package transport

// C1 TDD 测试：Conn.Send 写超时保护
//
// ┌─────────────────────────────────────────────────────────────────────┐
// │  BUG (C1): WriteTimeout 字段在 ServerConfig 中已定义，但从未在      │
// │  Conn.Send() 中调用 SetWriteDeadline，导致慢客户端无限阻塞广播循环   │
// │                                                                     │
// │  TDD 流程:                                                          │
// │  Phase 1 — BUG 验证:                                               │
// │    TestConnSend_BugVerification_SlowClientBlocks                    │
// │      → 修复前后均 PASS (无 writeTimeout 时 Send 永远阻塞，           │
// │        backward-compatible 行为保留)                                │
// │                                                                     │
// │  Phase 2 — 修复验证:                                               │
// │    TestConnSend_FixVerification_WithTimeoutReturnsError             │
// │      修复前: FAIL (writeTimeout 字段不存在或 SetWriteDeadline 未调用)│
// │      修复后: PASS (Send 在 writeTimeout 内返回 error)               │
// └─────────────────────────────────────────────────────────────────────┘

import (
	"net"
	"testing"
	"time"

	"github.com/boomlulu/boomnetwork/codec"
)

// newBlockingConn 创建一个 net.Pipe 对。
// pw (写端) 一旦 Flush() 就会阻塞，因为 pr (读端) 从不 Read。
// net.Pipe() 无内部缓冲区，Write 同步阻塞直到对端 Read。
// bufio.FrameWriter 的 Flush() 最终调用 pw.Write()，因此也阻塞。
func newBlockingPipe(t *testing.T) (pr, pw net.Conn) {
	t.Helper()
	pr, pw = net.Pipe()
	t.Cleanup(func() {
		pr.Close()
		pw.Close()
	})
	return
}

// ══════════════════════════════════════════════════════════════
// Phase 1: BUG 验证
// ══════════════════════════════════════════════════════════════

// TestConnSend_BugVerification_SlowClientBlocks 证明 bug 存在。
//
// 当 Conn 没有 writeTimeout（零值），Send() 在慢客户端下无限阻塞。
// 这是 C1 bug 的核心行为。
//
// 预期: PASS（修复前后均成立）
//   - 修复前: 无 writeTimeout → Send 阻塞 → 200ms select 触发 → PASS
//   - 修复后: 无 writeTimeout → Send 仍阻塞（向后兼容）→ PASS
func TestConnSend_BugVerification_SlowClientBlocks(t *testing.T) {
	pr, pw := newBlockingPipe(t)
	_ = pr // 故意不读，模拟慢/死亡客户端

	conn := &Conn{
		ID:     1,
		conn:   pw,
		writer: codec.NewFrameWriter(pw),
		// writeTimeout: 0 (零值 = 不设置写超时，重现 bug 场景)
	}

	msg := codec.NewCoreMessage(8 /*Heartbeat，任意 Core Cmd*/, make([]byte, 64))

	done := make(chan error, 1)
	go func() { done <- conn.Send(msg) }()

	select {
	case err := <-done:
		// 如果 Send 立刻返回，说明 bufio 有内部缓冲吸收了写入
		// 这不应该发生（net.Pipe 无缓冲）
		t.Logf("UNEXPECTED: Send returned immediately (err=%v)", err)
		t.Fatal("BUG VERIFICATION FAILED: Send should block with no writeTimeout and a non-reading client")
	case <-time.After(200 * time.Millisecond):
		// 200ms 内未返回 → BUG CONFIRMED
		t.Log("BUG VERIFIED ✓ Conn.Send blocks indefinitely with no writeTimeout (slow client)")
		t.Log("  → 慢客户端拖死整个广播循环，所有同房玩家帧延迟")
	}
	// t.Cleanup 关闭 pr/pw，解除阻塞的 goroutine，避免 goroutine 泄漏
}

// ══════════════════════════════════════════════════════════════
// Phase 2: 修复验证
// ══════════════════════════════════════════════════════════════

// TestConnSend_FixVerification_WithTimeoutReturnsError 证明修复有效。
//
// 预期:
//   修复前: FAIL（writeTimeout 字段不存在，或存在但 Send 不使用它）
//   修复后: PASS（Send 在 writeTimeout 内返回超时 error）
func TestConnSend_FixVerification_WithTimeoutReturnsError(t *testing.T) {
	pr, pw := newBlockingPipe(t)
	_ = pr // 故意不读

	const writeTimeout = 150 * time.Millisecond
	conn := &Conn{
		ID:           1,
		conn:         pw,
		writer:       codec.NewFrameWriter(pw),
		writeTimeout: writeTimeout, // C1 修复：设置写超时
	}

	msg := codec.NewCoreMessage(8, make([]byte, 64))

	start := time.Now()
	err := conn.Send(msg) // 修复后：在 writeTimeout 内返回
	elapsed := time.Since(start)

	if err == nil {
		t.Fatal("FIX VERIFICATION FAILED: Send should return error when write times out (net.Pipe reader not reading)")
	}

	// 允许 3× 超时的误差（调度抖动）
	maxAllowed := writeTimeout * 3
	if elapsed > maxAllowed {
		t.Errorf("FIX VERIFICATION FAILED: Send took %v, expected < %v", elapsed, maxAllowed)
	}

	t.Logf("FIX VERIFIED ✓ Send returned error in %v (timeout=%v): %v", elapsed, writeTimeout, err)
}

// TestConnSend_FixVerification_FastClientSucceeds 保证快速客户端不受超时影响。
//
// 修复不应影响正常（读端活跃）的连接。
func TestConnSend_FixVerification_FastClientSucceeds(t *testing.T) {
	pr, pw := net.Pipe()
	defer pr.Close()
	defer pw.Close()

	// 读端活跃 goroutine，模拟正常客户端
	go func() {
		buf := make([]byte, 4096)
		for {
			if _, err := pr.Read(buf); err != nil {
				return
			}
		}
	}()

	conn := &Conn{
		ID:           1,
		conn:         pw,
		writer:       codec.NewFrameWriter(pw),
		writeTimeout: 500 * time.Millisecond, // 宽裕的超时
	}

	msg := codec.NewCoreMessage(8, make([]byte, 64))

	start := time.Now()
	err := conn.Send(msg)
	elapsed := time.Since(start)

	if err != nil {
		t.Errorf("FIX VERIFICATION: Send to fast client should succeed, got error: %v", err)
	}
	if elapsed > 50*time.Millisecond {
		t.Errorf("FIX VERIFICATION: Send to fast client took %v, expected < 50ms", elapsed)
	}
	t.Logf("FIX VERIFIED ✓ Send to fast client succeeded in %v (writeTimeout does not affect normal sends)", elapsed)
}

// TestConnSend_FixVerification_ZeroTimeoutPreservesBlockingBehavior 验证向后兼容。
//
// 当 writeTimeout=0 时，行为与修复前完全相同（不设置截止时间）。
// 这保证了已有的不配置超时的代码路径不受影响。
func TestConnSend_FixVerification_ZeroTimeoutPreservesBlockingBehavior(t *testing.T) {
	pr, pw := newBlockingPipe(t)
	_ = pr

	conn := &Conn{
		ID:           1,
		conn:         pw,
		writer:       codec.NewFrameWriter(pw),
		writeTimeout: 0, // 明确设置为 0：不应设置截止时间
	}

	msg := codec.NewCoreMessage(8, make([]byte, 64))

	done := make(chan error, 1)
	go func() { done <- conn.Send(msg) }()

	select {
	case <-done:
		t.Fatal("BACKWARD COMPAT BROKEN: writeTimeout=0 should NOT set deadline; Send should still block")
	case <-time.After(200 * time.Millisecond):
		t.Log("BACKWARD COMPAT ✓ writeTimeout=0 preserves blocking behavior (no deadline set)")
	}
}

// ══════════════════════════════════════════════════════════════
// Benchmarks
// ══════════════════════════════════════════════════════════════

// BenchmarkConnSend_FastClient_NoTimeout 基准：快速客户端无超时配置（基线）
func BenchmarkConnSend_FastClient_NoTimeout(b *testing.B) {
	pr, pw := net.Pipe()
	defer pr.Close()
	defer pw.Close()

	go func() {
		buf := make([]byte, 65536)
		for {
			if _, err := pr.Read(buf); err != nil {
				return
			}
		}
	}()

	conn := &Conn{
		ID:           1,
		conn:         pw,
		writer:       codec.NewFrameWriter(pw),
		writeTimeout: 0, // 无超时（基线）
	}
	msg := codec.NewCoreMessage(7, make([]byte, 64))

	b.ResetTimer()
	b.ReportAllocs()
	for i := 0; i < b.N; i++ {
		if err := conn.Send(msg); err != nil {
			b.Fatalf("Send error: %v", err)
		}
	}
}

// BenchmarkConnSend_FastClient_WithTimeout 基准：快速客户端有超时配置（修复后）
// 验证 SetWriteDeadline 对正常发送路径的额外开销
func BenchmarkConnSend_FastClient_WithTimeout(b *testing.B) {
	pr, pw := net.Pipe()
	defer pr.Close()
	defer pw.Close()

	go func() {
		buf := make([]byte, 65536)
		for {
			if _, err := pr.Read(buf); err != nil {
				return
			}
		}
	}()

	conn := &Conn{
		ID:           1,
		conn:         pw,
		writer:       codec.NewFrameWriter(pw),
		writeTimeout: 10 * time.Second, // 充裕的超时（不会触发）
	}
	msg := codec.NewCoreMessage(7, make([]byte, 64))

	b.ResetTimer()
	b.ReportAllocs()
	for i := 0; i < b.N; i++ {
		if err := conn.Send(msg); err != nil {
			b.Fatalf("Send error: %v", err)
		}
	}
}
