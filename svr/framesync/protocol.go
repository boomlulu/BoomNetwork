package framesync

import (
	"encoding/binary"
)

// === Cmd 定义（三层分级）===
//
// FlagsCmd byte: bit 0=LenSize, bit 1=HasSeq, bit 2-3=CmdType, bit 4-7=CoreCmd
//
// Core (CmdType=0):     高频核心命令, CoreCmd 0-15, 包头 3B
// Extended (CmdType=1): 框架扩展命令, ExtCmd uint16, 包头 5B
// Game (CmdType=2):     游戏自定义命令, GameCmd uint32, 包头 7B, 服务器透传

// --- Core Cmd (0-15) ---
const (
	CmdSessionBind    byte = 1
	CmdSessionBindRsp byte = 2
	CmdRequestStart   byte = 3  // 客户端 → 服务器：请求开始
	CmdStartFrameSync byte = 4  // 服务器 → 客户端：帧同步开始（广播）
	CmdStopFrameSync  byte = 5  // 双向：帧同步结束
	CmdFrameInput     byte = 6
	CmdPushFrames     byte = 7
	CmdHeartbeat      byte = 8
	CmdHeartbeatRsp   byte = 9
	CmdReconnect        byte = 10
	CmdReconnectRsp     byte = 11
	CmdServerShutdown   byte = 12 // 服务器 → 客户端：服务器即将关闭
)

// --- Extended Cmd (uint16) ---
const (
	// 房间管理
	ExtCmdGetRooms      uint16 = 1
	ExtCmdGetRoomsRsp   uint16 = 2
	ExtCmdCreateRoom    uint16 = 3
	ExtCmdCreateRoomRsp uint16 = 4
	ExtCmdJoinRoom      uint16 = 5
	ExtCmdJoinRoomRsp   uint16 = 6
	ExtCmdLeaveRoom     uint16 = 7
	ExtCmdLeaveRoomRsp  uint16 = 8
	ExtCmdMatchRoom     uint16 = 9  // 匹配房间
	ExtCmdMatchRoomRsp  uint16 = 10 // 匹配结果

	// 服务器推送
	ExtCmdPlayerJoined  uint16 = 20
	ExtCmdPlayerLeft    uint16 = 21
	ExtCmdPlayerOffline uint16 = 22 // 玩家断线（临时）
	ExtCmdPlayerOnline  uint16 = 23 // 玩家恢复在线
	ExtCmdRoomSnapshot  uint16 = 24 // 下发房间快照

	// 快照
	ExtCmdUploadSnapshot    uint16 = 30
	ExtCmdUploadSnapshotRsp uint16 = 31

	// 实体权威同步
	ExtCmdSendEntityState uint16 = 40 // 管理者发送实体状态
	ExtCmdPushEntityState uint16 = 41 // 广播实体状态（带 senderPid）

	// 权威转移（双向，用 Data[0] 区分 request/result）
	ExtCmdAuthorityTransfer uint16 = 42
	// Data[0]=0: C→S 请求 [0:1][entityId:4][release:1]
	// Data[0]=1: S→C 结果 [1:1][entityId:4][newOwner:4]

	// 轻量状态同步（帧同步未运行时的通信通道）
	ExtCmdSendStateMsg    uint16 = 50 // C→S 状态消息（服务器转发）
	ExtCmdPushStateMsg    uint16 = 51 // S→C 转发状态消息
	ExtCmdSetData         uint16 = 52 // C→S 设置 KV 数据
	ExtCmdPushData        uint16 = 53 // S→C 增量广播 KV 变更
	ExtCmdRequestDataSync uint16 = 54 // C→S 请求全量 KV 同步
	ExtCmdPushDataSync    uint16 = 55 // S→C 全量 KV 快照
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
	MatchKey    string
}

// EncodeRoomList 编码房间列表
// Wire: [Count:2] + N × [RoomId:4][PlayerCount:2][MaxPlayers:2][Running:1][MatchKeyLen:2][MatchKey:N]
func EncodeRoomList(rooms []RoomInfo) []byte {
	// 计算总长度（变长）
	size := 2
	for _, r := range rooms {
		size += 9 + 2 + len(r.MatchKey)
	}
	buf := make([]byte, size)
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
		binary.LittleEndian.PutUint16(buf[offset:], uint16(len(r.MatchKey)))
		offset += 2
		copy(buf[offset:], r.MatchKey)
		offset += len(r.MatchKey)
	}
	return buf
}

// JoinRoomResult 加入房间结果码
type JoinRoomResult byte

const (
	JoinRoomSuccess  JoinRoomResult = 0
	JoinRoomNotFound JoinRoomResult = 1
	JoinRoomFull     JoinRoomResult = 2
	JoinRoomNotBound JoinRoomResult = 3 // SessionBind 未调用
	JoinRoomBadData  JoinRoomResult = 4 // 请求数据格式错误
)

// EncodeJoinRoomError 编码加入房间失败响应
// Wire: [PlayerId=0:4][ErrorCode:1][0,0,0] — 8 字节
// PlayerId=0 表示失败（与旧版兼容），ErrorCode 在 byte[4] 区分原因
func EncodeJoinRoomError(result JoinRoomResult) []byte {
	buf := make([]byte, 8)
	buf[4] = byte(result)
	return buf
}

// EncodeJoinRoomRsp 编码加入房间成功响应
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

// === 权威转移编解码 ===

// DecodeAuthorityTransferRequest 解码权威转移请求
// Wire: [entityId:4][release:1]
func DecodeAuthorityTransferRequest(data []byte) (entityId int32, release bool, ok bool) {
	if len(data) < 5 {
		return 0, false, false
	}
	entityId = int32(binary.LittleEndian.Uint32(data[0:4]))
	release = data[4] != 0
	return entityId, release, true
}

// EncodeAuthorityTransferResult 编码权威转移结果广播
// Wire: [entityId:4][newOwnerPlayerId:4]
func EncodeAuthorityTransferResult(entityId int32, newOwnerPlayerId int32) []byte {
	buf := make([]byte, 8)
	binary.LittleEndian.PutUint32(buf[0:], uint32(entityId))
	binary.LittleEndian.PutUint32(buf[4:], uint32(newOwnerPlayerId))
	return buf
}

// === 轻量状态同步编解码 ===

// DataEntry KV 存储条目
type DataEntry struct {
	PlayerId int32
	Key      int32
	Value    []byte
}

// DataStoreKey 复合键: int64(playerId)<<32 | int64(uint32(key))
func DataStoreKey(playerId int32, key int32) int64 {
	return int64(playerId)<<32 | int64(uint32(key))
}

// EncodePushStateMsg 编码 PushStateMsg
// Wire: [PlayerId:4][Data:N]
func EncodePushStateMsg(playerId int32, data []byte) []byte {
	buf := make([]byte, 4+len(data))
	binary.LittleEndian.PutUint32(buf[0:], uint32(playerId))
	copy(buf[4:], data)
	return buf
}

// DecodeSetData 解码 SetData
// Wire: [Key:4][ValueLen:2][Value:N]
func DecodeSetData(data []byte) (key int32, value []byte, ok bool) {
	if len(data) < 6 {
		return 0, nil, false
	}
	key = int32(binary.LittleEndian.Uint32(data[0:4]))
	valueLen := int(binary.LittleEndian.Uint16(data[4:6]))
	if valueLen == 0 {
		return key, nil, true // delete
	}
	if len(data) < 6+valueLen {
		return 0, nil, false
	}
	value = make([]byte, valueLen)
	copy(value, data[6:6+valueLen])
	return key, value, true
}

// EncodePushData 编码 PushData (增量)
// Wire: [Version:4][PlayerId:4][Key:4][ValueLen:2][Value:N]
func EncodePushData(version uint32, playerId int32, key int32, value []byte) []byte {
	valueLen := len(value)
	buf := make([]byte, 14+valueLen)
	binary.LittleEndian.PutUint32(buf[0:], version)
	binary.LittleEndian.PutUint32(buf[4:], uint32(playerId))
	binary.LittleEndian.PutUint32(buf[8:], uint32(key))
	binary.LittleEndian.PutUint16(buf[12:], uint16(valueLen))
	if valueLen > 0 {
		copy(buf[14:], value)
	}
	return buf
}

// EncodePushDataSync 编码 PushDataSync (全量快照)
// Wire: [Version:4][EntryCount:2] + N × [PlayerId:4][Key:4][ValueLen:2][Value:N]
func EncodePushDataSync(version uint32, entries []DataEntry) []byte {
	size := 6 // Version(4) + EntryCount(2)
	for _, e := range entries {
		size += 10 + len(e.Value) // PlayerId(4) + Key(4) + ValueLen(2) + Value
	}
	buf := make([]byte, size)
	binary.LittleEndian.PutUint32(buf[0:], version)
	binary.LittleEndian.PutUint16(buf[4:], uint16(len(entries)))
	offset := 6
	for _, e := range entries {
		binary.LittleEndian.PutUint32(buf[offset:], uint32(e.PlayerId))
		offset += 4
		binary.LittleEndian.PutUint32(buf[offset:], uint32(e.Key))
		offset += 4
		binary.LittleEndian.PutUint16(buf[offset:], uint16(len(e.Value)))
		offset += 2
		if len(e.Value) > 0 {
			copy(buf[offset:], e.Value)
			offset += len(e.Value)
		}
	}
	return buf
}
