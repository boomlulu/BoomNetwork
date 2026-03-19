package framesync

import (
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
	PlayerDisconnected PlayerState = 1 // 断线但保留位置
)

// Player 房间内的玩家
type Player struct {
	ID             int32
	Conn           PlayerConn
	State          PlayerState
	DisconnectTime time.Time // 断线时间
}

// CachedFrame 缓冲的帧数据（已编码）
type CachedFrame struct {
	FrameNumber uint32
	EncodedData []byte // 已编码的 FrameData
}

// RoomConfig 房间配置
type RoomConfig struct {
	FrameRate            int32         // 帧率
	FrameBufferSize      int           // 帧缓冲区大小（保留最近 N 帧）
	DisconnectKeepAlive  time.Duration // 断线保留时间
}

// DefaultRoomConfig 默认配置
func DefaultRoomConfig() RoomConfig {
	return RoomConfig{
		FrameRate:           20,
		FrameBufferSize:     200, // 20fps × 10s = 200 帧
		DisconnectKeepAlive: 30 * time.Second,
	}
}

// Room 帧同步房间
type Room struct {
	mu      sync.Mutex
	players map[int32]*Player
	config  RoomConfig

	frameRate     int32
	frameInterval time.Duration
	startTime     int64
	frameNumber   uint32
	running       bool
	stopCh        chan struct{}

	pendingInputs []PlayerInput

	// 帧缓冲区：保留最近 N 帧，支持重连时重发
	frameBuffer []CachedFrame

	// 编码缓冲区复用
	frameBuf []byte
}

// NewRoom 创建帧同步房间
func NewRoom(frameRate int32) *Room {
	return NewRoomWithConfig(RoomConfig{
		FrameRate:           frameRate,
		FrameBufferSize:     int(frameRate) * 10, // 默认 10 秒
		DisconnectKeepAlive: 30 * time.Second,
	})
}

// NewRoomWithConfig 用配置创建房间
func NewRoomWithConfig(config RoomConfig) *Room {
	return &Room{
		players:       make(map[int32]*Player),
		config:        config,
		frameRate:     config.FrameRate,
		frameInterval: time.Duration(1000/config.FrameRate) * time.Millisecond,
		frameBuffer:   make([]CachedFrame, 0, config.FrameBufferSize),
		frameBuf:      make([]byte, 4096),
	}
}

// AddPlayer 添加玩家（新加入或重连替换连接）
func (r *Room) AddPlayer(id int32, conn PlayerConn) {
	r.mu.Lock()
	if existing, ok := r.players[id]; ok {
		// 重连：替换连接，恢复在线状态
		existing.Conn = conn
		existing.State = PlayerOnline
	} else {
		r.players[id] = &Player{ID: id, Conn: conn, State: PlayerOnline}
	}
	r.mu.Unlock()
}

// DisconnectPlayer 标记玩家断线（保留位置，不立即移除）
func (r *Room) DisconnectPlayer(id int32) {
	r.mu.Lock()
	if p, ok := r.players[id]; ok {
		p.State = PlayerDisconnected
		p.DisconnectTime = time.Now()
		p.Conn = nil
	}
	r.mu.Unlock()
}

// RemovePlayer 彻底移除玩家
func (r *Room) RemovePlayer(id int32) {
	r.mu.Lock()
	delete(r.players, id)
	r.mu.Unlock()
}

// PlayerCount 在线玩家数量
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

// TotalPlayerCount 总玩家数（含断线保留）
func (r *Room) TotalPlayerCount() int {
	r.mu.Lock()
	defer r.mu.Unlock()
	return len(r.players)
}

// CurrentFrameNumber 当前帧号
func (r *Room) CurrentFrameNumber() uint32 {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.frameNumber
}

// GetFramesSince 获取从 afterFrame 之后的所有缓冲帧
// 用于快速重连时重发缺失的帧
func (r *Room) GetFramesSince(afterFrame uint32) []CachedFrame {
	r.mu.Lock()
	defer r.mu.Unlock()

	var result []CachedFrame
	for _, cf := range r.frameBuffer {
		if cf.FrameNumber > afterFrame {
			// 拷贝数据，避免被后续帧覆盖
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
	r.stopCh = make(chan struct{})
	r.mu.Unlock()

	initData := &InitData{
		FrameRate:     r.frameRate,
		FrameInterval: int32(r.frameInterval.Milliseconds()),
		StartTime:     r.startTime,
	}
	r.broadcast(CmdStartFrameSync, EncodeInitData(initData))

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

	r.broadcast(CmdStopFrameSync, nil)
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
	ticker := time.NewTicker(r.frameInterval)
	defer ticker.Stop()

	// 定期清理断线超时的玩家
	cleanupTicker := time.NewTicker(5 * time.Second)
	defer cleanupTicker.Stop()

	for {
		select {
		case <-r.stopCh:
			return
		case <-ticker.C:
			r.stepFrame()
		case <-cleanupTicker.C:
			r.cleanupDisconnected()
		}
	}
}

func (r *Room) stepFrame() {
	r.mu.Lock()
	r.frameNumber++

	inputs := r.pendingInputs
	r.pendingInputs = nil
	r.mu.Unlock()

	frame := &FrameData{
		FrameNumber: r.frameNumber,
		Inputs:      inputs,
	}

	size := FrameDataSize(frame)
	if cap(r.frameBuf) < size {
		r.frameBuf = make([]byte, size)
	} else {
		r.frameBuf = r.frameBuf[:size]
	}
	EncodeFrameData(frame, r.frameBuf)

	// 缓存帧数据
	encoded := make([]byte, size)
	copy(encoded, r.frameBuf[:size])

	r.mu.Lock()
	r.frameBuffer = append(r.frameBuffer, CachedFrame{
		FrameNumber: r.frameNumber,
		EncodedData: encoded,
	})
	// 超出缓冲区上限，丢弃最早的
	if len(r.frameBuffer) > r.config.FrameBufferSize {
		r.frameBuffer = r.frameBuffer[len(r.frameBuffer)-r.config.FrameBufferSize:]
	}
	r.mu.Unlock()

	r.broadcast(CmdPushFrames, r.frameBuf[:size])
}

// cleanupDisconnected 清理断线超时的玩家
func (r *Room) cleanupDisconnected() {
	r.mu.Lock()
	defer r.mu.Unlock()

	now := time.Now()
	for id, p := range r.players {
		if p.State == PlayerDisconnected && now.Sub(p.DisconnectTime) > r.config.DisconnectKeepAlive {
			delete(r.players, id)
		}
	}
}

// broadcast 广播消息给所有在线玩家
func (r *Room) broadcast(cmd byte, data []byte) {
	r.mu.Lock()
	players := make([]*Player, 0, len(r.players))
	for _, p := range r.players {
		if p.State == PlayerOnline && p.Conn != nil {
			players = append(players, p)
		}
	}
	r.mu.Unlock()

	msg := &codec.Message{
		Cmd:  cmd,
		Data: data,
	}
	for _, p := range players {
		p.Conn.Send(msg)
	}
}
