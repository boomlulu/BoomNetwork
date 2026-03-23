package framesync

import (
	"bytes"
	"testing"
)

func TestEncodeDecodeInitData_NewFields(t *testing.T) {
	d := &InitData{
		FrameRate:           20,
		FrameInterval:       50,
		StartTime:           1234567890,
		SnapshotInterval:    100,
		QuickReconnectMaxMs: 5000,
	}
	buf := EncodeInitData(d)
	if len(buf) != InitDataSize {
		t.Fatalf("expected %d bytes, got %d", InitDataSize, len(buf))
	}

	decoded := DecodeInitData(buf)
	if decoded.FrameRate != 20 {
		t.Errorf("FrameRate: got %d, want 20", decoded.FrameRate)
	}
	if decoded.FrameInterval != 50 {
		t.Errorf("FrameInterval: got %d, want 50", decoded.FrameInterval)
	}
	if decoded.StartTime != 1234567890 {
		t.Errorf("StartTime: got %d, want 1234567890", decoded.StartTime)
	}
	if decoded.SnapshotInterval != 100 {
		t.Errorf("SnapshotInterval: got %d, want 100", decoded.SnapshotInterval)
	}
	if decoded.QuickReconnectMaxMs != 5000 {
		t.Errorf("QuickReconnectMaxMs: got %d, want 5000", decoded.QuickReconnectMaxMs)
	}
}

func TestDecodeInitData_LegacyCompat(t *testing.T) {
	// 只有 16 字节（旧服务器），新字段应为 0
	d := &InitData{FrameRate: 20, FrameInterval: 50, StartTime: 999}
	buf := EncodeInitData(d)
	legacy := buf[:16]

	decoded := DecodeInitData(legacy)
	if decoded.FrameRate != 20 {
		t.Errorf("FrameRate: got %d", decoded.FrameRate)
	}
	if decoded.SnapshotInterval != 0 {
		t.Errorf("SnapshotInterval should be 0 for legacy, got %d", decoded.SnapshotInterval)
	}
	if decoded.QuickReconnectMaxMs != 0 {
		t.Errorf("QuickReconnectMaxMs should be 0 for legacy, got %d", decoded.QuickReconnectMaxMs)
	}
}

func TestEncodeReconnectRsp_Success(t *testing.T) {
	snapshot := []byte{0xAA, 0xBB, 0xCC}
	buf := EncodeReconnectRsp(ReconnectSuccess, 42, 200, 150, snapshot)

	if buf[0] != ReconnectSuccess {
		t.Errorf("Result: got %d, want %d", buf[0], ReconnectSuccess)
	}
	if len(buf) != 13+3 {
		t.Errorf("Length: got %d, want 16", len(buf))
	}
	if !bytes.Equal(buf[13:], snapshot) {
		t.Errorf("Snapshot mismatch")
	}
}

func TestEncodeReconnectRsp_BufferStale(t *testing.T) {
	buf := EncodeReconnectRsp(ReconnectFailBufferStale, 42, 200, 0, nil)
	if buf[0] != ReconnectFailBufferStale {
		t.Errorf("Result: got %d, want %d", buf[0], ReconnectFailBufferStale)
	}
	if len(buf) != 13 {
		t.Errorf("Length: got %d, want 13", len(buf))
	}
}

func TestEncodeReconnectRsp_Fail(t *testing.T) {
	buf := EncodeReconnectRsp(ReconnectFail, 0, 0, 0, nil)
	if buf[0] != ReconnectFail {
		t.Errorf("Result: got %d, want %d", buf[0], ReconnectFail)
	}
}
