package main

import (
	"context"
	"encoding/binary"
	"flag"
	"log/slog"
	"math"
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

var playerCounter int64

// connContext 缓存连接对应的玩家 ID 和房间，减少 handleFrameInput 热路径上的重复 sync.Map 查找
type connContext struct {
	playerId int32
	room     *framesync.Room
}

// connContextMap 单次查找替代原来的 connPlayerMap + playerRoomMap 双查找
var connContextMap sync.Map // connID → *connContext


// reliableDispatch 用于 handleReliableMsg 内部分发解包后的 inner 消息
// 在 router.Freeze() 之后由 main() 设置，此后只读，无需同步。
var reliableDispatch func(*transport.Conn, *codec.Message) *codec.Message

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
	// 可靠通道（C→S）
	router.OnExt(framesync.ExtCmdReliableMsg, txStats(handleReliableMsg))
	// Game Cmd (uint32) — 服务器透传
	router.OnGame(txStats(handleGameRelay))

	// 路由注册完毕，冻结路由表 — Dispatch 不再加锁
	router.Freeze()
	reliableDispatch = router.Dispatch

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
	var wsServer *transport.WsServer
	if *wsAddr != "" {
		wsServer = transport.NewWsServer(rxHandler)
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

	// IP 限流表定期清理（防止公网扫描导致内存泄漏）
	go func() {
		ticker := time.NewTicker(5 * time.Minute)
		defer ticker.Stop()
		for {
			select {
			case <-ctx.Done():
				return
			case <-ticker.C:
				if s, ok := server.(interface{ CleanupIPLimiter(time.Duration) int }); ok {
					if n := s.CleanupIPLimiter(10 * time.Minute); n > 0 {
						slog.Info("IP limiter cleanup", "deleted", n)
					}
				}
				if wsServer != nil {
					if n := wsServer.CleanupIPLimiter(10 * time.Minute); n > 0 {
						slog.Info("WS IP limiter cleanup", "deleted", n)
					}
				}
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
	// ARCH-05: 同步关闭 WebSocket server，避免 WS goroutine 泄漏
	if wsServer != nil {
		wsServer.Close()
	}

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
	id := atomic.AddInt64(&playerCounter, 1)
	if id > math.MaxInt32 {
		// 回绕：重置为 1（极低概率，约 248 天 @ 100 conn/s）
		atomic.StoreInt64(&playerCounter, 1)
		return 1
	}
	return int32(id)
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
		bindPlayerToRoom(playerId, conn, room, false)
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
	// PERF-04: 用原子标志 IsAlive() 替代 GetRoom()，消除每帧 Mutex Lock/Unlock
	if !ctx.room.IsAlive() {
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
		rsp := framesync.EncodeReconnectRsp(framesync.ReconnectFail, 0, 0, 0, 0, nil)
		return codec.NewCoreMessage(framesync.CmdReconnectRsp, rsp)
	}

	playerId := int32(binary.LittleEndian.Uint32(msg.Data[0:4]))
	var lastFrame uint32
	if len(msg.Data) >= 8 {
		lastFrame = binary.LittleEndian.Uint32(msg.Data[4:8])
	}
	var lastS2CSeq uint32
	if len(msg.Data) >= 12 {
		lastS2CSeq = binary.LittleEndian.Uint32(msg.Data[8:12])
	}

	roomVal, ok := playerRoomMap.Load(playerId)
	if !ok {
		slog.Warn("reconnect failed: player not found in any room", "playerId", playerId)
		framesync.Metrics.ReconnectFail.Inc()
		rsp := framesync.EncodeReconnectRsp(framesync.ReconnectFail, 0, 0, 0, 0, nil)
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
		rsp := framesync.EncodeReconnectRsp(framesync.ReconnectFail, 0, 0, 0, 0, nil)
		return codec.NewCoreMessage(framesync.CmdReconnectRsp, rsp)
	}

	currentFrame := room.CurrentFrameNumber()
	snapshotFrame, snapshotData := room.GetSnapshot()

	// 快速重连路径: lastFrame > 0，检查帧环形缓冲区
	if lastFrame > 0 {
		oldestFrame := room.OldestBufferedFrame()
		if oldestFrame > 0 && lastFrame < oldestFrame {
			slog.Warn("reconnect frame buffer stale", "playerId", playerId, "lastFrame", lastFrame, "oldestFrame", oldestFrame)
			rsp := framesync.EncodeReconnectRsp(framesync.ReconnectFailBufferStale, room.ID, currentFrame, 0, 0, nil)
			return codec.NewCoreMessage(framesync.CmdReconnectRsp, rsp)
		}
		// 检查 S→C reliable buffer
		if _, s2cStale := room.GetS2CReliableSince(playerId, lastS2CSeq); s2cStale {
			slog.Warn("reconnect s2c reliable buffer stale", "playerId", playerId, "lastS2CSeq", lastS2CSeq)
			rsp := framesync.EncodeReconnectRsp(framesync.ReconnectFailS2CBufStale, room.ID, currentFrame, 0, 0, nil)
			return codec.NewCoreMessage(framesync.CmdReconnectRsp, rsp)
		}
	}

	// 先保存旧连接引用，更新所有映射到新连接后再关闭（避免旧连接的 onDisconnect 干扰新映射）
	var oldConn *transport.Conn
	if v, ok := playerConnMap.Load(playerId); ok {
		if oc := v.(*transport.Conn); oc != conn {
			oldConn = oc
		}
	}

	// 更新连接映射（AddPlayer 在 replayFrom 确定后调用，delivery loop 从正确帧号起追）
	connPlayerMap.Store(conn.ID, playerId)
	playerConnMap.Store(playerId, conn)
	connContextMap.Store(conn.ID, &connContext{playerId: playerId, room: room})

	// 新映射建立后再关闭旧连接（其 onDisconnect CAS 检查会跳过新连接的映射）
	if oldConn != nil {
		connPlayerMap.Delete(oldConn.ID)
		connContextMap.Delete(oldConn.ID)
		oldConn.Close()
	}
	room.SetDelegate(globalDelegate) // 幂等

	// 读取 serverLastC2SSeq（重连成功后告知客户端需要从哪条 C→S 开始重发）
	serverLastC2SSeq := room.GetLastProcessedC2SSeq(playerId)

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
	rsp := framesync.EncodeReconnectRsp(framesync.ReconnectSuccess, room.ID, currentFrame, snapshotFrame, serverLastC2SSeq, snapshotData)

	// 原子读取房间状态，避免连续两次独立读取的 TOCTOU（P2-13）
	roomState := room.GetState()

	// 通知同房其他玩家：恢复在线（非新加入）
	if roomState.Running {
		room.EnqueueEvent(framesync.FrameEventPlayerOnline, playerId)
	} else {
		broadcastToRoom(room, playerId, codec.NewExtMessage(framesync.ExtCmdPlayerOnline, framesync.EncodePlayerId(playerId)))
	}

	// 先从 handler goroutine 发送 ReconnectRsp（在 delivery loop 启动前，保证顺序）
	_ = sendMsg(conn, codec.NewCoreMessage(framesync.CmdReconnectRsp, rsp))

	// 若房间处于游戏级暂停，在 delivery loop 前补发（preamble 之外，conn 互斥保证顺序）
	if roomState.GamePaused {
		_ = sendMsg(conn, codec.NewExtMessage(framesync.ExtCmdFrameSyncPaused, []byte{byte(framesync.PauseReasonGamePause)}))
	}

	// 构建 delivery loop 前导消息（S→C reliable 在帧数据前到达）
	var preamble []*codec.Message
	if lastFrame > 0 {
		s2cMsgs, _ := room.GetS2CReliableSince(playerId, lastS2CSeq)
		preamble = append(preamble, s2cMsgs...)
		if len(s2cMsgs) > 0 {
			slog.Info("player reconnect: queuing s2c reliable preamble", "playerId", playerId, "count", len(s2cMsgs), "fromSeq", lastS2CSeq+1)
		}
	}

	// AddPlayer 启动 delivery loop：preamble → Phase 1（追帧）→ Phase 2（实时）
	// 追帧和实时帧均由 delivery loop 单 goroutine 串行写 conn，消除竞态。
	// replaying=true：追帧完成前不参与实时广播，delivery loop 内部调用 SetPlayerLive 升级。
	wrappedConn := &statsConn{inner: &simConn{inner: conn, cfg: GlobalNetSim}, pid: playerId}
	room.AddPlayer(playerId, wrappedConn, replayFrom > 0, replayFrom, preamble...)

	slog.Info("player reconnected", "playerId", playerId, "roomId", room.ID, "serverFrame", currentFrame, "snapshotFrame", snapshotFrame, "serverLastC2SSeq", serverLastC2SSeq, "replayFrom", replayFrom)
	return nil
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
	// 只绑定映射，不启动 delivery loop（需先确定 startFrame + preamble）
	bindMapsToRoom(playerId, conn, room)
	wrappedConn := &statsConn{inner: &simConn{inner: conn, cfg: GlobalNetSim}, pid: playerId}

	slog.Info("player joined room", "playerId", playerId, "roomId", room.ID, "online", room.PlayerCount(), "maxPlayers", room.MaxPlayers(), "existingPlayers", existingPlayers)

	// 通知同房其他玩家
	if room.IsRunning() {
		room.EnqueueEvent(framesync.FrameEventPlayerJoined, playerId)
	} else {
		broadcastToRoom(room, playerId, codec.NewExtMessage(framesync.ExtCmdPlayerJoined, framesync.EncodePlayerId(playerId)))
	}

	// P2-3: 先通过 sendMsg 直接发送 JoinRoomRsp（附上原始 Seq），再启动后续 goroutine。
	joinRsp := codec.NewExtMessage(framesync.ExtCmdJoinRoomRsp, framesync.EncodeJoinRoomRsp(playerId, room.ID, existingPlayers))
	joinRsp.HasSeq = msg.HasSeq
	joinRsp.Seq = msg.Seq
	_ = sendMsg(conn, joinRsp)

	if room.IsRunning() {
		// 迟到加入：goroutine 构建 preamble（快照 + StartFrameSync + KV），然后 AddPlayer 启动 delivery loop
		snapshotFrame, snapshotData := room.GetSnapshot()
		currentFrame := room.CurrentFrameNumber()
		hasKV := !room.DataStoreEmpty()

		go func() {
			var preamble []*codec.Message
			var replayFrom uint32

			if len(snapshotData) > 0 {
				snapshotMsg := framesync.EncodeSnapshot(snapshotFrame, snapshotData)
				preamble = append(preamble, codec.NewExtMessage(framesync.ExtCmdRoomSnapshot, snapshotMsg))
				replayFrom = snapshotFrame
				slog.Info("late-join (joinRoom): snapshot in preamble", "playerId", playerId, "snapshotFrame", snapshotFrame, "snapshotBytes", len(snapshotData))
			} else {
				oldestFrame := room.OldestBufferedFrame()
				if oldestFrame > 0 {
					replayFrom = oldestFrame - 1
				}
				slog.Info("late-join (joinRoom): no snapshot, replay from frames", "playerId", playerId, "oldestFrame", oldestFrame, "replayFrom", replayFrom, "currentFrame", currentFrame)
			}

			initData := framesync.InitData{
				FrameRate:           room.FrameRate(),
				FrameInterval:       1000 / room.FrameRate(),
				StartTime:           room.StartTime(),
				SnapshotInterval:    int32(cfg.SnapshotIntervalFrames),
				QuickReconnectMaxMs: int32(cfg.QuickReconnectMaxMs),
			}
			preamble = append(preamble, codec.NewCoreMessage(framesync.CmdStartFrameSync, framesync.EncodeInitData(&initData)))

			if hasKV {
				entries, version := room.GetDataSnapshot()
				preamble = append(preamble, codec.NewExtMessage(framesync.ExtCmdPushDataSync, framesync.EncodePushDataSync(version, entries)))
			}

			// AddPlayer 启动 delivery loop：preamble → Phase 1（追帧）→ Phase 2（实时）
			// replaying=true：追帧完成前不参与实时广播，delivery loop 内部调用 SetPlayerLive 升级。
			room.AddPlayer(playerId, wrappedConn, true, replayFrom, preamble...)
			slog.Info("late-join player delivery loop started", "playerId", playerId, "roomId", room.ID, "frame", currentFrame, "replayFrom", replayFrom)
		}()
	} else {
		// 房间未运行：立即以 startFrame=0 启动 delivery loop（Phase 1 为空，Phase 2 等待实时帧）
		room.AddPlayer(playerId, wrappedConn, false, 0)
		if !room.DataStoreEmpty() {
			entries, version := room.GetDataSnapshot()
			_ = sendMsg(conn, codec.NewExtMessage(framesync.ExtCmdPushDataSync, framesync.EncodePushDataSync(version, entries)))
		}
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
	// 只绑定映射，delivery loop 在确定 startFrame + preamble 后启动
	bindMapsToRoom(playerId, conn, room)
	wrappedConn := &statsConn{inner: &simConn{inner: conn, cfg: GlobalNetSim}, pid: playerId}

	slog.Info("player matched to room", "playerId", playerId, "roomId", room.ID, "online", room.PlayerCount(), "maxPlayers", room.MaxPlayers(), "matchKey", matchKey)

	if room.IsRunning() {
		room.EnqueueEvent(framesync.FrameEventPlayerJoined, playerId)
	} else {
		broadcastToRoom(room, playerId, codec.NewExtMessage(framesync.ExtCmdPlayerJoined, framesync.EncodePlayerId(playerId)))
	}

	if room.IsRunning() {
		// 迟到加入：goroutine 构建 preamble，然后 AddPlayer 启动 delivery loop
		snapshotFrame, snapshotData := room.GetSnapshot()
		currentFrame := room.CurrentFrameNumber()

		go func() {
			ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
			defer cancel()

			var preamble []*codec.Message
			var replayFrom uint32

			if len(snapshotData) > 0 {
				snapshotMsg := framesync.EncodeSnapshot(snapshotFrame, snapshotData)
				preamble = append(preamble, codec.NewExtMessage(framesync.ExtCmdRoomSnapshot, snapshotMsg))
				replayFrom = snapshotFrame
				slog.Info("late-join (matchRoom): snapshot in preamble", "playerId", playerId, "snapshotFrame", snapshotFrame, "snapshotBytes", len(snapshotData))
			} else {
				oldestFrame := room.OldestBufferedFrame()
				if oldestFrame > 0 {
					replayFrom = oldestFrame - 1
				}
				slog.Info("late-join (matchRoom): no snapshot, replay from frames", "playerId", playerId, "oldestFrame", oldestFrame, "replayFrom", replayFrom, "currentFrame", currentFrame)
			}

			select {
			case <-ctx.Done():
				slog.Warn("matchRoom preamble build timed out", "playerId", playerId)
				return
			default:
			}

			initData := framesync.InitData{
				FrameRate:           room.FrameRate(),
				FrameInterval:       1000 / room.FrameRate(),
				StartTime:           room.StartTime(),
				SnapshotInterval:    int32(cfg.SnapshotIntervalFrames),
				QuickReconnectMaxMs: int32(cfg.QuickReconnectMaxMs),
			}
			preamble = append(preamble, codec.NewCoreMessage(framesync.CmdStartFrameSync, framesync.EncodeInitData(&initData)))

			// KV 同步合并到此 goroutine（消除原来独立的 sleep+KV goroutine）
			if !room.DataStoreEmpty() {
				select {
				case <-ctx.Done():
					slog.Warn("matchRoom KV sync timed out", "playerId", playerId)
					return
				default:
				}
				entries, version := room.GetDataSnapshot()
				preamble = append(preamble, codec.NewExtMessage(framesync.ExtCmdPushDataSync, framesync.EncodePushDataSync(version, entries)))
			}

			// replaying=true：追帧完成前不参与实时广播，delivery loop 内部调用 SetPlayerLive 升级。
			room.AddPlayer(playerId, wrappedConn, true, replayFrom, preamble...)
			slog.Info("late-join (matchRoom) delivery loop started", "playerId", playerId, "roomId", room.ID, "frame", currentFrame, "replayFrom", replayFrom)
		}()
	} else {
		// 房间未运行：立即以 startFrame=0 启动 delivery loop，KV 同步直接发送（无需独立 goroutine）
		room.AddPlayer(playerId, wrappedConn, false, 0)
		if !room.DataStoreEmpty() {
			entries, version := room.GetDataSnapshot()
			_ = sendMsg(conn, codec.NewExtMessage(framesync.ExtCmdPushDataSync, framesync.EncodePushDataSync(version, entries)))
		}
	}

	return codec.NewExtMessage(framesync.ExtCmdMatchRoomRsp, framesync.EncodeJoinRoomRsp(playerId, room.ID, existingPlayers))
}

// ===================== 工具函数 =====================

// bindMapsToRoom 只绑定连接→玩家→房间三张映射表 + SetDelegate，不启动 delivery loop。
// AddPlayer（含 delivery loop 启动）由调用方在确定 startFrame + preamble 后单独调用。
func bindMapsToRoom(playerId int32, conn *transport.Conn, room *framesync.Room) {
	connPlayerMap.Store(conn.ID, playerId)
	playerRoomMap.Store(playerId, room)
	playerConnMap.Store(playerId, conn)
	connContextMap.Store(conn.ID, &connContext{playerId: playerId, room: room})
	room.SetDelegate(globalDelegate) // 幂等：多次 SetDelegate 安全
}

// bindPlayerToRoom 绑定映射并以 startFrame=0 启动 delivery loop（房间未运行时的快速路径）。
func bindPlayerToRoom(playerId int32, conn *transport.Conn, room *framesync.Room, replaying bool) {
	bindMapsToRoom(playerId, conn, room)
	room.AddPlayer(playerId, &statsConn{inner: &simConn{inner: conn, cfg: GlobalNetSim}, pid: playerId}, replaying, 0)
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

	room.Start()
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
func sendMsg(conn *transport.Conn, msg *codec.Message) error {
	GameStats.RecordTx(int64(len(msg.Data)))
	framesync.Metrics.BytesSent.Add(float64(len(msg.Data)))
	logMsgFromMsg("tx", msg, connPid(conn), "")
	return conn.Send(msg)
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
	room.BroadcastReliable(playerId, pushMsg)
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
	room.BroadcastReliable(playerId, codec.NewExtMessage(framesync.ExtCmdPushStateMsg, push))
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

// handleReliableMsg 处理 C→S 可靠消息（ExtCmdReliableMsg=200）
// 流程: 幂等去重（seq <= lastProcessed 直接 ACK）→ 更新 lastProcessed → 解包 inner → 内部分发 → ACK
func handleReliableMsg(conn *transport.Conn, msg *codec.Message) *codec.Message {
	seq, ok := framesync.DecodeReliableMsgSeq(msg.Data)
	if !ok {
		return nil
	}

	playerId, ok := loadConnPlayerId(conn.ID) // L1: safe type assertion
	if !ok {
		return nil
	}

	room, ok := loadPlayerRoom(playerId) // L1: safe type assertion
	if !ok {
		return nil
	}

	lastProcessed := room.GetLastProcessedC2SSeq(playerId)
	if seq <= lastProcessed {
		// 重复消息：已处理过，仅补发 ACK，不再执行
		_ = sendMsg(conn, codec.NewExtMessage(framesync.ExtCmdReliableAck, framesync.EncodeReliableAck(lastProcessed)))
		return nil
	}

	room.SetLastProcessedC2SSeq(playerId, seq)

	inner := framesync.DecodeReliableMsgInner(msg.Data)
	if inner != nil && reliableDispatch != nil {
		rsp := reliableDispatch(conn, inner)
		if rsp != nil {
			_ = sendMsg(conn, rsp)
		}
	}

	_ = sendMsg(conn, codec.NewExtMessage(framesync.ExtCmdReliableAck, framesync.EncodeReliableAck(seq)))
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
