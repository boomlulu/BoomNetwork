package framesync

import (
	"encoding/binary"
)

// Cmd 定义
const (
	CmdSessionBind    = 10
	CmdSessionBindRsp = 11
	CmdStartFrameSync = 20
	CmdStopFrameSync  = 21
	CmdFrameInput     = 30
	CmdPushFrames     = 31

	CmdHeartbeat      = 40
	CmdHeartbeatRsp   = 41

	CmdReconnect      = 50
	CmdReconnectRsp   = 51

	// 房间管理
	CmdGetRooms      = 60
	CmdGetRoomsRsp   = 61
	CmdCreateRoom    = 62
	CmdCreateRoomRsp = 63
	CmdJoinRoom      = 64
	CmdJoinRoomRsp   = 65
	CmdLeaveRoom     = 66
	CmdLeaveRoomRsp  = 67

	// 服务器推送
	CmdPlayerJoined = 70
	CmdPlayerLeft   = 71
)

// InitData 帧同步初始化数据
// Wire: [FrameRate:4][FrameInterval:4][StartTime:8]
type InitData struct {
	FrameRate     int32
	FrameInterval int32 // ms
	StartTime     int64 // ms timestamp
}

const InitDataSize = 16

func EncodeInitData(d *InitData) []byte {
	buf := make([]byte, InitDataSize)
	binary.LittleEndian.PutUint32(buf[0:], uint32(d.FrameRate))
	binary.LittleEndian.PutUint32(buf[4:], uint32(d.FrameInterval))
	binary.LittleEndian.PutUint64(buf[8:], uint64(d.StartTime))
	return buf
}

func DecodeInitData(buf []byte) *InitData {
	return &InitData{
		FrameRate:     int32(binary.LittleEndian.Uint32(buf[0:])),
		FrameInterval: int32(binary.LittleEndian.Uint32(buf[4:])),
		StartTime:     int64(binary.LittleEndian.Uint64(buf[8:])),
	}
}

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
// Wire: [PlayerId:4][RoomId:4]
func EncodeJoinRoomRsp(playerId int32, roomId int32) []byte {
	buf := make([]byte, 8)
	binary.LittleEndian.PutUint32(buf[0:], uint32(playerId))
	binary.LittleEndian.PutUint32(buf[4:], uint32(roomId))
	return buf
}

// EncodePlayerId 编码玩家 ID（PlayerJoined / PlayerLeft 推送用）
// Wire: [PlayerId:4]
func EncodePlayerId(playerId int32) []byte {
	buf := make([]byte, 4)
	binary.LittleEndian.PutUint32(buf[0:], uint32(playerId))
	return buf
}
