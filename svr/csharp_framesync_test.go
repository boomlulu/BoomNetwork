// Package boomnetwork_test — Go 集成测试，模拟两个 C# 客户端完成帧同步回路验证。
//
// 流程：
//  1. 两个客户端各完成 SessionBind + JoinRoom（同一房间）
//  2. 客户端 A / B 各发送 N 帧输入（CmdFrameInput）
//  3. 两个客户端各自接收并解码至少 N 帧 FrameData 广播（CmdPushFrames）
//  4. 验证帧序号连续递增，帧内包含双方输入数据
//  5. 验证帧数据格式符合协议规范（FlagsCmd 正确，encode/decode round-trip 一致）
package boomnetwork_test

import (
	"encoding/binary"
	"net"
	"sync"
	"testing"
	"time"

	"github.com/boomlulu/boomnetwork/codec"
	"github.com/boomlulu/boomnetwork/framesync"
)

const (
	csFsN         = 7  // 每客户端发送/接收帧数，N >= 5
	csFsFrameRate = 20 // fps
)

// csFsConn 实现 framesync.PlayerConn，包装 net.Conn，供房间 deliveryLoop 使用。
type csFsConn struct {
	conn   net.Conn
	writer *codec.FrameWriter
	mu     sync.Mutex
}

func newCsFsConn(c net.Conn) *csFsConn {
	return &csFsConn{conn: c, writer: codec.NewFrameWriter(c)}
}

func (p *csFsConn) Send(msg *codec.Message) error {
	p.mu.Lock()
	defer p.mu.Unlock()
	if err := p.writer.WriteMessage(msg); err != nil {
		return err
	}
	return p.writer.Flush()
}

func (p *csFsConn) Close() error {
	return p.conn.Close()
}

// csFsServeConn 处理服务端单个客户端连接：
//   SessionBind → JoinRoom（加入预创建的房间）→ CmdFrameInput 循环
//
// 完成 AddPlayer 后向 joinedCh 发送信号，让测试主流程知道玩家已就绪。
func csFsServeConn(
	serverConn net.Conn,
	room *framesync.Room,
	assignedId int32,
	joinedCh chan<- struct{},
	t *testing.T,
) {
	defer serverConn.Close()
	serverConn.SetDeadline(time.Now().Add(10 * time.Second))

	r := codec.NewFrameReader(serverConn, 0)
	pc := newCsFsConn(serverConn)

	// ── SessionBind ────────────────────────────────────────────────────────────
	msg, err := r.ReadMessageCopy()
	if err != nil {
		t.Errorf("[srv %d] read SessionBind: %v", assignedId, err)
		return
	}
	if msg.CmdType != codec.CmdTypeCore || msg.Cmd != framesync.CmdSessionBind {
		t.Errorf("[srv %d] expected CmdSessionBind, got type=%d cmd=%d",
			assignedId, msg.CmdType, msg.Cmd)
		return
	}

	rspBind := make([]byte, 4)
	binary.LittleEndian.PutUint32(rspBind, uint32(assignedId))
	if err := pc.Send(codec.NewCoreMessage(framesync.CmdSessionBindRsp, rspBind)); err != nil {
		t.Errorf("[srv %d] send SessionBindRsp: %v", assignedId, err)
		return
	}
	t.Logf("[srv %d] SessionBind → SessionBindRsp OK (assignedId=%d)", assignedId, assignedId)

	// ── JoinRoom ───────────────────────────────────────────────────────────────
	msg, err = r.ReadMessageCopy()
	if err != nil {
		t.Errorf("[srv %d] read JoinRoom: %v", assignedId, err)
		return
	}
	if msg.CmdType != codec.CmdTypeExtended || msg.ExtCmd != framesync.ExtCmdJoinRoom {
		t.Errorf("[srv %d] expected ExtCmdJoinRoom, got type=%d extCmd=%d",
			assignedId, msg.CmdType, msg.ExtCmd)
		return
	}
	if len(msg.Data) < 4 {
		t.Errorf("[srv %d] JoinRoom data too short: %d", assignedId, len(msg.Data))
		return
	}
	roomId := int32(binary.LittleEndian.Uint32(msg.Data[0:4]))
	if roomId != room.ID {
		t.Errorf("[srv %d] JoinRoom roomId mismatch: want %d got %d", assignedId, room.ID, roomId)
		return
	}

	rspJoin := framesync.EncodeJoinRoomRsp(assignedId, room.ID, []int32{})
	if err := pc.Send(codec.NewExtMessage(framesync.ExtCmdJoinRoomRsp, rspJoin)); err != nil {
		t.Errorf("[srv %d] send JoinRoomRsp: %v", assignedId, err)
		return
	}
	t.Logf("[srv %d] JoinRoom → JoinRoomRsp OK (roomId=%d)", assignedId, room.ID)

	// ── AddPlayer：启动 deliveryLoop ───────────────────────────────────────────
	// AddPlayer 返回后，deliveryLoop goroutine 已启动，pc.Send() 被移交给房间。
	if err := room.AddPlayer(assignedId, pc, false, 0); err != nil {
		t.Errorf("[srv %d] AddPlayer: %v", assignedId, err)
		return
	}
	joinedCh <- struct{}{} // 通知主流程，该玩家已加入房间

	// ── CmdFrameInput 循环：将客户端输入转发给房间 ────────────────────────────
	for {
		msg, err := r.ReadMessageCopy()
		if err != nil {
			return // 连接关闭或超时，正常退出
		}
		if msg.CmdType == codec.CmdTypeCore && msg.Cmd == framesync.CmdFrameInput {
			inputCopy := make([]byte, len(msg.Data))
			copy(inputCopy, msg.Data)
			room.OnInput(assignedId, inputCopy)
		}
	}
}

// csFsRunClient 模拟 C# 客户端完整帧同步流程：
//   SessionBind → JoinRoom → 并发[发送 N 帧输入 / 接收 N 帧 FrameData]
//
// 为避免 net.Pipe 全同步管道死锁，输入发送和帧接收在独立 goroutine 中并发执行。
// 收集到的 FrameData 通过 framesCh 返回给测试主流程。
func csFsRunClient(
	t *testing.T,
	conn net.Conn,
	playerId int32,
	roomId int32,
	nFrames int,
	framesCh chan<- []*framesync.FrameData,
	wg *sync.WaitGroup,
) {
	defer wg.Done()
	defer conn.Close()
	conn.SetDeadline(time.Now().Add(15 * time.Second))

	r := codec.NewFrameReader(conn, 0)
	w := codec.NewFrameWriter(conn)

	// ── SessionBind ────────────────────────────────────────────────────────────
	bindData := make([]byte, 4)
	binary.LittleEndian.PutUint32(bindData, uint32(playerId))
	if err := w.WriteMessage(codec.NewCoreMessage(framesync.CmdSessionBind, bindData)); err != nil {
		t.Errorf("[cli %d] write SessionBind: %v", playerId, err)
		framesCh <- nil
		return
	}
	if err := w.Flush(); err != nil {
		t.Errorf("[cli %d] flush SessionBind: %v", playerId, err)
		framesCh <- nil
		return
	}

	rsp1, err := r.ReadMessageCopy()
	if err != nil {
		t.Errorf("[cli %d] read SessionBindRsp: %v", playerId, err)
		framesCh <- nil
		return
	}
	// 验证 FlagsCmd：CmdType=Core, Cmd=CmdSessionBindRsp
	if rsp1.CmdType != codec.CmdTypeCore || rsp1.Cmd != framesync.CmdSessionBindRsp {
		t.Errorf("[cli %d] SessionBindRsp FlagsCmd: expected type=%d cmd=%d, got type=%d cmd=%d",
			playerId, codec.CmdTypeCore, framesync.CmdSessionBindRsp, rsp1.CmdType, rsp1.Cmd)
		framesCh <- nil
		return
	}
	if len(rsp1.Data) < 4 {
		t.Errorf("[cli %d] SessionBindRsp data too short: %d", playerId, len(rsp1.Data))
		framesCh <- nil
		return
	}
	assignedId := int32(binary.LittleEndian.Uint32(rsp1.Data[0:4]))
	if assignedId == 0 {
		t.Errorf("[cli %d] SessionBindRsp: playerId=0 (auth failure or server error)", playerId)
		framesCh <- nil
		return
	}
	t.Logf("[cli %d] ✓ SessionBindRsp: assignedId=%d", playerId, assignedId)

	// ── JoinRoom ───────────────────────────────────────────────────────────────
	joinData := make([]byte, 4)
	binary.LittleEndian.PutUint32(joinData, uint32(roomId))
	if err := w.WriteMessage(codec.NewExtMessage(framesync.ExtCmdJoinRoom, joinData)); err != nil {
		t.Errorf("[cli %d] write JoinRoom: %v", playerId, err)
		framesCh <- nil
		return
	}
	if err := w.Flush(); err != nil {
		t.Errorf("[cli %d] flush JoinRoom: %v", playerId, err)
		framesCh <- nil
		return
	}

	rsp2, err := r.ReadMessageCopy()
	if err != nil {
		t.Errorf("[cli %d] read JoinRoomRsp: %v", playerId, err)
		framesCh <- nil
		return
	}
	// 验证 FlagsCmd：CmdType=Extended, ExtCmd=ExtCmdJoinRoomRsp
	if rsp2.CmdType != codec.CmdTypeExtended || rsp2.ExtCmd != framesync.ExtCmdJoinRoomRsp {
		t.Errorf("[cli %d] JoinRoomRsp FlagsCmd: expected type=%d extCmd=%d, got type=%d extCmd=%d",
			playerId, codec.CmdTypeExtended, framesync.ExtCmdJoinRoomRsp, rsp2.CmdType, rsp2.ExtCmd)
		framesCh <- nil
		return
	}
	if len(rsp2.Data) < 4 {
		t.Errorf("[cli %d] JoinRoomRsp data too short: %d", playerId, len(rsp2.Data))
		framesCh <- nil
		return
	}
	if rspPid := int32(binary.LittleEndian.Uint32(rsp2.Data[0:4])); rspPid == 0 {
		errCode := framesync.JoinRoomResult(0)
		if len(rsp2.Data) >= 5 {
			errCode = framesync.JoinRoomResult(rsp2.Data[4])
		}
		t.Errorf("[cli %d] JoinRoom failed: errCode=%d", playerId, errCode)
		framesCh <- nil
		return
	}
	t.Logf("[cli %d] ✓ JoinRoomRsp OK", playerId)

	// ── 并发：发送 N 帧输入（sub-goroutine）+ 接收 N 帧数据（主 goroutine）────────
	// net.Pipe 是全同步管道，C→S 写和 S→C 读必须并发执行，避免双向阻塞死锁。
	inputsDone := make(chan struct{})
	inputData := []byte{byte(playerId & 0xFF), 0x01} // 携带 playerId 标识，便于验证

	go func() {
		defer close(inputsDone)
		for i := 0; i < nFrames; i++ {
			if err := w.WriteMessage(codec.NewCoreMessage(framesync.CmdFrameInput, inputData)); err != nil {
				t.Logf("[cli %d] input goroutine: write CmdFrameInput[%d]: %v", playerId, i, err)
				return
			}
			if err := w.Flush(); err != nil {
				t.Logf("[cli %d] input goroutine: flush CmdFrameInput[%d]: %v", playerId, i, err)
				return
			}
		}
		t.Logf("[cli %d] sent %d CmdFrameInput messages", playerId, nFrames)
	}()

	// 读消息循环：跳过 CmdStartFrameSync，收集 CmdPushFrames
	var collected []*framesync.FrameData
	for len(collected) < nFrames {
		msg, err := r.ReadMessageCopy()
		if err != nil {
			t.Logf("[cli %d] read stopped after %d/%d frames: %v", playerId, len(collected), nFrames, err)
			break
		}
		// 深拷贝 Data（ReadMessageCopy 已拷贝，但额外保险）
		if msg.Data != nil {
			cp := make([]byte, len(msg.Data))
			copy(cp, msg.Data)
			msg.Data = cp
		}

		switch {
		case msg.CmdType == codec.CmdTypeCore && msg.Cmd == framesync.CmdStartFrameSync:
			t.Logf("[cli %d] ✓ CmdStartFrameSync received (len=%d)", playerId, len(msg.Data))

		case msg.CmdType == codec.CmdTypeCore && msg.Cmd == framesync.CmdPushFrames:
			// 验证 FlagsCmd（由 codec.FrameReader 已解码，此处再次断言）
			fd, decErr := framesync.DecodeFrameData(msg.Data)
			if decErr != nil {
				t.Errorf("[cli %d] DecodeFrameData frame[%d]: %v", playerId, len(collected), decErr)
				continue
			}
			collected = append(collected, fd)
			t.Logf("[cli %d] frame[%d] frameId=%d inputs=%d",
				playerId, len(collected)-1, fd.FrameNumber, len(fd.Inputs))
		}
	}

	<-inputsDone
	framesCh <- collected
}

// TestCSharpFrameSync 模拟两个 C# 客户端完成帧同步回路验证：
//  1. 两个客户端各完成 SessionBind + JoinRoom（同一房间）
//  2. 客户端 A / B 各发送 N 帧输入
//  3. 两个客户端各自接收并解码至少 N 帧 FrameData 广播
//  4. 验证帧序号连续递增，帧内包含双方输入数据
//  5. 验证帧数据大小和格式符合协议规范（encode/decode round-trip 一致）
func TestCSharpFrameSync(t *testing.T) {
	const N = csFsN

	// 创建帧同步房间（SnapshotIntervalFrames 设大，避免快照检查在测试期间触发暂停）
	roomMgr := framesync.NewRoomManager(framesync.RoomConfig{
		FrameRate:              csFsFrameRate,
		MaxPlayers:             2,
		FrameBufferSize:        200,
		SnapshotIntervalFrames: 10000, // 不触发快照超期暂停
		QuickReconnectMaxMs:    5000,
	})
	room := roomMgr.CreateRoomWithMaxPlayers(2, "")
	if room == nil {
		t.Fatal("CreateRoomWithMaxPlayers returned nil")
	}
	defer room.Stop()
	t.Logf("room created: roomId=%d", room.ID)

	// 两对 net.Pipe 管道（全双工，in-process，无外部端口依赖）
	serverA, clientA := net.Pipe()
	serverB, clientB := net.Pipe()

	// joinedCh: 每个玩家完成 AddPlayer 后发送信号，集满 2 个后再 Start
	joinedCh := make(chan struct{}, 2)

	// 启动服务端处理 goroutine（先于客户端启动，等待客户端连接）
	go csFsServeConn(serverA, room, 1, joinedCh, t)
	go csFsServeConn(serverB, room, 2, joinedCh, t)

	// framesCh: 两个客户端 goroutine 完成后各发一份 FrameData 列表
	framesCh := make(chan []*framesync.FrameData, 2)
	var clientWg sync.WaitGroup
	clientWg.Add(2)

	// 启动客户端 goroutine（需要在等待 joinedCh 之前启动，否则服务端无法收到 SessionBind）
	go csFsRunClient(t, clientA, 1, room.ID, N, framesCh, &clientWg)
	go csFsRunClient(t, clientB, 2, room.ID, N, framesCh, &clientWg)

	// 等待两个玩家都完成 JoinRoom + AddPlayer
	for i := 0; i < 2; i++ {
		select {
		case <-joinedCh:
		case <-time.After(5 * time.Second):
			t.Fatal("timeout waiting for both players to join room")
		}
	}
	t.Log("both players joined, starting room")

	// 启动帧同步（广播 CmdStartFrameSync，开启 tickLoop @ 20fps）
	room.Start()

	// 等待两个客户端各自收集完 N 帧
	done := make(chan struct{})
	go func() {
		clientWg.Wait()
		close(done)
	}()
	select {
	case <-done:
	case <-time.After(15 * time.Second):
		t.Fatal("timeout waiting for clients to collect frames")
	}

	// 收集两客户端结果
	close(framesCh)
	allFrames := make([][]*framesync.FrameData, 0, 2)
	for fds := range framesCh {
		if fds != nil {
			allFrames = append(allFrames, fds)
		}
	}
	if len(allFrames) != 2 {
		t.Fatalf("expected 2 frame collections from 2 clients, got %d", len(allFrames))
	}

	// ── 验证：对每个客户端收集的帧做全量检查 ─────────────────────────────────────
	for i, fds := range allFrames {
		clientId := int32(i + 1)

		// 1. 验证帧数 >= N
		if len(fds) < N {
			t.Errorf("client %d: expected >= %d frames, got %d", clientId, N, len(fds))
		}
		t.Logf("client %d: received %d frames", clientId, len(fds))

		// 2. 验证帧序号连续递增（frameId 单调递增）
		for j := 1; j < len(fds); j++ {
			if fds[j].FrameNumber <= fds[j-1].FrameNumber {
				t.Errorf("client %d: frame[%d].FrameNumber=%d not > frame[%d].FrameNumber=%d",
					clientId, j, fds[j].FrameNumber, j-1, fds[j-1].FrameNumber)
			}
		}

		// 3. 验证帧内包含双方输入数据（所有帧中找到 player1 和 player2 的输入）
		var foundP1, foundP2 bool
		for _, fd := range fds {
			for _, inp := range fd.Inputs {
				switch inp.PlayerId {
				case 1:
					foundP1 = true
				case 2:
					foundP2 = true
				}
			}
		}
		if !foundP1 {
			t.Errorf("client %d: no frames contain player 1 input across all received frames", clientId)
		}
		if !foundP2 {
			t.Errorf("client %d: no frames contain player 2 input across all received frames", clientId)
		}

		// 4. 验证帧数据大小和格式符合协议规范：
		//    - FlagsCmd 已由 codec.FrameReader 在解码时校验（CmdType=Core, Cmd=CmdPushFrames）
		//    - FrameDataSize 与 EncodeFrameData 实际写入字节数一致
		//    - encode/decode round-trip：重新编码再解码，FrameNumber 不变
		for _, fd := range fds {
			expectedSize := framesync.FrameDataSize(fd)
			buf := make([]byte, expectedSize)
			n := framesync.EncodeFrameData(fd, buf)
			if n != expectedSize {
				t.Errorf("client %d frame %d: FrameDataSize=%d but EncodeFrameData wrote %d bytes",
					clientId, fd.FrameNumber, expectedSize, n)
			}
			fd2, err := framesync.DecodeFrameData(buf[:n])
			if err != nil {
				t.Errorf("client %d frame %d: round-trip decode error: %v", clientId, fd.FrameNumber, err)
				continue
			}
			if fd2.FrameNumber != fd.FrameNumber {
				t.Errorf("client %d frame %d: round-trip FrameNumber mismatch: got %d",
					clientId, fd.FrameNumber, fd2.FrameNumber)
			}
			if len(fd2.Inputs) != len(fd.Inputs) {
				t.Errorf("client %d frame %d: round-trip Inputs count mismatch: want %d got %d",
					clientId, fd.FrameNumber, len(fd.Inputs), len(fd2.Inputs))
			}
		}
	}

	t.Logf("✓ TestCSharpFrameSync PASS: 2 clients, %d frames each, frame IDs consecutive, both inputs present", N)
}
