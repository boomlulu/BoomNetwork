package main

import (
	"encoding/binary"
	"flag"
	"fmt"
	"math/rand"
	"net"
	"os"
	"runtime"
	"sync"
	"sync/atomic"
	"time"

	"github.com/boomlulu/boomnetwork/codec"
	"github.com/boomlulu/boomnetwork/framesync"
)

var (
	addr           = flag.String("addr", "127.0.0.1:9000", "server address")
	rooms          = flag.Int("rooms", 750, "number of rooms")
	playersPerRoom = flag.Int("players", 4, "players per room")
	duration       = flag.Duration("duration", 10*time.Second, "test duration")
	frameRate      = flag.Int("fps", 20, "server frame rate")
	inputSize      = flag.Int("input-size", 32, "input payload size in bytes")
	// 网络模拟
	simLatency  = flag.Int("latency", 0, "simulated one-way latency in ms (0=off)")
	simJitter   = flag.Int("jitter", 0, "simulated latency jitter in ms (0=off)")
	simLossRate = flag.Float64("loss", 0, "simulated packet loss rate 0.0-1.0 (0=off)")
)

// 统计
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
	fmt.Println("  BoomNetwork Stress Test")
	fmt.Printf("  %d rooms × %d players = %d total\n", *rooms, *playersPerRoom, totalPlayers)
	fmt.Printf("  Duration: %s, FrameRate: %d fps\n", *duration, *frameRate)
	fmt.Printf("  Input payload: %d bytes\n", *inputSize)
	if *simLatency > 0 || *simLossRate > 0 {
		fmt.Printf("  Network sim: latency=%dms jitter=%dms loss=%.1f%%\n", *simLatency, *simJitter, *simLossRate*100)
	}
	fmt.Println("==========================================")

	// 启动内嵌服务器
	es := startEmbeddedServer()
	defer es.Close()
	time.Sleep(300 * time.Millisecond)

	// 初始内存
	var memBefore runtime.MemStats
	runtime.GC()
	runtime.ReadMemStats(&memBefore)

	startTime := time.Now()

	// 启动客户端
	var wg sync.WaitGroup
	stopCh := make(chan struct{})

	for i := 0; i < totalPlayers; i++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			runClient(stopCh)
		}()
		if i%100 == 99 {
			time.Sleep(30 * time.Millisecond)
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
			fmt.Printf("\n[WARN] Timeout waiting for connections: %d/%d\n", c, totalPlayers)
			goto proceed
		default:
			time.Sleep(200 * time.Millisecond)
		}
	}
proceed:

	connected := atomic.LoadInt64(&totalConnected)
	fmt.Printf("\n[Connected] %d/%d (failed: %d)\n", connected, totalPlayers, atomic.LoadInt64(&totalConnectFail))

	// 重置计数器，只统计稳定运行期间的数据
	atomic.StoreInt64(&totalFrameRecv, 0)
	atomic.StoreInt64(&totalInputSent, 0)
	atomic.StoreInt64(&totalBytesSent, 0)
	atomic.StoreInt64(&totalBytesRecv, 0)

	fmt.Printf("[Running] Stress test for %s...\n", *duration)

	// Soak test: 定时采样内存（每 30 秒或 duration/10，取较大值）
	sampleInterval := *duration / 10
	if sampleInterval < 30*time.Second {
		sampleInterval = 30 * time.Second
	}
	if *duration >= 60*time.Second {
		fmt.Printf("[Soak] Memory sampling every %s\n", sampleInterval)
		soakStart := time.Now()
		for time.Since(soakStart) < *duration {
			sleepDur := sampleInterval
			remaining := *duration - time.Since(soakStart)
			if sleepDur > remaining {
				sleepDur = remaining
			}
			if sleepDur <= 0 {
				break
			}
			time.Sleep(sleepDur)

			var ms runtime.MemStats
			runtime.ReadMemStats(&ms)
			elapsed := time.Since(soakStart)
			fmt.Printf("[Soak %s] Heap=%dMB Alloc=%dMB GC=%d Goroutines=%d Frames=%d\n",
				elapsed.Round(time.Second),
				ms.HeapInuse/1024/1024,
				ms.HeapAlloc/1024/1024,
				ms.NumGC,
				runtime.NumGoroutine(),
				atomic.LoadInt64(&totalFrameRecv))
		}
	} else {
		time.Sleep(*duration)
	}

	// 停止
	close(stopCh)
	time.Sleep(500 * time.Millisecond) // 等发送完

	elapsed := *duration

	// 最终内存
	var memAfter runtime.MemStats
	runtime.GC()
	runtime.ReadMemStats(&memAfter)

	// --- 报告 ---
	framesRecv := atomic.LoadInt64(&totalFrameRecv)
	inputsSent := atomic.LoadInt64(&totalInputSent)
	bytesSent := atomic.LoadInt64(&totalBytesSent)
	bytesRecv := atomic.LoadInt64(&totalBytesRecv)

	elapsedSec := elapsed.Seconds()
	sendBW := float64(bytesSent) / elapsedSec
	recvBW := float64(bytesRecv) / elapsedSec

	_ = startTime

	fmt.Println()
	fmt.Println("==========================================")
	fmt.Println("  Stress Test Results")
	fmt.Println("==========================================")
	fmt.Printf("  Duration:            %.1fs\n", elapsedSec)
	fmt.Printf("  Connected:           %d / %d\n", connected, totalPlayers)
	fmt.Println()
	fmt.Println("  [ Throughput ]")
	fmt.Printf("  Frames received:     %d total (%.0f/s all clients)\n", framesRecv, float64(framesRecv)/elapsedSec)
	if connected > 0 {
		fmt.Printf("  Per client:          %.1f frames/s\n", float64(framesRecv)/float64(connected)/elapsedSec)
	}
	fmt.Printf("  Inputs sent:         %d total (%.0f/s all clients)\n", inputsSent, float64(inputsSent)/elapsedSec)
	fmt.Println()
	fmt.Println("  [ Bandwidth ]")
	fmt.Printf("  Upload (all):        %.2f MB/s (clients → server)\n", sendBW/1e6)
	fmt.Printf("  Download (all):      %.2f MB/s (server → clients)\n", recvBW/1e6)
	if connected > 0 {
		fmt.Printf("  Per client upload:   %.2f KB/s\n", sendBW/float64(connected)/1024)
		fmt.Printf("  Per client download: %.2f KB/s\n", recvBW/float64(connected)/1024)
	}
	fmt.Printf("  Total transferred:   %.2f MB up + %.2f MB down\n", float64(bytesSent)/1e6, float64(bytesRecv)/1e6)
	fmt.Println()
	fmt.Println("  [ Server Memory ]")
	fmt.Printf("  Heap in use:         %.2f MB\n", float64(memAfter.HeapInuse)/1e6)
	fmt.Printf("  Heap alloc:          %.2f MB\n", float64(memAfter.HeapAlloc)/1e6)
	fmt.Printf("  Stack in use:        %.2f MB\n", float64(memAfter.StackInuse)/1e6)
	fmt.Printf("  Total sys:           %.2f MB\n", float64(memAfter.Sys)/1e6)
	fmt.Printf("  GC cycles:           %d\n", memAfter.NumGC-memBefore.NumGC)
	fmt.Printf("  Goroutines:          %d\n", runtime.NumGoroutine())
	fmt.Println()
	fmt.Println("  [ CPU ]")
	fmt.Printf("  NumCPU:              %d\n", runtime.NumCPU())
	fmt.Printf("  GOMAXPROCS:          %d\n", runtime.GOMAXPROCS(0))
	fmt.Println("==========================================")

	wg.Wait()
}

// --- 内嵌服务器 ---

type embeddedServer struct {
	listener net.Listener
	mu       sync.Mutex
	roomList []*framesync.Room
	roomFill int
}

func startEmbeddedServer() *embeddedServer {
	es := &embeddedServer{}
	ln, err := net.Listen("tcp", *addr)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Listen failed: %v\n", err)
		os.Exit(1)
	}
	es.listener = ln
	fmt.Printf("[Server] Listening on %s\n", *addr)
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
	nextId := int32(0)
	for {
		conn, err := es.listener.Accept()
		if err != nil {
			return
		}
		if tcp, ok := conn.(*net.TCPConn); ok {
			tcp.SetNoDelay(true)
		}

		nextId++
		id := nextId
		go es.handleConn(conn, id)
	}
}

func (es *embeddedServer) handleConn(conn net.Conn, playerId int32) {
	reader := codec.NewFrameReader(conn)
	writer := codec.NewFrameWriter(conn)

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
	rsp := make([]byte, 4)
	binary.LittleEndian.PutUint32(rsp, uint32(playerId))
	writer.WriteMessage(&codec.Message{Cmd: framesync.CmdSessionBindRsp, HasSeq: msg.HasSeq, Seq: msg.Seq, Data: rsp})
	writer.Flush()

	pc := &writerConn{writer: writer}
	room.AddPlayer(playerId, pc, false, 0)

	if shouldStart {
		room.Start()
	}

	// 读输入
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

// --- 模拟客户端 ---

func runClient(stopCh chan struct{}) {
	conn, err := net.Dial("tcp", *addr)
	if err != nil {
		atomic.AddInt64(&totalConnectFail, 1)
		return
	}
	defer conn.Close()

	if tcp, ok := conn.(*net.TCPConn); ok {
		tcp.SetNoDelay(true)
	}

	reader := codec.NewFrameReader(conn)
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
	rand.Read(inputData)

	interval := time.Duration(1000 / *frameRate) * time.Millisecond
	ticker := time.NewTicker(interval)
	defer ticker.Stop()

	for {
		select {
		case <-stopCh:
			return
		case <-recvDone:
			return
		case <-ticker.C:
			// 网络模拟: 丢包
			if *simLossRate > 0 && rand.Float64() < *simLossRate {
				continue
			}
			// 网络模拟: 延迟 + 抖动
			if *simLatency > 0 {
				delay := *simLatency
				if *simJitter > 0 {
					delay += rand.Intn(*simJitter*2) - *simJitter
					if delay < 0 {
						delay = 0
					}
				}
				time.Sleep(time.Duration(delay) * time.Millisecond)
			}
			writer.WriteMessage(&codec.Message{Cmd: framesync.CmdFrameInput, Data: inputData})
			writer.Flush()
			atomic.AddInt64(&totalInputSent, 1)
			atomic.AddInt64(&totalBytesSent, int64(codec.EncodedSize(&codec.Message{Cmd: framesync.CmdFrameInput, Data: inputData})))
		}
	}
}
