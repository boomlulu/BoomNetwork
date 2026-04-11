package framesync

import (
	"context"
	"errors"
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

// ErrRoomFull 新玩家加入时房间已满
// NEW-04: AddPlayer 内部原子容量检查，消除外部 check-then-act TOCTOU。
var ErrRoomFull = errors.New("room is full")

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
	PlayerReplaying    PlayerState = 2   // 正在接收历史帧，不参与广播
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
	alive     atomic.Bool // 是否仍在 RoomManager 中（原子标志，消除热路径 RLock）
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
