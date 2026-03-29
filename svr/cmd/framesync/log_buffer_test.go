package main

import (
	"testing"
	"time"
)

func TestLogRingBuffer_AddAndRecent(t *testing.T) {
	b := NewLogRingBuffer(10)
	b.Add(LogEntry{Ts: 1, Level: "INFO", Msg: "hello"})
	b.Add(LogEntry{Ts: 2, Level: "WARN", Msg: "world"})

	entries := b.Recent(10, "")
	if len(entries) != 2 {
		t.Fatalf("expected 2, got %d", len(entries))
	}
	// Most recent first
	if entries[0].Msg != "world" || entries[1].Msg != "hello" {
		t.Fatalf("wrong order: %v", entries)
	}
}

func TestLogRingBuffer_Overflow(t *testing.T) {
	b := NewLogRingBuffer(3)
	for i := 0; i < 5; i++ {
		b.Add(LogEntry{Ts: int64(i), Level: "INFO", Msg: string(rune('A' + i))})
	}
	entries := b.Recent(10, "")
	if len(entries) != 3 {
		t.Fatalf("expected 3 (cap), got %d", len(entries))
	}
	// Should have C(2), D(3), E(4) — most recent first: E, D, C
	if entries[0].Msg != "E" || entries[1].Msg != "D" || entries[2].Msg != "C" {
		t.Fatalf("expected [E D C], got [%s %s %s]", entries[0].Msg, entries[1].Msg, entries[2].Msg)
	}
}

func TestLogRingBuffer_RecentLimitN(t *testing.T) {
	b := NewLogRingBuffer(100)
	for i := 0; i < 50; i++ {
		b.Add(LogEntry{Ts: int64(i), Level: "INFO", Msg: "msg"})
	}
	entries := b.Recent(5, "")
	if len(entries) != 5 {
		t.Fatalf("expected 5, got %d", len(entries))
	}
}

func TestLogRingBuffer_LevelFilter(t *testing.T) {
	b := NewLogRingBuffer(10)
	b.Add(LogEntry{Level: "DEBUG", Msg: "debug"})
	b.Add(LogEntry{Level: "INFO", Msg: "info"})
	b.Add(LogEntry{Level: "WARN", Msg: "warn"})
	b.Add(LogEntry{Level: "ERROR", Msg: "error"})

	// Filter WARN and above
	entries := b.Recent(10, "WARN")
	if len(entries) != 2 {
		t.Fatalf("expected 2 (WARN+ERROR), got %d", len(entries))
	}
	if entries[0].Msg != "error" || entries[1].Msg != "warn" {
		t.Fatalf("expected [error warn], got [%s %s]", entries[0].Msg, entries[1].Msg)
	}

	// Filter ERROR only
	entries = b.Recent(10, "ERROR")
	if len(entries) != 1 || entries[0].Msg != "error" {
		t.Fatalf("expected [error], got %v", entries)
	}

	// No filter (empty string = INFO+)
	entries = b.Recent(10, "")
	if len(entries) != 3 { // INFO, WARN, ERROR (DEBUG filtered out)
		t.Fatalf("expected 3 (INFO+), got %d", len(entries))
	}
}

func TestLogRingBuffer_Empty(t *testing.T) {
	b := NewLogRingBuffer(10)
	entries := b.Recent(10, "")
	if len(entries) != 0 {
		t.Fatalf("expected 0, got %d", len(entries))
	}
}

func TestLogRingBuffer_NotifyCh(t *testing.T) {
	b := NewLogRingBuffer(10)
	ch := make(chan LogEntry, 5)
	b.SetNotifyCh(ch)

	b.Add(LogEntry{Level: "INFO", Msg: "notified"})

	select {
	case e := <-ch:
		if e.Msg != "notified" {
			t.Fatalf("expected 'notified', got '%s'", e.Msg)
		}
	case <-time.After(time.Second):
		t.Fatal("timeout waiting for notification")
	}
}

func TestLogRingBuffer_NotifyCh_FullDrops(t *testing.T) {
	b := NewLogRingBuffer(10)
	ch := make(chan LogEntry, 1) // capacity 1
	b.SetNotifyCh(ch)

	b.Add(LogEntry{Msg: "first"})
	b.Add(LogEntry{Msg: "second"}) // should be dropped (channel full)

	if len(ch) != 1 {
		t.Fatalf("expected channel length 1, got %d", len(ch))
	}
}

func TestParseSlogLevel(t *testing.T) {
	tests := []struct {
		input    string
		expected string
	}{
		{"DEBUG", "DEBUG"},
		{"info", "INFO"},
		{"WARN", "WARN"},
		{"error", "ERROR"},
		{"", "INFO"},
		{"unknown", "DEBUG"}, // unknown → no filter
	}
	for _, tt := range tests {
		lv := parseSlogLevel(tt.input)
		if lv.String() != tt.expected {
			t.Errorf("parseSlogLevel(%q) = %s, want %s", tt.input, lv.String(), tt.expected)
		}
	}
}

func TestConfig_LoadDefault(t *testing.T) {
	cfg := DefaultConfig()
	if cfg.FrameRate != 20 {
		t.Fatalf("expected frameRate 20, got %d", cfg.FrameRate)
	}
	if cfg.Addr != ":9000" {
		t.Fatalf("expected addr :9000, got %s", cfg.Addr)
	}
	if cfg.FrameBufferSize != 2400 {
		t.Fatalf("expected frameBufferSize 2400, got %d", cfg.FrameBufferSize)
	}
	if cfg.DisconnectKeepSec != 120 {
		t.Fatalf("expected disconnectKeepSec 120, got %d", cfg.DisconnectKeepSec)
	}
}

func TestConfig_LoadMissing(t *testing.T) {
	cfg := LoadConfig("/nonexistent/path.yaml")
	// Should return defaults
	if cfg.FrameRate != 20 {
		t.Fatalf("missing file should return defaults, got frameRate=%d", cfg.FrameRate)
	}
}

func TestConfig_LoadEmpty(t *testing.T) {
	cfg := LoadConfig("")
	if cfg.FrameRate != 20 {
		t.Fatalf("empty path should return defaults, got frameRate=%d", cfg.FrameRate)
	}
}

func TestConfig_LoadActualFile(t *testing.T) {
	cfg := LoadConfig("config.yaml")
	// config.yaml exists in cmd/framesync/
	if cfg.Addr == "" {
		t.Fatal("loaded config should have non-empty addr")
	}
}
