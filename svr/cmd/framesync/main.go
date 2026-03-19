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

// handleReconnect 处理重连请求
// 客户端发送: [playerId:4][lastFrame:4]
// 服务端返回: [currentFrame:4] 并重发缺失的帧
func handleReconnect(conn *transport.Conn, msg *codec.Message) *codec.Message {
	if len(msg.Data) < 4 {
		return &codec.Message{Cmd: framesync.CmdReconnectRsp, Data: []byte{0, 0, 0, 0}}
	}

	playerId := int32(binary.LittleEndian.Uint32(msg.Data[0:4]))

	// 客户端最后确认的帧号（可选，老客户端可能不发）
	var lastFrame uint32
	if len(msg.Data) >= 8 {
		lastFrame = binary.LittleEndian.Uint32(msg.Data[4:8])
	}

	fmt.Printf("[Server] Player %d reconnecting (conn %d, lastFrame=%d)\n", playerId, conn.ID, lastFrame)

	// 更新连接映射
	connPlayerMap.Store(conn.ID, playerId)
	playerConnMap.Store(playerId, conn)

	// 重新加入房间（替换旧连接，恢复在线状态）
	room.AddPlayer(playerId, conn)

	// 返回当前帧号
	currentFrame := room.CurrentFrameNumber()
	rsp := make([]byte, 4)
	binary.LittleEndian.PutUint32(rsp, currentFrame)

	// 异步重发缺失的帧（在回复之后发，不阻塞响应）
	if lastFrame > 0 && lastFrame < currentFrame {
		go func() {
			frames := room.GetFramesSince(lastFrame)
			fmt.Printf("[Server] Resending %d frames to player %d (from %d to %d)\n",
				len(frames), playerId, lastFrame+1, currentFrame)
			for _, cf := range frames {
				conn.Send(&codec.Message{
					Cmd:  framesync.CmdPushFrames,
					Data: cf.EncodedData,
				})
			}
		}()
	}

	fmt.Printf("[Server] Player %d reconnected at frame %d\n", playerId, currentFrame)

	return &codec.Message{Cmd: framesync.CmdReconnectRsp, Data: rsp}
}
