package main

import (
	"context"
	"encoding/binary"
	"flag"
	"log"
	"net/http"
	"os"
	"os/signal"
	"sync"
	"syscall"
	"time"

	"github.com/boom/boomnetwork/codec"
	"github.com/boom/boomnetwork/framesync"
	"github.com/boom/boomnetwork/session"
	"github.com/boom/boomnetwork/transport"
	"github.com/prometheus/client_golang/prometheus/promhttp"
)

// 构建版本信息（通过 -ldflags 注入）
var (
	BuildHash string
	BuildTime string
)

var cfg ServerConfig

var (
	addr        = flag.String("addr", ":9000", "listen address")
	proto       = flag.String("proto", "tcp", "protocol: tcp or kcp")
	ppr         = flag.Int("ppr", 4, "default players per room")
	authToken   = flag.String("token", "", "auth token (empty = no auth)")
	metricsAddr = flag.String("metrics", ":9090", "prometheus metrics address (empty = disabled)")
	adminAddr   = flag.String("admin", ":9091", "admin HTTP address (empty = disabled)")
	adminToken  = flag.String("admin-token", "", "admin API bearer token (empty = no auth)")
	configFile  = flag.String("config", "", "JSON config file path (overrides flags)")
	genConfig   = flag.Bool("gen-config", false, "generate default config.json and exit")
	autoRoom    = flag.Bool("autoroom", false, "auto-assign room on SessionBind (for legacy/stress tests)")
)

var roomMgr *framesync.RoomManager

// 映射关系
var connPlayerMap sync.Map // connID → int32(playerId)
var playerRoomMap sync.Map // int32(playerId) → *Room
var playerConnMap sync.Map // int32(playerId) → *transport.Conn

var playerCounter int32
var playerMu sync.Mutex

func main() {
	log.SetFlags(log.Ldate | log.Ltime | log.Lmicroseconds)
	flag.Parse()

	if BuildHash != "" {
		log.Printf("[Server] version hash=%s built=%s\n", BuildHash, BuildTime)
	}

	// 生成默认配置文件
	if *genConfig {
		SaveDefaultConfig("config.yaml")
		return
	}

	// 加载配置：有 -config 时配置文件覆盖 flags，否则 flags 覆盖 DefaultConfig
	cfg = DefaultConfig()
	if *configFile != "" {
		cfg = LoadConfig(*configFile)
		*addr        = cfg.Addr
		*proto       = cfg.Proto
		*ppr         = cfg.PlayersPerRoom
		*authToken   = cfg.AuthToken
		*metricsAddr = cfg.MetricsAddr
		*adminAddr   = cfg.AdminAddr
		*adminToken  = cfg.AdminToken
	} else {
		cfg.Addr           = *addr
		cfg.Proto          = *proto
		cfg.PlayersPerRoom = *ppr
		cfg.AuthToken      = *authToken
		cfg.MetricsAddr    = *metricsAddr
		cfg.AdminAddr      = *adminAddr
		cfg.AdminToken     = *adminToken
	}

	// 用配置初始化 RoomManager
	roomMgr = framesync.NewRoomManager(framesync.RoomConfig{
		FrameRate:              int32(cfg.FrameRate),
		MaxPlayers:             cfg.PlayersPerRoom,
		FrameBufferSize:        cfg.FrameBufferSize,
		DisconnectKeepAlive:    time.Duration(cfg.DisconnectKeepSec) * time.Second,
		SnapshotIntervalFrames: int32(cfg.SnapshotIntervalFrames),
		QuickReconnectMaxMs:    int32(cfg.QuickReconnectMaxMs),
	})

	router := session.NewRouter()
	// 帧同步
	router.On(framesync.CmdSessionBind, txStats(handleSessionBind))
	router.On(framesync.CmdRequestStart, txStats(handleRequestStart))
	router.On(framesync.CmdStopFrameSync, txStats(handleRequestStop))
	router.On(framesync.CmdFrameInput, txStats(handleFrameInput))
	router.On(framesync.CmdHeartbeat, txStats(handleHeartbeat))
	router.On(framesync.CmdReconnect, txStats(handleReconnect))
	// 房间管理
	router.On(framesync.CmdGetRooms, txStats(handleGetRooms))
	router.On(framesync.CmdCreateRoom, txStats(handleCreateRoom))
	router.On(framesync.CmdJoinRoom, txStats(handleJoinRoom))
	router.On(framesync.CmdLeaveRoom, txStats(handleLeaveRoom))
	router.On(framesync.CmdMatchRoom, txStats(handleMatchRoom))
	// 快照
	router.On(framesync.CmdUploadSnapshot, txStats(handleUploadSnapshot))
	// 实体权威同步
	router.On(framesync.CmdSendEntityState, txStats(handleSendEntityState))

	// 在 router 外层包一层 RX 计数 + 消息日志
	baseHandler := router.AsTransportHandler()
	rxHandler := func(conn *transport.Conn, msg *codec.Message) {
		GameStats.RecordRx(int64(len(msg.Data)))
		// 关键 RX 消息的日志在 txStats 中带 detail 记录，这里跳过避免双记
		switch msg.Cmd {
		case framesync.CmdReconnect, framesync.CmdJoinRoom:
			// txStats 已处理
		default:
			LogMsg("rx", msg.Cmd, connPid(conn), len(msg.Data))
		}
		baseHandler(conn, msg)
	}
	server := transport.NewServer(*proto, rxHandler)
	server.SetOnDisconnect(onClientDisconnect)

	// 安全配置
	secCfg := transport.DefaultSecurityConfig()
	if *authToken != "" {
		secCfg.RequireAuth = true
		secCfg.AuthToken = *authToken
	}
	server.SetSecurity(secCfg)

	if err := server.Listen(*addr); err != nil {
		log.Fatalf("Failed: %v", err)
		os.Exit(1)
	}
	authInfo := ""
	if secCfg.RequireAuth {
		authInfo = ", auth=required"
	}
	log.Printf("[FrameSync Server] Running on %s (proto=%s, ppr=%d%s)\n", *addr, *proto, *ppr, authInfo)

	// Prometheus metrics endpoint
	if *metricsAddr != "" {
		go func() {
			http.Handle("/metrics", promhttp.Handler())
			log.Printf("[Metrics] Listening on %s/metrics\n", *metricsAddr)
			if err := http.ListenAndServe(*metricsAddr, nil); err != nil {
				log.Printf("[Metrics] Failed: %v\n", err)
			}
		}()
	}

	// 全局 context 用于优雅关闭
	ctx, cancel := context.WithCancel(context.Background())

	// Admin HTTP + WebSocket server
	if *adminAddr != "" {
		go startAdminServer(ctx, *adminAddr, *adminToken)
	}

	sig := make(chan os.Signal, 1)
	signal.Notify(sig, syscall.SIGINT, syscall.SIGTERM)
	<-sig

	log.Println("[FrameSync Server] Shutting down...")
	cancel() // 通知 admin server + WS hub 优雅关闭
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
	framesync.Metrics.ConnectionsCurrent.Dec()

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
	log.Printf("[Server] Player %d disconnected from room %d (kept for reconnect)\n", playerId, room.ID)

	// 广播 PlayerOffline：通知其他客户端该玩家临时掉线（非永久离开）
	broadcastToRoom(room, playerId, framesync.CmdPlayerOffline, framesync.EncodePlayerId(playerId))

	// 如果房间没有在线玩家了，延迟清理
	if room.PlayerCount() == 0 {
		roomID := room.ID
		go func() {
			time.Sleep(30 * time.Second) // 30 秒等待重连
			if room.PlayerCount() == 0 {
				room.Stop()
				roomMgr.RemoveRoom(roomID)
				// 清理 playerRoomMap 中指向该房间的映射
				playerRoomMap.Range(func(key, val any) bool {
					if r, ok := val.(*framesync.Room); ok && r.ID == roomID {
						playerRoomMap.Delete(key)
					}
					return true
				})
				log.Printf("[Server] Room %d cleaned up (empty after 30s)\n", roomID)
			}
		}()
	}
}

// ===================== 帧同步 Handler =====================

func handleSessionBind(conn *transport.Conn, msg *codec.Message) *codec.Message {
	// Token 鉴权（如果启用）
	if *authToken != "" {
		clientToken := ""
		if msg.Data != nil && len(msg.Data) > 0 {
			clientToken = string(msg.Data)
		}
		if clientToken != *authToken {
			log.Printf("[Server] Auth failed for conn %d (bad token)\n", conn.ID)
			framesync.Metrics.AuthFailures.Inc()
			return &codec.Message{Cmd: framesync.CmdSessionBindRsp, Data: []byte{0, 0, 0, 0}}
		}
	}
	framesync.Metrics.ConnectionsTotal.Inc()
	framesync.Metrics.ConnectionsCurrent.Inc()

	playerId := nextPlayerId()

	// 记录映射
	connPlayerMap.Store(conn.ID, playerId)
	playerConnMap.Store(playerId, conn)

	// autoroom 模式：SessionBind 时自动分房（兼容压测和旧版 FrameSyncExample）
	if *autoRoom {
		room := roomMgr.AutoAssignRoom(*ppr)
		bindPlayerToRoom(playerId, conn, room)
		log.Printf("[Server] Player %d bound (conn %d, auto room %d, online=%d)\n",
			playerId, conn.ID, room.ID, room.PlayerCount())
	} else {
		log.Printf("[Server] Player %d bound (conn %d)\n", playerId, conn.ID)
	}

	rsp := make([]byte, 4)
	binary.LittleEndian.PutUint32(rsp, uint32(playerId))

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
	framesync.Metrics.InputsReceived.Inc()
	return nil
}

func handleHeartbeat(conn *transport.Conn, msg *codec.Message) *codec.Message {
	return &codec.Message{Cmd: framesync.CmdHeartbeatRsp}
}

func handleReconnect(conn *transport.Conn, msg *codec.Message) *codec.Message {
	if len(msg.Data) < 4 {
		rsp := framesync.EncodeReconnectRsp(framesync.ReconnectFail, 0, 0, 0, nil)
		return &codec.Message{Cmd: framesync.CmdReconnectRsp, Data: rsp}
	}

	playerId := int32(binary.LittleEndian.Uint32(msg.Data[0:4]))
	var lastFrame uint32
	if len(msg.Data) >= 8 {
		lastFrame = binary.LittleEndian.Uint32(msg.Data[4:8])
	}

	roomVal, ok := playerRoomMap.Load(playerId)
	if !ok {
		log.Printf("[Server] Reconnect failed: player %d not found in any room\n", playerId)
		rsp := framesync.EncodeReconnectRsp(framesync.ReconnectFail, 0, 0, 0, nil)
		return &codec.Message{Cmd: framesync.CmdReconnectRsp, Data: rsp}
	}
	room := roomVal.(*framesync.Room)

	// 检查房间是否还在 RoomManager 中（可能已被清理）
	if roomMgr.GetRoom(room.ID) == nil {
		log.Printf("[Server] Reconnect failed: player %d room %d already cleaned up\n", playerId, room.ID)
		playerRoomMap.Delete(playerId)
		rsp := framesync.EncodeReconnectRsp(framesync.ReconnectFail, 0, 0, 0, nil)
		return &codec.Message{Cmd: framesync.CmdReconnectRsp, Data: rsp}
	}

	currentFrame := room.CurrentFrameNumber()
	snapshotFrame, snapshotData := room.GetSnapshot()

	// 快速重连路径: lastFrame > 0，检查是否还在环形缓冲区内
	if lastFrame > 0 {
		oldestFrame := room.OldestBufferedFrame()
		if oldestFrame > 0 && lastFrame < oldestFrame {
			// lastFrame 已超出缓冲区 → 返回 BufferStale，客户端降级到快照重连
			log.Printf("[Server] Reconnect buffer stale: player %d lastFrame=%d < oldest=%d\n",
				playerId, lastFrame, oldestFrame)
			rsp := framesync.EncodeReconnectRsp(framesync.ReconnectFailBufferStale, room.ID, currentFrame, 0, nil)
			return &codec.Message{Cmd: framesync.CmdReconnectRsp, Data: rsp}
		}
	}

	// 更新连接映射
	connPlayerMap.Store(conn.ID, playerId)
	playerConnMap.Store(playerId, conn)
	room.AddPlayer(playerId, conn)

	// 决定从哪帧开始补帧
	var replayFrom uint32
	if lastFrame > 0 {
		// 快速重连: 从客户端最后帧补
		replayFrom = lastFrame
		snapshotFrame = 0
		snapshotData = nil
	} else if snapshotFrame > 0 {
		// 快照重连: 用快照 + 从快照帧之后补帧
		replayFrom = snapshotFrame
	} else {
		// 都没有，不补帧
		snapshotFrame = 0
		snapshotData = nil
	}

	rsp := framesync.EncodeReconnectRsp(framesync.ReconnectSuccess, room.ID, currentFrame, snapshotFrame, snapshotData)

	// 通知同房其他玩家：恢复在线（非新加入）
	broadcastToRoom(room, playerId, framesync.CmdPlayerOnline, framesync.EncodePlayerId(playerId))

	// 异步补帧
	if replayFrom > 0 && replayFrom < currentFrame {
		go func() {
			frames := room.GetFramesSince(replayFrom)
			for _, cf := range frames {
				sendMsg(conn, &codec.Message{Cmd: framesync.CmdPushFrames, Data: cf.EncodedData})
			}
			log.Printf("[Server] Player %d replayed %d frames (%d→%d)\n", playerId, len(frames), replayFrom+1, currentFrame)
		}()
	}

	log.Printf("[Server] Player %d reconnected (room %d, serverFrame=%d, snapshot=%d)\n",
		playerId, room.ID, currentFrame, snapshotFrame)
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

	log.Printf("[Server] Room %d created (max=%d)\n", room.ID, maxPlayers)
	return &codec.Message{Cmd: framesync.CmdCreateRoomRsp, Data: rsp}
}

func handleJoinRoom(conn *transport.Conn, msg *codec.Message) *codec.Message {
	if len(msg.Data) < 4 {
		return &codec.Message{Cmd: framesync.CmdJoinRoomRsp, Data: make([]byte, 8)}
	}

	roomId := int32(binary.LittleEndian.Uint32(msg.Data[0:4]))
	room := roomMgr.GetRoom(roomId)
	if room == nil {
		log.Printf("[Server] JoinRoom failed: room %d not found\n", roomId)
		return &codec.Message{Cmd: framesync.CmdJoinRoomRsp, Data: make([]byte, 8)}
	}

	if room.PlayerCount() >= room.MaxPlayers() {
		log.Printf("[Server] JoinRoom failed: room %d full\n", roomId)
		return &codec.Message{Cmd: framesync.CmdJoinRoomRsp, Data: make([]byte, 8)}
	}

	// 先取已有玩家列表（新人加入前）
	existingPlayers := room.GetPlayerIds()

	// 复用 SessionBind 时分配的 playerId，不重新分配
	val, ok := connPlayerMap.Load(conn.ID)
	if !ok {
		log.Printf("[Server] JoinRoom failed: conn %d not bound (SessionBind missing)\n", conn.ID)
		return &codec.Message{Cmd: framesync.CmdJoinRoomRsp, Data: make([]byte, 8)}
	}
	playerId := val.(int32)
	bindPlayerToRoom(playerId, conn, room)

	log.Printf("[Server] Player %d joined room %d (online=%d/%d, existing=%v)\n",
		playerId, room.ID, room.PlayerCount(), room.MaxPlayers(), existingPlayers)

	// 通知同房其他玩家
	broadcastToRoom(room, playerId, framesync.CmdPlayerJoined, framesync.EncodePlayerId(playerId))

	// 如果房间已在运行，给迟到者：快照 → StartFrameSync → 补帧
	if room.IsRunning() {
		snapshotFrame, snapshotData := room.GetSnapshot()
		currentFrame := room.CurrentFrameNumber()

		go func() {
			time.Sleep(10 * time.Millisecond) // 确保 JoinRoomRsp 先到达

			// 1. 下发快照（迟到者用来初始化世界状态）
			var replayFrom uint32
			if snapshotData != nil && len(snapshotData) > 0 {
				snapshotMsg := framesync.EncodeSnapshot(snapshotFrame, snapshotData)
				sendMsg(conn, &codec.Message{Cmd: framesync.CmdRoomSnapshot, Data: snapshotMsg})
				replayFrom = snapshotFrame
				log.Printf("[Server] Sent room snapshot to late-join player %d (frame %d, %d bytes)\n",
					playerId, snapshotFrame, len(snapshotData))
			} else {
				// 无快照兜底：从缓冲区最旧帧开始补帧（最佳努力）
				oldestFrame := room.OldestBufferedFrame()
				if oldestFrame > 0 {
					replayFrom = oldestFrame - 1 // GetFramesSince 是 afterFrame，所以 -1
				}
				log.Printf("[Server] WARNING: No snapshot for late-join player %d, replaying from oldest buffered frame %d\n",
					playerId, oldestFrame)
			}

			// 2. StartFrameSync
			initData := framesync.InitData{
				FrameRate:           room.FrameRate(),
				FrameInterval:       1000 / room.FrameRate(),
				StartTime:           room.StartTime(),
				SnapshotInterval:    int32(cfg.SnapshotIntervalFrames),
				QuickReconnectMaxMs: int32(cfg.QuickReconnectMaxMs),
			}
			sendMsg(conn, &codec.Message{
				Cmd:  framesync.CmdStartFrameSync,
				Data: framesync.EncodeInitData(&initData),
			})

			// 3. 补帧
			if replayFrom > 0 && replayFrom < currentFrame {
				frames := room.GetFramesSince(replayFrom)
				for _, cf := range frames {
					sendMsg(conn, &codec.Message{Cmd: framesync.CmdPushFrames, Data: cf.EncodedData})
				}
				log.Printf("[Server] Late-join player %d: replayed %d frames (%d→%d)\n",
					playerId, len(frames), replayFrom+1, currentFrame)
			}

			log.Printf("[Server] Late-join player %d ready (room %d, frame %d)\n",
				playerId, room.ID, currentFrame)
		}()
	}

	return &codec.Message{Cmd: framesync.CmdJoinRoomRsp, Data: framesync.EncodeJoinRoomRsp(playerId, room.ID, existingPlayers)}
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
	// 注意：不删除 connPlayerMap / playerConnMap
	// 这两个映射是 SessionBind 建立的，LeaveRoom 只清理房间关系
	// 删了会导致 re-JoinRoom 失败（"SessionBind missing"）

	log.Printf("[Server] Player %d left room %d\n", playerId, room.ID)

	broadcastToRoom(room, playerId, framesync.CmdPlayerLeft, framesync.EncodePlayerId(playerId))
	return &codec.Message{Cmd: framesync.CmdLeaveRoomRsp}
}

func handleMatchRoom(conn *transport.Conn, msg *codec.Message) *codec.Message {
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

	val, ok := connPlayerMap.Load(conn.ID)
	if !ok {
		log.Printf("[Server] MatchRoom failed: conn %d not bound\n", conn.ID)
		return &codec.Message{Cmd: framesync.CmdMatchRoomRsp, Data: make([]byte, 8)}
	}
	playerId := val.(int32)

	room := roomMgr.MatchRoom(maxPlayers)
	existingPlayers := room.GetPlayerIds()
	bindPlayerToRoom(playerId, conn, room)

	log.Printf("[Server] Player %d matched to room %d (online=%d/%d)\n",
		playerId, room.ID, room.PlayerCount(), room.MaxPlayers())

	broadcastToRoom(room, playerId, framesync.CmdPlayerJoined, framesync.EncodePlayerId(playerId))

	// 迟到加入（房间已在运行）
	if room.IsRunning() {
		snapshotFrame, snapshotData := room.GetSnapshot()
		currentFrame := room.CurrentFrameNumber()

		go func() {
			time.Sleep(10 * time.Millisecond)

			var replayFrom uint32
			if snapshotData != nil && len(snapshotData) > 0 {
				snapshotMsg := framesync.EncodeSnapshot(snapshotFrame, snapshotData)
				sendMsg(conn, &codec.Message{Cmd: framesync.CmdRoomSnapshot, Data: snapshotMsg})
				replayFrom = snapshotFrame
			} else {
				oldestFrame := room.OldestBufferedFrame()
				if oldestFrame > 0 {
					replayFrom = oldestFrame - 1
				}
			}

			initData := framesync.InitData{
				FrameRate:           room.FrameRate(),
				FrameInterval:       1000 / room.FrameRate(),
				StartTime:           room.StartTime(),
				SnapshotInterval:    int32(cfg.SnapshotIntervalFrames),
				QuickReconnectMaxMs: int32(cfg.QuickReconnectMaxMs),
			}
			sendMsg(conn, &codec.Message{
				Cmd:  framesync.CmdStartFrameSync,
				Data: framesync.EncodeInitData(&initData),
			})

			if replayFrom > 0 && replayFrom < currentFrame {
				frames := room.GetFramesSince(replayFrom)
				for _, cf := range frames {
					sendMsg(conn, &codec.Message{Cmd: framesync.CmdPushFrames, Data: cf.EncodedData})
				}
			}
		}()
	}

	return &codec.Message{Cmd: framesync.CmdMatchRoomRsp, Data: framesync.EncodeJoinRoomRsp(playerId, room.ID, existingPlayers)}
}

// ===================== 工具函数 =====================

func bindPlayerToRoom(playerId int32, conn *transport.Conn, room *framesync.Room) {
	connPlayerMap.Store(conn.ID, playerId)
	playerRoomMap.Store(playerId, room)
	playerConnMap.Store(playerId, conn)
	room.AddPlayer(playerId, &statsConn{inner: &simConn{inner: conn, cfg: GlobalNetSim}, pid: playerId})
}

func handleRequestStart(conn *transport.Conn, msg *codec.Message) *codec.Message {
	val, ok := connPlayerMap.Load(conn.ID)
	if !ok {
		return nil
	}
	playerId := val.(int32)

	roomVal, ok := playerRoomMap.Load(playerId)
	if !ok {
		log.Printf("[Server] RequestStart failed: player %d not in room\n", playerId)
		return nil
	}
	room := roomVal.(*framesync.Room)

	if room.IsRunning() {
		log.Printf("[Server] RequestStart: room %d already running\n", room.ID)
		return nil
	}

	// RequestStart 携带初始快照：在第一帧推送之前存好，避免早期断线无快照
	if len(msg.Data) > 0 {
		room.SetInitialSnapshot(msg.Data)
		log.Printf("[Server] Initial snapshot stored for room %d (%d bytes)\n", room.ID, len(msg.Data))
	}

	log.Printf("[Server] Player %d requested start room %d (online=%d)\n",
		playerId, room.ID, room.PlayerCount())

	go func() {
		time.Sleep(10 * time.Millisecond) // 确保本消息处理完
		room.Start()
	}()
	return nil
}

func handleRequestStop(conn *transport.Conn, msg *codec.Message) *codec.Message {
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

	if !room.IsRunning() {
		return nil
	}

	log.Printf("[Server] Player %d requested stop room %d\n", playerId, room.ID)
	room.Stop()
	return nil
}

// sendMsg 发送消息并记录游戏 TX 流量 + 消息日志
func sendMsg(conn *transport.Conn, msg *codec.Message) {
	GameStats.RecordTx(int64(len(msg.Data)))
	LogMsg("tx", msg.Cmd, connPid(conn), len(msg.Data))
	conn.Send(msg)
}

func broadcastToRoom(room *framesync.Room, excludePlayerId int32, cmd byte, data []byte) {
	msg := &codec.Message{Cmd: cmd, Data: data}
	room.ForEachOnlinePlayer(func(id int32, conn framesync.PlayerConn) {
		if id != excludePlayerId {
			conn.Send(msg) // TX 由 statsConn 自动计入
		}
	})
}

// ===================== 快照 Handler =====================

func handleUploadSnapshot(conn *transport.Conn, msg *codec.Message) *codec.Message {
	val, ok := connPlayerMap.Load(conn.ID)
	if !ok {
		return &codec.Message{Cmd: framesync.CmdUploadSnapshotRsp, Data: []byte{0}}
	}
	playerId := val.(int32)

	roomVal, ok := playerRoomMap.Load(playerId)
	if !ok {
		return &codec.Message{Cmd: framesync.CmdUploadSnapshotRsp, Data: []byte{0}}
	}
	room := roomVal.(*framesync.Room)

	frameNumber, snapshotData := framesync.DecodeUploadSnapshot(msg.Data)
	if snapshotData == nil {
		return &codec.Message{Cmd: framesync.CmdUploadSnapshotRsp, Data: []byte{0}}
	}

	accepted := room.UpdateSnapshot(frameNumber, snapshotData)
	if accepted {
		return &codec.Message{Cmd: framesync.CmdUploadSnapshotRsp, Data: []byte{1}}
	}
	return &codec.Message{Cmd: framesync.CmdUploadSnapshotRsp, Data: []byte{0}}
}

// handleSendEntityState 实体权威同步：透传给同房其他玩家（prepend senderPid）
func handleSendEntityState(conn *transport.Conn, msg *codec.Message) *codec.Message {
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

	// 构造 PushEntityState: [senderPid:4B] + 原始数据
	push := make([]byte, 4+len(msg.Data))
	binary.LittleEndian.PutUint32(push[0:4], uint32(playerId))
	copy(push[4:], msg.Data)

	pushMsg := &codec.Message{Cmd: framesync.CmdPushEntityState, Data: push}
	room.ForEachOnlinePlayer(func(id int32, c framesync.PlayerConn) {
		if id != playerId {
			c.Send(pushMsg)
		}
	})
	return nil
}
