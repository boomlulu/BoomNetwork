// BoomNetwork TowerDefense Demo — Tower Attack Logic (Fixed-Point)
//
// Arrow:    nearest enemy, single-target.
// Cannon:   FARTHEST in range (artillery), AoE blast. Min range 3.
// Magic:    nearest enemy, single-target + slow.
// Ice:      AoE slow pulse centered on tower (no damage). Elite immune.
// Sniper:   nearest enemy, very high single-target damage.
// Fortress: FARTHEST in range (team), heavy AoE blast. [Team tower]
// Storm:    nearest enemy, chains to N additional targets. [Team tower]
//
// REWARD SPLIT (Option D):
//   75% of kill reward → tower owner's personal gold.
//   25% → shared gold.
//   Team towers (OwnerId == TeamOwner): 100% → shared gold.

namespace BoomNetwork.Samples.TowerDefense
{
    public static class TowerSystem
    {
        public static void Tick(GameState state)
        {
            for (int cy = 0; cy < GameState.GridH; cy++)
            {
                for (int cx = 0; cx < GameState.GridW; cx++)
                {
                    int gIdx = GameState.CellIndex(cx, cy);
                    ref var tower = ref state.Grid[gIdx];
                    if (tower.Type == TowerType.None) continue;

                    if (tower.CooldownFrames > 0)
                    {
                        tower.CooldownFrames--;
                        continue;
                    }

                    FInt towerX  = GameState.CellCenterX(cx);
                    FInt towerZ  = GameState.CellCenterZ(cy);
                    FInt range   = GameState.GetTowerRange(tower.Type, tower.Level);
                    FInt rangeSq = range * range;
                    int  lvl     = tower.Level;
                    int  owner   = tower.OwnerId;

                    // Target selection
                    int target = (tower.Type == TowerType.Cannon || tower.Type == TowerType.Fortress)
                        ? FindFarthest(state, towerX, towerZ, rangeSq)
                        : FindNearest(state, towerX, towerZ, rangeSq);

                    if (target < 0) continue; // no valid target

                    switch (tower.Type)
                    {
                        case TowerType.Arrow:
                            DamageEnemy(state, target, GameState.GetTowerDamage(TowerType.Arrow, lvl), owner);
                            break;

                        case TowerType.Cannon:
                            FireCannon(state, target, lvl, owner);
                            break;

                        case TowerType.Magic:
                            DamageEnemy(state, target, GameState.GetTowerDamage(TowerType.Magic, lvl), owner);
                            if (state.Enemies[target].IsAlive)
                                state.Enemies[target].SlowFrames = GameState.GetMagicSlowFrames(lvl);
                            break;

                        case TowerType.Ice:
                            FireIce(state, towerX, towerZ, lvl); // AoE from tower center
                            break;

                        case TowerType.Sniper:
                            DamageEnemy(state, target, GameState.GetTowerDamage(TowerType.Sniper, lvl), owner);
                            break;

                        case TowerType.Fortress:
                            FireFortress(state, target, lvl, owner);
                            break;

                        case TowerType.Storm:
                            FireStorm(state, target, towerX, towerZ, rangeSq, lvl, owner);
                            break;
                    }

                    tower.CooldownFrames = GameState.GetTowerCooldown(tower.Type, tower.Level);
                }
            }
        }

        // ── Cannon: farthest in [minRange, maxRange] ──────────────────────
        static readonly FInt CannonMinRangeSq =
            GameState.CannonMinRange * GameState.CannonMinRange;

        static int FindFarthest(GameState state, FInt towerX, FInt towerZ, FInt rangeSq)
        {
            int  best     = -1;
            FInt bestDist = CannonMinRangeSq;
            for (int i = 0; i < GameState.MaxEnemies; i++)
            {
                ref var e = ref state.Enemies[i];
                if (!e.IsAlive) continue;
                FInt dSq = FInt.DistanceSqr(towerX, towerZ, e.PosX, e.PosZ);
                if (dSq <= rangeSq && dSq > bestDist) { bestDist = dSq; best = i; }
            }
            return best;
        }

        static int FindNearest(GameState state, FInt towerX, FInt towerZ, FInt rangeSq)
        {
            int  best     = -1;
            FInt bestDist = FInt.MaxValue;
            for (int i = 0; i < GameState.MaxEnemies; i++)
            {
                ref var e = ref state.Enemies[i];
                if (!e.IsAlive) continue;
                FInt dSq = FInt.DistanceSqr(towerX, towerZ, e.PosX, e.PosZ);
                if (dSq <= rangeSq && dSq < bestDist) { bestDist = dSq; best = i; }
            }
            return best;
        }

        // ── Cannon AoE ────────────────────────────────────────────────────
        static void FireCannon(GameState state, int primaryTarget, int level, int owner)
        {
            ref var primary = ref state.Enemies[primaryTarget];
            FInt hitX    = primary.PosX, hitZ = primary.PosZ;
            FInt blast   = GameState.GetCannonAoeRadius(level);
            FInt blastSq = blast * blast;
            int  dmg     = GameState.GetTowerDamage(TowerType.Cannon, level);
            for (int i = 0; i < GameState.MaxEnemies; i++)
            {
                if (!state.Enemies[i].IsAlive) continue;
                if (FInt.DistanceSqr(hitX, hitZ, state.Enemies[i].PosX, state.Enemies[i].PosZ) <= blastSq)
                    DamageEnemy(state, i, dmg, owner);
            }
        }

        // ── Ice slow pulse ────────────────────────────────────────────────
        static void FireIce(GameState state, FInt towerX, FInt towerZ, int level)
        {
            FInt aoeR   = GameState.GetIceAoeRadius(level);
            FInt aoeSq  = aoeR * aoeR;
            int  frames = GameState.GetIceSlowFrames(level);
            for (int i = 0; i < GameState.MaxEnemies; i++)
            {
                ref var e = ref state.Enemies[i];
                if (!e.IsAlive || e.Type == EnemyType.Elite) continue;
                if (FInt.DistanceSqr(towerX, towerZ, e.PosX, e.PosZ) <= aoeSq && e.SlowFrames < frames)
                    e.SlowFrames = frames;
            }
        }

        // ── Fortress heavy AoE ────────────────────────────────────────────
        static void FireFortress(GameState state, int primaryTarget, int level, int owner)
        {
            ref var primary = ref state.Enemies[primaryTarget];
            FInt hitX    = primary.PosX, hitZ = primary.PosZ;
            FInt blast   = GameState.GetFortressAoeRadius(level);
            FInt blastSq = blast * blast;
            int  dmg     = GameState.GetTowerDamage(TowerType.Fortress, level);
            for (int i = 0; i < GameState.MaxEnemies; i++)
            {
                if (!state.Enemies[i].IsAlive) continue;
                if (FInt.DistanceSqr(hitX, hitZ, state.Enemies[i].PosX, state.Enemies[i].PosZ) <= blastSq)
                    DamageEnemy(state, i, dmg, owner);
            }
        }

        // ── Storm chain lightning ─────────────────────────────────────────
        // Hits primary target + up to (chainCount-1) additional enemies in range.
        // Deterministic: picks additional targets in slot-index order (not random).
        static void FireStorm(GameState state, int primaryTarget,
            FInt towerX, FInt towerZ, FInt rangeSq, int level, int owner)
        {
            int dmg        = GameState.GetTowerDamage(TowerType.Storm, level);
            int chainCount = GameState.GetStormChainCount(level); // 2/3/4

            DamageEnemy(state, primaryTarget, dmg, owner);
            int chains = 1;

            for (int i = 0; i < GameState.MaxEnemies && chains < chainCount; i++)
            {
                if (i == primaryTarget) continue;
                ref var e = ref state.Enemies[i];
                if (!e.IsAlive) continue;
                if (FInt.DistanceSqr(towerX, towerZ, e.PosX, e.PosZ) <= rangeSq)
                {
                    DamageEnemy(state, i, dmg, owner);
                    chains++;
                }
            }
        }

        // ── Reward distribution ───────────────────────────────────────────
        static void DamageEnemy(GameState state, int idx, int damage, int ownerId)
        {
            ref var e = ref state.Enemies[idx];
            if (!e.IsAlive) return;
            e.Hp -= damage;
            if (e.Hp <= 0)
            {
                e.IsAlive = false;
                int total = GameState.GetEnemyReward(e.Type);
                if (ownerId == GameState.TeamOwner)
                {
                    // Team tower: 100% goes to shared treasury
                    state.SharedGold += total;
                }
                else if (ownerId >= 0 && ownerId < GameState.MaxPlayers)
                {
                    // Personal tower: 75% personal, 25% shared
                    int personal = total * GameState.PersonalRewardPct / 100;
                    state.PlayerGold[ownerId] += personal;
                    state.SharedGold          += total - personal;
                }
                else
                {
                    state.SharedGold += total;
                }
            }
        }
    }
}
