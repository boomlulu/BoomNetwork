package main

import (
	"context"
	"fmt"
	"log/slog"
	"strings"
	"sync"
)

// ===================== 日志环形缓冲区 =====================

// LogEntry 单条日志
type LogEntry struct {
	Ts    int64  `json:"ts"`    // unix ms
	Level string `json:"level"` // "DEBUG", "INFO", "WARN", "ERROR"
	Msg   string `json:"msg"`
	Attrs string `json:"attrs,omitempty"` // key=value 对
}

// LogRingBuffer 环形缓冲区，保存最近 N 条日志
type LogRingBuffer struct {
	mu       sync.Mutex
	buf      []LogEntry
	pos      int
	count    int
	cap_     int
	notifyCh chan LogEntry // 可选：实时推送到 GMHub
}

// NewLogRingBuffer 创建环形缓冲区
func NewLogRingBuffer(cap int) *LogRingBuffer {
	return &LogRingBuffer{
		buf:  make([]LogEntry, cap),
		cap_: cap,
	}
}

// Add 添加一条日志
func (b *LogRingBuffer) Add(e LogEntry) {
	b.mu.Lock()
	b.buf[b.pos] = e
	b.pos = (b.pos + 1) % b.cap_
	if b.count < b.cap_ {
		b.count++
	}
	b.mu.Unlock()

	if b.notifyCh != nil {
		select {
		case b.notifyCh <- e:
		default: // 满了就丢弃
		}
	}
}

// SetNotifyCh 设置实时通知 channel
func (b *LogRingBuffer) SetNotifyCh(ch chan LogEntry) {
	b.notifyCh = ch
}

// Recent 返回最近 n 条日志（最新的在前），按 minLevel 过滤
func (b *LogRingBuffer) Recent(n int, minLevel string) []LogEntry {
	minLvl := parseSlogLevel(minLevel)

	b.mu.Lock()
	defer b.mu.Unlock()

	result := make([]LogEntry, 0, min(n, b.count))
	for i := 0; i < b.count && len(result) < n; i++ {
		idx := (b.pos - 1 - i + b.cap_) % b.cap_
		e := b.buf[idx]
		if parseSlogLevel(e.Level) >= minLvl {
			result = append(result, e)
		}
	}
	return result
}

func parseSlogLevel(s string) slog.Level {
	switch strings.ToUpper(s) {
	case "DEBUG":
		return slog.LevelDebug
	case "INFO", "":
		return slog.LevelInfo
	case "WARN":
		return slog.LevelWarn
	case "ERROR":
		return slog.LevelError
	default:
		return slog.LevelDebug // 未知级别 → 不过滤
	}
}

func min(a, b int) int {
	if a < b {
		return a
	}
	return b
}

// ===================== slog TeeHandler =====================

// TeeHandler 将日志同时写入原 Handler 和环形缓冲区
type TeeHandler struct {
	inner  slog.Handler
	buffer *LogRingBuffer
	attrs  []slog.Attr
	group  string
}

// NewTeeHandler 包裹已有 handler，同时写入 ring buffer
func NewTeeHandler(inner slog.Handler, buffer *LogRingBuffer) *TeeHandler {
	return &TeeHandler{inner: inner, buffer: buffer}
}

func (h *TeeHandler) Enabled(ctx context.Context, level slog.Level) bool {
	return h.inner.Enabled(ctx, level)
}

func (h *TeeHandler) Handle(ctx context.Context, r slog.Record) error {
	// 1. 写入原 handler (stdout JSON)
	err := h.inner.Handle(ctx, r)

	// 2. 捕获到环形缓冲区
	var parts []string
	// 先加 handler 级别的 attrs
	for _, a := range h.attrs {
		parts = append(parts, fmt.Sprintf("%s=%v", a.Key, a.Value))
	}
	r.Attrs(func(a slog.Attr) bool {
		key := a.Key
		if h.group != "" {
			key = h.group + "." + key
		}
		parts = append(parts, fmt.Sprintf("%s=%v", key, a.Value))
		return true
	})

	h.buffer.Add(LogEntry{
		Ts:    r.Time.UnixMilli(),
		Level: r.Level.String(),
		Msg:   r.Message,
		Attrs: strings.Join(parts, " "),
	})

	return err
}

func (h *TeeHandler) WithAttrs(attrs []slog.Attr) slog.Handler {
	return &TeeHandler{
		inner:  h.inner.WithAttrs(attrs),
		buffer: h.buffer,
		attrs:  append(append([]slog.Attr{}, h.attrs...), attrs...),
		group:  h.group,
	}
}

func (h *TeeHandler) WithGroup(name string) slog.Handler {
	g := name
	if h.group != "" {
		g = h.group + "." + name
	}
	return &TeeHandler{
		inner:  h.inner.WithGroup(name),
		buffer: h.buffer,
		attrs:  h.attrs,
		group:  g,
	}
}

// LogBuf 全局日志缓冲区（在 main 中初始化）
var LogBuf *LogRingBuffer

// WrapWithLogBuffer 包裹现有 handler，返回 TeeHandler
func WrapWithLogBuffer(inner slog.Handler) *TeeHandler {
	if LogBuf == nil {
		LogBuf = NewLogRingBuffer(2000)
	}
	return NewTeeHandler(inner, LogBuf)
}
