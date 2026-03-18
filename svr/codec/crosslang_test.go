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

	writeFixture(t, "go_empty.bin", &Message{Cmd: 42})

	writeFixture(t, "go_hello.bin", &Message{
		Cmd: 10, HasSeq: true, Seq: 12345,
		Data: []byte("Hello from Go"),
	})

	largeData := make([]byte, 70000)
	for i := range largeData {
		largeData[i] = byte(i % 256)
	}
	writeFixture(t, "go_large.bin", &Message{
		Cmd: 5, HasSeq: true, Seq: 999,
		Data: largeData,
	})
}

func TestVerifyCSharpFixtures(t *testing.T) {
	msg1 := readFixture(t, "csharp_empty.bin")
	if msg1.Cmd != 42 {
		t.Errorf("empty.Cmd: got %d, want 42", msg1.Cmd)
	}
	if msg1.HasSeq {
		t.Error("empty should not have seq")
	}

	msg2 := readFixture(t, "csharp_hello.bin")
	if msg2.Cmd != 10 {
		t.Errorf("hello.Cmd: got %d, want 10", msg2.Cmd)
	}
	if !msg2.HasSeq || msg2.Seq != 12345 {
		t.Errorf("hello.Seq: got %d, want 12345", msg2.Seq)
	}
	if string(msg2.Data) != "Hello from C#" {
		t.Errorf("hello.Data: got %q", msg2.Data)
	}

	msg3 := readFixture(t, "csharp_large.bin")
	if msg3.Cmd != 5 || msg3.Seq != 999 || len(msg3.Data) != 70000 {
		t.Errorf("large: Cmd=%d Seq=%d DataLen=%d", msg3.Cmd, msg3.Seq, len(msg3.Data))
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
	// 直接验证 encode → decode 一致性
	msgs := []*Message{
		{Cmd: 0},
		{Cmd: 63},
		{Cmd: 10, HasSeq: true, Seq: 1},
		{Cmd: 31, Data: []byte("test")},
	}

	for _, msg := range msgs {
		buf := Encode(msg)
		decoded, err := DecodeCopy(buf)
		PutBuf(buf)
		if err != nil {
			t.Fatalf("Decode failed for %v: %v", msg, err)
		}
		if decoded.Cmd != msg.Cmd {
			t.Errorf("Cmd mismatch: %d vs %d", decoded.Cmd, msg.Cmd)
		}
		if decoded.HasSeq != msg.HasSeq || decoded.Seq != msg.Seq {
			t.Errorf("Seq mismatch")
		}
		if !bytes.Equal(decoded.Data, msg.Data) {
			t.Errorf("Data mismatch")
		}
	}
}
