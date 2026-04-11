package codec

import (
	"bufio"
	"encoding/binary"
	"fmt"
	"io"
)

// defaultMaxMessageSize 内部默认最大消息大小（64KB）
// NEW-02: 移除全局 var MaxMessageSize，改为构造参数，消除 TCP/WS 并发设值竞态。
const defaultMaxMessageSize = 65536

// FrameReader 带缓冲的帧读取器，复用内部 buffer 减少分配
type FrameReader struct {
	reader         *bufio.Reader
	headerBuf      [5]byte // 最大: FlagsCmd(1) + BodyLen(4) = 5
	frameBuf       []byte  // 复用 frame buffer，按需扩容
	maxMessageSize int
}

// NewFrameReader 创建帧读取器。
// maxMsgSize 指定单帧最大字节数（0 表示使用默认值 64KB）。
// NEW-02: 通过构造参数传入，避免全局变量在 TCP/WS 并发使用时的竞态。
func NewFrameReader(r io.Reader, maxMsgSize int) *FrameReader {
	if maxMsgSize <= 0 {
		maxMsgSize = defaultMaxMessageSize
	}
	return &FrameReader{
		reader:         bufio.NewReaderSize(r, 8192),
		frameBuf:       make([]byte, 1024),
		maxMessageSize: maxMsgSize,
	}
}

// SetMaxMessageSize 设置最大消息大小
func (fr *FrameReader) SetMaxMessageSize(size int) {
	fr.maxMessageSize = size
}

// Reset 将 FrameReader 切换到新的 io.Reader（复用内部 frameBuf，避免重新分配）
// 适用于 benchmark 及连接复用场景：reader 对象一次创建，多次 Reset 重用。
func (fr *FrameReader) Reset(r io.Reader) {
	fr.reader.Reset(r)
}

// ReadFrame 读取一个完整帧（复用内部 buffer，零分配热路径）
// 新格式: [FlagsCmd:1][BodyLen:2/4][Body...]
func (fr *FrameReader) ReadFrame() ([]byte, error) {
	// 读 1 字节 FlagsCmd
	if _, err := io.ReadFull(fr.reader, fr.headerBuf[:1]); err != nil {
		return nil, fmt.Errorf("read flagscmd: %w", err)
	}

	flagsCmd := fr.headerBuf[0]
	largeLen := flagsCmd&FlagLenSize4 != 0
	lenFieldSize := 2
	if largeLen {
		lenFieldSize = 4
	}

	// 读 BodyLen
	if _, err := io.ReadFull(fr.reader, fr.headerBuf[1:1+lenFieldSize]); err != nil {
		return nil, fmt.Errorf("read bodylen: %w", err)
	}

	var bodyLen int
	if largeLen {
		bodyLen = int(binary.LittleEndian.Uint32(fr.headerBuf[1:]))
	} else {
		bodyLen = int(binary.LittleEndian.Uint16(fr.headerBuf[1:]))
	}

	if bodyLen > fr.maxMessageSize {
		return nil, fmt.Errorf("body too large: %d (max %d)", bodyLen, fr.maxMessageSize)
	}

	headerSize := 1 + lenFieldSize
	totalLen := headerSize + bodyLen

	if cap(fr.frameBuf) < totalLen {
		fr.frameBuf = make([]byte, totalLen)
	} else {
		fr.frameBuf = fr.frameBuf[:totalLen]
	}

	copy(fr.frameBuf, fr.headerBuf[:headerSize])

	if bodyLen > 0 {
		if _, err := io.ReadFull(fr.reader, fr.frameBuf[headerSize:]); err != nil {
			return nil, fmt.Errorf("read body: %w", err)
		}
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
// 注意：这些函数不用 bufio，直接 io.ReadFull，适合单次或简单场景。
// 高频场景请用 FrameReader/FrameWriter。

// ReadFrame 从 reader 中读取一个完整帧（每次分配，不用 bufio）
func ReadFrame(r io.Reader) ([]byte, error) {
	// 读 1 字节 FlagsCmd
	var flagsBuf [1]byte
	if _, err := io.ReadFull(r, flagsBuf[:]); err != nil {
		return nil, fmt.Errorf("read flagscmd: %w", err)
	}

	flagsCmd := flagsBuf[0]
	largeLen := flagsCmd&FlagLenSize4 != 0
	lenFieldSize := 2
	if largeLen {
		lenFieldSize = 4
	}

	// 读 BodyLen
	lenBuf := make([]byte, lenFieldSize)
	if _, err := io.ReadFull(r, lenBuf); err != nil {
		return nil, fmt.Errorf("read bodylen: %w", err)
	}

	var bodyLen int
	if largeLen {
		bodyLen = int(binary.LittleEndian.Uint32(lenBuf))
	} else {
		bodyLen = int(binary.LittleEndian.Uint16(lenBuf))
	}

	if bodyLen > defaultMaxMessageSize {
		return nil, fmt.Errorf("body too large: %d (max %d)", bodyLen, defaultMaxMessageSize)
	}

	headerSize := 1 + lenFieldSize
	totalLen := headerSize + bodyLen

	frame := make([]byte, totalLen)
	frame[0] = flagsCmd
	copy(frame[1:], lenBuf)

	if bodyLen > 0 {
		if _, err := io.ReadFull(r, frame[headerSize:]); err != nil {
			return nil, fmt.Errorf("read body: %w", err)
		}
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

// WriteMessage 编码并写入一条消息
func WriteMessage(w io.Writer, msg *Message) error {
	data := Encode(msg)
	_, err := w.Write(data)
	PutBuf(data)
	return err
}
