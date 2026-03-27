package main

import (
	"context"
	"encoding/binary"
	"flag"
	"log/slog"
	"net"
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

var logLevel slog.LevelVar

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
	slog.SetDefault(slog.New(slog.NewJSONHandler(os.Stdout, &slog.HandlerOptions{Level: &logLevel})))
	flag.Parse()

	if BuildHash != "" {
		slog.Info("server version", "hash", BuildHash, "built", BuildTime)
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

	// S15: 环境变量覆盖 token（优先级最高，适合容器/systemd 生产部署）
	if v := os.Getenv("BOOM_ADMIN_TOKEN"); v != "" {
		cfg.AdminToken = v
		*adminToken = v
		slog.Info("admin token overridden by env var", "var", "BOOM_ADMIN_TOKEN")
	}
	if v := os.Getenv("BOOM_AUTH_TOKEN"); v != "" {
		cfg.AuthToken = v
		*authToken = v
		slog.Info("auth token overridden by env var", "var", "BOOM_AUTH_TOKEN")
	}

	// S17: 注入 WebSocket Origin 白名单
	wsAllowedOrigins = cfg.AllowedOrigins

	// 用配置初始化 RoomManager
	roomMgr = framesync.NewRoomManager(framesync.RoomConfig{
		FrameRate:              int32(cfg.FrameRate),
		MaxPlayers:             cfg.PlayersPerRoom,
		FrameBufferSize:        cfg.FrameBufferSize,
		DisconnectKeepAlive:    time.Duration(cfg.DisconnectKeepSec) * time.Second,
		SnapshotIntervalFrames: int32(cfg.SnapshotIntervalFrames),
		QuickReconnectMaxMs:    int32(cfg.QuickReconnectMaxMs),
	})
	roomMgr.SetMaxRooms(cfg.MaxRooms)

	router := session.NewRouter()
	// Core Cmd (0-15) — 高频
	router.OnCore(framesync.CmdSessionBind, txStats(handleSessionBind))
	router.OnCore(framesync.CmdRequestStart, txStats(handleRequestStart))
	router.OnCore(framesync.CmdStopFrameSync, txStats(handleRequestStop))
	router.OnCore(framesync.CmdFrameInput, txStats(handleFrameInput))
	router.OnCore(framesync.CmdHeartbeat, txStats(handleHeartbeat))
	router.OnCore(framesync.CmdReconnect, txStats(handleReconnect))
	// Extended Cmd (uint16) — 房间/快照/实体
	router.OnExt(framesync.ExtCmdGetRooms, txStats(handleGetRooms))
	router.OnExt(framesync.ExtCmdCreateRoom, txStats(handleCreateRoom))
	router.OnExt(framesync.ExtCmdJoinRoom, txStats(handleJoinRoom))
	router.OnExt(framesync.ExtCmdLeaveRoom, txStats(handleLeaveRoom))
	router.OnExt(framesync.ExtCmdMatchRoom, txStats(handleMatchRoom))
	router.OnExt(framesync.ExtCmdUploadSnapshot, txStats(handleUploadSnapshot))
	router.OnExt(framesync.ExtCmdSendEntityState, txStats(handleSendEntityState))
	router.OnExt(framesync.ExtCmdAuthorityTransfer, txStats(handleRequestAuthorityTransfer))
	// 轻量状态同步
	router.OnExt(framesync.ExtCmdSendStateMsg, txStats(handleSendStateMsg))
	router.OnExt(framesync.ExtCmdSetData, txStats(handleSetData))
	router.OnExt(framesync.ExtCmdRequestDataSync, txStats(handleRequestDataSync))
	// Game Cmd (uint32) — 服务器透传
	router.OnGame(txStats(handleGameRelay))

	// 路由注册完毕，冻结路由表 — Dispatch 不再加锁
	router.Freeze()

	// 在 router 外层包一层 RX 计数 + 消息日志 + netsim 响应延迟
	baseDispatch := router.Dispatch
	rxHandler := func(conn *transport.Conn, msg *codec.Message) {
		GameStats.RecordRx(int64(len(msg.Data)))
		framesync.Metrics.BytesReceived.Add(float64(len(msg.Data)))
		// 关键 RX 消息的日志在 txStats 中带 detail 记录，这里跳过避免双记
		if msg.CmdType == codec.CmdTypeCore && msg.Cmd == framesync.CmdReconnect {
			// txStats 已处理
		} else if msg.CmdType == codec.CmdTypeExtended && msg.ExtCmd == framesync.ExtCmdJoinRoom {
			// txStats 已处理
		} else {
			LogMsg("rx", msg.CmdType, msg.Cmd, msg.ExtCmd, msg.GameCmd, connPid(conn), len(msg.Data))
		}

		rsp := baseDispatch(conn, msg)
		if rsp == nil {
			return
		}
		rsp.HasSeq = msg.HasSeq
		rsp.Seq = msg.Seq

		// 通过全局 netsim 对响应也加延迟
		sc := &simConn{inner: conn, cfg: GlobalNetSim}
		sc.Send(rsp)
	}
	server := transport.NewServer(*proto, rxHandler)
	server.SetOnDisconnect(onClientDisconnect)
	server.SetOnRateLimited(func() { framesync.Metrics.RateLimited.Inc() })
	server.SetMaxConns(cfg.MaxConnections)

	// 安全配置
	secCfg := transport.DefaultSecurityConfig()
	if *authToken != "" {
		secCfg.RequireAuth = true
		secCfg.AuthToken = *authToken
	}
	server.SetSecurity(secCfg)

	if err := server.Listen(*addr); err != nil {
		slog.Error("server listen failed", "err", err)
		os.Exit(1)
	}
	authRequired := secCfg.RequireAuth
	slog.Info("framesync server running", "addr", *addr, "proto", *proto, "ppr", *ppr, "auth", authRequired)

	// Prometheus metrics endpoint
	if *metricsAddr != "" {
		go func() {
			http.Handle("/metrics", promhttp.Handler())
			slog.Info("metrics listening", "addr", *metricsAddr+"/metrics")
			if err := http.ListenAndServe(*metricsAddr, nil); err != nil {
				slog.Error("metrics server failed", "err", err)
			}
		}()
	}

	// 全局 context 用于优雅关闭
	ctx, cancel := context.WithCancel(context.Background())

	// Admin HTTP + WebSocket server
	if *adminAddr != "" {
		go startAdminServer(ctx, *adminAddr, *adminToken)
	}

	// 空房间定期清理
	cleanupSec := cfg.RoomCleanupSec
	if cleanupSec <= 0 {
		cleanupSec = 30
	}
	go func() {
		ticker := time.NewTicker(time.Duration(cleanupSec) * time.Second)
		defer ticker.Stop()
		idleTimeout := time.Duration(cleanupSec) * time.Second
		for {
			select {
			case <-ctx.Done():
				return
			case <-ticker.C:
				roomMgr.CleanupEmptyRooms(idleTimeout)
			}
		}
	}()

	// S22: notify systemd that the server is ready
	sdNotifyReady()

	sig := make(chan os.Signal, 1)
	signal.Notify(sig, syscall.SIGINT, syscall.SIGTERM, syscall.SIGHUP)
	for s := range sig {
		if s == syscall.SIGHUP {
			reloadConfig()
			continue
		}
		break // SIGINT or SIGTERM → shutdown
	}

	slog.Info("framesync server shutting down...")

	// 1. Broadcast ServerShutdown to all connected clients
	playerConnMap.Range(func(key, val any) bool {
		if conn, ok := val.(*transport.Conn); ok {
			conn.Send(codec.NewCoreMessage(framesync.CmdServerShutdown, nil))
		}
		return true
	})

	// 2. Cancel admin server + WS hub
	cancel()

	// 3. Stop all rooms (broadcasts StopFrameSync)
	roomMgr.StopAll()

	// 4. Close server (closes listener + all connections)
	server.Close()

	// 5. Wait for connection goroutines to drain (30s timeout)
	done := make(chan struct{})
	go func() { server.Wait(); close(done) }()
	select {
	case <-done:
		slog.Info("shutdown complete")
	case <-time.After(30 * time.Second):
		slog.Warn("shutdown timed out after 30s, forcing exit")
	}
}

func reloadConfig() {
	if *configFile == "" {
		slog.Warn("config reload skipped: no config file specified")
		return
	}
	newCfg := LoadConfig(*configFile)

	// Hot-reloadable fields only
	if newCfg.LogLevel != "" {
		var lvl slog.Level
		if err := lvl.UnmarshalText([]byte(newCfg.LogLevel)); err == nil {
			logLevel.Set(lvl)
		}
	}
	codec.MaxMessageSize = newCfg.MaxMessageSize

	slog.Info("config reloaded", "path", *configFile)
}

// sdNotifyReady sends READY=1 to systemd when running under Type=notify.
// It is a no-op when NOTIFY_SOCKET is not set (dev / Docker / bare process).
func sdNotifyReady() {
	addr := os.Getenv("NOTIFY_SOCKET")
	if addr == "" {
		return
	}
	conn, err := net.Dial("unixgram", addr)
	if err != nil {
		slog.Warn("sd_notify dial failed", "error", err)
		return
	}
	defer conn.Close()
	conn.Write([]byte("READY=1"))
	slog.Info("sd_notify: READY=1 sent")
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

	// CAS 检查：只有当 playerConnMap 中仍指向当前 conn 时才删除
	// 防止重连后旧连接断开覆盖新连接的映射
	currentConn, loaded := playerConnMap.Load(playerId)
	if loaded && currentConn.(*transport.Conn) == conn {
		playerConnMap.Delete(playerId)
	} else {
		// 已被新连接替换，跳过房间断线处理
		slog.Info("old conn disconnected, already reconnected — skip room cleanup", "playerId", playerId, "connId", conn.ID)
		return
	}

	// 清理 per-player 速率统计，防止 map 泄漏
	PlayerRates.Remove(playerId)

	roomVal, ok := playerRoomMap.Load(playerId)
	if !ok {
		return
	}
	room := roomVal.(*framesync.Room)
	room.DisconnectPlayer(playerId)
	slog.Info("player disconnected from room (kept for reconnect)", "playerId", playerId, "roomId", room.ID)

	// 广播 PlayerOffline：通知其他客户端该玩家临时掉线（非永久离开）
	broadcastToRoom(room, playerId, codec.NewExtMessage(framesync.ExtCmdPlayerOffline, framesync.EncodePlayerId(playerId)))

	// 释放断线玩家持有的所有实体权威
	releasedEntities := room.ReleaseAllAuthority(playerId)
	for _, eid := range releasedEntities {
		data := framesync.EncodeAuthorityTransferResult(eid, 0)
		broadcastToRoom(room, -1, codec.NewExtMessage(framesync.ExtCmdAuthorityTransfer, data))
		slog.Info("entity authority released on disconnect", "entityId", eid, "playerId", playerId)
	}

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
				slog.Info("room cleaned up (empty after 30s)", "roomId", roomID)
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
			slog.Warn("auth failed, disconnecting", "connId", conn.ID, "addr", conn.RemoteAddr())
			framesync.Metrics.AuthFailures.Inc()
			// S16: 发送错误响应后延迟关闭连接（100ms 让响应先 flush）
			go func() {
				time.Sleep(100 * time.Millisecond)
				conn.Close()
			}()
			return codec.NewCoreMessage(framesync.CmdSessionBindRsp, []byte{0, 0, 0, 0})
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
		slog.Info("player bound (auto room)", "playerId", playerId, "connId", conn.ID, "roomId", room.ID, "online", room.PlayerCount())
	} else {
		slog.Info("player bound", "playerId", playerId, "connId", conn.ID)
	}

	rsp := make([]byte, 4)
	binary.LittleEndian.PutUint32(rsp, uint32(playerId))

	return codec.NewCoreMessage(framesync.CmdSessionBindRsp, rsp)
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
	return codec.NewCoreMessage(framesync.CmdHeartbeatRsp, nil)
}

func handleReconnect(conn *transport.Conn, msg *codec.Message) *codec.Message {
	if len(msg.Data) < 4 {
		framesync.Metrics.MessageErrors.Inc()
		framesync.Metrics.ReconnectFail.Inc()
		rsp := framesync.EncodeReconnectRsp(framesync.ReconnectFail, 0, 0, 0, nil)
		return codec.NewCoreMessage(framesync.CmdReconnectRsp, rsp)
	}

	playerId := int32(binary.LittleEndian.Uint32(msg.Data[0:4]))
	var lastFrame uint32
	if len(msg.Data) >= 8 {
		lastFrame = binary.LittleEndian.Uint32(msg.Data[4:8])
	}

	roomVal, ok := playerRoomMap.Load(playerId)
	if !ok {
		slog.Warn("reconnect failed: player not found in any room", "playerId", playerId)
		framesync.Metrics.ReconnectFail.Inc()
		rsp := framesync.EncodeReconnectRsp(framesync.ReconnectFail, 0, 0, 0, nil)
		return codec.NewCoreMessage(framesync.CmdReconnectRsp, rsp)
	}
	room := roomVal.(*framesync.Room)

	// 检查房间是否还在 RoomManager 中（可能已被清理）
	if roomMgr.GetRoom(room.ID) == nil {
		slog.Warn("reconnect failed: room already cleaned up", "playerId", playerId, "roomId", room.ID)
		playerRoomMap.Delete(playerId)
		framesync.Metrics.ReconnectFail.Inc()
		rsp := framesync.EncodeReconnectRsp(framesync.ReconnectFail, 0, 0, 0, nil)
		return codec.NewCoreMessage(framesync.CmdReconnectRsp, rsp)
	}

	currentFrame := room.CurrentFrameNumber()
	snapshotFrame, snapshotData := room.GetSnapshot()

	// 快速重连路径: lastFrame > 0，检查是否还在环形缓冲区内
	if lastFrame > 0 {
		oldestFrame := room.OldestBufferedFrame()
		if oldestFrame > 0 && lastFrame < oldestFrame {
			// lastFrame 已超出缓冲区 → 返回 BufferStale，客户端降级到快照重连
			slog.Warn("reconnect buffer stale", "playerId", playerId, "lastFrame", lastFrame, "oldestFrame", oldestFrame)
			rsp := framesync.EncodeReconnectRsp(framesync.ReconnectFailBufferStale, room.ID, currentFrame, 0, nil)
			return codec.NewCoreMessage(framesync.CmdReconnectRsp, rsp)
		}
	}

	// 关闭旧连接（如果存在），防止旧连接的 disconnect 回调干扰新映射
	if oldConn, ok := playerConnMap.Load(playerId); ok {
		oldC := oldConn.(*transport.Conn)
		if oldC != conn {
			connPlayerMap.Delete(oldC.ID) // 先清理旧 connID 映射
			oldC.Close()
		}
	}

	// 更新连接映射
	connPlayerMap.Store(conn.ID, playerId)
	playerConnMap.Store(playerId, conn)
	room.AddPlayer(playerId, &statsConn{inner: &simConn{inner: conn, cfg: GlobalNetSim}, pid: playerId})

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

	framesync.Metrics.ReconnectSuccess.Inc()
	rsp := framesync.EncodeReconnectRsp(framesync.ReconnectSuccess, room.ID, currentFrame, snapshotFrame, snapshotData)

	// 通知同房其他玩家：恢复在线（非新加入）
	broadcastToRoom(room, playerId, codec.NewExtMessage(framesync.ExtCmdPlayerOnline, framesync.EncodePlayerId(playerId)))

	// 异步补帧
	if replayFrom > 0 && replayFrom < currentFrame {
		go func() {
			frames := room.GetFramesSince(replayFrom)
			for _, cf := range frames {
				sendMsg(conn, codec.NewCoreMessage(framesync.CmdPushFrames, cf.EncodedData))
			}
			slog.Info("player replayed frames on reconnect", "playerId", playerId, "count", len(frames), "fromFrame", replayFrom+1, "toFrame", currentFrame)
		}()
	}

	slog.Info("player reconnected", "playerId", playerId, "roomId", room.ID, "serverFrame", currentFrame, "snapshotFrame", snapshotFrame)
	return codec.NewCoreMessage(framesync.CmdReconnectRsp, rsp)
}

// ===================== 房间管理 Handler =====================

func handleGetRooms(conn *transport.Conn, msg *codec.Message) *codec.Message {
	infos := roomMgr.GetAllRoomInfos()
	return codec.NewExtMessage(framesync.ExtCmdGetRoomsRsp, framesync.EncodeRoomList(infos))
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
	if room == nil {
		slog.Warn("create room rejected: server at capacity", "maxPlayers", maxPlayers)
		return codec.NewExtMessage(framesync.ExtCmdCreateRoomRsp, make([]byte, 4)) // roomId=0 signals failure
	}
	rsp := make([]byte, 4)
	binary.LittleEndian.PutUint32(rsp, uint32(room.ID))

	slog.Info("room created", "roomId", room.ID, "maxPlayers", maxPlayers)
	return codec.NewExtMessage(framesync.ExtCmdCreateRoomRsp, rsp)
}

func handleJoinRoom(conn *transport.Conn, msg *codec.Message) *codec.Message {
	if len(msg.Data) < 4 {
		framesync.Metrics.MessageErrors.Inc()
		return codec.NewExtMessage(framesync.ExtCmdJoinRoomRsp, framesync.EncodeJoinRoomError(framesync.JoinRoomBadData))
	}

	roomId := int32(binary.LittleEndian.Uint32(msg.Data[0:4]))
	room := roomMgr.GetRoom(roomId)
	if room == nil {
		slog.Warn("join room failed: room not found", "roomId", roomId)
		return codec.NewExtMessage(framesync.ExtCmdJoinRoomRsp, framesync.EncodeJoinRoomError(framesync.JoinRoomNotFound))
	}

	if room.PlayerCount() >= room.MaxPlayers() {
		slog.Warn("join room failed: room full", "roomId", roomId)
		return codec.NewExtMessage(framesync.ExtCmdJoinRoomRsp, framesync.EncodeJoinRoomError(framesync.JoinRoomFull))
	}

	// 先取已有玩家列表（新人加入前）
	existingPlayers := room.GetPlayerIds()

	// 复用 SessionBind 时分配的 playerId，不重新分配
	val, ok := connPlayerMap.Load(conn.ID)
	if !ok {
		slog.Warn("join room failed: conn not bound (SessionBind missing)", "connId", conn.ID)
		return codec.NewExtMessage(framesync.ExtCmdJoinRoomRsp, framesync.EncodeJoinRoomError(framesync.JoinRoomNotBound))
	}
	playerId := val.(int32)
	bindPlayerToRoom(playerId, conn, room)

	slog.Info("player joined room", "playerId", playerId, "roomId", room.ID, "online", room.PlayerCount(), "maxPlayers", room.MaxPlayers(), "existingPlayers", existingPlayers)

	// 通知同房其他玩家
	broadcastToRoom(room, playerId, codec.NewExtMessage(framesync.ExtCmdPlayerJoined, framesync.EncodePlayerId(playerId)))

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
				sendMsg(conn, codec.NewExtMessage(framesync.ExtCmdRoomSnapshot, snapshotMsg))
				replayFrom = snapshotFrame
				slog.Info("sent room snapshot to late-join player", "playerId", playerId, "snapshotFrame", snapshotFrame, "bytes", len(snapshotData))
			} else {
				// 无快照兜底：从缓冲区最旧帧开始补帧（最佳努力）
				oldestFrame := room.OldestBufferedFrame()
				if oldestFrame > 0 {
					replayFrom = oldestFrame - 1 // GetFramesSince 是 afterFrame，所以 -1
				}
				slog.Warn("no snapshot for late-join player, replaying from oldest buffered frame", "playerId", playerId, "oldestFrame", oldestFrame)
			}

			// 2. StartFrameSync
			initData := framesync.InitData{
				FrameRate:           room.FrameRate(),
				FrameInterval:       1000 / room.FrameRate(),
				StartTime:           room.StartTime(),
				SnapshotInterval:    int32(cfg.SnapshotIntervalFrames),
				QuickReconnectMaxMs: int32(cfg.QuickReconnectMaxMs),
			}
			sendMsg(conn, codec.NewCoreMessage(framesync.CmdStartFrameSync, framesync.EncodeInitData(&initData)))

			// 3. 补帧
			if replayFrom > 0 && replayFrom < currentFrame {
				frames := room.GetFramesSince(replayFrom)
				for _, cf := range frames {
					sendMsg(conn, codec.NewCoreMessage(framesync.CmdPushFrames, cf.EncodedData))
				}
				slog.Info("late-join player replayed frames", "playerId", playerId, "count", len(frames), "fromFrame", replayFrom+1, "toFrame", currentFrame)
			}

			slog.Info("late-join player ready", "playerId", playerId, "roomId", room.ID, "frame", currentFrame)
		}()
	}

	// 新加入的玩家自动收到 KV 全量同步
	if !room.DataStoreEmpty() {
		go func() {
			time.Sleep(15 * time.Millisecond) // 确保 JoinRoomRsp 先到达
			entries, version := room.GetDataSnapshot()
			sendMsg(conn, codec.NewExtMessage(framesync.ExtCmdPushDataSync, framesync.EncodePushDataSync(version, entries)))
		}()
	}

	return codec.NewExtMessage(framesync.ExtCmdJoinRoomRsp, framesync.EncodeJoinRoomRsp(playerId, room.ID, existingPlayers))
}

func handleLeaveRoom(conn *transport.Conn, msg *codec.Message) *codec.Message {
	val, ok := connPlayerMap.Load(conn.ID)
	if !ok {
		return codec.NewExtMessage(framesync.ExtCmdLeaveRoomRsp, nil)
	}
	playerId := val.(int32)

	roomVal, ok := playerRoomMap.LoadAndDelete(playerId)
	if !ok {
		return codec.NewExtMessage(framesync.ExtCmdLeaveRoomRsp, nil)
	}
	room := roomVal.(*framesync.Room)
	room.RemovePlayer(playerId)
	// 注意：不删除 connPlayerMap / playerConnMap
	// 这两个映射是 SessionBind 建立的，LeaveRoom 只清理房间关系
	// 删了会导致 re-JoinRoom 失败（"SessionBind missing"）

	// 清除该玩家的 KV 数据并广播删除
	deleted, versions := room.ClearPlayerData(playerId)
	for i, entry := range deleted {
		push := framesync.EncodePushData(versions[i], playerId, entry.Key, nil)
		broadcastToRoom(room, playerId, codec.NewExtMessage(framesync.ExtCmdPushData, push))
	}

	slog.Info("player left room", "playerId", playerId, "roomId", room.ID)

	broadcastToRoom(room, playerId, codec.NewExtMessage(framesync.ExtCmdPlayerLeft, framesync.EncodePlayerId(playerId)))

	// 空房间立即清理
	if room.TotalPlayerCount() == 0 {
		room.Stop()
		roomMgr.RemoveRoom(room.ID)
		slog.Info("room removed (empty after leave)", "roomId", room.ID)
	}

	return codec.NewExtMessage(framesync.ExtCmdLeaveRoomRsp, nil)
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

	// 解析 matchKey: [maxPlayers:2][matchKeyLen:2][matchKey:N]
	var matchKey string
	if len(msg.Data) >= 4 {
		keyLen := int(binary.LittleEndian.Uint16(msg.Data[2:4]))
		if keyLen > 0 && len(msg.Data) >= 4+keyLen {
			matchKey = string(msg.Data[4 : 4+keyLen])
		}
	}

	val, ok := connPlayerMap.Load(conn.ID)
	if !ok {
		slog.Warn("match room failed: conn not bound", "connId", conn.ID)
		return codec.NewExtMessage(framesync.ExtCmdMatchRoomRsp, make([]byte, 8))
	}
	playerId := val.(int32)

	room := roomMgr.MatchRoom(maxPlayers, matchKey)
	if room == nil {
		slog.Warn("match room rejected: server at capacity", "playerId", playerId, "maxPlayers", maxPlayers)
		return codec.NewExtMessage(framesync.ExtCmdMatchRoomRsp, framesync.EncodeJoinRoomError(framesync.JoinRoomNotFound))
	}
	existingPlayers := room.GetPlayerIds()
	bindPlayerToRoom(playerId, conn, room)

	slog.Info("player matched to room", "playerId", playerId, "roomId", room.ID, "online", room.PlayerCount(), "maxPlayers", room.MaxPlayers(), "matchKey", matchKey)

	broadcastToRoom(room, playerId, codec.NewExtMessage(framesync.ExtCmdPlayerJoined, framesync.EncodePlayerId(playerId)))

	// 迟到加入（房间已在运行）
	if room.IsRunning() {
		snapshotFrame, snapshotData := room.GetSnapshot()
		currentFrame := room.CurrentFrameNumber()

		go func() {
			time.Sleep(10 * time.Millisecond)

			var replayFrom uint32
			if snapshotData != nil && len(snapshotData) > 0 {
				snapshotMsg := framesync.EncodeSnapshot(snapshotFrame, snapshotData)
				sendMsg(conn, codec.NewExtMessage(framesync.ExtCmdRoomSnapshot, snapshotMsg))
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
			sendMsg(conn, codec.NewCoreMessage(framesync.CmdStartFrameSync, framesync.EncodeInitData(&initData)))

			if replayFrom > 0 && replayFrom < currentFrame {
				frames := room.GetFramesSince(replayFrom)
				for _, cf := range frames {
					sendMsg(conn, codec.NewCoreMessage(framesync.CmdPushFrames, cf.EncodedData))
				}
			}
		}()
	}

	// 新加入的玩家自动收到 KV 全量同步
	if !room.DataStoreEmpty() {
		go func() {
			time.Sleep(15 * time.Millisecond)
			entries, version := room.GetDataSnapshot()
			sendMsg(conn, codec.NewExtMessage(framesync.ExtCmdPushDataSync, framesync.EncodePushDataSync(version, entries)))
		}()
	}

	return codec.NewExtMessage(framesync.ExtCmdMatchRoomRsp, framesync.EncodeJoinRoomRsp(playerId, room.ID, existingPlayers))
}

// ===================== 工具函数 =====================

func bindPlayerToRoom(playerId int32, conn *transport.Conn, room *framesync.Room) {
	connPlayerMap.Store(conn.ID, playerId)
	playerRoomMap.Store(playerId, room)
	playerConnMap.Store(playerId, conn)
	room.AddPlayer(playerId, &statsConn{inner: &simConn{inner: conn, cfg: GlobalNetSim}, pid: playerId})

	// 确保 panic 恢复回调已设置（幂等）
	if room.OnPanic == nil {
		room.OnPanic = onRoomPanic
	}
}

// onRoomPanic Room tickLoop panic 后清理全局映射并移除僵尸房间
func onRoomPanic(room *framesync.Room, playerIds []int32) {
	for _, pid := range playerIds {
		playerRoomMap.Delete(pid)
	}
	roomMgr.RemoveRoom(room.ID)
	slog.Error("room cleaned up after panic", "roomId", room.ID, "evictedPlayers", len(playerIds))
}

func handleRequestStart(conn *transport.Conn, msg *codec.Message) *codec.Message {
	val, ok := connPlayerMap.Load(conn.ID)
	if !ok {
		return nil
	}
	playerId := val.(int32)

	roomVal, ok := playerRoomMap.Load(playerId)
	if !ok {
		slog.Warn("request start failed: player not in room", "playerId", playerId)
		return nil
	}
	room := roomVal.(*framesync.Room)

	if room.IsRunning() {
		slog.Info("request start: room already running", "roomId", room.ID)
		return nil
	}

	// RequestStart 携带初始快照：在第一帧推送之前存好，避免早期断线无快照
	if len(msg.Data) > 0 {
		room.SetInitialSnapshot(msg.Data)
		slog.Info("initial snapshot stored for room", "roomId", room.ID, "bytes", len(msg.Data))
	}

	slog.Info("player requested room start", "playerId", playerId, "roomId", room.ID, "online", room.PlayerCount())

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

	slog.Info("player requested room stop", "playerId", playerId, "roomId", room.ID)
	room.Stop()
	return nil
}

// sendMsg 发送消息并记录游戏 TX 流量 + 消息日志
func sendMsg(conn *transport.Conn, msg *codec.Message) {
	GameStats.RecordTx(int64(len(msg.Data)))
	framesync.Metrics.BytesSent.Add(float64(len(msg.Data)))
	logMsgFromMsg("tx", msg, connPid(conn), "")
	conn.Send(msg)
}

func broadcastToRoom(room *framesync.Room, excludePlayerId int32, msg *codec.Message) {
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
		return codec.NewExtMessage(framesync.ExtCmdUploadSnapshotRsp, []byte{0})
	}
	playerId := val.(int32)

	roomVal, ok := playerRoomMap.Load(playerId)
	if !ok {
		return codec.NewExtMessage(framesync.ExtCmdUploadSnapshotRsp, []byte{0})
	}
	room := roomVal.(*framesync.Room)

	frameNumber, snapshotData := framesync.DecodeUploadSnapshot(msg.Data)
	if snapshotData == nil {
		return codec.NewExtMessage(framesync.ExtCmdUploadSnapshotRsp, []byte{0})
	}

	accepted := room.UpdateSnapshot(frameNumber, snapshotData)
	if accepted {
		framesync.Metrics.SnapshotSizeBytes.Set(float64(len(snapshotData)))
		return codec.NewExtMessage(framesync.ExtCmdUploadSnapshotRsp, []byte{1})
	}
	return codec.NewExtMessage(framesync.ExtCmdUploadSnapshotRsp, []byte{0})
}

// handleSendEntityState 实体权威同步：透传给同房其他玩家（prepend senderPid）
func handleRequestAuthorityTransfer(conn *transport.Conn, msg *codec.Message) *codec.Message {
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

	entityId, release, ok := framesync.DecodeAuthorityTransferRequest(msg.Data)
	if !ok {
		return nil
	}

	var newOwner int32
	var changed bool

	if release {
		changed = room.ReleaseAuthority(entityId, playerId)
		newOwner = 0
	} else {
		granted, current := room.TryGrantAuthority(entityId, playerId)
		changed = granted
		newOwner = current
		if !granted {
			slog.Warn("authority denied: entity held by another player", "playerId", playerId, "entityId", entityId, "heldBy", current)
			return nil
		}
	}

	if changed {
		data := framesync.EncodeAuthorityTransferResult(entityId, newOwner)
		broadcastToRoom(room, -1, codec.NewExtMessage(framesync.ExtCmdAuthorityTransfer, data))
		slog.Info("entity authority transferred", "entityId", entityId, "newOwner", newOwner)
	}
	return nil
}

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

	pushMsg := codec.NewExtMessage(framesync.ExtCmdPushEntityState, push)
	room.ForEachOnlinePlayer(func(id int32, c framesync.PlayerConn) {
		if id != playerId {
			c.Send(pushMsg)
		}
	})
	return nil
}

// ===================== 轻量状态同步 Handler =====================

// handleSendStateMsg 状态消息：加上 playerId 前缀，转发给同房其他玩家
func handleSendStateMsg(conn *transport.Conn, msg *codec.Message) *codec.Message {
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

	push := framesync.EncodePushStateMsg(playerId, msg.Data)
	broadcastToRoom(room, playerId, codec.NewExtMessage(framesync.ExtCmdPushStateMsg, push))
	return nil
}

// handleSetData KV 数据设置：存储到房间，增量广播给其他玩家
func handleSetData(conn *transport.Conn, msg *codec.Message) *codec.Message {
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

	key, value, ok := framesync.DecodeSetData(msg.Data)
	if !ok {
		return nil
	}

	version := room.SetData(playerId, key, value)
	push := framesync.EncodePushData(version, playerId, key, value)
	broadcastToRoom(room, playerId, codec.NewExtMessage(framesync.ExtCmdPushData, push))
	return nil
}

// handleRequestDataSync 全量 KV 同步请求
func handleRequestDataSync(conn *transport.Conn, msg *codec.Message) *codec.Message {
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

	entries, version := room.GetDataSnapshot()
	return codec.NewExtMessage(framesync.ExtCmdPushDataSync, framesync.EncodePushDataSync(version, entries))
}

func handleGameRelay(conn *transport.Conn, msg *codec.Message) *codec.Message {
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

	// 转发游戏消息：在 Data 前插入 senderPid(4B)
	relayData := make([]byte, 4+len(msg.Data))
	binary.LittleEndian.PutUint32(relayData[0:4], uint32(playerId))
	if len(msg.Data) > 0 {
		copy(relayData[4:], msg.Data)
	}
	relay := codec.NewGameMessage(msg.GameCmd, relayData)
	broadcastToRoom(room, playerId, relay)
	return nil
}
