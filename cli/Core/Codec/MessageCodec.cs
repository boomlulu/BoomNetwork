using System;
using System.Buffers;
using System.Buffers.Binary;

namespace BoomNetwork.Core.Codec
{
    /// <summary>
    /// 消息编解码器 — 三层 Cmd 动态包头
    ///
    /// Wire: [FlagsCmd:1][BodyLen:2/4][Seq:0/4][ExtCmd/GameCmd:0/2/4][Data:N]
    ///
    /// FlagsCmd byte:
    ///   bit 0:   LenSize  (0=2B, 1=4B)
    ///   bit 1:   HasSeq   (0=无, 1=4B)
    ///   bit 2-3: CmdType  (00=Core, 01=Extended, 10=Game)
    ///   bit 4-7: CoreCmd  (0-15, 仅 CmdType=Core)
    /// </summary>
    public static class MessageCodec
    {
        public static int Encode(in Message msg, Span<byte> output)
        {
            int totalSize = msg.TotalSize;
            if (output.Length < totalSize)
                throw new ArgumentException($"Buffer too small: need {totalSize}, got {output.Length}");

            int dataLen = msg.DataLength;
            int extra = msg.CmdExtraSize;
            int totalPayload = dataLen + extra;
            bool largeLen = totalPayload > 65530;

            // FlagsCmd
            byte flagsCmd = (byte)(((byte)msg.MsgType & 0x03) << 2);
            if (msg.MsgType == CmdType.Core)
                flagsCmd |= (byte)((msg.Cmd & 0x0F) << 4);
            if (largeLen) flagsCmd |= (byte)MessageFlags.LenSize4;
            if (msg.HasSeq) flagsCmd |= (byte)MessageFlags.HasSeq;
            output[0] = flagsCmd;
            int offset = 1;

            // BodyLen
            int bodyLen = totalPayload + (msg.HasSeq ? 4 : 0);
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

            // ExtCmd / GameCmd
            switch (msg.MsgType)
            {
                case CmdType.Extended:
                    BinaryPrimitives.WriteUInt16LittleEndian(output.Slice(offset), msg.ExtCmd);
                    offset += 2;
                    break;
                case CmdType.Game:
                    BinaryPrimitives.WriteUInt32LittleEndian(output.Slice(offset), msg.GameCmd);
                    offset += 4;
                    break;
            }

            // Data
            if (dataLen > 0)
            {
                msg.Data.AsSpan(0, dataLen).CopyTo(output.Slice(offset));
                offset += dataLen;
            }

            return offset;
        }

        public static int EncodedSize(in Message msg) => msg.TotalSize;

        public static Message Decode(ReadOnlySpan<byte> buffer) => Decode(buffer, usePool: false);

        public static Message Decode(ReadOnlySpan<byte> buffer, bool usePool)
        {
            if (buffer.Length < Message.MinHeaderSize)
                throw new ArgumentException($"Buffer too short: {buffer.Length}");

            byte flagsCmd = buffer[0];
            bool largeLen = (flagsCmd & (byte)MessageFlags.LenSize4) != 0;
            bool hasSeq = (flagsCmd & (byte)MessageFlags.HasSeq) != 0;
            var cmdType = (CmdType)((flagsCmd >> 2) & 0x03);
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

            var msg = new Message
            {
                MsgType = cmdType,
                HasSeq = hasSeq,
            };

            int remainLen = bodyLen;

            // Seq
            if (hasSeq)
            {
                msg.Seq = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset));
                offset += 4;
                remainLen -= 4;
            }

            // Cmd / ExtCmd / GameCmd
            switch (cmdType)
            {
                case CmdType.Core:
                    msg.Cmd = (byte)(flagsCmd >> 4);
                    break;
                case CmdType.Extended:
                    msg.ExtCmd = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset));
                    offset += 2;
                    remainLen -= 2;
                    break;
                case CmdType.Game:
                    msg.GameCmd = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(offset));
                    offset += 4;
                    remainLen -= 4;
                    break;
            }

            // Data
            if (remainLen > 0)
            {
                if (usePool)
                {
                    msg.Data = ArrayPool<byte>.Shared.Rent(remainLen);
                    buffer.Slice(offset, remainLen).CopyTo(msg.Data);
                }
                else
                {
                    msg.Data = buffer.Slice(offset, remainLen).ToArray();
                }
                msg.DataLength = remainLen;
            }
            else
            {
                msg.Data = Array.Empty<byte>();
                msg.DataLength = 0;
            }

            return msg;
        }

        public static int PeekFrameSize(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length < 1)
                return -1;

            byte flagsCmd = buffer[0];
            bool largeLen = (flagsCmd & (byte)MessageFlags.LenSize4) != 0;
            int lenFieldSize = largeLen ? 4 : 2;

            if (buffer.Length < 1 + lenFieldSize)
                return -1;

            int bodyLen = largeLen
                ? BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(1))
                : BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(1));

            return 1 + lenFieldSize + bodyLen;
        }

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
