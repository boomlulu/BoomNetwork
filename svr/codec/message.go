package codec

import (
	"encoding/binary"
	"fmt"
	"sync"
)

// 新包头格式:
// [FlagsCmd: 1B][BodyLen: 2B/4B][Seq: 0B/4B][Data: NB]
//
// FlagsCmd byte:
//   bit 0:   LenSize (0=BodyLen 2B, 1=BodyLen 4B)
//   bit 1:   HasSeq  (0=no Seq, 1=Seq 4B)
//   bit 2-7: Cmd     (0-63)

const (
	FlagLenSize4 = 0x01
	FlagHasSeq   = 0x02

	MinHeaderSize = 3  // FlagsCmd(1) + BodyLen(2)
	LargeBodyThreshold = 65530
)

// Message 网络消息
type Message struct {
	Cmd    byte
	Seq    int32
	HasSeq bool
	Data   []byte
}

func (m *Message) String() string {
	return fmt.Sprintf("[Msg Cmd=%d Seq=%d DataLen=%d]", m.Cmd, m.Seq, len(m.Data))
}

// HeaderSize 包头大小
func (m *Message) HeaderSize() int {
	size := 1 // FlagsCmd
	if len(m.Data) > LargeBodyThreshold {
		size += 4
	} else {
		size += 2
	}
	if m.HasSeq {
		size += 4
	}
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
	largeLen := dataLen > LargeBodyThreshold

	// FlagsCmd
	flagsCmd := msg.Cmd << 2
	if largeLen {
		flagsCmd |= FlagLenSize4
	}
	if msg.HasSeq {
		flagsCmd |= FlagHasSeq
	}
	buf[0] = flagsCmd
	offset := 1

	// BodyLen
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

	// Seq
	if msg.HasSeq {
		binary.LittleEndian.PutUint32(buf[offset:], uint32(msg.Seq))
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
	cmd := flagsCmd >> 2
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
		Cmd:    cmd,
		HasSeq: hasSeq,
	}

	// Seq
	if hasSeq {
		if len(buf) < offset+4 {
			return nil, fmt.Errorf("buffer too short for seq")
		}
		msg.Seq = int32(binary.LittleEndian.Uint32(buf[offset:]))
		offset += 4
	}

	// Data
	dataLen := bodyLen
	if hasSeq {
		dataLen -= 4
	}
	if dataLen > 0 {
		msg.Data = buf[offset : offset+dataLen]
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
