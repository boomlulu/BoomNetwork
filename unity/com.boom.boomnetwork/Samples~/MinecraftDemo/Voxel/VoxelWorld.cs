// BoomNetwork MinecraftDemo — Voxel World Manager
// Zero per-frame GC allocation — all collections are reused.

using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace BoomNetwork.Samples.MinecraftDemo
{
    public class VoxelWorld : MonoBehaviour
    {
        [Header("World Settings")]
        [SerializeField] int seed = 42;
        [SerializeField] Material chunkMaterial;

        public Material ChunkMaterial { set => chunkMaterial = value; }

        [Header("Chunk Loading")]
        [SerializeField] int loadRadiusH = VoxelConstants.LoadRadiusH;
        [SerializeField] int loadRadiusV = VoxelConstants.LoadRadiusV;
        [SerializeField] int maxChunkBuildsPerFrame = 2;

        readonly Dictionary<int3, ChunkData> _chunks = new();
        readonly Dictionary<int3, ChunkRenderer> _renderers = new();
        readonly Queue<int3> _meshBuildQueue = new();
        readonly HashSet<int3> _meshBuildSet = new();  // O(1) contains check

        // Reusable collections for UpdatePlayerPosition (avoid per-call allocation)
        readonly HashSet<int3> _chunksToKeep = new();
        readonly List<int3> _toRemove = new();

        // Cached delegate to avoid per-BuildMesh allocation
        System.Func<int3, BlockType> _getBlockCached;

        int3 _lastPlayerChunk = new int3(int.MaxValue);

        public int Seed
        {
            get => seed;
            set => seed = value;
        }

        public IReadOnlyDictionary<int3, ChunkData> Chunks => _chunks;

        public BlockType GetBlock(int3 worldPos)
        {
            int3 chunkPos = VoxelConstants.WorldToChunk(worldPos);
            if (!_chunks.TryGetValue(chunkPos, out var chunk))
                return BlockType.Air;
            int3 local = VoxelConstants.WorldToLocal(worldPos);
            return chunk.GetBlock(local);
        }

        public bool SetBlock(int3 worldPos, BlockType type)
        {
            int3 chunkPos = VoxelConstants.WorldToChunk(worldPos);
            if (!_chunks.TryGetValue(chunkPos, out var chunk))
                return false;

            int3 local = VoxelConstants.WorldToLocal(worldPos);
            chunk.SetBlock(local, type);
            EnqueueMeshBuild(chunkPos);
            MarkNeighborDirtyIfBoundary(local, chunkPos);
            return true;
        }

        public void UpdatePlayerPosition(Vector3 playerWorldPos)
        {
            int3 playerChunk = VoxelConstants.WorldToChunk(new int3(
                (int)math.floor(playerWorldPos.x),
                (int)math.floor(playerWorldPos.y),
                (int)math.floor(playerWorldPos.z)));

            if (math.all(playerChunk == _lastPlayerChunk)) return;
            _lastPlayerChunk = playerChunk;

            _chunksToKeep.Clear();
            for (int x = -loadRadiusH; x <= loadRadiusH; x++)
            for (int y = -loadRadiusV; y <= loadRadiusV; y++)
            for (int z = -loadRadiusH; z <= loadRadiusH; z++)
            {
                int3 cp = playerChunk + new int3(x, y, z);
                _chunksToKeep.Add(cp);

                if (!_chunks.ContainsKey(cp))
                    LoadChunk(cp);
            }

            _toRemove.Clear();
            foreach (var cp in _chunks.Keys)
            {
                if (!_chunksToKeep.Contains(cp))
                    _toRemove.Add(cp);
            }
            for (int i = 0; i < _toRemove.Count; i++)
                UnloadChunk(_toRemove[i]);
        }

        /// <summary>
        /// Generate and build ALL chunks synchronously.
        /// GC spike happens once at load time, then zero GC during gameplay.
        /// </summary>
        public void GenerateFullWorld()
        {
            for (int cx = 0; cx < VoxelConstants.WorldChunksX; cx++)
            for (int cy = 0; cy < VoxelConstants.WorldChunksY; cy++)
            for (int cz = 0; cz < VoxelConstants.WorldChunksZ; cz++)
                LoadChunk(new int3(cx, cy, cz));

            // Build all meshes NOW instead of spreading across frames
            while (_meshBuildQueue.Count > 0)
            {
                int3 cp = _meshBuildQueue.Dequeue();
                _meshBuildSet.Remove(cp);
                if (_chunks.TryGetValue(cp, out var chunk) && chunk.IsDirty)
                {
                    BuildChunkMesh(cp, chunk);
                    chunk.IsDirty = false;
                }
            }
        }

        public void ProcessMeshQueue()
        {
            int built = 0;
            while (_meshBuildQueue.Count > 0 && built < maxChunkBuildsPerFrame)
            {
                int3 cp = _meshBuildQueue.Dequeue();
                _meshBuildSet.Remove(cp);
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
            if (_meshBuildSet.Add(chunkPos))
                _meshBuildQueue.Enqueue(chunkPos);
        }

        void BuildChunkMesh(int3 chunkPos, ChunkData chunk)
        {
            if (!_renderers.TryGetValue(chunkPos, out var renderer))
            {
                renderer = ChunkRenderer.Create(chunkPos, chunkMaterial, transform);
                _renderers[chunkPos] = renderer;
            }

            _getBlockCached ??= GetBlock;
            ChunkMeshBuilder.BuildMesh(chunk, _getBlockCached, renderer.Mesh);
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

        public void ClearWorld()
        {
            // Avoid allocating new list — iterate keys into reusable buffer
            _toRemove.Clear();
            foreach (var cp in _chunks.Keys)
                _toRemove.Add(cp);
            for (int i = 0; i < _toRemove.Count; i++)
                UnloadChunk(_toRemove[i]);
            _meshBuildQueue.Clear();
            _meshBuildSet.Clear();
            _lastPlayerChunk = new int3(int.MaxValue);
        }
    }
}
