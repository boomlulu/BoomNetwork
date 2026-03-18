package codec

import (
	"bytes"
	"testing"
)

func TestEncodeDecode_EmptyData_NoSeq(t *testing.T) {
	msg := &Message{Cmd: 42}

	buf := Encode(msg)
	defer PutBuf(buf)
	decoded, err := Decode(buf)
	if err != nil {
		t.Fatalf("Decode failed: %v", err)
	}

	if decoded.Cmd != 42 {
		t.Errorf("Cmd: got %d, want 42", decoded.Cmd)
	}
	if decoded.HasSeq {
		t.Error("HasSeq should be false")
	}
	if len(decoded.Data) != 0 {
		t.Errorf("Data: got len %d, want 0", len(decoded.Data))
	}
}

func TestEncodeDecode_WithData_WithSeq(t *testing.T) {
	payload := []byte("Hello BoomNetwork!")
	msg := &Message{Cmd: 10, HasSeq: true, Seq: 12345, Data: payload}

	buf := Encode(msg)
	defer PutBuf(buf)
	decoded, err := Decode(buf)
	if err != nil {
		t.Fatalf("Decode failed: %v", err)
	}

	if decoded.Cmd != 10 {
		t.Errorf("Cmd: got %d, want 10", decoded.Cmd)
	}
	if !decoded.HasSeq || decoded.Seq != 12345 {
		t.Errorf("Seq: got %d, want 12345", decoded.Seq)
	}
	if !bytes.Equal(decoded.Data, payload) {
		t.Errorf("Data mismatch")
	}
}

func TestEncodeDecode_LargeData(t *testing.T) {
	largeData := make([]byte, 70000)
	for i := range largeData {
		largeData[i] = byte(i % 256)
	}
	msg := &Message{Cmd: 5, HasSeq: true, Seq: 999, Data: largeData}

	buf := Encode(msg)
	defer PutBuf(buf)
	decoded, err := DecodeCopy(buf)
	if err != nil {
		t.Fatalf("Decode failed: %v", err)
	}

	if decoded.Cmd != 5 {
		t.Errorf("Cmd: got %d, want 5", decoded.Cmd)
	}
	if decoded.Seq != 999 {
		t.Errorf("Seq: got %d, want 999", decoded.Seq)
	}
	if len(decoded.Data) != 70000 {
		t.Errorf("Data len: got %d, want 70000", len(decoded.Data))
	}
}

func TestDecode_BufferTooShort(t *testing.T) {
	_, err := Decode([]byte{0x01})
	if err == nil {
		t.Error("Expected error for short buffer")
	}
}

func TestHeaderSize_Variants(t *testing.T) {
	// No Seq, small: 1 + 2 = 3
	m1 := &Message{Cmd: 1}
	if m1.HeaderSize() != 3 {
		t.Errorf("m1 header: got %d, want 3", m1.HeaderSize())
	}

	// With Seq, small: 1 + 2 + 4 = 7
	m2 := &Message{Cmd: 1, HasSeq: true, Seq: 1}
	if m2.HeaderSize() != 7 {
		t.Errorf("m2 header: got %d, want 7", m2.HeaderSize())
	}

	// No Seq, large: 1 + 4 = 5
	m3 := &Message{Cmd: 1, Data: make([]byte, 70000)}
	if m3.HeaderSize() != 5 {
		t.Errorf("m3 header: got %d, want 5", m3.HeaderSize())
	}
}

func TestPeekFrameSize(t *testing.T) {
	msg := &Message{Cmd: 1, HasSeq: true, Seq: 5, Data: []byte("hello")}
	buf := Encode(msg)
	defer PutBuf(buf)

	size := PeekFrameSize(buf)
	if size != len(buf) {
		t.Errorf("PeekFrameSize: got %d, want %d", size, len(buf))
	}
}

func TestFramingReadWrite(t *testing.T) {
	msg := &Message{Cmd: 31, HasSeq: true, Seq: 3, Data: []byte("framing test")}

	var buf bytes.Buffer
	if err := WriteMessage(&buf, msg); err != nil {
		t.Fatalf("WriteMessage failed: %v", err)
	}

	decoded, err := ReadMessage(&buf)
	if err != nil {
		t.Fatalf("ReadMessage failed: %v", err)
	}

	if decoded.Cmd != 31 {
		t.Errorf("Cmd: got %d, want 31", decoded.Cmd)
	}
	if string(decoded.Data) != "framing test" {
		t.Errorf("Data: got %q", decoded.Data)
	}
}

func TestFramingMultipleMessages(t *testing.T) {
	var buf bytes.Buffer
	for i := 0; i < 5; i++ {
		msg := &Message{Cmd: byte(i + 1), Data: []byte("msg")}
		if err := WriteMessage(&buf, msg); err != nil {
			t.Fatalf("WriteMessage %d failed: %v", i, err)
		}
	}

	for i := 0; i < 5; i++ {
		decoded, err := ReadMessage(&buf)
		if err != nil {
			t.Fatalf("ReadMessage %d failed: %v", i, err)
		}
		if decoded.Cmd != byte(i+1) {
			t.Errorf("Message %d Cmd: got %d, want %d", i, decoded.Cmd, i+1)
		}
	}
}
