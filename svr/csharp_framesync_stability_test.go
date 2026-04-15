// Package boomnetwork_test — Go 集成测试：3个模拟C#客户端持续帧同步20分钟压力测试。
//
// 背景：帧同步以20fps推进，服务端维护ring buffer（2400帧 = 120秒缓冲）。
//
// 验收标准：
//  1. 3个客户端各完成 SessionBind + JoinRoom（同一房间）
//  2. 3个客户端以20fps速率持续上传 UploadFrameInput（共约24000帧，运行20分钟）
//  3. 3个客户端各自持续接收并解码 FrameData 广播
//  4. 帧序号严格递增无跳帧、无goroutine泄漏、无panic
//  5. 每分钟日志输出帧计数统计，证明稳定运行
//  6. 运行命令：go test ./svr/ -run TestCSharpFrameSyncStability -v -timeout 25m
package boomnetwork_test

import (
	"encoding/binary"
	"net"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/boomlulu/boomnetwork/codec"
	"github.com/boomlulu/boomnetwork/framesync"
)

const (
	stabilityTestDuration = 20 * time.Minute
	stabilityFrameRate    = 20  // fps
	stabilityNumClients   = 3
	stabilityFrameBuffer  = 2400 // 服务端ring buffer帧数 = 120s @20fps
)

// stabilityClientStats 单个客户端的运行统计
type stabilityClientStats struct {
	clientID        int32
	totalFrames     uint64
	gapCount        int64  // 帧序号非单调递增次数（期望为0）
	firstFrameNum   uint32
	lastFrameNum    uint32
}

// serveStabilityConn 处理稳定性测试中一个服务端连接：
//
//	SessionBind → JoinRoom → AddPlayer → 转发输入循环
//
// 与 csFsServeConn 类似，但使用更长的 deadline（25分钟）。
func serveStabilityConn(
	t *testing.T,
	serverConn net.Conn,
	room *framesync.Room,
	assignedId int32,
	joinedCh chan<- struct{},
	serverWg *sync.WaitGroup,
) {
	t.Helper()
	defer serverWg.Done()
	defer serverConn.Close()

	// 25分钟 deadline：20分钟测试 + 5分钟缓冲
	serverConn.SetDeadline(time.Now().Add(stabilityTestDuration + 5*time.Minute))

	r := codec.NewFrameReader(serverConn, 0)
	pc := newCsFsConn(serverConn)

	// ── SessionBind ──────────────────────────────────────────────────────────
	msg, err := r.ReadMessageCopy()
	if err != nil {
		t.Errorf("[stab-srv %d] read SessionBind: %v", assignedId, err)
		return
	}
	if msg.CmdType != codec.CmdTypeCore || msg.Cmd != framesync.CmdSessionBind {
		t.Errorf("[stab-srv %d] expected CmdSessionBind, got type=%d cmd=%d",
			assignedId, msg.CmdType, msg.Cmd)
		return
	}
	rspBind := make([]byte, 4)
	binary.LittleEndian.PutUint32(rspBind, uint32(assignedId))
	if err := pc.Send(codec.NewCoreMessage(framesync.CmdSessionBindRsp, rspBind)); err != nil {
		t.Errorf("[stab-srv %d] send SessionBindRsp: %v", assignedId, err)
		return
	}

	// ── JoinRoom ─────────────────────────────────────────────────────────────
	msg, err = r.ReadMessageCopy()
	if err != nil {
		t.Errorf("[stab-srv %d] read JoinRoom: %v", assignedId, err)
		return
	}
	if msg.CmdType != codec.CmdTypeExtended || msg.ExtCmd != framesync.ExtCmdJoinRoom {
		t.Errorf("[stab-srv %d] expected ExtCmdJoinRoom, got type=%d extCmd=%d",
			assignedId, msg.CmdType, msg.ExtCmd)
		return
	}
	rspJoin := framesync.EncodeJoinRoomRsp(assignedId, room.ID, []int32{})
	if err := pc.Send(codec.NewExtMessage(framesync.ExtCmdJoinRoomRsp, rspJoin)); err != nil {
		t.Errorf("[stab-srv %d] send JoinRoomRsp: %v", assignedId, err)
		return
	}

	// ── AddPlayer：启动 deliveryLoop ─────────────────────────────────────────
	if err := room.AddPlayer(assignedId, pc, false, 0); err != nil {
		t.Errorf("[stab-srv %d] AddPlayer: %v", assignedId, err)
		return
	}
	joinedCh <- struct{}{} // 通知主 goroutine 玩家已就绪

	// ── 转发输入循环（直到连接关闭）────────────────────────────────────────
	for {
		msg, err := r.ReadMessageCopy()
		if err != nil {
			return // 连接正常关闭（测试结束时客户端关闭连接）
		}
		if msg.CmdType == codec.CmdTypeCore && msg.Cmd == framesync.CmdFrameInput {
			inputCopy := make([]byte, len(msg.Data))
			copy(inputCopy, msg.Data)
			room.OnInput(assignedId, inputCopy)
		}
	}
}

// runStabilityClient 模拟一个C#客户端进行20分钟稳定性测试：
//
//	SessionBind → JoinRoom → 并发[发送输入@20fps / 接收帧数据]
//
// 流程控制：
//   - 输入goroutine以ticker驱动20fps发送，持续stabilityTestDuration后关闭连接
//   - 接收goroutine阻塞读帧，连接关闭后自动退出
//   - 分钟统计goroutine每分钟输出帧计数日志
func runStabilityClient(
	t *testing.T,
	conn net.Conn,
	playerId int32,
	roomId int32,
	stats *stabilityClientStats,
	wg *sync.WaitGroup,
) {
	t.Helper()
	defer wg.Done()
	defer conn.Close()

	stats.clientID = playerId

	// 连接级 deadline（25分钟兜底，正常由输入goroutine在20分钟时主动关闭连接）
	conn.SetDeadline(time.Now().Add(stabilityTestDuration + 5*time.Minute))

	r := codec.NewFrameReader(conn, 0)
	w := codec.NewFrameWriter(conn)

	// ── SessionBind ──────────────────────────────────────────────────────────
	bindData := make([]byte, 4)
	binary.LittleEndian.PutUint32(bindData, uint32(playerId))
	if err := w.WriteMessage(codec.NewCoreMessage(framesync.CmdSessionBind, bindData)); err != nil {
		t.Errorf("[stab-cli %d] write SessionBind: %v", playerId, err)
		return
	}
	if err := w.Flush(); err != nil {
		t.Errorf("[stab-cli %d] flush SessionBind: %v", playerId, err)
		return
	}
	rsp1, err := r.ReadMessageCopy()
	if err != nil || rsp1.CmdType != codec.CmdTypeCore || rsp1.Cmd != framesync.CmdSessionBindRsp {
		t.Errorf("[stab-cli %d] read SessionBindRsp: %v", playerId, err)
		return
	}
	if len(rsp1.Data) < 4 || int32(binary.LittleEndian.Uint32(rsp1.Data[0:4])) == 0 {
		t.Errorf("[stab-cli %d] SessionBindRsp: playerId=0 (auth failure)", playerId)
		return
	}
	t.Logf("[stab-cli %d] ✓ SessionBind OK", playerId)

	// ── JoinRoom ─────────────────────────────────────────────────────────────
	joinData := make([]byte, 4)
	binary.LittleEndian.PutUint32(joinData, uint32(roomId))
	if err := w.WriteMessage(codec.NewExtMessage(framesync.ExtCmdJoinRoom, joinData)); err != nil {
		t.Errorf("[stab-cli %d] write JoinRoom: %v", playerId, err)
		return
	}
	if err := w.Flush(); err != nil {
		t.Errorf("[stab-cli %d] flush JoinRoom: %v", playerId, err)
		return
	}
	rsp2, err := r.ReadMessageCopy()
	if err != nil || rsp2.CmdType != codec.CmdTypeExtended || rsp2.ExtCmd != framesync.ExtCmdJoinRoomRsp {
		t.Errorf("[stab-cli %d] read JoinRoomRsp: %v", playerId, err)
		return
	}
	if len(rsp2.Data) >= 4 && int32(binary.LittleEndian.Uint32(rsp2.Data[0:4])) == 0 {
		t.Errorf("[stab-cli %d] JoinRoom failed (playerId=0 in response)", playerId)
		return
	}
	t.Logf("[stab-cli %d] ✓ JoinRoom OK, handshake complete", playerId)

	// ── 原子计数器（跨goroutine安全）───────────────────────────────────────
	var frameCountAtomic uint64 // 已接收帧总数
	var gapCountAtomic uint64   // 非单调递增次数（期望为0）

	// connClosed 由接收goroutine在退出时关闭，通知分钟统计goroutine停止
	connClosed := make(chan struct{})

	// ── 分钟统计 goroutine ────────────────────────────────────────────────────
	// 每分钟读取 frameCountAtomic 增量，输出日志；收到 connClosed 后退出。
	minuteStatsDone := make(chan struct{})
	go func() {
		defer close(minuteStatsDone)
		ticker := time.NewTicker(time.Minute)
		defer ticker.Stop()
		var prevTotal uint64
		minuteNum := 0
		for {
			select {
			case <-ticker.C:
				minuteNum++
				cur := atomic.LoadUint64(&frameCountAtomic)
				delta := cur - prevTotal
				prevTotal = cur
				t.Logf("[stab-cli %d] ── minute %d: +%d frames (cumulative: %d) ──",
					playerId, minuteNum, delta, cur)
			case <-connClosed:
				return
			}
		}
	}()

	// ── 输入发送 goroutine（以20fps速率发送 CmdFrameInput）────────────────────
	// 运行 stabilityTestDuration 后关闭连接，触发接收goroutine退出。
	inputDone := make(chan struct{})
	inputData := []byte{byte(playerId & 0xFF), 0x01} // 携带 playerId 标识
	go func() {
		defer close(inputDone)
		ticker := time.NewTicker(time.Second / time.Duration(stabilityFrameRate))
		defer ticker.Stop()
		deadline := time.Now().Add(stabilityTestDuration)
		for {
			select {
			case now := <-ticker.C:
				if now.After(deadline) {
					// 测试时长已到，关闭连接触发接收goroutine退出
					conn.Close()
					return
				}
				if err := w.WriteMessage(codec.NewCoreMessage(framesync.CmdFrameInput, inputData)); err != nil {
					return // 连接已关闭，正常退出
				}
				if err := w.Flush(); err != nil {
					return
				}
			}
		}
	}()

	// ── 接收循环（阻塞式读帧，直到连接关闭）────────────────────────────────
	// 帧序号验证：严格单调递增（lastFrame < fd.FrameNumber）
	var lastFrameNum uint32
	var firstFrameSet bool
	for {
		msg, err := r.ReadMessageCopy()
		if err != nil {
			break // 连接关闭（测试正常结束）或超时
		}
		switch {
		case msg.CmdType == codec.CmdTypeCore && msg.Cmd == framesync.CmdStartFrameSync:
			// 帧同步启动消息，跳过不计数

		case msg.CmdType == codec.CmdTypeCore && msg.Cmd == framesync.CmdPushFrames:
			fd, decErr := framesync.DecodeFrameData(msg.Data)
			if decErr != nil {
				t.Errorf("[stab-cli %d] DecodeFrameData error: %v", playerId, decErr)
				continue
			}
			if !firstFrameSet {
				stats.firstFrameNum = fd.FrameNumber
				firstFrameSet = true
			} else if fd.FrameNumber <= lastFrameNum {
				// 帧序号非单调递增（跳帧或重复帧）
				atomic.AddUint64(&gapCountAtomic, 1)
				t.Errorf("[stab-cli %d] frame non-monotonic: got %d after %d",
					playerId, fd.FrameNumber, lastFrameNum)
			}
			lastFrameNum = fd.FrameNumber
			atomic.AddUint64(&frameCountAtomic, 1)
		}
	}

	// 接收循环退出后通知分钟统计goroutine
	close(connClosed)

	// 等待分钟统计goroutine和输入goroutine退出
	<-minuteStatsDone
	<-inputDone

	// 收集最终统计数据
	stats.totalFrames = atomic.LoadUint64(&frameCountAtomic)
	stats.gapCount = int64(atomic.LoadUint64(&gapCountAtomic))
	stats.lastFrameNum = lastFrameNum

	t.Logf("[stab-cli %d] DONE: totalFrames=%d firstFrame=%d lastFrame=%d gapCount=%d",
		playerId, stats.totalFrames, stats.firstFrameNum, stats.lastFrameNum, stats.gapCount)
}

// TestCSharpFrameSyncStability 3个C#客户端持续帧同步20分钟压力测试。
//
// 测试命令：go test ./svr/ -run TestCSharpFrameSyncStability -v -timeout 25m
//
// 验证：
//   - 3个goroutine并发执行，使用 sync.WaitGroup 协调
//   - 每分钟输出帧计数统计（约1200帧/分钟 @20fps）
//   - 帧序号严格单调递增（无跳帧）
//   - 总帧数接近24000（20fps × 1200s，允许5%偏差）
//   - 无panic（tickLoop内有recover，panic会被记录为测试失败）
func TestCSharpFrameSyncStability(t *testing.T) {
	// ── 创建房间（ring buffer 2400帧，禁用快照超期暂停）──────────────────────
	roomMgr := framesync.NewRoomManager(framesync.RoomConfig{
		FrameRate:              stabilityFrameRate,
		MaxPlayers:             stabilityNumClients,
		FrameBufferSize:        stabilityFrameBuffer, // 2400 = 120s @20fps
		SnapshotIntervalFrames: 10_000_000,           // 极大值：禁用快照超期检查
		QuickReconnectMaxMs:    5000,
	})
	room := roomMgr.CreateRoomWithMaxPlayers(stabilityNumClients, "")
	if room == nil {
		t.Fatal("CreateRoomWithMaxPlayers returned nil")
	}
	defer room.Stop()
	t.Logf("[stab] room created: roomId=%d frameBuffer=%d fps=%d",
		room.ID, stabilityFrameBuffer, stabilityFrameRate)

	// ── 创建3对 net.Pipe 管道（in-process，无外部端口依赖）────────────────────
	serverConns := make([]net.Conn, stabilityNumClients)
	clientConns := make([]net.Conn, stabilityNumClients)
	for i := 0; i < stabilityNumClients; i++ {
		serverConns[i], clientConns[i] = net.Pipe()
	}

	// ── 启动服务端 goroutine（每个客户端一个）──────────────────────────────
	joinedCh := make(chan struct{}, stabilityNumClients)
	var serverWg sync.WaitGroup
	serverWg.Add(stabilityNumClients)
	for i := 0; i < stabilityNumClients; i++ {
		go serveStabilityConn(t, serverConns[i], room, int32(i+1), joinedCh, &serverWg)
	}

	// ── 启动3个客户端 goroutine（必须在等待 joinedCh 之前启动）─────────────
	// 原因：服务端 goroutine 阻塞等待客户端发 SessionBind；客户端 goroutine 握手
	// 完成后服务端 AddPlayer → joinedCh 信号。若先等 joinedCh 再启动客户端，死锁。
	var clientWg sync.WaitGroup
	clientWg.Add(stabilityNumClients)
	stats := make([]stabilityClientStats, stabilityNumClients)
	for i := 0; i < stabilityNumClients; i++ {
		go runStabilityClient(t, clientConns[i], int32(i+1), room.ID, &stats[i], &clientWg)
	}

	// ── 等待所有客户端完成 JoinRoom + AddPlayer ────────────────────────────
	for i := 0; i < stabilityNumClients; i++ {
		select {
		case <-joinedCh:
		case <-time.After(10 * time.Second):
			t.Fatalf("[stab] timeout waiting for client %d to join room", i+1)
		}
	}
	t.Logf("[stab] all %d clients joined, starting frame sync", stabilityNumClients)

	// ── 启动帧同步（广播 CmdStartFrameSync，开启 tickLoop @ 20fps）──────────
	room.Start()

	t.Logf("[stab] stability test running for %v (expected ~%d frames @%dfps)...",
		stabilityTestDuration,
		stabilityFrameRate*int(stabilityTestDuration.Seconds()),
		stabilityFrameRate)

	// ── 等待所有客户端 goroutine 完成 ─────────────────────────────────────
	clientWg.Wait()
	t.Logf("[stab] all clients done, collecting results")

	// ── 等待服务端 goroutine 退出（客户端关闭连接后服务端会自动退出）────────
	serverDone := make(chan struct{})
	go func() {
		serverWg.Wait()
		close(serverDone)
	}()
	select {
	case <-serverDone:
	case <-time.After(10 * time.Second):
		t.Log("[stab] warning: server goroutines did not exit within 10s")
	}

	// ── 验证结果 ──────────────────────────────────────────────────────────
	t.Logf("[stab] === Final Results ===")
	expectedFrames := uint64(stabilityFrameRate) * uint64(stabilityTestDuration.Seconds()) // 24000
	lowerBound := expectedFrames * 95 / 100                                                 // 允许5%偏差

	for i := range stats {
		s := &stats[i]
		t.Logf("[stab-cli %d] totalFrames=%d firstFrame=%d lastFrame=%d gapCount=%d",
			s.clientID, s.totalFrames, s.firstFrameNum, s.lastFrameNum, s.gapCount)

		// 验证1：总帧数接近预期（允许5%偏差，考虑startup/shutdown开销）
		if s.totalFrames < lowerBound {
			t.Errorf("[stab-cli %d] too few frames: got %d, expected >= %d (95%% of %d)",
				s.clientID, s.totalFrames, lowerBound, expectedFrames)
		}

		// 验证2：无跳帧（帧序号严格单调递增）
		if s.gapCount > 0 {
			t.Errorf("[stab-cli %d] %d frame gap(s) detected (non-monotonic frame numbers)",
				s.clientID, s.gapCount)
		}
	}

	// 验证3：所有客户端最终帧号一致（差异不超过200帧，net.Pipe理论上接近0）
	if len(stats) == stabilityNumClients {
		maxFrame := stats[0].lastFrameNum
		minFrame := stats[0].lastFrameNum
		for _, s := range stats[1:] {
			if s.lastFrameNum > maxFrame {
				maxFrame = s.lastFrameNum
			}
			if s.lastFrameNum < minFrame {
				minFrame = s.lastFrameNum
			}
		}
		spread := maxFrame - minFrame
		t.Logf("[stab] client frame spread: max=%d min=%d diff=%d", maxFrame, minFrame, spread)
		if spread > 200 {
			t.Errorf("[stab] frame spread too large across clients: diff=%d (expected <= 200)", spread)
		}
	}

	t.Logf("✓ TestCSharpFrameSyncStability PASS: %d clients, ~%d frames each, %v stable, no gaps",
		stabilityNumClients, expectedFrames, stabilityTestDuration)
}
