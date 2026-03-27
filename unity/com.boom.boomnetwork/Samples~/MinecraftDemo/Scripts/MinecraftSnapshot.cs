// BoomNetwork MinecraftDemo — World Snapshot Serialization

using System;
using System.Collections.Generic;
using System.IO;
using Unity.Mathematics;

namespace BoomNetwork.Samples.MinecraftDemo
{
    /// <summary>
    /// Serializes/deserializes world block changes for snapshot-based reconnection.
    /// Uses delta-only encoding: only stores blocks that differ from initial generation.
    /// Format: [seed:4B][deltaCount:4B][deltas...] where each delta = [worldX:2B][worldY:2B][worldZ:2B][blockType:1B]
    /// </summary>
    public static class MinecraftSnapshot
    {
        const int DeltaEntrySize = 7; // 2+2+2+1

        /// <summary>
        /// Take a snapshot of world changes (blocks that differ from initial generation).
        /// </summary>
        public static byte[] TakeSnapshot(VoxelWorld world)
        {
            var deltas = new List<(int3 pos, BlockType block)>();

            // Compare each loaded chunk against freshly generated data
            foreach (var kvp in world.Chunks)
            {
                int3 cp = kvp.Key;
                ChunkData chunk = kvp.Value;
                int3 origin = chunk.WorldOrigin;

                // Generate a reference chunk to compare against
                var refChunk = new ChunkData(cp);
                WorldGenerator.GenerateChunk(refChunk, world.Seed);

                for (int i = 0; i < VoxelConstants.ChunkVolume; i++)
                {
                    if (chunk.Blocks[i] != refChunk.Blocks[i])
                    {
                        int3 local = VoxelConstants.To3D(i);
                        int3 worldPos = origin + local;
                        deltas.Add((worldPos, chunk.Blocks[i]));
                    }
                }
            }

            // Serialize
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);

            bw.Write(world.Seed);
            bw.Write(deltas.Count);
            foreach (var (pos, block) in deltas)
            {
                bw.Write((short)pos.x);
                bw.Write((short)pos.y);
                bw.Write((short)pos.z);
                bw.Write((byte)block);
            }

            return ms.ToArray();
        }

        /// <summary>
        /// Load a snapshot: regenerate world from seed, then apply deltas.
        /// </summary>
        public static void LoadSnapshot(VoxelWorld world, byte[] data)
        {
            if (data == null || data.Length < 8) return;

            using var ms = new MemoryStream(data);
            using var br = new BinaryReader(ms);

            int seed = br.ReadInt32();
            int deltaCount = br.ReadInt32();

            // Regenerate world with saved seed
            world.Seed = seed;
            world.ClearWorld();
            world.GenerateFullWorld();

            // Apply deltas
            for (int i = 0; i < deltaCount; i++)
            {
                int3 pos = new int3(
                    br.ReadInt16(),
                    br.ReadInt16(),
                    br.ReadInt16());
                BlockType block = (BlockType)br.ReadByte();
                world.SetBlock(pos, block);
            }
        }

        /// <summary>
        /// Estimate snapshot size in bytes.
        /// </summary>
        public static int EstimateSize(int deltaCount)
        {
            return 8 + deltaCount * DeltaEntrySize;
        }
    }
}
