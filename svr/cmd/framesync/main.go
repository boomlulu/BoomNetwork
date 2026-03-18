package main

import (
	"encoding/binary"
	"flag"
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

var (
	addr  = flag.String("addr", ":9000", "listen address")
	proto = flag.String("proto", "tcp", "protocol: tcp or kcp")
)

var connPlayerMap sync.Map
var playerConnMap sync.Map

var room = framesync.NewRoom(20)
var autoStartCount = 2

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
	fmt.Printf("[FrameSync Server] Running on %s (proto=%s, frameRate=20, autoStart=%d)\n", *addr, *proto, autoStartCount)

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
	playerConnMap.Store(playerId, conn)
	room.AddPlayer(playerId, conn)

	rsp := make([]byte, 4)
	binary.LittleEndian.PutUint32(rsp, uint32(playerId))

	fmt.Printf("[Server] Player %d bound (conn %d)\n", playerId, conn.ID)

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
	return nil
}

func handleHeartbeat(conn *transport.Conn, msg *codec.Message) *codec.Message {
	return &codec.Message{Cmd: framesync.CmdHeartbeatRsp}
}

func handleReconnect(conn *transport.Conn, msg *codec.Message) *codec.Message {
	if len(msg.Data) < 4 {
		return &codec.Message{Cmd: framesync.CmdReconnectRsp, Data: []byte{0, 0, 0, 0}}
	}

	playerId := int32(binary.LittleEndian.Uint32(msg.Data))
	fmt.Printf("[Server] Player %d reconnecting (conn %d)\n", playerId, conn.ID)

	connPlayerMap.Store(conn.ID, playerId)
	playerConnMap.Store(playerId, conn)
	room.AddPlayer(playerId, conn)

	frameNumber := room.CurrentFrameNumber()
	rsp := make([]byte, 4)
	binary.LittleEndian.PutUint32(rsp, uint32(frameNumber))

	fmt.Printf("[Server] Player %d reconnected at frame %d\n", playerId, frameNumber)

	return &codec.Message{Cmd: framesync.CmdReconnectRsp, Data: rsp}
}
