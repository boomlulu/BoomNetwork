package framesync

import (
	"log"
	"sync"
	"time"

	"github.com/boom/boomnetwork/codec"
)

// PlayerConn 玩家连接接口
type PlayerConn interface {
	Send(msg *codec.Message) error
}

// PlayerState 玩家状态
type PlayerState int

const (
	PlayerOnline       PlayerState = 0
	PlayerDisconnected PlayerState = 1
)

// Player 房间内的玩家
type Player struct {
	ID             int32
	Conn           PlayerConn
	State          PlayerState
	DisconnectTime time.Time
}

// CachedFrame 缓冲的帧数据
type CachedFrame struct {
	FrameNumber uint32
	EncodedData []byte
}

// RoomConfig 房间配置
type RoomConfig struct {
	FrameRate              int32
	MaxPlayers             int
	FrameBufferSize        int           // 环形缓冲区大小
	DisconnectKeepAlive    time.Duration
	SnapshotIntervalFrames int32         // 快照间隔（帧数），下发给客户端
	QuickReconnectMaxMs    int32         // 快速重连最长重试时间（ms），下发给客户端
}

// DefaultRoomConfig 默认配置
func DefaultRoomConfig() RoomConfig {
	return RoomConfig{
		FrameRate:              20,
		MaxPlayers:             4,
		FrameBufferSize:        2400,
		DisconnectKeepAlive:    120 * time.Second,
		SnapshotIntervalFrames: 100,
		QuickReconnectMaxMs:    5000,
	}
}

// Room 帧同步房间
type Room struct {
	ID     int32
	mu     sync.Mutex
	config RoomConfig

	players map[int32]*Player

	frameRate     int32
	frameInterval time.Duration
	startTime     int64
	frameNumber   uint32
	running       bool
	stopCh        chan struct{}

	pendingInputs []PlayerInput

	// 环形帧缓冲区：固定大小，不会增长
	frameRing    []CachedFrame
	frameRingPos int // 下一个写入位置
	frameRingLen int // 当前有效帧数

	// 复用的编码缓冲区和广播玩家列表
	frameBuf       []byte
	broadcastSlice []*Player

	// 快照存储
	snapshotFrame uint32
	snapshotData  []byte

	// 快照新鲜度监控: 连续 3 个快照间隔未收到快照 → 暂停帧同步
	snapshotStaleFrames uint32 // 自上次快照以来经过的帧数
	snapshotPaused      bool   // 是否因快照过期而暂停

	// 实体权威表: entityId → ownerPlayerId (0 = unclaimed)
	entityAuthority map[int32]int32
}

// NewRoom 创建帧同步房间
func NewRoom(frameRate int32) *Room {
	return NewRoomWithConfig(RoomConfig{
		FrameRate:           frameRate,
		FrameBufferSize:     int(frameRate) * 10,
		DisconnectKeepAlive: 30 * time.Second,
	})
}

// NewRoomWithConfig 用配置创建房间
func NewRoomWithConfig(config RoomConfig) *Room {
	return &Room{
		players:         make(map[int32]*Player),
		config:          config,
		frameRate:       config.FrameRate,
		frameInterval:   time.Duration(1000/config.FrameRate) * time.Millisecond,
		frameRing:       make([]CachedFrame, config.FrameBufferSize),
		frameBuf:        make([]byte, 4096),
		broadcastSlice:  make([]*Player, 0, 16),
		entityAuthority: make(map[int32]int32),
	}
}

// AddPlayer 添加或重连玩家
func (r *Room) AddPlayer(id int32, conn PlayerConn) {
	r.mu.Lock()
	if existing, ok := r.players[id]; ok {
		existing.Conn = conn
		existing.State = PlayerOnline
	} else {
		r.players[id] = &Player{ID: id, Conn: conn, State: PlayerOnline}
	}
	r.mu.Unlock()
}

// DisconnectPlayer 标记断线保留
func (r *Room) DisconnectPlayer(id int32) {
	r.mu.Lock()
	if p, ok := r.players[id]; ok {
		p.State = PlayerDisconnected
		p.DisconnectTime = time.Now()
		p.Conn = nil
	}
	r.mu.Unlock()
}

// RemovePlayer 彻底移除
func (r *Room) RemovePlayer(id int32) {
	r.mu.Lock()
	delete(r.players, id)
	r.mu.Unlock()
}

// PlayerCount 在线玩家数
func (r *Room) PlayerCount() int {
	r.mu.Lock()
	defer r.mu.Unlock()
	count := 0
	for _, p := range r.players {
		if p.State == PlayerOnline {
			count++
		}
	}
	return count
}

// TotalPlayerCount 总玩家数（含断线）
func (r *Room) TotalPlayerCount() int {
	r.mu.Lock()
	defer r.mu.Unlock()
	return len(r.players)
}

// GetPlayerIds 获取所有玩家 ID（含断线保留的）
func (r *Room) GetPlayerIds() []int32 {
	r.mu.Lock()
	defer r.mu.Unlock()
	ids := make([]int32, 0, len(r.players))
	for id := range r.players {
		ids = append(ids, id)
	}
	return ids
}

// IsRunning 帧同步是否正在运行
func (r *Room) IsRunning() bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.running
}

// MaxPlayers 房间最大人数
func (r *Room) MaxPlayers() int {
	return r.config.MaxPlayers
}

// FrameRate 帧率
func (r *Room) FrameRate() int32 {
	return r.frameRate
}

// StartTime 开始时间戳（毫秒）
func (r *Room) StartTime() int64 {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.startTime
}

// GetRoomInfo 获取房间信息快照
func (r *Room) GetRoomInfo() RoomInfo {
	return RoomInfo{
		RoomId:      r.ID,
		PlayerCount: r.PlayerCount(),
		MaxPlayers:  r.config.MaxPlayers,
		Running:     r.IsRunning(),
	}
}

// ForEachOnlinePlayer 遍历在线玩家（用于广播推送）
func (r *Room) ForEachOnlinePlayer(fn func(id int32, conn PlayerConn)) {
	r.mu.Lock()
	players := make([]struct {
		id   int32
		conn PlayerConn
	}, 0, len(r.players))
	for _, p := range r.players {
		if p.State == PlayerOnline && p.Conn != nil {
			players = append(players, struct {
				id   int32
				conn PlayerConn
			}{p.ID, p.Conn})
		}
	}
	r.mu.Unlock()

	for _, p := range players {
		fn(p.id, p.conn)
	}
}

// PlayerInfo GM 用的玩家信息快照
type PlayerInfo struct {
	ID             int32       `json:"id"`
	State          PlayerState `json:"state"` // 0=online, 1=disconnected
	DisconnectTime int64       `json:"disconnect_time,omitempty"` // unix ms, 0=online
}

// ForEachPlayer 遍历所有玩家（含离线），用于 GM 查询
func (r *Room) ForEachPlayer(fn func(info PlayerInfo)) {
	r.mu.Lock()
	infos := make([]PlayerInfo, 0, len(r.players))
	for _, p := range r.players {
		info := PlayerInfo{ID: p.ID, State: p.State}
		if p.State == PlayerDisconnected {
			info.DisconnectTime = p.DisconnectTime.UnixMilli()
		}
		infos = append(infos, info)
	}
	r.mu.Unlock()
	for _, info := range infos {
		fn(info)
	}
}

// CurrentFrameNumber 当前帧号
func (r *Room) CurrentFrameNumber() uint32 {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.frameNumber
}

// SetInitialSnapshot 设置初始快照（frame 0，仅在帧同步开始前调用）
func (r *Room) SetInitialSnapshot(data []byte) {
	r.mu.Lock()
	defer r.mu.Unlock()
	r.snapshotFrame = 0
	r.snapshotData = make([]byte, len(data))
	copy(r.snapshotData, data)
	r.snapshotStaleFrames = 0
}

// UpdateSnapshot 更新房间快照（只接受比当前更新的帧号）
func (r *Room) UpdateSnapshot(frameNumber uint32, data []byte) bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	if frameNumber <= r.snapshotFrame {
		return false
	}
	r.snapshotFrame = frameNumber
	r.snapshotData = make([]byte, len(data))
	copy(r.snapshotData, data)
	r.snapshotStaleFrames = 0

	if r.snapshotPaused {
		r.snapshotPaused = false
		log.Printf("[Room %d] Snapshot received, resuming frame sync\n", r.ID)
	}

	log.Printf("[Room %d] Snapshot updated at frame %d (%d bytes)\n", r.ID, frameNumber, len(data))
	return true
}

// GetSnapshot 获取最新快照
func (r *Room) GetSnapshot() (frameNumber uint32, data []byte) {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.snapshotFrame, r.snapshotData
}

// GetFramesSince 获取 afterFrame 之后的所有缓冲帧
func (r *Room) GetFramesSince(afterFrame uint32) []CachedFrame {
	r.mu.Lock()
	defer r.mu.Unlock()

	var result []CachedFrame
	// 从环形缓冲区读取
	for i := 0; i < r.frameRingLen; i++ {
		idx := (r.frameRingPos - r.frameRingLen + i + len(r.frameRing)) % len(r.frameRing)
		cf := &r.frameRing[idx]
		if cf.FrameNumber > afterFrame {
			data := make([]byte, len(cf.EncodedData))
			copy(data, cf.EncodedData)
			result = append(result, CachedFrame{
				FrameNumber: cf.FrameNumber,
				EncodedData: data,
			})
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
	idx := (r.frameRingPos - r.frameRingLen + len(r.frameRing)) % len(r.frameRing)
	return r.frameRing[idx].FrameNumber
}

// IsSnapshotPaused 是否因快照过期而暂停
func (r *Room) IsSnapshotPaused() bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.snapshotPaused
}

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
	r.frameRingPos = 0
	r.frameRingLen = 0
	r.snapshotStaleFrames = 0
	r.snapshotPaused = false
	r.stopCh = make(chan struct{})
	r.mu.Unlock()

	initData := &InitData{
		FrameRate:           r.frameRate,
		FrameInterval:       int32(r.frameInterval.Milliseconds()),
		StartTime:           r.startTime,
		SnapshotInterval:    r.config.SnapshotIntervalFrames,
		QuickReconnectMaxMs: r.config.QuickReconnectMaxMs,
	}
	r.broadcast(codec.NewCoreMessage(CmdStartFrameSync, EncodeInitData(initData)))

	go r.tickLoop()
}

// Stop 停止帧同步
func (r *Room) Stop() {
	r.mu.Lock()
	if !r.running {
		r.mu.Unlock()
		return
	}
	r.running = false
	close(r.stopCh)
	r.mu.Unlock()

	r.broadcast(codec.NewCoreMessage(CmdStopFrameSync, nil))
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
			log.Printf("[Room %d] PANIC recovered: %v\n", r.ID, rec)
			Metrics.RoomPanics.Inc()
			r.mu.Lock()
			r.running = false
			r.mu.Unlock()
		}
	}()

	ticker := time.NewTicker(r.frameInterval)
	defer ticker.Stop()

	cleanupTicker := time.NewTicker(5 * time.Second)
	defer cleanupTicker.Stop()

	for {
		select {
		case <-r.stopCh:
			return
		case <-ticker.C:
			r.stepFrame()
		case <-cleanupTicker.C:
			removed := r.CleanupDisconnected()
			for _, id := range removed {
				r.broadcast(codec.NewExtMessage(ExtCmdPlayerLeft, EncodePlayerId(id)))
				log.Printf("[Room %d] Player %d removed (disconnect timeout)\n", r.ID, id)
			}
		}
	}
}

func (r *Room) stepFrame() {
	r.mu.Lock()

	// 快照新鲜度检查: 连续 3 个快照间隔未收到快照 → 暂停
	if r.config.SnapshotIntervalFrames > 0 && r.frameNumber > 0 {
		r.snapshotStaleFrames++
		staleLimit := uint32(r.config.SnapshotIntervalFrames * 3)
		if r.snapshotStaleFrames >= staleLimit && !r.snapshotPaused {
			r.snapshotPaused = true
			log.Printf("[Room %d] WARNING: No snapshot for %d frames (limit=%d), pausing frame sync\n",
				r.ID, r.snapshotStaleFrames, staleLimit)
			r.mu.Unlock()
			return
		}
		if r.snapshotPaused {
			r.mu.Unlock()
			return
		}
	}

	r.frameNumber++
	frameNum := r.frameNumber

	// 取走输入
	inputs := r.pendingInputs
	r.pendingInputs = nil

	// 组帧 + 编码（在锁内复用 frameBuf）
	frame := &FrameData{FrameNumber: frameNum, Inputs: inputs}
	size := FrameDataSize(frame)
	if cap(r.frameBuf) < size {
		r.frameBuf = make([]byte, size)
	} else {
		r.frameBuf = r.frameBuf[:size]
	}
	EncodeFrameData(frame, r.frameBuf)

	// 写入环形缓冲区（复用已有的 slot，不 make 新 slice）
	slot := &r.frameRing[r.frameRingPos]
	slot.FrameNumber = frameNum
	if cap(slot.EncodedData) >= size {
		slot.EncodedData = slot.EncodedData[:size]
	} else {
		slot.EncodedData = make([]byte, size)
	}
	copy(slot.EncodedData, r.frameBuf[:size])

	r.frameRingPos = (r.frameRingPos + 1) % len(r.frameRing)
	if r.frameRingLen < len(r.frameRing) {
		r.frameRingLen++
	}

	// 收集在线玩家（复用 broadcastSlice）
	r.broadcastSlice = r.broadcastSlice[:0]
	for _, p := range r.players {
		if p.State == PlayerOnline && p.Conn != nil {
			r.broadcastSlice = append(r.broadcastSlice, p)
		}
	}
	r.mu.Unlock()

	// 广播在锁外执行，不阻塞其他操作
	msg := codec.NewCoreMessage(CmdPushFrames, r.frameBuf[:size])
	for _, p := range r.broadcastSlice {
		p.Conn.Send(msg)
	}
}

// CleanupDisconnected 清理超时断线玩家，返回被移除的玩家 ID
func (r *Room) CleanupDisconnected() []int32 {
	r.mu.Lock()
	defer r.mu.Unlock()

	var removed []int32
	now := time.Now()
	for id, p := range r.players {
		if p.State == PlayerDisconnected && now.Sub(p.DisconnectTime) > r.config.DisconnectKeepAlive {
			delete(r.players, id)
			removed = append(removed, id)
		}
	}
	return removed
}

// broadcast 广播（用于非热路径：Start/Stop）
func (r *Room) broadcast(msg *codec.Message) {
	r.mu.Lock()
	players := make([]*Player, 0, len(r.players))
	for _, p := range r.players {
		if p.State == PlayerOnline && p.Conn != nil {
			players = append(players, p)
		}
	}
	r.mu.Unlock()

	for _, p := range players {
		p.Conn.Send(msg)
	}
}

// === 实体权威转移 ===

// TryGrantAuthority 尝试将 entityId 的权威授予 requesterId。
// 始终授予（支持抢夺），服务器按请求到达顺序仲裁。
func (r *Room) TryGrantAuthority(entityId int32, requesterId int32) (granted bool, currentOwner int32) {
	r.mu.Lock()
	defer r.mu.Unlock()
	r.entityAuthority[entityId] = requesterId
	return true, requesterId
}

// ReleaseAuthority 释放玩家对某实体的权威。只有持有者可释放。
func (r *Room) ReleaseAuthority(entityId int32, requesterId int32) bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.entityAuthority[entityId] == requesterId {
		r.entityAuthority[entityId] = 0
		return true
	}
	return false
}

// ReleaseAllAuthority 释放某玩家持有的所有实体权威（断线清理用）。
func (r *Room) ReleaseAllAuthority(playerId int32) []int32 {
	r.mu.Lock()
	defer r.mu.Unlock()
	var released []int32
	for eid, owner := range r.entityAuthority {
		if owner == playerId {
			r.entityAuthority[eid] = 0
			released = append(released, eid)
		}
	}
	return released
}
