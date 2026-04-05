package transport

import (
	"net"
	"sync/atomic"
	"testing"
	"time"

	"github.com/boomlulu/boomnetwork/codec"
	"github.com/gorilla/websocket"
)

// buildTestFrame 构造一条最简 Core 消息帧（Type=0, Cmd=1, 无数据）。
// 格式：[4B big-endian 总长][1B type][1B cmd][2B extCmd=0][2B reserved=0]
func buildTestFrame() []byte {
	// BoomNetwork wire format: 4-byte length-prefix (big-endian) + payload
	// Minimum Core message payload: type(1) + cmd(1) = 2 bytes
	payload := []byte{
		0x00, // MsgType = Core
		0x01, // Cmd = 1 (Heartbeat or any valid cmd)
		0x00, 0x00, // ExtCmd = 0
		0x00, 0x00, // seq / flags
	}
	frame := make([]byte, 4+len(payload))
	frame[0] = 0
	frame[1] = 0
	frame[2] = 0
	frame[3] = byte(len(payload))
	copy(frame[4:], payload)
	return frame
}

// TestWsServer_Connect 验证 WsServer 能接受 WebSocket 连接并调用 handler。
// 模拟 WebGL 客户端（BoomNetworkWS.jslib）通过二进制 WebSocket 接入。
func TestWsServer_Connect(t *testing.T) {
	var msgCount int32

	srv := NewWsServer(func(conn *Conn, msg *codec.Message) {
		atomic.AddInt32(&msgCount, 1)
	})

	// 使用 :0 让 OS 分配端口
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("listen: %v", err)
	}
	addr := ln.Addr().String()
	ln.Close()

	if err := srv.Listen(addr); err != nil {
		t.Fatalf("WsServer.Listen: %v", err)
	}
	defer srv.Close()

	time.Sleep(20 * time.Millisecond)

	dialer := websocket.Dialer{HandshakeTimeout: 2 * time.Second}
	wsConn, _, err := dialer.Dial("ws://"+addr+"/", nil)
	if err != nil {
		t.Fatalf("dial: %v", err)
	}
	defer wsConn.Close()

	// 发送一条合法的 BoomNetwork 二进制帧
	if err := wsConn.WriteMessage(websocket.BinaryMessage, buildTestFrame()); err != nil {
		t.Fatalf("send: %v", err)
	}

	// 等待 handler 被调用
	deadline := time.Now().Add(1 * time.Second)
	for time.Now().Before(deadline) {
		if atomic.LoadInt32(&msgCount) > 0 {
			t.Log("handler invoked ✓")
			return
		}
		time.Sleep(10 * time.Millisecond)
	}
	t.Fatal("timeout: handler was not invoked within 1s")
}

// TestWsServer_MaxConns 验证超过最大连接数时新连接被服务器关闭。
func TestWsServer_MaxConns(t *testing.T) {
	srv := NewWsServer(func(conn *Conn, msg *codec.Message) {})
	srv.SetMaxConns(1)

	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("listen: %v", err)
	}
	addr := ln.Addr().String()
	ln.Close()

	if err := srv.Listen(addr); err != nil {
		t.Fatalf("WsServer.Listen: %v", err)
	}
	defer srv.Close()

	time.Sleep(20 * time.Millisecond)

	dialer := websocket.Dialer{HandshakeTimeout: 2 * time.Second}

	// 第一个连接 — 应该成功
	ws1, _, err := dialer.Dial("ws://"+addr+"/", nil)
	if err != nil {
		t.Fatalf("first dial failed: %v", err)
	}
	defer ws1.Close()

	time.Sleep(30 * time.Millisecond)

	// 第二个连接 — maxConns=1 已满，服务器应关闭连接
	ws2, resp2, err := dialer.Dial("ws://"+addr+"/", nil)
	if err != nil {
		t.Logf("second dial rejected at handshake (expected): %v status=%v ✓", err, resp2)
		return
	}
	defer ws2.Close()

	// 如果 Upgrade 成功，等待服务器主动关闭
	ws2.SetReadDeadline(time.Now().Add(500 * time.Millisecond))
	_, _, readErr := ws2.ReadMessage()
	if readErr == nil {
		t.Error("expected server to close second connection, but it stayed open")
	} else {
		t.Logf("server closed second connection: %v ✓", readErr)
	}
}

// TestWsServer_ConnCount 验证连接计数正确跟踪连接/断开。
func TestWsServer_ConnCount(t *testing.T) {
	srv := NewWsServer(func(conn *Conn, msg *codec.Message) {})

	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("listen: %v", err)
	}
	addr := ln.Addr().String()
	ln.Close()

	if err := srv.Listen(addr); err != nil {
		t.Fatalf("WsServer.Listen: %v", err)
	}
	defer srv.Close()

	time.Sleep(20 * time.Millisecond)

	if got := srv.ConnCount(); got != 0 {
		t.Fatalf("initial ConnCount = %d, want 0", got)
	}

	dialer := websocket.Dialer{HandshakeTimeout: 2 * time.Second}
	ws, _, err := dialer.Dial("ws://"+addr+"/", nil)
	if err != nil {
		t.Fatalf("dial: %v", err)
	}

	time.Sleep(20 * time.Millisecond)
	if got := srv.ConnCount(); got != 1 {
		t.Fatalf("ConnCount after connect = %d, want 1", got)
	}

	ws.Close()
	time.Sleep(50 * time.Millisecond)
	if got := srv.ConnCount(); got != 0 {
		t.Fatalf("ConnCount after disconnect = %d, want 0", got)
	}
}
