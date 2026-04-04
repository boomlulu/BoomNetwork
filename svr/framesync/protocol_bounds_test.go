package framesync

import (
	"encoding/binary"
	"strings"
	"testing"
)

// C3 fix tests: DecodeInitData + DecodeFrameData bounds checking

// ─── DecodeInitData ─────────────────────────────────────────────────────────

func TestDecodeInitData_TooShort_ReturnsNil(t *testing.T) {
	cases := []int{0, 1, 8, 15}
	for _, n := range cases {
		if got := DecodeInitData(make([]byte, n)); got != nil {
			t.Errorf("len=%d: expected nil, got %+v", n, got)
		}
	}
}

func TestDecodeInitData_LegacySize_OK(t *testing.T) {
	buf := make([]byte, 16)
	binary.LittleEndian.PutUint32(buf[0:], 30)  // FrameRate
	binary.LittleEndian.PutUint32(buf[4:], 33)  // FrameInterval
	binary.LittleEndian.PutUint64(buf[8:], 999) // StartTime
	d := DecodeInitData(buf)
	if d == nil {
		t.Fatal("expected non-nil")
	}
	if d.FrameRate != 30 || d.FrameInterval != 33 || d.StartTime != 999 {
		t.Errorf("unexpected values: %+v", d)
	}
	if d.SnapshotInterval != 0 || d.QuickReconnectMaxMs != 0 {
		t.Errorf("optional fields should be zero: %+v", d)
	}
}

func TestDecodeInitData_FullSize_OK(t *testing.T) {
	buf := make([]byte, InitDataSize)
	binary.LittleEndian.PutUint32(buf[0:], 60)
	binary.LittleEndian.PutUint32(buf[16:], 120) // SnapshotInterval
	binary.LittleEndian.PutUint32(buf[20:], 5000) // QuickReconnectMaxMs
	d := DecodeInitData(buf)
	if d == nil {
		t.Fatal("expected non-nil")
	}
	if d.SnapshotInterval != 120 || d.QuickReconnectMaxMs != 5000 {
		t.Errorf("unexpected optional values: %+v", d)
	}
}

// ─── DecodeFrameData ─────────────────────────────────────────────────────────

func TestDecodeFrameData_EmptyBuffer_Error(t *testing.T) {
	_, err := DecodeFrameData([]byte{})
	if err == nil {
		t.Fatal("expected error for empty buffer")
	}
}

func TestDecodeFrameData_TooShort_Error(t *testing.T) {
	_, err := DecodeFrameData(make([]byte, 5))
	if err == nil {
		t.Fatal("expected error for 5-byte buffer (need 6)")
	}
}

func TestDecodeFrameData_Valid_NoInputsNoEvents(t *testing.T) {
	buf := make([]byte, 7) // FrameNumber(4)+InputCount(2)+EventCount(1)
	binary.LittleEndian.PutUint32(buf[0:], 42)
	// inputCount=0, eventCount=0
	f, err := DecodeFrameData(buf)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if f.FrameNumber != 42 {
		t.Errorf("FrameNumber: got %d", f.FrameNumber)
	}
	if len(f.Inputs) != 0 || len(f.Events) != 0 {
		t.Errorf("expected empty inputs/events")
	}
}

func TestDecodeFrameData_Valid_Roundtrip(t *testing.T) {
	frame := &FrameData{
		FrameNumber: 77,
		Inputs: []PlayerInput{
			{PlayerId: 1, Data: []byte{0xAA, 0xBB}},
			{PlayerId: 2, Data: nil},
		},
		Events: []FrameEvent{
			{EventType: FrameEventPlayerJoined, PlayerId: 3},
		},
	}
	size := FrameDataSize(frame)
	buf := make([]byte, size)
	written := EncodeFrameData(frame, buf)
	if written != size {
		t.Fatalf("EncodeFrameData: wrote %d != size %d", written, size)
	}

	decoded, err := DecodeFrameData(buf[:written])
	if err != nil {
		t.Fatalf("DecodeFrameData: %v", err)
	}
	if decoded.FrameNumber != 77 {
		t.Errorf("FrameNumber: got %d", decoded.FrameNumber)
	}
	if len(decoded.Inputs) != 2 {
		t.Fatalf("Inputs: got %d", len(decoded.Inputs))
	}
	if decoded.Inputs[0].PlayerId != 1 || len(decoded.Inputs[0].Data) != 2 {
		t.Errorf("Input[0] mismatch")
	}
	if len(decoded.Events) != 1 || decoded.Events[0].EventType != FrameEventPlayerJoined {
		t.Errorf("Events mismatch")
	}
}

func TestDecodeFrameData_OversizedInputCount_Error(t *testing.T) {
	// inputCount = 257 (> maxFrameInputs=256)
	buf := make([]byte, 6)
	binary.LittleEndian.PutUint32(buf[0:], 1)
	binary.LittleEndian.PutUint16(buf[4:], 257)
	_, err := DecodeFrameData(buf)
	if err == nil {
		t.Fatal("expected error for oversized inputCount")
	}
	if !strings.Contains(err.Error(), "exceeds limit") {
		t.Errorf("error should mention limit: %v", err)
	}
}

func TestDecodeFrameData_OversizedDataLen_Error(t *testing.T) {
	// inputCount=1, dataLen=4097 (> maxInputDataLen=4096)
	buf := make([]byte, 12)
	binary.LittleEndian.PutUint32(buf[0:], 1)
	binary.LittleEndian.PutUint16(buf[4:], 1)    // inputCount=1
	binary.LittleEndian.PutUint32(buf[6:], 1)    // playerId
	binary.LittleEndian.PutUint16(buf[10:], 4097) // dataLen=4097
	_, err := DecodeFrameData(buf)
	if err == nil {
		t.Fatal("expected error for oversized dataLen")
	}
}

func TestDecodeFrameData_TruncatedInputHeader_Error(t *testing.T) {
	// inputCount=1 but only 7 bytes total (need 12 for input header)
	buf := make([]byte, 7)
	binary.LittleEndian.PutUint32(buf[0:], 1)
	binary.LittleEndian.PutUint16(buf[4:], 1) // inputCount=1
	// buf[6] exists but only 1 byte, need 6 for header
	_, err := DecodeFrameData(buf)
	if err == nil {
		t.Fatal("expected error for truncated input header")
	}
}

func TestDecodeFrameData_TruncatedInputData_Error(t *testing.T) {
	// inputCount=1, dataLen=100 but buffer ends
	buf := make([]byte, 12)
	binary.LittleEndian.PutUint32(buf[0:], 1)
	binary.LittleEndian.PutUint16(buf[4:], 1)   // inputCount=1
	binary.LittleEndian.PutUint32(buf[6:], 1)   // playerId
	binary.LittleEndian.PutUint16(buf[10:], 100) // dataLen=100
	_, err := DecodeFrameData(buf)
	if err == nil {
		t.Fatal("expected error for truncated input data")
	}
}

func TestDecodeFrameData_OversizedEventCount_Error(t *testing.T) {
	// No inputs, eventCount=33 (> maxFrameEvents=32)
	buf := make([]byte, 7)
	binary.LittleEndian.PutUint32(buf[0:], 1)
	binary.LittleEndian.PutUint16(buf[4:], 0) // inputCount=0
	buf[6] = 33                                // eventCount=33
	_, err := DecodeFrameData(buf)
	if err == nil {
		t.Fatal("expected error for oversized eventCount")
	}
}

func TestDecodeFrameData_TruncatedEvent_Error(t *testing.T) {
	// No inputs, eventCount=1 but only 2 bytes for event (need 5)
	buf := make([]byte, 9) // 4+2+1+2 bytes (short by 2)
	binary.LittleEndian.PutUint32(buf[0:], 1)
	binary.LittleEndian.PutUint16(buf[4:], 0) // inputCount=0
	buf[6] = 1                                 // eventCount=1
	buf[7] = 1                                 // EventType
	// 1 byte for PlayerId instead of 4
	_, err := DecodeFrameData(buf)
	if err == nil {
		t.Fatal("expected error for truncated event")
	}
}
