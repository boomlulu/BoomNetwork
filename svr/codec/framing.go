package codec

import (
	"encoding/binary"
	"fmt"
	"io"
)

// ReadFrame 从 reader 中读取一个完整的消息帧
// 阻塞直到读到完整帧或出错
func ReadFrame(r io.Reader) ([]byte, error) {
	// 读 4 字节 BodyLen
	header := make([]byte, HeaderSize)
	if _, err := io.ReadFull(r, header); err != nil {
		return nil, fmt.Errorf("read header: %w", err)
	}

	bodyLen := int(binary.LittleEndian.Uint32(header))
	if bodyLen < BodyHeaderSize {
		return nil, fmt.Errorf("invalid body length: %d", bodyLen)
	}
	if bodyLen > 1<<20 { // 1MB 上限保护
		return nil, fmt.Errorf("body too large: %d", bodyLen)
	}

	// 读 body
	frame := make([]byte, HeaderSize+bodyLen)
	copy(frame, header)
	if _, err := io.ReadFull(r, frame[HeaderSize:]); err != nil {
		return nil, fmt.Errorf("read body: %w", err)
	}

	return frame, nil
}

// ReadMessage 从 reader 中读取并解码一条完整消息
func ReadMessage(r io.Reader) (*Message, error) {
	frame, err := ReadFrame(r)
	if err != nil {
		return nil, err
	}
	return Decode(frame)
}

// WriteMessage 编码并写入一条消息
func WriteMessage(w io.Writer, msg *Message) error {
	data := Encode(msg)
	_, err := w.Write(data)
	return err
}
