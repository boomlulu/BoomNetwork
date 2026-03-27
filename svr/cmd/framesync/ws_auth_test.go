package main

import (
	"fmt"
	"testing"

	"github.com/vmihailenco/msgpack/v5"
)

// 模拟 C# MsgPackLite 编码的信封格式：
// map { "t": "auth", "id": "", "tp": "", "p": <raw msgpack bytes of {"token":"84224155"}> }
// 关键问题：C# 的 Payload 如果被 WriteBin 包裹，Go 端解码时 RawMessage 会包含 bin 前缀

func TestDecodeAuthEnvelope_RawPayload(t *testing.T) {
	// 1. 先编码 auth payload: {"token": "84224155"}
	tokenPayload, _ := msgpack.Marshal(map[string]string{"token": "84224155"})
	t.Logf("token payload hex: %x", tokenPayload)
	t.Logf("token payload len: %d", len(tokenPayload))

	// 2. 构造正确的信封（Payload 作为 RawMessage 直接嵌入）
	correctEnv := GMEnvelope{
		Type:    "auth",
		ID:      "",
		Topic:   "",
		Payload: tokenPayload,
	}
	correctData, _ := msgpack.Marshal(&correctEnv)
	t.Logf("correct envelope hex: %x", correctData)

	// 3. 解码回来
	var decoded GMEnvelope
	if err := msgpack.Unmarshal(correctData, &decoded); err != nil {
		t.Fatalf("decode failed: %v", err)
	}
	t.Logf("decoded payload hex: %x", []byte(decoded.Payload))

	var auth AuthPayload
	if err := msgpack.Unmarshal(decoded.Payload, &auth); err != nil {
		t.Fatalf("auth decode failed: %v", err)
	}
	t.Logf("auth token: %q", auth.Token)
	if auth.Token != "84224155" {
		t.Errorf("expected 84224155, got %q", auth.Token)
	}

	// 4. 模拟 C# 错误编码：Payload 被包成 msgpack bin
	// C# MsgPackLite.WriteBin 会写 0xc4 + len + data
	// 手工构造这种错误的信封
	binWrappedPayload := make([]byte, 0, 2+len(tokenPayload))
	binWrappedPayload = append(binWrappedPayload, 0xc4, byte(len(tokenPayload)))
	binWrappedPayload = append(binWrappedPayload, tokenPayload...)

	wrongEnvMap := map[string]interface{}{
		"t":  "auth",
		"id": "",
		"tp": "",
		"p":  binWrappedPayload, // 这里 Go msgpack 会把 []byte 编码为 bin
	}
	wrongData, _ := msgpack.Marshal(wrongEnvMap)
	t.Logf("wrong envelope hex: %x", wrongData)

	var decoded2 GMEnvelope
	if err := msgpack.Unmarshal(wrongData, &decoded2); err != nil {
		t.Fatalf("decode wrong envelope failed: %v", err)
	}
	t.Logf("decoded2 payload hex: %x", []byte(decoded2.Payload))

	var auth2 AuthPayload
	err := msgpack.Unmarshal(decoded2.Payload, &auth2)
	t.Logf("auth2 decode error: %v, token: %q", err, auth2.Token)

	// 5. 模拟 C# 修复后的编码：用手工拼接 msgpack map，p 字段是 raw bytes 直接嵌入
	// 这等价于 RawMsgPack 的行为
	t.Log("=== Simulating C# fixed encoding ===")
	manualBuf := buildCSharpEnvelope("auth", "", "", tokenPayload)
	t.Logf("manual envelope hex: %x", manualBuf)

	var decoded3 GMEnvelope
	if err := msgpack.Unmarshal(manualBuf, &decoded3); err != nil {
		t.Fatalf("decode manual envelope failed: %v", err)
	}
	t.Logf("decoded3 type: %q", decoded3.Type)
	t.Logf("decoded3 payload hex: %x", []byte(decoded3.Payload))

	var auth3 AuthPayload
	if err := msgpack.Unmarshal(decoded3.Payload, &auth3); err != nil {
		t.Fatalf("auth3 decode failed: %v", err)
	}
	t.Logf("auth3 token: %q", auth3.Token)
	if auth3.Token != "84224155" {
		t.Errorf("expected 84224155, got %q", auth3.Token)
	}
}

// buildCSharpEnvelope 模拟 C# GmEnvelope.Encode() 修复后的行为：
// 手工写 msgpack map，p 字段的值是 raw msgpack bytes 直接拼入
func buildCSharpEnvelope(typ, id, topic string, payload []byte) []byte {
	buf := []byte{0x84} // fixmap with 4 entries

	// "t" -> typ
	buf = appendMsgpackStr(buf, "t")
	buf = appendMsgpackStr(buf, typ)

	// "id" -> id
	buf = appendMsgpackStr(buf, "id")
	buf = appendMsgpackStr(buf, id)

	// "tp" -> topic
	buf = appendMsgpackStr(buf, "tp")
	buf = appendMsgpackStr(buf, topic)

	// "p" -> raw payload bytes (NOT bin-wrapped)
	buf = appendMsgpackStr(buf, "p")
	if payload != nil {
		buf = append(buf, payload...)
	} else {
		buf = append(buf, 0xc0) // nil
	}

	return buf
}

func appendMsgpackStr(buf []byte, s string) []byte {
	n := len(s)
	if n <= 31 {
		buf = append(buf, byte(0xa0|n))
	} else if n <= 255 {
		buf = append(buf, 0xd9, byte(n))
	} else {
		buf = append(buf, 0xda, byte(n>>8), byte(n))
	}
	return append(buf, []byte(s)...)
}

func TestMsgEntryWire_RoomFields(t *testing.T) {
	// 验证 MsgEntryWire 新字段正确序列化/反序列化
	wire := MsgEntryWire{
		Ts: 1000, Dir: "rx", Cmd: 5, Name: "FrameInput",
		Pid: 10, Size: 64, RoomID: 3, MatchKey: "demo",
	}
	data, err := msgpack.Marshal(&wire)
	if err != nil {
		t.Fatal(err)
	}

	var decoded MsgEntryWire
	if err := msgpack.Unmarshal(data, &decoded); err != nil {
		t.Fatal(err)
	}

	if decoded.RoomID != 3 || decoded.MatchKey != "demo" {
		t.Errorf("got RoomID=%d MatchKey=%q", decoded.RoomID, decoded.MatchKey)
	}
}

func TestDecodeAuthPayload_FromCSharpMsgPackLite(t *testing.T) {
	// 模拟 C# MsgPackLite.EncodeMap({"token": "84224155"}) 的输出
	// fixmap(1) + fixstr("token") + fixstr("84224155")
	var csharpPayload []byte
	csharpPayload = append(csharpPayload, 0x81) // fixmap with 1 entry
	// key: "token" (5 chars)
	csharpPayload = append(csharpPayload, 0xa5)
	csharpPayload = append(csharpPayload, []byte("token")...)
	// value: "84224155" (8 chars)
	csharpPayload = append(csharpPayload, 0xa8)
	csharpPayload = append(csharpPayload, []byte("84224155")...)

	t.Logf("C# payload hex: %x", csharpPayload)

	// Go 端解码
	var auth AuthPayload
	err := msgpack.Unmarshal(csharpPayload, &auth)
	if err != nil {
		t.Fatalf("unmarshal failed: %v", err)
	}
	if auth.Token != "84224155" {
		t.Errorf("expected 84224155, got %q", auth.Token)
	}
	fmt.Printf("✓ Token decoded: %q\n", auth.Token)

	// 现在模拟整个信封（p 字段是 raw bytes）
	envelope := buildCSharpEnvelope("auth", "", "", csharpPayload)
	t.Logf("full envelope hex: %x", envelope)

	var env GMEnvelope
	if err := msgpack.Unmarshal(envelope, &env); err != nil {
		t.Fatalf("envelope decode failed: %v", err)
	}
	t.Logf("env.Type=%q env.Payload hex=%x", env.Type, []byte(env.Payload))

	var auth2 AuthPayload
	if err := msgpack.Unmarshal(env.Payload, &auth2); err != nil {
		t.Fatalf("auth2 decode failed: %v", err)
	}
	if auth2.Token != "84224155" {
		t.Errorf("expected 84224155, got %q", auth2.Token)
	}
	fmt.Printf("✓ Full chain decoded: %q\n", auth2.Token)
}
