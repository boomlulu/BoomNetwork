package main

import (
	"encoding/binary"
	"fmt"
	"os"
	"os/signal"
	"sync"
	"syscall"

	"github.com/boom/boomnetwork/codec"
	"github.com/boom/boomnetwork/framesync"
	"github.com/boom/boomnetwork/session"
	"github.com/boom/boomnetwork/transport"
)

// connPlayerMap 连接 ID → 玩家 ID 映射
var connPlayerMap sync.Map

// room 全局房间（简化版，实际应支持多房间）
var room = framesync.NewRoom(20) // 20帧/秒

// autoStartCount 自动开始帧同步的玩家数（0=手动）
var autoStartCount = 2

var playerCounter int32
var playerMu sync.Mutex

func main() {
	addr := ":9000"
	if len(os.Args) > 1 {
		addr = os.Args[1]
	}

	router := session.NewRouter()

	router.On(framesync.CmdSessionBind, handleSessionBind)
	router.On(framesync.CmdFrameInput, handleFrameInput)

	server := transport.NewTcpServer(router.AsTransportHandler())
	if err := server.Listen(addr); err != nil {
		fmt.Fprintf(os.Stderr, "Failed: %v\n", err)
		os.Exit(1)
	}
	fmt.Printf("[FrameSync Server] Running on %s (frameRate=20, autoStart=%d players)\n", addr, autoStartCount)

	sig := make(chan os.Signal, 1)
	signal.Notify(sig, syscall.SIGINT, syscall.SIGTERM)
	<-sig

	fmt.Println("\n[FrameSync Server] Shutting down...")
	room.Stop()
	server.Close()
}

func handleSessionBind(conn *transport.Conn, msg *codec.Message) *codec.Message {
	playerMu.Lock()
	playerCounter++
	playerId := playerCounter
	playerMu.Unlock()

	connPlayerMap.Store(conn.ID, playerId)
	room.AddPlayer(playerId, conn)

	// 响应：玩家 ID
	rsp := make([]byte, 4)
	binary.LittleEndian.PutUint32(rsp, uint32(playerId))

	fmt.Printf("[Server] Player %d bound (conn %d)\n", playerId, conn.ID)

	// 自动开始
	if autoStartCount > 0 && room.PlayerCount() >= autoStartCount {
		room.Start()
	}

	return &codec.Message{Cmd: framesync.CmdSessionBindRsp, Data: rsp}
}

func handleFrameInput(conn *transport.Conn, msg *codec.Message) *codec.Message {
	val, ok := connPlayerMap.Load(conn.ID)
	if !ok {
		return nil
	}
	playerId := val.(int32)
	room.OnInput(playerId, msg.Data)
	return nil // 输入不需要回复
}
