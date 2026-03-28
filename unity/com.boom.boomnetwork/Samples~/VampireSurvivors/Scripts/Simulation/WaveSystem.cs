// BoomNetwork VampireSurvivors Demo — Wave Spawner (Fixed-Point)

namespace BoomNetwork.Samples.VampireSurvivors
{
    public static class WaveSystem
    {
        const uint WaveGapFrames = 100;
        const uint SpawnIntervalFrames = 5;
        const int BaseEnemyCount = 15;
        const int EnemiesPerWave = 10;

        public static void Tick(GameState state)
        {
            if (!state.HasAlivePlayers()) return;

            if (state.WaveSpawnRemaining == 0)
            {
                if (state.WaveSpawnTimer > 0) { state.WaveSpawnTimer--; return; }
                state.WaveNumber++;
                state.WaveSpawnRemaining = (uint)(BaseEnemyCount + state.WaveNumber * EnemiesPerWave);
                state.WaveSpawnTimer = SpawnIntervalFrames;
                return;
            }
            if (state.WaveSpawnTimer > 0) { state.WaveSpawnTimer--; return; }

            int slot = state.AllocEnemy();
            if (slot < 0) { state.WaveSpawnTimer = SpawnIntervalFrames; return; }

            SpawnEnemy(state, slot);
            state.WaveSpawnRemaining--;
            state.WaveSpawnTimer = SpawnIntervalFrames;
        }

        static void SpawnEnemy(GameState state, int slot)
        {
            ref var e = ref state.Enemies[slot];
            e.IsAlive = true;
            e.BehaviorTimer = 0;
            e.DirX = FInt.Zero; e.DirZ = FInt.Zero;
            e.Type = PickEnemyType(state);

            switch (e.Type)
            {
                case EnemyType.Zombie: e.Hp = GameState.ZombieHp + state.WaveNumber / 3; break;
                case EnemyType.Bat: e.Hp = GameState.BatHp + state.WaveNumber / 4; break;
                case EnemyType.SkeletonMage:
                    e.Hp = GameState.MageHp + state.WaveNumber / 2;
                    e.BehaviorTimer = (uint)DeterministicRng.RangeInt(ref state.RngState, 0, (int)GameState.MageFireCooldown);
                    break;
            }

            int edge = DeterministicRng.RangeInt(ref state.RngState, 0, 4);
            FInt along = DeterministicRng.Range(ref state.RngState, -GameState.ArenaHalfSize, GameState.ArenaHalfSize);
            FInt margin = GameState.ArenaHalfSize + FInt.FromInt(2);

            switch (edge)
            {
                case 0: e.PosX = along; e.PosZ = margin; break;
                case 1: e.PosX = along; e.PosZ = -margin; break;
                case 2: e.PosX = -margin; e.PosZ = along; break;
                default: e.PosX = margin; e.PosZ = along; break;
            }
            e.TargetPlayerId = state.FindNearestPlayer(e.PosX, e.PosZ);
        }

        static EnemyType PickEnemyType(GameState state)
        {
            int roll = DeterministicRng.RangeInt(ref state.RngState, 0, 100);
            if (state.WaveNumber >= 5)
            {
                if (roll < 20) return EnemyType.SkeletonMage;
                if (roll < 50) return EnemyType.Bat;
                return EnemyType.Zombie;
            }
            if (state.WaveNumber >= 3)
            {
                if (roll < 30) return EnemyType.Bat;
            }
            return EnemyType.Zombie;
        }
    }
}
