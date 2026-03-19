using System;

namespace BoomNetwork.Core.Framing
{
    /// <summary>
    /// 环形缓冲区 — 避免数据前移的 O(n) 开销
    /// 写入追加到尾部，读取从头部开始，空间不足时才 compact。
    /// </summary>
    public class RingBuffer
    {
        private byte[] _buffer;
        private int _readPos;
        private int _writePos;

        public RingBuffer(int capacity = 8192)
        {
            _buffer = new byte[capacity];
        }

        /// <summary>
        /// 可读字节数
        /// </summary>
        public int ReadableBytes => _writePos - _readPos;

        /// <summary>
        /// 可写空间
        /// </summary>
        public int WritableBytes => _buffer.Length - _writePos;

        /// <summary>
        /// 写入数据
        /// </summary>
        public void Write(byte[] data, int offset, int length)
        {
            EnsureWritable(length);
            Buffer.BlockCopy(data, offset, _buffer, _writePos, length);
            _writePos += length;
        }

        /// <summary>
        /// 读取可读区域（不拷贝，返回内部缓冲区的 Span）
        /// </summary>
        public ReadOnlySpan<byte> ReadableSpan => _buffer.AsSpan(_readPos, ReadableBytes);

        /// <summary>
        /// 消费已读字节
        /// </summary>
        public void Consume(int bytes)
        {
            _readPos += bytes;
            if (_readPos == _writePos)
            {
                // 全部消费完，重置位置
                _readPos = 0;
                _writePos = 0;
            }
        }

        /// <summary>
        /// 重置
        /// </summary>
        public void Reset()
        {
            _readPos = 0;
            _writePos = 0;
        }

        private void EnsureWritable(int bytes)
        {
            if (WritableBytes >= bytes)
                return;

            // 先尝试 compact（把已读部分回收）
            if (_readPos > 0)
            {
                int readable = ReadableBytes;
                Buffer.BlockCopy(_buffer, _readPos, _buffer, 0, readable);
                _readPos = 0;
                _writePos = readable;

                if (WritableBytes >= bytes)
                    return;
            }

            // compact 后还不够，扩容
            int newSize = _buffer.Length;
            while (newSize - _writePos < bytes)
                newSize *= 2;

            var newBuffer = new byte[newSize];
            Buffer.BlockCopy(_buffer, _readPos, newBuffer, 0, ReadableBytes);
            _writePos = ReadableBytes;
            _readPos = 0;
            _buffer = newBuffer;
        }
    }
}
