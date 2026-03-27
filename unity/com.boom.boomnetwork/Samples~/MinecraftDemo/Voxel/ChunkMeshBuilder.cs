// BoomNetwork MinecraftDemo — Greedy Meshing Chunk Mesh Builder
// Algorithm based on 0fps.net greedy meshing + Minecraft4Unity (MIT, paternostrox)
// Zero GC allocation during BuildMesh — all buffers are static and reused.

using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace BoomNetwork.Samples.MinecraftDemo
{
    public static class ChunkMeshBuilder
    {
        // Reusable mesh output buffers
        static readonly List<Vector3> s_vertices = new List<Vector3>(4096);
        static readonly List<int> s_triangles = new List<int>(6144);
        static readonly List<Vector2> s_uvs = new List<Vector2>(4096);
        static readonly List<Color32> s_colors = new List<Color32>(4096);

        // Reusable per-slice buffers (max slice size = 16×16 = 256)
        const int MaxSliceSize = VoxelConstants.ChunkSizeX * VoxelConstants.ChunkSizeZ; // 256
        static readonly BlockType[] s_mask = new BlockType[MaxSliceSize];
        static readonly bool[] s_visited = new bool[MaxSliceSize];

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

            for (int dir = 0; dir < 6; dir++)
            {
                int3 axes = VoxelConstants.DirectionAlignedAxes[dir];
                int axisU = axes.x;
                int axisV = axes.y;
                int axisD = axes.z;

                int3 dirOffset = VoxelConstants.DirectionOffsets[dir];
                int sizeU = GetAxisSize(axisU);
                int sizeV = GetAxisSize(axisV);
                int sizeD = GetAxisSize(axisD);
                int sliceSize = sizeU * sizeV;

                for (int d = 0; d < sizeD; d++)
                {
                    // Clear reusable buffers (only the portion we use)
                    System.Array.Clear(s_mask, 0, sliceSize);
                    bool anyFace = false;

                    for (int u = 0; u < sizeU; u++)
                    {
                        for (int v = 0; v < sizeV; v++)
                        {
                            int3 pos = MakePos(axisU, u, axisV, v, axisD, d);
                            BlockType block = chunk.Blocks[VoxelConstants.To1D(pos)];

                            if (block.IsTransparent()) continue;

                            int3 neighborLocal = pos + dirOffset;
                            BlockType neighbor;

                            if (neighborLocal.x < 0 || neighborLocal.x >= sx ||
                                neighborLocal.y < 0 || neighborLocal.y >= sy ||
                                neighborLocal.z < 0 || neighborLocal.z >= sz)
                            {
                                neighbor = getNeighborBlock(origin + neighborLocal);
                            }
                            else
                            {
                                neighbor = chunk.Blocks[VoxelConstants.To1D(neighborLocal)];
                            }

                            if (neighbor.IsTransparent())
                            {
                                s_mask[u * sizeV + v] = block;
                                anyFace = true;
                            }
                        }
                    }

                    if (!anyFace) continue;

                    // Clear visited buffer
                    System.Array.Clear(s_visited, 0, sliceSize);

                    for (int u = 0; u < sizeU; u++)
                    {
                        for (int v = 0; v < sizeV; v++)
                        {
                            int idx = u * sizeV + v;
                            if (s_visited[idx] || s_mask[idx] == BlockType.Air) continue;

                            BlockType faceBlock = s_mask[idx];

                            // Greedy extend V
                            int height = 1;
                            while (v + height < sizeV)
                            {
                                int ni = u * sizeV + (v + height);
                                if (s_visited[ni] || s_mask[ni] != faceBlock) break;
                                height++;
                            }

                            // Greedy extend U
                            int width = 1;
                            while (u + width < sizeU)
                            {
                                bool canExtend = true;
                                for (int dv = 0; dv < height; dv++)
                                {
                                    int ni = (u + width) * sizeV + (v + dv);
                                    if (s_visited[ni] || s_mask[ni] != faceBlock)
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
                                    s_visited[(u + du) * sizeV + (v + dv)] = true;

                            EmitQuad(dir, axisU, axisV, axisD, u, v, d, width, height, faceBlock);
                        }
                    }
                }
            }

            mesh.Clear();
            // List overloads are fine — Unity copies to native internally.
            // The key optimization is that these Lists are static and reused (no managed alloc).
            mesh.SetVertices(s_vertices);
            mesh.SetTriangles(s_triangles, 0);
            mesh.SetUVs(0, s_uvs);
            mesh.SetColors(s_colors);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
        }

        static void EmitQuad(int dir, int axisU, int axisV, int axisD,
                             int u, int v, int d, int width, int height, BlockType block)
        {
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

            if (dir % 2 == 0)
            {
                s_vertices.Add((Vector3)corner);
                s_vertices.Add((Vector3)(corner + dv));
                s_vertices.Add((Vector3)(corner + du + dv));
                s_vertices.Add((Vector3)(corner + du));
            }
            else
            {
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

            s_uvs.Add(new Vector2(0, 0));
            s_uvs.Add(new Vector2(0, height));
            s_uvs.Add(new Vector2(width, height));
            s_uvs.Add(new Vector2(width, 0));

            var color = GetBlockFaceColor(block, dir);
            s_colors.Add(color);
            s_colors.Add(color);
            s_colors.Add(color);
            s_colors.Add(color);
        }

        static readonly float[] FaceBrightness = { 0.85f, 0.75f, 1.0f, 0.6f, 0.9f, 0.8f };

        static Color GetBlockFaceColor(BlockType block, int dir)
        {
            float b = FaceBrightness[dir];
            return block switch
            {
                BlockType.Stone => new Color(0.50f * b, 0.50f * b, 0.50f * b),
                BlockType.Dirt  => new Color(0.55f * b, 0.35f * b, 0.20f * b),
                BlockType.Grass => dir == 2 ? new Color(0.30f * b, 0.65f * b, 0.18f * b)
                                : dir == 3 ? new Color(0.55f * b, 0.35f * b, 0.20f * b)
                                :            new Color(0.42f * b, 0.38f * b, 0.20f * b),
                BlockType.Wood  => (dir == 2 || dir == 3) ? new Color(0.55f * b, 0.40f * b, 0.20f * b)
                                :                           new Color(0.60f * b, 0.45f * b, 0.25f * b),
                BlockType.Leaf  => new Color(0.20f * b, 0.55f * b, 0.15f * b),
                BlockType.Sand  => new Color(0.85f * b, 0.78f * b, 0.55f * b),
                BlockType.Water => new Color(0.20f * b, 0.40f * b, 0.80f * b),
                _               => new Color(1f * b, 0f, 1f * b),
            };
        }

        static int GetAxisSize(int axis) => axis switch
        {
            0 => VoxelConstants.ChunkSizeX,
            1 => VoxelConstants.ChunkSizeY,
            2 => VoxelConstants.ChunkSizeZ,
            _ => 0
        };

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
