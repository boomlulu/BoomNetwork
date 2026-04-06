// BoomNetwork VampireSurvivors Demo — Enemy AI (Fixed-Point)
// New Boss types: TwinCore (双核 Boss) + SplitBoss/SplitHalf (分裂 Boss)

namespace BoomNetwork.Samples.VampireSurvivors
{
    public static class EnemySystem
    {
        const uint RetargetInterval = 20;
        static readonly FInt _pointZeroOne = new FInt(10);
        static readonly FInt _06 = new FInt(614);
        static readonly FInt _04 = new FInt(409);

        public static void Tick(GameState state)
        {
            FInt dt = state.Dt;
            FInt arenaLimit = GameState.ArenaHalfSize + FInt.FromInt(3);

            for (int i = 0; i < GameState.MaxEnemies; i++)
            {
                ref var e = ref state.Enemies[i];
                if (!e.IsAlive) continue;

                // SlowFrames 每帧衰减
                if (e.SlowFrames > 0) e.SlowFrames--;

                // TwinCore 命中窗口衰减
                if (e.HitWindowTimer > 0 && e.Type == EnemyType.TwinCore)
                    e.HitWindowTimer--;

                bool needRetarget = e.TargetPlayerId < 0
                    || e.TargetPlayerId >= GameState.MaxPlayers
                    || !state.Players[e.TargetPlayerId].IsAlive
                    || (state.FrameNumber % RetargetInterval == 0);
                if (needRetarget)
                    e.TargetPlayerId = state.FindNearestPlayer(e.PosX, e.PosZ);
                if (e.TargetPlayerId < 0) continue;

                switch (e.Type)
                {
                    case EnemyType.Zombie:    TickZombie(ref e, state, dt); break;
                    case EnemyType.Bat:       TickBat(ref e, state, dt); break;
                    case EnemyType.SkeletonMage: TickMage(ref e, state, dt); break;
                    case EnemyType.Boss:      TickBoss(ref e, state, dt); break;
                    case EnemyType.TwinCore:  TickTwinCore(ref e, state, dt, i); break;
                    case EnemyType.SplitBoss: TickSplitBoss(ref e, state, dt, i); break;
                    case EnemyType.SplitHalf: TickSplitHalf(ref e, state, dt, i); break;
                }

                e.PosX = FInt.Clamp(e.PosX, -arenaLimit, arenaLimit);
                e.PosZ = FInt.Clamp(e.PosZ, -arenaLimit, arenaLimit);
            }
        }

        static void TickZombie(ref EnemyState e, GameState state, FInt dt)
        {
            ref var target = ref state.Players[e.TargetPlayerId];
            FInt dx = target.PosX - e.PosX, dz = target.PosZ - e.PosZ;
            FInt distSq = dx * dx + dz * dz;
            if (distSq < _pointZeroOne) return;
            FInt invDist = FInt.InvSqrt(distSq);
            FInt speed = e.SlowFrames > 0 ? GameState.ZombieSpeed / FInt.FromInt(2) : GameState.ZombieSpeed;
            FInt step = speed * dt;
            e.PosX = e.PosX + dx * invDist * step;
            e.PosZ = e.PosZ + dz * invDist * step;
        }

        static void TickBat(ref EnemyState e, GameState state, FInt dt)
        {
            if (e.BehaviorTimer == 0)
            {
                ref var target = ref state.Players[e.TargetPlayerId];
                FInt dx = target.PosX - e.PosX, dz = target.PosZ - e.PosZ;
                FInt distSq = dx * dx + dz * dz;
                if (distSq > _pointZeroOne)
                {
                    FInt invDist = FInt.InvSqrt(distSq);
                    FInt homeX = dx * invDist, homeZ = dz * invDist;
                    FInt randAngle = DeterministicRng.Range(ref state.RngState, -FInt.Pi, FInt.Pi);
                    FInt randX = FInt.Cos(randAngle), randZ = FInt.Sin(randAngle);
                    FInt rawX = homeX * _06 + randX * _04;
                    FInt rawZ = homeZ * _06 + randZ * _04;
                    FInt lenSq = FInt.LengthSqr(rawX, rawZ);
                    if (lenSq > FInt.Epsilon)
                    {
                        FInt invLen = FInt.InvSqrt(lenSq);
                        e.DirX = rawX * invLen;
                        e.DirZ = rawZ * invLen;
                    }
                }
                e.BehaviorTimer = GameState.BatDirChangeInterval;
            }
            else e.BehaviorTimer--;

            FInt speed = e.SlowFrames > 0 ? GameState.BatSpeed / FInt.FromInt(2) : GameState.BatSpeed;
            e.PosX = e.PosX + e.DirX * speed * dt;
            e.PosZ = e.PosZ + e.DirZ * speed * dt;
        }

        static void TickMage(ref EnemyState e, GameState state, FInt dt)
        {
            ref var target = ref state.Players[e.TargetPlayerId];
            FInt dx = target.PosX - e.PosX, dz = target.PosZ - e.PosZ;
            FInt distSq = dx * dx + dz * dz;
            FInt rangeSq = GameState.MageAttackRange * GameState.MageAttackRange;

            if (distSq > rangeSq && distSq > _pointZeroOne)
            {
                FInt invDist = FInt.InvSqrt(distSq);
                FInt speed = e.SlowFrames > 0 ? GameState.MageSpeed / FInt.FromInt(2) : GameState.MageSpeed;
                e.PosX = e.PosX + dx * invDist * speed * dt;
                e.PosZ = e.PosZ + dz * invDist * speed * dt;
            }

            if (e.BehaviorTimer == 0)
            {
                e.BehaviorTimer = GameState.MageFireCooldown;
                int slot = state.AllocProjectile();
                if (slot >= 0 && distSq > _pointZeroOne)
                {
                    FInt invDist = FInt.InvSqrt(distSq);
                    ref var proj = ref state.Projectiles[slot];
                    proj.IsAlive = true; proj.Type = ProjectileType.BoneShard;
                    proj.PosX = e.PosX; proj.PosZ = e.PosZ;
                    proj.DirX = dx * invDist; proj.DirZ = dz * invDist;
                    proj.Radius = GameState.BoneShardRadius;
                    proj.LifetimeFrames = GameState.BoneShardLifetime;
                    proj.OwnerPlayerId = -1; proj.DamageTick = 0;
                }
            }
            else e.BehaviorTimer--;
        }

        static void TickBoss(ref EnemyState e, GameState state, FInt dt)
        {
            ref var target = ref state.Players[e.TargetPlayerId];
            FInt dx = target.PosX - e.PosX, dz = target.PosZ - e.PosZ;
            FInt distSq = dx * dx + dz * dz;
            if (distSq < _pointZeroOne) return;
            FInt invDist = FInt.InvSqrt(distSq);
            e.PosX = e.PosX + dx * invDist * GameState.BossSpeed * dt;
            e.PosZ = e.PosZ + dz * invDist * GameState.BossSpeed * dt;
        }

        // ==================== 新 Boss AI ====================

        /// <summary>
        /// TwinCore：两个核绕同一中心缓慢旋转，朝最近玩家靠近。
        /// 只有两核在 40 帧窗口内同时被击中才会掉血。
        /// </summary>
        static void TickTwinCore(ref EnemyState e, GameState state, FInt dt, int selfIdx)
        {
            ref var target = ref state.Players[e.TargetPlayerId];
            FInt dx = target.PosX - e.PosX, dz = target.PosZ - e.PosZ;
            FInt distSq = dx * dx + dz * dz;
            if (distSq < _pointZeroOne) return;
            FInt invDist = FInt.InvSqrt(distSq);
            FInt speed = e.SlowFrames > 0 ? GameState.TwinCoreSpeed / FInt.FromInt(2) : GameState.TwinCoreSpeed;
            e.PosX = e.PosX + dx * invDist * speed * dt;
            e.PosZ = e.PosZ + dz * invDist * speed * dt;

            // HitWindowTimer 在 Tick() 顶部统一衰减
        }

        /// <summary>
        /// SplitBoss：倒计时结束后分裂成两个 SplitHalf。
        /// 两个半体各追一个不同玩家，必须在 60 帧内同时被击杀，否则存活者恢复满血并回到 SplitBoss 状态。
        /// </summary>
        static void TickSplitBoss(ref EnemyState e, GameState state, FInt dt, int selfIdx)
        {
            ref var target = ref state.Players[e.TargetPlayerId];
            FInt dx = target.PosX - e.PosX, dz = target.PosZ - e.PosZ;
            FInt distSq = dx * dx + dz * dz;
            if (distSq > _pointZeroOne)
            {
                FInt invDist = FInt.InvSqrt(distSq);
                FInt speed = e.SlowFrames > 0 ? GameState.SplitBossSpeed / FInt.FromInt(2) : GameState.SplitBossSpeed;
                e.PosX = e.PosX + dx * invDist * speed * dt;
                e.PosZ = e.PosZ + dz * invDist * speed * dt;
            }

            // 分裂计时
            if (e.BehaviorTimer > 0) { e.BehaviorTimer--; return; }

            // 分裂！生成两个 SplitHalf
            int slotA = state.AllocEnemy();
            int slotB = slotA >= 0 ? state.AllocEnemy() : -1;
            if (slotA < 0 || slotB < 0) { e.BehaviorTimer = GameState.SplitBossSplitTimer; return; }

            int halfHp = e.Hp / 2 + 1;

            // 找两个不同目标玩家
            int targetA = e.TargetPlayerId;
            int targetB = -1;
            for (int p = 0; p < GameState.MaxPlayers; p++)
            {
                if (p == targetA) continue;
                if (state.Players[p].IsActive && state.Players[p].IsAlive) { targetB = p; break; }
            }
            if (targetB < 0) targetB = targetA;

            ref var halfA = ref state.Enemies[slotA];
            halfA.IsAlive = true; halfA.Type = EnemyType.SplitHalf;
            halfA.PosX = e.PosX - GameState.TwinCoreOffset;
            halfA.PosZ = e.PosZ;
            halfA.Hp = halfHp; halfA.TargetPlayerId = targetA;
            halfA.BehaviorTimer = 0; halfA.DirX = FInt.Zero; halfA.DirZ = FInt.Zero;
            halfA.SlowFrames = 0; halfA.LinkedEnemyIdx = slotB; halfA.HitWindowTimer = 0;

            ref var halfB = ref state.Enemies[slotB];
            halfB.IsAlive = true; halfB.Type = EnemyType.SplitHalf;
            halfB.PosX = e.PosX + GameState.TwinCoreOffset;
            halfB.PosZ = e.PosZ;
            halfB.Hp = halfHp; halfB.TargetPlayerId = targetB;
            halfB.BehaviorTimer = 0; halfB.DirX = FInt.Zero; halfB.DirZ = FInt.Zero;
            halfB.SlowFrames = 0; halfB.LinkedEnemyIdx = slotA; halfB.HitWindowTimer = 0;

            // 原体消失
            e.IsAlive = false;
        }

        /// <summary>
        /// SplitHalf：追踪各自目标。
        /// HitWindowTimer > 0 表示另一半已死，倒计时内没死则原地转回 SplitBoss。
        /// </summary>
        static void TickSplitHalf(ref EnemyState e, GameState state, FInt dt, int selfIdx)
        {
            // 死亡确认窗口倒计时
            if (e.HitWindowTimer > 0)
            {
                e.HitWindowTimer--;
                if (e.HitWindowTimer == 0)
                {
                    // 存活超时 → 转回 SplitBoss
                    e.Type = EnemyType.SplitBoss;
                    e.Hp = GameState.SplitBossHp;
                    e.BehaviorTimer = GameState.SplitBossSplitTimer;
                    e.LinkedEnemyIdx = -1;
                    return;
                }
            }

            ref var target = ref state.Players[e.TargetPlayerId];
            FInt dx = target.PosX - e.PosX, dz = target.PosZ - e.PosZ;
            FInt distSq = dx * dx + dz * dz;
            if (distSq < _pointZeroOne) return;
            FInt invDist = FInt.InvSqrt(distSq);
            FInt speed = e.SlowFrames > 0 ? GameState.SplitHalfSpeed / FInt.FromInt(2) : GameState.SplitHalfSpeed;
            e.PosX = e.PosX + dx * invDist * speed * dt;
            e.PosZ = e.PosZ + dz * invDist * speed * dt;
        }
    }
}
