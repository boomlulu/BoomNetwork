package codec

import (
	"bytes"
	"testing"
)

var benchMsg = &Message{
	Version:   0,
	Cmd:       100,
	ClientSeq: 12345,
	ServerSeq: 67890,
	Data:      []byte("Hello BoomNetwork benchmark test payload!"),
}

var benchMsgLarge = &Message{
	Version:   0,
	Cmd:       200,
	ClientSeq: 1,
	ServerSeq: 2,
	Data:      make([]byte, 1024),
}

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

func BenchmarkEncodeTo_Small(b *testing.B) {
	buf := make([]byte, 4096)
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		EncodeTo(benchMsg, buf)
	}
}

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

func BenchmarkFrameReader_Small(b *testing.B) {
	// 预编码 N 条消息到 buffer
	var buf bytes.Buffer
	for i := 0; i < 10000; i++ {
		WriteMessage(&buf, benchMsg)
	}
	data := buf.Bytes()

	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		reader := NewFrameReader(bytes.NewReader(data))
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
