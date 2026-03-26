package codec

import (
	"bytes"
	"os"
	"path/filepath"
	"testing"
)

var testDataDir = filepath.Join("..", "..", "testdata")

func TestGenerateGoFixtures(t *testing.T) {
	os.MkdirAll(testDataDir, 0755)

	writeFixture(t, "go_core.bin", NewCoreMessage(7, nil))

	msg := NewCoreMessage(10, []byte("Hello from Go"))
	msg.HasSeq = true
	msg.Seq = 12345
	writeFixture(t, "go_core_seq.bin", msg)

	writeFixture(t, "go_ext.bin", NewExtMessage(42, []byte("ext data")))

	writeFixture(t, "go_game.bin", NewGameMessage(1001, []byte("game data")))

	largeData := make([]byte, 70000)
	for i := range largeData {
		largeData[i] = byte(i % 256)
	}
	lmsg := NewCoreMessage(5, largeData)
	lmsg.HasSeq = true
	lmsg.Seq = 999
	writeFixture(t, "go_large.bin", lmsg)
}

func TestVerifyCSharpFixtures(t *testing.T) {
	msg1 := readFixture(t, "csharp_core.bin")
	if msg1 == nil {
		return
	}
	if msg1.CmdType != CmdTypeCore || msg1.Cmd != 7 {
		t.Errorf("core: CmdType=%d Cmd=%d", msg1.CmdType, msg1.Cmd)
	}

	msg2 := readFixture(t, "csharp_ext.bin")
	if msg2 == nil {
		return
	}
	if msg2.CmdType != CmdTypeExtended || msg2.ExtCmd != 42 {
		t.Errorf("ext: CmdType=%d ExtCmd=%d", msg2.CmdType, msg2.ExtCmd)
	}

	msg3 := readFixture(t, "csharp_game.bin")
	if msg3 == nil {
		return
	}
	if msg3.CmdType != CmdTypeGame || msg3.GameCmd != 1001 {
		t.Errorf("game: CmdType=%d GameCmd=%d", msg3.CmdType, msg3.GameCmd)
	}
}

func writeFixture(t *testing.T, filename string, msg *Message) {
	t.Helper()
	data := Encode(msg)
	defer PutBuf(data)
	path := filepath.Join(testDataDir, filename)
	cp := make([]byte, len(data))
	copy(cp, data)
	if err := os.WriteFile(path, cp, 0644); err != nil {
		t.Fatalf("Write %s: %v", filename, err)
	}
	t.Logf("Generated %s (%d bytes)", filename, len(data))
}

func readFixture(t *testing.T, filename string) *Message {
	t.Helper()
	path := filepath.Join(testDataDir, filename)
	data, err := os.ReadFile(path)
	if err != nil {
		t.Skipf("Fixture not found: %s", path)
		return nil
	}
	msg, err := DecodeCopy(data)
	if err != nil {
		t.Fatalf("Decode %s: %v", filename, err)
	}
	return msg
}

func TestCrossLanguageRoundTrip(t *testing.T) {
	msgs := []*Message{
		NewCoreMessage(0, nil),
		NewCoreMessage(15, nil),
		NewExtMessage(100, []byte("test")),
		NewGameMessage(0xDEADBEEF, []byte("beef")),
	}
	msgs[2].HasSeq = true
	msgs[2].Seq = 1

	for _, msg := range msgs {
		buf := Encode(msg)
		decoded, err := DecodeCopy(buf)
		PutBuf(buf)
		if err != nil {
			t.Fatalf("Decode failed for %v: %v", msg, err)
		}
		if decoded.CmdType != msg.CmdType {
			t.Errorf("CmdType mismatch: %d vs %d", decoded.CmdType, msg.CmdType)
		}
		if !bytes.Equal(decoded.Data, msg.Data) {
			t.Errorf("Data mismatch for %v", msg)
		}
	}
}
