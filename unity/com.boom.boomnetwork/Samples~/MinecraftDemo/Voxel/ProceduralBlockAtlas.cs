// BoomNetwork MinecraftDemo — Procedural Block Atlas Generator
// Generates a simple colored block atlas texture at runtime (no external assets needed)

using UnityEngine;

namespace BoomNetwork.Samples.MinecraftDemo
{
    /// <summary>
    /// Generates a procedural 8×8 tile atlas texture with simple block colors.
    /// Each block type has 6 consecutive tiles (one per face direction: +X -X +Y -Y +Z -Z).
    /// Tiles have subtle shading/variation per face for visual interest.
    /// </summary>
    public static class ProceduralBlockAtlas
    {
        const int TileSize = 16; // pixels per tile
        const int AtlasCols = VoxelConstants.AtlasCols;
        const int AtlasRows = VoxelConstants.AtlasRows;

        // Base colors for each block type (index matches BlockType enum)
        static readonly Color[] BaseColors =
        {
            new Color(0, 0, 0, 0),          // 0: Air (transparent)
            new Color(0.5f, 0.5f, 0.5f),    // 1: Stone (gray)
            new Color(0.55f, 0.35f, 0.2f),  // 2: Dirt (brown)
            new Color(0.35f, 0.65f, 0.2f),  // 3: Grass (green — top face)
            new Color(0.6f, 0.45f, 0.25f),  // 4: Wood (tan — side face)
            new Color(0.2f, 0.55f, 0.15f),  // 5: Leaf (dark green)
            new Color(0.85f, 0.78f, 0.55f), // 6: Sand (sandy)
            new Color(0.2f, 0.4f, 0.8f),    // 7: Water (blue)
        };

        // Face brightness multipliers: +X, -X, +Y, -Y, +Z, -Z
        static readonly float[] FaceBrightness = { 0.85f, 0.75f, 1.0f, 0.6f, 0.9f, 0.8f };

        public static Texture2D Generate()
        {
            int width = AtlasCols * TileSize;
            int height = AtlasRows * TileSize;
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point, // pixelated look
                wrapMode = TextureWrapMode.Clamp,
            };

            // Fill with transparent
            var pixels = new Color[width * height];

            for (int blockType = 0; blockType < BlockTypeExt.Count; blockType++)
            {
                Color baseColor = BaseColors[blockType];

                for (int face = 0; face < 6; face++)
                {
                    int atlasIndex = blockType * 6 + face;
                    int col = atlasIndex % AtlasCols;
                    int row = atlasIndex / AtlasCols;

                    Color faceColor = GetFaceColor((BlockType)blockType, face, baseColor);

                    FillTile(pixels, width, col, row, faceColor);
                }
            }

            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }

        static Color GetFaceColor(BlockType block, int face, Color baseColor)
        {
            float brightness = FaceBrightness[face];

            switch (block)
            {
                case BlockType.Grass:
                    // Top = green, bottom = dirt, sides = dirt with green stripe at top
                    if (face == 2) // +Y (top)
                        return new Color(0.35f, 0.65f, 0.2f) * brightness;
                    if (face == 3) // -Y (bottom)
                        return new Color(0.55f, 0.35f, 0.2f) * brightness;
                    // Sides: dirt with slight green tint
                    return new Color(0.45f, 0.4f, 0.2f) * brightness;

                case BlockType.Wood:
                    // Top/bottom = rings, sides = bark
                    if (face == 2 || face == 3)
                        return new Color(0.55f, 0.4f, 0.2f) * brightness;
                    return new Color(0.6f, 0.45f, 0.25f) * brightness;

                default:
                    return baseColor * brightness;
            }
        }

        static void FillTile(Color[] pixels, int texWidth, int col, int row, Color color)
        {
            // Atlas Y is flipped: row 0 = bottom of texture
            int baseX = col * TileSize;
            int baseY = (AtlasRows - 1 - row) * TileSize;

            for (int py = 0; py < TileSize; py++)
            {
                for (int px = 0; px < TileSize; px++)
                {
                    // Add subtle noise for texture
                    float noise = ((px * 7 + py * 13) % 17) / 170f - 0.05f;
                    Color c = new Color(
                        Mathf.Clamp01(color.r + noise),
                        Mathf.Clamp01(color.g + noise),
                        Mathf.Clamp01(color.b + noise),
                        color.a > 0 ? 1f : 0f);

                    // Border darkening (1px edge)
                    if (px == 0 || py == 0 || px == TileSize - 1 || py == TileSize - 1)
                    {
                        c.r *= 0.8f;
                        c.g *= 0.8f;
                        c.b *= 0.8f;
                    }

                    pixels[(baseY + py) * texWidth + (baseX + px)] = c;
                }
            }
        }

        /// <summary>Create a material using the BlockAtlas shader and procedural texture.</summary>
        public static Material CreateMaterial()
        {
            var shader = Shader.Find("BoomNetwork/BlockAtlas");
            if (shader == null)
            {
                Debug.LogWarning("BlockAtlas shader not found, falling back to Standard");
                shader = Shader.Find("Standard");
            }

            var mat = new Material(shader);
            var tex = Generate();
            mat.mainTexture = tex;
            mat.SetVector("_AtlasSize", new Vector4(AtlasCols, AtlasRows, 0, 0));
            return mat;
        }
    }
}
