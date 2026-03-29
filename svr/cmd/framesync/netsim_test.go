package main

import (
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/boomlulu/boomnetwork/codec"
)

// mockConn records messages sent to it
type mockConn struct {
	mu   sync.Mutex
	msgs []*codec.Message
}

func (m *mockConn) Send(msg *codec.Message) error {
	m.mu.Lock()
	// Deep copy data to avoid shared buffer issues
	cp := &codec.Message{CmdType: msg.CmdType, Cmd: msg.Cmd, ExtCmd: msg.ExtCmd, GameCmd: msg.GameCmd}
	if msg.Data != nil {
		cp.Data = make([]byte, len(msg.Data))
		copy(cp.Data, msg.Data)
	}
	m.msgs = append(m.msgs, cp)
	m.mu.Unlock()
	return nil
}

func (m *mockConn) count() int {
	m.mu.Lock()
	defer m.mu.Unlock()
	return len(m.msgs)
}

func TestSimConn_Disabled_PassThrough(t *testing.T) {
	inner := &mockConn{}
	cfg := &NetSimConfig{}
	cfg.SetEnabled(false)
	sc := &simConn{inner: inner, cfg: cfg}

	msg := &codec.Message{Cmd: 1, Data: []byte{0x42}}
	if err := sc.Send(msg); err != nil {
		t.Fatal(err)
	}
	if inner.count() != 1 {
		t.Fatalf("expected 1 message, got %d", inner.count())
	}
}

func TestSimConn_Loss100_DropsAll(t *testing.T) {
	inner := &mockConn{}
	cfg := &NetSimConfig{}
	cfg.SetEnabled(true)
	atomic.StoreInt32(&cfg.LossPercent, 100)
	sc := &simConn{inner: inner, cfg: cfg}

	for i := 0; i < 50; i++ {
		sc.Send(&codec.Message{Cmd: 1})
	}
	if inner.count() != 0 {
		t.Fatalf("100%% loss should drop all, got %d", inner.count())
	}
}

func TestSimConn_Loss0_NoLatency_PassAll(t *testing.T) {
	inner := &mockConn{}
	cfg := &NetSimConfig{}
	cfg.SetEnabled(true)
	atomic.StoreInt32(&cfg.LossPercent, 0)
	atomic.StoreInt32(&cfg.LatencyMs, 0)
	sc := &simConn{inner: inner, cfg: cfg}

	for i := 0; i < 20; i++ {
		sc.Send(&codec.Message{Cmd: 1})
	}
	if inner.count() != 20 {
		t.Fatalf("0%% loss 0ms latency should pass all, got %d", inner.count())
	}
}

func TestSimConn_Delay_EventuallyDelivers(t *testing.T) {
	inner := &mockConn{}
	cfg := &NetSimConfig{}
	cfg.SetEnabled(true)
	atomic.StoreInt32(&cfg.LatencyMs, 50)
	atomic.StoreInt32(&cfg.LossPercent, 0)
	sc := &simConn{inner: inner, cfg: cfg}

	sc.Send(&codec.Message{Cmd: 1, Data: []byte{1, 2, 3}})

	// Should not be delivered immediately
	if inner.count() != 0 {
		t.Fatal("message should be delayed, not immediate")
	}

	// Wait for delivery
	time.Sleep(150 * time.Millisecond)
	if inner.count() != 1 {
		t.Fatalf("after delay, expected 1 message, got %d", inner.count())
	}
}

func TestSimConn_Delay_CopiesData(t *testing.T) {
	inner := &mockConn{}
	cfg := &NetSimConfig{}
	cfg.SetEnabled(true)
	atomic.StoreInt32(&cfg.LatencyMs, 50)
	sc := &simConn{inner: inner, cfg: cfg}

	data := []byte{0xAA, 0xBB}
	sc.Send(&codec.Message{Cmd: 1, Data: data})

	// Mutate original data — delayed message should not be affected
	data[0] = 0xFF

	time.Sleep(150 * time.Millisecond)
	inner.mu.Lock()
	if inner.msgs[0].Data[0] == 0xFF {
		t.Fatal("delayed message data was not copied — shares buffer with caller")
	}
	inner.mu.Unlock()
}

func TestSimConn_PendingMax_DegradeToImmediate(t *testing.T) {
	inner := &mockConn{}
	cfg := &NetSimConfig{}
	cfg.SetEnabled(true)
	atomic.StoreInt32(&cfg.LatencyMs, 1000)

	// Simulate pending at max
	oldPending := atomic.LoadInt64(&simPending)
	atomic.StoreInt64(&simPending, simPendingMax)
	defer atomic.StoreInt64(&simPending, oldPending)

	sc := &simConn{inner: inner, cfg: cfg}
	sc.Send(&codec.Message{Cmd: 1})

	// Should be delivered immediately (degraded) since pending is at max
	if inner.count() != 1 {
		t.Fatal("should degrade to immediate send when pending at max")
	}
}

func TestNetSimConfig_EnableDisable(t *testing.T) {
	cfg := &NetSimConfig{}
	if cfg.IsEnabled() {
		t.Fatal("should be disabled by default")
	}
	cfg.SetEnabled(true)
	if !cfg.IsEnabled() {
		t.Fatal("should be enabled after SetEnabled(true)")
	}
	cfg.SetEnabled(false)
	if cfg.IsEnabled() {
		t.Fatal("should be disabled after SetEnabled(false)")
	}
}
