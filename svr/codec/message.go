package codec

import (
	"encoding/binary"
	"fmt"
)

const (
	HeaderSize     = 4  // BodyLen 字段长度
	BodyHeaderSize = 13 // Version(1) + Cmd(4) + ClientSeq(4) + ServerSeq(4)
	MinFrameSize   = HeaderSize + BodyHeaderSize
)

// Message 网络消息结构，和 C# 端线格式完全一致
// Wire: [BodyLen:4 LE][Version:1][Cmd:4 LE][ClientSeq:4 LE][ServerSeq:4 LE][Data:N]
type Message struct {
	Version   byte
	Cmd       uint32
	ClientSeq int32
	ServerSeq int32
	Data      []byte
}

func (m *Message) String() string {
	return fmt.Sprintf("[Msg Cmd=%d CS=%d SS=%d DataLen=%d]", m.Cmd, m.ClientSeq, m.ServerSeq, len(m.Data))
}

// Encode 编码 Message 为完整线格式 bytes
func Encode(msg *Message) []byte {
	dataLen := len(msg.Data)
	bodyLen := BodyHeaderSize + dataLen
	totalLen := HeaderSize + bodyLen
	buf := make([]byte, totalLen)

	// BodyLen
	binary.LittleEndian.PutUint32(buf[0:4], uint32(bodyLen))

	// Version
	buf[4] = msg.Version

	// Cmd
	binary.LittleEndian.PutUint32(buf[5:9], msg.Cmd)

	// ClientSeq
	binary.LittleEndian.PutUint32(buf[9:13], uint32(msg.ClientSeq))

	// ServerSeq
	binary.LittleEndian.PutUint32(buf[13:17], uint32(msg.ServerSeq))

	// Data
	if dataLen > 0 {
		copy(buf[17:], msg.Data)
	}

	return buf
}

// Decode 从完整线格式 bytes 解码
func Decode(buf []byte) (*Message, error) {
	if len(buf) < MinFrameSize {
		return nil, fmt.Errorf("buffer too short: %d", len(buf))
	}

	bodyLen := int(binary.LittleEndian.Uint32(buf[0:4]))
	if len(buf) < HeaderSize+bodyLen {
		return nil, fmt.Errorf("incomplete frame: need %d, got %d", HeaderSize+bodyLen, len(buf))
	}

	msg := &Message{
		Version:   buf[4],
		Cmd:       binary.LittleEndian.Uint32(buf[5:9]),
		ClientSeq: int32(binary.LittleEndian.Uint32(buf[9:13])),
		ServerSeq: int32(binary.LittleEndian.Uint32(buf[13:17])),
	}

	dataLen := bodyLen - BodyHeaderSize
	if dataLen > 0 {
		msg.Data = make([]byte, dataLen)
		copy(msg.Data, buf[17:17+dataLen])
	}

	return msg, nil
}
