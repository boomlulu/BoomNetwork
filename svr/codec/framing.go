package codec

import (
	"bufio"
	"encoding/binary"
	"fmt"
	"io"
)

// FrameReader 带缓冲的帧读取器，复用内部 buffer 减少分配
type FrameReader struct {
	reader    *bufio.Reader
	headerBuf [HeaderSize]byte  // 复用 header buffer
	frameBuf  []byte            // 复用 frame buffer，按需扩容
}

// NewFrameReader 创建帧读取器
func NewFrameReader(r io.Reader) *FrameReader {
	return &FrameReader{
		reader:   bufio.NewReaderSize(r, 8192),
		frameBuf: make([]byte, 1024),
	}
}

// ReadFrame 读取一个完整帧（复用内部 buffer，零分配热路径）
// 返回的 []byte 是内部 buffer 的切片，下次调用 ReadFrame 后失效
func (fr *FrameReader) ReadFrame() ([]byte, error) {
	// 读 4 字节 BodyLen
	if _, err := io.ReadFull(fr.reader, fr.headerBuf[:]); err != nil {
		return nil, fmt.Errorf("read header: %w", err)
	}

	bodyLen := int(binary.LittleEndian.Uint32(fr.headerBuf[:]))
	if bodyLen < BodyHeaderSize {
		return nil, fmt.Errorf("invalid body length: %d", bodyLen)
	}
	if bodyLen > 1<<20 {
		return nil, fmt.Errorf("body too large: %d", bodyLen)
	}

	totalLen := HeaderSize + bodyLen

	// 按需扩容 frameBuf
	if cap(fr.frameBuf) < totalLen {
		fr.frameBuf = make([]byte, totalLen)
	} else {
		fr.frameBuf = fr.frameBuf[:totalLen]
	}

	// 拷贝 header
	copy(fr.frameBuf, fr.headerBuf[:])

	// 读 body
	if _, err := io.ReadFull(fr.reader, fr.frameBuf[HeaderSize:]); err != nil {
		return nil, fmt.Errorf("read body: %w", err)
	}

	return fr.frameBuf[:totalLen], nil
}

// ReadMessage 读取并解码一条消息（零拷贝，Data 引用内部 buffer）
// 返回的 Message.Data 在下次调用 ReadMessage 后失效
// 如果需要持有 Data，请调用 ReadMessageCopy
func (fr *FrameReader) ReadMessage() (*Message, error) {
	frame, err := fr.ReadFrame()
	if err != nil {
		return nil, err
	}
	return Decode(frame)
}

// ReadMessageCopy 读取并解码一条消息（Data 独立拷贝，可安全持有）
func (fr *FrameReader) ReadMessageCopy() (*Message, error) {
	frame, err := fr.ReadFrame()
	if err != nil {
		return nil, err
	}
	return DecodeCopy(frame)
}

// FrameWriter 带缓冲的帧写入器
type FrameWriter struct {
	writer *bufio.Writer
	buf    []byte
}

// NewFrameWriter 创建帧写入器
func NewFrameWriter(w io.Writer) *FrameWriter {
	return &FrameWriter{
		writer: bufio.NewWriterSize(w, 8192),
		buf:    make([]byte, 1024),
	}
}

// WriteMessage 编码并写入一条消息（复用内部 buffer）
func (fw *FrameWriter) WriteMessage(msg *Message) error {
	size := EncodedSize(msg)
	if cap(fw.buf) < size {
		fw.buf = make([]byte, size)
	} else {
		fw.buf = fw.buf[:size]
	}

	EncodeTo(msg, fw.buf)
	_, err := fw.writer.Write(fw.buf[:size])
	return err
}

// Flush 刷新缓冲区
func (fw *FrameWriter) Flush() error {
	return fw.writer.Flush()
}

// --- 兼容旧 API（供测试和简单场景使用）---

// ReadFrame 从 reader 中读取一个完整帧（每次分配）
func ReadFrame(r io.Reader) ([]byte, error) {
	header := make([]byte, HeaderSize)
	if _, err := io.ReadFull(r, header); err != nil {
		return nil, fmt.Errorf("read header: %w", err)
	}

	bodyLen := int(binary.LittleEndian.Uint32(header))
	if bodyLen < BodyHeaderSize {
		return nil, fmt.Errorf("invalid body length: %d", bodyLen)
	}
	if bodyLen > 1<<20 {
		return nil, fmt.Errorf("body too large: %d", bodyLen)
	}

	frame := make([]byte, HeaderSize+bodyLen)
	copy(frame, header)
	if _, err := io.ReadFull(r, frame[HeaderSize:]); err != nil {
		return nil, fmt.Errorf("read body: %w", err)
	}

	return frame, nil
}

// ReadMessage 从 reader 中读取并解码一条完整消息（每次分配）
func ReadMessage(r io.Reader) (*Message, error) {
	frame, err := ReadFrame(r)
	if err != nil {
		return nil, err
	}
	return DecodeCopy(frame)
}

// WriteMessage 编码并写入一条消息（每次分配）
func WriteMessage(w io.Writer, msg *Message) error {
	data := Encode(msg)
	_, err := w.Write(data)
	PutBuf(data)
	return err
}
