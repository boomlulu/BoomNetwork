// BoomNetwork TowerDefense Demo — Wave Spawning System (Fixed-Point)
//
// 10 waves — progressive difficulty introduction:
//   W1:  Basic only          (tutorial)
//   W2:  Basic + Fast        (speed introduced)
//   W3:  Basic + Fast        (more Fast pressure)
//   W4:  + Tank              (tanky threat)
//   W5:  Heavy Tank wave     (Tank majority)
//   W6:  + Armored           (slow high-HP — need Cannon/Sniper)
//   W7:  Armored majority    (armor pressure peaks)
//   W8:  + Elite             (fast immune-to-slow — need Ice+Arrow)
//   W9:  All types mixed     (resource management)
//   W10: Boss wave (all types, large numbers)

namespace BoomNetwork.Samples.TowerDefense
{
    public static class WaveSystem
    {
        const int SpawnInterval = 10; // frames between individual spawns

        // Spawn timer is embedded in Wave.InterWaveTimer when negative (reused as spawn tick)
        // We use a separate static to avoid adding state. Simulation manages it via TDSimulation.
        public static int SpawnTickCounter; // reset on each wave start (non-snapshot field, rebuilt on load)

        public static void Tick(GameState state)
        {
            ref var wave = ref state.Wave;

            if (wave.AllWavesDone) return;

            // Inter-wave countdown
            if (wave.SpawnRemaining == 0)
            {
                if (wave.WaveNumber >= GameState.MaxWaves)
                {
                    wave.AllWavesDone = true;
                    return;
                }

                wave.InterWaveTimer--;
                if (wave.InterWaveTimer > 0) return;

                // Start next wave
                wave.WaveNumber++;
                wave.SpawnRemaining = GetWaveCount(wave.WaveNumber);
                wave.InterWaveTimer = GameState.InterWaveFrames;
                SpawnTickCounter = 0;
                return;
            }

            // Spawning
            SpawnTickCounter++;
            if (SpawnTickCounter < SpawnInterval) return;
            SpawnTickCounter = 0;

            SpawnEnemy(state);
            wave.SpawnRemaining--;
        }

        static void SpawnEnemy(GameState state)
        {
            int wn = state.Wave.WaveNumber;
            EnemyType type = PickEnemyType(state, wn);

            // Pick a random edge spawn position
            // Edges: 0=top(z=0), 1=bottom(z=GridH), 2=left(x=0), 3=right(x=GridW)
            int edge = DeterministicRng.RangeInt(ref state.RngState, 0, 4);
            FInt spawnX, spawnZ;
            switch (edge)
            {
                case 0: // top edge (z = -0.5, x = random)
                    spawnX = FInt.FromInt(DeterministicRng.RangeInt(ref state.RngState, 0, GameState.GridW));
                    spawnZ = FInt.FromFloat(-0.5f);
                    break;
                case 1: // bottom edge
                    spawnX = FInt.FromInt(DeterministicRng.RangeInt(ref state.RngState, 0, GameState.GridW));
                    spawnZ = FInt.FromInt(GameState.GridH);
                    break;
                case 2: // left edge
                    spawnX = FInt.FromFloat(-0.5f);
                    spawnZ = FInt.FromInt(DeterministicRng.RangeInt(ref state.RngState, 0, GameState.GridH));
                    break;
                default: // right edge
                    spawnX = FInt.FromInt(GameState.GridW);
                    spawnZ = FInt.FromInt(DeterministicRng.RangeInt(ref state.RngState, 0, GameState.GridH));
                    break;
            }

            int slot = state.AllocEnemy();
            if (slot < 0) return; // pool full

            ref var e = ref state.Enemies[slot];
            e.IsAlive = true;
            e.Type = type;
            e.PosX = spawnX;
            e.PosZ = spawnZ;
            e.Hp = GameState.GetEnemyHp(type);
            e.SlowFrames = 0;
        }

        static EnemyType PickEnemyType(GameState state, int waveNumber)
        {
            int r = DeterministicRng.RangeInt(ref state.RngState, 0, 100);
            switch (waveNumber)
            {
                case 1: return EnemyType.Basic; // 100% Basic
                case 2: return r < 70 ? EnemyType.Basic : EnemyType.Fast; // 70/30
                case 3: return r < 50 ? EnemyType.Basic : EnemyType.Fast; // 50/50
                case 4: // 40 Basic / 35 Fast / 25 Tank
                    if (r < 40) return EnemyType.Basic;
                    if (r < 75) return EnemyType.Fast;
                    return EnemyType.Tank;
                case 5: // 20 Basic / 20 Fast / 60 Tank (Tank push)
                    if (r < 20) return EnemyType.Basic;
                    if (r < 40) return EnemyType.Fast;
                    return EnemyType.Tank;
                case 6: // 30 Basic / 20 Fast / 20 Tank / 30 Armored (Armored introduced)
                    if (r < 30) return EnemyType.Basic;
                    if (r < 50) return EnemyType.Fast;
                    if (r < 70) return EnemyType.Tank;
                    return EnemyType.Armored;
                case 7: // 15 Basic / 15 Fast / 30 Tank / 40 Armored (Armored peaks)
                    if (r < 15) return EnemyType.Basic;
                    if (r < 30) return EnemyType.Fast;
                    if (r < 60) return EnemyType.Tank;
                    return EnemyType.Armored;
                case 8: // 10 Basic / 15 Fast / 25 Tank / 25 Armored / 25 Elite (Elite introduced)
                    if (r < 10) return EnemyType.Basic;
                    if (r < 25) return EnemyType.Fast;
                    if (r < 50) return EnemyType.Tank;
                    if (r < 75) return EnemyType.Armored;
                    return EnemyType.Elite;
                case 9: // 5 Basic / 15 Fast / 25 Tank / 25 Armored / 30 Elite
                    if (r < 5)  return EnemyType.Basic;
                    if (r < 20) return EnemyType.Fast;
                    if (r < 45) return EnemyType.Tank;
                    if (r < 70) return EnemyType.Armored;
                    return EnemyType.Elite;
                default: // Wave 10 boss: 5 Basic / 10 Fast / 25 Tank / 30 Armored / 30 Elite
                    if (r < 5)  return EnemyType.Basic;
                    if (r < 15) return EnemyType.Fast;
                    if (r < 40) return EnemyType.Tank;
                    if (r < 70) return EnemyType.Armored;
                    return EnemyType.Elite;
            }
        }

        static int GetWaveCount(int waveNumber)
        {
            switch (waveNumber)
            {
                case 1:  return 10;
                case 2:  return 14;
                case 3:  return 18;
                case 4:  return 22;
                case 5:  return 28;
                case 6:  return 30;
                case 7:  return 35;
                case 8:  return 40;
                case 9:  return 50;
                default: return 60; // Wave 10 boss
            }
        }

        /// <summary>Get wave count for display (before spawning starts).</summary>
        public static int GetWaveCountForDisplay(int waveNumber) => GetWaveCount(waveNumber);
    }
}
