package transport

import (
	"fmt"
	"net"
	"testing"
	"time"
)

// ===================== RateLimiter =====================

func TestRateLimiter_UnderLimit_ReturnsOK(t *testing.T) {
	rl := NewRateLimiter(100)
	for i := 0; i < 80; i++ {
		if lv := rl.AllowLevel(); lv != RateLevelOK {
			t.Fatalf("call %d: expected OK, got %v", i, lv)
		}
	}
}

func TestRateLimiter_WarnAt80Percent(t *testing.T) {
	rl := NewRateLimiter(100)
	// Burn through 80 calls (all OK)
	for i := 0; i < 80; i++ {
		rl.AllowLevel()
	}
	// 81st call should be Warn (>80%)
	if lv := rl.AllowLevel(); lv != RateLevelWarn {
		t.Fatalf("expected Warn at 81%%, got %v", lv)
	}
}

func TestRateLimiter_WarnOncePerSecond(t *testing.T) {
	rl := NewRateLimiter(100)
	for i := 0; i < 80; i++ {
		rl.AllowLevel()
	}
	// First warn
	lv := rl.AllowLevel()
	if lv != RateLevelWarn {
		t.Fatalf("expected first Warn, got %v", lv)
	}
	// Subsequent calls within same second should be OK (warn throttled)
	for i := 82; i <= 100; i++ {
		lv = rl.AllowLevel()
		if lv == RateLevelWarn {
			t.Fatalf("call %d: got Warn again within same second", i)
		}
	}
}

func TestRateLimiter_DenyOverLimit(t *testing.T) {
	rl := NewRateLimiter(10)
	for i := 0; i < 10; i++ {
		rl.AllowLevel()
	}
	// 11th call exceeds limit
	if lv := rl.AllowLevel(); lv != RateLevelDeny {
		t.Fatalf("expected Deny, got %v", lv)
	}
}

func TestRateLimiter_Allow_ReturnsFalseOnDeny(t *testing.T) {
	rl := NewRateLimiter(1)
	if !rl.Allow() {
		t.Fatal("first call should be allowed")
	}
	if rl.Allow() {
		t.Fatal("second call should be denied")
	}
}

func TestRateLimiter_WindowReset(t *testing.T) {
	rl := NewRateLimiter(5)
	// Exhaust limit
	for i := 0; i < 5; i++ {
		rl.AllowLevel()
	}
	if rl.AllowLevel() != RateLevelDeny {
		t.Fatal("should be denied after exhausting limit")
	}

	// Wait for a new second window (atomic CAS 自动按 unix 秒滚动，无需手动重置)
	time.Sleep(1100 * time.Millisecond)

	// Should reset and allow again
	if lv := rl.AllowLevel(); lv != RateLevelOK {
		t.Fatalf("after window reset, expected OK, got %v", lv)
	}
}

// ===================== IPRateLimiter =====================

type fakeAddr struct {
	s string
}

func (a fakeAddr) Network() string { return "tcp" }
func (a fakeAddr) String() string  { return a.s }

func TestIPRateLimiter_Disabled(t *testing.T) {
	l := NewIPRateLimiter(0)
	addr := fakeAddr{"1.2.3.4:5678"}
	for i := 0; i < 1000; i++ {
		if !l.Allow(addr) {
			t.Fatal("should always allow when maxPerSec=0")
		}
	}
}

func TestIPRateLimiter_AllowThenDeny(t *testing.T) {
	l := NewIPRateLimiter(3)
	addr := fakeAddr{"10.0.0.1:9000"}
	for i := 0; i < 3; i++ {
		if !l.Allow(addr) {
			t.Fatalf("call %d should be allowed", i)
		}
	}
	if l.Allow(addr) {
		t.Fatal("4th call should be denied")
	}
}

func TestIPRateLimiter_PerIPIsolation(t *testing.T) {
	l := NewIPRateLimiter(2)
	a1 := fakeAddr{"10.0.0.1:9000"}
	a2 := fakeAddr{"10.0.0.2:9000"}
	// Exhaust IP 1
	l.Allow(a1)
	l.Allow(a1)
	if l.Allow(a1) {
		t.Fatal("IP1 should be denied")
	}
	// IP 2 should still be allowed
	if !l.Allow(a2) {
		t.Fatal("IP2 should be allowed")
	}
}

func TestIPRateLimiter_BareAddress(t *testing.T) {
	// net.SplitHostPort fails on bare IP → fallback to raw string
	l := NewIPRateLimiter(1)
	addr := fakeAddr{"127.0.0.1"} // no port
	if !l.Allow(addr) {
		t.Fatal("first call should be allowed")
	}
	if l.Allow(addr) {
		t.Fatal("second call should be denied")
	}
}

func TestIPRateLimiter_IPv6(t *testing.T) {
	l := NewIPRateLimiter(1)
	addr := fakeAddr{net.JoinHostPort("::1", "9000")}
	if !l.Allow(addr) {
		t.Fatal("first call should be allowed")
	}
	if l.Allow(addr) {
		t.Fatal("second call should be denied for same IPv6")
	}
}

func TestIPRateLimiter_DifferentPorts_SameIP(t *testing.T) {
	l := NewIPRateLimiter(1)
	a1 := fakeAddr{"192.168.1.1:1111"}
	a2 := fakeAddr{"192.168.1.1:2222"} // same IP, different port
	l.Allow(a1)
	if l.Allow(a2) {
		t.Fatal("same IP different port should share rate limit")
	}
}

func TestIPRateLimiter_Concurrent(t *testing.T) {
	l := NewIPRateLimiter(1000)
	done := make(chan struct{})
	for g := 0; g < 10; g++ {
		go func(id int) {
			defer func() { done <- struct{}{} }()
			addr := fakeAddr{fmt.Sprintf("10.0.%d.1:9000", id)}
			for i := 0; i < 100; i++ {
				l.Allow(addr)
			}
		}(g)
	}
	for g := 0; g < 10; g++ {
		<-done
	}
}

// H1 benchmark: RateLimiter atomic CAS vs. previous sync.Mutex
func BenchmarkRateLimiter_Allow(b *testing.B) {
	rl := NewRateLimiter(1000)
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		rl.Allow()
	}
}

func BenchmarkRateLimiter_Allow_Parallel(b *testing.B) {
	rl := NewRateLimiter(1000)
	b.ResetTimer()
	b.RunParallel(func(pb *testing.PB) {
		for pb.Next() {
			rl.Allow()
		}
	})
}

func TestIPRateLimiterCleanup(t *testing.T) {
	l := NewIPRateLimiter(100)
	maxAge := 50 * time.Millisecond

	// Create 100 different IP entries
	for i := 0; i < 100; i++ {
		ip := fmt.Sprintf("10.0.%d.%d:1234", i/256, i%256)
		l.Allow(fakeAddr{ip})
	}

	// Wait for entries to expire
	time.Sleep(maxAge + 20*time.Millisecond)

	// Cleanup should remove all 100 entries
	deleted := l.Cleanup(maxAge)
	if deleted != 100 {
		t.Errorf("expected 100 deleted, got %d", deleted)
	}

	// After cleanup, Allow creates a fresh entry (count resets to 1, not cumulative)
	allowed := l.Allow(fakeAddr{"10.0.0.0:1234"})
	if !allowed {
		t.Error("after cleanup, first Allow should succeed (fresh entry)")
	}
}

func TestIPRateLimiterCleanupKeepsActive(t *testing.T) {
	l := NewIPRateLimiter(1000)
	maxAge := 80 * time.Millisecond

	// Create 100 entries
	for i := 0; i < 100; i++ {
		ip := fmt.Sprintf("192.168.%d.%d:9000", i/256, i%256)
		l.Allow(fakeAddr{ip})
	}

	// Wait half the maxAge
	time.Sleep(maxAge / 2)

	// Refresh 10 entries (update their lastSeen)
	for i := 0; i < 10; i++ {
		ip := fmt.Sprintf("192.168.%d.%d:9000", i/256, i%256)
		l.Allow(fakeAddr{ip})
	}

	// Wait until original entries expire (but refreshed ones are still young)
	time.Sleep(maxAge/2 + 20*time.Millisecond)

	deleted := l.Cleanup(maxAge)
	if deleted != 90 {
		t.Errorf("expected 90 deleted (100-10 active), got %d", deleted)
	}
}
