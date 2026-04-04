package framesync

import (
	"testing"
)

func TestUpdateSnapshot_RejectsStale(t *testing.T) {
	room := NewRoomWithConfig(RoomConfig{
		FrameRate:              20,
		FrameBufferSize:        100,
		SnapshotIntervalFrames: 50,
		QuickReconnectMaxMs:    5000,
	})

	// frame=100 应接受
	if !room.UpdateSnapshot(100, []byte{1, 2, 3}) {
		t.Error("frame=100 should be accepted")
	}
	f, d := room.GetSnapshot()
	if f != 100 || len(d) != 3 {
		t.Errorf("snapshot: frame=%d len=%d", f, len(d))
	}

	// frame=50 应拒绝（比当前旧）
	if room.UpdateSnapshot(50, []byte{4, 5}) {
		t.Error("frame=50 should be rejected (stale)")
	}
	f, _ = room.GetSnapshot()
	if f != 100 {
		t.Errorf("snapshot should still be 100, got %d", f)
	}

	// frame=100 相同帧号也应拒绝
	if room.UpdateSnapshot(100, []byte{6, 7}) {
		t.Error("frame=100 (same) should be rejected")
	}

	// frame=200 应接受
	if !room.UpdateSnapshot(200, []byte{8, 9}) {
		t.Error("frame=200 should be accepted")
	}
	f, _ = room.GetSnapshot()
	if f != 200 {
		t.Errorf("snapshot should be 200, got %d", f)
	}
}

func TestOldestBufferedFrame_Empty(t *testing.T) {
	room := NewRoomWithConfig(RoomConfig{
		FrameRate:       20,
		FrameBufferSize: 10,
	})
	if room.OldestBufferedFrame() != 0 {
		t.Errorf("empty ring should return 0, got %d", room.OldestBufferedFrame())
	}
}

// OldestBufferedFrame 和 snapshot 测试直接操作 room 内部字段

func TestOldestBufferedFrame_AfterFrames(t *testing.T) {
	room := NewRoomWithConfig(RoomConfig{
		FrameRate:       20,
		FrameBufferSize: 5,
	})
	// 手动模拟写入帧到环形缓冲区
	for i := uint32(1); i <= 3; i++ {
		room.frameRing[room.frameRingPos] = CachedFrame{FrameNumber: i, EncodedData: []byte{0}}
		room.frameRingPos = (room.frameRingPos + 1) % len(room.frameRing)
		room.frameRingLen++
	}

	if oldest := room.OldestBufferedFrame(); oldest != 1 {
		t.Errorf("oldest should be 1, got %d", oldest)
	}

	// 写满 + 覆盖
	for i := uint32(4); i <= 8; i++ {
		room.frameRing[room.frameRingPos] = CachedFrame{FrameNumber: i, EncodedData: []byte{0}}
		room.frameRingPos = (room.frameRingPos + 1) % len(room.frameRing)
		if room.frameRingLen < len(room.frameRing) {
			room.frameRingLen++
		}
	}

	// 缓冲区大小 5，帧 4-8 在里面，最旧是 4
	if oldest := room.OldestBufferedFrame(); oldest != 4 {
		t.Errorf("oldest should be 4 after overflow, got %d", oldest)
	}
}

func TestSnapshotStaleResetsOnUpload(t *testing.T) {
	room := NewRoomWithConfig(RoomConfig{
		FrameRate:              20,
		FrameBufferSize:        100,
		SnapshotIntervalFrames: 10,
	})

	// 模拟 snapshotStaleFrames 累积
	room.mu.Lock()
	room.snapshotStaleFrames = 25
	room.mu.Unlock()

	// 上传快照应重置计数器
	room.UpdateSnapshot(50, []byte{1})

	room.mu.Lock()
	if room.snapshotStaleFrames != 0 {
		t.Errorf("snapshotStaleFrames should be 0 after upload, got %d", room.snapshotStaleFrames)
	}
	room.mu.Unlock()
}

func TestSnapshotPauseAndResume(t *testing.T) {
	room := NewRoomWithConfig(RoomConfig{
		FrameRate:              20,
		FrameBufferSize:        100,
		SnapshotIntervalFrames: 10,
	})

	// 模拟达到暂停阈值
	room.mu.Lock()
	room.snapshotStaleFrames = 29 // 3×10 - 1
	room.snapshotPaused = false
	room.running = true
	room.frameNumber = 100
	room.mu.Unlock()

	if room.IsSnapshotPaused() {
		t.Error("should not be paused yet")
	}

	// 模拟暂停状态
	room.mu.Lock()
	room.snapshotStaleFrames = 30
	room.snapshotPaused = true
	room.mu.Unlock()

	if !room.IsSnapshotPaused() {
		t.Error("should be paused")
	}

	// 上传快照应恢复
	room.UpdateSnapshot(110, []byte{1})

	if room.IsSnapshotPaused() {
		t.Error("should resume after snapshot upload")
	}
}

func TestGamePause_Toggle(t *testing.T) {
	room := NewRoomWithConfig(RoomConfig{
		FrameRate:       20,
		FrameBufferSize: 100,
	})

	if room.IsGamePaused() {
		t.Error("should not be paused initially")
	}

	// First pause → state changed
	if !room.GamePause() {
		t.Error("GamePause should return true on state change")
	}
	if !room.IsGamePaused() {
		t.Error("should be paused after GamePause")
	}

	// Duplicate pause → no change
	if room.GamePause() {
		t.Error("GamePause should return false when already paused")
	}

	// Resume → state changed
	if !room.GameResume() {
		t.Error("GameResume should return true on state change")
	}
	if room.IsGamePaused() {
		t.Error("should not be paused after GameResume")
	}

	// Duplicate resume → no change
	if room.GameResume() {
		t.Error("GameResume should return false when already running")
	}
}

func TestGamePause_StepFrameBlocked(t *testing.T) {
	room := NewRoomWithConfig(RoomConfig{
		FrameRate:       20,
		FrameBufferSize: 100,
	})
	room.mu.Lock()
	room.running = true
	room.mu.Unlock()

	// Push a few frames normally
	room.stepFrame()
	room.stepFrame()
	room.stepFrame()
	if room.frameNumber != 3 {
		t.Fatalf("expected frame 3, got %d", room.frameNumber)
	}

	// Pause → stepFrame should not advance
	room.GamePause()
	room.stepFrame()
	room.stepFrame()
	if room.frameNumber != 3 {
		t.Errorf("frame should stay 3 during pause, got %d", room.frameNumber)
	}

	// Resume → stepFrame should advance again
	room.GameResume()
	room.stepFrame()
	if room.frameNumber != 4 {
		t.Errorf("frame should be 4 after resume, got %d", room.frameNumber)
	}
}

func TestGamePause_InputsBuffered(t *testing.T) {
	room := NewRoomWithConfig(RoomConfig{
		FrameRate:       20,
		FrameBufferSize: 100,
	})
	room.mu.Lock()
	room.running = true
	room.mu.Unlock()

	// Advance to frame 1 so we have a baseline
	room.stepFrame()

	// Pause and add input
	room.GamePause()
	room.OnInput(1, []byte{0xAB, 0xCD})

	// stepFrame blocked — input stays in pendingInputs
	room.stepFrame()
	if room.frameNumber != 1 {
		t.Fatalf("frame should stay 1 during pause, got %d", room.frameNumber)
	}

	// Resume — next frame should carry the buffered input
	room.GameResume()
	room.stepFrame()
	if room.frameNumber != 2 {
		t.Errorf("frame should be 2 after resume, got %d", room.frameNumber)
	}

	// Verify the input was included by checking the ring buffer
	room.mu.Lock()
	// The latest frame is at (ringPos-1) mod size
	pos := (room.frameRingPos - 1 + len(room.frameRing)) % len(room.frameRing)
	data := room.frameRing[pos].EncodedData
	room.mu.Unlock()

	frame, err := DecodeFrameData(data)
	if err != nil {
		t.Fatalf("DecodeFrameData: %v", err)
	}
	if frame.FrameNumber != 2 {
		t.Errorf("decoded frame should be 2, got %d", frame.FrameNumber)
	}
	if len(frame.Inputs) != 1 {
		t.Fatalf("expected 1 input in resumed frame, got %d", len(frame.Inputs))
	}
	if frame.Inputs[0].PlayerId != 1 {
		t.Errorf("input playerId should be 1, got %d", frame.Inputs[0].PlayerId)
	}
}
