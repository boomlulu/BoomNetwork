// BoomNetwork MinecraftDemo — Scene Bootstrap
// Attach this to an empty GameObject to set up the entire demo automatically.
// It creates VoxelWorld, sets up materials, and connects everything.

using UnityEngine;
using BoomNetwork.Unity;

namespace BoomNetwork.Samples.MinecraftDemo
{
    [RequireComponent(typeof(BoomNetworkManager))]
    [RequireComponent(typeof(MinecraftNetworkManager))]
    public class MinecraftDemoBootstrap : MonoBehaviour
    {
        [Header("World Settings")]
        [SerializeField] int worldSeed = 42;

        [Header("Visual Settings")]
        [SerializeField] Color fogColor = new Color(0.7f, 0.85f, 0.95f);
        [SerializeField] float fogDensity = 0.008f;

        void Awake()
        {
            Application.targetFrameRate = 60;
            SetupMatchKey();
            SetupLighting();
            SetupWorld();
            SetupBlockHighlight();
        }

        void SetupMatchKey()
        {
            var network = GetComponent<BoomNetworkManager>();
            network.MatchKey = "minecraft";
        }

        void SetupLighting()
        {
            Camera.main?.gameObject.SetActive(false);
            RenderSettings.fogMode = FogMode.Exponential;
            RenderSettings.fogDensity = fogDensity;
            RenderSettings.fogColor = fogColor;
            RenderSettings.fog = true;

            if (FindFirstObjectByType<Light>() == null)
            {
                var lightGo = new GameObject("DirectionalLight");
                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.color = new Color(1f, 0.96f, 0.88f);
                light.intensity = 1.1f;
                lightGo.transform.eulerAngles = new Vector3(50f, -30f, 0f);
            }
        }

        void SetupWorld()
        {
            var worldGo = new GameObject("VoxelWorld");
            var world = worldGo.AddComponent<VoxelWorld>();
            world.Seed = worldSeed;
            world.ChunkMaterial = ProceduralBlockAtlas.CreateMaterial();

            var netManager = GetComponent<MinecraftNetworkManager>();
            netManager.VoxelWorld = world;
            netManager.WorldSeed = worldSeed;
        }

        void SetupBlockHighlight()
        {
            var highlightGo = new GameObject("BlockHighlight");
            var highlight = highlightGo.AddComponent<MinecraftBlockHighlight>();
            gameObject.AddComponent<BlockHighlightUpdater>().Init(highlight);
        }
    }

    public class BlockHighlightUpdater : MonoBehaviour
    {
        MinecraftBlockHighlight _highlight;
        MinecraftPlayerController _controller;

        public void Init(MinecraftBlockHighlight highlight) => _highlight = highlight;

        void LateUpdate()
        {
            if (_controller == null)
                _controller = FindFirstObjectByType<MinecraftPlayerController>();
            if (_controller == null || _highlight == null) return;

            if (_controller.HasTarget)
                _highlight.Show(_controller.TargetBlockPos);
            else
                _highlight.Hide();
        }
    }
}
