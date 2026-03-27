// BoomNetwork MinecraftDemo — Chunk Data (pure data, no MonoBehaviour)

using Unity.Mathematics;

namespace BoomNetwork.Samples.MinecraftDemo
{
    /// <summary>
    /// Pure data container for a 16×16×16 chunk of blocks.
    /// No Unity dependencies — can be used in Jobs.
    /// </summary>
    public class ChunkData
    {
        public readonly int3 ChunkPosition;
        public readonly BlockType[] Blocks;
        public bool IsDirty;

        public ChunkData(int3 chunkPos)
        {
            ChunkPosition = chunkPos;
            Blocks = new BlockType[VoxelConstants.ChunkVolume];
            IsDirty = true;
        }

        public BlockType GetBlock(int x, int y, int z)
        {
            if (x < 0 || x >= VoxelConstants.ChunkSizeX ||
                y < 0 || y >= VoxelConstants.ChunkSizeY ||
                z < 0 || z >= VoxelConstants.ChunkSizeZ)
                return BlockType.Air;
            return Blocks[VoxelConstants.To1D(x, y, z)];
        }

        public BlockType GetBlock(int3 local) => GetBlock(local.x, local.y, local.z);

        public void SetBlock(int x, int y, int z, BlockType type)
        {
            if (x < 0 || x >= VoxelConstants.ChunkSizeX ||
                y < 0 || y >= VoxelConstants.ChunkSizeY ||
                z < 0 || z >= VoxelConstants.ChunkSizeZ)
                return;
            Blocks[VoxelConstants.To1D(x, y, z)] = type;
            IsDirty = true;
        }

        public void SetBlock(int3 local, BlockType type) => SetBlock(local.x, local.y, local.z, type);

        /// <summary>World-space origin of this chunk in block units</summary>
        public int3 WorldOrigin => new int3(
            ChunkPosition.x * VoxelConstants.ChunkSizeX,
            ChunkPosition.y * VoxelConstants.ChunkSizeY,
            ChunkPosition.z * VoxelConstants.ChunkSizeZ);
    }
}
