package codec

import (
	"bytes"
	"testing"
)

func TestEncodeDecode_EmptyData(t *testing.T) {
	msg := &Message{
		Version:   1,
		Cmd:       42,
		ClientSeq: 10,
		ServerSeq: 20,
		Data:      nil,
	}

	buf := Encode(msg)
	decoded, err := Decode(buf)
	if err != nil {
		t.Fatalf("Decode failed: %v", err)
	}

	if decoded.Version != 1 {
		t.Errorf("Version: got %d, want 1", decoded.Version)
	}
	if decoded.Cmd != 42 {
		t.Errorf("Cmd: got %d, want 42", decoded.Cmd)
	}
	if decoded.ClientSeq != 10 {
		t.Errorf("ClientSeq: got %d, want 10", decoded.ClientSeq)
	}
	if decoded.ServerSeq != 20 {
		t.Errorf("ServerSeq: got %d, want 20", decoded.ServerSeq)
	}
	if len(decoded.Data) != 0 {
		t.Errorf("Data length: got %d, want 0", len(decoded.Data))
	}
}

func TestEncodeDecode_WithData(t *testing.T) {
	payload := []byte("Hello BoomNetwork!")
	msg := &Message{
		Version:   0,
		Cmd:       100,
		ClientSeq: 1,
		ServerSeq: 2,
		Data:      payload,
	}

	buf := Encode(msg)
	decoded, err := Decode(buf)
	if err != nil {
		t.Fatalf("Decode failed: %v", err)
	}

	if decoded.Cmd != 100 {
		t.Errorf("Cmd: got %d, want 100", decoded.Cmd)
	}
	if !bytes.Equal(decoded.Data, payload) {
		t.Errorf("Data mismatch: got %q, want %q", decoded.Data, payload)
	}
}

func TestEncodeDecode_LargeValues(t *testing.T) {
	msg := &Message{
		Version:   255,
		Cmd:       0xFFFFFFFF,
		ClientSeq: 0x7FFFFFFF,  // int32 max
		ServerSeq: -2147483648, // int32 min
		Data:      []byte{0xFF, 0x00, 0xAB},
	}

	buf := Encode(msg)
	decoded, err := Decode(buf)
	if err != nil {
		t.Fatalf("Decode failed: %v", err)
	}

	if decoded.Version != 255 {
		t.Errorf("Version: got %d, want 255", decoded.Version)
	}
	if decoded.Cmd != 0xFFFFFFFF {
		t.Errorf("Cmd: got %d, want %d", decoded.Cmd, uint32(0xFFFFFFFF))
	}
	if decoded.ClientSeq != 0x7FFFFFFF {
		t.Errorf("ClientSeq: got %d, want %d", decoded.ClientSeq, int32(0x7FFFFFFF))
	}
	if decoded.ServerSeq != -2147483648 {
		t.Errorf("ServerSeq: got %d, want %d", decoded.ServerSeq, int32(-2147483648))
	}
	if !bytes.Equal(decoded.Data, []byte{0xFF, 0x00, 0xAB}) {
		t.Errorf("Data mismatch")
	}
}

func TestDecode_BufferTooShort(t *testing.T) {
	_, err := Decode([]byte{0x01, 0x02})
	if err == nil {
		t.Error("Expected error for short buffer")
	}
}

func TestEncodedSize(t *testing.T) {
	msg := &Message{
		Version: 0,
		Cmd:     1,
		Data:    make([]byte, 128),
	}

	buf := Encode(msg)
	expected := HeaderSize + BodyHeaderSize + 128
	if len(buf) != expected {
		t.Errorf("Encoded size: got %d, want %d", len(buf), expected)
	}
}

func TestFramingReadWrite(t *testing.T) {
	msg := &Message{
		Version:   0,
		Cmd:       77,
		ClientSeq: 3,
		ServerSeq: 4,
		Data:      []byte("framing test"),
	}

	var buf bytes.Buffer
	if err := WriteMessage(&buf, msg); err != nil {
		t.Fatalf("WriteMessage failed: %v", err)
	}

	decoded, err := ReadMessage(&buf)
	if err != nil {
		t.Fatalf("ReadMessage failed: %v", err)
	}

	if decoded.Cmd != 77 {
		t.Errorf("Cmd: got %d, want 77", decoded.Cmd)
	}
	if string(decoded.Data) != "framing test" {
		t.Errorf("Data: got %q, want %q", decoded.Data, "framing test")
	}
}

func TestFramingMultipleMessages(t *testing.T) {
	var buf bytes.Buffer

	for i := 0; i < 5; i++ {
		msg := &Message{
			Cmd:       uint32(i + 1),
			ClientSeq: int32(i),
			Data:      []byte("msg"),
		}
		if err := WriteMessage(&buf, msg); err != nil {
			t.Fatalf("WriteMessage %d failed: %v", i, err)
		}
	}

	for i := 0; i < 5; i++ {
		decoded, err := ReadMessage(&buf)
		if err != nil {
			t.Fatalf("ReadMessage %d failed: %v", i, err)
		}
		if decoded.Cmd != uint32(i+1) {
			t.Errorf("Message %d Cmd: got %d, want %d", i, decoded.Cmd, i+1)
		}
	}
}
