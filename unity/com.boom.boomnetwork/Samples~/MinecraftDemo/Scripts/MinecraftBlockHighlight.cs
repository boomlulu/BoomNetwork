// BoomNetwork MinecraftDemo — Block Selection Highlight

using Unity.Mathematics;
using UnityEngine;

namespace BoomNetwork.Samples.MinecraftDemo
{
    /// <summary>
    /// Wireframe highlight cube that follows the targeted block position.
    /// </summary>
    public class MinecraftBlockHighlight : MonoBehaviour
    {
        LineRenderer _lineRenderer;

        // Wireframe cube: 12 edges
        static readonly Vector3[] CubeCorners =
        {
            new Vector3(0, 0, 0), new Vector3(1, 0, 0),
            new Vector3(1, 0, 1), new Vector3(0, 0, 1),
            new Vector3(0, 1, 0), new Vector3(1, 1, 0),
            new Vector3(1, 1, 1), new Vector3(0, 1, 1),
        };

        // Line strip that draws all 12 edges
        static readonly int[] LineIndices =
        {
            0, 1, 1, 2, 2, 3, 3, 0, // bottom
            4, 5, 5, 6, 6, 7, 7, 4, // top
            0, 4, 1, 5, 2, 6, 3, 7, // verticals
        };

        void Awake()
        {
            _lineRenderer = gameObject.AddComponent<LineRenderer>();
            _lineRenderer.useWorldSpace = false;
            _lineRenderer.startWidth = 0.02f;
            _lineRenderer.endWidth = 0.02f;
            _lineRenderer.startColor = Color.white;
            _lineRenderer.endColor = Color.white;
            _lineRenderer.material = new Material(Shader.Find("Sprites/Default"));
            _lineRenderer.positionCount = LineIndices.Length;

            for (int i = 0; i < LineIndices.Length; i++)
                _lineRenderer.SetPosition(i, CubeCorners[LineIndices[i]]);

            // Slight scale expansion to avoid z-fighting
            transform.localScale = Vector3.one * 1.005f;
            gameObject.SetActive(false);
        }

        public void Show(int3 blockPos)
        {
            transform.position = new Vector3(blockPos.x - 0.0025f, blockPos.y - 0.0025f, blockPos.z - 0.0025f);
            gameObject.SetActive(true);
        }

        public void Hide()
        {
            gameObject.SetActive(false);
        }
    }
}
