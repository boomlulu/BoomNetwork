package framesync

import (
	"encoding/binary"
)

// Cmd 定义 (FlagsCmd 6bit, 范围 0-63)
const (
	CmdSessionBind    = 1
	CmdSessionBindRsp = 2
	CmdRequestStart   = 3  // 客户端 → 服务器：请求开始
	CmdStartFrameSync = 4  // 服务器 → 客户端：帧同步开始（广播）
	CmdStopFrameSync  = 21 // 服务器 → 客户端：帧同步结束
	CmdFrameInput     = 5
	CmdPushFrames     = 6

	CmdHeartbeat    = 7
	CmdHeartbeatRsp = 8

	CmdReconnect    = 9
	CmdReconnectRsp = 10

	// 房间管理
	CmdGetRooms      = 11
	CmdGetRoomsRsp   = 12
	CmdCreateRoom    = 13
	CmdCreateRoomRsp = 14
	CmdJoinRoom      = 15
	CmdJoinRoomRsp   = 16
	CmdLeaveRoom     = 17
	CmdLeaveRoomRsp  = 18

	// 服务器推送
	CmdPlayerJoined  = 19
	CmdPlayerLeft    = 20
	CmdPlayerOffline = 24 // 玩家断线（临时，可能重连）
	CmdPlayerOnline  = 25 // 玩家恢复在线（重连成功）
	CmdRoomSnapshot  = 26 // 服务器 → 客户端：下发房间快照（迟到者加入用）

	// 快照
	CmdUploadSnapshot    = 22 // 客户端 → 服务器：上传快照
	CmdUploadSnapshotRsp = 23 // 服务器 → 客户端：上传确认

	// 实体权威同步
	CmdSendEntityState = 27 // 客户端 → 服务器：管理者发送实体状态
	CmdPushEntityState = 28 // 服务器 → 客户端：广播实体状态（带 senderPid）

	// 匹配
	CmdMatchRoom    = 29 // 客户端 → 服务器：请求匹配房间（有空位就加入，否则创建）
	CmdMatchRoomRsp = 30 // 服务器 → 客户端：匹配结果（格式同 JoinRoomRsp）
)

// InitData 帧同步初始化数据
// Wire: [FrameRate:4][FrameInterval:4][StartTime:8][SnapshotInterval:4][QuickReconnectMaxMs:4]
type InitData struct {
	FrameRate            int32
	FrameInterval        int32 // ms
	StartTime            int64 // ms timestamp
	SnapshotInterval     int32 // 快照间隔（帧数），客户端按此频率上传快照
	QuickReconnectMaxMs  int32 // 快速重连最长重试时间（ms），超时后客户端降级到快照重连
}

const InitDataSize = 24

func EncodeInitData(d *InitData) []byte {
	buf := make([]byte, InitDataSize)
	binary.LittleEndian.PutUint32(buf[0:], uint32(d.FrameRate))
	binary.LittleEndian.PutUint32(buf[4:], uint32(d.FrameInterval))
	binary.LittleEndian.PutUint64(buf[8:], uint64(d.StartTime))
	binary.LittleEndian.PutUint32(buf[16:], uint32(d.SnapshotInterval))
	binary.LittleEndian.PutUint32(buf[20:], uint32(d.QuickReconnectMaxMs))
	return buf
}

func DecodeInitData(buf []byte) *InitData {
	d := &InitData{
		FrameRate:     int32(binary.LittleEndian.Uint32(buf[0:])),
		FrameInterval: int32(binary.LittleEndian.Uint32(buf[4:])),
		StartTime:     int64(binary.LittleEndian.Uint64(buf[8:])),
	}
	if len(buf) >= InitDataSize {
		d.SnapshotInterval = int32(binary.LittleEndian.Uint32(buf[16:]))
		d.QuickReconnectMaxMs = int32(binary.LittleEndian.Uint32(buf[20:]))
	}
	return d
}

// ReconnectResult 重连结果码
const (
	ReconnectSuccess         byte = 1 // 成功
	ReconnectFail            byte = 0 // 通用失败
	ReconnectFailBufferStale byte = 2 // lastFrame 已超出环形缓冲区，需要降级到快照重连
)

// PlayerInput 单个玩家输入
type PlayerInput struct {
	PlayerId int32
	Data     []byte
}

// FrameData 一帧数据
type FrameData struct {
	FrameNumber uint32
	Inputs      []PlayerInput
}

// EncodeFrameData 编码帧数据
// Wire: [FrameNumber:4][InputCount:2][Inputs...]
// 每个 Input: [PlayerId:4][DataLen:2][Data:N]
func EncodeFrameData(f *FrameData, buf []byte) int {
	offset := 0

	binary.LittleEndian.PutUint32(buf[offset:], f.FrameNumber)
	offset += 4

	inputCount := len(f.Inputs)
	binary.LittleEndian.PutUint16(buf[offset:], uint16(inputCount))
	offset += 2

	for _, input := range f.Inputs {
		binary.LittleEndian.PutUint32(buf[offset:], uint32(input.PlayerId))
		offset += 4

		dataLen := len(input.Data)
		binary.LittleEndian.PutUint16(buf[offset:], uint16(dataLen))
		offset += 2

		if dataLen > 0 {
			copy(buf[offset:], input.Data)
			offset += dataLen
		}
	}

	return offset
}

// DecodeFrameData 解码帧数据
func DecodeFrameData(buf []byte) *FrameData {
	offset := 0
	f := &FrameData{}

	f.FrameNumber = binary.LittleEndian.Uint32(buf[offset:])
	offset += 4

	inputCount := int(binary.LittleEndian.Uint16(buf[offset:]))
	offset += 2

	f.Inputs = make([]PlayerInput, inputCount)
	for i := 0; i < inputCount; i++ {
		f.Inputs[i].PlayerId = int32(binary.LittleEndian.Uint32(buf[offset:]))
		offset += 4

		dataLen := int(binary.LittleEndian.Uint16(buf[offset:]))
		offset += 2

		if dataLen > 0 {
			f.Inputs[i].Data = make([]byte, dataLen)
			copy(f.Inputs[i].Data, buf[offset:offset+dataLen])
			offset += dataLen
		}
	}

	return f
}

// FrameDataSize 计算编码后大小
func FrameDataSize(f *FrameData) int {
	size := 6 // FrameNumber(4) + InputCount(2)
	for _, input := range f.Inputs {
		size += 6 + len(input.Data) // PlayerId(4) + DataLen(2) + Data
	}
	return size
}

// === 房间协议编解码 ===

// RoomInfo 房间信息（用于列表展示）
type RoomInfo struct {
	RoomId      int32
	PlayerCount int
	MaxPlayers  int
	Running     bool
}

// EncodeRoomList 编码房间列表
// Wire: [Count:2] + N × [RoomId:4][PlayerCount:2][MaxPlayers:2][Running:1]
func EncodeRoomList(rooms []RoomInfo) []byte {
	buf := make([]byte, 2+len(rooms)*9)
	binary.LittleEndian.PutUint16(buf[0:], uint16(len(rooms)))
	offset := 2
	for _, r := range rooms {
		binary.LittleEndian.PutUint32(buf[offset:], uint32(r.RoomId))
		offset += 4
		binary.LittleEndian.PutUint16(buf[offset:], uint16(r.PlayerCount))
		offset += 2
		binary.LittleEndian.PutUint16(buf[offset:], uint16(r.MaxPlayers))
		offset += 2
		if r.Running {
			buf[offset] = 1
		}
		offset += 1
	}
	return buf
}

// EncodeJoinRoomRsp 编码加入房间响应
// Wire: [PlayerId:4][RoomId:4][PlayerCount:2][PlayerIds:4×N]
func EncodeJoinRoomRsp(playerId int32, roomId int32, existingPlayerIds []int32) []byte {
	buf := make([]byte, 8+2+len(existingPlayerIds)*4)
	binary.LittleEndian.PutUint32(buf[0:], uint32(playerId))
	binary.LittleEndian.PutUint32(buf[4:], uint32(roomId))
	binary.LittleEndian.PutUint16(buf[8:], uint16(len(existingPlayerIds)))
	offset := 10
	for _, pid := range existingPlayerIds {
		binary.LittleEndian.PutUint32(buf[offset:], uint32(pid))
		offset += 4
	}
	return buf
}

// EncodePlayerId 编码玩家 ID（PlayerJoined / PlayerLeft 推送用）
// Wire: [PlayerId:4]
func EncodePlayerId(playerId int32) []byte {
	buf := make([]byte, 4)
	binary.LittleEndian.PutUint32(buf[0:], uint32(playerId))
	return buf
}

// EncodeSnapshot 编码快照（服务器下发 RoomSnapshot 也复用此格式）
// Wire: [FrameNumber:4][SnapshotData:N]
func EncodeSnapshot(frameNumber uint32, snapshotData []byte) []byte {
	buf := make([]byte, 4+len(snapshotData))
	binary.LittleEndian.PutUint32(buf[0:], frameNumber)
	copy(buf[4:], snapshotData)
	return buf
}

// DecodeUploadSnapshot 解码客户端上传的快照
// Wire: [FrameNumber:4][SnapshotData:N]
func DecodeUploadSnapshot(data []byte) (frameNumber uint32, snapshotData []byte) {
	if len(data) < 4 {
		return 0, nil
	}
	frameNumber = binary.LittleEndian.Uint32(data[0:4])
	if len(data) > 4 {
		snapshotData = make([]byte, len(data)-4)
		copy(snapshotData, data[4:])
	}
	return
}

// EncodeReconnectRsp 编码重连响应
// Wire: [Result:1][RoomId:4][ServerFrame:4][SnapshotFrame:4][SnapshotData:N]
// Result: 0=失败, 1=成功, 2=缓冲区过期(需降级到快照重连)
func EncodeReconnectRsp(result byte, roomId int32, serverFrame uint32, snapshotFrame uint32, snapshotData []byte) []byte {
	headerSize := 1 + 4 + 4 + 4 // result + roomId + serverFrame + snapshotFrame
	buf := make([]byte, headerSize+len(snapshotData))

	buf[0] = result
	binary.LittleEndian.PutUint32(buf[1:], uint32(roomId))
	binary.LittleEndian.PutUint32(buf[5:], serverFrame)
	binary.LittleEndian.PutUint32(buf[9:], snapshotFrame)

	if len(snapshotData) > 0 {
		copy(buf[13:], snapshotData)
	}
	return buf
}
