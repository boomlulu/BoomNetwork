// Package boomnetwork_test — Go 集成测试，模拟 C# 客户端完成连接建立全流程。
//
// 流程：
//  1. 建立 TCP 连接（in-process，无外部进程依赖）
//  2. 发送 SessionBind（CmdType=Core, Cmd=0x01），携带 playerId
//  3. 等待并解析 SessionBindRsp，验证 playerId 回显正确
//  4. 发送 CreateRoom（CmdType=Extended, ExtCmd=3），验证响应包含 roomId
//  5. 发送 JoinRoom（CmdType=Extended, ExtCmd=5），验证加入成功响应
//  6. 每个响应均验证 FlagsCmd 解码正确（CmdType / Cmd / ExtCmd）
package boomnetwork_test

import (
	"encoding/binary"
	"fmt"
	"net"
	"sync/atomic"
	"testing"
	"time"

	"github.com/boomlulu/boomnetwork/codec"
	"github.com/boomlulu/boomnetwork/framesync"
)

// testPlayerCounter 用于测试内 playerId 自增（与 cmd/framesync/main.go 中的全局计数器无关）
var testPlayerCounter int64

func nextTestPlayerId() int32 {
	return int32(atomic.AddInt64(&testPlayerCounter, 1))
}

// serveTestConn 模拟服务器端连接处理：SessionBind → CreateRoom → JoinRoom
// 收到 JoinRoom 并发送响应后关闭连接（流程结束）。
func serveTestConn(conn net.Conn, roomMgr *framesync.RoomManager, t *testing.T) {
	defer conn.Close()
	conn.SetDeadline(time.Now().Add(5 * time.Second))

	r := codec.NewFrameReader(conn, 0)
	w := codec.NewFrameWriter(conn)

	var playerId int32

	for {
		msg, err := r.ReadMessageCopy()
		if err != nil {
			// 连接关闭或超时，退出
			return
		}

		switch {
		case msg.CmdType == codec.CmdTypeCore && msg.Cmd == framesync.CmdSessionBind:
			// 验证 FlagsCmd 解码（服务器侧检查请求）：CmdType=Core, Cmd=CmdSessionBind(1)
			// codec.Decode 已确保 CmdType 和 Cmd 正确解析，此处仅记录
			t.Logf("[server] SessionBind received: CmdType=%d Cmd=%d DataLen=%d",
				msg.CmdType, msg.Cmd, len(msg.Data))

			// 服务器分配新 playerId（不采用客户端提交的值，保持与真实服务器一致）
			playerId = nextTestPlayerId()

			rspData := make([]byte, 4)
			binary.LittleEndian.PutUint32(rspData, uint32(playerId))
			rsp := codec.NewCoreMessage(framesync.CmdSessionBindRsp, rspData)
			if err := w.WriteMessage(rsp); err != nil {
				t.Logf("[server] write SessionBindRsp error: %v", err)
				return
			}
			if err := w.Flush(); err != nil {
				t.Logf("[server] flush SessionBindRsp error: %v", err)
				return
			}
			t.Logf("[server] SessionBindRsp sent: playerId=%d", playerId)

		case msg.CmdType == codec.CmdTypeExtended && msg.ExtCmd == framesync.ExtCmdCreateRoom:
			t.Logf("[server] CreateRoom received: CmdType=%d ExtCmd=%d DataLen=%d",
				msg.CmdType, msg.ExtCmd, len(msg.Data))

			// 解析 MaxPlayers（客户端编码：[MaxPlayers:2][MatchKeyLen:2][MatchKey:N]）
			maxPlayers := 2
			if len(msg.Data) >= 2 {
				maxPlayers = int(binary.LittleEndian.Uint16(msg.Data[0:2]))
			}
			matchKey := ""
			if len(msg.Data) >= 4 {
				mkLen := int(binary.LittleEndian.Uint16(msg.Data[2:4]))
				if mkLen > 0 && len(msg.Data) >= 4+mkLen {
					matchKey = string(msg.Data[4 : 4+mkLen])
				}
			}

			room := roomMgr.CreateRoomWithMaxPlayers(maxPlayers, matchKey)
			if room == nil {
				// 创建失败，返回 roomId=0
				rspData := make([]byte, 4)
				rsp := codec.NewExtMessage(framesync.ExtCmdCreateRoomRsp, rspData)
				w.WriteMessage(rsp)
				w.Flush()
				t.Logf("[server] CreateRoom failed (room manager returned nil)")
				return
			}

			rspData := make([]byte, 4)
			binary.LittleEndian.PutUint32(rspData, uint32(room.ID))
			rsp := codec.NewExtMessage(framesync.ExtCmdCreateRoomRsp, rspData)
			if err := w.WriteMessage(rsp); err != nil {
				t.Logf("[server] write CreateRoomRsp error: %v", err)
				return
			}
			if err := w.Flush(); err != nil {
				t.Logf("[server] flush CreateRoomRsp error: %v", err)
				return
			}
			t.Logf("[server] CreateRoomRsp sent: roomId=%d", room.ID)

		case msg.CmdType == codec.CmdTypeExtended && msg.ExtCmd == framesync.ExtCmdJoinRoom:
			t.Logf("[server] JoinRoom received: CmdType=%d ExtCmd=%d DataLen=%d",
				msg.CmdType, msg.ExtCmd, len(msg.Data))

			if len(msg.Data) < 4 {
				errData := framesync.EncodeJoinRoomError(framesync.JoinRoomBadData)
				rsp := codec.NewExtMessage(framesync.ExtCmdJoinRoomRsp, errData)
				w.WriteMessage(rsp)
				w.Flush()
				t.Logf("[server] JoinRoom bad data")
				return
			}
			roomId := int32(binary.LittleEndian.Uint32(msg.Data[0:4]))
			room := roomMgr.GetRoom(roomId)
			if room == nil {
				errData := framesync.EncodeJoinRoomError(framesync.JoinRoomNotFound)
				rsp := codec.NewExtMessage(framesync.ExtCmdJoinRoomRsp, errData)
				w.WriteMessage(rsp)
				w.Flush()
				t.Logf("[server] JoinRoom room not found: roomId=%d", roomId)
				return
			}

			// 成功响应：EncodeJoinRoomRsp(playerId, roomId, existingPlayers=[])
			successData := framesync.EncodeJoinRoomRsp(playerId, roomId, []int32{})
			rsp := codec.NewExtMessage(framesync.ExtCmdJoinRoomRsp, successData)
			if err := w.WriteMessage(rsp); err != nil {
				t.Logf("[server] write JoinRoomRsp error: %v", err)
				return
			}
			if err := w.Flush(); err != nil {
				t.Logf("[server] flush JoinRoomRsp error: %v", err)
				return
			}
			t.Logf("[server] JoinRoomRsp sent: playerId=%d roomId=%d", playerId, roomId)
			// 流程完成，退出
			return

		default:
			t.Logf("[server] unexpected message: CmdType=%d Cmd=%d ExtCmd=%d", msg.CmdType, msg.Cmd, msg.ExtCmd)
		}
	}
}

// TestCSharpClientConnect 模拟 C# 客户端完成连接建立全流程：
// SessionBind → CreateRoom → JoinRoom，验证每步的 FlagsCmd 解码和业务字段正确。
func TestCSharpClientConnect(t *testing.T) {
	// ── 启动 in-process TCP 服务器 ─────────────────────────────────────────────
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("net.Listen: %v", err)
	}
	defer ln.Close()

	roomMgr := framesync.NewRoomManager()
	serverDone := make(chan struct{})

	go func() {
		defer close(serverDone)
		conn, err := ln.Accept()
		if err != nil {
			// Listener closed, normal shutdown
			return
		}
		serveTestConn(conn, roomMgr, t)
	}()

	// ── 客户端连接 ─────────────────────────────────────────────────────────────
	clientConn, err := net.Dial("tcp", ln.Addr().String())
	if err != nil {
		t.Fatalf("net.Dial: %v", err)
	}
	defer clientConn.Close()
	clientConn.SetDeadline(time.Now().Add(5 * time.Second))

	r := codec.NewFrameReader(clientConn, 0)
	w := codec.NewFrameWriter(clientConn)

	// ── Step 1: SessionBind ────────────────────────────────────────────────────
	// Wire: CmdType=Core(0), Cmd=CmdSessionBind(1), Data=[PlayerId:4]
	// FlagsCmd = (Cmd & 0x0F) << 4 | 0 = 0x10（无 HasSeq，无 LenSize4）
	const clientPlayerId = int32(100) // 客户端希望绑定的 ID（服务器可能忽略并重新分配）
	bindData := make([]byte, 4)
	binary.LittleEndian.PutUint32(bindData, uint32(clientPlayerId))

	bindMsg := codec.NewCoreMessage(framesync.CmdSessionBind, bindData)
	if err := w.WriteMessage(bindMsg); err != nil {
		t.Fatalf("write SessionBind: %v", err)
	}
	if err := w.Flush(); err != nil {
		t.Fatalf("flush SessionBind: %v", err)
	}
	t.Logf("[client] SessionBind sent: clientPlayerId=%d", clientPlayerId)

	// 读取 SessionBindRsp
	rsp1, err := r.ReadMessageCopy()
	if err != nil {
		t.Fatalf("read SessionBindRsp: %v", err)
	}

	// 验证 FlagsCmd 解码：CmdType=Core(0), Cmd=CmdSessionBindRsp(2)
	if rsp1.CmdType != codec.CmdTypeCore {
		t.Fatalf("SessionBindRsp FlagsCmd: expected CmdType=Core(%d), got %d",
			codec.CmdTypeCore, rsp1.CmdType)
	}
	if rsp1.Cmd != framesync.CmdSessionBindRsp {
		t.Fatalf("SessionBindRsp FlagsCmd: expected Cmd=CmdSessionBindRsp(%d), got %d",
			framesync.CmdSessionBindRsp, rsp1.Cmd)
	}

	// 验证 playerId 回显（服务器分配的 playerId，不为 0）
	if len(rsp1.Data) < 4 {
		t.Fatalf("SessionBindRsp data too short: %d bytes", len(rsp1.Data))
	}
	assignedPlayerId := int32(binary.LittleEndian.Uint32(rsp1.Data[0:4]))
	if assignedPlayerId == 0 {
		t.Fatal("SessionBindRsp: playerId=0 indicates auth failure or server error")
	}
	t.Logf("[client] ✓ SessionBindRsp: CmdType=%d Cmd=%d assignedPlayerId=%d",
		rsp1.CmdType, rsp1.Cmd, assignedPlayerId)

	// ── Step 2: CreateRoom ─────────────────────────────────────────────────────
	// Wire: CmdType=Extended(1), ExtCmd=ExtCmdCreateRoom(3)
	// Data: [MaxPlayers:2][MatchKeyLen:2]
	// FlagsCmd = (CmdTypeExtended & 0x03) << 2 = 0x04
	createData := make([]byte, 4)
	binary.LittleEndian.PutUint16(createData[0:], 4) // MaxPlayers=4
	binary.LittleEndian.PutUint16(createData[2:], 0) // MatchKeyLen=0

	createMsg := codec.NewExtMessage(framesync.ExtCmdCreateRoom, createData)
	if err := w.WriteMessage(createMsg); err != nil {
		t.Fatalf("write CreateRoom: %v", err)
	}
	if err := w.Flush(); err != nil {
		t.Fatalf("flush CreateRoom: %v", err)
	}
	t.Logf("[client] CreateRoom sent: maxPlayers=4")

	// 读取 CreateRoomRsp
	rsp2, err := r.ReadMessageCopy()
	if err != nil {
		t.Fatalf("read CreateRoomRsp: %v", err)
	}

	// 验证 FlagsCmd 解码：CmdType=Extended(1), ExtCmd=ExtCmdCreateRoomRsp(4)
	if rsp2.CmdType != codec.CmdTypeExtended {
		t.Fatalf("CreateRoomRsp FlagsCmd: expected CmdType=Extended(%d), got %d",
			codec.CmdTypeExtended, rsp2.CmdType)
	}
	if rsp2.ExtCmd != framesync.ExtCmdCreateRoomRsp {
		t.Fatalf("CreateRoomRsp FlagsCmd: expected ExtCmd=ExtCmdCreateRoomRsp(%d), got %d",
			framesync.ExtCmdCreateRoomRsp, rsp2.ExtCmd)
	}

	// 验证 roomId > 0（0 表示创建失败）
	if len(rsp2.Data) < 4 {
		t.Fatalf("CreateRoomRsp data too short: %d bytes", len(rsp2.Data))
	}
	roomId := int32(binary.LittleEndian.Uint32(rsp2.Data[0:4]))
	if roomId <= 0 {
		t.Fatalf("CreateRoomRsp: roomId=%d (expected positive)", roomId)
	}
	t.Logf("[client] ✓ CreateRoomRsp: CmdType=%d ExtCmd=%d roomId=%d",
		rsp2.CmdType, rsp2.ExtCmd, roomId)

	// ── Step 3: JoinRoom ───────────────────────────────────────────────────────
	// Wire: CmdType=Extended(1), ExtCmd=ExtCmdJoinRoom(5)
	// Data: [RoomId:4]
	joinData := make([]byte, 4)
	binary.LittleEndian.PutUint32(joinData, uint32(roomId))

	joinMsg := codec.NewExtMessage(framesync.ExtCmdJoinRoom, joinData)
	if err := w.WriteMessage(joinMsg); err != nil {
		t.Fatalf("write JoinRoom: %v", err)
	}
	if err := w.Flush(); err != nil {
		t.Fatalf("flush JoinRoom: %v", err)
	}
	t.Logf("[client] JoinRoom sent: roomId=%d", roomId)

	// 读取 JoinRoomRsp
	rsp3, err := r.ReadMessageCopy()
	if err != nil {
		t.Fatalf("read JoinRoomRsp: %v", err)
	}

	// 验证 FlagsCmd 解码：CmdType=Extended(1), ExtCmd=ExtCmdJoinRoomRsp(6)
	if rsp3.CmdType != codec.CmdTypeExtended {
		t.Fatalf("JoinRoomRsp FlagsCmd: expected CmdType=Extended(%d), got %d",
			codec.CmdTypeExtended, rsp3.CmdType)
	}
	if rsp3.ExtCmd != framesync.ExtCmdJoinRoomRsp {
		t.Fatalf("JoinRoomRsp FlagsCmd: expected ExtCmd=ExtCmdJoinRoomRsp(%d), got %d",
			framesync.ExtCmdJoinRoomRsp, rsp3.ExtCmd)
	}

	// EncodeJoinRoomRsp wire: [PlayerId:4][RoomId:4][PlayerCount:2][PlayerIds:4×N]
	// EncodeJoinRoomError wire: [PlayerId=0:4][ErrorCode:1][0,0,0]
	// 区分：Data[0:4]=0 表示失败（ErrorCode 在 Data[4]）
	if len(rsp3.Data) < 8 {
		t.Fatalf("JoinRoomRsp data too short: %d bytes", len(rsp3.Data))
	}
	rspPlayerId := int32(binary.LittleEndian.Uint32(rsp3.Data[0:4]))
	if rspPlayerId == 0 {
		errorCode := framesync.JoinRoomResult(rsp3.Data[4])
		t.Fatalf("JoinRoomRsp: join failed, errorCode=%d (%s)", errorCode, joinRoomErrorDesc(errorCode))
	}
	rspRoomId := int32(binary.LittleEndian.Uint32(rsp3.Data[4:8]))
	if rspRoomId != roomId {
		t.Fatalf("JoinRoomRsp: expected roomId=%d, got %d", roomId, rspRoomId)
	}
	t.Logf("[client] ✓ JoinRoomRsp: CmdType=%d ExtCmd=%d playerId=%d roomId=%d",
		rsp3.CmdType, rsp3.ExtCmd, rspPlayerId, rspRoomId)

	// ── 等待服务器 goroutine 完成 ───────────────────────────────────────────────
	select {
	case <-serverDone:
	case <-time.After(2 * time.Second):
		t.Log("[client] warning: server goroutine did not finish within 2s")
	}

	t.Log("✓ TestCSharpClientConnect: all steps passed")
}

// joinRoomErrorDesc 返回 JoinRoomResult 的可读描述
func joinRoomErrorDesc(r framesync.JoinRoomResult) string {
	switch r {
	case framesync.JoinRoomNotFound:
		return "NotFound"
	case framesync.JoinRoomFull:
		return "Full"
	case framesync.JoinRoomNotBound:
		return "NotBound"
	case framesync.JoinRoomBadData:
		return "BadData"
	default:
		return fmt.Sprintf("Unknown(%d)", r)
	}
}
