package main

import (
	"context"
	"fmt"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/boom/boomnetwork/framesync"

	"github.com/gorilla/websocket"
	"github.com/vmihailenco/msgpack/v5"
)

// 模拟 C# 客户端的完整 WS 流程
func TestWSIntegration_CreateRoom(t *testing.T) {
	// 启动测试 HTTP 服务器
	token := "testtoken123"
	mux := http.NewServeMux()
	setupAdminRoutes(mux, token)
	server := httptest.NewServer(mux)
	defer server.Close()

	// WebSocket 连接
	wsURL := "ws" + strings.TrimPrefix(server.URL, "http") + "/ws"
	dialer := websocket.Dialer{}
	ws, _, err := dialer.Dial(wsURL, nil)
	if err != nil {
		t.Fatalf("dial failed: %v", err)
	}
	defer ws.Close()

	// ===== Step 1: Auth =====
	// 模拟 C# MsgPackLite 编码 auth payload
	authPayload := encodeCSharpMap(map[string]interface{}{"token": token})
	authEnv := buildCSharpEnvelope("auth", "", "", authPayload)

	if err := ws.WriteMessage(websocket.BinaryMessage, authEnv); err != nil {
		t.Fatalf("send auth failed: %v", err)
	}

	// 读 auth_ok
	_, rspData, err := ws.ReadMessage()
	if err != nil {
		t.Fatalf("read auth response failed: %v", err)
	}
	var rspEnv GMEnvelope
	if err := msgpack.Unmarshal(rspData, &rspEnv); err != nil {
		t.Fatalf("decode auth response failed: %v", err)
	}
	if rspEnv.Type != "auth_ok" {
		t.Fatalf("expected auth_ok, got %q", rspEnv.Type)
	}
	t.Log("✓ Auth OK")

	// ===== Step 2: Subscribe rooms =====
	subEnv := buildCSharpEnvelope("sub", "", "rooms", nil)
	if err := ws.WriteMessage(websocket.BinaryMessage, subEnv); err != nil {
		t.Fatalf("send sub failed: %v", err)
	}
	t.Log("✓ Subscribed to rooms")

	// ===== Step 3: Send create_room RPC =====
	rpcPayload := encodeCSharpMap(map[string]interface{}{
		"max_players": 4,
		"match_key":   "test-room",
	})
	rpcEnv := buildCSharpEnvelope("rpc", "rpc-001", "create_room", rpcPayload)
	t.Logf("RPC envelope hex: %x", rpcEnv)

	if err := ws.WriteMessage(websocket.BinaryMessage, rpcEnv); err != nil {
		t.Fatalf("send rpc failed: %v", err)
	}
	t.Log("✓ Sent create_room RPC")

	// ===== Step 4: Read RPC response =====
	ws.SetReadDeadline(time.Now().Add(5 * time.Second))
	_, rpcRspData, err := ws.ReadMessage()
	if err != nil {
		t.Fatalf("read rpc response failed: %v", err)
	}
	var rpcRspEnv GMEnvelope
	if err := msgpack.Unmarshal(rpcRspData, &rpcRspEnv); err != nil {
		t.Fatalf("decode rpc response failed: %v", err)
	}
	t.Logf("RPC response: type=%q id=%q topic=%q payloadHex=%x",
		rpcRspEnv.Type, rpcRspEnv.ID, rpcRspEnv.Topic, []byte(rpcRspEnv.Payload))

	if rpcRspEnv.Type == "err" {
		var errResult ErrorResult
		msgpack.Unmarshal(rpcRspEnv.Payload, &errResult)
		t.Fatalf("RPC error: %s", errResult.Error)
	}

	if rpcRspEnv.Type != "rsp" {
		t.Fatalf("expected rsp, got %q", rpcRspEnv.Type)
	}

	var result CreateRoomResult
	if err := msgpack.Unmarshal(rpcRspEnv.Payload, &result); err != nil {
		t.Fatalf("decode create room result failed: %v", err)
	}
	if !result.Ok || result.RoomID <= 0 {
		t.Fatalf("create room failed: ok=%v roomId=%d", result.Ok, result.RoomID)
	}
	t.Logf("✓ Room created: id=%d", result.RoomID)

	// Verify room exists
	room := roomMgr.GetRoom(result.RoomID)
	if room == nil {
		t.Fatal("room not found in manager")
	}
	t.Logf("✓ Room verified: id=%d maxPlayers=%d matchKey=%q", room.ID, room.MaxPlayers(), room.MatchKey)
}

// encodeCSharpMap 模拟 C# MsgPackLite.EncodeMap
func encodeCSharpMap(m map[string]interface{}) []byte {
	n := len(m)
	var buf []byte
	if n <= 15 {
		buf = append(buf, byte(0x80|n))
	} else {
		buf = append(buf, 0xde, byte(n>>8), byte(n))
	}
	for k, v := range m {
		buf = appendMsgpackStr(buf, k)
		buf = appendMsgpackValue(buf, v)
	}
	return buf
}

func appendMsgpackValue(buf []byte, v interface{}) []byte {
	switch val := v.(type) {
	case nil:
		return append(buf, 0xc0)
	case string:
		return appendMsgpackStr(buf, val)
	case int:
		if val >= 0 && val <= 127 {
			return append(buf, byte(val))
		}
		if val >= -32 && val < 0 {
			return append(buf, byte(0xe0|(val&0x1f)))
		}
		// int32
		buf = append(buf, 0xd2)
		buf = append(buf, byte(val>>24), byte(val>>16), byte(val>>8), byte(val))
		return buf
	default:
		return append(buf, 0xc0) // nil fallback
	}
}

func setupAdminRoutes(mux *http.ServeMux, token string) {
	// 初始化全局 roomMgr（测试环境）
	initTestGlobals()
	hub := newTestGMHub()
	go hub.Run()
	mux.HandleFunc("/ws", hub.HandleUpgrade(token))
}

var testGlobalsOnce sync.Once

func initTestGlobals() {
	testGlobalsOnce.Do(func() {
		roomMgr = framesync.NewRoomManager()
	})
}

func newTestGMHub() *GMHub {
	ctx := context.Background()
	return newGMHub(ctx)
}

func TestWSIntegration_RPCEncoding(t *testing.T) {
	// 验证 C# 编码的 RPC envelope 能被 Go 正确解码
	payload := encodeCSharpMap(map[string]interface{}{
		"max_players": 4,
		"match_key":   "demo",
	})
	envelope := buildCSharpEnvelope("rpc", "test-id", "create_room", payload)

	t.Logf("Envelope hex: %x", envelope)
	t.Logf("Envelope len: %d", len(envelope))

	var env GMEnvelope
	if err := msgpack.Unmarshal(envelope, &env); err != nil {
		t.Fatalf("unmarshal failed: %v", err)
	}

	t.Logf("Type=%q ID=%q Topic=%q PayloadLen=%d PayloadHex=%x",
		env.Type, env.ID, env.Topic, len(env.Payload), []byte(env.Payload))

	if env.Type != "rpc" {
		t.Errorf("expected type=rpc, got %q", env.Type)
	}
	if env.Topic != "create_room" {
		t.Errorf("expected topic=create_room, got %q", env.Topic)
	}
	if env.ID != "test-id" {
		t.Errorf("expected id=test-id, got %q", env.ID)
	}

	// 解码 payload
	var p CreateRoomPayload
	if err := msgpack.Unmarshal(env.Payload, &p); err != nil {
		t.Fatalf("payload unmarshal failed: %v (hex: %x)", err, []byte(env.Payload))
	}
	if p.MaxPlayers != 4 {
		t.Errorf("expected max_players=4, got %d", p.MaxPlayers)
	}
	if p.MatchKey != "demo" {
		t.Errorf("expected match_key=demo, got %q", p.MatchKey)
	}

	fmt.Printf("✓ RPC decoded: MaxPlayers=%d MatchKey=%q\n", p.MaxPlayers, p.MatchKey)
}
