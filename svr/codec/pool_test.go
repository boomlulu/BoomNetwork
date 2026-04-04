package codec

// pool_test.go — P1-1 Message Pool 单元测试 + Benchmark
//
// 覆盖范围:
//   P1-1a  DecodePooled vs Decode 结果一致性
//   P1-1b  PutMessage 清零所有字段（防悬空引用）
//   P1-1c  GetMessage / PutMessage Pool 复用（无 panic，无竞争）
//   P1-1d  DecodePooled 错误路径归还 Message（不泄漏）
//   Bench  Decode（每次新分配）vs DecodePooled（Pool 复用）allocs/op 对比

import (
	"bytes"
	"testing"
)

// ─── 辅助 ────────────────────────────────────────────────────────────────────

func encodeMsg(t *testing.T, msg *Message) []byte {
	t.Helper()
	buf := Encode(msg)
	return buf
}

// ─── P1-1a: DecodePooled 与 Decode 结果一致 ─────────────────────────────────

func TestDecodePooled_Core_MatchesDecode(t *testing.T) {
	orig := &Message{CmdType: CmdTypeCore, Cmd: 7, HasSeq: true, Seq: 42, Data: []byte{1, 2, 3}}
	encoded := encodeMsg(t, orig)
	defer PutBuf(encoded)

	got1, err1 := Decode(encoded)
	got2, err2 := DecodePooled(encoded)
	if err2 != nil {
		defer PutMessage(got2)
	}

	if err1 != nil || err2 != nil {
		t.Fatalf("decode errors: Decode=%v, DecodePooled=%v", err1, err2)
	}
	defer PutMessage(got2)

	if got1.CmdType != got2.CmdType {
		t.Errorf("CmdType: want %d, got %d", got1.CmdType, got2.CmdType)
	}
	if got1.Cmd != got2.Cmd {
		t.Errorf("Cmd: want %d, got %d", got1.Cmd, got2.Cmd)
	}
	if got1.Seq != got2.Seq {
		t.Errorf("Seq: want %d, got %d", got1.Seq, got2.Seq)
	}
	if got1.HasSeq != got2.HasSeq {
		t.Errorf("HasSeq: want %v, got %v", got1.HasSeq, got2.HasSeq)
	}
	if !bytes.Equal(got1.Data, got2.Data) {
		t.Errorf("Data: want %v, got %v", got1.Data, got2.Data)
	}
}

func TestDecodePooled_Extended_MatchesDecode(t *testing.T) {
	orig := &Message{CmdType: CmdTypeExtended, ExtCmd: 0x1234, Data: []byte("hello")}
	encoded := encodeMsg(t, orig)
	defer PutBuf(encoded)

	got1, _ := Decode(encoded)
	got2, err := DecodePooled(encoded)
	if err != nil {
		t.Fatalf("DecodePooled error: %v", err)
	}
	defer PutMessage(got2)

	if got2.ExtCmd != got1.ExtCmd {
		t.Errorf("ExtCmd: want %d, got %d", got1.ExtCmd, got2.ExtCmd)
	}
	if !bytes.Equal(got2.Data, got1.Data) {
		t.Errorf("Data mismatch")
	}
}

func TestDecodePooled_Game_MatchesDecode(t *testing.T) {
	orig := &Message{CmdType: CmdTypeGame, GameCmd: 0xDEADBEEF}
	encoded := encodeMsg(t, orig)
	defer PutBuf(encoded)

	got1, _ := Decode(encoded)
	got2, err := DecodePooled(encoded)
	if err != nil {
		t.Fatalf("DecodePooled error: %v", err)
	}
	defer PutMessage(got2)

	if got2.GameCmd != got1.GameCmd {
		t.Errorf("GameCmd: want %d, got %d", got1.GameCmd, got2.GameCmd)
	}
}

// ─── P1-1b: PutMessage 清零所有字段 ─────────────────────────────────────────

func TestPutMessage_ClearsAllFields(t *testing.T) {
	orig := &Message{
		CmdType: CmdTypeExtended,
		Cmd:     5,
		ExtCmd:  9999,
		GameCmd: 0xCAFE,
		Seq:     77,
		HasSeq:  true,
		Data:    []byte{0xFF, 0xEE},
	}
	encoded := encodeMsg(t, orig)
	defer PutBuf(encoded)

	msg, err := DecodePooled(encoded)
	if err != nil {
		t.Fatalf("decode: %v", err)
	}

	PutMessage(msg)

	// 归还后字段应全部为零值（PutMessage 执行 *m = Message{}）
	if msg.CmdType != 0 {
		t.Errorf("CmdType after Put: want 0, got %d", msg.CmdType)
	}
	if msg.ExtCmd != 0 {
		t.Errorf("ExtCmd after Put: want 0, got %d", msg.ExtCmd)
	}
	if msg.Data != nil {
		t.Errorf("Data after Put: want nil, got %v", msg.Data)
	}
	if msg.HasSeq {
		t.Error("HasSeq after Put: want false")
	}
}

// ─── P1-1c: Pool 复用，无 panic，无竞争 ──────────────────────────────────────

func TestMessagePool_GetPut_NoPanic(t *testing.T) {
	for i := 0; i < 100; i++ {
		m := GetMessage()
		m.Cmd = byte(i % 16)
		m.Seq = int32(i)
		PutMessage(m)
	}
}

func TestMessagePool_Concurrent_NoPanic(t *testing.T) {
	done := make(chan struct{})
	for i := 0; i < 32; i++ {
		go func() {
			defer func() { done <- struct{}{} }()
			for j := 0; j < 200; j++ {
				m := GetMessage()
				m.Cmd = 3
				PutMessage(m)
			}
		}()
	}
	for i := 0; i < 32; i++ {
		<-done
	}
}

// ─── P1-1d: DecodePooled 错误路径归还 Message ────────────────────────────────

// 当解码失败时，DecodePooled 应将 Message 归还 Pool（不泄漏），且返回 nil
func TestDecodePooled_ErrorPath_ReturnsNil(t *testing.T) {
	// 过短的 buffer，触发 "buffer too short" 错误
	msg, err := DecodePooled([]byte{0x00})
	if err == nil {
		t.Error("expected error for too-short buffer")
		if msg != nil {
			PutMessage(msg)
		}
	}
	if msg != nil {
		t.Errorf("expected nil msg on error, got %v", msg)
	}
}

// ─── Benchmarks ──────────────────────────────────────────────────────────────

var benchDecodeData []byte

func init() {
	benchDecodeData = Encode(&Message{
		CmdType: CmdTypeCore,
		Cmd:     5,
		HasSeq:  true,
		Seq:     12345,
		Data:    []byte("BoomNetwork benchmark payload 32B!!"),
	})
}

// BenchmarkDecode_Allocating 基准：每次调用分配新 *Message（原始路径）
func BenchmarkDecode_Allocating(b *testing.B) {
	b.ReportAllocs()
	for i := 0; i < b.N; i++ {
		msg, _ := Decode(benchDecodeData)
		_ = msg
	}
}

// BenchmarkDecodePooled_Reuse 基准：DecodePooled + PutMessage（Pool 复用路径）
// 期望：allocs/op = 0（稳态，Pool 命中后无分配）
func BenchmarkDecodePooled_Reuse(b *testing.B) {
	b.ReportAllocs()
	for i := 0; i < b.N; i++ {
		msg, _ := DecodePooled(benchDecodeData)
		PutMessage(msg)
	}
}

// BenchmarkDecodePooled_Parallel 并发基准
func BenchmarkDecodePooled_Parallel(b *testing.B) {
	b.ReportAllocs()
	b.RunParallel(func(pb *testing.PB) {
		for pb.Next() {
			msg, _ := DecodePooled(benchDecodeData)
			PutMessage(msg)
		}
	})
}
