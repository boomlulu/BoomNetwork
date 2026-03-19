using System;
using System.Buffers;
using System.Buffers.Binary;

namespace BoomNetwork.Core.Codec
{
    /// <summary>
    /// 消息编解码器 — 新动态包头格式
    ///
    /// Wire: [FlagsCmd:1][BodyLen:2/4][Seq:0/4][Data:N]
    ///
    /// FlagsCmd byte:
    ///   bit 0:   LenSize (0=2B, 1=4B)
    ///   bit 1:   HasSeq  (0=无, 1=4B)
    ///   bit 2-7: Cmd     (0-63)
    /// </summary>
    public static class MessageCodec
    {
        /// <summary>
        /// 编码 Message（含包头）
        /// </summary>
        public static int Encode(in Message msg, Span<byte> output)
        {
            int totalSize = EncodedSize(msg);
            if (output.Length < totalSize)
                throw new ArgumentException($"Buffer too small: need {totalSize}, got {output.Length}");

            bool largeLen = msg.NeedLargeLen;
            int dataLen = msg.DataLength;

            // FlagsCmd
            byte flagsCmd = (byte)(msg.Cmd << 2);
            if (largeLen) flagsCmd |= (byte)MessageFlags.LenSize4;
            if (msg.HasSeq) flagsCmd |= (byte)MessageFlags.HasSeq;
            output[0] = flagsCmd;
            int offset = 1;

            // BodyLen (不含 FlagsCmd 本身，包含 BodyLen 之后的所有字节)
            int bodyLen = dataLen + (msg.HasSeq ? 4 : 0);
            if (largeLen)
            {
                BinaryPrimitives.WriteInt32LittleEndian(output.Slice(offset), bodyLen);
                offset += 4;
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(output.Slice(offset), (ushort)bodyLen);
                offset += 2;
            }

            // Seq
            if (msg.HasSeq)
            {
                BinaryPrimitives.WriteInt32LittleEndian(output.Slice(offset), msg.Seq);
                offset += 4;
            }

            // Data
            if (dataLen > 0)
            {
                msg.Data.AsSpan(0, dataLen).CopyTo(output.Slice(offset));
                offset += dataLen;
            }

            return offset;
        }

        /// <summary>
        /// 编码后的总大小
        /// </summary>
        public static int EncodedSize(in Message msg)
        {
            return msg.TotalSize;
        }

        /// <summary>
        /// 从 buffer 解码一条消息
        /// </summary>
        public static Message Decode(ReadOnlySpan<byte> buffer)
        {
            return Decode(buffer, usePool: false);
        }

        /// <summary>
        /// 解码，可选 ArrayPool
        /// </summary>
        public static Message Decode(ReadOnlySpan<byte> buffer, bool usePool)
        {
            if (buffer.Length < Message.MinHeaderSize)
                throw new ArgumentException($"Buffer too short: {buffer.Length}");

            byte flagsCmd = buffer[0];
            bool largeLen = (flagsCmd & (byte)MessageFlags.LenSize4) != 0;
            bool hasSeq = (flagsCmd & (byte)MessageFlags.HasSeq) != 0;
            byte cmd = (byte)(flagsCmd >> 2);
            int offset = 1;

            // BodyLen
            int bodyLen;
            if (largeLen)
            {
                bodyLen = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset));
                offset += 4;
            }
            else
            {
                bodyLen = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset));
                offset += 2;
            }

            // Seq
            int seq = 0;
            if (hasSeq)
            {
                seq = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset));
                offset += 4;
            }

            // Data
            int dataLen = bodyLen - (hasSeq ? 4 : 0);
            var msg = new Message
            {
                Cmd = cmd,
                Seq = seq,
                HasSeq = hasSeq,
            };

            if (dataLen > 0)
            {
                if (usePool)
                {
                    msg.Data = ArrayPool<byte>.Shared.Rent(dataLen);
                    buffer.Slice(offset, dataLen).CopyTo(msg.Data);
                }
                else
                {
                    msg.Data = buffer.Slice(offset, dataLen).ToArray();
                }
                msg.DataLength = dataLen;
            }
            else
            {
                msg.Data = Array.Empty<byte>();
                msg.DataLength = 0;
            }

            return msg;
        }

        /// <summary>
        /// 从 buffer 开头解析出总帧长度（用于 Framing 层）
        /// 返回 -1 表示数据不够
        /// </summary>
        public static int PeekFrameSize(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length < 1)
                return -1;

            byte flagsCmd = buffer[0];
            bool largeLen = (flagsCmd & (byte)MessageFlags.LenSize4) != 0;
            int lenFieldSize = largeLen ? 4 : 2;

            if (buffer.Length < 1 + lenFieldSize)
                return -1;

            int bodyLen;
            if (largeLen)
            {
                bodyLen = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(1));
            }
            else
            {
                bodyLen = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(1));
            }

            return 1 + lenFieldSize + bodyLen;
        }

        /// <summary>
        /// 归还 ArrayPool 的 Data
        /// </summary>
        public static void ReturnData(ref Message msg)
        {
            if (msg.Data != null && msg.Data.Length > 0 && msg.DataLength > 0)
            {
                ArrayPool<byte>.Shared.Return(msg.Data);
                msg.Data = Array.Empty<byte>();
                msg.DataLength = 0;
            }
        }
    }
}
