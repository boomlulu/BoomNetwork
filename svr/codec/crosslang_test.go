package codec

import (
	"bytes"
	"encoding/hex"
	"os"
	"path/filepath"
	"testing"
)

var testDataDir = filepath.Join("..", "..", "testdata")

// TestGenerateGoFixtures 生成 Go 端 fixture 文件，供 C# 验证
func TestGenerateGoFixtures(t *testing.T) {
	os.MkdirAll(testDataDir, 0755)

	// Case 1: 空数据
	writeFixture(t, "go_empty.bin", &Message{
		Version: 1, Cmd: 42, ClientSeq: 10, ServerSeq: 20,
	})

	// Case 2: 有数据
	writeFixture(t, "go_hello.bin", &Message{
		Version: 0, Cmd: 100, ClientSeq: 1, ServerSeq: 2,
		Data: []byte("Hello from Go"),
	})

	// Case 3: 极值
	writeFixture(t, "go_extreme.bin", &Message{
		Version: 255, Cmd: 0xFFFFFFFF,
		ClientSeq: 0x7FFFFFFF, ServerSeq: -2147483648,
		Data: []byte{0xFF, 0x00, 0xAB, 0xCD},
	})
}

// TestVerifyCSharpFixtures 读取 C# 生成的 fixture，验证 Go 解码正确
func TestVerifyCSharpFixtures(t *testing.T) {
	// Case 1: 空数据
	msg1 := readFixture(t, "csharp_empty.bin")
	assertEqual(t, "empty.Version", msg1.Version, byte(1))
	assertEqualU32(t, "empty.Cmd", msg1.Cmd, 42)
	assertEqualI32(t, "empty.ClientSeq", msg1.ClientSeq, 10)
	assertEqualI32(t, "empty.ServerSeq", msg1.ServerSeq, 20)
	if len(msg1.Data) != 0 {
		t.Errorf("empty.Data: got len %d, want 0", len(msg1.Data))
	}

	// Case 2: 有数据
	msg2 := readFixture(t, "csharp_hello.bin")
	assertEqualU32(t, "hello.Cmd", msg2.Cmd, 100)
	if string(msg2.Data) != "Hello from C#" {
		t.Errorf("hello.Data: got %q, want %q", msg2.Data, "Hello from C#")
	}

	// Case 3: 极值
	msg3 := readFixture(t, "csharp_extreme.bin")
	assertEqual(t, "extreme.Version", msg3.Version, byte(255))
	assertEqualU32(t, "extreme.Cmd", msg3.Cmd, 0xFFFFFFFF)
	assertEqualI32(t, "extreme.ClientSeq", msg3.ClientSeq, 0x7FFFFFFF)
	assertEqualI32(t, "extreme.ServerSeq", msg3.ServerSeq, -2147483648)
	expected := []byte{0xFF, 0x00, 0xAB, 0xCD}
	if !bytes.Equal(msg3.Data, expected) {
		t.Errorf("extreme.Data: got %s, want %s", hex.EncodeToString(msg3.Data), hex.EncodeToString(expected))
	}
}

func writeFixture(t *testing.T, filename string, msg *Message) {
	t.Helper()
	data := Encode(msg)
	path := filepath.Join(testDataDir, filename)
	if err := os.WriteFile(path, data, 0644); err != nil {
		t.Fatalf("Write %s: %v", filename, err)
	}
	t.Logf("Generated %s (%d bytes)", filename, len(data))
}

func readFixture(t *testing.T, filename string) *Message {
	t.Helper()
	path := filepath.Join(testDataDir, filename)
	data, err := os.ReadFile(path)
	if err != nil {
		t.Skipf("Fixture not found: %s (run C# generate first)", path)
		return nil
	}
	msg, err := Decode(data)
	if err != nil {
		t.Fatalf("Decode %s: %v", filename, err)
	}
	return msg
}

func assertEqual(t *testing.T, name string, got, want byte) {
	t.Helper()
	if got != want {
		t.Errorf("%s: got %d, want %d", name, got, want)
	}
}

func assertEqualU32(t *testing.T, name string, got, want uint32) {
	t.Helper()
	if got != want {
		t.Errorf("%s: got %d, want %d", name, got, want)
	}
}

func assertEqualI32(t *testing.T, name string, got, want int32) {
	t.Helper()
	if got != want {
		t.Errorf("%s: got %d, want %d", name, got, want)
	}
}
