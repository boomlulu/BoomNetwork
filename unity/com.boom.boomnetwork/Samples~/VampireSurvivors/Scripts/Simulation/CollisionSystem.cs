// BoomNetwork VampireSurvivors Demo — Spatial Hash Collision (Fixed-Point)
// Updated: TwinCore co-hit logic, FocusFire/FrostNova damage scaling,
//          FireTrailPuddle + SplitShot collision, RevivalTotem spawn on death.

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
        static readonly int[] _posXRaw = new int[GameState.MaxEnemies];
        static readonly int[] _posZRaw = new int[GameState.MaxEnemies];

        static readonly FInt _approxEnemyR = new FInt(409);

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
            ResolveSplitShotVsEnemies(state);
            ResolveOrbsVsEnemies(state);
            ResolveHolyPuddleVsEnemies(state);
            ResolveFireTrailVsEnemies(state);
            ResolveEnemiesVsPlayers(state);
            ResolveBoneShardsVsPlayers(state);
            ResolvePlayersVsGems(state);
            TickRevivalTotems(state);
        }

        // --- Spatial query helpers ---

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

        // --- Knife collision ---

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
                if (!enemy.IsAlive) continue;
                FInt actualR = GameState.KnifeRadius + GameState.GetEnemyRadius(enemy.Type);
                long arSq = (long)actualR.Raw * actualR.Raw;
                long dx = proj.PosX.Raw - _posXRaw[hit];
                long dz = proj.PosZ.Raw - _posZRaw[hit];
                if (dx * dx + dz * dz > arSq) continue;

                int dmg = state.ScaleDamage(GameState.KnifeDamage, hit);
                proj.IsAlive = false;
                WeaponSystem.DamageTwinCoreAware(state, hit, dmg, proj.OwnerPlayerId);
            }
        }

        // --- SplitShot Main + Splinter collision ---

        static void ResolveSplitShotVsEnemies(GameState state)
        {
            int approxR = (GameState.SplitShotRadius + _approxEnemyR).Raw;
            for (int i = 0; i < GameState.MaxProjectiles; i++)
            {
                ref var proj = ref state.Projectiles[i];
                if (!proj.IsAlive) continue;
                if (proj.Type != ProjectileType.SplitShotMain && proj.Type != ProjectileType.SplitShotSplinter) continue;

                int hit = QueryNearest(proj.PosX.Raw, proj.PosZ.Raw, approxR);
                if (hit < 0) continue;
                ref var enemy = ref state.Enemies[hit];
                if (!enemy.IsAlive) continue;
                FInt actualR = GameState.SplitShotRadius + GameState.GetEnemyRadius(enemy.Type);
                long arSq = (long)actualR.Raw * actualR.Raw;
                long dx = proj.PosX.Raw - _posXRaw[hit];
                long dz = proj.PosZ.Raw - _posZRaw[hit];
                if (dx * dx + dz * dz > arSq) continue;

                int dmg = state.ScaleDamage(GameState.SplitShotDamage, hit);
                bool wasSplitMain = proj.Type == ProjectileType.SplitShotMain;
                proj.IsAlive = false;
                WeaponSystem.DamageTwinCoreAware(state, hit, dmg, proj.OwnerPlayerId);

                // 主弹命中 → 生成 3 个碎片
                if (wasSplitMain)
                    WeaponSystem.SpawnSplitShotSplinters(state,
                        state.Enemies[hit].PosX, state.Enemies[hit].PosZ,
                        proj.DirX, proj.DirZ, proj.OwnerPlayerId);
            }
        }

        // --- Orbs vs enemies ---

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
                    if (!enemy.IsAlive) continue;
                    FInt actualR = GameState.OrbHitRadius + GameState.GetEnemyRadius(enemy.Type);
                    long arSq = (long)actualR.Raw * actualR.Raw;
                    long dx = orbX.Raw - _posXRaw[hit];
                    long dz = orbZ.Raw - _posZRaw[hit];
                    if (dx * dx + dz * dz > arSq) continue;

                    int dmg = state.ScaleDamage(GameState.OrbDamage, hit);
                    WeaponSystem.DamageTwinCoreAware(state, hit, dmg, p);
                }
            }
        }

        // --- HolyPuddle AoE ---

        static void ResolveHolyPuddleVsEnemies(GameState state)
        {
            ResolveAoEPuddleVsEnemies(state, ProjectileType.HolyPuddle,
                GameState.HolyWaterDamageTick, GameState.HolyWaterDamage);
        }

        // --- FireTrail AoE ---

        static void ResolveFireTrailVsEnemies(GameState state)
        {
            ResolveAoEPuddleVsEnemies(state, ProjectileType.FireTrailPuddle,
                GameState.FireTrailDamageTick, GameState.FireTrailDamage);
        }

        static void ResolveAoEPuddleVsEnemies(GameState state, ProjectileType pType, uint damageTick, int baseDamage)
        {
            for (int i = 0; i < GameState.MaxProjectiles; i++)
            {
                ref var proj = ref state.Projectiles[i];
                if (!proj.IsAlive || proj.Type != pType) continue;
                if (proj.DamageTick % damageTick != 0) continue;

                int searchR = (proj.Radius + _approxEnemyR).Raw;
                int pxRaw = proj.PosX.Raw, pzRaw = proj.PosZ.Raw;
                int owner = proj.OwnerPlayerId;
                FInt projRadius = proj.Radius;

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
                                if (enemy.IsAlive)
                                {
                                    FInt actualR = projRadius + GameState.GetEnemyRadius(enemy.Type);
                                    long arSq = (long)actualR.Raw * actualR.Raw;
                                    if (dx * dx + dz * dz <= arSq)
                                    {
                                        int dmg = state.ScaleDamage(baseDamage, idx);
                                        WeaponSystem.DamageTwinCoreAware(state, idx, dmg, owner);
                                    }
                                }
                            }
                            idx = NextInBucket[idx];
                        }
                    }
            }
        }

        // --- Enemies vs Players ---

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
                if (player.Hp <= 0)
                {
                    player.Hp = 0;
                    player.IsAlive = false;
                    TrySpawnRevivalTotem(state, p);
                }
            }
        }

        // --- BoneShards vs Players ---

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
                    if (player.Hp <= 0)
                    {
                        player.Hp = 0; player.IsAlive = false;
                        TrySpawnRevivalTotem(state, p);
                    }
                    break;
                }
            }
        }

        // --- Gem pickup + level up ---

        public static void AttractGems(GameState state)
        {
            FInt magnetSq = GameState.XpMagnetRadius * GameState.XpMagnetRadius;
            for (int g = 0; g < GameState.MaxGems; g++)
            {
                ref var gem = ref state.Gems[g];
                if (!gem.IsAlive) continue;

                int nearest = -1; FInt nearestDistSq = FInt.MaxValue;
                for (int p = 0; p < GameState.MaxPlayers; p++)
                {
                    ref var player = ref state.Players[p];
                    if (!player.IsActive || !player.IsAlive) continue;
                    FInt distSq = FInt.LengthSqr(player.PosX - gem.PosX, player.PosZ - gem.PosZ);
                    if (distSq < nearestDistSq) { nearestDistSq = distSq; nearest = p; }
                }
                if (nearest < 0) continue;

                if (!gem.Attracting && nearestDistSq > magnetSq) continue;
                gem.Attracting = true;

                FInt dist = FInt.Sqrt(nearestDistSq);
                if (dist.Raw <= 0) continue;

                FInt t = FInt.One - FInt.Clamp(dist / GameState.XpMagnetRadius, FInt.Zero, FInt.One);
                FInt speed = GameState.XpMagnetBaseSpeed + (GameState.XpMagnetMaxSpeed - GameState.XpMagnetBaseSpeed) * t;
                FInt step = speed * state.Dt;
                if (step > dist) step = dist;

                ref var target = ref state.Players[nearest];
                FInt invDist = FInt.InvSqrt(nearestDistSq);
                gem.PosX = gem.PosX + (target.PosX - gem.PosX) * invDist * step;
                gem.PosZ = gem.PosZ + (target.PosZ - gem.PosZ) * invDist * step;
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
                        player.XpToNextLevel = player.XpToNextLevel * 6 / 5 + 2;
                        player.Hp = Math.Min(player.Hp + 40, player.MaxHp);
                        player.PendingLevelUp = true;
                        // 确定性生成升级选项
                        WeaponSystem.GenerateUpgradeOptions(ref player, ref state.RngState);
                    }
                }
            }
        }

        // --- Revival Totem (logic moved from WeaponSystem for clarity) ---

        static void TickRevivalTotems(GameState state)
        {
            // WeaponSystem.TickRevivalTotem 已处理图腾推进；
            // 此处额外保证：若图腾归属玩家复活后自动清除图腾
            for (int t = 0; t < GameState.MaxRevivalTotems; t++)
            {
                ref var totem = ref state.RevivalTotems[t];
                if (!totem.Active) continue;
                ref var owner = ref state.Players[totem.OwnerSlot];
                if (owner.IsAlive) totem.Active = false; // 已复活
            }
        }

        static void TrySpawnRevivalTotem(GameState state, int deadPlayerSlot)
        {
            ref var dead = ref state.Players[deadPlayerSlot];

            // 检查该玩家是否持有 RevivalTotem 武器
            if (dead.FindWeaponSlot(WeaponType.RevivalTotem) < 0) return;

            // 找空图腾槽
            for (int t = 0; t < GameState.MaxRevivalTotems; t++)
            {
                ref var totem = ref state.RevivalTotems[t];
                if (totem.Active && totem.OwnerSlot == deadPlayerSlot) return; // 已有图腾
                if (!totem.Active)
                {
                    totem.Active = true;
                    totem.PosX = dead.PosX;
                    totem.PosZ = dead.PosZ;
                    totem.OwnerSlot = deadPlayerSlot;
                    totem.ReviveProgress = 0;
                    return;
                }
            }
        }

        // --- Kill enemy (full version with boss gems) ---

        static void KillEnemy(GameState state, int idx, int killerPlayerId)
        {
            ref var enemy = ref state.Enemies[idx];
            if (!enemy.IsAlive) return;

            if (enemy.Type == EnemyType.SplitHalf && enemy.LinkedEnemyIdx >= 0)
            {
                ref var partner = ref state.Enemies[enemy.LinkedEnemyIdx];
                if (partner.IsAlive)
                {
                    // 伙伴还活着 → 给它死亡确认窗口
                    partner.HitWindowTimer = GameState.SplitHalfDeathWindow;
                    partner.LinkedEnemyIdx = -1;
                    // 这一半死掉但不立刻给 XP（等双方都死后才算完整击杀）
                    enemy.IsAlive = false;
                    if (killerPlayerId >= 0 && killerPlayerId < GameState.MaxPlayers)
                        state.Players[killerPlayerId].KillCount++;
                    return;
                }
                // 伙伴也已死 → 正常掉落
            }

            bool isBoss = enemy.Type == EnemyType.Boss || enemy.Type == EnemyType.TwinCore
                || enemy.Type == EnemyType.SplitBoss;
            int gemCount = enemy.Type == EnemyType.TwinCore ? GameState.TwinCoreBossGemCount
                : enemy.Type == EnemyType.SplitBoss || enemy.Type == EnemyType.SplitHalf
                    ? GameState.SplitBossBossGemCount / 2
                    : GameState.BossGemCount;

            if (isBoss || enemy.Type == EnemyType.SplitHalf)
            {
                for (int g = 0; g < gemCount; g++)
                {
                    FInt offX = DeterministicRng.Range(ref state.RngState, new FInt(-1024), new FInt(1024));
                    FInt offZ = DeterministicRng.Range(ref state.RngState, new FInt(-1024), new FInt(1024));
                    int gemSlot = state.AllocGem();
                    if (gemSlot < 0) break;
                    ref var gem = ref state.Gems[gemSlot];
                    gem.IsAlive = true;
                    gem.PosX = enemy.PosX + offX; gem.PosZ = enemy.PosZ + offZ;
                    gem.Value = GameState.GetEnemyXpValue(enemy.Type) / Math.Max(1, gemCount);
                }
            }
            else
            {
                WeaponSystem.SpawnXpGem(state, enemy.PosX, enemy.PosZ, enemy.Type);
            }

            // 清除集火标记
            if (state.FocusFireTarget == idx) { state.FocusFireTarget = -1; state.FocusFireTimer = 0; }

            enemy.IsAlive = false;
            if (killerPlayerId >= 0 && killerPlayerId < GameState.MaxPlayers)
                state.Players[killerPlayerId].KillCount++;
        }
    }
}
