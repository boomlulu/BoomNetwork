// BoomNetwork MinecraftDemo — Voxel Constants

using Unity.Mathematics;

namespace BoomNetwork.Samples.MinecraftDemo
{
    public static class VoxelConstants
    {
        // Chunk dimensions (standard Minecraft-style 16×16×16)
        public const int ChunkSizeX = 16;
        public const int ChunkSizeY = 16;
        public const int ChunkSizeZ = 16;
        public const int ChunkVolume = ChunkSizeX * ChunkSizeY * ChunkSizeZ; // 4096

        // World dimensions in chunks
        public const int WorldChunksX = 8;
        public const int WorldChunksY = 4;
        public const int WorldChunksZ = 8;

        // World dimensions in blocks
        public const int WorldBlocksX = WorldChunksX * ChunkSizeX; // 128
        public const int WorldBlocksY = WorldChunksY * ChunkSizeY; // 64
        public const int WorldBlocksZ = WorldChunksZ * ChunkSizeZ; // 128

        // Chunk loading
        public const int LoadRadiusH = 4;  // horizontal chunk radius around player
        public const int LoadRadiusV = 2;  // vertical chunk radius around player

        // Atlas: 8 columns × 8 rows, each block type uses 6 consecutive tiles
        public const int AtlasCols = 8;
        public const int AtlasRows = 8;

        // 6 face directions: +X, -X, +Y, -Y, +Z, -Z
        public static readonly int3[] DirectionOffsets = new int3[]
        {
            new int3(1, 0, 0),   // 0: Right  (+X)
            new int3(-1, 0, 0),  // 1: Left   (-X)
            new int3(0, 1, 0),   // 2: Up     (+Y)
            new int3(0, -1, 0),  // 3: Down   (-Y)
            new int3(0, 0, 1),   // 4: Front  (+Z)
            new int3(0, 0, -1),  // 5: Back   (-Z)
        };

        // For greedy meshing: which axes to sweep for each direction
        // [direction] => (sweepAxisU, sweepAxisV, depthAxis)
        // Example: direction 0 (+X) sweeps Y,Z at fixed X depth
        public static readonly int3[] DirectionAlignedAxes = new int3[]
        {
            new int3(1, 2, 0), // +X: U=Y, V=Z, depth=X
            new int3(1, 2, 0), // -X: U=Y, V=Z, depth=X
            new int3(0, 2, 1), // +Y: U=X, V=Z, depth=Y
            new int3(0, 2, 1), // -Y: U=X, V=Z, depth=Y
            new int3(0, 1, 2), // +Z: U=X, V=Y, depth=Z
            new int3(0, 1, 2), // -Z: U=X, V=Y, depth=Z
        };

        /// <summary>
        /// Flat array index from 3D coordinates. z-major order: z + y*Sz + x*Sy*Sz
        /// </summary>
        public static int To1D(int x, int y, int z)
        {
            return z + y * ChunkSizeZ + x * ChunkSizeY * ChunkSizeZ;
        }

        public static int To1D(int3 p) => To1D(p.x, p.y, p.z);

        public static int3 To3D(int index)
        {
            int x = index / (ChunkSizeY * ChunkSizeZ);
            int remainder = index % (ChunkSizeY * ChunkSizeZ);
            int y = remainder / ChunkSizeZ;
            int z = remainder % ChunkSizeZ;
            return new int3(x, y, z);
        }

        /// <summary>World block position → chunk coordinate</summary>
        public static int3 WorldToChunk(int3 worldPos)
        {
            return new int3(
                FloorDiv(worldPos.x, ChunkSizeX),
                FloorDiv(worldPos.y, ChunkSizeY),
                FloorDiv(worldPos.z, ChunkSizeZ));
        }

        /// <summary>World block position → local position within chunk</summary>
        public static int3 WorldToLocal(int3 worldPos)
        {
            return new int3(
                FloorMod(worldPos.x, ChunkSizeX),
                FloorMod(worldPos.y, ChunkSizeY),
                FloorMod(worldPos.z, ChunkSizeZ));
        }

        /// <summary>Chunk coordinate + local position → world block position</summary>
        public static int3 ChunkToWorld(int3 chunkPos, int3 localPos)
        {
            return new int3(
                chunkPos.x * ChunkSizeX + localPos.x,
                chunkPos.y * ChunkSizeY + localPos.y,
                chunkPos.z * ChunkSizeZ + localPos.z);
        }

        static int FloorDiv(int a, int b)
        {
            return a >= 0 ? a / b : (a - b + 1) / b;
        }

        static int FloorMod(int a, int b)
        {
            int r = a % b;
            return r < 0 ? r + b : r;
        }
    }
}
