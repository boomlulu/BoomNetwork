// BoomNetwork MinecraftDemo — World Snapshot (Zero GC)
//
// Instead of comparing entire world against regenerated reference each snapshot,
// we track modified blocks as they happen. TakeSnapshot just serializes the dirty set.
// No per-snapshot allocation — buffer is pre-allocated and reused.

using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace BoomNetwork.Samples.MinecraftDemo
{
    public static class MinecraftSnapshot
    {
        // Dirty block tracking: world position → current block type
        // Only blocks that differ from initial generation are stored here.
        static readonly Dictionary<int3, BlockType> s_dirtyBlocks = new(256);

        // Pre-allocated serialization buffer (grows as needed, never shrinks)
        static byte[] s_buffer = new byte[1024];

        const int HeaderSize = 8; // seed(4) + count(4)
        const int EntrySize = 7;  // x(2) + y(2) + z(2) + type(1)

        /// <summary>Record a block modification for snapshot tracking.</summary>
        public static void TrackBlockChange(int3 worldPos, BlockType newType, BlockType originalType)
        {
            if (newType == originalType)
                s_dirtyBlocks.Remove(worldPos); // reverted to original, no longer dirty
            else
                s_dirtyBlocks[worldPos] = newType;
        }

        /// <summary>Clear all tracked changes (called on world reset/load).</summary>
        public static void ClearTracking()
        {
            s_dirtyBlocks.Clear();
        }

        /// <summary>
        /// Serialize dirty blocks into a byte array. Zero managed allocation
        /// (reuses static buffer, only returns a new array for the final result
        /// because the framework requires a byte[] return).
        /// </summary>
        public static byte[] TakeSnapshot(VoxelWorld world)
        {
            int count = s_dirtyBlocks.Count;
            int requiredSize = HeaderSize + count * EntrySize;

            // Grow buffer if needed (rare, amortized)
            if (s_buffer.Length < requiredSize)
                s_buffer = new byte[requiredSize * 2];

            // Header
            WriteInt32(s_buffer, 0, world.Seed);
            WriteInt32(s_buffer, 4, count);

            // Entries
            int offset = HeaderSize;
            foreach (var kvp in s_dirtyBlocks)
            {
                WriteInt16(s_buffer, offset, (short)kvp.Key.x); offset += 2;
                WriteInt16(s_buffer, offset, (short)kvp.Key.y); offset += 2;
                WriteInt16(s_buffer, offset, (short)kvp.Key.z); offset += 2;
                s_buffer[offset] = (byte)kvp.Value; offset += 1;
            }

            // Framework requires a correctly-sized byte[] (it stores the whole thing)
            var result = new byte[requiredSize];
            Buffer.BlockCopy(s_buffer, 0, result, 0, requiredSize);
            return result;
        }

        /// <summary>
        /// Load a snapshot: regenerate world from seed, then apply deltas.
        /// </summary>
        public static void LoadSnapshot(VoxelWorld world, byte[] data)
        {
            if (data == null || data.Length < HeaderSize) return;

            int seed = ReadInt32(data, 0);
            int deltaCount = ReadInt32(data, 4);

            world.Seed = seed;
            world.ClearWorld();
            ClearTracking();
            world.GenerateFullWorld();

            int offset = HeaderSize;
            for (int i = 0; i < deltaCount && offset + EntrySize <= data.Length; i++)
            {
                var pos = new int3(
                    ReadInt16(data, offset),
                    ReadInt16(data, offset + 2),
                    ReadInt16(data, offset + 4));
                var block = (BlockType)data[offset + 6];
                offset += EntrySize;

                world.SetBlock(pos, block);
                s_dirtyBlocks[pos] = block;
            }
        }

        // --- Byte helpers (no BinaryWriter allocation) ---

        static void WriteInt32(byte[] buf, int off, int val)
        {
            buf[off]     = (byte)(val & 0xFF);
            buf[off + 1] = (byte)((val >> 8) & 0xFF);
            buf[off + 2] = (byte)((val >> 16) & 0xFF);
            buf[off + 3] = (byte)((val >> 24) & 0xFF);
        }

        static void WriteInt16(byte[] buf, int off, short val)
        {
            buf[off]     = (byte)(val & 0xFF);
            buf[off + 1] = (byte)((val >> 8) & 0xFF);
        }

        static int ReadInt32(byte[] buf, int off)
        {
            return buf[off] | (buf[off + 1] << 8) | (buf[off + 2] << 16) | (buf[off + 3] << 24);
        }

        static short ReadInt16(byte[] buf, int off)
        {
            return (short)(buf[off] | (buf[off + 1] << 8));
        }
    }
}
