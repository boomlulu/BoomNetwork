package codec

import (
	"encoding/binary"
	"fmt"
	"sync"
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

// bufPool 复用编码缓冲区，减少 GC 压力
var bufPool = sync.Pool{
	New: func() interface{} {
		buf := make([]byte, 0, 1024)
		return &buf
	},
}

// Encode 编码 Message 为完整线格式 bytes
// 返回的 []byte 来自内部 pool，调用方在发送完毕后应调用 PutBuf 归还
func Encode(msg *Message) []byte {
	dataLen := len(msg.Data)
	bodyLen := BodyHeaderSize + dataLen
	totalLen := HeaderSize + bodyLen

	bufPtr := bufPool.Get().(*[]byte)
	buf := *bufPtr
	if cap(buf) < totalLen {
		buf = make([]byte, totalLen)
	} else {
		buf = buf[:totalLen]
	}

	binary.LittleEndian.PutUint32(buf[0:4], uint32(bodyLen))
	buf[4] = msg.Version
	binary.LittleEndian.PutUint32(buf[5:9], msg.Cmd)
	binary.LittleEndian.PutUint32(buf[9:13], uint32(msg.ClientSeq))
	binary.LittleEndian.PutUint32(buf[13:17], uint32(msg.ServerSeq))

	if dataLen > 0 {
		copy(buf[17:], msg.Data)
	}

	return buf
}

// PutBuf 归还 Encode 返回的缓冲区到 pool
func PutBuf(buf []byte) {
	buf = buf[:0]
	bufPool.Put(&buf)
}

// EncodeTo 编码到调用方提供的 buffer（零分配）
// 返回写入的字节数
func EncodeTo(msg *Message, buf []byte) int {
	dataLen := len(msg.Data)
	bodyLen := BodyHeaderSize + dataLen
	totalLen := HeaderSize + bodyLen

	binary.LittleEndian.PutUint32(buf[0:4], uint32(bodyLen))
	buf[4] = msg.Version
	binary.LittleEndian.PutUint32(buf[5:9], msg.Cmd)
	binary.LittleEndian.PutUint32(buf[9:13], uint32(msg.ClientSeq))
	binary.LittleEndian.PutUint32(buf[13:17], uint32(msg.ServerSeq))

	if dataLen > 0 {
		copy(buf[17:], msg.Data)
	}

	return totalLen
}

// EncodedSize 计算编码后的总长度
func EncodedSize(msg *Message) int {
	return HeaderSize + BodyHeaderSize + len(msg.Data)
}

// Decode 从完整线格式 bytes 解码
// 注意: Data 引用 buf 的切片（零拷贝），调用方不应修改 buf
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
		// 零拷贝：直接引用原 buffer 的切片
		msg.Data = buf[17 : 17+dataLen]
	}

	return msg, nil
}

// DecodeCopy 从完整线格式 bytes 解码（复制 Data，安全持有）
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
