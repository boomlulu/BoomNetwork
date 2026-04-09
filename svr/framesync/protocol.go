package framesync

import (
	"encoding/binary"
	"fmt"

	"github.com/boomlulu/boomnetwork/codec"
)

// Protocol decode limits — prevent OOM / panic from malformed input
const (
	maxFrameInputs  = 256
	maxInputDataLen = 4096
	maxFrameEvents  = 32
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
	CmdServerShutdown     byte = 12 // 服务器 → 客户端：服务器即将关闭
	CmdRateLimitWarning   byte = 13 // 服务器 → 客户端：消息速率接近上限，请降速
	CmdKicked             byte = 14 // 服务器 → 客户端：你已被踢出 [Reason:1]
)

// --- Kick Reason ---
const (
	KickReasonRateLimit byte = 1 // 消息频率超限
	KickReasonAdmin     byte = 2 // 管理员手动踢出
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

	// 帧同步暂停/恢复
	ExtCmdFrameSyncPaused  uint16 = 56 // S→C [Reason:1]
	ExtCmdFrameSyncResumed uint16 = 57 // S→C (empty body)

	// 游戏级暂停（客户端请求）
	ExtCmdRequestGamePause  uint16 = 58 // C→S (empty body)
	ExtCmdRequestGameResume uint16 = 59 // C→S (empty body)

	// 帧 hash 校验
	ExtCmdFrameHash        uint16 = 60 // C→S [FrameNumber:4][Hash:4]
	ExtCmdFrameHashMismatch uint16 = 61 // S→C [FrameNumber:4][PlayerCount:1][PlayerId:4 + Hash:4]...

	// 可靠通道（双向，快速重连不掉消息）
	ExtCmdReliableMsg uint16 = 200 // 包装任意消息，赋予可靠语义
	// Wire: [reliableSeq:4][innerCmdType:1][innerCmd:0/2/4][innerData:N]
	ExtCmdReliableAck uint16 = 201 // S→C 确认已处理的 C→S reliable seq
	// Wire: [ackSeq:4]
)

// FrameSyncPauseReason 帧同步暂停原因
type FrameSyncPauseReason byte

const (
	PauseReasonSnapshotStale FrameSyncPauseReason = 1 // 快照过期
	PauseReasonDesync        FrameSyncPauseReason = 2 // 帧 hash 不匹配
	PauseReasonGamePause     FrameSyncPauseReason = 3 // 游戏逻辑暂停（客户端请求）
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
	const legacySize = 16 // FrameRate(4)+FrameInterval(4)+StartTime(8)
	if len(buf) < legacySize {
		return nil
	}
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
	ReconnectSuccess           byte = 1 // 成功
	ReconnectFail              byte = 0 // 通用失败
	ReconnectFailBufferStale   byte = 2 // lastFrame 已超出帧环形缓冲区，需要降级到快照重连
	ReconnectFailS2CBufStale   byte = 3 // S→C reliable buffer 已超出，需要降级到快照重连
)

// PlayerInput 单个玩家输入
type PlayerInput struct {
	PlayerId int32
	Data     []byte
}

// FrameEvent 帧内事件（嵌入 FrameData，确保所有客户端在同一帧处理）
type FrameEvent struct {
	EventType byte
	PlayerId  int32
}

// Frame event types
const (
	FrameEventPlayerJoined  byte = 1
	FrameEventPlayerLeft    byte = 2
	FrameEventPlayerOffline byte = 3
	FrameEventPlayerOnline  byte = 4
	FrameEventHostChanged   byte = 5
)

// FrameData 一帧数据
type FrameData struct {
	FrameNumber uint32
	Inputs      []PlayerInput
	Events      []FrameEvent // 帧内事件
}

// EncodeFrameData 编码帧数据
// Wire: [FrameNumber:4][InputCount:2][Inputs...][EventCount:1][Events...]
// 每个 Input: [PlayerId:4][DataLen:2][Data:N]
// 每个 Event: [EventType:1][PlayerId:4]
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

	// Events
	buf[offset] = byte(len(f.Events))
	offset++
	for _, evt := range f.Events {
		buf[offset] = evt.EventType
		offset++
		binary.LittleEndian.PutUint32(buf[offset:], uint32(evt.PlayerId))
		offset += 4
	}

	return offset
}

// DecodeFrameData 解码帧数据
// C3 fix: 全量边界检查，防止越界 panic 和 OOM
func DecodeFrameData(buf []byte) (*FrameData, error) {
	if len(buf) < 6 {
		return nil, fmt.Errorf("FrameData too short: need 6, got %d", len(buf))
	}
	offset := 0
	f := &FrameData{}

	f.FrameNumber = binary.LittleEndian.Uint32(buf[offset:])
	offset += 4

	inputCount := int(binary.LittleEndian.Uint16(buf[offset:]))
	offset += 2

	if inputCount > maxFrameInputs {
		return nil, fmt.Errorf("FrameData inputCount %d exceeds limit %d", inputCount, maxFrameInputs)
	}

	f.Inputs = make([]PlayerInput, inputCount)
	for i := 0; i < inputCount; i++ {
		if offset+6 > len(buf) {
			return nil, fmt.Errorf("FrameData truncated reading input[%d] header at offset %d", i, offset)
		}
		f.Inputs[i].PlayerId = int32(binary.LittleEndian.Uint32(buf[offset:]))
		offset += 4

		dataLen := int(binary.LittleEndian.Uint16(buf[offset:]))
		offset += 2

		if dataLen > maxInputDataLen {
			return nil, fmt.Errorf("FrameData input[%d].dataLen %d exceeds limit %d", i, dataLen, maxInputDataLen)
		}
		if dataLen > 0 {
			if offset+dataLen > len(buf) {
				return nil, fmt.Errorf("FrameData truncated reading input[%d].data: need %d, got %d", i, offset+dataLen, len(buf))
			}
			f.Inputs[i].Data = make([]byte, dataLen)
			copy(f.Inputs[i].Data, buf[offset:offset+dataLen])
			offset += dataLen
		}
	}

	// Events（向后兼容：旧格式无此字段）
	if offset < len(buf) {
		if offset+1 > len(buf) {
			return nil, fmt.Errorf("FrameData truncated reading eventCount")
		}
		eventCount := int(buf[offset])
		offset++

		if eventCount > maxFrameEvents {
			return nil, fmt.Errorf("FrameData eventCount %d exceeds limit %d", eventCount, maxFrameEvents)
		}

		f.Events = make([]FrameEvent, eventCount)
		for i := 0; i < eventCount; i++ {
			if offset+5 > len(buf) {
				return nil, fmt.Errorf("FrameData truncated reading event[%d] at offset %d", i, offset)
			}
			f.Events[i].EventType = buf[offset]
			offset++
			f.Events[i].PlayerId = int32(binary.LittleEndian.Uint32(buf[offset:]))
			offset += 4
		}
	}

	return f, nil
}

// FrameDataSize 计算编码后大小
func FrameDataSize(f *FrameData) int {
	size := 6 // FrameNumber(4) + InputCount(2)
	for _, input := range f.Inputs {
		size += 6 + len(input.Data) // PlayerId(4) + DataLen(2) + Data
	}
	size += 1 + len(f.Events)*5 // EventCount(1) + N × (EventType(1) + PlayerId(4))
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
// Wire: [Result:1][RoomId:4][ServerFrame:4][SnapshotFrame:4][ServerLastC2SSeq:4][SnapshotData:N]
// Result: 0=失败, 1=成功, 2=帧缓冲区过期, 3=S→C reliable buffer 过期(均需降级到快照重连)
func EncodeReconnectRsp(result byte, roomId int32, serverFrame uint32, snapshotFrame uint32, serverLastC2SSeq uint32, snapshotData []byte) []byte {
	headerSize := 1 + 4 + 4 + 4 + 4 // result + roomId + serverFrame + snapshotFrame + serverLastC2SSeq
	buf := make([]byte, headerSize+len(snapshotData))

	buf[0] = result
	binary.LittleEndian.PutUint32(buf[1:], uint32(roomId))
	binary.LittleEndian.PutUint32(buf[5:], serverFrame)
	binary.LittleEndian.PutUint32(buf[9:], snapshotFrame)
	binary.LittleEndian.PutUint32(buf[13:], serverLastC2SSeq)

	if len(snapshotData) > 0 {
		copy(buf[17:], snapshotData)
	}
	return buf
}

// EncodeReliableMsgData 编码 ExtCmdReliableMsg 的消息体
// Wire: [reliableSeq:4][innerCmdType:1][innerCmd:0/2/4][innerData:N]
func EncodeReliableMsgData(seq uint32, inner *codec.Message) []byte {
	var cmdHeaderSize int
	switch inner.CmdType {
	case codec.CmdTypeCore:
		cmdHeaderSize = 2 // type(1) + cmd(1)
	case codec.CmdTypeExtended:
		cmdHeaderSize = 3 // type(1) + extCmd(2)
	case codec.CmdTypeGame:
		cmdHeaderSize = 5 // type(1) + gameCmd(4)
	default:
		cmdHeaderSize = 1
	}
	dataLen := len(inner.Data)
	buf := make([]byte, 4+cmdHeaderSize+dataLen)
	binary.LittleEndian.PutUint32(buf[0:], seq)
	switch inner.CmdType {
	case codec.CmdTypeCore:
		buf[4] = codec.CmdTypeCore
		buf[5] = inner.Cmd
	case codec.CmdTypeExtended:
		buf[4] = codec.CmdTypeExtended
		binary.LittleEndian.PutUint16(buf[5:], inner.ExtCmd)
	case codec.CmdTypeGame:
		buf[4] = codec.CmdTypeGame
		binary.LittleEndian.PutUint32(buf[5:], inner.GameCmd)
	}
	if dataLen > 0 {
		copy(buf[4+cmdHeaderSize:], inner.Data)
	}
	return buf
}

// DecodeReliableMsgSeq 从 ExtCmdReliableMsg 消息体中读取 reliableSeq（不解析 inner）
func DecodeReliableMsgSeq(data []byte) (seq uint32, ok bool) {
	if len(data) < 5 {
		return 0, false
	}
	return binary.LittleEndian.Uint32(data[0:4]), true
}

// DecodeReliableMsgInner 从 ExtCmdReliableMsg 消息体中解包内层消息（data[4:] 起）
func DecodeReliableMsgInner(data []byte) *codec.Message {
	if len(data) < 5 { // seq(4) + type(1) minimum
		return nil
	}
	inner := data[4:]
	innerType := inner[0]
	switch innerType {
	case codec.CmdTypeCore:
		if len(inner) < 2 {
			return nil
		}
		innerData := make([]byte, len(inner)-2)
		copy(innerData, inner[2:])
		return &codec.Message{CmdType: codec.CmdTypeCore, Cmd: inner[1], Data: innerData}
	case codec.CmdTypeExtended:
		if len(inner) < 3 {
			return nil
		}
		extCmd := binary.LittleEndian.Uint16(inner[1:3])
		innerData := make([]byte, len(inner)-3)
		copy(innerData, inner[3:])
		return &codec.Message{CmdType: codec.CmdTypeExtended, ExtCmd: extCmd, Data: innerData}
	case codec.CmdTypeGame:
		if len(inner) < 5 {
			return nil
		}
		gameCmd := binary.LittleEndian.Uint32(inner[1:5])
		innerData := make([]byte, len(inner)-5)
		copy(innerData, inner[5:])
		return &codec.Message{CmdType: codec.CmdTypeGame, GameCmd: gameCmd, Data: innerData}
	}
	return nil
}

// EncodeReliableAck 编码 ExtCmdReliableAck 消息体
// Wire: [ackSeq:4]
func EncodeReliableAck(ackSeq uint32) []byte {
	buf := make([]byte, 4)
	binary.LittleEndian.PutUint32(buf, ackSeq)
	return buf
}

// DecodeFrameHash 解码客户端上报的帧 hash
// Wire: [FrameNumber:4][Hash:4]
func DecodeFrameHash(data []byte) (frameNumber uint32, hash uint32, ok bool) {
	if len(data) < 8 {
		return 0, 0, false
	}
	frameNumber = binary.LittleEndian.Uint32(data[0:4])
	hash = binary.LittleEndian.Uint32(data[4:8])
	return frameNumber, hash, true
}

// EncodeFrameHashMismatch 编码 hash 不匹配详情
// Wire: [FrameNumber:4][PlayerCount:1][PlayerId:4 + Hash:4]...
func EncodeFrameHashMismatch(frameNumber uint32, playerHashes map[int32]uint32) []byte {
	buf := make([]byte, 5+len(playerHashes)*8)
	binary.LittleEndian.PutUint32(buf[0:], frameNumber)
	buf[4] = byte(len(playerHashes))
	offset := 5
	for pid, hash := range playerHashes {
		binary.LittleEndian.PutUint32(buf[offset:], uint32(pid))
		offset += 4
		binary.LittleEndian.PutUint32(buf[offset:], hash)
		offset += 4
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
