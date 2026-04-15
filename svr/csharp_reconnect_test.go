// Package boomnetwork_test — Go 集成测试，模拟 C# 客户端两种断线重连模式。
//
// 背景：
//   BoomNetwork 支持两种断线重连模式：
//     - Fast Reconnect（lastFrame > 0）：客户端发送 [playerId:4][lastFrame:4][lastS2CSeq:4]，
//       服务端从 lastFrame+1 开始补帧。
//     - Snapshot Reconnect（lastFrame = 0）：客户端发送 [playerId:4]，
//       服务端在响应中内嵌完整快照（snapshotFrame + snapshotData）。
//
// 测试设计（基于 net.Pipe 全双工同步管道）：
//   - 服务端 goroutine：处理两阶段（初始 + 重连）的协议握手
//   - 客户端 Phase1 goroutine：SessionBind → JoinRoom → 收帧 → 关闭连接
//   - 主 goroutine：协调 room.Start()、快照注入，执行 Phase2 重连客户端操作并验证
package boomnetwork_test

import (
	"encoding/binary"
	"net"
	"testing"
	"time"

	"github.com/boomlulu/boomnetwork/codec"
	"github.com/boomlulu/boomnetwork/framesync"
)

// csRcMinFrames Phase1 至少收到的帧数（决定 lastFrame）
const csRcMinFrames = 3

// csRcRoomConfig 重连测试房间配置：高帧缓冲、禁止快照过期暂停
var csRcRoomConfig = framesync.RoomConfig{
	FrameRate:              20,
	MaxPlayers:             2,
	FrameBufferSize:        200,
	SnapshotIntervalFrames: 10000, // 禁用快照过期检查
	QuickReconnectMaxMs:    30000,
}

// TestCSharpReconnect 验证 C# 客户端协议的两种断线重连模式：
//   - FastReconnect：lastFrame > 0，补帧从 lastFrame+1 开始
//   - SnapshotReconnect：lastFrame = 0，响应含快照数据
func TestCSharpReconnect(t *testing.T) {
	t.Run("FastReconnect", func(t *testing.T) {
		testCsReconnect(t, false)
	})
	t.Run("SnapshotReconnect", func(t *testing.T) {
		testCsReconnect(t, true)
	})
}

// testCsReconnect 重连测试主体。snapshotMode=true → Snapshot Reconnect。
func testCsReconnect(t *testing.T, snapshotMode bool) {
	t.Helper()

	const playerID = int32(10) // 固定值，不与 testPlayerCounter 冲突

	// ── 创建房间 ──────────────────────────────────────────────────────────────
	roomMgr := framesync.NewRoomManager(csRcRoomConfig)
	room := roomMgr.CreateRoomWithMaxPlayers(2, "")
	if room == nil {
		t.Fatal("CreateRoomWithMaxPlayers returned nil")
	}
	defer room.Stop()
	t.Logf("[setup] room created: roomId=%d", room.ID)

	// ── 两对 net.Pipe（in-process，无外部端口依赖）────────────────────────────
	svrConn1, cliConn1 := net.Pipe() // 初始连接
	svrConn2, cliConn2 := net.Pipe() // 重连

	// ── 协调通道 ──────────────────────────────────────────────────────────────
	joinedCh := make(chan struct{}, 1)  // 服务端 AddPlayer 完成
	discCh := make(chan struct{}, 1)    // 服务端断线处理完成，Phase2 可以开始
	lastFrameCh := make(chan uint32, 1) // Phase1 客户端 → 主：lastFrame

	// ══ 服务端 goroutine（处理 Phase1 初始连接 + Phase2 重连）══════════════════
	go func() {
		// ─ Phase 1: 初始连接 ─────────────────────────────────────────────────
		func() {
			defer svrConn1.Close()
			svrConn1.SetDeadline(time.Now().Add(10 * time.Second))
			r1 := codec.NewFrameReader(svrConn1, 0)
			pc1 := newCsFsConn(svrConn1)

			// SessionBind
			msg, err := r1.ReadMessageCopy()
			if err != nil || msg.CmdType != codec.CmdTypeCore || msg.Cmd != framesync.CmdSessionBind {
				t.Errorf("[srv] phase1 expected SessionBind: %v", err)
				return
			}
			rspBind := make([]byte, 4)
			binary.LittleEndian.PutUint32(rspBind, uint32(playerID))
			if err := pc1.Send(codec.NewCoreMessage(framesync.CmdSessionBindRsp, rspBind)); err != nil {
				t.Errorf("[srv] phase1 send SessionBindRsp: %v", err)
				return
			}
			t.Logf("[srv] phase1 SessionBind OK")

			// JoinRoom
			msg, err = r1.ReadMessageCopy()
			if err != nil || msg.CmdType != codec.CmdTypeExtended || msg.ExtCmd != framesync.ExtCmdJoinRoom {
				t.Errorf("[srv] phase1 expected JoinRoom: %v", err)
				return
			}
			rspJoin := framesync.EncodeJoinRoomRsp(playerID, room.ID, []int32{})
			if err := pc1.Send(codec.NewExtMessage(framesync.ExtCmdJoinRoomRsp, rspJoin)); err != nil {
				t.Errorf("[srv] phase1 send JoinRoomRsp: %v", err)
				return
			}
			t.Logf("[srv] phase1 JoinRoom OK")

			// AddPlayer：启动 deliveryLoop，会将帧推送给客户端
			if err := room.AddPlayer(playerID, pc1, false, 0); err != nil {
				t.Errorf("[srv] phase1 AddPlayer: %v", err)
				return
			}
			joinedCh <- struct{}{} // 通知主 goroutine 玩家已就绪

			// 转发客户端输入，直到连接断开
			for {
				msg, err := r1.ReadMessageCopy()
				if err != nil {
					break // conn closed by client (simulated disconnect)
				}
				if msg.CmdType == codec.CmdTypeCore && msg.Cmd == framesync.CmdFrameInput {
					room.OnInput(playerID, msg.Data)
				}
			}
		}() // svrConn1.Close() via defer

		// DisconnectPlayer（onDeliveryExit 可能先调用，此处幂等）
		room.DisconnectPlayer(playerID)
		t.Logf("[srv] phase1 player disconnected")
		discCh <- struct{}{} // 通知主 goroutine Phase2 可以开始

		// ─ Phase 2: 重连 ──────────────────────────────────────────────────────
		func() {
			defer svrConn2.Close()
			svrConn2.SetDeadline(time.Now().Add(10 * time.Second))
			r2 := codec.NewFrameReader(svrConn2, 0)
			pc2 := newCsFsConn(svrConn2)

			// CmdReconnect: [playerId:4][lastFrame:4][lastS2CSeq:4]（lastFrame=0 表示快照重连）
			msg, err := r2.ReadMessageCopy()
			if err != nil || msg.CmdType != codec.CmdTypeCore || msg.Cmd != framesync.CmdReconnect {
				t.Errorf("[srv] phase2 expected CmdReconnect, got type=%d cmd=%d err=%v",
					msg.CmdType, msg.Cmd, err)
				return
			}
			if len(msg.Data) < 4 {
				t.Errorf("[srv] phase2 CmdReconnect data too short: %d", len(msg.Data))
				return
			}
			recPlayerId := int32(binary.LittleEndian.Uint32(msg.Data[0:4]))
			var lastFrame uint32
			if len(msg.Data) >= 8 {
				lastFrame = binary.LittleEndian.Uint32(msg.Data[4:8])
			}
			t.Logf("[srv] phase2 CmdReconnect: playerId=%d lastFrame=%d", recPlayerId, lastFrame)

			// 获取重连快照（一次加锁原子读取）
			rs := room.GetReconnectSnapshot()

			var replayFrom uint32
			var rspSnapshotFrame uint32
			var rspSnapshotData []byte

			if lastFrame > 0 {
				// Fast Reconnect: 从 lastFrame 开始补帧，无快照
				replayFrom = lastFrame
			} else {
				// Snapshot Reconnect: 快照嵌入响应，从 snapshotFrame 补帧
				replayFrom = rs.SnapshotFrame
				rspSnapshotFrame = rs.SnapshotFrame
				rspSnapshotData = rs.Snapshot
			}

			// 发送 CmdReconnectRsp
			// Wire: [Result:1][RoomId:4][ServerFrame:4][SnapshotFrame:4][ServerLastC2SSeq:4][SnapshotData:N]
			rsp := framesync.EncodeReconnectRsp(
				framesync.ReconnectSuccess,
				room.ID,
				rs.Frame,
				rspSnapshotFrame,
				0, // serverLastC2SSeq（测试简化，无 C2S reliable 重放）
				rspSnapshotData,
			)
			if err := pc2.Send(codec.NewCoreMessage(framesync.CmdReconnectRsp, rsp)); err != nil {
				t.Errorf("[srv] phase2 send CmdReconnectRsp: %v", err)
				return
			}
			t.Logf("[srv] phase2 CmdReconnectRsp sent: serverFrame=%d snapshotFrame=%d snapshotBytes=%d",
				rs.Frame, rspSnapshotFrame, len(rspSnapshotData))

			// AddPlayer: 替换连接并启动补帧 delivery loop
			// replaying=true 时 delivery loop Phase1 发送 replayFrom 之后的所有历史帧
			if err := room.AddPlayer(recPlayerId, pc2, replayFrom > 0, replayFrom); err != nil {
				t.Errorf("[srv] phase2 AddPlayer: %v", err)
				return
			}
			t.Logf("[srv] phase2 AddPlayer OK: replayFrom=%d", replayFrom)

			// 继续转发输入（实时阶段），直到测试结束关闭 cliConn2
			for {
				msg, err := r2.ReadMessageCopy()
				if err != nil {
					break
				}
				if msg.CmdType == codec.CmdTypeCore && msg.Cmd == framesync.CmdFrameInput {
					room.OnInput(recPlayerId, msg.Data)
				}
			}
		}() // svrConn2.Close() via defer
	}()

	// ══ 客户端 Phase1 goroutine（与服务端 goroutine 并发执行）═══════════════════
	// net.Pipe 全同步：客户端和服务端必须并发，不能在主 goroutine 中顺序执行。
	go func() {
		defer cliConn1.Close() // 关闭后服务端 r1.ReadMessageCopy() 返回错误，触发 DisconnectPlayer

		cliConn1.SetDeadline(time.Now().Add(10 * time.Second))
		r1 := codec.NewFrameReader(cliConn1, 0)
		w1 := codec.NewFrameWriter(cliConn1)

		// SessionBind
		bindData := make([]byte, 4)
		binary.LittleEndian.PutUint32(bindData, uint32(playerID))
		if err := w1.WriteMessage(codec.NewCoreMessage(framesync.CmdSessionBind, bindData)); err != nil {
			t.Errorf("[cli1] write SessionBind: %v", err)
			return
		}
		if err := w1.Flush(); err != nil {
			t.Errorf("[cli1] flush SessionBind: %v", err)
			return
		}
		rsp1, err := r1.ReadMessageCopy()
		if err != nil || rsp1.CmdType != codec.CmdTypeCore || rsp1.Cmd != framesync.CmdSessionBindRsp {
			t.Errorf("[cli1] read SessionBindRsp: %v", err)
			return
		}
		t.Logf("[cli1] SessionBindRsp OK")

		// JoinRoom
		joinData := make([]byte, 4)
		binary.LittleEndian.PutUint32(joinData, uint32(room.ID))
		if err := w1.WriteMessage(codec.NewExtMessage(framesync.ExtCmdJoinRoom, joinData)); err != nil {
			t.Errorf("[cli1] write JoinRoom: %v", err)
			return
		}
		if err := w1.Flush(); err != nil {
			t.Errorf("[cli1] flush JoinRoom: %v", err)
			return
		}
		rsp2, err := r1.ReadMessageCopy()
		if err != nil || rsp2.CmdType != codec.CmdTypeExtended || rsp2.ExtCmd != framesync.ExtCmdJoinRoomRsp {
			t.Errorf("[cli1] read JoinRoomRsp: %v", err)
			return
		}
		t.Logf("[cli1] JoinRoomRsp OK")

		// 接收帧（room.Start() 触发 CmdStartFrameSync，然后开始推 CmdPushFrames）
		var collected []*framesync.FrameData
		for len(collected) < csRcMinFrames {
			msg, err := r1.ReadMessageCopy()
			if err != nil {
				t.Logf("[cli1] read stopped after %d frames: %v", len(collected), err)
				break
			}
			switch {
			case msg.CmdType == codec.CmdTypeCore && msg.Cmd == framesync.CmdStartFrameSync:
				t.Logf("[cli1] CmdStartFrameSync received")
			case msg.CmdType == codec.CmdTypeCore && msg.Cmd == framesync.CmdPushFrames:
				fd, decErr := framesync.DecodeFrameData(msg.Data)
				if decErr != nil {
					t.Errorf("[cli1] DecodeFrameData: %v", decErr)
					continue
				}
				collected = append(collected, fd)
				t.Logf("[cli1] frame[%d] frameId=%d", len(collected)-1, fd.FrameNumber)
			}
		}

		if len(collected) < csRcMinFrames {
			t.Errorf("[cli1] collected %d frames, expected >= %d", len(collected), csRcMinFrames)
			lastFrameCh <- 0
			return
		}
		lastFrame := collected[len(collected)-1].FrameNumber
		t.Logf("[cli1] collected %d frames, lastFrame=%d", len(collected), lastFrame)
		lastFrameCh <- lastFrame
		// defer cliConn1.Close() 触发服务端 disconnect 处理
	}()

	// ── 主 goroutine：协调 room.Start() ─────────────────────────────────────
	select {
	case <-joinedCh:
	case <-time.After(5 * time.Second):
		t.Fatal("timeout waiting for player to join")
	}
	t.Log("[main] player joined, starting room")
	room.Start()

	// 等待 Phase1 完成（客户端收集到足够帧后发 lastFrame）
	var lastFrame uint32
	select {
	case lastFrame = <-lastFrameCh:
	case <-time.After(10 * time.Second):
		t.Fatal("timeout waiting for phase1 frames")
	}
	if lastFrame == 0 {
		t.Fatal("phase1 failed to collect frames")
	}
	t.Logf("[main] phase1 done: lastFrame=%d", lastFrame)

	// Snapshot Reconnect：模拟客户端在断线前已上传快照
	if snapshotMode {
		snapshotPayload := []byte("test-snapshot-payload-for-reconnect")
		room.UpdateSnapshot(lastFrame, snapshotPayload)
		t.Logf("[main] snapshot set: frame=%d bytes=%d", lastFrame, len(snapshotPayload))
	}

	// 等待服务端断线处理完成
	select {
	case <-discCh:
	case <-time.After(3 * time.Second):
		t.Fatal("timeout waiting for disconnect signal")
	}

	// 短暂等待：确保 Phase1 delivery loop goroutine 退出（onDeliveryExit 清理）
	time.Sleep(30 * time.Millisecond)

	// ══ 主 goroutine 作为 Phase2 客户端（重连握手 + 验证）══════════════════════
	cliConn2.SetDeadline(time.Now().Add(10 * time.Second))
	r2 := codec.NewFrameReader(cliConn2, 0)
	w2 := codec.NewFrameWriter(cliConn2)

	// 构造 CmdReconnect 握手
	// Fast Reconnect:      [playerId:4][lastFrame:4][lastS2CSeq:4]  lastFrame > 0
	// Snapshot Reconnect:  [playerId:4]                             lastFrame = 0
	var reconnData []byte
	if snapshotMode {
		reconnData = make([]byte, 4) // 只发 playerId，lastFrame=0
		binary.LittleEndian.PutUint32(reconnData[0:], uint32(playerID))
	} else {
		reconnData = make([]byte, 12)
		binary.LittleEndian.PutUint32(reconnData[0:], uint32(playerID))
		binary.LittleEndian.PutUint32(reconnData[4:], lastFrame)
		binary.LittleEndian.PutUint32(reconnData[8:], 0) // lastS2CSeq=0
	}
	if err := w2.WriteMessage(codec.NewCoreMessage(framesync.CmdReconnect, reconnData)); err != nil {
		t.Fatalf("[cli2] write CmdReconnect: %v", err)
	}
	if err := w2.Flush(); err != nil {
		t.Fatalf("[cli2] flush CmdReconnect: %v", err)
	}
	t.Logf("[cli2] CmdReconnect sent: snapshotMode=%v lastFrame=%d", snapshotMode, lastFrame)

	// ── 读取并验证 CmdReconnectRsp ────────────────────────────────────────────
	// Wire: [Result:1][RoomId:4][ServerFrame:4][SnapshotFrame:4][ServerLastC2SSeq:4][SnapshotData:N]
	// offsets:         0         1               5                9                   13              17
	rspMsg, err := r2.ReadMessageCopy()
	if err != nil {
		t.Fatalf("[cli2] read CmdReconnectRsp: %v", err)
	}
	if rspMsg.CmdType != codec.CmdTypeCore || rspMsg.Cmd != framesync.CmdReconnectRsp {
		t.Fatalf("[cli2] expected CmdReconnectRsp, got CmdType=%d Cmd=%d",
			rspMsg.CmdType, rspMsg.Cmd)
	}
	const rspHdrSize = 1 + 4 + 4 + 4 + 4 // result + roomId + serverFrame + snapshotFrame + serverLastC2SSeq
	if len(rspMsg.Data) < rspHdrSize {
		t.Fatalf("[cli2] CmdReconnectRsp data too short: %d (need %d)", len(rspMsg.Data), rspHdrSize)
	}

	result := rspMsg.Data[0]
	rspRoomId := int32(binary.LittleEndian.Uint32(rspMsg.Data[1:5]))
	rspServerFrame := binary.LittleEndian.Uint32(rspMsg.Data[5:9])
	rspSnapshotFrame := binary.LittleEndian.Uint32(rspMsg.Data[9:13])
	var rspSnapshotData []byte
	if len(rspMsg.Data) > rspHdrSize {
		rspSnapshotData = rspMsg.Data[rspHdrSize:]
	}
	t.Logf("[cli2] CmdReconnectRsp: result=%d roomId=%d serverFrame=%d snapshotFrame=%d snapshotBytes=%d",
		result, rspRoomId, rspServerFrame, rspSnapshotFrame, len(rspSnapshotData))

	// 验证 1: Result = ReconnectSuccess
	if result != framesync.ReconnectSuccess {
		t.Fatalf("[cli2] ✗ result=%d, expected ReconnectSuccess(%d)", result, framesync.ReconnectSuccess)
	}
	t.Logf("[cli2] ✓ result=ReconnectSuccess")

	// 验证 2: roomId 正确
	if rspRoomId != room.ID {
		t.Errorf("[cli2] ✗ roomId=%d, expected %d", rspRoomId, room.ID)
	}

	if snapshotMode {
		// Snapshot Reconnect 专项验证
		if rspSnapshotFrame == 0 {
			t.Errorf("[cli2] ✗ SnapshotReconnect: snapshotFrame=0, expected > 0")
		} else {
			t.Logf("[cli2] ✓ SnapshotReconnect: snapshotFrame=%d > 0", rspSnapshotFrame)
		}
		if len(rspSnapshotData) == 0 {
			t.Errorf("[cli2] ✗ SnapshotReconnect: snapshotData is empty")
		} else {
			t.Logf("[cli2] ✓ SnapshotReconnect: snapshotData len=%d", len(rspSnapshotData))
		}
	} else {
		// Fast Reconnect 专项验证
		if rspSnapshotFrame != 0 {
			t.Errorf("[cli2] ✗ FastReconnect: snapshotFrame=%d, expected 0", rspSnapshotFrame)
		}
	}

	// ── 验证重连后继续收到新帧 ──────────────────────────────────────────────
	const wantPostReconnFrames = 2
	var phase2Frames []*framesync.FrameData
	for len(phase2Frames) < wantPostReconnFrames {
		msg, err := r2.ReadMessageCopy()
		if err != nil {
			t.Fatalf("[cli2] read frame error after %d frames: %v", len(phase2Frames), err)
		}
		if msg.CmdType == codec.CmdTypeCore && msg.Cmd == framesync.CmdPushFrames {
			fd, decErr := framesync.DecodeFrameData(msg.Data)
			if decErr != nil {
				t.Fatalf("[cli2] DecodeFrameData: %v", decErr)
			}
			phase2Frames = append(phase2Frames, fd)
			t.Logf("[cli2] phase2 frame[%d] frameId=%d", len(phase2Frames)-1, fd.FrameNumber)
		}
	}

	if !snapshotMode {
		// Fast Reconnect: 第一补帧帧号必须 > lastFrame（即从 lastFrame+1 开始补）
		if len(phase2Frames) > 0 && phase2Frames[0].FrameNumber <= lastFrame {
			t.Errorf("[cli2] ✗ FastReconnect: first replay frame=%d, expected > lastFrame=%d",
				phase2Frames[0].FrameNumber, lastFrame)
		} else if len(phase2Frames) > 0 {
			t.Logf("[cli2] ✓ FastReconnect: first replay frame=%d > lastFrame=%d",
				phase2Frames[0].FrameNumber, lastFrame)
		}
	} else {
		// Snapshot Reconnect: 第一帧必须 > snapshotFrame
		if len(phase2Frames) > 0 && rspSnapshotFrame > 0 && phase2Frames[0].FrameNumber <= rspSnapshotFrame {
			t.Errorf("[cli2] ✗ SnapshotReconnect: first frame=%d, expected > snapshotFrame=%d",
				phase2Frames[0].FrameNumber, rspSnapshotFrame)
		} else if len(phase2Frames) > 0 {
			t.Logf("[cli2] ✓ SnapshotReconnect: first frame=%d > snapshotFrame=%d",
				phase2Frames[0].FrameNumber, rspSnapshotFrame)
		}
	}

	// 验证帧序号连续递增
	for i := 1; i < len(phase2Frames); i++ {
		if phase2Frames[i].FrameNumber <= phase2Frames[i-1].FrameNumber {
			t.Errorf("[cli2] frame sequence not monotonic: frame[%d]=%d <= frame[%d]=%d",
				i, phase2Frames[i].FrameNumber, i-1, phase2Frames[i-1].FrameNumber)
		}
	}

	// 关闭重连连接，结束测试
	cliConn2.Close()

	mode := "FastReconnect"
	if snapshotMode {
		mode = "SnapshotReconnect"
	}
	t.Logf("✓ %s PASS: lastFrame=%d phase2Frames=%d", mode, lastFrame, len(phase2Frames))
}
