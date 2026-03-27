// BoomNetwork MinecraftDemo — Chunk Renderer (MonoBehaviour wrapper)

using Unity.Mathematics;
using UnityEngine;

namespace BoomNetwork.Samples.MinecraftDemo
{
    /// <summary>
    /// MonoBehaviour that holds MeshFilter + MeshRenderer + MeshCollider for a chunk.
    /// Created/destroyed by VoxelWorld.
    /// </summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider))]
    public class ChunkRenderer : MonoBehaviour
    {
        MeshFilter _filter;
        MeshRenderer _renderer;
        MeshCollider _collider;
        Mesh _mesh;

        public Mesh Mesh => _mesh;

        public static ChunkRenderer Create(int3 chunkPos, Material material, Transform parent)
        {
            var go = new GameObject($"Chunk_{chunkPos.x}_{chunkPos.y}_{chunkPos.z}");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(
                chunkPos.x * VoxelConstants.ChunkSizeX,
                chunkPos.y * VoxelConstants.ChunkSizeY,
                chunkPos.z * VoxelConstants.ChunkSizeZ);
            go.isStatic = true;

            var cr = go.AddComponent<ChunkRenderer>();
            cr._filter = go.GetComponent<MeshFilter>();
            cr._renderer = go.GetComponent<MeshRenderer>();
            cr._collider = go.GetComponent<MeshCollider>();

            cr._mesh = new Mesh { name = $"ChunkMesh_{chunkPos}" };
            cr._filter.sharedMesh = cr._mesh;
            cr._renderer.sharedMaterial = material;

            return cr;
        }

        public void UpdateCollider()
        {
            if (_mesh.vertexCount > 0)
                _collider.sharedMesh = _mesh;
            else
                _collider.sharedMesh = null;
        }

        void OnDestroy()
        {
            if (_mesh != null)
                Destroy(_mesh);
        }
    }
}
