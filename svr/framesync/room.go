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

// Player 房间内的玩家
type Player struct {
	ID   int32
	Conn PlayerConn
}

// Room 帧同步房间
// 负责: 收集玩家输入 → 按固定帧率组帧 → 广播给所有玩家
type Room struct {
	mu      sync.Mutex
	players map[int32]*Player

	frameRate     int32
	frameInterval time.Duration
	startTime     int64
	frameNumber   uint32
	running       bool
	stopCh        chan struct{}

	// 当前帧收集到的输入
	pendingInputs []PlayerInput

	// 编码缓冲区复用
	frameBuf []byte
}

// NewRoom 创建帧同步房间
func NewRoom(frameRate int32) *Room {
	return &Room{
		players:       make(map[int32]*Player),
		frameRate:     frameRate,
		frameInterval: time.Duration(1000/frameRate) * time.Millisecond,
		frameBuf:      make([]byte, 4096),
	}
}

// AddPlayer 添加玩家到房间
func (r *Room) AddPlayer(id int32, conn PlayerConn) {
	r.mu.Lock()
	r.players[id] = &Player{ID: id, Conn: conn}
	r.mu.Unlock()
}

// RemovePlayer 移除玩家
func (r *Room) RemovePlayer(id int32) {
	r.mu.Lock()
	delete(r.players, id)
	r.mu.Unlock()
}

// PlayerCount 玩家数量
func (r *Room) PlayerCount() int {
	r.mu.Lock()
	defer r.mu.Unlock()
	return len(r.players)
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

	// 广播 StartFrameSync
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

// tickLoop 帧驱动循环
func (r *Room) tickLoop() {
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

// stepFrame 执行一帧：收集输入 → 组帧 → 广播
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

	r.broadcast(CmdPushFrames, r.frameBuf[:size])
}

// broadcast 广播消息给所有玩家
func (r *Room) broadcast(cmd byte, data []byte) {
	r.mu.Lock()
	players := make([]*Player, 0, len(r.players))
	for _, p := range r.players {
		players = append(players, p)
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
