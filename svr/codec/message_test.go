package codec

import (
	"bytes"
	"testing"
)

// === Core Message Tests ===

func TestCore_EmptyData_NoSeq(t *testing.T) {
	msg := NewCoreMessage(7, nil) // Heartbeat-ish
	assertRoundtrip(t, msg)
}

func TestCore_WithData_WithSeq(t *testing.T) {
	msg := NewCoreMessage(10, []byte("Hello BoomNetwork!"))
	msg.HasSeq = true
	msg.Seq = 12345
	assertRoundtrip(t, msg)
}

func TestCore_MaxCmd(t *testing.T) {
	msg := NewCoreMessage(15, []byte("max core cmd"))
	decoded := roundtrip(t, msg)
	if decoded.Cmd != 15 {
		t.Errorf("Cmd: got %d, want 15", decoded.Cmd)
	}
}

func TestCore_LargeData(t *testing.T) {
	data := make([]byte, 70000)
	for i := range data {
		data[i] = byte(i % 256)
	}
	msg := NewCoreMessage(5, data)
	msg.HasSeq = true
	msg.Seq = 999
	decoded := roundtrip(t, msg)
	if len(decoded.Data) != 70000 {
		t.Errorf("Data len: got %d, want 70000", len(decoded.Data))
	}
}

// === Extended Message Tests ===

func TestExt_Basic(t *testing.T) {
	msg := NewExtMessage(42, []byte("authority transfer"))
	assertRoundtrip(t, msg)
}

func TestExt_WithSeq(t *testing.T) {
	msg := NewExtMessage(5, []byte("join room"))
	msg.HasSeq = true
	msg.Seq = 7777
	decoded := roundtrip(t, msg)
	if decoded.CmdType != CmdTypeExtended {
		t.Errorf("CmdType: got %d, want Extended", decoded.CmdType)
	}
	if decoded.ExtCmd != 5 {
		t.Errorf("ExtCmd: got %d, want 5", decoded.ExtCmd)
	}
	if decoded.Seq != 7777 {
		t.Errorf("Seq: got %d, want 7777", decoded.Seq)
	}
}

func TestExt_EmptyData(t *testing.T) {
	msg := NewExtMessage(100, nil)
	assertRoundtrip(t, msg)
}

func TestExt_MaxCmd(t *testing.T) {
	msg := NewExtMessage(65535, []byte("max ext"))
	decoded := roundtrip(t, msg)
	if decoded.ExtCmd != 65535 {
		t.Errorf("ExtCmd: got %d, want 65535", decoded.ExtCmd)
	}
}

// === Game Message Tests ===

func TestGame_Basic(t *testing.T) {
	msg := NewGameMessage(1001, []byte("game event"))
	assertRoundtrip(t, msg)
}

func TestGame_MaxCmd(t *testing.T) {
	msg := NewGameMessage(0xFFFFFFFF, []byte("max game cmd"))
	decoded := roundtrip(t, msg)
	if decoded.CmdType != CmdTypeGame {
		t.Errorf("CmdType: got %d, want Game", decoded.CmdType)
	}
	if decoded.GameCmd != 0xFFFFFFFF {
		t.Errorf("GameCmd: got %d, want 4294967295", decoded.GameCmd)
	}
}

func TestGame_EmptyData(t *testing.T) {
	msg := NewGameMessage(500, nil)
	assertRoundtrip(t, msg)
}

func TestGame_WithSeq(t *testing.T) {
	msg := NewGameMessage(42, []byte("game with seq"))
	msg.HasSeq = true
	msg.Seq = -123
	decoded := roundtrip(t, msg)
	if decoded.Seq != -123 {
		t.Errorf("Seq: got %d, want -123", decoded.Seq)
	}
}

// === Header Size Tests ===

func TestHeaderSize_Core(t *testing.T) {
	m := NewCoreMessage(1, nil)
	if m.HeaderSize() != 3 { // FlagsCmd(1) + BodyLen(2)
		t.Errorf("Core no-seq: got %d, want 3", m.HeaderSize())
	}
	m.HasSeq = true
	if m.HeaderSize() != 7 { // 1 + 2 + 4(seq)
		t.Errorf("Core with-seq: got %d, want 7", m.HeaderSize())
	}
}

func TestHeaderSize_Ext(t *testing.T) {
	m := NewExtMessage(1, nil)
	if m.HeaderSize() != 5 { // 1 + 2 + 2(ExtCmd)
		t.Errorf("Ext no-seq: got %d, want 5", m.HeaderSize())
	}
}

func TestHeaderSize_Game(t *testing.T) {
	m := NewGameMessage(1, nil)
	if m.HeaderSize() != 7 { // 1 + 2 + 4(GameCmd)
		t.Errorf("Game no-seq: got %d, want 7", m.HeaderSize())
	}
}

// === PeekFrameSize ===

func TestPeekFrameSize_AllTypes(t *testing.T) {
	for _, msg := range []*Message{
		NewCoreMessage(1, []byte("core")),
		NewExtMessage(42, []byte("ext")),
		NewGameMessage(999, []byte("game")),
	} {
		buf := Encode(msg)
		size := PeekFrameSize(buf)
		if size != len(buf) {
			t.Errorf("PeekFrameSize(%s): got %d, want %d", msg, size, len(buf))
		}
		PutBuf(buf)
	}
}

// === Framing Tests ===

func TestFraming_MixedTypes(t *testing.T) {
	msgs := []*Message{
		NewCoreMessage(1, []byte("core1")),
		NewExtMessage(20, []byte("ext1")),
		NewGameMessage(100, []byte("game1")),
		NewCoreMessage(8, nil),
		NewExtMessage(42, []byte("auth transfer")),
	}

	var buf bytes.Buffer
	for _, msg := range msgs {
		if err := WriteMessage(&buf, msg); err != nil {
			t.Fatalf("WriteMessage failed: %v", err)
		}
	}

	for i, want := range msgs {
		got, err := ReadMessage(&buf)
		if err != nil {
			t.Fatalf("ReadMessage %d failed: %v", i, err)
		}
		if got.CmdType != want.CmdType {
			t.Errorf("msg %d CmdType: got %d, want %d", i, got.CmdType, want.CmdType)
		}
		switch want.CmdType {
		case CmdTypeCore:
			if got.Cmd != want.Cmd {
				t.Errorf("msg %d Cmd: got %d, want %d", i, got.Cmd, want.Cmd)
			}
		case CmdTypeExtended:
			if got.ExtCmd != want.ExtCmd {
				t.Errorf("msg %d ExtCmd: got %d, want %d", i, got.ExtCmd, want.ExtCmd)
			}
		case CmdTypeGame:
			if got.GameCmd != want.GameCmd {
				t.Errorf("msg %d GameCmd: got %d, want %d", i, got.GameCmd, want.GameCmd)
			}
		}
		if !bytes.Equal(got.Data, want.Data) {
			t.Errorf("msg %d Data mismatch: got %q, want %q", i, got.Data, want.Data)
		}
	}
}

func TestDecode_BufferTooShort(t *testing.T) {
	_, err := Decode([]byte{0x01})
	if err == nil {
		t.Error("Expected error for short buffer")
	}
}

// === Helpers ===

func roundtrip(t *testing.T, msg *Message) *Message {
	t.Helper()
	buf := Encode(msg)
	defer PutBuf(buf)
	decoded, err := DecodeCopy(buf)
	if err != nil {
		t.Fatalf("Decode failed: %v", err)
	}
	return decoded
}

func assertRoundtrip(t *testing.T, msg *Message) {
	t.Helper()
	decoded := roundtrip(t, msg)
	if decoded.CmdType != msg.CmdType {
		t.Errorf("CmdType: got %d, want %d", decoded.CmdType, msg.CmdType)
	}
	switch msg.CmdType {
	case CmdTypeCore:
		if decoded.Cmd != msg.Cmd {
			t.Errorf("Cmd: got %d, want %d", decoded.Cmd, msg.Cmd)
		}
	case CmdTypeExtended:
		if decoded.ExtCmd != msg.ExtCmd {
			t.Errorf("ExtCmd: got %d, want %d", decoded.ExtCmd, msg.ExtCmd)
		}
	case CmdTypeGame:
		if decoded.GameCmd != msg.GameCmd {
			t.Errorf("GameCmd: got %d, want %d", decoded.GameCmd, msg.GameCmd)
		}
	}
	if decoded.HasSeq != msg.HasSeq {
		t.Errorf("HasSeq: got %v, want %v", decoded.HasSeq, msg.HasSeq)
	}
	if decoded.Seq != msg.Seq {
		t.Errorf("Seq: got %d, want %d", decoded.Seq, msg.Seq)
	}
	if !bytes.Equal(decoded.Data, msg.Data) {
		t.Errorf("Data mismatch")
	}
}
