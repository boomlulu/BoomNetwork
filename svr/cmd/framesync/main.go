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
	addr  = flag.String("addr", ":9000", "listen address")
	proto = flag.String("proto", "tcp", "protocol: tcp or kcp")
	ppr   = flag.Int("ppr", 4, "default players per room")
)

var roomMgr = framesync.NewRoomManager()

// 映射关系
var connPlayerMap sync.Map // connID → int32(playerId)
var playerRoomMap sync.Map // int32(playerId) → *Room
var playerConnMap sync.Map // int32(playerId) → *transport.Conn

var playerCounter int32
var playerMu sync.Mutex

func main() {
	flag.Parse()

	router := session.NewRouter()
	// 帧同步
	router.On(framesync.CmdSessionBind, handleSessionBind)
	router.On(framesync.CmdRequestStart, handleRequestStart)
	router.On(framesync.CmdFrameInput, handleFrameInput)
	router.On(framesync.CmdHeartbeat, handleHeartbeat)
	router.On(framesync.CmdReconnect, handleReconnect)
	// 房间管理
	router.On(framesync.CmdGetRooms, handleGetRooms)
	router.On(framesync.CmdCreateRoom, handleCreateRoom)
	router.On(framesync.CmdJoinRoom, handleJoinRoom)
	router.On(framesync.CmdLeaveRoom, handleLeaveRoom)

	server := transport.NewServer(*proto, router.AsTransportHandler())
	server.SetOnDisconnect(onClientDisconnect)
	if err := server.Listen(*addr); err != nil {
		fmt.Fprintf(os.Stderr, "Failed: %v\n", err)
		os.Exit(1)
	}
	fmt.Printf("[FrameSync Server] Running on %s (proto=%s, ppr=%d)\n", *addr, *proto, *ppr)

	sig := make(chan os.Signal, 1)
	signal.Notify(sig, syscall.SIGINT, syscall.SIGTERM)
	<-sig

	fmt.Println("\n[FrameSync Server] Shutting down...")
	roomMgr.StopAll()
	server.Close()
}

func nextPlayerId() int32 {
	playerMu.Lock()
	playerCounter++
	id := playerCounter
	playerMu.Unlock()
	return id
}

func onClientDisconnect(conn *transport.Conn) {
	val, ok := connPlayerMap.LoadAndDelete(conn.ID)
	if !ok {
		return
	}
	playerId := val.(int32)
	playerConnMap.Delete(playerId)

	roomVal, ok := playerRoomMap.Load(playerId)
	if !ok {
		return
	}
	room := roomVal.(*framesync.Room)
	room.DisconnectPlayer(playerId)
	fmt.Printf("[Server] Player %d disconnected from room %d\n", playerId, room.ID)

	// 通知同房其他玩家
	broadcastToRoom(room, playerId, framesync.CmdPlayerLeft, framesync.EncodePlayerId(playerId))

	// 如果房间没有在线玩家了，延迟清理
	if room.PlayerCount() == 0 {
		roomID := room.ID
		go func() {
			time.Sleep(5 * time.Second)
			if room.PlayerCount() == 0 {
				room.Stop()
				roomMgr.RemoveRoom(roomID)
				fmt.Printf("[Server] Room %d cleaned up (empty)\n", roomID)
			}
		}()
	}
}

// ===================== 帧同步 Handler =====================

func handleSessionBind(conn *transport.Conn, msg *codec.Message) *codec.Message {
	playerId := nextPlayerId()

	// 只记录 conn → playerId 映射，不分配房间
	connPlayerMap.Store(conn.ID, playerId)
	playerConnMap.Store(playerId, conn)

	rsp := make([]byte, 4)
	binary.LittleEndian.PutUint32(rsp, uint32(playerId))

	fmt.Printf("[Server] Player %d bound (conn %d)\n", playerId, conn.ID)

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

	roomVal, ok := playerRoomMap.Load(playerId)
	if !ok {
		return &codec.Message{Cmd: framesync.CmdReconnectRsp, Data: []byte{0, 0, 0, 0}}
	}
	room := roomVal.(*framesync.Room)

	// 更新连接映射
	connPlayerMap.Store(conn.ID, playerId)
	playerConnMap.Store(playerId, conn)
	room.AddPlayer(playerId, conn)

	currentFrame := room.CurrentFrameNumber()
	rsp := make([]byte, 4)
	binary.LittleEndian.PutUint32(rsp, currentFrame)

	if lastFrame > 0 && lastFrame < currentFrame {
		go func() {
			frames := room.GetFramesSince(lastFrame)
			for _, cf := range frames {
				conn.Send(&codec.Message{Cmd: framesync.CmdPushFrames, Data: cf.EncodedData})
			}
		}()
	}

	fmt.Printf("[Server] Player %d reconnected (room %d, frame %d)\n", playerId, room.ID, currentFrame)
	return &codec.Message{Cmd: framesync.CmdReconnectRsp, Data: rsp}
}

// ===================== 房间管理 Handler =====================

func handleGetRooms(conn *transport.Conn, msg *codec.Message) *codec.Message {
	infos := roomMgr.GetAllRoomInfos()
	return &codec.Message{Cmd: framesync.CmdGetRoomsRsp, Data: framesync.EncodeRoomList(infos)}
}

func handleCreateRoom(conn *transport.Conn, msg *codec.Message) *codec.Message {
	maxPlayers := *ppr
	if len(msg.Data) >= 2 {
		maxPlayers = int(binary.LittleEndian.Uint16(msg.Data[0:2]))
	}
	if maxPlayers < 1 {
		maxPlayers = 1
	}
	if maxPlayers > 100 {
		maxPlayers = 100
	}

	room := roomMgr.CreateRoomWithMaxPlayers(maxPlayers)
	rsp := make([]byte, 4)
	binary.LittleEndian.PutUint32(rsp, uint32(room.ID))

	fmt.Printf("[Server] Room %d created (max=%d)\n", room.ID, maxPlayers)
	return &codec.Message{Cmd: framesync.CmdCreateRoomRsp, Data: rsp}
}

func handleJoinRoom(conn *transport.Conn, msg *codec.Message) *codec.Message {
	if len(msg.Data) < 4 {
		return &codec.Message{Cmd: framesync.CmdJoinRoomRsp, Data: make([]byte, 8)}
	}

	roomId := int32(binary.LittleEndian.Uint32(msg.Data[0:4]))
	room := roomMgr.GetRoom(roomId)
	if room == nil {
		fmt.Printf("[Server] JoinRoom failed: room %d not found\n", roomId)
		return &codec.Message{Cmd: framesync.CmdJoinRoomRsp, Data: make([]byte, 8)}
	}

	if room.PlayerCount() >= room.MaxPlayers() {
		fmt.Printf("[Server] JoinRoom failed: room %d full\n", roomId)
		return &codec.Message{Cmd: framesync.CmdJoinRoomRsp, Data: make([]byte, 8)}
	}

	playerId := nextPlayerId()
	bindPlayerToRoom(playerId, conn, room)

	fmt.Printf("[Server] Player %d joined room %d (online=%d/%d)\n",
		playerId, room.ID, room.PlayerCount(), room.MaxPlayers())

	// 通知同房其他玩家
	broadcastToRoom(room, playerId, framesync.CmdPlayerJoined, framesync.EncodePlayerId(playerId))

	return &codec.Message{Cmd: framesync.CmdJoinRoomRsp, Data: framesync.EncodeJoinRoomRsp(playerId, room.ID)}
}

func handleLeaveRoom(conn *transport.Conn, msg *codec.Message) *codec.Message {
	val, ok := connPlayerMap.Load(conn.ID)
	if !ok {
		return &codec.Message{Cmd: framesync.CmdLeaveRoomRsp}
	}
	playerId := val.(int32)

	roomVal, ok := playerRoomMap.LoadAndDelete(playerId)
	if !ok {
		return &codec.Message{Cmd: framesync.CmdLeaveRoomRsp}
	}
	room := roomVal.(*framesync.Room)
	room.RemovePlayer(playerId)
	connPlayerMap.Delete(conn.ID)
	playerConnMap.Delete(playerId)

	fmt.Printf("[Server] Player %d left room %d\n", playerId, room.ID)

	broadcastToRoom(room, playerId, framesync.CmdPlayerLeft, framesync.EncodePlayerId(playerId))
	return &codec.Message{Cmd: framesync.CmdLeaveRoomRsp}
}

// ===================== 工具函数 =====================

func bindPlayerToRoom(playerId int32, conn *transport.Conn, room *framesync.Room) {
	connPlayerMap.Store(conn.ID, playerId)
	playerRoomMap.Store(playerId, room)
	playerConnMap.Store(playerId, conn)
	room.AddPlayer(playerId, conn)
}

func handleRequestStart(conn *transport.Conn, msg *codec.Message) *codec.Message {
	val, ok := connPlayerMap.Load(conn.ID)
	if !ok {
		return nil
	}
	playerId := val.(int32)

	roomVal, ok := playerRoomMap.Load(playerId)
	if !ok {
		fmt.Printf("[Server] RequestStart failed: player %d not in room\n", playerId)
		return nil
	}
	room := roomVal.(*framesync.Room)

	if room.IsRunning() {
		fmt.Printf("[Server] RequestStart: room %d already running\n", room.ID)
		return nil
	}

	fmt.Printf("[Server] Player %d requested start room %d (online=%d)\n",
		playerId, room.ID, room.PlayerCount())

	go func() {
		time.Sleep(10 * time.Millisecond) // 确保本消息处理完
		room.Start()
	}()
	return nil
}

func broadcastToRoom(room *framesync.Room, excludePlayerId int32, cmd byte, data []byte) {
	msg := &codec.Message{Cmd: cmd, Data: data}
	room.ForEachOnlinePlayer(func(id int32, conn framesync.PlayerConn) {
		if id != excludePlayerId {
			conn.Send(msg)
		}
	})
}
