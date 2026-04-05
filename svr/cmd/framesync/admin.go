package main

import (
	"bufio"
	"context"
	"encoding/json"
	"fmt"
	"log/slog"
	"net"
	"net/http"
	"runtime"
	"strconv"
	"strings"
	"sync/atomic"
	"time"

	"github.com/boomlulu/boomnetwork/codec"
	"github.com/boomlulu/boomnetwork/framesync"
	"github.com/boomlulu/boomnetwork/transport"
)

var serverStartTime = time.Now()

// gmHub 全局 GM WebSocket Hub，供 main.go 消息处理器推送 desync 事件
var gmHub *GMHub

// totalConnEver 累计连接玩家数（atomic，SessionBind 时递增）
var totalConnEver int64

// startAdminServer 启动 Admin HTTP 服务
//
// 路由：
//
//	GET  /health           健康检查（不鉴权）
//	GET  /stats            流量统计（Game + GM）
//	GET  /messages         最近 N 条网络消息
//	GET  /rooms            房间列表 + 玩家详情
//	POST /kick/{pid}       踢出玩家
//	POST /rooms/stop/{id}  强停房间
func startAdminServer(ctx context.Context, addr, token string) {
	mux := http.NewServeMux()

	// /health 不鉴权（健康检查探针需要无障碍访问）
	mux.HandleFunc("/health", handleHealth)

	// 其余端点走鉴权
	mux.HandleFunc("/stats", withAuth(token, handleStats))
	mux.HandleFunc("/messages", withAuth(token, handleMessages))
	mux.HandleFunc("/rooms/inspect/", withAuth(token, handleRoomInspect))
	mux.HandleFunc("/rooms", withAuth(token, handleRooms))
	mux.HandleFunc("/rooms/stop/", withAuth(token, handleStopRoom))
	mux.HandleFunc("/rooms/kill/", withAuth(token, handleAdminKillRoom))
	mux.HandleFunc("/rooms/create", withAuth(token, handleAdminCreateRoom))
	mux.HandleFunc("/kick/", withAuth(token, handleKick))
	mux.HandleFunc("/players/", withAuth(token, handlePlayerDetail))
	mux.HandleFunc("/perf", withAuth(token, handlePerf))
	mux.HandleFunc("/rates", withAuth(token, handleRates))
	mux.HandleFunc("/netsim", withAuth(token, handleNetSim))
	mux.HandleFunc("/log-level", withAuth(token, handleLogLevel))
	mux.HandleFunc("/config/reload", withAuth(token, handleConfigReload))
	mux.HandleFunc("/logs", withAuth(token, handleLogs))

	// 新增端点
	mux.HandleFunc("/rooms/stop-all", withAuth(token, handleStopAll))
	mux.HandleFunc("/broadcast", withAuth(token, handleBroadcast))
	mux.HandleFunc("/rooms/replay/", withAuth(token, handleRoomReplay))

	// WebSocket GM 长连接
	gmHub = newGMHub(ctx)
	go gmHub.Run()
	mux.HandleFunc("/ws", gmHub.HandleUpgrade(token))

	handler := gmTrafficMiddleware(mux)

	srv := &http.Server{Addr: addr, Handler: handler}

	// 优雅关闭
	go func() {
		<-ctx.Done()
		shutCtx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		srv.Shutdown(shutCtx)
		gmHub.Stop()
	}()

	slog.Info("admin listening", "addr", addr)
	if token != "" {
		slog.Info("admin auth enabled")
	}
	if err := srv.ListenAndServe(); err != http.ErrServerClosed {
		slog.Error("admin server failed", "error", err)
	}
}

// ===================== Auth Middleware (G4) =====================

func withAuth(token string, next http.HandlerFunc) http.HandlerFunc {
	if token == "" {
		return next // 不鉴权
	}
	return func(w http.ResponseWriter, r *http.Request) {
		auth := r.Header.Get("Authorization")
		if auth != "Bearer "+token {
			jsonError(w, http.StatusUnauthorized, "invalid or missing token")
			return
		}
		next(w, r)
	}
}

// ===================== GM Traffic Middleware =====================

type countingWriter struct {
	http.ResponseWriter
	bytes int64
}

func (cw *countingWriter) Write(b []byte) (int, error) {
	n, err := cw.ResponseWriter.Write(b)
	cw.bytes += int64(n)
	return n, err
}

// Hijack 透传给底层 ResponseWriter，让 WebSocket Upgrade 可以接管 TCP 连接
func (cw *countingWriter) Hijack() (net.Conn, *bufio.ReadWriter, error) {
	if h, ok := cw.ResponseWriter.(http.Hijacker); ok {
		return h.Hijack()
	}
	return nil, nil, fmt.Errorf("underlying ResponseWriter does not implement http.Hijacker")
}

func gmTrafficMiddleware(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		GmStats.RecordRx(int64(len(r.URL.String()) + 200))
		cw := &countingWriter{ResponseWriter: w}
		next.ServeHTTP(cw, r)
		GmStats.RecordTx(cw.bytes)
	})
}

// ===================== GET /health =====================

func handleHealth(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	rooms := roomMgr.RoomCount()
	players := countOnlinePlayers()
	uptime := time.Since(serverStartTime).Truncate(time.Second).String()

	w.Header().Set("Content-Type", "application/json")
	fmt.Fprintf(w, `{"status":"ok","rooms":%d,"players":%d,"uptime":%q,"buildHash":%q,"buildTime":%q,"goVersion":%q}`,
		rooms, players, uptime, BuildHash, BuildTime, runtime.Version())
}

// ===================== GET /stats =====================

// matchKeyStats 按 matchKey 汇总的房间/玩家数
type matchKeyStats struct {
	Rooms   int `json:"rooms"`
	Players int `json:"players"`
}

func handleStats(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	g := GameStats.Snapshot()
	m := GmStats.Snapshot()

	// 按 matchKey 分组统计
	byMK := make(map[string]matchKeyStats)
	infos := roomMgr.GetAllRoomInfos()
	for _, info := range infos {
		room := roomMgr.GetRoom(info.RoomId)
		if room == nil {
			continue
		}
		mk := room.MatchKey
		if mk == "" {
			mk = "(default)"
		}
		s := byMK[mk]
		s.Rooms++
		s.Players += room.PlayerCount()
		byMK[mk] = s
	}
	byMKJSON, _ := json.Marshal(byMK)

	var mem runtime.MemStats
	runtime.ReadMemStats(&mem)

	type statsResp struct {
		// 流量
		GameRxTotal int64 `json:"game_rx_total"`
		GameTxTotal int64 `json:"game_tx_total"`
		GameRx1Min  int64 `json:"game_rx_1min"`
		GameTx1Min  int64 `json:"game_tx_1min"`
		GameRx5Sec  int64 `json:"game_rx_5sec"`
		GameTx5Sec  int64 `json:"game_tx_5sec"`
		GmRxTotal   int64 `json:"gm_rx_total"`
		GmTxTotal   int64 `json:"gm_tx_total"`
		GmRx1Min    int64 `json:"gm_rx_1min"`
		GmTx1Min    int64 `json:"gm_tx_1min"`
		GmRx5Sec    int64 `json:"gm_rx_5sec"`
		GmTx5Sec    int64 `json:"gm_tx_5sec"`
		// 连接
		ActiveConnections int   `json:"active_connections"` // 当前在线玩家数
		TotalConnections  int64 `json:"total_connections"`  // 累计连接总数（服务器启动以来）
		// 运行时
		Goroutines int     `json:"goroutines"`
		HeapMB     float64 `json:"heap_mb"`
		Uptime     string  `json:"uptime"`
		// 房间
		TotalRooms int `json:"total_rooms"`
	}

	resp := statsResp{
		GameRxTotal: g.RxTotal, GameTxTotal: g.TxTotal,
		GameRx1Min: g.Rx1Min, GameTx1Min: g.Tx1Min,
		GameRx5Sec: g.Rx5Sec, GameTx5Sec: g.Tx5Sec,
		GmRxTotal: m.RxTotal, GmTxTotal: m.TxTotal,
		GmRx1Min: m.Rx1Min, GmTx1Min: m.Tx1Min,
		GmRx5Sec: m.Rx5Sec, GmTx5Sec: m.Tx5Sec,
		ActiveConnections: countOnlinePlayers(),
		TotalConnections:  atomic.LoadInt64(&totalConnEver),
		Goroutines:        runtime.NumGoroutine(),
		HeapMB:            float64(mem.HeapAlloc) / (1024 * 1024),
		Uptime:            time.Since(serverStartTime).Truncate(time.Second).String(),
		TotalRooms:        roomMgr.RoomCount(),
	}

	respJSON, _ := json.Marshal(resp)

	// 拼入 by_match_key（避免嵌套 struct 带来的额外 alloc）
	w.Header().Set("Content-Type", "application/json")
	w.Write(respJSON[:len(respJSON)-1]) // 去掉结尾 }
	w.Write([]byte(`,"by_match_key":`))
	w.Write(byMKJSON)
	w.Write([]byte("}"))
}

// ===================== GET /messages =====================

func handleMessages(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	limit := 100
	if v := r.URL.Query().Get("limit"); v != "" {
		if n, err := strconv.Atoi(v); err == nil && n > 0 && n <= 500 {
			limit = n
		}
	}
	msgs := MsgLog.Recent(limit)
	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(msgs)
}

// ===================== GET /rooms (G1) =====================

type roomDetail struct {
	ID           int32                  `json:"id"`
	Running      bool                   `json:"running"`
	Paused       bool                   `json:"paused"`
	FrameNumber  uint32                 `json:"frame_number"`
	FrameRate    int32                  `json:"frame_rate"`
	MaxPlayers   int                    `json:"max_players"`
	OnlineCount  int                    `json:"online_count"`
	TotalPlayers int                    `json:"total_players"`
	MatchKey     string                 `json:"match_key,omitempty"`
	Players      []framesync.PlayerInfo `json:"players"`
}

func handleRooms(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}

	infos := roomMgr.GetAllRoomInfos()
	rooms := make([]roomDetail, 0, len(infos))

	for _, info := range infos {
		room := roomMgr.GetRoom(info.RoomId)
		if room == nil {
			continue
		}
		detail := roomDetail{
			ID:           room.ID,
			Running:      room.IsRunning(),
			Paused:       room.IsSnapshotPaused(),
			FrameNumber:  room.CurrentFrameNumber(),
			FrameRate:    room.FrameRate(),
			MaxPlayers:   room.MaxPlayers(),
			OnlineCount:  room.PlayerCount(),
			TotalPlayers: room.TotalPlayerCount(),
			MatchKey:     room.MatchKey,
		}
		room.ForEachPlayer(func(p framesync.PlayerInfo) {
			detail.Players = append(detail.Players, p)
		})
		rooms = append(rooms, detail)
	}

	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(rooms)
}

// ===================== GET /rooms/inspect/{id} =====================

func handleRoomInspect(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}

	idStr := strings.TrimPrefix(r.URL.Path, "/rooms/inspect/")
	id, err := strconv.Atoi(idStr)
	if err != nil || id <= 0 {
		jsonError(w, http.StatusBadRequest, "invalid room id")
		return
	}

	room := roomMgr.GetRoom(int32(id))
	if room == nil {
		jsonError(w, http.StatusNotFound, fmt.Sprintf("room %d not found", id))
		return
	}

	result := buildRoomInspect(room)
	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(result)
}

// ===================== POST /kick/{pid} (G2) =====================

func handleKick(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}

	// 从 URL 解析 playerId: /kick/123
	pidStr := strings.TrimPrefix(r.URL.Path, "/kick/")
	pid, err := strconv.Atoi(pidStr)
	if err != nil || pid <= 0 {
		jsonError(w, http.StatusBadRequest, "invalid player id")
		return
	}
	playerId := int32(pid)

	// 找到玩家所在房间
	roomVal, ok := playerRoomMap.Load(playerId)
	if !ok {
		jsonError(w, http.StatusNotFound, fmt.Sprintf("player %d not in any room", playerId))
		return
	}
	room := roomVal.(*framesync.Room)

	// 从房间移除
	room.RemovePlayer(playerId)
	playerRoomMap.Delete(playerId)

	// 断开连接（先通知再关闭）
	if connVal, ok := playerConnMap.LoadAndDelete(playerId); ok {
		conn := connVal.(*transport.Conn)
		conn.Send(codec.NewCoreMessage(framesync.CmdKicked, []byte{framesync.KickReasonAdmin}))
		conn.Close()
	}

	// 通知同房其他玩家
	if room.IsRunning() {
		room.EnqueueEvent(framesync.FrameEventPlayerLeft, playerId)
	} else {
		broadcastToRoom(room, playerId, codec.NewExtMessage(framesync.ExtCmdPlayerLeft, framesync.EncodePlayerId(playerId)))
	}

	slog.Warn("admin kicked player", "player_id", playerId, "room_id", room.ID)

	w.Header().Set("Content-Type", "application/json")
	fmt.Fprintf(w, `{"ok":true,"kicked":%d,"room":%d}`, playerId, room.ID)
}

// ===================== POST /rooms/stop/{id} (G3) =====================

func handleStopRoom(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}

	// 从 URL 解析 roomId: /rooms/stop/123
	idStr := strings.TrimPrefix(r.URL.Path, "/rooms/stop/")
	id, err := strconv.Atoi(idStr)
	if err != nil || id <= 0 {
		jsonError(w, http.StatusBadRequest, "invalid room id")
		return
	}
	roomId := int32(id)

	room := roomMgr.GetRoom(roomId)
	if room == nil {
		jsonError(w, http.StatusNotFound, fmt.Sprintf("room %d not found", roomId))
		return
	}

	// 停止帧同步
	room.Stop()

	// 清理所有玩家的房间映射
	room.ForEachPlayer(func(p framesync.PlayerInfo) {
		playerRoomMap.Delete(p.ID)
	})

	// 从管理器移除
	roomMgr.RemoveRoom(roomId)

	slog.Info("admin stopped room", "room_id", roomId)

	w.Header().Set("Content-Type", "application/json")
	fmt.Fprintf(w, `{"ok":true,"stopped":%d}`, roomId)
}

// ===================== POST /rooms/kill/{id} =====================

func handleAdminKillRoom(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}

	idStr := strings.TrimPrefix(r.URL.Path, "/rooms/kill/")
	id, err := strconv.Atoi(idStr)
	if err != nil || id <= 0 {
		jsonError(w, http.StatusBadRequest, "invalid room id")
		return
	}
	roomId := int32(id)

	room := roomMgr.GetRoom(roomId)
	if room == nil {
		jsonError(w, http.StatusNotFound, fmt.Sprintf("room %d not found", roomId))
		return
	}

	// 强制销毁：关闭所有玩家连接，跳过优雅广播
	room.ForEachPlayer(func(p framesync.PlayerInfo) {
		playerRoomMap.Delete(p.ID)
		if connVal, ok := playerConnMap.LoadAndDelete(p.ID); ok {
			connVal.(*transport.Conn).Close()
		}
	})
	room.Stop()
	roomMgr.RemoveRoom(roomId)

	slog.Info("admin killed room", "room_id", roomId)

	w.Header().Set("Content-Type", "application/json")
	fmt.Fprintf(w, `{"ok":true,"killed":%d}`, roomId)
}

// ===================== POST /rooms/create =====================

func handleAdminCreateRoom(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}

	maxPlayers := 2
	if v := r.URL.Query().Get("max_players"); v != "" {
		if n, err := strconv.Atoi(v); err == nil && n > 0 {
			maxPlayers = n
		}
	}
	matchKey := r.URL.Query().Get("match_key")

	room := roomMgr.CreateRoomWithMaxPlayers(maxPlayers, matchKey)
	if room == nil {
		jsonError(w, http.StatusConflict, "max rooms reached")
		return
	}
	room.MatchKey = matchKey

	slog.Info("admin created room", "room_id", room.ID, "max_players", maxPlayers, "match_key", matchKey)

	w.Header().Set("Content-Type", "application/json")
	fmt.Fprintf(w, `{"ok":true,"room_id":%d}`, room.ID)
}

// ===================== Helpers =====================

func countOnlinePlayers() int {
	var count int64
	connPlayerMap.Range(func(_, _ any) bool {
		atomic.AddInt64(&count, 1)
		return true
	})
	return int(count)
}

// ===================== GET /players/{pid} (G5) =====================

func handlePlayerDetail(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}

	pidStr := strings.TrimPrefix(r.URL.Path, "/players/")
	pid, err := strconv.Atoi(pidStr)
	if err != nil || pid <= 0 {
		jsonError(w, http.StatusBadRequest, "invalid player id")
		return
	}
	playerId := int32(pid)

	// 基本信息
	detail := map[string]interface{}{
		"id":     playerId,
		"online": false,
		"room":   0,
	}

	// 是否有连接
	if _, ok := playerConnMap.Load(playerId); ok {
		detail["online"] = true
	}

	// 所在房间
	if roomVal, ok := playerRoomMap.Load(playerId); ok {
		room := roomVal.(*framesync.Room)
		detail["room"] = room.ID
		detail["room_running"] = room.IsRunning()
		detail["room_frame"] = room.CurrentFrameNumber()

		// 查找该玩家在房间里的状态
		room.ForEachPlayer(func(p framesync.PlayerInfo) {
			if p.ID == playerId {
				detail["state"] = p.State
				if p.DisconnectTime > 0 {
					detail["disconnect_time"] = p.DisconnectTime
				}
			}
		})
	}

	// 最近该玩家的消息（从 MsgLog 筛选）
	recentMsgs := MsgLog.Recent(100)
	playerMsgs := make([]MsgEntry, 0)
	for _, m := range recentMsgs {
		if m.Pid == playerId {
			playerMsgs = append(playerMsgs, m)
			if len(playerMsgs) >= 20 {
				break
			}
		}
	}
	detail["recent_messages"] = playerMsgs

	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(detail)
}

// ===================== GET /perf (G6) =====================

// 缓存 ReadMemStats 结果，避免每次请求都触发 STW
var (
	perfCachedJSON []byte
	perfCacheTime  int64 // unix seconds
)

const perfCacheTTL = 5 // 秒

func handlePerf(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}

	now := time.Now().Unix()
	if atomic.LoadInt64(&perfCacheTime)+perfCacheTTL > now && perfCachedJSON != nil {
		w.Header().Set("Content-Type", "application/json")
		w.Write(perfCachedJSON)
		return
	}

	var memStats runtime.MemStats
	runtime.ReadMemStats(&memStats)

	json := fmt.Appendf(nil,
		`{"goroutines":%d,"heap_mb":%.2f,"sys_mb":%.2f,"gc_count":%d,"gc_pause_us":%d,"rooms":%d,"players":%d}`,
		runtime.NumGoroutine(),
		float64(memStats.HeapAlloc)/(1024*1024),
		float64(memStats.Sys)/(1024*1024),
		memStats.NumGC,
		memStats.PauseNs[(memStats.NumGC+255)%256]/1000,
		roomMgr.RoomCount(),
		countOnlinePlayers(),
	)

	perfCachedJSON = json
	atomic.StoreInt64(&perfCacheTime, now)

	w.Header().Set("Content-Type", "application/json")
	w.Write(json)
}

// ===================== GET /rates (G8) =====================

func handleRates(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	top := PlayerRates.TopPlayers(20)
	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(top)
}

// ===================== GET/POST /netsim (网络模拟) =====================

func handleNetSim(w http.ResponseWriter, r *http.Request) {
	switch r.Method {
	case http.MethodGet:
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprintf(w,
			`{"enabled":%v,"latency_ms":%d,"jitter_ms":%d,"loss_percent":%d,"stats_dropped":%d,"stats_delayed":%d}`,
			GlobalNetSim.IsEnabled(),
			atomic.LoadInt32(&GlobalNetSim.LatencyMs),
			atomic.LoadInt32(&GlobalNetSim.JitterMs),
			atomic.LoadInt32(&GlobalNetSim.LossPercent),
			atomic.LoadInt64(&simDropped),
			atomic.LoadInt64(&simDelayed),
		)
	case http.MethodPost:
		var req struct {
			Enabled     *bool `json:"enabled"`
			LatencyMs   *int  `json:"latency_ms"`
			JitterMs    *int  `json:"jitter_ms"`
			LossPercent *int  `json:"loss_percent"`
		}
		if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
			jsonError(w, http.StatusBadRequest, err.Error())
			return
		}
		if req.Enabled != nil {
			GlobalNetSim.SetEnabled(*req.Enabled)
		}
		if req.LatencyMs != nil {
			atomic.StoreInt32(&GlobalNetSim.LatencyMs, int32(*req.LatencyMs))
		}
		if req.JitterMs != nil {
			atomic.StoreInt32(&GlobalNetSim.JitterMs, int32(*req.JitterMs))
		}
		if req.LossPercent != nil {
			atomic.StoreInt32(&GlobalNetSim.LossPercent, int32(*req.LossPercent))
		}
		slog.Info("admin netsim updated",
			"enabled", GlobalNetSim.IsEnabled(),
			"latency_ms", atomic.LoadInt32(&GlobalNetSim.LatencyMs),
			"jitter_ms", atomic.LoadInt32(&GlobalNetSim.JitterMs),
			"loss_percent", atomic.LoadInt32(&GlobalNetSim.LossPercent))
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprintf(w, `{"ok":true}`)
	default:
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
	}
}

// ===================== GET/POST /log-level (S8) =====================

func handleLogLevel(w http.ResponseWriter, r *http.Request) {
	switch r.Method {
	case http.MethodGet:
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprintf(w, `{"level":%q}`, logLevel.Level().String())
	case http.MethodPost:
		var body struct {
			Level string `json:"level"`
		}
		if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
			http.Error(w, "bad json", http.StatusBadRequest)
			return
		}
		var lvl slog.Level
		if err := lvl.UnmarshalText([]byte(body.Level)); err != nil {
			http.Error(w, "invalid level (use DEBUG/INFO/WARN/ERROR)", http.StatusBadRequest)
			return
		}
		logLevel.Set(lvl)
		slog.Info("log level changed", "level", lvl.String())
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprintf(w, `{"level":%q}`, lvl.String())
	default:
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
	}
}

// ===================== POST /config/reload (S12) =====================

func handleConfigReload(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	reloadConfig()
	w.Header().Set("Content-Type", "application/json")
	fmt.Fprintf(w, `{"ok":true,"level":%q}`, logLevel.Level().String())
}

// handleLogs GET /logs?lines=100&level=warn — 返回最近 N 条日志
func handleLogs(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	n := 100
	if v := r.URL.Query().Get("lines"); v != "" {
		if parsed, err := strconv.Atoi(v); err == nil && parsed > 0 {
			n = parsed
			if n > 2000 {
				n = 2000
			}
		}
	}
	level := strings.ToUpper(r.URL.Query().Get("level"))
	if LogBuf == nil {
		w.Header().Set("Content-Type", "application/json")
		w.Write([]byte("[]"))
		return
	}
	entries := LogBuf.Recent(n, level)
	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(entries)
}

// ===================== POST /rooms/stop-all =====================

func handleStopAll(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}

	infos := roomMgr.GetAllRoomInfos()
	stopped := 0
	for _, info := range infos {
		room := roomMgr.GetRoom(info.RoomId)
		if room == nil {
			continue
		}
		room.Stop()
		room.ForEachPlayer(func(p framesync.PlayerInfo) {
			playerRoomMap.Delete(p.ID)
		})
		roomMgr.RemoveRoom(info.RoomId)
		stopped++
	}

	slog.Info("admin stop-all rooms", "stopped", stopped)
	w.Header().Set("Content-Type", "application/json")
	fmt.Fprintf(w, `{"ok":true,"stopped":%d}`, stopped)
}

// ===================== POST /broadcast =====================

func handleBroadcast(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}

	var req struct {
		Message string `json:"message"`
	}
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil || req.Message == "" {
		jsonError(w, http.StatusBadRequest, "missing or invalid message")
		return
	}

	// 将公告写入 KV 存储 key=0（广播通道），玩家通过 OnDataChanged 接收
	msgBytes := []byte(req.Message)
	sent := 0
	infos := roomMgr.GetAllRoomInfos()
	for _, info := range infos {
		room := roomMgr.GetRoom(info.RoomId)
		if room == nil {
			continue
		}
		room.SetData(0, 0, msgBytes) // playerId=0 表示服务器, key=0 = broadcast channel
		sent += room.PlayerCount()
	}

	slog.Info("admin broadcast", "message", req.Message, "reached_players", sent)
	w.Header().Set("Content-Type", "application/json")
	fmt.Fprintf(w, `{"ok":true,"sent":%d}`, sent)
}

// ===================== GET /rooms/replay/{id} =====================

func handleRoomReplay(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}

	idStr := strings.TrimPrefix(r.URL.Path, "/rooms/replay/")
	id, err := strconv.Atoi(idStr)
	if err != nil || id <= 0 {
		jsonError(w, http.StatusBadRequest, "invalid room id")
		return
	}

	room := roomMgr.GetRoom(int32(id))
	if room == nil {
		jsonError(w, http.StatusNotFound, fmt.Sprintf("room %d not found", id))
		return
	}

	afterFrame := uint32(0)
	if v := r.URL.Query().Get("after_frame"); v != "" {
		if n, err2 := strconv.ParseUint(v, 10, 32); err2 == nil {
			afterFrame = uint32(n)
		}
	}

	frames := room.GetFramesSince(afterFrame)

	type replayFrame struct {
		FrameNumber uint32 `json:"frame_number"`
		Data        []byte `json:"data"` // JSON base64-encodes []byte automatically
	}
	result := make([]replayFrame, 0, len(frames))
	for _, f := range frames {
		result = append(result, replayFrame{FrameNumber: f.FrameNumber, Data: f.EncodedData})
	}

	slog.Info("admin replay export", "room_id", id, "frames", len(result))
	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(result)
}

func jsonError(w http.ResponseWriter, code int, msg string) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(code)
	b, _ := json.Marshal(map[string]string{"error": msg})
	w.Write(b)
}
