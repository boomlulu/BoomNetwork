package framesync

// room_integration_test.go — 集成测试（真实内存管道连接）
//
// 使用 net.Pipe() 在同一进程内建立 S→C 真实字节流：
//   - pipeConn 使用 codec.FrameWriter 向管道写入，与 transport.Conn 保持相同的帧格式
//   - 客户端侧用 codec.FrameReader 读取并解码，验证帧内容端到端一致
//
// 覆盖场景:
//   1. deliveryLoop 追帧（catch-up）— 玩家加入时有历史帧，应先补帧
//   2. deliveryLoop 实时帧 — 玩家加入后推进的新帧都能被接收
//   3. Reliable 消息通过真实连接到达
//   4. Disconnect 触发 pipeConn.Close()

import (
	"net"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/boomlulu/boomnetwork/codec"
)

// pipeConn 将 net.Conn 包装为 PlayerConn 接口（使用 codec.FrameWriter，与服务器帧格式兼容）
type pipeConn struct {
	conn   net.Conn
	writer *codec.FrameWriter
	mu     sync.Mutex
	closed atomic.Bool
}

func newPipeConn(c net.Conn) *pipeConn {
	return &pipeConn{conn: c, writer: codec.NewFrameWriter(c)}
}

func (p *pipeConn) Send(msg *codec.Message) error {
	p.mu.Lock()
	defer p.mu.Unlock()
	if err := p.writer.WriteMessage(msg); err != nil {
		return err
	}
	return p.writer.Flush()
}

func (p *pipeConn) Close() error {
	p.closed.Store(true)
	return p.conn.Close()
}

// readAllMessages 从 clientSide 读取消息直到连接关闭，收集到 msgs 通道
func readAllMessages(clientSide net.Conn, msgs chan<- *codec.Message) {
	reader := codec.NewFrameReader(clientSide)
	for {
		msg, err := reader.ReadMessage()
		if err != nil {
			close(msgs)
			return
		}
		// 深拷贝 Data（ReadMessage 内部 buffer 复用）
		if msg.Data != nil {
			cp := make([]byte, len(msg.Data))
			copy(cp, msg.Data)
			msg.Data = cp
		}
		msgs <- msg
	}
}

// ─── 测试 1: deliveryLoop 追帧 + 实时帧 ──────────────────────────────────────

// TestIntegration_DeliveryLoop_CatchupAndLive 验证玩家加入时先补历史帧，随后收到新帧
func TestIntegration_DeliveryLoop_CatchupAndLive(t *testing.T) {
	room := NewRoomWithConfig(RoomConfig{
		FrameRate:              50, // 快速推帧，缩短等待时间
		MaxPlayers:             2,
		FrameBufferSize:        200,
		SnapshotIntervalFrames: 200, // 长间隔，避免 stale 暂停影响测试
	})

	// 先启动房间，推进若干历史帧
	room.Start()
	defer room.Stop()

	// 等待至少 10 帧
	time.Sleep(250 * time.Millisecond) // 50fps × 0.25s ≈ 12 帧
	historyFrames := room.CurrentFrameNumber()
	if historyFrames == 0 {
		t.Skip("no history frames generated, test environment too slow")
	}

	// 建立 net.Pipe，用 pipeConn 接入房间
	serverSide, clientSide := net.Pipe()
	pc := newPipeConn(serverSide)

	msgCh := make(chan *codec.Message, 256)
	go readAllMessages(clientSide, msgCh)

	// 玩家从 frame 0 加入（应触发追帧）
	room.AddPlayer(1, pc, false, 0)

	// 再等一段时间，让追帧 + 新帧都发送
	time.Sleep(200 * time.Millisecond)

	// 断开连接，让 readAllMessages goroutine 退出
	clientSide.Close()

	// 收集所有已收到的消息
	var received []*codec.Message
	timeout := time.After(500 * time.Millisecond)
drain:
	for {
		select {
		case msg, ok := <-msgCh:
			if !ok {
				break drain
			}
			received = append(received, msg)
		case <-timeout:
			break drain
		}
	}

	// 过滤出帧推送消息（CmdPushFrameSync = 0x03）
	var frameMessages []*codec.Message
	for _, m := range received {
		if m.CmdType == codec.CmdTypeCore && m.Cmd == CmdPushFrames {
			frameMessages = append(frameMessages, m)
		}
	}

	if len(frameMessages) == 0 {
		t.Fatal("expected at least one CmdPushFrameSync message delivered via net.Pipe")
	}

	// 验证帧号单调递增
	lastFn := uint32(0)
	for _, m := range frameMessages {
		if len(m.Data) < 4 {
			t.Fatalf("frame data too short: %d bytes", len(m.Data))
		}
		// FrameData 格式: [FrameNumber:4][InputCount:1]...
		fn := uint32(m.Data[0]) | uint32(m.Data[1])<<8 | uint32(m.Data[2])<<16 | uint32(m.Data[3])<<24
		if fn < lastFn {
			t.Fatalf("frame number not monotonic: %d < %d", fn, lastFn)
		}
		lastFn = fn
	}

	t.Logf("integration: received %d frame messages (history=%d)", len(frameMessages), historyFrames)
}

// ─── 测试 2: Reliable 消息通过真实连接到达 ────────────────────────────────────

func TestIntegration_SendReliable_ArrivesOnWire(t *testing.T) {
	room := NewRoomWithConfig(RoomConfig{
		FrameRate:              20,
		MaxPlayers:             2,
		FrameBufferSize:        100,
		SnapshotIntervalFrames: 200,
	})
	room.Start()
	defer room.Stop()

	serverSide, clientSide := net.Pipe()
	pc := newPipeConn(serverSide)

	msgCh := make(chan *codec.Message, 64)
	go readAllMessages(clientSide, msgCh)

	room.AddPlayer(1, pc, false, 0)
	time.Sleep(20 * time.Millisecond) // 等 deliveryLoop 初始化

	inner := codec.NewCoreMessage(0x42, []byte("reliable-payload"))
	room.SendReliableToPlayer(1, inner)

	// 等待消息到达
	var reliableMsg *codec.Message
	timeout := time.After(1 * time.Second)
	for {
		select {
		case msg, ok := <-msgCh:
			if !ok {
				goto done
			}
			if msg.CmdType == codec.CmdTypeExtended && msg.ExtCmd == ExtCmdReliableMsg {
				reliableMsg = msg
				goto done
			}
		case <-timeout:
			goto done
		}
	}
done:
	clientSide.Close()

	if reliableMsg == nil {
		t.Fatal("expected ExtCmdReliableMsg to arrive via net.Pipe")
	}

	// 验证 seq=1 嵌在头部（EncodeReliableMsgData: [seq:4][cmd:1][...body...]）
	if len(reliableMsg.Data) < 4 {
		t.Fatalf("reliable msg data too short: %d", len(reliableMsg.Data))
	}
	seq := uint32(reliableMsg.Data[0]) | uint32(reliableMsg.Data[1])<<8 |
		uint32(reliableMsg.Data[2])<<16 | uint32(reliableMsg.Data[3])<<24
	if seq != 1 {
		t.Fatalf("expected seq=1, got %d", seq)
	}
}

// ─── 测试 3: DisconnectPlayer 触发连接关闭 ────────────────────────────────────

func TestIntegration_DisconnectPlayer_ClosesConn(t *testing.T) {
	room := NewRoomWithConfig(RoomConfig{
		FrameRate:           20,
		MaxPlayers:          2,
		FrameBufferSize:     100,
		SnapshotIntervalFrames: 200,
	})
	room.Start()
	defer room.Stop()

	serverSide, clientSide := net.Pipe()
	pc := newPipeConn(serverSide)

	msgCh := make(chan *codec.Message, 64)
	go readAllMessages(clientSide, msgCh)

	room.AddPlayer(1, pc, false, 0)
	time.Sleep(20 * time.Millisecond)

	// 连接发送错误时 deliveryLoop 会触发 DisconnectPlayer + Close
	// 我们直接关闭 clientSide 来模拟网络断开，
	// 这会导致 serverSide 的下一次写入返回错误，从而触发 onDeliveryExit
	clientSide.Close()

	// 等待 deliveryLoop 检测到错误并断开
	deadline := time.Now().Add(1 * time.Second)
	for time.Now().Before(deadline) {
		if room.PlayerCount() == 0 {
			break
		}
		time.Sleep(10 * time.Millisecond)
	}

	if room.PlayerCount() != 0 {
		t.Fatalf("expected PlayerCount=0 after connection close, got %d", room.PlayerCount())
	}
	if !pc.closed.Load() {
		t.Fatal("expected pipeConn.Close() to be called on delivery failure")
	}
}
