using System;
using System.Buffers;
using System.Buffers.Binary;

namespace BoomNetwork.Core.Codec
{
    /// <summary>
    /// 消息编解码器 — 无状态，纯函数
    /// Message ↔ bytes
    /// </summary>
    public static class MessageCodec
    {
        /// <summary>
        /// 编码 Message 为完整的线格式 bytes（含 4 字节 BodyLen 前缀）
        /// </summary>
        public static int Encode(in Message msg, Span<byte> output)
        {
            int dataLen = msg.Data?.Length ?? 0;
            int bodyLen = Message.BodyHeaderSize + dataLen;
            int totalLen = Message.HeaderSize + bodyLen;

            if (output.Length < totalLen)
                throw new ArgumentException($"Output buffer too small: need {totalLen}, got {output.Length}");

            BinaryPrimitives.WriteInt32LittleEndian(output, bodyLen);
            var span = output.Slice(Message.HeaderSize);

            span[0] = msg.Version;
            span = span.Slice(1);

            BinaryPrimitives.WriteUInt32LittleEndian(span, msg.Cmd);
            span = span.Slice(4);

            BinaryPrimitives.WriteInt32LittleEndian(span, msg.ClientSeq);
            span = span.Slice(4);

            BinaryPrimitives.WriteInt32LittleEndian(span, msg.ServerSeq);
            span = span.Slice(4);

            if (dataLen > 0)
            {
                msg.Data.AsSpan().CopyTo(span);
            }

            return totalLen;
        }

        /// <summary>
        /// 编码后的总长度
        /// </summary>
        public static int EncodedSize(in Message msg)
        {
            return Message.HeaderSize + Message.BodyHeaderSize + (msg.Data?.Length ?? 0);
        }

        /// <summary>
        /// 解码（Data 使用 ArrayPool 租借，调用方负责归还）
        /// </summary>
        public static Message Decode(ReadOnlySpan<byte> buffer)
        {
            return Decode(buffer, usePool: false);
        }

        /// <summary>
        /// 解码，可选择是否从 ArrayPool 租借 Data 缓冲区
        /// usePool=true 时，调用方必须通过 ReturnData 归还 Data
        /// </summary>
        public static Message Decode(ReadOnlySpan<byte> buffer, bool usePool)
        {
            if (buffer.Length < Message.HeaderSize + Message.BodyHeaderSize)
                throw new ArgumentException($"Buffer too short: {buffer.Length}");

            int bodyLen = BinaryPrimitives.ReadInt32LittleEndian(buffer);
            var body = buffer.Slice(Message.HeaderSize);

            var msg = new Message
            {
                Version = body[0],
                Cmd = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(1)),
                ClientSeq = BinaryPrimitives.ReadInt32LittleEndian(body.Slice(5)),
                ServerSeq = BinaryPrimitives.ReadInt32LittleEndian(body.Slice(9)),
            };

            int dataLen = bodyLen - Message.BodyHeaderSize;
            if (dataLen > 0)
            {
                if (usePool)
                {
                    msg.Data = ArrayPool<byte>.Shared.Rent(dataLen);
                    body.Slice(Message.BodyHeaderSize, dataLen).CopyTo(msg.Data);
                    msg.DataLength = dataLen;
                }
                else
                {
                    msg.Data = body.Slice(Message.BodyHeaderSize, dataLen).ToArray();
                    msg.DataLength = dataLen;
                }
            }
            else
            {
                msg.Data = Array.Empty<byte>();
                msg.DataLength = 0;
            }

            return msg;
        }

        /// <summary>
        /// 归还从 ArrayPool 租借的 Data 缓冲区
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
