// BoomNetwork MinecraftDemo — Voxel Block Types
// Inspired by Minecraft4Unity (MIT License, paternostrox)

namespace BoomNetwork.Samples.MinecraftDemo
{
    /// <summary>
    /// Block type enum. Air must be 0. Order determines atlas row index.
    /// Atlas layout: each block type has 6 consecutive tiles (one per face direction).
    /// </summary>
    public enum BlockType : byte
    {
        Air = 0,
        Stone = 1,
        Dirt = 2,
        Grass = 3,    // top=grass, side=grass_side, bottom=dirt
        Wood = 4,     // top/bottom=wood_top, side=wood_side
        Leaf = 5,
        Sand = 6,
        Water = 7,
    }

    public static class BlockTypeExt
    {
        public const int Count = 8;

        public static bool IsSolid(this BlockType b) => b != BlockType.Air && b != BlockType.Water;
        public static bool IsTransparent(this BlockType b) => b == BlockType.Air;
    }
}
