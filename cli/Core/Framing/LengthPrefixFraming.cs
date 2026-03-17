using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace BoomNetwork.Core.Framing
{
    /// <summary>
    /// Length-Prefix 粘包/拆包处理器
    ///
    /// 线格式: [BodyLen:4 bytes LE][Body: BodyLen bytes]
    ///
    /// 喂入原始字节流，输出完整的消息帧。
    /// 处理 TCP 粘包（一次收到多条）和拆包（一条分多次到达）。
    /// </summary>
    public class LengthPrefixFraming
    {
        private byte[] _accumBuffer;
        private int _accumLength;
        private readonly Queue<byte[]> _frames = new();

        public LengthPrefixFraming(int initialBufferSize = 4096)
        {
            _accumBuffer = new byte[initialBufferSize];
            _accumLength = 0;
        }

        /// <summary>
        /// 喂入原始字节，内部自动拆分出完整帧
        /// </summary>
        /// <returns>本次解析出的完整帧数量</returns>
        public int Feed(byte[] data, int offset, int length)
        {
            EnsureCapacity(length);
            Buffer.BlockCopy(data, offset, _accumBuffer, _accumLength, length);
            _accumLength += length;

            int frameCount = 0;

            while (_accumLength >= Message.HeaderSize)
            {
                int bodyLen = BinaryPrimitives.ReadInt32LittleEndian(
                    _accumBuffer.AsSpan(0, Message.HeaderSize));

                int totalLen = Message.HeaderSize + bodyLen;

                if (_accumLength < totalLen)
                    break; // 数据不完整，等下次 Feed

                // 提取完整帧
                var frame = new byte[totalLen];
                Buffer.BlockCopy(_accumBuffer, 0, frame, 0, totalLen);
                _frames.Enqueue(frame);
                frameCount++;

                // 移除已处理的数据
                int remaining = _accumLength - totalLen;
                if (remaining > 0)
                {
                    Buffer.BlockCopy(_accumBuffer, totalLen, _accumBuffer, 0, remaining);
                }
                _accumLength = remaining;
            }

            return frameCount;
        }

        /// <summary>
        /// 取出一个完整帧
        /// </summary>
        public bool TryDequeueFrame(out byte[] frame)
        {
            return _frames.TryDequeue(out frame!);
        }

        /// <summary>
        /// 待取出的帧数量
        /// </summary>
        public int PendingFrames => _frames.Count;

        /// <summary>
        /// 重置状态
        /// </summary>
        public void Reset()
        {
            _accumLength = 0;
            _frames.Clear();
        }

        private void EnsureCapacity(int additionalBytes)
        {
            int required = _accumLength + additionalBytes;
            if (required <= _accumBuffer.Length)
                return;

            int newSize = _accumBuffer.Length;
            while (newSize < required)
                newSize *= 2;

            var newBuffer = new byte[newSize];
            Buffer.BlockCopy(_accumBuffer, 0, newBuffer, 0, _accumLength);
            _accumBuffer = newBuffer;
        }
    }
}
