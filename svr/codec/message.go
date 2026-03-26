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
	extra := cmdExtraSize(m.CmdType)
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
	extra := cmdExtraSize(msg.CmdType)
	totalPayload := dataLen + extra
	largeLen := totalPayload > LargeBodyThreshold

	// FlagsCmd
	var flagsCmd byte
	flagsCmd = (msg.CmdType & 0x03) << 2
	if msg.CmdType == CmdTypeCore {
		flagsCmd |= (msg.Cmd & 0x0F) << 4
	}
	if largeLen {
		flagsCmd |= FlagLenSize4
	}
	if msg.HasSeq {
		flagsCmd |= FlagHasSeq
	}
	buf[0] = flagsCmd
	offset := 1

	// BodyLen (covers: Seq + ExtCmd/GameCmd + Data)
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

	// Seq
	if msg.HasSeq {
		binary.LittleEndian.PutUint32(buf[offset:], uint32(msg.Seq))
		offset += 4
	}

	// ExtCmd / GameCmd
	switch msg.CmdType {
	case CmdTypeExtended:
		binary.LittleEndian.PutUint16(buf[offset:], msg.ExtCmd)
		offset += 2
	case CmdTypeGame:
		binary.LittleEndian.PutUint32(buf[offset:], msg.GameCmd)
		offset += 4
	}

	// Data
	if dataLen > 0 {
		copy(buf[offset:], msg.Data)
		offset += dataLen
	}

	return offset
}

// Decode 解码（Data 零拷贝引用原 buffer）
func Decode(buf []byte) (*Message, error) {
	if len(buf) < MinHeaderSize {
		return nil, fmt.Errorf("buffer too short: %d", len(buf))
	}

	flagsCmd := buf[0]
	largeLen := flagsCmd&FlagLenSize4 != 0
	hasSeq := flagsCmd&FlagHasSeq != 0
	cmdType := (flagsCmd >> 2) & 0x03
	offset := 1

	// BodyLen
	var bodyLen int
	if largeLen {
		if len(buf) < offset+4 {
			return nil, fmt.Errorf("buffer too short for 4B len")
		}
		bodyLen = int(binary.LittleEndian.Uint32(buf[offset:]))
		offset += 4
	} else {
		bodyLen = int(binary.LittleEndian.Uint16(buf[offset:]))
		offset += 2
	}

	msg := &Message{
		CmdType: cmdType,
		HasSeq:  hasSeq,
	}

	remainLen := bodyLen

	// Seq
	if hasSeq {
		if len(buf) < offset+4 {
			return nil, fmt.Errorf("buffer too short for seq")
		}
		msg.Seq = int32(binary.LittleEndian.Uint32(buf[offset:]))
		offset += 4
		remainLen -= 4
	}

	// Cmd / ExtCmd / GameCmd
	switch cmdType {
	case CmdTypeCore:
		msg.Cmd = flagsCmd >> 4
	case CmdTypeExtended:
		if len(buf) < offset+2 {
			return nil, fmt.Errorf("buffer too short for ExtCmd")
		}
		msg.ExtCmd = binary.LittleEndian.Uint16(buf[offset:])
		offset += 2
		remainLen -= 2
	case CmdTypeGame:
		if len(buf) < offset+4 {
			return nil, fmt.Errorf("buffer too short for GameCmd")
		}
		msg.GameCmd = binary.LittleEndian.Uint32(buf[offset:])
		offset += 4
		remainLen -= 4
	}

	// Data
	if remainLen > 0 {
		msg.Data = buf[offset : offset+remainLen]
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
