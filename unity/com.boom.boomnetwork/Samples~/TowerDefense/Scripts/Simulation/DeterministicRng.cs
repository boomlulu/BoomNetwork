// BoomNetwork TowerDefense Demo — Deterministic RNG (Fixed-Point)
// Copied from VampireSurvivors Demo with namespace change.

namespace BoomNetwork.Samples.TowerDefense
{
    public static class DeterministicRng
    {
        public static uint Next(ref uint state)
        {
            state = state * 1664525u + 1013904223u;
            return state;
        }

        /// <summary>Return FInt in [0, 1).</summary>
        public static FInt NextFInt(ref uint state)
        {
            uint v = Next(ref state) >> 8;
            return new FInt((int)(v >> 14));
        }

        /// <summary>Return FInt in [min, max).</summary>
        public static FInt Range(ref uint state, FInt min, FInt max)
        {
            FInt t = NextFInt(ref state);
            return min + t * (max - min);
        }

        /// <summary>Return int in [min, max) (exclusive max).</summary>
        public static int RangeInt(ref uint state, int min, int max)
        {
            if (min >= max) return min;
            return min + (int)(Next(ref state) % (uint)(max - min));
        }
    }
}
