// BoomNetwork VampireSurvivors Demo — Weapon System (Fixed-Point)

using System;

namespace BoomNetwork.Samples.VampireSurvivors
{
    public static class WeaponSystem
    {
        static readonly FInt _autoAimRange = FInt.FromInt(15);
        static readonly FInt _arenaKillLimit = GameState.ArenaHalfSize + FInt.FromInt(5);
        static readonly FInt _03 = new FInt(307);  // 0.3 * 1024 = 307
        static readonly FInt _05 = new FInt(512);  // 0.5 * 1024 = 512
        static readonly FInt _360 = FInt.FromInt(360);
        static readonly FInt _30 = FInt.FromInt(30);

        public static void Tick(GameState state)
        {
            for (int p = 0; p < GameState.MaxPlayers; p++)
            {
                ref var player = ref state.Players[p];
                if (!player.IsActive || !player.IsAlive) continue;

                for (int w = 0; w < PlayerState.MaxWeaponSlots; w++)
                {
                    var weapon = player.GetWeapon(w);
                    if (weapon.Type == WeaponType.None) continue;
                    if (weapon.Cooldown > 0) { weapon.Cooldown--; player.SetWeapon(w, weapon); continue; }

                    switch (weapon.Type)
                    {
                        case WeaponType.Knife: FireKnife(state, ref player, ref weapon, p); break;
                        case WeaponType.Orb: UpdateOrbs(state, ref player, ref weapon); break;
                        case WeaponType.Lightning: FireLightning(state, ref player, ref weapon, p); break;
                        case WeaponType.HolyWater: FireHolyWater(state, ref player, ref weapon, p); break;
                    }
                    player.SetWeapon(w, weapon);
                }
            }

            AdvanceProjectiles(state);
            AdvanceOrbs(state);

            for (int i = 0; i < GameState.MaxLightningFlashes; i++)
                if (state.Flashes[i].FramesLeft > 0) state.Flashes[i].FramesLeft--;
        }

        static void FireKnife(GameState state, ref PlayerState player, ref WeaponSlot weapon, int playerIdx)
        {
            // Auto-aim
            FInt aimX = player.FacingX, aimZ = player.FacingZ;
            int nearest = state.FindNearestEnemy(player.PosX, player.PosZ, _autoAimRange);
            if (nearest >= 0)
            {
                ref var tgt = ref state.Enemies[nearest];
                FInt dx = tgt.PosX - player.PosX, dz = tgt.PosZ - player.PosZ;
                FInt lenSq = FInt.LengthSqr(dx, dz);
                if (lenSq > FInt.Epsilon) { FInt inv = FInt.InvSqrt(lenSq); aimX = dx * inv; aimZ = dz * inv; }
            }

            int count = 1 + (weapon.Level - 1) / 2;
            FInt spread = count > 1 ? _30 : FInt.Zero;
            FInt startAngle = -spread / 2;
            FInt step = count > 1 ? spread / FInt.FromInt(count - 1) : FInt.Zero;

            for (int k = 0; k < count; k++)
            {
                int slot = state.AllocProjectile();
                if (slot < 0) break;

                FInt angleDeg = startAngle + step * k;
                FInt cos = FInt.CosDeg(angleDeg), sin = FInt.SinDeg(angleDeg);
                FInt dirX = aimX * cos - aimZ * sin;
                FInt dirZ = aimX * sin + aimZ * cos;

                ref var proj = ref state.Projectiles[slot];
                proj.IsAlive = true; proj.Type = ProjectileType.Knife;
                proj.PosX = player.PosX; proj.PosZ = player.PosZ;
                proj.DirX = dirX; proj.DirZ = dirZ;
                proj.Radius = GameState.KnifeRadius;
                proj.LifetimeFrames = GameState.KnifeLifetimeFrames;
                proj.OwnerPlayerId = playerIdx; proj.DamageTick = 0;
            }
            weapon.Cooldown = Math.Max(4u, GameState.KnifeBaseCooldown - (uint)weapon.Level);
        }

        static void UpdateOrbs(GameState state, ref PlayerState player, ref WeaponSlot weapon)
        {
            int orbCount = Math.Min(weapon.Level + 1, PlayerState.MaxOrbs);
            for (int i = 0; i < PlayerState.MaxOrbs; i++)
            {
                var orb = player.GetOrb(i);
                if (i < orbCount)
                {
                    if (!orb.Active) { orb.Active = true; orb.AngleDeg = FInt.FromInt(i) * (_360 / FInt.FromInt(orbCount)); }
                }
                else orb.Active = false;
                player.SetOrb(i, orb);
            }
            weapon.Cooldown = 20;
        }

        static void AdvanceOrbs(GameState state)
        {
            for (int p = 0; p < GameState.MaxPlayers; p++)
            {
                ref var player = ref state.Players[p];
                if (!player.IsActive || !player.IsAlive) continue;
                FInt angStep = GameState.OrbAngularSpeed * state.Dt;
                for (int i = 0; i < PlayerState.MaxOrbs; i++)
                {
                    var orb = player.GetOrb(i);
                    if (!orb.Active) continue;
                    orb.AngleDeg = orb.AngleDeg + angStep;
                    if (orb.AngleDeg >= _360) orb.AngleDeg = orb.AngleDeg - _360;
                    player.SetOrb(i, orb);
                }
            }
        }

        static void FireLightning(GameState state, ref PlayerState player, ref WeaponSlot weapon, int playerIdx)
        {
            int chains = GameState.LightningBaseChains + weapon.Level - 1;
            FInt range = GameState.LightningRange + FInt.FromInt(weapon.Level) * _05;
            FInt rangeSq = range * range;
            int damage = GameState.LightningDamage + weapon.Level;
            FInt cx = player.PosX, cz = player.PosZ;

            for (int c = 0; c < chains; c++)
            {
                int nearest = -1;
                FInt nearestDist = FInt.MaxValue;
                for (int i = 0; i < GameState.MaxEnemies; i++)
                {
                    ref var e = ref state.Enemies[i];
                    if (!e.IsAlive) continue;
                    FInt dx = e.PosX - cx, dz = e.PosZ - cz;
                    FInt d = dx * dx + dz * dz;
                    if (d < rangeSq && d < nearestDist) { nearestDist = d; nearest = i; }
                }
                if (nearest < 0) break;

                ref var enemy = ref state.Enemies[nearest];
                enemy.Hp -= damage;

                int flash = state.AllocFlash();
                if (flash >= 0) { state.Flashes[flash].PosX = enemy.PosX; state.Flashes[flash].PosZ = enemy.PosZ; state.Flashes[flash].FramesLeft = 4; }

                if (enemy.Hp <= 0)
                {
                    enemy.IsAlive = false;
                    SpawnXpGem(state, enemy.PosX, enemy.PosZ, enemy.Type);
                    if (playerIdx >= 0 && playerIdx < GameState.MaxPlayers) state.Players[playerIdx].KillCount++;
                }
                cx = enemy.PosX; cz = enemy.PosZ;
            }
            weapon.Cooldown = Math.Max(10u, GameState.LightningBaseCooldown - (uint)(weapon.Level * 3));
        }

        static void FireHolyWater(GameState state, ref PlayerState player, ref WeaponSlot weapon, int playerIdx)
        {
            int slot = state.AllocProjectile();
            if (slot < 0) { weapon.Cooldown = 5; return; }

            ref var proj = ref state.Projectiles[slot];
            proj.IsAlive = true; proj.Type = ProjectileType.HolyPuddle;
            proj.PosX = player.PosX; proj.PosZ = player.PosZ;
            proj.DirX = FInt.Zero; proj.DirZ = FInt.Zero;
            proj.Radius = GameState.HolyWaterBaseRadius + FInt.FromInt(weapon.Level) * _03;
            proj.LifetimeFrames = GameState.HolyWaterLifetime + (uint)(weapon.Level * 10);
            proj.OwnerPlayerId = playerIdx; proj.DamageTick = 0;
            weapon.Cooldown = Math.Max(20u, GameState.HolyWaterBaseCooldown - (uint)(weapon.Level * 5));
        }

        static void AdvanceProjectiles(GameState state)
        {
            FInt dt = state.Dt;
            for (int i = 0; i < GameState.MaxProjectiles; i++)
            {
                ref var p = ref state.Projectiles[i];
                if (!p.IsAlive) continue;

                if (p.Type == ProjectileType.Knife || p.Type == ProjectileType.BoneShard)
                {
                    FInt speed = p.Type == ProjectileType.Knife ? GameState.KnifeSpeed : GameState.BoneShardSpeed;
                    p.PosX = p.PosX + p.DirX * speed * dt;
                    p.PosZ = p.PosZ + p.DirZ * speed * dt;
                }
                if (p.Type == ProjectileType.HolyPuddle) p.DamageTick++;

                p.LifetimeFrames--;
                if (p.LifetimeFrames == 0) { p.IsAlive = false; continue; }
                if (p.PosX < -_arenaKillLimit || p.PosX > _arenaKillLimit ||
                    p.PosZ < -_arenaKillLimit || p.PosZ > _arenaKillLimit)
                    p.IsAlive = false;
            }
        }

        public static void SpawnXpGem(GameState state, FInt x, FInt z, EnemyType type)
        {
            int gem = state.AllocGem();
            if (gem < 0) return;
            ref var g = ref state.Gems[gem];
            g.IsAlive = true; g.PosX = x; g.PosZ = z;
            g.Value = GameState.GetEnemyXpValue(type);
        }
    }
}
