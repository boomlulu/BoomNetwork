// BoomNetwork TowerDefense Demo — Enemy AI (Fixed-Point)
//
// Enemies follow the PathSystem flow field toward the base.
// Magic tower slow (SlowFrames) halves movement speed.
// On reaching a base cell, enemies deal damage and die.

namespace BoomNetwork.Samples.TowerDefense
{
    public static class EnemySystem
    {
        // Base center world position for arrival check
        static readonly FInt BaseCenterX = GameState.CellCenterX(GameState.BaseCX);
        static readonly FInt BaseCenterZ = GameState.CellCenterZ(GameState.BaseCY);
        // Arrival threshold: if within this distance of base center, deal damage
        static readonly FInt ArrivalDistSqr = FInt.FromFloat(2.5f * 2.5f); // 6.25

        public static void Tick(GameState state)
        {
            for (int i = 0; i < GameState.MaxEnemies; i++)
            {
                ref var e = ref state.Enemies[i];
                if (!e.IsAlive) continue;

                // Check arrival at base (any base cell)
                if (IsAtBase(e.PosX, e.PosZ))
                {
                    state.BaseHp -= GameState.GetEnemyDamage(e.Type);
                    if (state.BaseHp < 0) state.BaseHp = 0;
                    e.IsAlive = false;
                    continue;
                }

                // Tick slow
                if (e.SlowFrames > 0) e.SlowFrames--;

                // Move along flow field
                PathSystem.GetFlow(e.PosX, e.PosZ, out FInt dirX, out FInt dirZ);

                if (dirX == FInt.Zero && dirZ == FInt.Zero)
                {
                    // No path — stuck. Just stand still.
                    continue;
                }

                FInt speed = GameState.GetEnemySpeed(e.Type);
                // Elite is immune to slow
                if (e.SlowFrames > 0 && e.Type != EnemyType.Elite) speed = speed / 2;

                e.PosX = e.PosX + dirX * speed;
                e.PosZ = e.PosZ + dirZ * speed;

                // Clamp to map bounds
                e.PosX = FInt.Clamp(e.PosX, FInt.Zero, FInt.FromInt(GameState.GridW));
                e.PosZ = FInt.Clamp(e.PosZ, FInt.Zero, FInt.FromInt(GameState.GridH));
            }
        }

        static bool IsAtBase(FInt posX, FInt posZ)
        {
            // Check if inside the 2×2 base region (with a small margin)
            FInt bxMin = FInt.FromInt(GameState.BaseCX);
            FInt bxMax = FInt.FromInt(GameState.BaseCX + 2);
            FInt bzMin = FInt.FromInt(GameState.BaseCY);
            FInt bzMax = FInt.FromInt(GameState.BaseCY + 2);
            return posX >= bxMin && posX < bxMax && posZ >= bzMin && posZ < bzMax;
        }
    }
}
