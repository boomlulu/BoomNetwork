// BoomNetwork VampireSurvivors Demo — Enemy AI (Fixed-Point)

namespace BoomNetwork.Samples.VampireSurvivors
{
    public static class EnemySystem
    {
        const uint RetargetInterval = 20;
        static readonly FInt _pointZeroOne = new FInt(10);  // 0.01 * 1024 = 10
        static readonly FInt _06 = new FInt(614);           // 0.6  * 1024 = 614
        static readonly FInt _04 = new FInt(409);           // 0.4  * 1024 = 409

        public static void Tick(GameState state)
        {
            FInt dt = state.Dt;
            FInt arenaLimit = GameState.ArenaHalfSize + FInt.FromInt(3);

            for (int i = 0; i < GameState.MaxEnemies; i++)
            {
                ref var e = ref state.Enemies[i];
                if (!e.IsAlive) continue;

                bool needRetarget = e.TargetPlayerId < 0
                    || e.TargetPlayerId >= GameState.MaxPlayers
                    || !state.Players[e.TargetPlayerId].IsAlive
                    || (state.FrameNumber % RetargetInterval == 0);
                if (needRetarget)
                    e.TargetPlayerId = state.FindNearestPlayer(e.PosX, e.PosZ);
                if (e.TargetPlayerId < 0) continue;

                switch (e.Type)
                {
                    case EnemyType.Zombie: TickZombie(ref e, state, dt); break;
                    case EnemyType.Bat: TickBat(ref e, state, dt); break;
                    case EnemyType.SkeletonMage: TickMage(ref e, state, dt); break;
                    case EnemyType.Boss: TickBoss(ref e, state, dt); break;
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
            FInt step = GameState.ZombieSpeed * dt;
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

            e.PosX = e.PosX + e.DirX * GameState.BatSpeed * dt;
            e.PosZ = e.PosZ + e.DirZ * GameState.BatSpeed * dt;
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
                e.PosX = e.PosX + dx * invDist * GameState.MageSpeed * dt;
                e.PosZ = e.PosZ + dz * invDist * GameState.MageSpeed * dt;
            }

            if (e.BehaviorTimer == 0)
            {
                e.BehaviorTimer = GameState.MageFireCooldown;
                int slot = state.AllocProjectile();
                if (slot >= 0 && distSq > _pointZeroOne)
                {
                    FInt invDist = FInt.InvSqrt(distSq);
                    ref var proj = ref state.Projectiles[slot];
                    proj.IsAlive = true;
                    proj.Type = ProjectileType.BoneShard;
                    proj.PosX = e.PosX; proj.PosZ = e.PosZ;
                    proj.DirX = dx * invDist; proj.DirZ = dz * invDist;
                    proj.Radius = GameState.BoneShardRadius;
                    proj.LifetimeFrames = GameState.BoneShardLifetime;
                    proj.OwnerPlayerId = -1;
                    proj.DamageTick = 0;
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
    }
}
