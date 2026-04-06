// BoomNetwork VampireSurvivors Demo — Wave Spawner (Fixed-Point)
// Boss 现在交替生成 TwinCore（双核 Boss）和 SplitBoss（分裂 Boss）。

namespace BoomNetwork.Samples.VampireSurvivors
{
    public static class WaveSystem
    {
        const uint WaveGapFrames = 100;
        const uint SpawnIntervalFrames = 5;
        const int BaseEnemyCount = 15;
        const int EnemiesPerWave = 10;

        public static void Tick(GameState state, bool isMultiplayer = true)
        {
            if (!state.HasAlivePlayers()) return;

            if (state.WaveSpawnRemaining == 0)
            {
                if (state.WaveSpawnTimer > 0) { state.WaveSpawnTimer--; return; }
                state.WaveNumber++;
                state.WaveSpawnRemaining = (uint)(BaseEnemyCount + state.WaveNumber * EnemiesPerWave);
                state.WaveSpawnTimer = SpawnIntervalFrames;

                // Boss 只在多人模式生成
                if (isMultiplayer && state.WaveNumber % GameState.BossWaveInterval == 0)
                    SpawnBoss(state);

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
            e.IsAlive = true; e.BehaviorTimer = 0;
            e.DirX = FInt.Zero; e.DirZ = FInt.Zero;
            e.SlowFrames = 0; e.LinkedEnemyIdx = -1; e.HitWindowTimer = 0;
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

        static void SpawnBoss(GameState state)
        {
            // Wave 5, 15, 25... → SplitBoss；Wave 10, 20, 30... → TwinCore 对
            int bossIndex = state.WaveNumber / GameState.BossWaveInterval;
            if (bossIndex % 2 == 0)
                SpawnSplitBoss(state);
            else
                SpawnTwinCorePair(state);
        }

        static void SpawnTwinCorePair(GameState state)
        {
            int slotA = state.AllocEnemy();
            int slotB = slotA >= 0 ? state.AllocEnemy() : -1;
            if (slotA < 0 || slotB < 0) return;

            int hp = GameState.TwinCoreHpPerCore + state.WaveNumber * 3;

            ref var coreA = ref state.Enemies[slotA];
            coreA.IsAlive = true; coreA.Type = EnemyType.TwinCore;
            coreA.Hp = hp; coreA.BehaviorTimer = 0;
            coreA.PosX = -GameState.TwinCoreOffset;
            coreA.PosZ = GameState.ArenaHalfSize + FInt.FromInt(2);
            coreA.DirX = FInt.Zero; coreA.DirZ = FInt.Zero;
            coreA.SlowFrames = 0; coreA.LinkedEnemyIdx = slotB; coreA.HitWindowTimer = 0;
            coreA.TargetPlayerId = state.FindNearestPlayer(coreA.PosX, coreA.PosZ);

            ref var coreB = ref state.Enemies[slotB];
            coreB.IsAlive = true; coreB.Type = EnemyType.TwinCore;
            coreB.Hp = hp; coreB.BehaviorTimer = 0;
            coreB.PosX = GameState.TwinCoreOffset;
            coreB.PosZ = GameState.ArenaHalfSize + FInt.FromInt(2);
            coreB.DirX = FInt.Zero; coreB.DirZ = FInt.Zero;
            coreB.SlowFrames = 0; coreB.LinkedEnemyIdx = slotA; coreB.HitWindowTimer = 0;
            coreB.TargetPlayerId = state.FindNearestPlayer(coreB.PosX, coreB.PosZ);
        }

        static void SpawnSplitBoss(GameState state)
        {
            int slot = state.AllocEnemy();
            if (slot < 0) return;
            ref var e = ref state.Enemies[slot];
            e.IsAlive = true; e.Type = EnemyType.SplitBoss;
            e.Hp = GameState.SplitBossHp + state.WaveNumber * 5;
            e.BehaviorTimer = GameState.SplitBossSplitTimer;
            e.DirX = FInt.Zero; e.DirZ = FInt.Zero;
            e.SlowFrames = 0; e.LinkedEnemyIdx = -1; e.HitWindowTimer = 0;
            e.PosX = FInt.Zero;
            e.PosZ = GameState.ArenaHalfSize + FInt.FromInt(2);
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
