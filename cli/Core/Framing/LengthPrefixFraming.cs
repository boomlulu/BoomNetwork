using System;
using System.Buffers;
using System.Collections.Generic;
using BoomNetwork.Core.Codec;

namespace BoomNetwork.Core.Framing
{
    /// <summary>
    /// 从 ArrayPool 租借的帧缓冲区
    /// 使用完后必须调用 Dispose() 归还。
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
    /// 粘包/拆包处理器 — 适配动态包头格式
    ///
    /// 通过 MessageCodec.PeekFrameSize 探测完整帧的长度，
    /// 支持动态包头（BodyLen 2B 或 4B）。
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
        /// 喂入原始字节，自动拆分出完整帧
        /// </summary>
        public int Feed(byte[] data, int offset, int length)
        {
            _ring.Write(data, offset, length);

            int frameCount = 0;

            while (_ring.ReadableBytes >= Message.MinHeaderSize)
            {
                var readable = _ring.ReadableSpan;
                int frameSize = MessageCodec.PeekFrameSize(readable);

                if (frameSize < 0 || _ring.ReadableBytes < frameSize)
                    break;

                var pooledBuf = ArrayPool<byte>.Shared.Rent(frameSize);
                readable.Slice(0, frameSize).CopyTo(pooledBuf);
                _ring.Consume(frameSize);

                _frames.Enqueue(new PooledFrame { Buffer = pooledBuf, Length = frameSize });
                frameCount++;
            }

            return frameCount;
        }

        public bool TryDequeueFrame(out PooledFrame frame)
        {
            return _frames.TryDequeue(out frame);
        }

        public int PendingFrames => _frames.Count;

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
