package framesync

import (
	"context"
	"log/slog"
	"math"
	"sync"
	"sync/atomic"
	"time"

	"github.com/boomlulu/boomnetwork/codec"
)

// 帧投递相关常量
const (
	frameChBuffer    = 64             // 每个玩家投递 channel 的缓冲帧数（~3s @ 20fps）
	replayBatchSize  = 100            // 补帧批次大小：每发完 N 帧休眠一次
	replayBatchDelay = 5 * time.Millisecond // 补帧批次间隔
)

// PlayerConn 玩家连接接口
type PlayerConn interface {
	Send(msg *codec.Message) error
	Close() error
}

// s2cBufSize S→C 可靠通道每个玩家的环形缓冲区大小（条数）
const s2cBufSize = 256

// cachedS2CMsg S→C 可靠通道缓存的消息
type cachedS2CMsg struct {
	seq uint32
	msg *codec.Message // 已包装为 ExtCmdReliableMsg，可直接发送
}

// playerSlicePool 复用 []*Player 临时切片，供 ForEachOnlinePlayer 等非热路径使用
// sync.Pool 本身线程安全，可替代 make([]*Player,...) 避免 per-call GC 分配
var playerSlicePool = sync.Pool{
	New: func() any {
		s := make([]*Player, 0, 16)
		return &s
	},
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
	JoinedAt       time.Time // 首次加入时间（GM 检视用）

	// S→C 可靠通道
	s2cSeq     uint32                    // 已分配的最新 seq（单调递增）
	s2cBuf     [s2cBufSize]cachedS2CMsg  // 环形缓冲区（slot = seq % s2cBufSize）
	s2cBufHead uint32                    // 缓冲区中最老的 seq（seq < s2cBufHead 视为 stale）

	// C→S 可靠通道
	lastProcessedC2SSeq uint32 // 已处理（去重）的最新 C→S seq

	// 单写者投递通道（per-player delivery loop 专用）
	cursor   uint32               // 已投递给该玩家的最新帧号（追帧基准）
	frameCh  chan *CachedFrame    // 有界 channel，stepFrame 向此投递实时帧
	cancelFn context.CancelFunc  // 取消 delivery goroutine
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

// RoomDelegate Room 和 Player 生命周期回调接口
// 由外层（应用层 / 游戏玩法层）实现并注入 Room
type RoomDelegate interface {
	// Room 生命周期
	OnRoomStarted(room *Room)
	OnRoomStopped(room *Room)
	OnRoomPaused(room *Room, reason FrameSyncPauseReason)
	OnRoomResumed(room *Room)
	OnRoomPanicked(room *Room, playerIds []int32)

	// Player 生命周期
	OnPlayerJoined(room *Room, player *Player)
	OnPlayerReconnected(room *Room, player *Player)
	OnPlayerDisconnected(room *Room, player *Player)
	OnPlayerRemoved(room *Room, playerID int32) // 彻底移除（keepalive 到期 / 主动离开）
}

// NoopRoomDelegate 空实现，游戏玩法层按需 embed 并覆盖所需方法
type NoopRoomDelegate struct{}

func (NoopRoomDelegate) OnRoomStarted(*Room)                      {}
func (NoopRoomDelegate) OnRoomStopped(*Room)                      {}
func (NoopRoomDelegate) OnRoomPaused(*Room, FrameSyncPauseReason) {}
func (NoopRoomDelegate) OnRoomResumed(*Room)                      {}
func (NoopRoomDelegate) OnRoomPanicked(*Room, []int32)            {}
func (NoopRoomDelegate) OnPlayerJoined(*Room, *Player)            {}
func (NoopRoomDelegate) OnPlayerReconnected(*Room, *Player)       {}
func (NoopRoomDelegate) OnPlayerDisconnected(*Room, *Player)      {}
func (NoopRoomDelegate) OnPlayerRemoved(*Room, int32)             {}

// Room 帧同步房间
type Room struct {
	ID       int32
	MatchKey string // 匹配 key，相同 key 才能匹配到一起
	mu       sync.Mutex
	config   RoomConfig

	players map[int32]*Player

	frameRate     int32
	frameInterval time.Duration
	startTime     int64
	frameNumber   uint32
	running       bool
	stopCh        chan struct{}

	pendingInputs    []PlayerInput
	pendingInputsBuf []PlayerInput // 双缓冲 swap，复用底层数组，减少每帧分配
	pendingEvents    []FrameEvent  // 帧内事件队列（同步中使用）
	pendingEventsBuf []FrameEvent  // 双缓冲 swap
	hostPlayerId     int32         // 房主 ID（0 = 无房主）

	// onlineCount 原子计数器，O(1) 替代每次遍历 players 的 PlayerCount()
	onlineCount int32

	// 环形帧缓冲区：固定大小，不会增长
	frameRing    []CachedFrame
	frameRingPos int // 下一个写入位置
	frameRingLen int // 当前有效帧数

	// 复用的编码缓冲区和广播玩家列表
	frameBuf     []byte
	broadcastBuf []*Player // broadcast() 专用，非热路径（Start/Stop/Pause），调用方已通过 running 状态机串行化

	// 快照存储
	snapshotFrame uint32
	snapshotData  []byte

	// 快照新鲜度监控: 连续 3 个快照间隔未收到快照 → 暂停帧同步
	snapshotStaleFrames uint32 // 自上次快照以来经过的帧数
	snapshotPaused      bool   // 是否因快照过期而暂停

	// 游戏级暂停: 客户端请求，服务器停推帧（心跳保持）
	gamePaused bool

	// 实体权威表: entityId → ownerPlayerId (0 = unclaimed)
	entityAuthority map[int32]int32

	// 轻量状态同步 KV 存储
	dataStore   map[int64]DataEntry // key = DataStoreKey(playerId, key)
	dataVersion uint32

	// Desync detection: frame hash collection
	frameHashes    map[uint32]map[int32]uint32 // frameNumber → playerId → hash
	desyncDetected bool

	// 生命周期委托：通知外层状态变更
	delegate RoomDelegate

	// 房间生命周期
	createdAt time.Time  // 创建时间
	hadPlayer atomic.Bool // 是否有过玩家加入（原子读写，无需持锁）
	startedAt time.Time  // 指标：Start 时间
	emptyAt   time.Time  // 最近一次变空的时刻；有玩家时为零值
}

// NewRoom 创建帧同步房间
func NewRoom(frameRate int32) *Room {
	return NewRoomWithConfig(RoomConfig{
		FrameRate:           frameRate,
		FrameBufferSize:     int(frameRate) * 10,
		DisconnectKeepAlive: 30 * time.Second,
	})
}

// Validate 填充 RoomConfig 中的零值为安全默认值（L3: SnapshotIntervalFrames=0 时逻辑失效防护）
func (c *RoomConfig) Validate() {
	if c.FrameRate <= 0 {
		c.FrameRate = 20
	}
	if c.MaxPlayers <= 0 {
		c.MaxPlayers = 4
	}
	if c.FrameBufferSize <= 0 {
		c.FrameBufferSize = int(c.FrameRate) * 120
	}
	if c.DisconnectKeepAlive <= 0 {
		c.DisconnectKeepAlive = 120 * time.Second
	}
	if c.SnapshotIntervalFrames <= 0 {
		c.SnapshotIntervalFrames = 100
	}
	if c.QuickReconnectMaxMs <= 0 {
		c.QuickReconnectMaxMs = 5000
	}
}

// NewRoomWithConfig 用配置创建房间
func NewRoomWithConfig(config RoomConfig) *Room {
	config.Validate() // L3: 补全零值为安全默认值
	return &Room{
		players:          make(map[int32]*Player),
		config:           config,
		frameRate:        config.FrameRate,
		frameInterval:    time.Duration(1000/config.FrameRate) * time.Millisecond,
		frameRing:        make([]CachedFrame, config.FrameBufferSize),
		frameBuf:     make([]byte, 4096),
		broadcastBuf: make([]*Player, 0, 16),
		pendingInputs:    make([]PlayerInput, 0, 8),    // 双端预分配：swap 后两侧永远有 cap
		pendingInputsBuf: make([]PlayerInput, 0, 8),
		pendingEvents:    make([]FrameEvent, 0, 4),     // 同上
		pendingEventsBuf: make([]FrameEvent, 0, 4),
		entityAuthority:  make(map[int32]int32),
		dataStore:        make(map[int64]DataEntry),
		frameHashes:      make(map[uint32]map[int32]uint32),
		createdAt:        time.Now(),
	}
}

// SetDelegate 注入生命周期委托（幂等，可在任意时刻设置）
func (r *Room) SetDelegate(d RoomDelegate) {
	r.mu.Lock()
	r.delegate = d
	r.mu.Unlock()
}

// removePlayerLocked 唯一的玩家移除出口（必须在持锁状态下调用）
func (r *Room) removePlayerLocked(id int32) {
	if p, ok := r.players[id]; ok {
		if p.State == PlayerOnline {
			atomic.AddInt32(&r.onlineCount, -1)
		}
		if p.cancelFn != nil {
			p.cancelFn()
			p.cancelFn = nil
		}
		p.frameCh = nil
	}
	delete(r.players, id)
	if r.hostPlayerId == id {
		r.electHost()
	}
	if len(r.players) == 0 {
		r.emptyAt = time.Now()
	}
}

// AddPlayer 添加或重连玩家。
// startFrame：玩家已有帧号（delivery loop 从此处之后开始追帧）。
// preamble：在帧数据之前先投递的控制消息（快照/StartFrameSync/S2C reliable 等）。
func (r *Room) AddPlayer(id int32, conn PlayerConn, startFrame uint32, preamble ...*codec.Message) {
	r.mu.Lock()
	isReconnect := false
	var player *Player
	if existing, ok := r.players[id]; ok {
		// 取消旧的 delivery loop（重连时）
		if existing.cancelFn != nil {
			existing.cancelFn()
			existing.cancelFn = nil
		}
		existing.frameCh = nil
		if existing.State != PlayerOnline {
			atomic.AddInt32(&r.onlineCount, 1)
		}
		existing.Conn = conn
		existing.State = PlayerOnline
		existing.cursor = startFrame
		isReconnect = true
		player = existing
	} else {
		atomic.AddInt32(&r.onlineCount, 1)
		player = &Player{ID: id, Conn: conn, State: PlayerOnline, JoinedAt: time.Now(), cursor: startFrame}
		r.players[id] = player
	}
	r.hadPlayer.Store(true)
	r.emptyAt = time.Time{} // 有玩家，清零空房间计时
	if r.hostPlayerId == 0 {
		r.hostPlayerId = id
	}

	// 创建有界 channel 和 delivery goroutine
	player.frameCh = make(chan *CachedFrame, frameChBuffer)
	ctx, cancel := context.WithCancel(context.Background())
	player.cancelFn = cancel

	// 锁内捕获 channel 引用：避免 Unlock 后 DisconnectPlayer 并发置 nil 造成竞态
	newFrameCh := player.frameCh
	d := r.delegate
	r.mu.Unlock()

	go r.deliveryLoop(ctx, player, conn, newFrameCh, preamble)

	if d != nil {
		if isReconnect {
			d.OnPlayerReconnected(r, player)
		} else {
			d.OnPlayerJoined(r, player)
		}
	}
}

// DisconnectPlayer 标记断线保留（幂等：多次调用安全，delegate 只在状态真正变化时触发）
func (r *Room) DisconnectPlayer(id int32) {
	r.mu.Lock()
	var player *Player
	if p, ok := r.players[id]; ok {
		if p.State == PlayerOnline {
			atomic.AddInt32(&r.onlineCount, -1)
			p.State = PlayerDisconnected
			p.DisconnectTime = time.Now()
			p.Conn = nil
			if p.cancelFn != nil {
				p.cancelFn()
				p.cancelFn = nil
			}
			p.frameCh = nil // 阻止 stepFrame 继续投递
			player = p      // 仅首次（Online→Disconnected）才触发 delegate
		} else {
			// 已是 Disconnected，仍清理字段（幂等），但不触发 delegate
			p.Conn = nil
			if p.cancelFn != nil {
				p.cancelFn()
				p.cancelFn = nil
			}
			p.frameCh = nil
		}
	}
	if r.hostPlayerId == id && r.running {
		r.electHost()
	}
	d := r.delegate
	r.mu.Unlock()

	if d != nil && player != nil {
		d.OnPlayerDisconnected(r, player)
	}
}

// RemovePlayer 彻底移除玩家（通过 removePlayerLocked 统一路径）
func (r *Room) RemovePlayer(id int32) {
	r.mu.Lock()
	r.removePlayerLocked(id)
	d := r.delegate
	r.mu.Unlock()

	if d != nil {
		d.OnPlayerRemoved(r, id)
	}
}

// EnqueueEvent 添加帧内事件（在同步中调用，事件随下一帧广播）
func (r *Room) EnqueueEvent(eventType byte, playerId int32) {
	r.mu.Lock()
	r.pendingEvents = append(r.pendingEvents, FrameEvent{EventType: eventType, PlayerId: playerId})
	r.mu.Unlock()
}

// HostPlayerId 获取当前房主
func (r *Room) HostPlayerId() int32 {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.hostPlayerId
}

// setHost 设置房主并入队 HostChanged 事件（需在锁内调用）
func (r *Room) setHost(id int32) {
	r.hostPlayerId = id
	r.pendingEvents = append(r.pendingEvents, FrameEvent{EventType: FrameEventHostChanged, PlayerId: id})
}

// electHost 选举新房主：选第一个在线玩家（需在锁内调用）
func (r *Room) electHost() {
	for _, p := range r.players {
		if p.State == PlayerOnline {
			r.setHost(p.ID)
			return
		}
	}
	r.hostPlayerId = 0 // 无在线玩家
}

// PlayerCount 在线玩家数（原子读，无锁）
func (r *Room) PlayerCount() int {
	return int(atomic.LoadInt32(&r.onlineCount))
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

// AppendPlayerIds 将所有玩家 ID（含断线保留的）追加到 dst 并返回。
// 调用方可传入预分配切片避免分配：ids = room.AppendPlayerIds(ids[:0])
func (r *Room) AppendPlayerIds(dst []int32) []int32 {
	r.mu.Lock()
	defer r.mu.Unlock()
	for id := range r.players {
		dst = append(dst, id)
	}
	return dst
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

// IsGamePaused 是否处于游戏级暂停
func (r *Room) IsGamePaused() bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.gamePaused
}

// GamePause 设置游戏级暂停。返回 true 表示状态变更（从运行→暂停）。
func (r *Room) GamePause() bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.gamePaused {
		return false
	}
	r.gamePaused = true
	return true
}

// GameResume 解除游戏级暂停。返回 true 表示状态变更（从暂停→运行）。
func (r *Room) GameResume() bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	if !r.gamePaused {
		return false
	}
	r.gamePaused = false
	return true
}

// GetRoomInfo 获取房间信息快照
// P2-4: 单次加锁同时读取 running + onlineCount，避免 IsRunning() 独立加锁。
func (r *Room) GetRoomInfo() RoomInfo {
	r.mu.Lock()
	running := r.running
	playerCount := int(atomic.LoadInt32(&r.onlineCount))
	r.mu.Unlock()
	return RoomInfo{
		RoomId:      r.ID,
		PlayerCount: playerCount,
		MaxPlayers:  r.config.MaxPlayers,
		Running:     running,
		MatchKey:    r.MatchKey,
	}
}

// ForEachOnlinePlayer 遍历在线玩家（用于广播推送）
// 使用 playerSlicePool 复用临时切片，消除每次调用的匿名 struct slice 分配。
func (r *Room) ForEachOnlinePlayer(fn func(id int32, conn PlayerConn)) {
	sp := playerSlicePool.Get().(*[]*Player)
	players := (*sp)[:0]

	r.mu.Lock()
	for _, p := range r.players {
		if p.State == PlayerOnline && p.Conn != nil {
			players = append(players, p)
		}
	}
	r.mu.Unlock()

	for _, p := range players {
		fn(p.ID, p.Conn)
	}

	// 归还前清零指针，防止 Pool 持有 Player 对象阻碍 GC
	for i := range players {
		players[i] = nil
	}
	// M4: 超大切片不归还 Pool，避免 Pool 长期持有大内存（cap>64 直接丢弃）
	if cap(players) <= 64 {
		*sp = players[:0]
		playerSlicePool.Put(sp)
	}
}

// === S→C 可靠通道 ===

// SendReliableToPlayer 向指定玩家发送可靠消息（在线时立即发送，离线时只入队等待重连补发）
// inner 消息将被包装为 ExtCmdReliableMsg，赋予单调递增 seq 并存入环形缓冲区。
func (r *Room) SendReliableToPlayer(playerId int32, inner *codec.Message) {
	r.mu.Lock()
	p, ok := r.players[playerId]
	if !ok {
		r.mu.Unlock()
		return
	}
	p.s2cSeq++
	seq := p.s2cSeq
	data := EncodeReliableMsgData(seq, inner)
	reliableMsg := codec.NewExtMessage(ExtCmdReliableMsg, data)
	slot := seq % s2cBufSize
	p.s2cBuf[slot] = cachedS2CMsg{seq: seq, msg: reliableMsg}
	if seq >= s2cBufSize {
		p.s2cBufHead = seq - s2cBufSize + 1
	}
	conn := p.Conn
	state := p.State
	r.mu.Unlock()

	if conn != nil && state == PlayerOnline {
		_ = conn.Send(reliableMsg)
	}
}

// BroadcastReliable 向房间内所有在线玩家（可选排除一个）广播可靠消息
func (r *Room) BroadcastReliable(excludePlayerId int32, inner *codec.Message) {
	r.mu.Lock()
	sp := playerSlicePool.Get().(*[]*Player)
	players := (*sp)[:0]
	for _, p := range r.players {
		if p.ID != excludePlayerId {
			players = append(players, p)
		}
	}
	r.mu.Unlock()

	for _, p := range players {
		r.SendReliableToPlayer(p.ID, inner)
	}

	for i := range players {
		players[i] = nil
	}
	if cap(players) <= 64 {
		*sp = players[:0]
		playerSlicePool.Put(sp)
	}
}

// GetS2CReliableSince 获取 seq > afterSeq 的所有缓冲消息（用于重连补发）
// 返回 stale=true 表示 afterSeq 已超出缓冲区，必须降级到 Snapshot 重连。
func (r *Room) GetS2CReliableSince(playerId int32, afterSeq uint32) (msgs []*codec.Message, stale bool) {
	r.mu.Lock()
	defer r.mu.Unlock()
	p, ok := r.players[playerId]
	if !ok {
		return nil, false
	}
	if p.s2cSeq == 0 || afterSeq >= p.s2cSeq {
		return nil, false // 没有需要补发的消息
	}
	if afterSeq < p.s2cBufHead && p.s2cSeq > 0 {
		return nil, true // stale：请求的起点已被覆盖
	}
	count := p.s2cSeq - afterSeq
	result := make([]*codec.Message, 0, count)
	for seq := afterSeq + 1; seq <= p.s2cSeq; seq++ {
		slot := seq % s2cBufSize
		if p.s2cBuf[slot].seq == seq {
			result = append(result, p.s2cBuf[slot].msg)
		}
	}
	return result, false
}

// GetLastProcessedC2SSeq 获取已处理的最新 C→S reliable seq（用于重连时告知客户端）
func (r *Room) GetLastProcessedC2SSeq(playerId int32) uint32 {
	r.mu.Lock()
	defer r.mu.Unlock()
	p, ok := r.players[playerId]
	if !ok {
		return 0
	}
	return p.lastProcessedC2SSeq
}

// SetLastProcessedC2SSeq 更新已处理的 C→S reliable seq（去重用）
func (r *Room) SetLastProcessedC2SSeq(playerId int32, seq uint32) {
	r.mu.Lock()
	defer r.mu.Unlock()
	if p, ok := r.players[playerId]; ok {
		p.lastProcessedC2SSeq = seq
	}
}

// PlayerInfo GM 用的玩家信息快照
type PlayerInfo struct {
	ID             int32       `json:"id"`
	State          PlayerState `json:"state"` // 0=online, 1=disconnected
	DisconnectTime int64       `json:"disconnect_time,omitempty"` // unix ms, 0=online
	JoinedAt       int64       `json:"joined_at,omitempty"`       // unix ms，加入时间
}

// ForEachPlayer 遍历所有玩家（含离线），用于 GM 查询
func (r *Room) ForEachPlayer(fn func(info PlayerInfo)) {
	r.mu.Lock()
	infos := make([]PlayerInfo, 0, len(r.players))
	for _, p := range r.players {
		info := PlayerInfo{ID: p.ID, State: p.State, JoinedAt: p.JoinedAt.UnixMilli()}
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
	if frameNumber <= r.snapshotFrame {
		r.mu.Unlock()
		return false
	}
	r.snapshotFrame = frameNumber
	r.snapshotData = make([]byte, len(data))
	copy(r.snapshotData, data)
	r.snapshotStaleFrames = 0

	wasPaused := r.snapshotPaused
	if wasPaused {
		r.snapshotPaused = false
		slog.Info("snapshot received, resuming frame sync", "roomId", r.ID)
	}
	d := r.delegate // M3: 锁内捕获 delegate，消除锁外读取 r.delegate 的 TOCTOU
	r.mu.Unlock()

	if wasPaused {
		r.broadcast(codec.NewExtMessage(ExtCmdFrameSyncResumed, nil))
		if d != nil {
			d.OnRoomResumed(r)
		}
	}

	slog.Info("snapshot updated", "roomId", r.ID, "frame", frameNumber, "bytes", len(data))
	return true
}

// GetSnapshot 获取最新快照
func (r *Room) GetSnapshot() (frameNumber uint32, data []byte) {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.snapshotFrame, r.snapshotData
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

// IsSnapshotPaused 是否因快照过期而暂停
func (r *Room) IsSnapshotPaused() bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.snapshotPaused
}

// CreatedAt 返回房间创建时间
func (r *Room) CreatedAt() time.Time { return r.createdAt }

// HadPlayer 是否有过玩家加入（原子读，无锁）
func (r *Room) HadPlayer() bool {
	return r.hadPlayer.Load()
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

	for {
		select {
		case <-r.stopCh:
			return
		case <-ticker.C:
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

	// M1: 编码在锁外执行（inputs/events 已局部持有）
	frame := &FrameData{FrameNumber: frameNum, Inputs: inputs, Events: events}
	size := FrameDataSize(frame)
	if cap(r.frameBuf) < size {
		r.frameBuf = make([]byte, size)
	} else {
		r.frameBuf = r.frameBuf[:size]
	}
	EncodeFrameData(frame, r.frameBuf)

	// 写入环形缓冲区，同时向每个玩家的 delivery channel 投递（单写者原则：只有各自的 deliveryLoop 写 conn）
	r.mu.Lock()
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
				select {
				case <-ctx.Done():
					return
				case <-time.After(replayBatchDelay):
				}
			}
		}
	}

	// 阶段 2：实时 — 从 channel 读取 stepFrame 投递的帧；跳过阶段 1 已发送的帧（防重）
	for {
		select {
		case <-ctx.Done():
			return
		case cf, ok := <-frameCh:
			if !ok {
				return
			}
			if cf.FrameNumber <= p.cursor {
				continue // 阶段 1 已发，跳过
			}
			if err := conn.Send(codec.NewCoreMessage(CmdPushFrames, cf.EncodedData)); err != nil {
				return
			}
			p.cursor = cf.FrameNumber
		}
	}
}

// onDeliveryExit 在 deliveryLoop 退出时调用。
// 若玩家仍在线（ctx 非正常取消，即 conn 发送失败），关闭 conn 并标记断线。
// DisconnectPlayer 幂等：transport 的 onClientDisconnect 回调也可能调用它，无副作用。
func (r *Room) onDeliveryExit(p *Player, conn PlayerConn) {
	r.mu.Lock()
	isOnline := p.State == PlayerOnline
	r.mu.Unlock()
	if isOnline && conn != nil {
		conn.Close() //nolint:errcheck — 触发 transport.OnDisconnect → onClientDisconnect
		r.DisconnectPlayer(p.ID)
	}
}

// ReconcilePlayers 收敛玩家期望状态：通过 removePlayerLocked 移除所有超过 keepalive 的断线玩家
// 同时入队帧事件通知同房玩家，返回被移除的玩家 ID（供 Reconciler 回调 delegate）
func (r *Room) ReconcilePlayers() []int32 {
	r.mu.Lock()
	var evicted []int32
	now := time.Now()
	for id, p := range r.players {
		if p.State == PlayerDisconnected && now.Sub(p.DisconnectTime) > r.config.DisconnectKeepAlive {
			r.removePlayerLocked(id)
			evicted = append(evicted, id)
		}
	}
	r.mu.Unlock()

	for _, id := range evicted {
		if r.IsRunning() {
			r.EnqueueEvent(FrameEventPlayerLeft, id)
		} else {
			r.broadcast(codec.NewExtMessage(ExtCmdPlayerLeft, EncodePlayerId(id)))
		}
	}
	return evicted
}

// ShouldDestroy 期望状态检查：房间是否空置超过 grace 时长，应当被销毁
func (r *Room) ShouldDestroy(grace time.Duration) bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	return !r.emptyAt.IsZero() && time.Since(r.emptyAt) > grace
}

// EmptyAt 返回房间最近一次变空的时刻（零值表示当前有玩家）
func (r *Room) EmptyAt() time.Time {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.emptyAt
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

// === 轻量状态同步 KV 存储 ===

// SetData 设置/删除 KV 数据，返回新版本号
// value == nil 表示删除
func (r *Room) SetData(playerId int32, key int32, value []byte) uint32 {
	r.mu.Lock()
	defer r.mu.Unlock()
	compositeKey := DataStoreKey(playerId, key)
	if value == nil {
		delete(r.dataStore, compositeKey)
	} else {
		r.dataStore[compositeKey] = DataEntry{
			PlayerId: playerId,
			Key:      key,
			Value:    value,
		}
	}
	r.dataVersion++
	return r.dataVersion
}

// GetDataSnapshot 获取全量 KV 快照 + 当前版本号
func (r *Room) GetDataSnapshot() ([]DataEntry, uint32) {
	r.mu.Lock()
	defer r.mu.Unlock()
	entries := make([]DataEntry, 0, len(r.dataStore))
	for _, e := range r.dataStore {
		entries = append(entries, e)
	}
	return entries, r.dataVersion
}

// ClearPlayerData 清除指定玩家的所有 KV 数据
// 返回被删除的条目（用于广播删除）和每次删除后的版本号
func (r *Room) ClearPlayerData(playerId int32) ([]DataEntry, []uint32) {
	r.mu.Lock()
	defer r.mu.Unlock()
	var deleted []DataEntry
	var versions []uint32
	for k, e := range r.dataStore {
		if e.PlayerId == playerId {
			deleted = append(deleted, e)
			delete(r.dataStore, k)
			r.dataVersion++
			versions = append(versions, r.dataVersion)
		}
	}
	return deleted, versions
}

// DataVersion 获取当前数据版本号
func (r *Room) DataVersion() uint32 {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.dataVersion
}

// DataStoreEmpty 检查数据存储是否为空
func (r *Room) DataStoreEmpty() bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	return len(r.dataStore) == 0
}

// ===================== Desync Detection =====================

// ReportFrameHash 客户端上报帧 hash，检测不同步
// Returns true if desync detected
func (r *Room) ReportFrameHash(playerId int32, frameNumber uint32, hash uint32) bool {
	r.mu.Lock()
	defer r.mu.Unlock()

	if r.desyncDetected || !r.running {
		return false
	}

	if r.frameHashes[frameNumber] == nil {
		r.frameHashes[frameNumber] = make(map[int32]uint32)
	}
	r.frameHashes[frameNumber][playerId] = hash

	// Check for mismatch: compare against any existing hash for this frame
	hashes := r.frameHashes[frameNumber]
	if len(hashes) >= 2 {
		var firstHash uint32
		first := true
		for _, h := range hashes {
			if first {
				firstHash = h
				first = false
				continue
			}
			if h != firstHash {
				r.desyncDetected = true
				r.frameHashes = make(map[uint32]map[int32]uint32) // M9: 释放内存，检测完成后无需保留
				return true
			}
		}
	}

	// Clean up old frame hashes (keep only last 200 frames).
	// 周期性清理：每 100 帧触发一次，避免每次 ReportFrameHash 都 O(n) 扫描全表。
	if frameNumber > 200 && frameNumber%100 == 0 {
		cutoff := frameNumber - 200
		for fn := range r.frameHashes {
			if fn < cutoff {
				delete(r.frameHashes, fn)
			}
		}
	}

	return false
}

// GetFrameHashes returns hashes for a specific frame (for logging)
func (r *Room) GetFrameHashes(frameNumber uint32) map[int32]uint32 {
	r.mu.Lock()
	defer r.mu.Unlock()
	result := make(map[int32]uint32)
	if hashes, ok := r.frameHashes[frameNumber]; ok {
		for k, v := range hashes {
			result[k] = v
		}
	}
	return result
}

// ===================== GM Inspect Accessors =====================

// EntityAuthorityEntry 实体权威条目（GM 检视用）
type EntityAuthorityEntry struct {
	EntityId int32
	OwnerId  int32
}

// DataStoreEntry KV 数据条目（GM 检视用）
type DataStoreEntry struct {
	PlayerId int32
	Key      int32
	Value    []byte
}

// GetEntityAuthority 获取当前实体权威表快照
func (r *Room) GetEntityAuthority() []EntityAuthorityEntry {
	r.mu.Lock()
	defer r.mu.Unlock()
	entries := make([]EntityAuthorityEntry, 0, len(r.entityAuthority))
	for eid, owner := range r.entityAuthority {
		entries = append(entries, EntityAuthorityEntry{EntityId: eid, OwnerId: owner})
	}
	return entries
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

// SnapshotFrame 最新快照对应的帧号
func (r *Room) SnapshotFrame() uint32 {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.snapshotFrame
}

// SnapshotSize 最新快照数据大小（字节）
func (r *Room) SnapshotSize() int {
	r.mu.Lock()
	defer r.mu.Unlock()
	return len(r.snapshotData)
}

// SnapshotStaleFrames 自上次快照以来经过的帧数
func (r *Room) SnapshotStaleFrames() uint32 {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.snapshotStaleFrames
}

// GetDataStoreEntries 获取 KV 数据仓全量快照
func (r *Room) GetDataStoreEntries() []DataStoreEntry {
	r.mu.Lock()
	defer r.mu.Unlock()
	entries := make([]DataStoreEntry, 0, len(r.dataStore))
	for _, de := range r.dataStore {
		entries = append(entries, DataStoreEntry{
			PlayerId: de.PlayerId,
			Key:      de.Key,
			Value:    de.Value,
		})
	}
	return entries
}

// GetConfig 获取房间配置（只读）
func (r *Room) GetConfig() RoomConfig {
	return r.config
}

// PendingInputsLen 当前待处理输入队列深度（GM 检视用）
func (r *Room) PendingInputsLen() int {
	r.mu.Lock()
	defer r.mu.Unlock()
	return len(r.pendingInputs)
}

// StartedAt 帧同步启动时间；未启动时返回零值（GM 检视用）
func (r *Room) StartedAt() time.Time {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.startedAt
}

// IsDesyncDetected 是否已检测到 desync（GM 检视用）
func (r *Room) IsDesyncDetected() bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.desyncDetected
}
