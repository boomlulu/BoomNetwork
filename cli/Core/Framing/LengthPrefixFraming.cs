using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace BoomNetwork.Core.Framing
{
    /// <summary>
    /// 从 ArrayPool 租借的帧缓冲区
    /// 使用完后必须调用 Return() 归还，避免内存泄漏。
    /// </summary>
    public struct PooledFrame : IDisposable
    {
        public byte[] Buffer;
        public int Length;

        public ReadOnlySpan<byte> Span => Buffer.AsSpan(0, Length);

        public void Dispose()
        {
            if (Buffer != null)
            {
                ArrayPool<byte>.Shared.Return(Buffer);
                Buffer = null!;
            }
        }
    }

    /// <summary>
    /// Length-Prefix 粘包/拆包处理器
    ///
    /// 线格式: [BodyLen:4 bytes LE][Body: BodyLen bytes]
    ///
    /// 优化:
    ///   - 内部用 RingBuffer，避免数据前移的 O(n) 开销
    ///   - 输出帧使用 ArrayPool 租借，调用方用完后归还
    /// </summary>
    public class LengthPrefixFraming
    {
        private readonly RingBuffer _ring;
        private readonly Queue<PooledFrame> _frames = new();

        public LengthPrefixFraming(int initialBufferSize = 8192)
        {
            _ring = new RingBuffer(initialBufferSize);
        }

        /// <summary>
        /// 喂入原始字节，内部自动拆分出完整帧
        /// </summary>
        /// <returns>本次解析出的完整帧数量</returns>
        public int Feed(byte[] data, int offset, int length)
        {
            _ring.Write(data, offset, length);

            int frameCount = 0;

            while (_ring.ReadableBytes >= Message.HeaderSize)
            {
                var readable = _ring.ReadableSpan;
                int bodyLen = BinaryPrimitives.ReadInt32LittleEndian(readable);
                int totalLen = Message.HeaderSize + bodyLen;

                if (_ring.ReadableBytes < totalLen)
                    break;

                // 从 ArrayPool 租借，拷贝完整帧
                var pooledBuf = ArrayPool<byte>.Shared.Rent(totalLen);
                _ring.ReadableSpan.Slice(0, totalLen).CopyTo(pooledBuf);
                _ring.Consume(totalLen);

                _frames.Enqueue(new PooledFrame { Buffer = pooledBuf, Length = totalLen });
                frameCount++;
            }

            return frameCount;
        }

        /// <summary>
        /// 取出一个完整帧（从 ArrayPool 租借的，用完后调 Dispose 归还）
        /// </summary>
        public bool TryDequeueFrame(out PooledFrame frame)
        {
            return _frames.TryDequeue(out frame);
        }

        /// <summary>
        /// 待取出的帧数量
        /// </summary>
        public int PendingFrames => _frames.Count;

        /// <summary>
        /// 重置状态（归还所有未消费的帧）
        /// </summary>
        public void Reset()
        {
            _ring.Reset();
            while (_frames.TryDequeue(out var f))
            {
                f.Dispose();
            }
        }
    }
}
