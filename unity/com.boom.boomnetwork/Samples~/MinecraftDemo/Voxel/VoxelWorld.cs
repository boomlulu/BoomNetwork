// BoomNetwork MinecraftDemo — Voxel World Manager

using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace BoomNetwork.Samples.MinecraftDemo
{
    /// <summary>
    /// Manages all chunk data and provides world-level block access.
    /// Handles chunk loading/unloading around the player.
    /// </summary>
    public class VoxelWorld : MonoBehaviour
    {
        [Header("World Settings")]
        [SerializeField] int seed = 42;
        [SerializeField] Material chunkMaterial;

        [Header("Chunk Loading")]
        [SerializeField] int loadRadiusH = VoxelConstants.LoadRadiusH;
        [SerializeField] int loadRadiusV = VoxelConstants.LoadRadiusV;
        [SerializeField] int maxChunkBuildsPerFrame = 2;

        readonly Dictionary<int3, ChunkData> _chunks = new Dictionary<int3, ChunkData>();
        readonly Dictionary<int3, ChunkRenderer> _renderers = new Dictionary<int3, ChunkRenderer>();
        readonly Queue<int3> _meshBuildQueue = new Queue<int3>();

        int3 _lastPlayerChunk = new int3(int.MaxValue);

        public int Seed
        {
            get => seed;
            set => seed = value;
        }

        public IReadOnlyDictionary<int3, ChunkData> Chunks => _chunks;

        /// <summary>Get block at world position. Returns Air for unloaded chunks.</summary>
        public BlockType GetBlock(int3 worldPos)
        {
            int3 chunkPos = VoxelConstants.WorldToChunk(worldPos);
            if (!_chunks.TryGetValue(chunkPos, out var chunk))
                return BlockType.Air;
            int3 local = VoxelConstants.WorldToLocal(worldPos);
            return chunk.GetBlock(local);
        }

        /// <summary>Set block at world position. Marks chunk dirty for re-meshing.</summary>
        public bool SetBlock(int3 worldPos, BlockType type)
        {
            int3 chunkPos = VoxelConstants.WorldToChunk(worldPos);
            if (!_chunks.TryGetValue(chunkPos, out var chunk))
                return false;

            int3 local = VoxelConstants.WorldToLocal(worldPos);
            chunk.SetBlock(local, type);
            EnqueueMeshBuild(chunkPos);

            // If block is at chunk boundary, also rebuild neighbor chunk
            MarkNeighborDirtyIfBoundary(local, chunkPos);
            return true;
        }

        /// <summary>Update chunk loading around a world position.</summary>
        public void UpdatePlayerPosition(Vector3 playerWorldPos)
        {
            int3 playerChunk = VoxelConstants.WorldToChunk(new int3(
                (int)math.floor(playerWorldPos.x),
                (int)math.floor(playerWorldPos.y),
                (int)math.floor(playerWorldPos.z)));

            if (math.all(playerChunk == _lastPlayerChunk)) return;
            _lastPlayerChunk = playerChunk;

            // Load new chunks in range
            var chunksToKeep = new HashSet<int3>();
            for (int x = -loadRadiusH; x <= loadRadiusH; x++)
            for (int y = -loadRadiusV; y <= loadRadiusV; y++)
            for (int z = -loadRadiusH; z <= loadRadiusH; z++)
            {
                int3 cp = playerChunk + new int3(x, y, z);
                chunksToKeep.Add(cp);

                if (!_chunks.ContainsKey(cp))
                    LoadChunk(cp);
            }

            // Unload chunks out of range
            var toRemove = new List<int3>();
            foreach (var cp in _chunks.Keys)
            {
                if (!chunksToKeep.Contains(cp))
                    toRemove.Add(cp);
            }
            foreach (var cp in toRemove)
                UnloadChunk(cp);
        }

        /// <summary>Generate the full fixed-size world (for multiplayer deterministic init).</summary>
        public void GenerateFullWorld()
        {
            for (int cx = 0; cx < VoxelConstants.WorldChunksX; cx++)
            for (int cy = 0; cy < VoxelConstants.WorldChunksY; cy++)
            for (int cz = 0; cz < VoxelConstants.WorldChunksZ; cz++)
            {
                LoadChunk(new int3(cx, cy, cz));
            }
        }

        /// <summary>Rebuild meshes for all dirty chunks (up to limit per frame).</summary>
        public void ProcessMeshQueue()
        {
            int built = 0;
            while (_meshBuildQueue.Count > 0 && built < maxChunkBuildsPerFrame)
            {
                int3 cp = _meshBuildQueue.Dequeue();
                if (_chunks.TryGetValue(cp, out var chunk) && chunk.IsDirty)
                {
                    BuildChunkMesh(cp, chunk);
                    chunk.IsDirty = false;
                    built++;
                }
            }
        }

        void LoadChunk(int3 chunkPos)
        {
            if (_chunks.ContainsKey(chunkPos)) return;

            var chunk = new ChunkData(chunkPos);
            WorldGenerator.GenerateChunk(chunk, seed);
            _chunks[chunkPos] = chunk;
            EnqueueMeshBuild(chunkPos);
        }

        void UnloadChunk(int3 chunkPos)
        {
            _chunks.Remove(chunkPos);
            if (_renderers.TryGetValue(chunkPos, out var renderer))
            {
                _renderers.Remove(chunkPos);
                if (renderer != null)
                    Destroy(renderer.gameObject);
            }
        }

        void EnqueueMeshBuild(int3 chunkPos)
        {
            if (!_meshBuildQueue.Contains(chunkPos))
                _meshBuildQueue.Enqueue(chunkPos);
        }

        void BuildChunkMesh(int3 chunkPos, ChunkData chunk)
        {
            if (!_renderers.TryGetValue(chunkPos, out var renderer))
            {
                renderer = ChunkRenderer.Create(chunkPos, chunkMaterial, transform);
                _renderers[chunkPos] = renderer;
            }

            ChunkMeshBuilder.BuildMesh(chunk, GetBlock, renderer.Mesh);

            // Update collider
            renderer.UpdateCollider();
        }

        void MarkNeighborDirtyIfBoundary(int3 local, int3 chunkPos)
        {
            if (local.x == 0) MarkDirty(chunkPos + new int3(-1, 0, 0));
            if (local.x == VoxelConstants.ChunkSizeX - 1) MarkDirty(chunkPos + new int3(1, 0, 0));
            if (local.y == 0) MarkDirty(chunkPos + new int3(0, -1, 0));
            if (local.y == VoxelConstants.ChunkSizeY - 1) MarkDirty(chunkPos + new int3(0, 1, 0));
            if (local.z == 0) MarkDirty(chunkPos + new int3(0, 0, -1));
            if (local.z == VoxelConstants.ChunkSizeZ - 1) MarkDirty(chunkPos + new int3(0, 0, 1));
        }

        void MarkDirty(int3 chunkPos)
        {
            if (_chunks.TryGetValue(chunkPos, out var chunk))
            {
                chunk.IsDirty = true;
                EnqueueMeshBuild(chunkPos);
            }
        }

        /// <summary>Clear entire world (for snapshot load)</summary>
        public void ClearWorld()
        {
            var allChunks = new List<int3>(_chunks.Keys);
            foreach (var cp in allChunks)
                UnloadChunk(cp);
            _meshBuildQueue.Clear();
            _lastPlayerChunk = new int3(int.MaxValue);
        }
    }
}
