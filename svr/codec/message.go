package codec

import (
	"encoding/binary"
	"fmt"
	"sync"
)

// 包头格式:
// [FlagsCmd: 1B][BodyLen: 2B/4B][Seq: 0B/4B][ExtCmd/GameCmd: 0/2/4B][Data: NB]
//
// FlagsCmd byte:
//   bit 0:   LenSize  (0=BodyLen 2B, 1=BodyLen 4B)
//   bit 1:   HasSeq   (0=no Seq, 1=Seq 4B)
//   bit 2-3: CmdType  (00=Core, 01=Extended, 10=Game, 11=Reserved)
//   bit 4-7: CoreCmd  (0-15, 仅 CmdType=Core 时有意义)
//
// CmdType=00 Core:     高频帧同步命令, Cmd 在 FlagsCmd 高 4 位     → 包头 3B
// CmdType=01 Extended: 房间/实体管理, ExtCmd uint16 在 body 内      → 包头 5B
// CmdType=10 Game:     游戏自定义命令, GameCmd uint32 在 body 内    → 包头 7B

const (
	FlagLenSize4 = 0x01
	FlagHasSeq   = 0x02

	CmdTypeCore     = 0 // bits 2-3 = 00
	CmdTypeExtended = 1 // bits 2-3 = 01
	CmdTypeGame     = 2 // bits 2-3 = 10

	MinHeaderSize      = 3 // FlagsCmd(1) + BodyLen(2)
	LargeBodyThreshold = 65530
)

// Message 网络消息
type Message struct {
	CmdType byte   // CmdTypeCore / CmdTypeExtended / CmdTypeGame
	Cmd     byte   // Core Cmd (0-15)
	ExtCmd  uint16 // Extended Cmd (0-65535)
	GameCmd uint32 // Game Cmd (0-4294967295)
	Seq     int32
	HasSeq  bool
	Data    []byte
}

func (m *Message) String() string {
	switch m.CmdType {
	case CmdTypeExtended:
		return fmt.Sprintf("[ExtMsg ExtCmd=%d Seq=%d DataLen=%d]", m.ExtCmd, m.Seq, len(m.Data))
	case CmdTypeGame:
		return fmt.Sprintf("[GameMsg GameCmd=%d DataLen=%d]", m.GameCmd, len(m.Data))
	default:
		return fmt.Sprintf("[Msg Cmd=%d Seq=%d DataLen=%d]", m.Cmd, m.Seq, len(m.Data))
	}
}

// CmdSize 当前 CmdType 对应的额外字节数（在 body 内）
func cmdExtraSize(cmdType byte) int {
	switch cmdType {
	case CmdTypeExtended:
		return 2
	case CmdTypeGame:
		return 4
	default:
		return 0
	}
}

// HeaderSize 包头大小
func (m *Message) HeaderSize() int {
	extra := 0
	if m.CmdType == CmdTypeExtended {
		extra = 2
	} else if m.CmdType == CmdTypeGame {
		extra = 4
	}
	totalPayload := len(m.Data) + extra
	size := 1 // FlagsCmd
	if totalPayload > LargeBodyThreshold {
		size += 4
	} else {
		size += 2
	}
	if m.HasSeq {
		size += 4
	}
	size += extra
	return size
}

// EncodedSize 编码后的总大小
func EncodedSize(msg *Message) int {
	return msg.HeaderSize() + len(msg.Data)
}

// bufPool 复用编码缓冲区
var bufPool = sync.Pool{
	New: func() interface{} {
		buf := make([]byte, 0, 1024)
		return &buf
	},
}

// msgPool 复用 *Message 对象，减少高并发解码路径的 GC 压力
// 使用方式: msg := DecodePooled(buf); ... ; PutMessage(msg)
var msgPool = sync.Pool{
	New: func() any { return &Message{} },
}

// GetMessage 从 Pool 取出一个已清零的 *Message（需配合 PutMessage 归还）
func GetMessage() *Message {
	return msgPool.Get().(*Message)
}

// PutMessage 将 *Message 归还 Pool（调用后禁止继续使用该对象及其 Data 字段）
func PutMessage(m *Message) {
	*m = Message{} // 清零所有字段，防止 Data 引用外部 buffer 造成 GC 泄漏
	msgPool.Put(m)
}

// Encode 编码 Message
func Encode(msg *Message) []byte {
	totalLen := EncodedSize(msg)
	bufPtr := bufPool.Get().(*[]byte)
	buf := *bufPtr
	if cap(buf) < totalLen {
		buf = make([]byte, totalLen)
	} else {
		buf = buf[:totalLen]
	}
	EncodeTo(msg, buf)
	return buf
}

// PutBuf 归还 Encode 返回的缓冲区
func PutBuf(buf []byte) {
	buf = buf[:0]
	bufPool.Put(&buf)
}

// EncodeTo 编码到指定 buffer
func EncodeTo(msg *Message, buf []byte) int {
	dataLen := len(msg.Data)

	// Core 快速路径 — 无 extra, 无 CmdType switch, 直线执行
	if msg.CmdType == CmdTypeCore {
		largeLen := dataLen > LargeBodyThreshold
		flagsCmd := (msg.Cmd & 0x0F) << 4 // CmdType=00 所以 bits 2-3 = 0
		if largeLen {
			flagsCmd |= FlagLenSize4
		}
		if msg.HasSeq {
			flagsCmd |= FlagHasSeq
		}
		buf[0] = flagsCmd
		offset := 1
		bodyLen := dataLen
		if msg.HasSeq {
			bodyLen += 4
		}
		if largeLen {
			binary.LittleEndian.PutUint32(buf[offset:], uint32(bodyLen))
			offset += 4
		} else {
			binary.LittleEndian.PutUint16(buf[offset:], uint16(bodyLen))
			offset += 2
		}
		if msg.HasSeq {
			binary.LittleEndian.PutUint32(buf[offset:], uint32(msg.Seq))
			offset += 4
		}
		if dataLen > 0 {
			copy(buf[offset:], msg.Data)
			offset += dataLen
		}
		return offset
	}

	// Extended / Game 路径
	extra := 2
	if msg.CmdType == CmdTypeGame {
		extra = 4
	}
	totalPayload := dataLen + extra
	largeLen := totalPayload > LargeBodyThreshold

	flagsCmd := (msg.CmdType & 0x03) << 2
	if largeLen {
		flagsCmd |= FlagLenSize4
	}
	if msg.HasSeq {
		flagsCmd |= FlagHasSeq
	}
	buf[0] = flagsCmd
	offset := 1

	bodyLen := totalPayload
	if msg.HasSeq {
		bodyLen += 4
	}
	if largeLen {
		binary.LittleEndian.PutUint32(buf[offset:], uint32(bodyLen))
		offset += 4
	} else {
		binary.LittleEndian.PutUint16(buf[offset:], uint16(bodyLen))
		offset += 2
	}

	if msg.HasSeq {
		binary.LittleEndian.PutUint32(buf[offset:], uint32(msg.Seq))
		offset += 4
	}

	if msg.CmdType == CmdTypeExtended {
		binary.LittleEndian.PutUint16(buf[offset:], msg.ExtCmd)
		offset += 2
	} else {
		binary.LittleEndian.PutUint32(buf[offset:], msg.GameCmd)
		offset += 4
	}

	if dataLen > 0 {
		copy(buf[offset:], msg.Data)
		offset += dataLen
	}

	return offset
}

// decodeInto 将 buf 解码到已有的 Message 对象（供 Decode 和 DecodePooled 共享逻辑）
// 调用前须确保 len(buf) >= MinHeaderSize，且 msg 已清零
func decodeInto(buf []byte, msg *Message) error {
	flagsCmd := buf[0]
	largeLen := flagsCmd&FlagLenSize4 != 0
	hasSeq := flagsCmd&FlagHasSeq != 0
	msg.CmdType = (flagsCmd >> 2) & 0x03
	msg.HasSeq = hasSeq
	offset := 1

	// BodyLen
	var bodyLen int
	if largeLen {
		if len(buf) < offset+4 {
			return fmt.Errorf("buffer too short for 4B len")
		}
		bodyLen = int(binary.LittleEndian.Uint32(buf[offset:]))
		offset += 4
	} else {
		bodyLen = int(binary.LittleEndian.Uint16(buf[offset:]))
		offset += 2
	}

	remainLen := bodyLen

	// Seq
	if hasSeq {
		if len(buf) < offset+4 {
			return fmt.Errorf("buffer too short for seq")
		}
		msg.Seq = int32(binary.LittleEndian.Uint32(buf[offset:]))
		offset += 4
		remainLen -= 4
	}

	// Cmd / ExtCmd / GameCmd
	switch msg.CmdType {
	case CmdTypeCore:
		msg.Cmd = flagsCmd >> 4
	case CmdTypeExtended:
		if len(buf) < offset+2 {
			return fmt.Errorf("buffer too short for ExtCmd")
		}
		msg.ExtCmd = binary.LittleEndian.Uint16(buf[offset:])
		offset += 2
		remainLen -= 2
	case CmdTypeGame:
		if len(buf) < offset+4 {
			return fmt.Errorf("buffer too short for GameCmd")
		}
		msg.GameCmd = binary.LittleEndian.Uint32(buf[offset:])
		offset += 4
		remainLen -= 4
	}

	// Data 零拷贝引用原 buffer
	if remainLen > 0 {
		msg.Data = buf[offset : offset+remainLen]
	}

	return nil
}

// Decode 解码（Data 零拷贝引用原 buffer）
// 每次调用分配新 *Message；不需要归还，GC 自动回收。
func Decode(buf []byte) (*Message, error) {
	if len(buf) < MinHeaderSize {
		return nil, fmt.Errorf("buffer too short: %d", len(buf))
	}
	msg := &Message{}
	if err := decodeInto(buf, msg); err != nil {
		return nil, err
	}
	return msg, nil
}

// DecodePooled 从 msgPool 取 *Message 并解码（Data 零拷贝引用原 buffer）
// 调用方用完后须调用 PutMessage(msg) 归还，归还后禁止再访问 msg 及 msg.Data。
// 适用于高频解码路径（例如每帧收到的帧输入消息）。
func DecodePooled(buf []byte) (*Message, error) {
	if len(buf) < MinHeaderSize {
		return nil, fmt.Errorf("buffer too short: %d", len(buf))
	}
	msg := msgPool.Get().(*Message)
	*msg = Message{} // pool 对象可能有上次的残留字段，先清零
	if err := decodeInto(buf, msg); err != nil {
		msgPool.Put(msg) // 解码失败也要归还，避免泄漏
		return nil, err
	}
	return msg, nil
}

// DecodeCopy 解码（Data 独立拷贝）
func DecodeCopy(buf []byte) (*Message, error) {
	msg, err := Decode(buf)
	if err != nil {
		return nil, err
	}
	if len(msg.Data) > 0 {
		data := make([]byte, len(msg.Data))
		copy(data, msg.Data)
		msg.Data = data
	}
	return msg, nil
}

// PeekFrameSize 从 buffer 开头探测完整帧长度，返回 -1 表示数据不够
func PeekFrameSize(buf []byte) int {
	if len(buf) < 1 {
		return -1
	}
	flagsCmd := buf[0]
	largeLen := flagsCmd&FlagLenSize4 != 0
	lenFieldSize := 2
	if largeLen {
		lenFieldSize = 4
	}
	if len(buf) < 1+lenFieldSize {
		return -1
	}

	var bodyLen int
	if largeLen {
		bodyLen = int(binary.LittleEndian.Uint32(buf[1:]))
	} else {
		bodyLen = int(binary.LittleEndian.Uint16(buf[1:]))
	}

	return 1 + lenFieldSize + bodyLen
}

// === 便捷构造函数 ===

// NewCoreMessage 创建核心消息 (Cmd 0-15)
func NewCoreMessage(cmd byte, data []byte) *Message {
	return &Message{CmdType: CmdTypeCore, Cmd: cmd, Data: data}
}

// NewExtMessage 创建扩展消息 (ExtCmd uint16)
func NewExtMessage(extCmd uint16, data []byte) *Message {
	return &Message{CmdType: CmdTypeExtended, ExtCmd: extCmd, Data: data}
}

// NewGameMessage 创建游戏消息 (GameCmd uint32)
func NewGameMessage(gameCmd uint32, data []byte) *Message {
	return &Message{CmdType: CmdTypeGame, GameCmd: gameCmd, Data: data}
}
