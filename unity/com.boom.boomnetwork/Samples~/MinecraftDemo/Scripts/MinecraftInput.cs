// BoomNetwork MinecraftDemo — Input Encoder/Decoder for Frame Sync

using System;
using Unity.Mathematics;

namespace BoomNetwork.Samples.MinecraftDemo
{
    /// <summary>
    /// Encodes/decodes block actions for frame sync SendInput.
    /// Wire format: [actionType:1B][x:2B][y:2B][z:2B][blockType:1B] = 8 bytes
    /// </summary>
    public static class MinecraftInput
    {
        public const int InputSize = 8;

        public static void Encode(byte[] buffer, int offset,
            byte actionType, int3 position, BlockType blockType)
        {
            buffer[offset + 0] = actionType;
            WriteInt16(buffer, offset + 1, (short)position.x);
            WriteInt16(buffer, offset + 3, (short)position.y);
            WriteInt16(buffer, offset + 5, (short)position.z);
            buffer[offset + 7] = (byte)blockType;
        }

        public static void Decode(byte[] buffer, int offset,
            out byte actionType, out int3 position, out BlockType blockType)
        {
            actionType = buffer[offset + 0];
            position = new int3(
                ReadInt16(buffer, offset + 1),
                ReadInt16(buffer, offset + 3),
                ReadInt16(buffer, offset + 5));
            blockType = (BlockType)buffer[offset + 7];
        }

        public static void Decode(ReadOnlySpan<byte> span,
            out byte actionType, out int3 position, out BlockType blockType)
        {
            actionType = span[0];
            position = new int3(
                (short)(span[1] | (span[2] << 8)),
                (short)(span[3] | (span[4] << 8)),
                (short)(span[5] | (span[6] << 8)));
            blockType = (BlockType)span[7];
        }

        static void WriteInt16(byte[] buf, int offset, short value)
        {
            buf[offset] = (byte)(value & 0xFF);
            buf[offset + 1] = (byte)((value >> 8) & 0xFF);
        }

        static short ReadInt16(byte[] buf, int offset)
        {
            return (short)(buf[offset] | (buf[offset + 1] << 8));
        }
    }
}
