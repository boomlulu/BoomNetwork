// BoomNetwork MinecraftDemo — Procedural World Generator

using Unity.Mathematics;

namespace BoomNetwork.Samples.MinecraftDemo
{
    /// <summary>
    /// Generates terrain using 2D simplex noise heightmap.
    /// Deterministic: same seed → same world.
    /// </summary>
    public static class WorldGenerator
    {
        // Terrain parameters
        const float Frequency = 0.02f;
        const int Octaves = 4;
        const float Persistence = 0.5f;
        const int SeaLevel = 20;
        const int MaxHeight = 50;         // max terrain height in blocks
        const int DirtDepth = 4;          // dirt layers below grass
        const int SandBeachHeight = 22;   // sand below this level

        /// <summary>
        /// Fill a chunk with terrain blocks based on world-space noise.
        /// </summary>
        public static void GenerateChunk(ChunkData chunk, int seed)
        {
            int3 origin = chunk.WorldOrigin;

            for (int lx = 0; lx < VoxelConstants.ChunkSizeX; lx++)
            {
                for (int lz = 0; lz < VoxelConstants.ChunkSizeZ; lz++)
                {
                    int wx = origin.x + lx;
                    int wz = origin.z + lz;

                    // 2D heightmap noise
                    float n = SimplexNoise.FBM2D(
                        wx * Frequency + seed * 0.1f,
                        wz * Frequency + seed * 0.1f,
                        Octaves, 1f, Persistence);

                    int surfaceY = (int)(n * MaxHeight) + SeaLevel;

                    for (int ly = 0; ly < VoxelConstants.ChunkSizeY; ly++)
                    {
                        int wy = origin.y + ly;
                        BlockType block;

                        if (wy > surfaceY)
                        {
                            // Above surface: water if below sea level, else air
                            block = wy <= SeaLevel ? BlockType.Water : BlockType.Air;
                        }
                        else if (wy == surfaceY)
                        {
                            // Surface block
                            if (wy <= SandBeachHeight)
                                block = BlockType.Sand;
                            else
                                block = BlockType.Grass;
                        }
                        else if (wy > surfaceY - DirtDepth)
                        {
                            // Below surface: dirt layer
                            block = wy <= SandBeachHeight ? BlockType.Sand : BlockType.Dirt;
                        }
                        else
                        {
                            // Deep underground: stone
                            block = BlockType.Stone;
                        }

                        chunk.Blocks[VoxelConstants.To1D(lx, ly, lz)] = block;
                    }
                }
            }

            chunk.IsDirty = true;
        }
    }
}
