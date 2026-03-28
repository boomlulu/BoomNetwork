// BoomNetwork VampireSurvivors Demo — Spatial Hash Collision (Fixed-Point)

using System;

namespace BoomNetwork.Samples.VampireSurvivors
{
    public static class CollisionSystem
    {
        static readonly FInt CellSize = FInt.FromInt(2);
        const int GridCells = 20;
        const int TotalCells = GridCells * GridCells;

        static readonly int[] BucketHeads = new int[TotalCells];
        static readonly int[] NextInBucket = new int[GameState.MaxEnemies];
        // Cached positions as raw ints for fast lookup
        static readonly int[] _posXRaw = new int[GameState.MaxEnemies];
        static readonly int[] _posZRaw = new int[GameState.MaxEnemies];

        static readonly FInt _approxEnemyR = new FInt(409);  // 0.4 * 1024 = 409

        static int CellIndex(int rawX, int rawZ)
        {
            int cx = (rawX + GameState.ArenaHalfSize.Raw) / CellSize.Raw;
            int cz = (rawZ + GameState.ArenaHalfSize.Raw) / CellSize.Raw;
            if (cx < 0) cx = 0; else if (cx >= GridCells) cx = GridCells - 1;
            if (cz < 0) cz = 0; else if (cz >= GridCells) cz = GridCells - 1;
            return cz * GridCells + cx;
        }

        public static void CachePositions(GameState state)
        {
            for (int i = 0; i < GameState.MaxEnemies; i++)
            {
                _posXRaw[i] = state.Enemies[i].PosX.Raw;
                _posZRaw[i] = state.Enemies[i].PosZ.Raw;
            }
        }

        public static void Rebuild(GameState state)
        {
            for (int i = 0; i < TotalCells; i++) BucketHeads[i] = -1;
            for (int i = 0; i < GameState.MaxEnemies; i++)
            {
                if (!state.Enemies[i].IsAlive) continue;
                int cell = CellIndex(_posXRaw[i], _posZRaw[i]);
                NextInBucket[i] = BucketHeads[cell];
                BucketHeads[cell] = i;
            }
        }

        public static void Resolve(GameState state)
        {
            ResolveKnivesVsEnemies(state);
            ResolveOrbsVsEnemies(state);
            ResolveHolyPuddleVsEnemies(state);
            ResolveEnemiesVsPlayers(state);
            ResolveBoneShardsVsPlayers(state);
            ResolvePlayersVsGems(state);
        }

        // --- Spatial query helpers (operate on Raw ints for speed) ---

        static int QueryNearest(int cxRaw, int czRaw, int radiusRaw)
        {
            long rSq = (long)radiusRaw * radiusRaw;
            int hsRaw = GameState.ArenaHalfSize.Raw;
            int cellRaw = CellSize.Raw;
            int minCx = (cxRaw - radiusRaw + hsRaw) / cellRaw;
            int maxCx = (cxRaw + radiusRaw + hsRaw) / cellRaw;
            int minCz = (czRaw - radiusRaw + hsRaw) / cellRaw;
            int maxCz = (czRaw + radiusRaw + hsRaw) / cellRaw;
            if (minCx < 0) minCx = 0; if (maxCx >= GridCells) maxCx = GridCells - 1;
            if (minCz < 0) minCz = 0; if (maxCz >= GridCells) maxCz = GridCells - 1;

            int bestIdx = -1;
            long bestDist = long.MaxValue;
            for (int gz = minCz; gz <= maxCz; gz++)
                for (int gx = minCx; gx <= maxCx; gx++)
                {
                    int idx = BucketHeads[gz * GridCells + gx];
                    while (idx >= 0)
                    {
                        long dx = cxRaw - _posXRaw[idx];
                        long dz = czRaw - _posZRaw[idx];
                        long dSq = dx * dx + dz * dz;
                        if (dSq < rSq && dSq < bestDist) { bestDist = dSq; bestIdx = idx; }
                        idx = NextInBucket[idx];
                    }
                }
            return bestIdx;
        }

        // --- Collision resolvers ---

        static void ResolveKnivesVsEnemies(GameState state)
        {
            int approxR = (GameState.KnifeRadius + _approxEnemyR).Raw;
            for (int i = 0; i < GameState.MaxProjectiles; i++)
            {
                ref var proj = ref state.Projectiles[i];
                if (!proj.IsAlive || proj.Type != ProjectileType.Knife) continue;

                int hit = QueryNearest(proj.PosX.Raw, proj.PosZ.Raw, approxR);
                if (hit < 0) continue;

                ref var enemy = ref state.Enemies[hit];
                if (!enemy.IsAlive) continue; // may have been killed by Lightning this frame
                FInt actualR = GameState.KnifeRadius + GameState.GetEnemyRadius(enemy.Type);
                long arSq = (long)actualR.Raw * actualR.Raw;
                long dx = proj.PosX.Raw - _posXRaw[hit];
                long dz = proj.PosZ.Raw - _posZRaw[hit];
                if (dx * dx + dz * dz > arSq) continue;

                enemy.Hp -= GameState.KnifeDamage;
                proj.IsAlive = false;
                if (enemy.Hp <= 0) KillEnemy(state, hit, proj.OwnerPlayerId);
            }
        }

        static void ResolveOrbsVsEnemies(GameState state)
        {
            int approxR = (GameState.OrbHitRadius + _approxEnemyR).Raw;
            for (int p = 0; p < GameState.MaxPlayers; p++)
            {
                ref var player = ref state.Players[p];
                if (!player.IsActive || !player.IsAlive) continue;
                for (int o = 0; o < PlayerState.MaxOrbs; o++)
                {
                    var orb = player.GetOrb(o);
                    if (!orb.Active) continue;
                    FInt orbX = player.PosX + FInt.CosDeg(orb.AngleDeg) * GameState.OrbOrbitRadius;
                    FInt orbZ = player.PosZ + FInt.SinDeg(orb.AngleDeg) * GameState.OrbOrbitRadius;

                    int hit = QueryNearest(orbX.Raw, orbZ.Raw, approxR);
                    if (hit < 0) continue;

                    ref var enemy = ref state.Enemies[hit];
                    if (!enemy.IsAlive) continue; // may have been killed earlier this frame
                    FInt actualR = GameState.OrbHitRadius + GameState.GetEnemyRadius(enemy.Type);
                    long arSq = (long)actualR.Raw * actualR.Raw;
                    long dx = orbX.Raw - _posXRaw[hit];
                    long dz = orbZ.Raw - _posZRaw[hit];
                    if (dx * dx + dz * dz > arSq) continue;

                    enemy.Hp -= GameState.OrbDamage;
                    if (enemy.Hp <= 0) KillEnemy(state, hit, p);
                }
            }
        }

        static void ResolveHolyPuddleVsEnemies(GameState state)
        {
            for (int i = 0; i < GameState.MaxProjectiles; i++)
            {
                ref var proj = ref state.Projectiles[i];
                if (!proj.IsAlive || proj.Type != ProjectileType.HolyPuddle) continue;
                if (proj.DamageTick % GameState.HolyWaterDamageTick != 0) continue;

                int searchR = (proj.Radius + _approxEnemyR).Raw;
                int pxRaw = proj.PosX.Raw, pzRaw = proj.PosZ.Raw;
                int owner = proj.OwnerPlayerId;
                FInt projRadius = proj.Radius;

                // Inlined spatial hash query
                int hsRaw = GameState.ArenaHalfSize.Raw, cellRaw = CellSize.Raw;
                long searchRSq = (long)searchR * searchR;
                int minCx = (pxRaw - searchR + hsRaw) / cellRaw;
                int maxCx = (pxRaw + searchR + hsRaw) / cellRaw;
                int minCz = (pzRaw - searchR + hsRaw) / cellRaw;
                int maxCz = (pzRaw + searchR + hsRaw) / cellRaw;
                if (minCx < 0) minCx = 0; if (maxCx >= GridCells) maxCx = GridCells - 1;
                if (minCz < 0) minCz = 0; if (maxCz >= GridCells) maxCz = GridCells - 1;

                for (int gz = minCz; gz <= maxCz; gz++)
                    for (int gx = minCx; gx <= maxCx; gx++)
                    {
                        int idx = BucketHeads[gz * GridCells + gx];
                        while (idx >= 0)
                        {
                            long dx = pxRaw - _posXRaw[idx];
                            long dz = pzRaw - _posZRaw[idx];
                            if (dx * dx + dz * dz < searchRSq)
                            {
                                ref var enemy = ref state.Enemies[idx];
                                if (!enemy.IsAlive) { idx = NextInBucket[idx]; continue; }
                                FInt actualR = projRadius + GameState.GetEnemyRadius(enemy.Type);
                                long arSq = (long)actualR.Raw * actualR.Raw;
                                if (dx * dx + dz * dz <= arSq)
                                {
                                    enemy.Hp -= GameState.HolyWaterDamage;
                                    if (enemy.Hp <= 0) KillEnemy(state, idx, owner);
                                }
                            }
                            idx = NextInBucket[idx];
                        }
                    }
            }
        }

        static void ResolveEnemiesVsPlayers(GameState state)
        {
            for (int p = 0; p < GameState.MaxPlayers; p++)
            {
                ref var player = ref state.Players[p];
                if (!player.IsActive || !player.IsAlive || player.InvincibilityFrames > 0) continue;

                int searchR = (GameState.PlayerRadius + _approxEnemyR).Raw;
                int hit = QueryNearest(player.PosX.Raw, player.PosZ.Raw, searchR);
                if (hit < 0) continue;

                ref var enemy = ref state.Enemies[hit];
                FInt actualR = GameState.PlayerRadius + GameState.GetEnemyRadius(enemy.Type);
                long arSq = (long)actualR.Raw * actualR.Raw;
                long dx = player.PosX.Raw - _posXRaw[hit];
                long dz = player.PosZ.Raw - _posZRaw[hit];
                if (dx * dx + dz * dz > arSq) continue;

                player.Hp -= GameState.GetEnemyDamage(enemy.Type);
                player.InvincibilityFrames = GameState.InvincibilityDuration;
                if (player.Hp <= 0) { player.Hp = 0; player.IsAlive = false; }
            }
        }

        static void ResolveBoneShardsVsPlayers(GameState state)
        {
            for (int i = 0; i < GameState.MaxProjectiles; i++)
            {
                ref var proj = ref state.Projectiles[i];
                if (!proj.IsAlive || proj.Type != ProjectileType.BoneShard) continue;

                for (int p = 0; p < GameState.MaxPlayers; p++)
                {
                    ref var player = ref state.Players[p];
                    if (!player.IsActive || !player.IsAlive || player.InvincibilityFrames > 0) continue;

                    FInt r = proj.Radius + GameState.PlayerRadius;
                    long rSq = (long)r.Raw * r.Raw;
                    long dx = proj.PosX.Raw - player.PosX.Raw;
                    long dz = proj.PosZ.Raw - player.PosZ.Raw;
                    if (dx * dx + dz * dz > rSq) continue;

                    player.Hp -= GameState.BoneShardDamage;
                    player.InvincibilityFrames = GameState.InvincibilityDuration;
                    proj.IsAlive = false;
                    if (player.Hp <= 0) { player.Hp = 0; player.IsAlive = false; }
                    break;
                }
            }
        }

        static void ResolvePlayersVsGems(GameState state)
        {
            long rSq = (long)GameState.XpPickupRadius.Raw * GameState.XpPickupRadius.Raw;
            for (int p = 0; p < GameState.MaxPlayers; p++)
            {
                ref var player = ref state.Players[p];
                if (!player.IsActive || !player.IsAlive) continue;

                for (int g = 0; g < GameState.MaxGems; g++)
                {
                    ref var gem = ref state.Gems[g];
                    if (!gem.IsAlive) continue;
                    long dx = player.PosX.Raw - gem.PosX.Raw;
                    long dz = player.PosZ.Raw - gem.PosZ.Raw;
                    if (dx * dx + dz * dz > rSq) continue;

                    player.Xp += gem.Value;
                    gem.IsAlive = false;

                    while (player.Xp >= player.XpToNextLevel)
                    {
                        player.Xp -= player.XpToNextLevel;
                        player.Level++;
                        player.XpToNextLevel = player.XpToNextLevel * 6 / 5 + 2; // ×1.2 + 2, integer only
                        player.Hp = Math.Min(player.Hp + 40, player.MaxHp);
                        player.PendingLevelUp = true;
                    }
                }
            }
        }

        static void KillEnemy(GameState state, int idx, int killerPlayerId)
        {
            ref var enemy = ref state.Enemies[idx];

            if (enemy.Type == EnemyType.Boss)
            {
                // Boss scatters multiple gems on death
                for (int g = 0; g < GameState.BossGemCount; g++)
                {
                    FInt offX = DeterministicRng.Range(ref state.RngState, new FInt(-1024), new FInt(1024));
                    FInt offZ = DeterministicRng.Range(ref state.RngState, new FInt(-1024), new FInt(1024));
                    int gemSlot = state.AllocGem();
                    if (gemSlot < 0) break;
                    ref var gem = ref state.Gems[gemSlot];
                    gem.IsAlive = true;
                    gem.PosX = enemy.PosX + offX;
                    gem.PosZ = enemy.PosZ + offZ;
                    gem.Value = GameState.BossXpValue / GameState.BossGemCount;
                }
            }
            else
            {
                WeaponSystem.SpawnXpGem(state, enemy.PosX, enemy.PosZ, enemy.Type);
            }

            enemy.IsAlive = false;
            if (killerPlayerId >= 0 && killerPlayerId < GameState.MaxPlayers)
                state.Players[killerPlayerId].KillCount++;
        }
    }
}
