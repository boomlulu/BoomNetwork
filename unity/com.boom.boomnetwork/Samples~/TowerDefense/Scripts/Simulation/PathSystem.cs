// BoomNetwork TowerDefense Demo — Flow Field Pathfinding
//
// BFS from base center outward. Each passable cell stores the direction to move
// to reach the base in minimum steps. O(400) rebuild on every tower place/sell.
// Enemies query the table each frame: O(1) per enemy.
//
// Flow field directions are stored as (FInt dx, FInt dz) unit vectors.
// Cells occupied by towers are impassable. Base cells are the goal.

namespace BoomNetwork.Samples.TowerDefense
{
    public static class PathSystem
    {
        // Flow direction for each cell: the normalized direction toward base.
        // Static array — rebuilt synchronously in ApplyInputs (frame-driven).
        // NOTE: accessed by EnemySystem each tick.
        public static readonly FInt[] FlowDirX = new FInt[GameState.GridSize];
        public static readonly FInt[] FlowDirZ = new FInt[GameState.GridSize];
        public static readonly bool[] Reachable = new bool[GameState.GridSize];

        // BFS queue (reused, no allocation)
        static readonly int[] _queue = new int[GameState.GridSize];
        static readonly int[] _dist  = new int[GameState.GridSize];

        // Cardinal + diagonal neighbor offsets [dx, dy]
        static readonly int[] _nx = { 0,  0, 1, -1,  1,  1, -1, -1 };
        static readonly int[] _nz = { 1, -1, 0,  0,  1, -1,  1, -1 };

        const int INF = 999999;

        /// <summary>
        /// Rebuild flow field. Called from ApplyInputs whenever a tower is placed or sold.
        /// Reads Grid[].Type for blocked cells. All cells blocked by towers are impassable.
        /// </summary>
        public static void Rebuild(GameState state)
        {
            // Reset
            for (int i = 0; i < GameState.GridSize; i++)
            {
                _dist[i]       = INF;
                Reachable[i]   = false;
                FlowDirX[i]    = FInt.Zero;
                FlowDirZ[i]    = FInt.Zero;
            }

            // Seed: all base cells are distance 0 (goals)
            int head = 0, tail = 0;
            for (int y = GameState.BaseCY; y < GameState.BaseCY + 2; y++)
            {
                for (int x = GameState.BaseCX; x < GameState.BaseCX + 2; x++)
                {
                    int idx = GameState.CellIndex(x, y);
                    _dist[idx]     = 0;
                    Reachable[idx] = true;
                    FlowDirX[idx]  = FInt.Zero;
                    FlowDirZ[idx]  = FInt.Zero;
                    _queue[tail++] = idx;
                }
            }

            // BFS
            while (head < tail)
            {
                int cur = _queue[head++];
                int cx = cur % GameState.GridW;
                int cy = cur / GameState.GridW;
                int curDist = _dist[cur];

                for (int d = 0; d < 8; d++)
                {
                    int nx = cx + _nx[d];
                    int nz = cy + _nz[d];
                    if (!GameState.IsInBounds(nx, nz)) continue;

                    int nIdx = GameState.CellIndex(nx, nz);
                    if (_dist[nIdx] != INF) continue; // already visited

                    // Cannot pass through tower-occupied cells
                    if (state.Grid[nIdx].Type != TowerType.None) continue;

                    int stepCost = (d < 4) ? 10 : 14; // cardinal = 10, diagonal = 14
                    _dist[nIdx] = curDist + stepCost;
                    Reachable[nIdx] = true;

                    // Flow direction: from this neighbor back toward cur (toward base)
                    int ddx = cx - nx;
                    int ddz = cy - nz;
                    // Normalize (diagonal: 1/sqrt(2) ≈ 724/1024)
                    if (ddx != 0 && ddz != 0)
                    {
                        FlowDirX[nIdx] = new FInt(ddx * 724); // 724 ≈ 1024/sqrt(2)
                        FlowDirZ[nIdx] = new FInt(ddz * 724);
                    }
                    else
                    {
                        FlowDirX[nIdx] = FInt.FromInt(ddx);
                        FlowDirZ[nIdx] = FInt.FromInt(ddz);
                    }

                    _queue[tail++] = nIdx;
                }
            }
        }

        /// <summary>
        /// Get flow direction for world position. Returns normalized FInt direction.
        /// </summary>
        public static void GetFlow(FInt posX, FInt posZ, out FInt dirX, out FInt dirZ)
        {
            int cx = posX.ToInt();
            int cz = posZ.ToInt();
            cx = cx < 0 ? 0 : (cx >= GameState.GridW ? GameState.GridW - 1 : cx);
            cz = cz < 0 ? 0 : (cz >= GameState.GridH ? GameState.GridH - 1 : cz);
            int idx = GameState.CellIndex(cx, cz);
            dirX = FlowDirX[idx];
            dirZ = FlowDirZ[idx];
        }
    }
}
