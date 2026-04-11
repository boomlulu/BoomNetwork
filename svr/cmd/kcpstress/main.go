package main

import (
	"flag"
	"fmt"
	"log/slog"
	"os"
	"runtime"
	"sync"
	"sync/atomic"
	"time"

	"github.com/boomlulu/boomnetwork/codec"
	"github.com/boomlulu/boomnetwork/framesync"
	kcp "github.com/xtaci/kcp-go/v5"
)

var (
	addr           = flag.String("addr", "127.0.0.1:9000", "server address")
	rooms          = flag.Int("rooms", 250, "number of rooms")
	playersPerRoom = flag.Int("players", 4, "players per room")
	duration       = flag.Duration("duration", 10*time.Second, "test duration")
	frameRate      = flag.Int("fps", 20, "frame rate")
	inputSize      = flag.Int("input-size", 32, "input payload bytes")
)

var (
	totalConnected   int64
	totalConnectFail int64
	totalFrameRecv   int64
	totalInputSent   int64
	totalBytesSent   int64
	totalBytesRecv   int64
)

func main() {
	flag.Parse()

	totalPlayers := *rooms * *playersPerRoom
	fmt.Println("==========================================")
	fmt.Println("  BoomNetwork KCP Stress Test")
	fmt.Printf("  %d rooms × %d players = %d total\n", *rooms, *playersPerRoom, totalPlayers)
	fmt.Printf("  Duration: %s, FrameRate: %d fps\n", *duration, *frameRate)
	fmt.Println("==========================================")

	// 内嵌 KCP 服务器
	es := startServer()
	defer es.Close()
	time.Sleep(500 * time.Millisecond)

	var memBefore runtime.MemStats
	runtime.GC()
	runtime.ReadMemStats(&memBefore)

	// 启动客户端
	var wg sync.WaitGroup
	stopCh := make(chan struct{})

	for i := 0; i < totalPlayers; i++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			runClient(stopCh)
		}()
		if i%50 == 49 {
			time.Sleep(50 * time.Millisecond)
		}
	}

	// 等连接
	deadline := time.After(30 * time.Second)
	for {
		c := atomic.LoadInt64(&totalConnected)
		f := atomic.LoadInt64(&totalConnectFail)
		if c+f >= int64(totalPlayers) {
			break
		}
		select {
		case <-deadline:
			goto proceed
		default:
			time.Sleep(200 * time.Millisecond)
		}
	}
proceed:

	connected := atomic.LoadInt64(&totalConnected)
	fmt.Printf("\n[Connected] %d/%d (failed: %d)\n", connected, totalPlayers, atomic.LoadInt64(&totalConnectFail))

	atomic.StoreInt64(&totalFrameRecv, 0)
	atomic.StoreInt64(&totalInputSent, 0)
	atomic.StoreInt64(&totalBytesSent, 0)
	atomic.StoreInt64(&totalBytesRecv, 0)

	fmt.Printf("[Running] KCP stress test for %s...\n", *duration)
	time.Sleep(*duration)

	close(stopCh)
	time.Sleep(500 * time.Millisecond)

	elapsed := *duration

	var memAfter runtime.MemStats
	runtime.GC()
	runtime.ReadMemStats(&memAfter)

	framesRecv := atomic.LoadInt64(&totalFrameRecv)
	inputsSent := atomic.LoadInt64(&totalInputSent)
	bytesSent := atomic.LoadInt64(&totalBytesSent)
	bytesRecv := atomic.LoadInt64(&totalBytesRecv)
	elapsedSec := elapsed.Seconds()

	fmt.Println()
	fmt.Println("==========================================")
	fmt.Println("  KCP Stress Test Results")
	fmt.Println("==========================================")
	fmt.Printf("  Duration:            %.1fs\n", elapsedSec)
	fmt.Printf("  Connected:           %d / %d\n", connected, totalPlayers)
	fmt.Println()
	fmt.Println("  [ Throughput ]")
	fmt.Printf("  Frames received:     %d total (%.0f/s)\n", framesRecv, float64(framesRecv)/elapsedSec)
	if connected > 0 {
		fmt.Printf("  Per client:          %.1f frames/s\n", float64(framesRecv)/float64(connected)/elapsedSec)
	}
	fmt.Printf("  Inputs sent:         %d total (%.0f/s)\n", inputsSent, float64(inputsSent)/elapsedSec)
	fmt.Println()
	fmt.Println("  [ Bandwidth ]")
	fmt.Printf("  Upload (all):        %.2f MB/s\n", float64(bytesSent)/elapsedSec/1e6)
	fmt.Printf("  Download (all):      %.2f MB/s\n", float64(bytesRecv)/elapsedSec/1e6)
	if connected > 0 {
		fmt.Printf("  Per client upload:   %.2f KB/s\n", float64(bytesSent)/float64(connected)/elapsedSec/1024)
		fmt.Printf("  Per client download: %.2f KB/s\n", float64(bytesRecv)/float64(connected)/elapsedSec/1024)
	}
	fmt.Println()
	fmt.Println("  [ Server Memory ]")
	fmt.Printf("  Heap in use:         %.2f MB\n", float64(memAfter.HeapInuse)/1e6)
	fmt.Printf("  Heap alloc:          %.2f MB\n", float64(memAfter.HeapAlloc)/1e6)
	fmt.Printf("  Stack in use:        %.2f MB\n", float64(memAfter.StackInuse)/1e6)
	fmt.Printf("  Total sys:           %.2f MB\n", float64(memAfter.Sys)/1e6)
	fmt.Printf("  GC cycles:           %d\n", memAfter.NumGC-memBefore.NumGC)
	fmt.Printf("  Goroutines:          %d\n", runtime.NumGoroutine())
	fmt.Println("==========================================")

	wg.Wait()
}

// 内嵌 KCP 帧同步服务器
type embeddedServer struct {
	listener *kcp.Listener
	mu       sync.Mutex
	roomList []*framesync.Room
	roomFill int
	nextId   int32
}

func startServer() *embeddedServer {
	es := &embeddedServer{}
	ln, err := kcp.ListenWithOptions(*addr, nil, 0, 0)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Listen failed: %v\n", err)
		os.Exit(1)
	}
	es.listener = ln
	fmt.Printf("[KCP Server] Listening on %s\n", *addr)
	go es.acceptLoop()
	return es
}

func (es *embeddedServer) Close() {
	es.listener.Close()
	es.mu.Lock()
	for _, r := range es.roomList {
		r.Stop()
	}
	es.mu.Unlock()
}

func (es *embeddedServer) acceptLoop() {
	for {
		conn, err := es.listener.AcceptKCP()
		if err != nil {
			return
		}
		conn.SetStreamMode(true)
		conn.SetWriteDelay(false)
		conn.SetNoDelay(1, 10, 2, 1)
		conn.SetWindowSize(256, 256)

		es.mu.Lock()
		es.nextId++
		id := es.nextId
		es.mu.Unlock()

		go es.handleConn(conn, id)
	}
}

func (es *embeddedServer) handleConn(conn *kcp.UDPSession, playerId int32) {
	reader := codec.NewFrameReader(conn, 0)
	writer := codec.NewFrameWriter(conn)

	// 读 SessionBind
	msg, err := reader.ReadMessageCopy()
	if err != nil || msg.Cmd != framesync.CmdSessionBind {
		conn.Close()
		return
	}

	// 分配房间
	es.mu.Lock()
	if es.roomFill == 0 || es.roomFill >= *playersPerRoom {
		es.roomList = append(es.roomList, framesync.NewRoom(int32(*frameRate)))
		es.roomFill = 0
	}
	room := es.roomList[len(es.roomList)-1]
	es.roomFill++
	shouldStart := es.roomFill >= *playersPerRoom
	es.mu.Unlock()

	// 回复
	rspData := make([]byte, 4)
	writer.WriteMessage(&codec.Message{
		Cmd: framesync.CmdSessionBindRsp, HasSeq: msg.HasSeq, Seq: msg.Seq, Data: rspData,
	})
	writer.Flush()

	pc := &writerConn{writer: writer}
	if err := room.AddPlayer(playerId, pc, false, 0); err != nil {
		slog.Warn("kcpstress AddPlayer failed", "playerId", playerId, "err", err)
		return
	}
	if shouldStart {
		room.Start()
	}

	for {
		imsg, err := reader.ReadMessageCopy()
		if err != nil {
			return
		}
		if imsg.Cmd == framesync.CmdFrameInput {
			room.OnInput(playerId, imsg.Data)
		}
	}
}

type writerConn struct {
	writer *codec.FrameWriter
	mu     sync.Mutex
}

func (w *writerConn) Send(msg *codec.Message) error {
	w.mu.Lock()
	defer w.mu.Unlock()
	if err := w.writer.WriteMessage(msg); err != nil {
		return err
	}
	return w.writer.Flush()
}

func (w *writerConn) Close() error { return nil }

// KCP 客户端
func runClient(stopCh chan struct{}) {
	conn, err := kcp.DialWithOptions(*addr, nil, 0, 0)
	if err != nil {
		atomic.AddInt64(&totalConnectFail, 1)
		return
	}
	defer conn.Close()

	conn.SetStreamMode(true)
	conn.SetWriteDelay(false)
	conn.SetNoDelay(1, 10, 2, 1)
	conn.SetWindowSize(256, 256)

	reader := codec.NewFrameReader(conn, 0)
	writer := codec.NewFrameWriter(conn)

	// Bind
	writer.WriteMessage(&codec.Message{Cmd: framesync.CmdSessionBind, HasSeq: true, Seq: 1})
	writer.Flush()

	rsp, err := reader.ReadMessageCopy()
	if err != nil || rsp.Cmd != framesync.CmdSessionBindRsp {
		atomic.AddInt64(&totalConnectFail, 1)
		return
	}
	atomic.AddInt64(&totalConnected, 1)

	// 收消息
	recvDone := make(chan struct{})
	go func() {
		defer close(recvDone)
		for {
			msg, err := reader.ReadMessageCopy()
			if err != nil {
				return
			}
			atomic.AddInt64(&totalBytesRecv, int64(codec.EncodedSize(msg)))
			if msg.Cmd == framesync.CmdPushFrames {
				atomic.AddInt64(&totalFrameRecv, 1)
			}
		}
	}()

	// 发输入
	inputData := make([]byte, *inputSize)
	interval := time.Duration(1000 / *frameRate) * time.Millisecond
	ticker := time.NewTicker(interval)
	defer ticker.Stop()

	inputMsg := &codec.Message{Cmd: framesync.CmdFrameInput, Data: inputData}

	for {
		select {
		case <-stopCh:
			return
		case <-recvDone:
			return
		case <-ticker.C:
			writer.WriteMessage(inputMsg)
			writer.Flush()
			atomic.AddInt64(&totalInputSent, 1)
			atomic.AddInt64(&totalBytesSent, int64(codec.EncodedSize(inputMsg)))
		}
	}
}
