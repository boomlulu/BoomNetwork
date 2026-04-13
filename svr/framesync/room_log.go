package framesync

import (
	"sync"
	"time"
)

// ===================== Per-Room Log Buffer =====================

// RoomLogEntry 单条 room 日志
type RoomLogEntry struct {
	Ts    int64             `json:"ts"`              // unix ms
	Level string            `json:"level"`           // "INFO" / "WARN" / "ERROR"
	Msg   string            `json:"msg"`
	Attrs map[string]any    `json:"attrs,omitempty"` // 可选 key-value 附属信息
}

const roomLogBufCap = 500 // 每个房间最多保留 500 条日志

// roomLogBuffer 房间专属环形缓冲区
type roomLogBuffer struct {
	mu    sync.Mutex
	buf   []RoomLogEntry
	pos   int // 下一个写入位置
	count int // 当前有效条数
}

func newRoomLogBuffer() *roomLogBuffer {
	return &roomLogBuffer{
		buf: make([]RoomLogEntry, roomLogBufCap),
	}
}

// add 写入一条日志（内部，持锁调用者无需再加锁）
func (b *roomLogBuffer) add(e RoomLogEntry) {
	b.mu.Lock()
	b.buf[b.pos] = e
	b.pos = (b.pos + 1) % roomLogBufCap
	if b.count < roomLogBufCap {
		b.count++
	}
	b.mu.Unlock()
}

// recent 返回最新 n 条日志（最新的在前）
func (b *roomLogBuffer) recent(n int) []RoomLogEntry {
	b.mu.Lock()
	defer b.mu.Unlock()

	total := b.count
	if n > total {
		n = total
	}
	result := make([]RoomLogEntry, n)
	for i := 0; i < n; i++ {
		idx := (b.pos - 1 - i + roomLogBufCap) % roomLogBufCap
		result[i] = b.buf[idx]
	}
	return result
}

// ===================== Room 日志写入 API =====================

// LogEvent 向房间日志缓冲区写入一条日志（线程安全，可在持锁或释锁后调用）
func (r *Room) LogEvent(level, msg string, attrs map[string]any) {
	r.logBuf.add(RoomLogEntry{
		Ts:    time.Now().UnixMilli(),
		Level: level,
		Msg:   msg,
		Attrs: attrs,
	})
}

// GetLogs 返回最新 n 条房间日志
func (r *Room) GetLogs(n int) []RoomLogEntry {
	return r.logBuf.recent(n)
}
