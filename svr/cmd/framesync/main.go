package main

import (
	"encoding/binary"
	"flag"
	"fmt"
	"os"
	"os/signal"
	"sync"
	"syscall"
	"time"

	"github.com/boom/boomnetwork/codec"
	"github.com/boom/boomnetwork/framesync"
	"github.com/boom/boomnetwork/session"
	"github.com/boom/boomnetwork/transport"
)

var (
	addr           = flag.String("addr", ":9000", "listen address")
	proto          = flag.String("proto", "tcp", "protocol: tcp or kcp")
	playersPerRoom = flag.Int("ppr", 2, "players per room to auto-start")
)

var roomMgr = framesync.NewRoomManager()

// 映射关系
var connPlayerMap sync.Map  // connID → playerId
var playerRoomMap sync.Map  // playerId → *Room

var playerCounter int32
var playerMu sync.Mutex

func main() {
	flag.Parse()

	router := session.NewRouter()
	router.On(framesync.CmdSessionBind, handleSessionBind)
	router.On(framesync.CmdFrameInput, handleFrameInput)
	router.On(framesync.CmdHeartbeat, handleHeartbeat)
	router.On(framesync.CmdReconnect, handleReconnect)

	server := transport.NewServer(*proto, router.AsTransportHandler())
	if err := server.Listen(*addr); err != nil {
		fmt.Fprintf(os.Stderr, "Failed: %v\n", err)
		os.Exit(1)
	}
	fmt.Printf("[FrameSync Server] Running on %s (proto=%s, ppr=%d)\n", *addr, *proto, *playersPerRoom)

	sig := make(chan os.Signal, 1)
	signal.Notify(sig, syscall.SIGINT, syscall.SIGTERM)
	<-sig

	fmt.Println("\n[FrameSync Server] Shutting down...")
	roomMgr.StopAll()
	server.Close()
}

func handleSessionBind(conn *transport.Conn, msg *codec.Message) *codec.Message {
	playerMu.Lock()
	playerCounter++
	playerId := playerCounter
	playerMu.Unlock()

	// 自动分配房间
	room := roomMgr.AutoAssignRoom(*playersPerRoom)

	connPlayerMap.Store(conn.ID, playerId)
	playerRoomMap.Store(playerId, room)
	room.AddPlayer(playerId, conn)

	rsp := make([]byte, 4)
	binary.LittleEndian.PutUint32(rsp, uint32(playerId))

	pc := room.PlayerCount()
	tc := room.TotalPlayerCount()
	fmt.Printf("[Server] Player %d bound (conn %d, room %d, online=%d, total=%d, ppr=%d)\n",
		playerId, conn.ID, room.ID, pc, tc, *playersPerRoom)

	// 人满自动开始（延迟到 SessionBindRsp 发送后，确保客户端先收到 bind 再收到 start）
	shouldStart := pc >= *playersPerRoom
	if shouldStart {
		fmt.Printf("[Server] Room %d will start after bind rsp (%d/%d)\n", room.ID, pc, *playersPerRoom)
	}

	// 先发 SessionBindRsp 再 Start（通过 goroutine 延迟极短时间）
	if shouldStart {
		go func() {
			// 等一个极短的时间让 SessionBindRsp 先发出
			time.Sleep(10 * time.Millisecond)
			room.Start()
		}()
	}

	return &codec.Message{Cmd: framesync.CmdSessionBindRsp, Data: rsp}
}

func handleFrameInput(conn *transport.Conn, msg *codec.Message) *codec.Message {
	val, ok := connPlayerMap.Load(conn.ID)
	if !ok {
		return nil
	}
	playerId := val.(int32)

	roomVal, ok := playerRoomMap.Load(playerId)
	if !ok {
		return nil
	}
	room := roomVal.(*framesync.Room)
	room.OnInput(playerId, msg.Data)
	return nil
}

func handleHeartbeat(conn *transport.Conn, msg *codec.Message) *codec.Message {
	return &codec.Message{Cmd: framesync.CmdHeartbeatRsp}
}

func handleReconnect(conn *transport.Conn, msg *codec.Message) *codec.Message {
	if len(msg.Data) < 4 {
		return &codec.Message{Cmd: framesync.CmdReconnectRsp, Data: []byte{0, 0, 0, 0}}
	}

	playerId := int32(binary.LittleEndian.Uint32(msg.Data[0:4]))
	var lastFrame uint32
	if len(msg.Data) >= 8 {
		lastFrame = binary.LittleEndian.Uint32(msg.Data[4:8])
	}

	// 找到玩家的房间
	roomVal, ok := playerRoomMap.Load(playerId)
	if !ok {
		fmt.Printf("[Server] Player %d reconnect failed: no room found\n", playerId)
		return &codec.Message{Cmd: framesync.CmdReconnectRsp, Data: []byte{0, 0, 0, 0}}
	}
	room := roomVal.(*framesync.Room)

	fmt.Printf("[Server] Player %d reconnecting (conn %d, room %d, lastFrame=%d)\n",
		playerId, conn.ID, room.ID, lastFrame)

	connPlayerMap.Store(conn.ID, playerId)
	room.AddPlayer(playerId, conn)

	currentFrame := room.CurrentFrameNumber()
	rsp := make([]byte, 4)
	binary.LittleEndian.PutUint32(rsp, currentFrame)

	// 异步重发缺失帧
	if lastFrame > 0 && lastFrame < currentFrame {
		go func() {
			frames := room.GetFramesSince(lastFrame)
			fmt.Printf("[Server] Resending %d frames to player %d\n", len(frames), playerId)
			for _, cf := range frames {
				conn.Send(&codec.Message{Cmd: framesync.CmdPushFrames, Data: cf.EncodedData})
			}
		}()
	}

	fmt.Printf("[Server] Player %d reconnected at frame %d\n", playerId, currentFrame)
	return &codec.Message{Cmd: framesync.CmdReconnectRsp, Data: rsp}
}
