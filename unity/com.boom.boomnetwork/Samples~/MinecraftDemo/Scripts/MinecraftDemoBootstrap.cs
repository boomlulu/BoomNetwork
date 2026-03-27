// BoomNetwork MinecraftDemo — Scene Bootstrap
// Attach this to an empty GameObject to set up the entire demo automatically.
// It creates VoxelWorld, sets up materials, and connects everything.

using UnityEngine;
using BoomNetwork.Unity;

namespace BoomNetwork.Samples.MinecraftDemo
{
    /// <summary>
    /// One-click demo setup: creates VoxelWorld, material, lighting, and wires networking.
    /// Attach to a GameObject that also has BoomNetworkManager.
    /// </summary>
    [RequireComponent(typeof(BoomNetworkManager))]
    [RequireComponent(typeof(MinecraftNetworkManager))]
    public class MinecraftDemoBootstrap : MonoBehaviour
    {
        [Header("World Settings")]
        [SerializeField] int worldSeed = 42;

        [Header("Visual Settings")]
        [SerializeField] Color skyColor = new Color(0.53f, 0.81f, 0.98f);
        [SerializeField] Color fogColor = new Color(0.7f, 0.85f, 0.95f);
        [SerializeField] float fogDensity = 0.008f;

        void Awake()
        {
            SetupLighting();
            SetupWorld();
            SetupBlockHighlight();
        }

        void SetupLighting()
        {
            // Sky & fog
            Camera.main?.gameObject.SetActive(false); // disable default camera (player has own)
            RenderSettings.fogMode = FogMode.Exponential;
            RenderSettings.fogDensity = fogDensity;
            RenderSettings.fogColor = fogColor;
            RenderSettings.fog = true;

            // Skybox color (simple gradient)
            if (Camera.main == null)
            {
                // Camera will be created by player controller
            }

            // Directional light
            var existingLight = FindFirstObjectByType<Light>();
            if (existingLight == null)
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
            // Create VoxelWorld
            var worldGo = new GameObject("VoxelWorld");
            var world = worldGo.AddComponent<VoxelWorld>();

            // Generate procedural material
            var material = ProceduralBlockAtlas.CreateMaterial();

            // Use reflection to set serialized fields (since we're creating at runtime)
            // Alternative: make VoxelWorld fields public or add Init method
            world.Seed = worldSeed;
            SetPrivateField(world, "chunkMaterial", material);

            // Wire to network manager
            var netManager = GetComponent<MinecraftNetworkManager>();
            SetPrivateField(netManager, "voxelWorld", world);
            SetPrivateField(netManager, "worldSeed", worldSeed);
        }

        void SetupBlockHighlight()
        {
            var highlightGo = new GameObject("BlockHighlight");
            var highlight = highlightGo.AddComponent<MinecraftBlockHighlight>();

            // Wire highlight to update each frame based on controller target
            gameObject.AddComponent<BlockHighlightUpdater>().Init(highlight);
        }

        static void SetPrivateField(object target, string fieldName, object value)
        {
            var field = target.GetType().GetField(fieldName,
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
            if (field != null)
                field.SetValue(target, value);
            else
                Debug.LogWarning($"Field '{fieldName}' not found on {target.GetType().Name}");
        }
    }

    /// <summary>Updates block highlight position each frame based on player controller target.</summary>
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
