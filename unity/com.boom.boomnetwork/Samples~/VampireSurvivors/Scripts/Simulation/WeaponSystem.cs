// BoomNetwork VampireSurvivors Demo — Weapon System (Fixed-Point)
// 10 new co-op weapons added. All logic deterministic (FInt only).

using System;

namespace BoomNetwork.Samples.VampireSurvivors
{
    public static class WeaponSystem
    {
        static readonly FInt _autoAimRange = FInt.FromInt(15);
        static readonly FInt _arenaKillLimit = GameState.ArenaHalfSize + FInt.FromInt(5);
        static readonly FInt _03 = new FInt(307);
        static readonly FInt _05 = new FInt(512);
        static readonly FInt _360 = FInt.FromInt(360);
        static readonly FInt _30 = FInt.FromInt(30);

        // 全部 14 种武器，用于多人升级选项随机池
        static readonly WeaponType[] AllWeapons = new WeaponType[]
        {
            WeaponType.Knife, WeaponType.Orb, WeaponType.Lightning, WeaponType.HolyWater,
            WeaponType.LinkBeam, WeaponType.HealAura, WeaponType.ShieldWall,
            WeaponType.ChainLightningPlus, WeaponType.FocusFire, WeaponType.RevivalTotem,
            WeaponType.FrostNova, WeaponType.FireTrail, WeaponType.MagnetField, WeaponType.SplitShot,
        };

        // 单人模式武器池（移除 6 个协作技能）
        static readonly WeaponType[] SoloWeapons = new WeaponType[]
        {
            WeaponType.Knife, WeaponType.Orb, WeaponType.Lightning, WeaponType.HolyWater,
            WeaponType.FrostNova, WeaponType.FireTrail, WeaponType.MagnetField, WeaponType.SplitShot,
        };

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
                        case WeaponType.Knife:              FireKnife(state, ref player, ref weapon, p); break;
                        case WeaponType.Orb:                UpdateOrbs(state, ref player, ref weapon); break;
                        case WeaponType.Lightning:          FireLightning(state, ref player, ref weapon, p); break;
                        case WeaponType.HolyWater:          FireHolyWater(state, ref player, ref weapon, p); break;
                        case WeaponType.LinkBeam:           FireLinkBeam(state, ref player, ref weapon, p); break;
                        case WeaponType.HealAura:           FireHealAura(state, ref player, ref weapon, p); break;
                        case WeaponType.ShieldWall:         FireShieldWall(state, ref player, ref weapon, p); break;
                        case WeaponType.ChainLightningPlus: FireChainLightningPlus(state, ref player, ref weapon, p); break;
                        case WeaponType.FocusFire:          FireFocusFire(state, ref player, ref weapon, p); break;
                        case WeaponType.RevivalTotem:       TickRevivalTotem(state, ref player, ref weapon, p); break;
                        case WeaponType.FrostNova:          FireFrostNova(state, ref player, ref weapon, p); break;
                        case WeaponType.FireTrail:          FireTrailDrop(state, ref player, ref weapon, p); break;
                        case WeaponType.MagnetField:        FireMagnetField(state, ref player, ref weapon, p); break;
                        case WeaponType.SplitShot:          FireSplitShot(state, ref player, ref weapon, p); break;
                    }
                    player.SetWeapon(w, weapon);
                }
            }

            AdvanceProjectiles(state);
            AdvanceOrbs(state);
            TickFocusFireTimer(state);

            for (int i = 0; i < GameState.MaxLightningFlashes; i++)
                if (state.Flashes[i].FramesLeft > 0) state.Flashes[i].FramesLeft--;
        }

        // ==================== 原有武器 ====================

        static void FireKnife(GameState state, ref PlayerState player, ref WeaponSlot weapon, int playerIdx)
        {
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
                ref var proj = ref state.Projectiles[slot];
                proj.IsAlive = true; proj.Type = ProjectileType.Knife;
                proj.PosX = player.PosX; proj.PosZ = player.PosZ;
                proj.DirX = aimX * cos - aimZ * sin;
                proj.DirZ = aimX * sin + aimZ * cos;
                proj.Radius = GameState.KnifeRadius;
                proj.LifetimeFrames = GameState.KnifeLifetimeFrames;
                proj.OwnerPlayerId = playerIdx; proj.DamageTick = 0;
            }
            weapon.Cooldown = (uint)Math.Max(4, (int)GameState.KnifeBaseCooldown - weapon.Level);
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
                int nearest = -1; FInt nearestDist = FInt.MaxValue;
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
                int dmg = state.ScaleDamage(damage, nearest);
                enemy.Hp -= dmg;
                SpawnFlash(state, enemy.PosX, enemy.PosZ);
                if (enemy.Hp <= 0) KillEnemySimple(state, nearest, playerIdx);
                cx = enemy.PosX; cz = enemy.PosZ;
            }
            weapon.Cooldown = (uint)Math.Max(10, (int)GameState.LightningBaseCooldown - weapon.Level * 3);
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
            weapon.Cooldown = (uint)Math.Max(20, (int)GameState.HolyWaterBaseCooldown - weapon.Level * 5);
        }

        // ==================== 新协作技能 ====================

        /// <summary>链接光束：两玩家连线持续伤害沿线敌人。距离越近伤害加倍。</summary>
        static void FireLinkBeam(GameState state, ref PlayerState playerA, ref WeaponSlot weapon, int aIdx)
        {
            int damage = GameState.LinkBeamDamage + weapon.Level / 2;
            bool anyBeam = false;

            for (int b = 0; b < GameState.MaxPlayers; b++)
            {
                if (b == aIdx) continue;
                ref var playerB = ref state.Players[b];
                if (!playerB.IsActive || !playerB.IsAlive) continue;

                FInt abX = playerB.PosX - playerA.PosX;
                FInt abZ = playerB.PosZ - playerA.PosZ;
                FInt abLenSq = abX * abX + abZ * abZ;
                if (abLenSq < FInt.Epsilon) continue;

                FInt invAbLen = FInt.InvSqrt(abLenSq);
                FInt abUnitX = abX * invAbLen;
                FInt abUnitZ = abZ * invAbLen;
                FInt abLen = abLenSq * invAbLen; // ≈ |AB|

                // 近距离（< 5 单位）→ 2x 伤害
                int actualDamage = abLen < GameState.LinkBeamCloseDist ? damage * 2 : damage;
                anyBeam = true;

                for (int i = 0; i < GameState.MaxEnemies; i++)
                {
                    ref var e = ref state.Enemies[i];
                    if (!e.IsAlive) continue;

                    FInt aeX = e.PosX - playerA.PosX;
                    FInt aeZ = e.PosZ - playerA.PosZ;
                    FInt dot = aeX * abUnitX + aeZ * abUnitZ;
                    if (dot < FInt.Zero || dot > abLen) continue;

                    FInt cross = aeX * abUnitZ - aeZ * abUnitX;
                    if (cross < FInt.Zero) cross = -cross;
                    if (cross > GameState.LinkBeamWidth) continue;

                    int dmg = state.ScaleDamage(actualDamage, i);
                    DamageTwinCoreAware(state, i, dmg, aIdx);
                }
            }
            weapon.Cooldown = 1;
        }

        /// <summary>治疗光环：只治疗队友（不治疗自己）。</summary>
        static void FireHealAura(GameState state, ref PlayerState healer, ref WeaponSlot weapon, int healerIdx)
        {
            FInt radiusSq = GameState.HealAuraRadius * GameState.HealAuraRadius;
            int healAmount = GameState.HealAuraAmount + weapon.Level / 2;

            for (int i = 0; i < GameState.MaxPlayers; i++)
            {
                if (i == healerIdx) continue;
                ref var ally = ref state.Players[i];
                if (!ally.IsActive || !ally.IsAlive) continue;
                FInt dx = ally.PosX - healer.PosX, dz = ally.PosZ - healer.PosZ;
                if (dx * dx + dz * dz > radiusSq) continue;
                ally.Hp = Math.Min(ally.Hp + healAmount, ally.MaxHp);
            }
            weapon.Cooldown = (uint)Math.Max(30, (int)GameState.HealAuraBaseCooldown - weapon.Level * 3);
        }

        /// <summary>战术护盾：两玩家之间的中线持续伤害经过的敌人。</summary>
        static void FireShieldWall(GameState state, ref PlayerState playerA, ref WeaponSlot weapon, int aIdx)
        {
            int damage = GameState.ShieldWallDamage + weapon.Level / 2;
            FInt wallWidthSq = GameState.ShieldWallWidth * GameState.ShieldWallWidth;

            for (int b = 0; b < GameState.MaxPlayers; b++)
            {
                if (b == aIdx) continue;
                ref var playerB = ref state.Players[b];
                if (!playerB.IsActive || !playerB.IsAlive) continue;

                // 墙中心 = 两玩家中点
                FInt midX = (playerA.PosX + playerB.PosX) / FInt.FromInt(2);
                FInt midZ = (playerA.PosZ + playerB.PosZ) / FInt.FromInt(2);

                // 墙宽范围检测
                FInt wallLen = FInt.FromInt(weapon.Level + 2);
                FInt abX = playerB.PosX - playerA.PosX;
                FInt abZ = playerB.PosZ - playerA.PosZ;
                FInt abLenSq = abX * abX + abZ * abZ;
                if (abLenSq < FInt.Epsilon) continue;

                FInt invAbLen = FInt.InvSqrt(abLenSq);
                FInt abUnitX = abX * invAbLen;
                FInt abUnitZ = abZ * invAbLen;
                FInt abLen = abLenSq * invAbLen;
                FInt halfLen = abLen / FInt.FromInt(2);

                for (int i = 0; i < GameState.MaxEnemies; i++)
                {
                    ref var e = ref state.Enemies[i];
                    if (!e.IsAlive) continue;

                    FInt aeX = e.PosX - playerA.PosX;
                    FInt aeZ = e.PosZ - playerA.PosZ;
                    FInt dot = aeX * abUnitX + aeZ * abUnitZ;
                    if (dot < FInt.Zero || dot > abLen) continue;

                    // 靠近中点（中段 1/3）的敌人才受伤
                    FInt fromMid = dot - halfLen;
                    if (fromMid < FInt.Zero) fromMid = -fromMid;
                    FInt wallHalf = abLen / FInt.FromInt(6);
                    if (fromMid > wallHalf) continue;

                    FInt cross = aeX * abUnitZ - aeZ * abUnitX;
                    if (cross < FInt.Zero) cross = -cross;
                    if (cross > GameState.ShieldWallWidth) continue;

                    int dmg = state.ScaleDamage(damage, i);
                    DamageTwinCoreAware(state, i, dmg, aIdx);
                }
            }
            weapon.Cooldown = 1;
        }

        /// <summary>连锁闪电增强：经过队友附近的跳跃伤害翻倍。</summary>
        static void FireChainLightningPlus(GameState state, ref PlayerState player, ref WeaponSlot weapon, int playerIdx)
        {
            int chains = GameState.LightningBaseChains + weapon.Level;
            FInt range = GameState.LightningRange + FInt.FromInt(weapon.Level);
            FInt rangeSq = range * range;
            int baseDamage = GameState.ChainLightningPlusDamage + weapon.Level;
            FInt cx = player.PosX, cz = player.PosZ;

            for (int c = 0; c < chains; c++)
            {
                int nearest = -1; FInt nearestDist = FInt.MaxValue;
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

                // 检查是否有队友在闪电跳点附近
                bool nearTeammate = false;
                FInt teamRSq = GameState.ChainLightningPlusTeammateRadius * GameState.ChainLightningPlusTeammateRadius;
                for (int t = 0; t < GameState.MaxPlayers; t++)
                {
                    if (t == playerIdx) continue;
                    ref var ally = ref state.Players[t];
                    if (!ally.IsActive || !ally.IsAlive) continue;
                    FInt dx = ally.PosX - enemy.PosX, dz = ally.PosZ - enemy.PosZ;
                    if (dx * dx + dz * dz < teamRSq) { nearTeammate = true; break; }
                }

                int damage = nearTeammate ? baseDamage * 2 : baseDamage;
                int dmg = state.ScaleDamage(damage, nearest);
                enemy.Hp -= dmg;
                SpawnFlash(state, enemy.PosX, enemy.PosZ);
                if (enemy.Hp <= 0) KillEnemySimple(state, nearest, playerIdx);
                cx = enemy.PosX; cz = enemy.PosZ;
            }
            weapon.Cooldown = (uint)Math.Max(8, (int)GameState.ChainLightningPlusCooldown - weapon.Level * 2);
        }

        /// <summary>集火标记：标记最近敌人，全队对其伤害 +50%。</summary>
        static void FireFocusFire(GameState state, ref PlayerState player, ref WeaponSlot weapon, int playerIdx)
        {
            FInt range = _autoAimRange + FInt.FromInt(weapon.Level * 2);
            int target = state.FindNearestEnemy(player.PosX, player.PosZ, range);
            if (target >= 0)
            {
                state.FocusFireTarget = target;
                state.FocusFireTimer = GameState.FocusFireDuration + (uint)(weapon.Level * 20);
            }
            weapon.Cooldown = (uint)Math.Max(80, (int)GameState.FocusFireCooldown - weapon.Level * 10);
        }

        static void TickFocusFireTimer(GameState state)
        {
            if (state.FocusFireTimer > 0)
            {
                state.FocusFireTimer--;
                if (state.FocusFireTimer == 0) state.FocusFireTarget = -1;
            }
        }

        /// <summary>复活图腾：死亡检测由 CollisionSystem 处理；此处负责推进复活进度。</summary>
        static void TickRevivalTotem(GameState state, ref PlayerState player, ref WeaponSlot weapon, int playerIdx)
        {
            // 检测刚死亡的玩家并生成图腾（仅当该格位没有活跃图腾时）
            for (int t = 0; t < GameState.MaxRevivalTotems; t++)
            {
                ref var totem = ref state.RevivalTotems[t];
                if (!totem.Active) continue;

                // 任意存活玩家（非图腾归属者）靠近 → 推进进度
                FInt rSq = GameState.RevivalTotemRadius * GameState.RevivalTotemRadius;
                bool anyNear = false;
                for (int p = 0; p < GameState.MaxPlayers; p++)
                {
                    if (p == totem.OwnerSlot) continue;
                    ref var ally = ref state.Players[p];
                    if (!ally.IsActive || !ally.IsAlive) continue;
                    FInt dx = ally.PosX - totem.PosX, dz = ally.PosZ - totem.PosZ;
                    if (dx * dx + dz * dz < rSq) { anyNear = true; break; }
                }

                if (anyNear)
                {
                    totem.ReviveProgress++;
                    if (totem.ReviveProgress >= GameState.RevivalRequiredFrames)
                    {
                        // 复活
                        ref var deadPlayer = ref state.Players[totem.OwnerSlot];
                        deadPlayer.IsAlive = true;
                        deadPlayer.Hp = deadPlayer.MaxHp * GameState.RevivalHpPercent / 100;
                        deadPlayer.InvincibilityFrames = 60;
                        totem.Active = false;
                    }
                }
                else
                {
                    // 无人靠近则进度缓慢衰退（防止 AFK）
                    if (totem.ReviveProgress > 0) totem.ReviveProgress--;
                }
            }
            weapon.Cooldown = 1;
        }

        /// <summary>冰冻新星：周期性 AoE 减速范围内敌人 3 秒，被减速敌人受额外 25% 伤害。</summary>
        static void FireFrostNova(GameState state, ref PlayerState player, ref WeaponSlot weapon, int playerIdx)
        {
            FInt radius = GameState.FrostNovaBaseRadius + FInt.FromInt(weapon.Level);
            FInt radiusSq = radius * radius;

            for (int i = 0; i < GameState.MaxEnemies; i++)
            {
                ref var e = ref state.Enemies[i];
                if (!e.IsAlive) continue;
                FInt dx = e.PosX - player.PosX, dz = e.PosZ - player.PosZ;
                if (dx * dx + dz * dz <= radiusSq)
                    e.SlowFrames = GameState.FrostNovaSlowFrames + (uint)(weapon.Level * 10);
            }
            weapon.Cooldown = (uint)Math.Max(40, (int)GameState.FrostNovaBaseCooldown - weapon.Level * 4);
        }

        /// <summary>火焰轨迹：移动时在地面留下持续燃烧的火焰区域。</summary>
        static void FireTrailDrop(GameState state, ref PlayerState player, ref WeaponSlot weapon, int playerIdx)
        {
            // 只在玩家移动时放火焰（FacingX/Z 不为零视为移动）
            if (player.FacingX == FInt.Zero && player.FacingZ == FInt.Zero)
            { weapon.Cooldown = 4; return; }

            int slot = state.AllocProjectile();
            if (slot < 0) { weapon.Cooldown = 4; return; }
            ref var proj = ref state.Projectiles[slot];
            proj.IsAlive = true; proj.Type = ProjectileType.FireTrailPuddle;
            proj.PosX = player.PosX; proj.PosZ = player.PosZ;
            proj.DirX = FInt.Zero; proj.DirZ = FInt.Zero;
            proj.Radius = GameState.FireTrailRadius + FInt.FromInt(weapon.Level) * _03;
            proj.LifetimeFrames = GameState.FireTrailLifetime + (uint)(weapon.Level * 8);
            proj.OwnerPlayerId = playerIdx; proj.DamageTick = 0;
            weapon.Cooldown = (uint)Math.Max(6, (int)GameState.FireTrailCooldown - weapon.Level);
        }

        /// <summary>磁力场：将附近敌人拉向自己，方便队友 AoE 集中清怪。</summary>
        static void FireMagnetField(GameState state, ref PlayerState player, ref WeaponSlot weapon, int playerIdx)
        {
            FInt radius = GameState.MagnetFieldRadius + FInt.FromInt(weapon.Level);
            FInt radiusSq = radius * radius;
            FInt force = GameState.MagnetFieldForce * state.Dt;

            for (int i = 0; i < GameState.MaxEnemies; i++)
            {
                ref var e = ref state.Enemies[i];
                if (!e.IsAlive) continue;
                // TwinCore/SplitBoss 不受磁力影响（太重了）
                if (e.Type == EnemyType.TwinCore || e.Type == EnemyType.SplitBoss) continue;

                FInt dx = player.PosX - e.PosX, dz = player.PosZ - e.PosZ;
                FInt distSq = dx * dx + dz * dz;
                if (distSq > radiusSq || distSq < FInt.Epsilon) continue;

                FInt invDist = FInt.InvSqrt(distSq);
                e.PosX = e.PosX + dx * invDist * force;
                e.PosZ = e.PosZ + dz * invDist * force;
            }
            weapon.Cooldown = 1;
        }

        /// <summary>分裂弹：投射物命中敌人后分裂成 3 个小弹向外飞。</summary>
        static void FireSplitShot(GameState state, ref PlayerState player, ref WeaponSlot weapon, int playerIdx)
        {
            FInt aimX = player.FacingX, aimZ = player.FacingZ;
            int nearest = state.FindNearestEnemy(player.PosX, player.PosZ, _autoAimRange);
            if (nearest >= 0)
            {
                ref var tgt = ref state.Enemies[nearest];
                FInt dx = tgt.PosX - player.PosX, dz = tgt.PosZ - player.PosZ;
                FInt lenSq = FInt.LengthSqr(dx, dz);
                if (lenSq > FInt.Epsilon) { FInt inv = FInt.InvSqrt(lenSq); aimX = dx * inv; aimZ = dz * inv; }
            }

            int count = 1 + weapon.Level / 3;
            for (int k = 0; k < count; k++)
            {
                int slot = state.AllocProjectile();
                if (slot < 0) break;
                FInt spread = FInt.FromInt(k * 15);
                FInt cos = FInt.CosDeg(spread), sin = FInt.SinDeg(spread);
                ref var proj = ref state.Projectiles[slot];
                proj.IsAlive = true; proj.Type = ProjectileType.SplitShotMain;
                proj.PosX = player.PosX; proj.PosZ = player.PosZ;
                proj.DirX = aimX * cos - aimZ * sin;
                proj.DirZ = aimX * sin + aimZ * cos;
                proj.Radius = GameState.SplitShotRadius;
                proj.LifetimeFrames = GameState.SplitShotLifetime;
                proj.OwnerPlayerId = playerIdx; proj.DamageTick = 0;
            }
            weapon.Cooldown = (uint)Math.Max(5, (int)GameState.SplitShotBaseCooldown - weapon.Level);
        }

        /// <summary>分裂弹命中时生成 3 个碎片（从 CollisionSystem 调用）。</summary>
        public static void SpawnSplitShotSplinters(GameState state, FInt hitX, FInt hitZ, FInt inDirX, FInt inDirZ, int ownerPlayerId)
        {
            // 3 个方向：垂直左、垂直右、反射后方
            FInt perpX = -inDirZ, perpZ = inDirX;
            FInt dirs0X = perpX, dirs0Z = perpZ;
            FInt dirs1X = -perpX, dirs1Z = -perpZ;
            FInt dirs2X = -inDirX, dirs2Z = -inDirZ;

            SpawnSplinter(state, hitX, hitZ, dirs0X, dirs0Z, ownerPlayerId);
            SpawnSplinter(state, hitX, hitZ, dirs1X, dirs1Z, ownerPlayerId);
            SpawnSplinter(state, hitX, hitZ, dirs2X, dirs2Z, ownerPlayerId);
        }

        static void SpawnSplinter(GameState state, FInt x, FInt z, FInt dX, FInt dZ, int owner)
        {
            int slot = state.AllocProjectile();
            if (slot < 0) return;
            ref var p = ref state.Projectiles[slot];
            p.IsAlive = true; p.Type = ProjectileType.SplitShotSplinter;
            p.PosX = x; p.PosZ = z;
            FInt lenSq = dX * dX + dZ * dZ;
            if (lenSq > FInt.Epsilon) { FInt inv = FInt.InvSqrt(lenSq); p.DirX = dX * inv; p.DirZ = dZ * inv; }
            else { p.DirX = FInt.One; p.DirZ = FInt.Zero; }
            p.Radius = GameState.SplitShotRadius;
            p.LifetimeFrames = GameState.SplitShotSplinterLifetime;
            p.OwnerPlayerId = owner; p.DamageTick = 0;
        }

        // ==================== 投射物推进 ====================

        static void AdvanceProjectiles(GameState state)
        {
            FInt dt = state.Dt;
            for (int i = 0; i < GameState.MaxProjectiles; i++)
            {
                ref var p = ref state.Projectiles[i];
                if (!p.IsAlive) continue;

                switch (p.Type)
                {
                    case ProjectileType.Knife:
                    case ProjectileType.BoneShard:
                    case ProjectileType.SplitShotMain:
                    case ProjectileType.SplitShotSplinter:
                    {
                        FInt speed = p.Type == ProjectileType.BoneShard ? GameState.BoneShardSpeed
                            : p.Type == ProjectileType.SplitShotSplinter ? GameState.SplitShotSplinterSpeed
                            : p.Type == ProjectileType.SplitShotMain ? GameState.SplitShotSpeed
                            : GameState.KnifeSpeed;
                        p.PosX = p.PosX + p.DirX * speed * dt;
                        p.PosZ = p.PosZ + p.DirZ * speed * dt;
                        break;
                    }
                    case ProjectileType.HolyPuddle:
                    case ProjectileType.FireTrailPuddle:
                        p.DamageTick++;
                        break;
                }

                p.LifetimeFrames--;
                if (p.LifetimeFrames == 0) { p.IsAlive = false; continue; }
                if (p.PosX < -_arenaKillLimit || p.PosX > _arenaKillLimit ||
                    p.PosZ < -_arenaKillLimit || p.PosZ > _arenaKillLimit)
                    p.IsAlive = false;
            }
        }

        // ==================== 公共辅助 ====================

        public static void SpawnXpGem(GameState state, FInt x, FInt z, EnemyType type)
        {
            int gem = state.AllocGem();
            if (gem < 0) return;
            ref var g = ref state.Gems[gem];
            g.IsAlive = true; g.PosX = x; g.PosZ = z;
            g.Value = GameState.GetEnemyXpValue(type);
        }

        /// <summary>简单击杀（Weapon System 内置，不处理 Boss 宝石散落）。</summary>
        public static void KillEnemySimple(GameState state, int idx, int killerPlayerId)
        {
            ref var e = ref state.Enemies[idx];
            if (!e.IsAlive) return;
            e.IsAlive = false;
            SpawnXpGem(state, e.PosX, e.PosZ, e.Type);
            if (killerPlayerId >= 0 && killerPlayerId < GameState.MaxPlayers)
                state.Players[killerPlayerId].KillCount++;
        }

        /// <summary>
        /// TwinCore 感知的伤害函数：
        /// - 普通敌人：直接扣 HP
        /// - TwinCore：检查双核命中窗口
        /// </summary>
        public static void DamageTwinCoreAware(GameState state, int idx, int damage, int killerPlayerId)
        {
            ref var enemy = ref state.Enemies[idx];
            if (!enemy.IsAlive) return;

            if (enemy.Type == EnemyType.TwinCore && enemy.LinkedEnemyIdx >= 0)
            {
                ref var partner = ref state.Enemies[enemy.LinkedEnemyIdx];
                if (partner.IsAlive && partner.HitWindowTimer > 0)
                {
                    // 双核都已被标记 → 同时造成伤害
                    enemy.Hp -= damage;
                    partner.Hp -= damage;
                    enemy.HitWindowTimer = 0;
                    partner.HitWindowTimer = 0;
                    if (enemy.Hp <= 0) KillEnemySimple(state, idx, killerPlayerId);
                    if (state.Enemies[enemy.LinkedEnemyIdx].IsAlive && partner.Hp <= 0)
                        KillEnemySimple(state, enemy.LinkedEnemyIdx, killerPlayerId);
                }
                else
                {
                    // 只标记此核（等待另一核被击中）
                    enemy.HitWindowTimer = GameState.TwinCoreHitWindow;
                }
            }
            else
            {
                enemy.Hp -= damage;
                if (enemy.Hp <= 0) KillEnemySimple(state, idx, killerPlayerId);
            }
        }

        // ==================== 升级选项生成 ====================

        /// <summary>
        /// 确定性生成 4 个升级选项：优先当前武器（可升级），剩余从全池随机补。
        /// isMultiplayer=false 时只从单人池选（排除 6 个协作技能）。
        /// </summary>
        public static void GenerateUpgradeOptions(ref PlayerState p, ref uint rng, bool isMultiplayer = true)
        {
            var pool = isMultiplayer ? AllWeapons : SoloWeapons;
            byte[] opts = new byte[4];
            int count = 0;

            // 先塞入当前持有但未满级的武器（最多 2 个），排除不在当前池中的协作武器
            for (int ws = 0; ws < PlayerState.MaxWeaponSlots && count < 2; ws++)
            {
                var w = p.GetWeapon(ws);
                if (w.Type == WeaponType.None || w.Level >= GameState.MaxWeaponLevel) continue;
                bool inPool = false;
                for (int pi = 0; pi < pool.Length; pi++) if (pool[pi] == w.Type) { inPool = true; break; }
                if (inPool) opts[count++] = (byte)w.Type;
            }

            // 从当前池随机补足 4 个（避免重复）
            int attempts = 0;
            while (count < 4 && attempts < 50)
            {
                attempts++;
                int r = DeterministicRng.RangeInt(ref rng, 0, pool.Length);
                byte pick = (byte)pool[r];
                bool dup = false;
                for (int i = 0; i < count; i++) if (opts[i] == pick) { dup = true; break; }
                if (!dup) opts[count++] = pick;
            }

            // Fisher-Yates 打乱顺序
            for (int i = 3; i > 0; i--)
            {
                int j = DeterministicRng.RangeInt(ref rng, 0, i + 1);
                byte tmp = opts[i]; opts[i] = opts[j]; opts[j] = tmp;
            }

            p.SetUpgradeOpts(opts[0], opts[1], opts[2], opts[3]);
        }

        // ==================== 闪光辅助 ====================

        static void SpawnFlash(GameState state, FInt x, FInt z)
        {
            int flash = state.AllocFlash();
            if (flash >= 0) { state.Flashes[flash].PosX = x; state.Flashes[flash].PosZ = z; state.Flashes[flash].FramesLeft = 4; }
        }
    }
}
