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
	"sync/atomic"
	"syscall"
	"time"

	"github.com/boomlulu/boomnetwork/codec"
	"github.com/boomlulu/boomnetwork/framesync"
	"github.com/boomlulu/boomnetwork/session"
	"github.com/boomlulu/boomnetwork/transport"
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
	wsAddr      = flag.String("ws-addr", ":9001", "WebSocket listen address for WebGL clients (empty = disabled)")
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

// roomLifecycleDelegate 框架级生命周期委托
// 处理 Reconciler 驱动的玩家超时驱逐和 panic 后的外层映射清理
// 游戏玩法层可以在此基础上 embed 并覆盖所需方法
type roomLifecycleDelegate struct {
	framesync.NoopRoomDelegate
}

func (d *roomLifecycleDelegate) OnPlayerRemoved(room *framesync.Room, playerID int32) {
	playerRoomMap.Delete(playerID)
	slog.Info("player removed (disconnect timeout)", "roomId", room.ID, "playerId", playerID)
}

func (d *roomLifecycleDelegate) OnRoomPanicked(room *framesync.Room, playerIds []int32) {
	for _, pid := range playerIds {
		playerRoomMap.Delete(pid)
	}
	roomMgr.RemoveRoom(room.ID)
	slog.Error("room cleaned up after panic", "roomId", room.ID, "evictedPlayers", len(playerIds))
}

var globalDelegate = &roomLifecycleDelegate{}

var playerCounter int32

// connContext 缓存连接对应的玩家 ID 和房间，减少 handleFrameInput 热路径上的重复 sync.Map 查找
type connContext struct {
	playerId int32
	room     *framesync.Room
}

// connContextMap 单次查找替代原来的 connPlayerMap + playerRoomMap 双查找
var connContextMap sync.Map // connID → *connContext

// replayBatchSize / replayBatchDelay 控制补帧发送速率。
// P2-2: 分批发送防止一次性写入 2400 帧撑爆 TCP 发送缓冲区；每批 100 帧后短暂 yield，
// 让 ticker goroutine 有机会发送实时帧，避免补帧流量挤占正常帧路径。
const (
	replayBatchSize  = 100
	replayBatchDelay = 5 * time.Millisecond
)

func main() {
	jsonHandler := slog.NewJSONHandler(os.Stdout, &slog.HandlerOptions{Level: &logLevel})
	slog.SetDefault(slog.New(WrapWithLogBuffer(jsonHandler)))
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
		*wsAddr      = cfg.WSAddr
		*ppr         = cfg.PlayersPerRoom
		*authToken   = cfg.AuthToken
		*metricsAddr = cfg.MetricsAddr
		*adminAddr   = cfg.AdminAddr
		*adminToken  = cfg.AdminToken
	} else {
		cfg.Addr           = *addr
		cfg.Proto          = *proto
		cfg.WSAddr         = *wsAddr
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

	// S17+M5: 注入 WebSocket Origin 白名单并构建 O(1) 查找 map
	wsAllowedOrigins = cfg.AllowedOrigins
	initWsOriginMap()

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
	// 游戏级暂停
	router.OnExt(framesync.ExtCmdRequestGamePause, txStats(handleRequestGamePause))
	router.OnExt(framesync.ExtCmdRequestGameResume, txStats(handleRequestGameResume))
	// 帧 hash 校验
	router.OnExt(framesync.ExtCmdFrameHash, txStats(handleFrameHash))
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
	server.SetOnRateLimited(func(c *transport.Conn) {
		framesync.Metrics.RateLimited.Inc()
		c.Send(codec.NewCoreMessage(framesync.CmdKicked, []byte{framesync.KickReasonRateLimit}))
	})
	server.SetOnRateLimitWarn(func(c *transport.Conn) {
		c.Send(codec.NewCoreMessage(framesync.CmdRateLimitWarning, nil))
	})
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

	// WebGL 双端口：额外启动一个 WebSocket 服务器，与主协议共享全部路由逻辑。
	// WebGL 客户端无法使用 TCP/KCP，通过此端口接入；桌面/移动客户端走主协议端口。
	if *wsAddr != "" {
		wsServer := transport.NewWsServer(rxHandler)
		wsServer.SetOnDisconnect(onClientDisconnect)
		wsServer.SetOnRateLimited(func(c *transport.Conn) {
			framesync.Metrics.RateLimited.Inc()
			c.Send(codec.NewCoreMessage(framesync.CmdKicked, []byte{framesync.KickReasonRateLimit}))
		})
		wsServer.SetOnRateLimitWarn(func(c *transport.Conn) {
			c.Send(codec.NewCoreMessage(framesync.CmdRateLimitWarning, nil))
		})
		wsServer.SetMaxConns(cfg.MaxConnections)
		wsServer.SetSecurity(secCfg)
		if err := wsServer.Listen(*wsAddr); err != nil {
			slog.Error("ws server listen failed", "err", err)
			os.Exit(1)
		}
		slog.Info("websocket server running", "addr", *wsAddr)
	}

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

	// RoomReconciler：期望状态 vs 实际状态协调循环
	// 替代散落的 edge-triggered 生命周期管理（30s 销毁 goroutine、tickLoop cleanupTicker）
	emptyGraceSec := cfg.EmptyGraceSec
	if emptyGraceSec <= 0 {
		emptyGraceSec = 30
	}
	reconciler := framesync.NewRoomReconciler(
		roomMgr,
		globalDelegate,
		time.Duration(emptyGraceSec)*time.Second, // emptyGrace：所有玩家离开后的销毁宽限期
		5*time.Second,                             // reconcile interval
	)
	go reconciler.Run(ctx)

	// 兜底清理：从未有玩家加入的空房间（Reconciler 不处理 emptyAt 为零的房间）
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

// L1: sync.Map 安全类型断言辅助函数，防止类型不匹配时 panic
// 键类型：connPlayerMap/connContextMap 以 int(conn.ID) 为键；playerRoomMap/playerConnMap 以 int32(playerId) 为键。
func loadConnPlayerId(connId int) (int32, bool) {
	val, ok := connPlayerMap.Load(connId)
	if !ok {
		return 0, false
	}
	pid, ok2 := val.(int32)
	return pid, ok2
}

func loadConnPlayerIdAndDelete(connId int) (int32, bool) {
	val, ok := connPlayerMap.LoadAndDelete(connId)
	if !ok {
		return 0, false
	}
	pid, ok2 := val.(int32)
	return pid, ok2
}

func loadPlayerRoom(playerId int32) (*framesync.Room, bool) {
	val, ok := playerRoomMap.Load(playerId)
	if !ok {
		return nil, false
	}
	room, ok2 := val.(*framesync.Room)
	return room, ok2
}

func loadPlayerRoomAndDelete(playerId int32) (*framesync.Room, bool) {
	val, ok := playerRoomMap.LoadAndDelete(playerId)
	if !ok {
		return nil, false
	}
	room, ok2 := val.(*framesync.Room)
	return room, ok2
}

func loadConnContext(connId int) (*connContext, bool) {
	val, ok := connContextMap.Load(connId)
	if !ok {
		return nil, false
	}
	ctx, ok2 := val.(*connContext)
	return ctx, ok2
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
	// L2: 检查 Write 错误，避免静默丢失 READY 通知
	if _, err := conn.Write([]byte("READY=1")); err != nil {
		slog.Warn("sd_notify write failed", "error", err)
		return
	}
	slog.Info("sd_notify: READY=1 sent")
}

func nextPlayerId() int32 {
	return atomic.AddInt32(&playerCounter, 1)
}

func onClientDisconnect(conn *transport.Conn) {
	framesync.Metrics.ConnectionsCurrent.Dec()

	playerId, ok := loadConnPlayerIdAndDelete(conn.ID) // L1: safe type assertion
	if !ok {
		return
	}
	connContextMap.Delete(conn.ID) // 清理热路径缓存

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

	room, ok := loadPlayerRoom(playerId) // L1: safe type assertion
	if !ok {
		return
	}
	room.DisconnectPlayer(playerId)
	slog.Info("player disconnected from room (kept for reconnect)", "playerId", playerId, "roomId", room.ID)

	// 通知其他客户端该玩家临时掉线（非永久离开）
	if room.IsRunning() {
		room.EnqueueEvent(framesync.FrameEventPlayerOffline, playerId)
	} else {
		broadcastToRoom(room, playerId, codec.NewExtMessage(framesync.ExtCmdPlayerOffline, framesync.EncodePlayerId(playerId)))
	}

	// 释放断线玩家持有的所有实体权威
	releasedEntities := room.ReleaseAllAuthority(playerId)
	for _, eid := range releasedEntities {
		data := framesync.EncodeAuthorityTransferResult(eid, 0)
		broadcastToRoom(room, -1, codec.NewExtMessage(framesync.ExtCmdAuthorityTransfer, data))
		slog.Info("entity authority released on disconnect", "entityId", eid, "playerId", playerId)
	}

	// 房间销毁由 RoomReconciler 负责：
	// 当所有玩家 keepalive 到期后 emptyAt 会被设置，Reconciler 下一轮检测到 ShouldDestroy 后销毁
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
	atomic.AddInt64(&totalConnEver, 1)

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
	ctx, ok := loadConnContext(conn.ID) // L1: safe type assertion
	if !ok {
		return nil
	}
	// H6: 验证房间仍在 RoomManager 中，防止向已清理的房间写入
	if roomMgr != nil && roomMgr.GetRoom(ctx.room.ID) == nil {
		connContextMap.Delete(conn.ID)
		return nil
	}
	ctx.room.OnInput(ctx.playerId, msg.Data)
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
	room, ok2 := roomVal.(*framesync.Room) // L1: safe type assertion
	if !ok2 {
		return nil
	}

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
	connContextMap.Store(conn.ID, &connContext{playerId: playerId, room: room})
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
	if room.IsRunning() {
		room.EnqueueEvent(framesync.FrameEventPlayerOnline, playerId)
	} else {
		broadcastToRoom(room, playerId, codec.NewExtMessage(framesync.ExtCmdPlayerOnline, framesync.EncodePlayerId(playerId)))
	}

	// 异步补帧（P2-2: 分批发送，每批 replayBatchSize 帧后 sleep replayBatchDelay，防止撑爆 TCP 缓冲区）
	if replayFrom > 0 && replayFrom < currentFrame {
		go func() {
			frames := room.GetFramesSince(replayFrom)
			for i, cf := range frames {
				sendMsg(conn, codec.NewCoreMessage(framesync.CmdPushFrames, cf.EncodedData))
				if (i+1)%replayBatchSize == 0 && i+1 < len(frames) {
					time.Sleep(replayBatchDelay)
				}
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

	// 解析可选 matchKey: [MaxPlayers:2][MatchKeyLen:2][MatchKey:N]（向后兼容旧客户端）
	var matchKey string
	if len(msg.Data) >= 4 {
		keyLen := int(binary.LittleEndian.Uint16(msg.Data[2:4]))
		if keyLen > 0 && len(msg.Data) >= 4+keyLen {
			matchKey = string(msg.Data[4 : 4+keyLen])
		}
	}

	room := roomMgr.CreateRoomWithMaxPlayers(maxPlayers, matchKey)
	if room == nil {
		slog.Warn("create room rejected: server at capacity", "maxPlayers", maxPlayers)
		return codec.NewExtMessage(framesync.ExtCmdCreateRoomRsp, make([]byte, 4)) // roomId=0 signals failure
	}
	rsp := make([]byte, 4)
	binary.LittleEndian.PutUint32(rsp, uint32(room.ID))

	slog.Info("room created", "roomId", room.ID, "maxPlayers", maxPlayers, "matchKey", matchKey)
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
	playerId, ok := loadConnPlayerId(conn.ID) // L1: safe type assertion
	if !ok {
		slog.Warn("join room failed: conn not bound (SessionBind missing)", "connId", conn.ID)
		return codec.NewExtMessage(framesync.ExtCmdJoinRoomRsp, framesync.EncodeJoinRoomError(framesync.JoinRoomNotBound))
	}
	bindPlayerToRoom(playerId, conn, room)

	slog.Info("player joined room", "playerId", playerId, "roomId", room.ID, "online", room.PlayerCount(), "maxPlayers", room.MaxPlayers(), "existingPlayers", existingPlayers)

	// 通知同房其他玩家
	if room.IsRunning() {
		room.EnqueueEvent(framesync.FrameEventPlayerJoined, playerId)
	} else {
		broadcastToRoom(room, playerId, codec.NewExtMessage(framesync.ExtCmdPlayerJoined, framesync.EncodePlayerId(playerId)))
	}

	// P2-3: 先通过 sendMsg 直接发送 JoinRoomRsp（附上原始 Seq），再启动后续 goroutine。
	// TCP/KCP 保证同一连接的 Send 调用严格 FIFO，后续消息一定在 JoinRoomRsp 之后到达客户端，
	// 无需 time.Sleep 保序。
	joinRsp := codec.NewExtMessage(framesync.ExtCmdJoinRoomRsp, framesync.EncodeJoinRoomRsp(playerId, room.ID, existingPlayers))
	joinRsp.HasSeq = msg.HasSeq
	joinRsp.Seq = msg.Seq
	sendMsg(conn, joinRsp)

	// 如果房间已在运行，给迟到者：快照 → StartFrameSync → 补帧 → KV 同步（合并为单 goroutine）
	if room.IsRunning() || !room.DataStoreEmpty() {
		isRunning := room.IsRunning()
		var snapshotFrame uint32
		var snapshotData []byte
		var currentFrame uint32
		if isRunning {
			snapshotFrame, snapshotData = room.GetSnapshot()
			currentFrame = room.CurrentFrameNumber()
		}
		hasKV := !room.DataStoreEmpty()

		// H1: 补帧 goroutine 加 30s 超时，防止慢速客户端占用 goroutine 无限期
		go func() {
			ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
			defer cancel()

			// 1. 迟到者补帧（仅运行中才需要）
			if isRunning {
				var replayFrom uint32
				if len(snapshotData) > 0 {
					snapshotMsg := framesync.EncodeSnapshot(snapshotFrame, snapshotData)
					sendMsg(conn, codec.NewExtMessage(framesync.ExtCmdRoomSnapshot, snapshotMsg))
					replayFrom = snapshotFrame
					slog.Info("sent room snapshot to late-join player", "playerId", playerId, "snapshotFrame", snapshotFrame, "bytes", len(snapshotData))
				} else {
					oldestFrame := room.OldestBufferedFrame()
					if oldestFrame > 0 {
						replayFrom = oldestFrame - 1
					}
					slog.Warn("no snapshot for late-join player, replaying from oldest buffered frame", "playerId", playerId, "oldestFrame", oldestFrame)
				}

				initData := framesync.InitData{
					FrameRate:           room.FrameRate(),
					FrameInterval:       1000 / room.FrameRate(),
					StartTime:           room.StartTime(),
					SnapshotInterval:    int32(cfg.SnapshotIntervalFrames),
					QuickReconnectMaxMs: int32(cfg.QuickReconnectMaxMs),
				}
				sendMsg(conn, codec.NewCoreMessage(framesync.CmdStartFrameSync, framesync.EncodeInitData(&initData)))

				// P2-2: 分批补帧，防止一次性发送 2400 帧撑爆 TCP 缓冲区
				// replayFrom=0 时（无快照、oldestFrame=1）也需回放，GetFramesSince(0) 返回所有帧>0
				if replayFrom < currentFrame {
					frames := room.GetFramesSince(replayFrom)
					for i, cf := range frames {
						select {
						case <-ctx.Done():
							slog.Warn("late-join replay timed out", "playerId", playerId, "sentFrames", i, "totalFrames", len(frames))
							return
						default:
						}
						sendMsg(conn, codec.NewCoreMessage(framesync.CmdPushFrames, cf.EncodedData))
						if (i+1)%replayBatchSize == 0 && i+1 < len(frames) {
							time.Sleep(replayBatchDelay)
						}
					}
					slog.Info("late-join player replayed frames", "playerId", playerId, "count", len(frames), "fromFrame", replayFrom+1, "toFrame", currentFrame)
				}

				slog.Info("late-join player ready", "playerId", playerId, "roomId", room.ID, "frame", currentFrame)
			}

			// 2. KV 全量同步（无论运行中与否，只要有数据）
			if hasKV {
				select {
				case <-ctx.Done():
					slog.Warn("late-join KV sync timed out", "playerId", playerId)
					return
				default:
				}
				entries, version := room.GetDataSnapshot()
				sendMsg(conn, codec.NewExtMessage(framesync.ExtCmdPushDataSync, framesync.EncodePushDataSync(version, entries)))
			}
		}()
	}

	// JoinRoomRsp 已在上方直接发送，此处返回 nil 避免 dispatch 层重复发送
	return nil
}

func handleLeaveRoom(conn *transport.Conn, msg *codec.Message) *codec.Message {
	playerId, ok := loadConnPlayerId(conn.ID) // L1: safe type assertion
	if !ok {
		return codec.NewExtMessage(framesync.ExtCmdLeaveRoomRsp, nil)
	}

	room, ok := loadPlayerRoomAndDelete(playerId) // L1: safe type assertion
	if !ok {
		return codec.NewExtMessage(framesync.ExtCmdLeaveRoomRsp, nil)
	}
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

	if room.IsRunning() {
		room.EnqueueEvent(framesync.FrameEventPlayerLeft, playerId)
	} else {
		broadcastToRoom(room, playerId, codec.NewExtMessage(framesync.ExtCmdPlayerLeft, framesync.EncodePlayerId(playerId)))
	}

	// 显式离开：立即销毁空房间（Reconciler 兜底相同效果，这里保持即时响应）
	if room.TotalPlayerCount() == 0 {
		roomMgr.RemoveRoom(room.ID)
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

	playerId, ok := loadConnPlayerId(conn.ID) // L1: safe type assertion
	if !ok {
		slog.Warn("match room failed: conn not bound", "connId", conn.ID)
		return codec.NewExtMessage(framesync.ExtCmdMatchRoomRsp, make([]byte, 8))
	}

	room := roomMgr.MatchRoom(maxPlayers, matchKey)
	if room == nil {
		slog.Warn("match room rejected: server at capacity", "playerId", playerId, "maxPlayers", maxPlayers)
		return codec.NewExtMessage(framesync.ExtCmdMatchRoomRsp, framesync.EncodeJoinRoomError(framesync.JoinRoomNotFound))
	}
	existingPlayers := room.GetPlayerIds()
	bindPlayerToRoom(playerId, conn, room)

	slog.Info("player matched to room", "playerId", playerId, "roomId", room.ID, "online", room.PlayerCount(), "maxPlayers", room.MaxPlayers(), "matchKey", matchKey)

	if room.IsRunning() {
		room.EnqueueEvent(framesync.FrameEventPlayerJoined, playerId)
	} else {
		broadcastToRoom(room, playerId, codec.NewExtMessage(framesync.ExtCmdPlayerJoined, framesync.EncodePlayerId(playerId)))
	}

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

			// replayFrom=0 时（无快照、oldestFrame=1）也需回放，GetFramesSince(0) 返回所有帧>0
			if replayFrom < currentFrame {
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
	connContextMap.Store(conn.ID, &connContext{playerId: playerId, room: room})
	room.AddPlayer(playerId, &statsConn{inner: &simConn{inner: conn, cfg: GlobalNetSim}, pid: playerId})
	room.SetDelegate(globalDelegate) // 幂等：多次 SetDelegate 安全
}

func handleRequestStart(conn *transport.Conn, msg *codec.Message) *codec.Message {
	playerId, ok := loadConnPlayerId(conn.ID) // L1: safe type assertion
	if !ok {
		return nil
	}

	room, ok := loadPlayerRoom(playerId) // L1: safe type assertion
	if !ok {
		slog.Warn("request start failed: player not in room", "playerId", playerId)
		return nil
	}

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
	playerId, ok := loadConnPlayerId(conn.ID) // L1: safe type assertion
	if !ok {
		return nil
	}

	room, ok := loadPlayerRoom(playerId) // L1: safe type assertion
	if !ok {
		return nil
	}

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
	playerId, ok := loadConnPlayerId(conn.ID) // L1: safe type assertion
	if !ok {
		return codec.NewExtMessage(framesync.ExtCmdUploadSnapshotRsp, []byte{0})
	}

	room, ok := loadPlayerRoom(playerId) // L1: safe type assertion
	if !ok {
		return codec.NewExtMessage(framesync.ExtCmdUploadSnapshotRsp, []byte{0})
	}

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
	playerId, ok := loadConnPlayerId(conn.ID) // L1: safe type assertion
	if !ok {
		return nil
	}

	room, ok := loadPlayerRoom(playerId) // L1: safe type assertion
	if !ok {
		return nil
	}

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
	playerId, ok := loadConnPlayerId(conn.ID) // L1: safe type assertion
	if !ok {
		return nil
	}

	room, ok := loadPlayerRoom(playerId) // L1: safe type assertion
	if !ok {
		return nil
	}

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
	playerId, ok := loadConnPlayerId(conn.ID) // L1: safe type assertion
	if !ok {
		return nil
	}

	room, ok := loadPlayerRoom(playerId) // L1: safe type assertion
	if !ok {
		return nil
	}

	push := framesync.EncodePushStateMsg(playerId, msg.Data)
	broadcastToRoom(room, playerId, codec.NewExtMessage(framesync.ExtCmdPushStateMsg, push))
	return nil
}

// handleSetData KV 数据设置：存储到房间，增量广播给其他玩家
func handleSetData(conn *transport.Conn, msg *codec.Message) *codec.Message {
	playerId, ok := loadConnPlayerId(conn.ID) // L1: safe type assertion
	if !ok {
		return nil
	}

	room, ok := loadPlayerRoom(playerId) // L1: safe type assertion
	if !ok {
		return nil
	}

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
	playerId, ok := loadConnPlayerId(conn.ID) // L1: safe type assertion
	if !ok {
		return nil
	}

	room, ok := loadPlayerRoom(playerId) // L1: safe type assertion
	if !ok {
		return nil
	}

	entries, version := room.GetDataSnapshot()
	return codec.NewExtMessage(framesync.ExtCmdPushDataSync, framesync.EncodePushDataSync(version, entries))
}

// handleFrameHash 帧 hash 上报：收集并检测 desync
func handleFrameHash(conn *transport.Conn, msg *codec.Message) *codec.Message {
	playerId, ok := loadConnPlayerId(conn.ID) // L1: safe type assertion
	if !ok {
		return nil
	}

	frameNum, hash, ok := framesync.DecodeFrameHash(msg.Data)
	if !ok {
		return nil
	}

	room, ok := loadPlayerRoom(playerId) // L1: safe type assertion
	if !ok {
		return nil
	}

	if room.ReportFrameHash(playerId, frameNum, hash) {
		// Desync detected
		hashes := room.GetFrameHashes(frameNum)
		slog.Error("DESYNC DETECTED",
			"roomId", room.ID,
			"frame", frameNum,
			"hashes", hashes,
		)
		mismatchData := framesync.EncodeFrameHashMismatch(frameNum, hashes)
		broadcastToRoom(room, -1, codec.NewExtMessage(framesync.ExtCmdFrameHashMismatch, mismatchData))
		broadcastToRoom(room, -1, codec.NewExtMessage(framesync.ExtCmdFrameSyncPaused, []byte{byte(framesync.PauseReasonDesync)}))
		// 通知 GM Hub 推送 desync 事件
		if gmHub != nil {
			gmHub.NotifyDesync(DesyncEvent{RoomID: room.ID, FrameNumber: frameNum, PlayerHashes: hashes})
		}
	}
	return nil
}

// handleRequestGamePause 客户端请求游戏级暂停
func handleRequestGamePause(conn *transport.Conn, msg *codec.Message) *codec.Message {
	playerId, ok := loadConnPlayerId(conn.ID) // L1: safe type assertion
	if !ok {
		return nil
	}

	room, ok := loadPlayerRoom(playerId) // L1: safe type assertion
	if !ok {
		return nil
	}

	if room.GamePause() {
		slog.Info("game paused by player", "roomId", room.ID, "playerId", playerId)
		broadcastToRoom(room, -1, codec.NewExtMessage(framesync.ExtCmdFrameSyncPaused, []byte{byte(framesync.PauseReasonGamePause)}))
	}
	return nil
}

// handleRequestGameResume 客户端请求解除游戏级暂停
func handleRequestGameResume(conn *transport.Conn, msg *codec.Message) *codec.Message {
	playerId, ok := loadConnPlayerId(conn.ID) // L1: safe type assertion
	if !ok {
		return nil
	}

	room, ok := loadPlayerRoom(playerId) // L1: safe type assertion
	if !ok {
		return nil
	}

	if room.GameResume() {
		slog.Info("game resumed by player", "roomId", room.ID, "playerId", playerId)
		broadcastToRoom(room, -1, codec.NewExtMessage(framesync.ExtCmdFrameSyncResumed, nil))
	}
	return nil
}

func handleGameRelay(conn *transport.Conn, msg *codec.Message) *codec.Message {
	playerId, ok := loadConnPlayerId(conn.ID) // L1: safe type assertion
	if !ok {
		return nil
	}

	room, ok := loadPlayerRoom(playerId) // L1: safe type assertion
	if !ok {
		return nil
	}

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
