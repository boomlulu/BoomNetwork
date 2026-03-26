package main

import (
	"context"
	"log/slog"
	"net/http"
	"runtime"
	"sync"
	"sync/atomic"
	"time"

	"github.com/boom/boomnetwork/codec"
	"github.com/boom/boomnetwork/framesync"
	"github.com/boom/boomnetwork/transport"
	"github.com/gorilla/websocket"
	"github.com/vmihailenco/msgpack/v5"
)

// ===================== GMHub — 管理所有 WS 连接 =====================

type GMHub struct {
	mu    sync.RWMutex
	conns map[*GMConn]struct{}

	msgNotify chan MsgEntry // 来自 MsgLog 的实时消息通知

	ctx    context.Context
	cancel context.CancelFunc
}

func newGMHub(parent context.Context) *GMHub {
	ctx, cancel := context.WithCancel(parent)
	return &GMHub{
		conns:     make(map[*GMConn]struct{}),
		msgNotify: make(chan MsgEntry, 256),
		ctx:       ctx,
		cancel:    cancel,
	}
}

func (h *GMHub) addConn(c *GMConn) {
	h.mu.Lock()
	h.conns[c] = struct{}{}
	h.mu.Unlock()
	slog.Info("gm-ws client connected", "total", h.connCount())
}

func (h *GMHub) removeConn(c *GMConn) {
	h.mu.Lock()
	delete(h.conns, c)
	h.mu.Unlock()
	slog.Info("gm-ws client disconnected", "total", h.connCount())
}

func (h *GMHub) connCount() int {
	h.mu.RLock()
	n := len(h.conns)
	h.mu.RUnlock()
	return n
}

// broadcast 向所有订阅了指定 topic bit 的连接推送数据
func (h *GMHub) broadcast(bit uint32, data []byte) {
	h.mu.RLock()
	defer h.mu.RUnlock()
	for c := range h.conns {
		if atomic.LoadUint32(&c.topics)&bit != 0 {
			select {
			case c.sendCh <- data:
			default:
				// sendCh 满了，跳过（慢客户端）
			}
		}
	}
}

// Run 启动 Hub 的后台 goroutine（定时推送 + 实时消息监听）
func (h *GMHub) Run() {
	// 注册 MsgLog 通知通道
	MsgLog.SetNotifyCh(h.msgNotify)

	ticker2s := time.NewTicker(2 * time.Second)
	ticker5s := time.NewTicker(5 * time.Second)
	defer ticker2s.Stop()
	defer ticker5s.Stop()

	for {
		select {
		case <-h.ctx.Done():
			MsgLog.SetNotifyCh(nil)
			return

		case entry := <-h.msgNotify:
			// 实时消息推送
			data := makePushEnvelope(TopicMessages, MsgEntryToWire(entry))
			h.broadcast(BitMessages, data)

		case <-ticker2s.C:
			h.pushHealth()
			h.pushStats()
			h.pushRooms()

		case <-ticker5s.C:
			h.pushPerf()
			h.pushRates()
			h.pushNetsim()
		}
	}
}

func (h *GMHub) Stop() {
	h.cancel()
	// 关闭所有连接
	h.mu.RLock()
	for c := range h.conns {
		c.ws.Close()
	}
	h.mu.RUnlock()
}

// ===================== 定时推送数据采集 =====================

func (h *GMHub) pushHealth() {
	data := makePushEnvelope(TopicHealth, HealthPush{
		Status:  "ok",
		Rooms:   roomMgr.RoomCount(),
		Players: countOnlinePlayers(),
		Uptime:  time.Since(serverStartTime).Truncate(time.Second).String(),
	})
	h.broadcast(BitHealth, data)
}

func (h *GMHub) pushStats() {
	g := GameStats.Snapshot()
	m := GmStats.Snapshot()
	data := makePushEnvelope(TopicStats, StatsPush{
		GameRxTotal: g.RxTotal, GameTxTotal: g.TxTotal,
		GameRx1Min: g.Rx1Min, GameTx1Min: g.Tx1Min,
		GameRx5Sec: g.Rx5Sec, GameTx5Sec: g.Tx5Sec,
		GmRxTotal: m.RxTotal, GmTxTotal: m.TxTotal,
		GmRx1Min: m.Rx1Min, GmTx1Min: m.Tx1Min,
		GmRx5Sec: m.Rx5Sec, GmTx5Sec: m.Tx5Sec,
	})
	h.broadcast(BitStats, data)
}

func (h *GMHub) pushRooms() {
	infos := roomMgr.GetAllRoomInfos()
	rooms := make([]RoomDetailWire, 0, len(infos))
	for _, info := range infos {
		room := roomMgr.GetRoom(info.RoomId)
		if room == nil {
			continue
		}
		detail := RoomDetailWire{
			ID:           room.ID,
			Running:      room.IsRunning(),
			Paused:       room.IsSnapshotPaused(),
			FrameNumber:  room.CurrentFrameNumber(),
			FrameRate:    room.FrameRate(),
			MaxPlayers:   room.MaxPlayers(),
			OnlineCount:  room.PlayerCount(),
			TotalPlayers: room.TotalPlayerCount(),
		}
		room.ForEachPlayer(func(p framesync.PlayerInfo) {
			detail.Players = append(detail.Players, PlayerInfoWire{ID: p.ID, State: int(p.State)})
		})
		rooms = append(rooms, detail)
	}
	data := makePushEnvelope(TopicRooms, rooms)
	h.broadcast(BitRooms, data)
}

func (h *GMHub) pushPerf() {
	var mem runtime.MemStats
	runtime.ReadMemStats(&mem)
	data := makePushEnvelope(TopicPerf, PerfPush{
		Goroutines: runtime.NumGoroutine(),
		HeapMB:     float64(mem.HeapAlloc) / (1024 * 1024),
		SysMB:      float64(mem.Sys) / (1024 * 1024),
		GCCount:    mem.NumGC,
		GCPauseUs:  mem.PauseNs[(mem.NumGC+255)%256] / 1000,
		Rooms:      roomMgr.RoomCount(),
		Players:    countOnlinePlayers(),
	})
	h.broadcast(BitPerf, data)
}

func (h *GMHub) pushRates() {
	top := PlayerRates.TopPlayers(20)
	items := make([]PlayerRateWire, len(top))
	for i, t := range top {
		items[i] = PlayerRateWire{Pid: t.Pid, MsgPer5Sec: t.MsgPer5Sec}
	}
	data := makePushEnvelope(TopicRates, RatesPush{Top: items})
	h.broadcast(BitRates, data)
}

func (h *GMHub) pushNetsim() {
	data := makePushEnvelope(TopicNetsim, NetsimPush{
		Enabled:      GlobalNetSim.IsEnabled(),
		LatencyMs:    atomic.LoadInt32(&GlobalNetSim.LatencyMs),
		JitterMs:     atomic.LoadInt32(&GlobalNetSim.JitterMs),
		LossPercent:  atomic.LoadInt32(&GlobalNetSim.LossPercent),
		StatsDropped: atomic.LoadInt64(&simDropped),
		StatsDelayed: atomic.LoadInt64(&simDelayed),
	})
	h.broadcast(BitNetsim, data)
}

// ===================== WebSocket Upgrade =====================

// wsAllowedOrigins S17: Origin 白名单，由 main.go 在启动时注入。
// 空列表 = 允许所有 origin（向后兼容）。
var wsAllowedOrigins []string

var wsUpgrader = websocket.Upgrader{
	ReadBufferSize:  4096,
	WriteBufferSize: 4096,
	CheckOrigin: func(r *http.Request) bool {
		// S17: Origin 白名单检查
		if len(wsAllowedOrigins) == 0 {
			return true // 向后兼容：未配置白名单时允许所有
		}
		origin := r.Header.Get("Origin")
		for _, allowed := range wsAllowedOrigins {
			if origin == allowed {
				return true
			}
		}
		slog.Warn("gm-ws origin rejected", "origin", origin)
		return false
	},
}

func (h *GMHub) HandleUpgrade(token string) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		ws, err := wsUpgrader.Upgrade(w, r, nil)
		if err != nil {
			slog.Error("gm-ws upgrade failed", "error", err)
			return
		}

		c := &GMConn{
			ws:     ws,
			hub:    h,
			sendCh: make(chan []byte, 64),
			doneCh: make(chan struct{}),
		}
		go c.readPump(token)
		go c.writePump()
	}
}

// ===================== GMConn — 单个 WS 连接 =====================

type GMConn struct {
	ws     *websocket.Conn
	hub    *GMHub
	topics uint32       // 订阅位掩码 (atomic)
	sendCh chan []byte   // 出站缓冲
	doneCh chan struct{} // 关闭信号
	once   sync.Once
}

func (c *GMConn) close() {
	c.once.Do(func() {
		close(c.doneCh)
		c.hub.removeConn(c)
		c.ws.Close()
	})
}

// readPump 读循环：处理 auth → sub/unsub/rpc/ping
func (c *GMConn) readPump(token string) {
	defer c.close()

	// 设置最大消息大小
	c.ws.SetReadLimit(65536)

	// ===== 认证阶段（5 秒超时）=====
	c.ws.SetReadDeadline(time.Now().Add(5 * time.Second))
	_, data, err := c.ws.ReadMessage()
	if err != nil {
		return
	}

	env, err := decodeEnvelope(data)
	if err != nil || env.Type != "auth" {
		c.sendError("", "", "expected auth message")
		return
	}

	// 验证 token
	if token != "" {
		var auth AuthPayload
		if err := msgpack.Unmarshal(env.Payload, &auth); err != nil || auth.Token != token {
			c.sendEnvelope(&GMEnvelope{Type: "auth_err", Payload: mustEncodePayload(ErrorResult{Error: "invalid token"})})
			return
		}
	}

	// 认证成功
	c.sendEnvelope(&GMEnvelope{Type: "auth_ok"})
	c.hub.addConn(c)

	// 重置读超时（90 秒心跳超时）
	c.ws.SetReadDeadline(time.Now().Add(90 * time.Second))
	c.ws.SetPongHandler(func(string) error {
		c.ws.SetReadDeadline(time.Now().Add(90 * time.Second))
		return nil
	})

	// ===== 主循环 =====
	for {
		_, data, err := c.ws.ReadMessage()
		if err != nil {
			return
		}
		GmStats.RecordRx(int64(len(data)))

		// 每次收到消息都重置读超时
		c.ws.SetReadDeadline(time.Now().Add(90 * time.Second))

		env, err := decodeEnvelope(data)
		if err != nil {
			c.sendError("", "", "decode error")
			return
		}

		switch env.Type {
		case "sub":
			if bit, ok := topicToBit[env.Topic]; ok {
				atomic.OrUint32(&c.topics, bit)
				// 回填历史消息，让 Messages Tab 订阅后立即有数据
				if env.Topic == TopicMessages {
					recent := MsgLog.Recent(100)
					// Recent 返回逆序（最新在前），反转为时间正序推送
					for i := len(recent) - 1; i >= 0; i-- {
						data := makePushEnvelope(TopicMessages, MsgEntryToWire(recent[i]))
						select {
						case c.sendCh <- data:
						default:
						}
					}
				}
			}
		case "unsub":
			if bit, ok := topicToBit[env.Topic]; ok {
				atomic.AndUint32(&c.topics, ^bit)
			}
		case "rpc":
			c.handleRPC(env)
		case "ping":
			c.sendEnvelope(&GMEnvelope{Type: "pong"})
		}
	}
}

// writePump 写循环：发送 sendCh 中的数据 + 定时 ping
func (c *GMConn) writePump() {
	pingTicker := time.NewTicker(45 * time.Second)
	defer pingTicker.Stop()
	defer c.close()

	for {
		select {
		case data, ok := <-c.sendCh:
			if !ok {
				return
			}
			GmStats.RecordTx(int64(len(data)))
			c.ws.SetWriteDeadline(time.Now().Add(10 * time.Second))
			if err := c.ws.WriteMessage(websocket.BinaryMessage, data); err != nil {
				return
			}
			// 批量刷出队列中的其他消息
			n := len(c.sendCh)
			for i := 0; i < n; i++ {
				data = <-c.sendCh
				GmStats.RecordTx(int64(len(data)))
				if err := c.ws.WriteMessage(websocket.BinaryMessage, data); err != nil {
					return
				}
			}

		case <-pingTicker.C:
			c.ws.SetWriteDeadline(time.Now().Add(10 * time.Second))
			if err := c.ws.WriteMessage(websocket.PingMessage, nil); err != nil {
				return
			}

		case <-c.doneCh:
			return
		}
	}
}

// ===================== RPC 处理 =====================

func (c *GMConn) handleRPC(env *GMEnvelope) {
	switch env.Topic {
	case "kick":
		c.rpcKick(env)
	case "stop_room":
		c.rpcStopRoom(env)
	case "kill_room":
		c.rpcKillRoom(env)
	case "create_room":
		c.rpcCreateRoom(env)
	case "netsim":
		c.rpcNetsim(env)
	default:
		c.sendError(env.ID, env.Topic, "unknown rpc topic")
	}
}

func (c *GMConn) rpcKick(env *GMEnvelope) {
	var p KickPayload
	if err := msgpack.Unmarshal(env.Payload, &p); err != nil || p.Pid <= 0 {
		c.sendError(env.ID, "kick", "invalid pid")
		return
	}

	roomVal, ok := playerRoomMap.Load(p.Pid)
	if !ok {
		c.sendError(env.ID, "kick", "player not in any room")
		return
	}
	room := roomVal.(*framesync.Room)

	room.RemovePlayer(p.Pid)
	playerRoomMap.Delete(p.Pid)
	if connVal, ok := playerConnMap.LoadAndDelete(p.Pid); ok {
		connVal.(*transport.Conn).Close()
	}
	broadcastToRoom(room, p.Pid, codec.NewExtMessage(framesync.ExtCmdPlayerLeft, framesync.EncodePlayerId(p.Pid)))

	slog.Info("gm-ws kicked player", "player_id", p.Pid, "room_id", room.ID)
	c.sendRsp(env.ID, "kick", KickResult{Ok: true, Kicked: p.Pid, Room: room.ID})
}

func (c *GMConn) rpcStopRoom(env *GMEnvelope) {
	var p StopRoomPayload
	if err := msgpack.Unmarshal(env.Payload, &p); err != nil || p.RoomID <= 0 {
		c.sendError(env.ID, "stop_room", "invalid room id")
		return
	}

	room := roomMgr.GetRoom(p.RoomID)
	if room == nil {
		c.sendError(env.ID, "stop_room", "room not found")
		return
	}

	room.Stop()
	room.ForEachPlayer(func(pi framesync.PlayerInfo) {
		playerRoomMap.Delete(pi.ID)
	})
	roomMgr.RemoveRoom(p.RoomID)

	slog.Info("gm-ws stopped room", "room_id", p.RoomID)
	c.sendRsp(env.ID, "stop_room", StopRoomResult{Ok: true, Stopped: p.RoomID})
}

func (c *GMConn) rpcKillRoom(env *GMEnvelope) {
	var p KillRoomPayload
	if err := msgpack.Unmarshal(env.Payload, &p); err != nil || p.RoomID <= 0 {
		c.sendError(env.ID, "kill_room", "invalid room id")
		return
	}

	room := roomMgr.GetRoom(p.RoomID)
	if room == nil {
		c.sendError(env.ID, "kill_room", "room not found")
		return
	}

	// 强制销毁：关闭所有玩家连接，跳过优雅广播
	room.ForEachPlayer(func(pi framesync.PlayerInfo) {
		playerRoomMap.Delete(pi.ID)
		if connVal, ok := playerConnMap.LoadAndDelete(pi.ID); ok {
			connVal.(*transport.Conn).Close()
		}
	})
	room.Stop()
	roomMgr.RemoveRoom(p.RoomID)

	slog.Info("gm-ws killed room", "room_id", p.RoomID)
	c.sendRsp(env.ID, "kill_room", KillRoomResult{Ok: true, Killed: p.RoomID})
}

func (c *GMConn) rpcCreateRoom(env *GMEnvelope) {
	var p CreateRoomPayload
	if err := msgpack.Unmarshal(env.Payload, &p); err != nil {
		c.sendError(env.ID, "create_room", "invalid payload")
		return
	}
	if p.MaxPlayers <= 0 {
		p.MaxPlayers = 2
	}

	room := roomMgr.CreateRoomWithMaxPlayers(p.MaxPlayers)
	room.MatchKey = p.MatchKey

	slog.Info("gm-ws created room", "room_id", room.ID, "max_players", p.MaxPlayers, "match_key", p.MatchKey)
	c.sendRsp(env.ID, "create_room", CreateRoomResult{Ok: true, RoomID: room.ID})
}

func (c *GMConn) rpcNetsim(env *GMEnvelope) {
	var p NetsimPayload
	if err := msgpack.Unmarshal(env.Payload, &p); err != nil {
		c.sendError(env.ID, "netsim", "invalid payload")
		return
	}
	if p.Enabled != nil {
		GlobalNetSim.SetEnabled(*p.Enabled)
	}
	if p.LatencyMs != nil {
		atomic.StoreInt32(&GlobalNetSim.LatencyMs, int32(*p.LatencyMs))
	}
	if p.JitterMs != nil {
		atomic.StoreInt32(&GlobalNetSim.JitterMs, int32(*p.JitterMs))
	}
	if p.LossPercent != nil {
		atomic.StoreInt32(&GlobalNetSim.LossPercent, int32(*p.LossPercent))
	}
	slog.Info("gm-ws netsim updated")
	c.sendRsp(env.ID, "netsim", map[string]bool{"ok": true})
}

// ===================== 发送辅助 =====================

func (c *GMConn) sendEnvelope(env *GMEnvelope) {
	data, err := encodeEnvelope(env)
	if err != nil {
		return
	}
	select {
	case c.sendCh <- data:
	default:
	}
}

func (c *GMConn) sendRsp(id, topic string, payload interface{}) {
	c.sendEnvelope(&GMEnvelope{
		Type:    "rsp",
		ID:      id,
		Topic:   topic,
		Payload: mustEncodePayload(payload),
	})
}

func (c *GMConn) sendError(id, topic, msg string) {
	c.sendEnvelope(&GMEnvelope{
		Type:    "err",
		ID:      id,
		Topic:   topic,
		Payload: mustEncodePayload(ErrorResult{Error: msg}),
	})
}
