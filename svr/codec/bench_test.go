package codec

import (
	"bytes"
	"io"
	"testing"
)

var benchMsg = &Message{
	Cmd:    10,
	HasSeq: true,
	Seq:    12345,
	Data:   []byte("Hello BoomNetwork benchmark test payload!"),
}

var benchMsgLarge = &Message{
	Cmd:  20,
	Data: make([]byte, 1024),
}

// --- Encode (pool-backed, 热路径含 Pool 开销) ---

func BenchmarkEncode_Small(b *testing.B) {
	b.ReportAllocs()
	for i := 0; i < b.N; i++ {
		buf := Encode(benchMsg)
		PutBuf(buf)
	}
}

func BenchmarkEncode_Large(b *testing.B) {
	b.ReportAllocs()
	for i := 0; i < b.N; i++ {
		buf := Encode(benchMsgLarge)
		PutBuf(buf)
	}
}

// --- EncodeTo (预分配 buffer，零分配热路径，与 C# Encode(msg, buf) 等价) ---

func BenchmarkEncodeTo_Small(b *testing.B) {
	buf := make([]byte, 4096)
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		EncodeTo(benchMsg, buf)
	}
}

func BenchmarkEncodeTo_Large(b *testing.B) {
	buf := make([]byte, 4096)
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		EncodeTo(benchMsgLarge, buf)
	}
}

// --- Decode (每次 new *Message，基线) ---

func BenchmarkDecode_Small(b *testing.B) {
	encoded := Encode(benchMsg)
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		Decode(encoded)
	}
}

func BenchmarkDecode_Large(b *testing.B) {
	encoded := Encode(benchMsgLarge)
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		Decode(encoded)
	}
}

// --- DecodePooled (Pool 复用，零分配，与 C# ArrayPool 路径等价) ---

func BenchmarkDecodePooled_Small(b *testing.B) {
	encoded := Encode(benchMsg)
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		msg, _ := DecodePooled(encoded)
		PutMessage(msg)
	}
}

func BenchmarkDecodePooled_Large(b *testing.B) {
	encoded := Encode(benchMsgLarge)
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		msg, _ := DecodePooled(encoded)
		PutMessage(msg)
	}
}

// --- FrameReader / FrameWriter (10000 条消息端到端吞吐) ---
// NewFrameReader 在 ResetTimer 之前创建，每轮只 Reset，不重新 new。

func BenchmarkFrameReader_Small(b *testing.B) {
	var buf bytes.Buffer
	for i := 0; i < 10000; i++ {
		WriteMessage(&buf, benchMsg)
	}
	data := buf.Bytes()

	src := bytes.NewReader(data)
	reader := NewFrameReader(src)

	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		src.Seek(0, io.SeekStart)
		reader.Reset(src)
		for j := 0; j < 10000; j++ {
			reader.ReadMessage()
		}
	}
}

func BenchmarkFrameWriter_Small(b *testing.B) {
	var buf bytes.Buffer
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		buf.Reset()
		fw := NewFrameWriter(&buf)
		for j := 0; j < 10000; j++ {
			fw.WriteMessage(benchMsg)
		}
		fw.Flush()
	}
}
