// BoomNetwork MinecraftDemo — Greedy Meshing Chunk Mesh Builder
// Algorithm based on 0fps.net greedy meshing + Minecraft4Unity (MIT, paternostrox)

using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace BoomNetwork.Samples.MinecraftDemo
{
    /// <summary>
    /// Builds an optimized mesh for a chunk using greedy meshing.
    /// Merges adjacent coplanar faces of the same block type into larger quads.
    /// </summary>
    public static class ChunkMeshBuilder
    {
        // Reusable buffers to reduce GC
        static readonly List<Vector3> s_vertices = new List<Vector3>(4096);
        static readonly List<int> s_triangles = new List<int>(6144);
        static readonly List<Vector2> s_uvs = new List<Vector2>(4096);
        static readonly List<Color32> s_colors = new List<Color32>(4096);

        // Face normals for 6 directions
        static readonly Vector3[] s_normals6 =
        {
            Vector3.right,   // +X
            Vector3.left,    // -X
            Vector3.up,      // +Y
            Vector3.down,    // -Y
            Vector3.forward, // +Z
            Vector3.back,    // -Z
        };

        /// <summary>
        /// Build mesh for a chunk. Uses greedy meshing for face merging.
        /// Requires neighbor lookup function for cross-chunk face culling.
        /// </summary>
        /// <param name="chunk">The chunk data</param>
        /// <param name="getNeighborBlock">Returns block type at world position (for cross-chunk boundary checks)</param>
        /// <param name="mesh">Output mesh (cleared and filled)</param>
        public static void BuildMesh(ChunkData chunk, System.Func<int3, BlockType> getNeighborBlock, Mesh mesh)
        {
            s_vertices.Clear();
            s_triangles.Clear();
            s_uvs.Clear();
            s_colors.Clear();

            int sx = VoxelConstants.ChunkSizeX;
            int sy = VoxelConstants.ChunkSizeY;
            int sz = VoxelConstants.ChunkSizeZ;
            int3 origin = chunk.WorldOrigin;

            // For each of the 6 face directions
            for (int dir = 0; dir < 6; dir++)
            {
                int3 axes = VoxelConstants.DirectionAlignedAxes[dir];
                int axisU = axes.x; // sweep U axis
                int axisV = axes.y; // sweep V axis
                int axisD = axes.z; // depth axis

                int3 dirOffset = VoxelConstants.DirectionOffsets[dir];
                int sizeU = GetAxisSize(axisU);
                int sizeV = GetAxisSize(axisV);
                int sizeD = GetAxisSize(axisD);

                // For each depth slice
                for (int d = 0; d < sizeD; d++)
                {
                    // Build a 2D mask of which faces need rendering on this slice
                    var mask = new BlockType[sizeU * sizeV];
                    bool anyFace = false;

                    for (int u = 0; u < sizeU; u++)
                    {
                        for (int v = 0; v < sizeV; v++)
                        {
                            int3 pos = MakePos(axisU, u, axisV, v, axisD, d);
                            BlockType block = chunk.Blocks[VoxelConstants.To1D(pos)];

                            if (block.IsTransparent())
                            {
                                mask[u * sizeV + v] = BlockType.Air;
                                continue;
                            }

                            // Check neighbor in this direction
                            int3 neighborLocal = pos + new int3(dirOffset.x, dirOffset.y, dirOffset.z);
                            BlockType neighbor;

                            if (neighborLocal.x < 0 || neighborLocal.x >= sx ||
                                neighborLocal.y < 0 || neighborLocal.y >= sy ||
                                neighborLocal.z < 0 || neighborLocal.z >= sz)
                            {
                                // Cross-chunk boundary: ask world
                                int3 worldPos = origin + neighborLocal;
                                neighbor = getNeighborBlock(worldPos);
                            }
                            else
                            {
                                neighbor = chunk.Blocks[VoxelConstants.To1D(neighborLocal)];
                            }

                            if (neighbor.IsTransparent())
                            {
                                mask[u * sizeV + v] = block;
                                anyFace = true;
                            }
                            else
                            {
                                mask[u * sizeV + v] = BlockType.Air;
                            }
                        }
                    }

                    if (!anyFace) continue;

                    // Greedy merge: scan mask and merge adjacent same-type faces
                    var visited = new bool[sizeU * sizeV];

                    for (int u = 0; u < sizeU; u++)
                    {
                        for (int v = 0; v < sizeV; v++)
                        {
                            int idx = u * sizeV + v;
                            if (visited[idx] || mask[idx] == BlockType.Air) continue;

                            BlockType faceBlock = mask[idx];

                            // Extend in V direction
                            int height = 1;
                            while (v + height < sizeV)
                            {
                                int ni = u * sizeV + (v + height);
                                if (visited[ni] || mask[ni] != faceBlock) break;
                                height++;
                            }

                            // Extend in U direction
                            int width = 1;
                            while (u + width < sizeU)
                            {
                                bool canExtend = true;
                                for (int dv = 0; dv < height; dv++)
                                {
                                    int ni = (u + width) * sizeV + (v + dv);
                                    if (visited[ni] || mask[ni] != faceBlock)
                                    {
                                        canExtend = false;
                                        break;
                                    }
                                }
                                if (!canExtend) break;
                                width++;
                            }

                            // Mark visited
                            for (int du = 0; du < width; du++)
                                for (int dv = 0; dv < height; dv++)
                                    visited[(u + du) * sizeV + (v + dv)] = true;

                            // Emit quad
                            EmitQuad(dir, axisU, axisV, axisD,
                                     u, v, d, width, height, faceBlock, origin);
                        }
                    }
                }
            }

            mesh.Clear();
            mesh.SetVertices(s_vertices);
            mesh.SetTriangles(s_triangles, 0);
            mesh.SetUVs(0, s_uvs);
            mesh.SetColors(s_colors);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
        }

        static void EmitQuad(int dir, int axisU, int axisV, int axisD,
                             int u, int v, int d, int width, int height,
                             BlockType block, int3 chunkOrigin)
        {
            // Calculate the 4 corner positions
            // Positive-direction faces need depth offset by 1
            float depthOffset = (dir % 2 == 0) ? 1f : 0f;

            float3 corner = float3.zero;
            SetAxis(ref corner, axisD, d + depthOffset);
            SetAxis(ref corner, axisU, u);
            SetAxis(ref corner, axisV, v);

            float3 du = float3.zero;
            SetAxis(ref du, axisU, width);

            float3 dv = float3.zero;
            SetAxis(ref dv, axisV, height);

            int vertIdx = s_vertices.Count;

            // Wind vertices so normal faces outward
            if (dir % 2 == 0)
            {
                // Positive direction: CCW from outside
                s_vertices.Add((Vector3)corner);
                s_vertices.Add((Vector3)(corner + dv));
                s_vertices.Add((Vector3)(corner + du + dv));
                s_vertices.Add((Vector3)(corner + du));
            }
            else
            {
                // Negative direction: CW from outside (flipped)
                s_vertices.Add((Vector3)corner);
                s_vertices.Add((Vector3)(corner + du));
                s_vertices.Add((Vector3)(corner + du + dv));
                s_vertices.Add((Vector3)(corner + dv));
            }

            s_triangles.Add(vertIdx);
            s_triangles.Add(vertIdx + 1);
            s_triangles.Add(vertIdx + 2);
            s_triangles.Add(vertIdx);
            s_triangles.Add(vertIdx + 2);
            s_triangles.Add(vertIdx + 3);

            // UVs: tile the texture across the merged quad
            s_uvs.Add(new Vector2(0, 0));
            s_uvs.Add(new Vector2(0, height));
            s_uvs.Add(new Vector2(width, height));
            s_uvs.Add(new Vector2(width, 0));

            // Encode block type + face direction as vertex color for the shader
            // R = atlas column, G = atlas row (from block type × 6 + direction)
            int atlasIndex = GetAtlasIndex(block, dir);
            int atlasCol = atlasIndex % VoxelConstants.AtlasCols;
            int atlasRow = atlasIndex / VoxelConstants.AtlasCols;
            var color = new Color32((byte)atlasCol, (byte)atlasRow, 0, 255);
            s_colors.Add(color);
            s_colors.Add(color);
            s_colors.Add(color);
            s_colors.Add(color);
        }

        /// <summary>
        /// Atlas index: each block type has 6 tiles (one per face).
        /// Special case: Grass top/bottom differs from sides.
        /// </summary>
        static int GetAtlasIndex(BlockType block, int direction)
        {
            // Default: blockType * 6 + direction
            // But grass/wood have per-face variation handled at atlas painting time
            return (int)block * 6 + direction;
        }

        static int GetAxisSize(int axis)
        {
            return axis switch
            {
                0 => VoxelConstants.ChunkSizeX,
                1 => VoxelConstants.ChunkSizeY,
                2 => VoxelConstants.ChunkSizeZ,
                _ => 0
            };
        }

        static int3 MakePos(int axisU, int u, int axisV, int v, int axisD, int d)
        {
            int3 pos = int3.zero;
            SetAxisI(ref pos, axisU, u);
            SetAxisI(ref pos, axisV, v);
            SetAxisI(ref pos, axisD, d);
            return pos;
        }

        static void SetAxis(ref float3 vec, int axis, float value)
        {
            switch (axis)
            {
                case 0: vec.x = value; break;
                case 1: vec.y = value; break;
                case 2: vec.z = value; break;
            }
        }

        static void SetAxisI(ref int3 vec, int axis, int value)
        {
            switch (axis)
            {
                case 0: vec.x = value; break;
                case 1: vec.y = value; break;
                case 2: vec.z = value; break;
            }
        }
    }
}
