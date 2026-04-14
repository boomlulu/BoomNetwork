package framesync

import (
	"context"
	"log/slog"
	"math"
	"sync/atomic"
	"time"

	"github.com/boomlulu/boomnetwork/codec"
)

// Start 开始帧同步
func (r *Room) Start() {
	r.mu.Lock()
	if r.running {
		r.mu.Unlock()
		return
	}
	r.running = true
	r.frameNumber = 0
	r.startTime = time.Now().UnixMilli()
	r.startedAt = time.Now()
	r.frameRingPos = 0
	r.frameRingLen = 0
	r.snapshotFrame = 0  // 清除上一轮的快照，防止新会话迟加入者收到旧快照
	r.snapshotData = nil
	r.snapshotStaleFrames = 0
	r.snapshotPaused = false
	r.stopCh = make(chan struct{})
	d := r.delegate
	r.mu.Unlock()

	slog.Info("room started: snapshot cleared", "roomId", r.ID)
	r.LogEvent("INFO", "room started", nil)

	initData := &InitData{
		FrameRate:           r.frameRate,
		FrameInterval:       int32(r.frameInterval.Milliseconds()),
		StartTime:           r.startTime,
		SnapshotInterval:    r.config.SnapshotIntervalFrames,
		QuickReconnectMaxMs: r.config.QuickReconnectMaxMs,
	}
	r.broadcast(codec.NewCoreMessage(CmdStartFrameSync, EncodeInitData(initData)))
	go r.tickLoop()

	if d != nil {
		d.OnRoomStarted(r)
	}
}

// Stop 停止帧同步
func (r *Room) Stop() {
	r.mu.Lock()
	if !r.running {
		r.mu.Unlock()
		return
	}
	r.running = false
	startedAt := r.startedAt
	close(r.stopCh)
	d := r.delegate
	r.mu.Unlock()

	if !startedAt.IsZero() {
		Metrics.RoomLifetimeSeconds.Observe(time.Since(startedAt).Seconds())
	}
	r.LogEvent("INFO", "room stopped", nil)
	r.broadcast(codec.NewCoreMessage(CmdStopFrameSync, nil))

	if d != nil {
		d.OnRoomStopped(r)
	}
}

// OnInput 收到玩家输入
func (r *Room) OnInput(playerId int32, data []byte) {
	r.mu.Lock()
	r.pendingInputs = append(r.pendingInputs, PlayerInput{
		PlayerId: playerId,
		Data:     data,
	})
	r.mu.Unlock()
}

func (r *Room) tickLoop() {
	defer func() {
		if rec := recover(); rec != nil {
			slog.Error("PANIC recovered", "roomId", r.ID, "panic", rec)
			Metrics.RoomPanics.Inc()

			r.broadcast(codec.NewCoreMessage(CmdStopFrameSync, nil))

			r.mu.Lock()
			r.running = false
			playerIds := make([]int32, 0, len(r.players))
			for id := range r.players {
				playerIds = append(playerIds, id)
			}
			r.players = make(map[int32]*Player)
			atomic.StoreInt32(&r.onlineCount, 0)
			d := r.delegate
			r.mu.Unlock()

			// L4: 广播 PlayerLeft 通知残留客户端（panic 前正常 Stop 路径会发此事件，panic 恢复路径必须补发）
			for _, id := range playerIds {
				r.broadcast(codec.NewExtMessage(ExtCmdPlayerLeft, EncodePlayerId(id)))
			}

			if d != nil {
				d.OnRoomPanicked(r, playerIds)
			}
		}
	}()

	ticker := time.NewTicker(r.frameInterval)
	defer ticker.Stop()

	var lastTickAt time.Time
	for {
		select {
		case <-r.stopCh:
			return
		case t := <-ticker.C:
			if !lastTickAt.IsZero() {
				if elapsed := t.Sub(lastTickAt); elapsed > r.frameInterval*2 {
					elapsedMs := elapsed.Milliseconds()
					expectedMs := r.frameInterval.Milliseconds()
					slog.Warn("tickLoop jitter detected",
						"roomId", r.ID, "elapsedMs", elapsedMs, "expectedMs", expectedMs, "frame", r.frameNumber)
					r.LogEvent("WARN", "tickLoop jitter", map[string]any{
						"elapsed_ms": elapsedMs, "expected_ms": expectedMs,
					})
				}
			}
			lastTickAt = t
			r.stepFrame()
		}
	}
}

func (r *Room) stepFrame() {
	r.mu.Lock()

	// 快照新鲜度检查: 连续 3 个快照间隔未收到快照 → 暂停
	// gamePaused 期间 OnFrame 不触发，客户端无法调用 CheckSnapshotUpload，
	// 不应将暂停时间计入过期计数（否则升级暂停必然触发 SnapshotStale 死循环）。
	if r.config.SnapshotIntervalFrames > 0 && r.frameNumber > 0 && !r.gamePaused {
		r.snapshotStaleFrames++
		staleLimit := uint32(r.config.SnapshotIntervalFrames * 3)
		if r.snapshotStaleFrames >= staleLimit && !r.snapshotPaused {
			r.snapshotPaused = true
			slog.Warn("no snapshot received, pausing frame sync", "roomId", r.ID, "staleFrames", r.snapshotStaleFrames, "limit", staleLimit)
			d := r.delegate
			r.mu.Unlock()
			// NEW-01: 暂停时清空 frameHashes，避免帧号不递增导致哈希永驻内存。
			// 锁顺序规则：在 r.mu 释放后单独持 desyncMu（防死锁）。
			r.desyncMu.Lock()
			r.frameHashes = make(map[uint32]map[int32]uint32)
			r.desyncMu.Unlock()
			r.broadcast(codec.NewExtMessage(ExtCmdFrameSyncPaused, []byte{byte(PauseReasonSnapshotStale)}))
			if d != nil {
				d.OnRoomPaused(r, PauseReasonSnapshotStale)
			}
			return
		}
		if r.snapshotPaused {
			r.mu.Unlock()
			return
		}
	}

	// 游戏级暂停: 客户端请求的暂停，不推帧、不递增帧号
	if r.gamePaused {
		r.mu.Unlock()
		return
	}

	// M1: uint32 帧号溢出保护，接近 MaxUint32 时主动停房间
	if r.frameNumber == math.MaxUint32 {
		slog.Error("frame number overflow, stopping room", "roomId", r.ID)
		r.running = false
		close(r.stopCh)
		d := r.delegate
		r.mu.Unlock()
		r.broadcast(codec.NewCoreMessage(CmdStopFrameSync, nil))
		if d != nil {
			d.OnRoomStopped(r)
		}
		return
	}
	r.frameNumber++
	frameNum := r.frameNumber

	// 取走输入和事件（双缓冲 swap：复用底层数组，避免每帧重分配）
	inputs := r.pendingInputs
	r.pendingInputs = r.pendingInputsBuf[:0]
	r.pendingInputsBuf = inputs
	events := r.pendingEvents
	r.pendingEvents = r.pendingEventsBuf[:0]
	r.pendingEventsBuf = events
	r.mu.Unlock()

	// PERF-01: 直接在锁内确定 slot 位置并编码，省掉 frameBuf 中间 copy。
	// inputs/events 已在锁外局部持有，编码在锁内完成。
	frame := &FrameData{FrameNumber: frameNum, Inputs: inputs, Events: events}
	size := FrameDataSize(frame)

	// 写入环形缓冲区，同时向每个玩家的 delivery channel 投递（单写者原则：只有各自的 deliveryLoop 写 conn）
	r.mu.Lock()
	slot := &r.frameRing[r.frameRingPos]
	slot.FrameNumber = frameNum
	if cap(slot.EncodedData) < size {
		slot.EncodedData = make([]byte, size)
	} else {
		slot.EncodedData = slot.EncodedData[:size]
	}
	EncodeFrameData(frame, slot.EncodedData)
	r.frameRingPos = (r.frameRingPos + 1) % len(r.frameRing)
	if r.frameRingLen < len(r.frameRing) {
		r.frameRingLen++
	}
	// 非阻塞投递：channel 满时记录慢客户端警告（delivery loop 自行处理 conn 写入）
	for _, p := range r.players {
		if p.State == PlayerOnline && p.frameCh != nil {
			select {
			case p.frameCh <- slot:
			default:
				slog.Warn("delivery channel full, slow client", "playerId", p.ID, "roomId", r.ID)
				Metrics.BroadcastSendErrors.Inc()
			}
		}
	}
	r.mu.Unlock()

	Metrics.FramesPushed.Inc()
	Metrics.FrameBroadcastLatency.Observe(0) // channel 投递无阻塞延迟
}

// deliveryLoop 每个玩家独立的帧投递协程（单写者原则：唯一写 conn 的路径）。
// 阶段 1：追帧（catch-up） — 从 frameRing 中发送 cursor 之后的历史帧。
// 阶段 2：实时（live）   — 从 frameCh 中读取 stepFrame 投递的实时帧。
// 两个阶段串行在同一 goroutine，消除 replay/实时竞态。
func (r *Room) deliveryLoop(ctx context.Context, p *Player, conn PlayerConn, frameCh chan *CachedFrame, preamble []*codec.Message) {
	defer r.onDeliveryExit(p, conn)

	// PERF-02: 复用 Timer，避免追帧阶段每批次创建新 Timer 对象。
	replayTimer := time.NewTimer(0)
	if !replayTimer.Stop() {
		<-replayTimer.C
	}
	defer replayTimer.Stop()

	// 前导消息（快照 / StartFrameSync / S2C reliable 等），在帧数据前先发
	for _, msg := range preamble {
		select {
		case <-ctx.Done():
			return
		default:
		}
		if err := conn.Send(msg); err != nil {
			return
		}
	}

	// 阶段 1：追帧 — 读 frameRing，发送 cursor 之后的所有历史帧
	currentFrame := r.CurrentFrameNumber()
	if p.cursor < currentFrame {
		frames := r.GetFramesSince(p.cursor)
		catchupCount := len(frames)
		catchupStart := time.Now()
		slog.Info("deliveryLoop phase1 start",
			"roomId", r.ID, "playerId", p.ID, "fromFrame", p.cursor, "toFrame", currentFrame, "count", catchupCount)
		r.LogEvent("INFO", "deliveryLoop phase1 start", map[string]any{
			"player_id": p.ID, "from_frame": p.cursor, "to_frame": currentFrame, "count": catchupCount,
		})
		for i, cf := range frames {
			select {
			case <-ctx.Done():
				return
			default:
			}
			if err := conn.Send(codec.NewCoreMessage(CmdPushFrames, cf.EncodedData)); err != nil {
				return
			}
			p.cursor = cf.FrameNumber
			if (i+1)%replayBatchSize == 0 && i+1 < len(frames) {
				replayTimer.Reset(replayBatchDelay)
				select {
				case <-ctx.Done():
					return
				case <-replayTimer.C:
				}
			}
		}
		elapsedMs := time.Since(catchupStart).Milliseconds()
		slog.Info("deliveryLoop phase1 done",
			"roomId", r.ID, "playerId", p.ID, "count", catchupCount, "elapsedMs", elapsedMs)
		r.LogEvent("INFO", "deliveryLoop phase1 done", map[string]any{
			"player_id": p.ID, "count": catchupCount, "elapsed_ms": elapsedMs,
		})
	}

	// 阶段 1 结束：升级补帧玩家为在线（可以接收实时广播了）
	r.SetPlayerLive(p.ID)

	// 阶段 2：实时 — 从 channel 读取 stepFrame 投递的帧；跳过阶段 1 已发送的帧（防重）
	for {
		select {
		case <-ctx.Done():
			slog.Info("deliveryLoop phase2 ctx done", "roomId", r.ID, "playerId", p.ID, "cursor", p.cursor)
			return
		case cf, ok := <-frameCh:
			if !ok {
				slog.Info("deliveryLoop phase2 frameCh closed", "roomId", r.ID, "playerId", p.ID, "cursor", p.cursor)
				return
			}
			if cf.FrameNumber <= p.cursor {
				continue // 阶段 1 已发，跳过
			}
			if err := conn.Send(codec.NewCoreMessage(CmdPushFrames, cf.EncodedData)); err != nil {
				slog.Warn("deliveryLoop phase2 send error", "roomId", r.ID, "playerId", p.ID, "frame", cf.FrameNumber, "err", err)
				return
			}
			p.cursor = cf.FrameNumber
		}
	}
}

// onDeliveryExit 在 deliveryLoop 退出时调用。
// 若玩家仍在线（ctx 非正常取消，即 conn 发送失败），关闭 conn 并标记断线。
// DisconnectPlayer 幂等：transport 的 onClientDisconnect 回调也可能调用它，无副作用。
//
// conn == p.Conn 守卫：旧 deliveryLoop（重连前的 goroutine）的退出不能关闭新连接。
// 重连时 AddPlayer 将 p.Conn 更新为新连接；旧循环的 conn 与 p.Conn 不匹配 → 跳过清理，
// 防止旧循环竞态地 DisconnectPlayer → 取消新 deliveryLoop → 新连接无法推帧。
func (r *Room) onDeliveryExit(p *Player, conn PlayerConn) {
	r.mu.Lock()
	isCurrentConn := p.Conn == conn
	state := p.State
	isOnline := state == PlayerOnline && isCurrentConn
	r.mu.Unlock()
	slog.Info("onDeliveryExit", "roomId", r.ID, "playerId", p.ID, "state", state, "isCurrentConn", isCurrentConn, "isOnline", isOnline)
	if isOnline && conn != nil {
		conn.Close() //nolint:errcheck — 触发 transport.OnDisconnect → onClientDisconnect
		r.DisconnectPlayer(p.ID)
	}
}

// broadcast 广播（用于非热路径：Start/Stop/Pause/Resume）
// 调用链串行化保证：
//   - Start() / Stop() 由 running 状态机控制（re-entrant 检查）
//   - Pause / Resume 通过 snapshotPaused 状态位保证同一时刻只有一个调用路径执行
//
// 因此 broadcast() 不会被并发调用，broadcastBuf 无需额外保护。
// 控制消息发送失败仅记录日志，不触发断连（由下次帧广播失败时处理）。
func (r *Room) broadcast(msg *codec.Message) {
	r.mu.Lock()
	r.broadcastBuf = r.broadcastBuf[:0]
	for _, p := range r.players {
		if p.State == PlayerOnline && p.Conn != nil {
			r.broadcastBuf = append(r.broadcastBuf, p)
		}
	}
	r.mu.Unlock()

	for _, p := range r.broadcastBuf {
		if err := p.Conn.Send(msg); err != nil {
			slog.Warn("control broadcast send error", "playerId", p.ID, "err", err)
		}
	}
}

// GetFramesSince 获取 afterFrame 之后的所有缓冲帧
// 优化：两次遍历环形缓冲区，将所有帧数据合并到单个大 backing buffer，
// 将独立分配从 O(N帧) 降至 O(1)（1 次 backing + 1 次 result slice）。
func (r *Room) GetFramesSince(afterFrame uint32) []CachedFrame {
	r.mu.Lock()
	defer r.mu.Unlock()

	// 第一次遍历：统计总字节数和帧数
	totalBytes := 0
	count := 0
	for i := 0; i < r.frameRingLen; i++ {
		idx := (r.frameRingPos - r.frameRingLen + i + len(r.frameRing)) % len(r.frameRing)
		cf := &r.frameRing[idx]
		if cf.FrameNumber > afterFrame {
			totalBytes += len(cf.EncodedData)
			count++
		}
	}
	if count == 0 {
		return nil
	}

	// 一次性分配所有数据所需的大 buffer + 结果 slice
	backing := make([]byte, totalBytes)
	result := make([]CachedFrame, 0, count)

	// 第二次遍历：将帧数据连续拷贝到 backing，子切片引用其中各段
	offset := 0
	for i := 0; i < r.frameRingLen; i++ {
		idx := (r.frameRingPos - r.frameRingLen + i + len(r.frameRing)) % len(r.frameRing)
		cf := &r.frameRing[idx]
		if cf.FrameNumber > afterFrame {
			n := len(cf.EncodedData)
			copy(backing[offset:], cf.EncodedData)
			result = append(result, CachedFrame{
				FrameNumber: cf.FrameNumber,
				EncodedData: backing[offset : offset+n],
			})
			offset += n
		}
	}
	return result
}

// OldestBufferedFrame 环形缓冲区中最旧帧号（0 表示缓冲区为空）
func (r *Room) OldestBufferedFrame() uint32 {
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.frameRingLen == 0 {
		return 0
	}
	// L7: +len(r.frameRing) 防止 frameRingPos < frameRingLen 时模运算结果为负数
	idx := (r.frameRingPos - r.frameRingLen + len(r.frameRing)) % len(r.frameRing)
	return r.frameRing[idx].FrameNumber
}

// ReconnectSnapshot 重连所需的所有字段，一次加锁原子读取（NEW-03）
type ReconnectSnapshot struct {
	Running       bool
	GamePaused    bool
	Frame         uint32
	OldestFrame   uint32
	SnapshotFrame uint32
	Snapshot      []byte
}

// GetReconnectSnapshot 一次加锁读取所有重连所需字段，避免多次独立调用之间的 TOCTOU。
func (r *Room) GetReconnectSnapshot() ReconnectSnapshot {
	r.mu.Lock()
	defer r.mu.Unlock()

	var oldestFrame uint32
	if r.frameRingLen > 0 {
		idx := (r.frameRingPos - r.frameRingLen + len(r.frameRing)) % len(r.frameRing)
		oldestFrame = r.frameRing[idx].FrameNumber
	}

	var snapshotCopy []byte
	if len(r.snapshotData) > 0 {
		snapshotCopy = make([]byte, len(r.snapshotData))
		copy(snapshotCopy, r.snapshotData)
	}

	return ReconnectSnapshot{
		Running:       r.running,
		GamePaused:    r.gamePaused,
		Frame:         r.frameNumber,
		OldestFrame:   oldestFrame,
		SnapshotFrame: r.snapshotFrame,
		Snapshot:      snapshotCopy,
	}
}

// IsRunning 帧同步是否正在运行
func (r *Room) IsRunning() bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.running
}

// RoomState 房间状态快照（一次加锁原子读取）
type RoomState struct {
	Running    bool
	GamePaused bool
}

// GetState 原子读取房间运行状态，避免连续两次独立读取的 TOCTOU。
func (r *Room) GetState() RoomState {
	r.mu.Lock()
	defer r.mu.Unlock()
	return RoomState{
		Running:    r.running,
		GamePaused: r.gamePaused,
	}
}

// FrameBufferLen 当前帧缓冲中有效帧数
func (r *Room) FrameBufferLen() int {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.frameRingLen
}

// FrameBufferCap 帧缓冲区总容量
func (r *Room) FrameBufferCap() int {
	return len(r.frameRing) // 固定大小，无需锁
}

// PendingInputsLen 当前待处理输入队列深度（GM 检视用）
func (r *Room) PendingInputsLen() int {
	r.mu.Lock()
	defer r.mu.Unlock()
	return len(r.pendingInputs)
}

// CurrentFrameNumber 当前帧号
func (r *Room) CurrentFrameNumber() uint32 {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.frameNumber
}
