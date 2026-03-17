using System;
using System.Buffers.Binary;

namespace BoomNetwork.Core.Codec
{
    /// <summary>
    /// 消息编解码器 — 无状态，纯函数
    /// Message ↔ bytes，不含 length-prefix header（由 Framing 层处理）
    /// </summary>
    public static class MessageCodec
    {
        /// <summary>
        /// 编码 Message 为完整的线格式 bytes（含 4 字节 BodyLen 前缀）
        /// </summary>
        /// <returns>写入的总字节数</returns>
        public static int Encode(in Message msg, Span<byte> output)
        {
            int dataLen = msg.Data?.Length ?? 0;
            int bodyLen = Message.BodyHeaderSize + dataLen;
            int totalLen = Message.HeaderSize + bodyLen;

            if (output.Length < totalLen)
                throw new ArgumentException($"Output buffer too small: need {totalLen}, got {output.Length}");

            // BodyLen (4 bytes, little-endian)
            BinaryPrimitives.WriteInt32LittleEndian(output, bodyLen);
            var span = output.Slice(Message.HeaderSize);

            // Version (1 byte)
            span[0] = msg.Version;
            span = span.Slice(1);

            // Cmd (4 bytes)
            BinaryPrimitives.WriteUInt32LittleEndian(span, msg.Cmd);
            span = span.Slice(4);

            // ClientSeq (4 bytes)
            BinaryPrimitives.WriteInt32LittleEndian(span, msg.ClientSeq);
            span = span.Slice(4);

            // ServerSeq (4 bytes)
            BinaryPrimitives.WriteInt32LittleEndian(span, msg.ServerSeq);
            span = span.Slice(4);

            // Data
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
        /// 从完整的线格式 bytes 解码（含 4 字节 BodyLen 前缀）
        /// </summary>
        public static Message Decode(ReadOnlySpan<byte> buffer)
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
                msg.Data = body.Slice(Message.BodyHeaderSize, dataLen).ToArray();
            }
            else
            {
                msg.Data = Array.Empty<byte>();
            }

            return msg;
        }
    }
}
