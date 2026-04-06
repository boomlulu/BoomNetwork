// BoomNetwork VampireSurvivors Demo — Game State (Fixed-Point)
//
// All gameplay values use FInt (22.10 fixed-point).
// No floating-point in simulation — bit-level deterministic across platforms.

namespace BoomNetwork.Samples.VampireSurvivors
{
    public enum EnemyType : byte
    {
        Zombie = 0, Bat = 1, SkeletonMage = 2, Boss = 3,
        TwinCore = 4, SplitBoss = 5, SplitHalf = 6
    }

    public enum WeaponType : byte
    {
        None = 0,
        Knife = 1, Orb = 2, Lightning = 3, HolyWater = 4,
        LinkBeam = 5, HealAura = 6, ShieldWall = 7, ChainLightningPlus = 8,
        FocusFire = 9, RevivalTotem = 10,
        FrostNova = 11, FireTrail = 12, MagnetField = 13, SplitShot = 14,
    }

    public enum ProjectileType : byte
    {
        Knife = 0, BoneShard = 1, HolyPuddle = 2,
        FireTrailPuddle = 3, SplitShotMain = 4, SplitShotSplinter = 5
    }

    public struct WeaponSlot
    {
        public WeaponType Type;
        public int Level;
        public uint Cooldown;
    }

    public struct OrbState
    {
        public bool Active;
        public FInt AngleDeg;
    }

    public struct LightningFlash
    {
        public FInt PosX, PosZ;
        public uint FramesLeft;
    }

    public struct RevivalTotemState
    {
        public bool Active;
        public FInt PosX, PosZ;
        public int OwnerSlot;
        public uint ReviveProgress;
    }

    public struct PlayerState
    {
        public bool IsActive, IsAlive;
        public FInt PosX, PosZ;
        public FInt FacingX, FacingZ;
        public int Hp, MaxHp;
        public int Xp, Level, XpToNextLevel;
        public uint InvincibilityFrames;
        public int KillCount;

        public const int MaxWeaponSlots = 4;
        public WeaponSlot Weapon0, Weapon1, Weapon2, Weapon3;

        public bool PendingLevelUp;
        public byte UpgradeChoice;
        public byte UpgradeOpt0, UpgradeOpt1, UpgradeOpt2, UpgradeOpt3;

        public const int MaxOrbs = 5;
        public OrbState Orb0, Orb1, Orb2, Orb3, Orb4;

        public WeaponSlot GetWeapon(int i)
        {
            switch (i) { case 0: return Weapon0; case 1: return Weapon1; case 2: return Weapon2; default: return Weapon3; }
        }
        public void SetWeapon(int i, WeaponSlot v)
        {
            switch (i) { case 0: Weapon0 = v; break; case 1: Weapon1 = v; break; case 2: Weapon2 = v; break; default: Weapon3 = v; break; }
        }
        public OrbState GetOrb(int i)
        {
            switch (i) { case 0: return Orb0; case 1: return Orb1; case 2: return Orb2; case 3: return Orb3; default: return Orb4; }
        }
        public void SetOrb(int i, OrbState v)
        {
            switch (i) { case 0: Orb0 = v; break; case 1: Orb1 = v; break; case 2: Orb2 = v; break; case 3: Orb3 = v; break; default: Orb4 = v; break; }
        }
        public byte GetUpgradeOpt(int i)
        {
            switch (i) { case 0: return UpgradeOpt0; case 1: return UpgradeOpt1; case 2: return UpgradeOpt2; default: return UpgradeOpt3; }
        }
        public void SetUpgradeOpts(byte o0, byte o1, byte o2, byte o3)
        { UpgradeOpt0 = o0; UpgradeOpt1 = o1; UpgradeOpt2 = o2; UpgradeOpt3 = o3; }

        public int FindWeaponSlot(WeaponType type)
        {
            if (Weapon0.Type == type) return 0; if (Weapon1.Type == type) return 1;
            if (Weapon2.Type == type) return 2; if (Weapon3.Type == type) return 3;
            return -1;
        }
        public int FindEmptyWeaponSlot()
        {
            if (Weapon0.Type == WeaponType.None) return 0; if (Weapon1.Type == WeaponType.None) return 1;
            if (Weapon2.Type == WeaponType.None) return 2; if (Weapon3.Type == WeaponType.None) return 3;
            return -1;
        }
    }

    public struct EnemyState
    {
        public bool IsAlive;
        public EnemyType Type;
        public FInt PosX, PosZ;
        public FInt DirX, DirZ;
        public int Hp;
        public int TargetPlayerId;
        public uint BehaviorTimer;
        // 新增字段
        public uint SlowFrames;      // FrostNova 减速剩余帧
        public int LinkedEnemyIdx;   // TwinCore/SplitBoss 关联索引 (-1=无)
        public uint HitWindowTimer;  // TwinCore 双核命中窗口 / SplitHalf 死亡窗口
    }

    public struct ProjectileState
    {
        public bool IsAlive;
        public ProjectileType Type;
        public FInt PosX, PosZ;
        public FInt DirX, DirZ;
        public FInt Radius;
        public uint LifetimeFrames;
        public int OwnerPlayerId;
        public uint DamageTick;
    }

    public struct XpGemState
    {
        public bool IsAlive;
        public bool Attracting;
        public FInt PosX, PosZ;
        public int Value;
    }

    public class GameState
    {
        public const int MaxPlayers = 4;
        public const int MaxEnemies = 2048;
        public const int MaxProjectiles = 1024;
        public const int MaxGems = 2048;
        public const int MaxLightningFlashes = 128;
        public const int MaxRevivalTotems = MaxPlayers;

        public static readonly FInt ArenaHalfSize = FInt.FromInt(50);

        public static readonly FInt PlayerSpeed = new FInt(10240);
        public static readonly FInt PlayerRadius = new FInt(409);
        public const int PlayerMaxHp = 500;
        public const int PlayerBaseXpToLevel = 8;
        public const uint InvincibilityDuration = 60;

        public static readonly FInt ZombieSpeed = new FInt(2252);
        public static readonly FInt ZombieRadius = new FInt(409);
        public const int ZombieHp = 1;
        public const int ZombieDamage = 5;
        public const int ZombieXpValue = 1;

        public static readonly FInt BatSpeed = new FInt(4096);
        public static readonly FInt BatRadius = new FInt(307);
        public const int BatHp = 1;
        public const int BatDamage = 3;
        public const int BatXpValue = 2;
        public const uint BatDirChangeInterval = 8;

        public static readonly FInt MageSpeed = new FInt(1536);
        public static readonly FInt MageRadius = new FInt(409);
        public const int MageHp = 3;
        public const int MageDamage = 5;
        public const int MageXpValue = 5;
        public static readonly FInt MageAttackRange = FInt.FromInt(12);
        public const uint MageFireCooldown = 60;
        public static readonly FInt BoneShardSpeed = FInt.FromInt(5);
        public const uint BoneShardLifetime = 25;
        public static readonly FInt BoneShardRadius = new FInt(204);
        public const int BoneShardDamage = 8;

        public static readonly FInt KnifeSpeed = FInt.FromInt(14);
        public static readonly FInt KnifeRadius = new FInt(204);
        public const int KnifeDamage = 15;
        public const uint KnifeBaseCooldown = 6;
        public const uint KnifeLifetimeFrames = 80;

        public static readonly FInt OrbOrbitRadius = new FInt(1843);
        public static readonly FInt OrbAngularSpeed = FInt.FromInt(220);
        public static readonly FInt OrbHitRadius = new FInt(512);
        public const int OrbDamage = 25;

        public static readonly FInt LightningRange = FInt.FromInt(16);
        public const int LightningDamage = 30;
        public const uint LightningBaseCooldown = 20;
        public const int LightningBaseChains = 4;

        public static readonly FInt HolyWaterBaseRadius = FInt.FromInt(5);
        public const uint HolyWaterBaseCooldown = 50;
        public const uint HolyWaterLifetime = 100;
        public const int HolyWaterDamage = 18;
        public const uint HolyWaterDamageTick = 6;

        // Old Boss (kept for completeness)
        public static readonly FInt BossSpeed = new FInt(1024);
        public static readonly FInt BossRadius = new FInt(1536);
        public const int BossHp = 200;
        public const int BossDamage = 25;
        public const int BossXpValue = 50;
        public const int BossWaveInterval = 5;
        public const int BossGemCount = 8;

        // LinkBeam
        public const int LinkBeamDamage = 1;
        public static readonly FInt LinkBeamWidth = new FInt(614);
        public static readonly FInt LinkBeamCloseDist = FInt.FromInt(5);

        // HealAura
        public const int HealAuraAmount = 2;
        public static readonly FInt HealAuraRadius = FInt.FromInt(6);
        public const uint HealAuraBaseCooldown = 60;

        // ShieldWall
        public const int ShieldWallDamage = 2;
        public static readonly FInt ShieldWallWidth = new FInt(819);

        // ChainLightningPlus
        public const int ChainLightningPlusDamage = 6;
        public static readonly FInt ChainLightningPlusTeammateRadius = FInt.FromInt(4);
        public const uint ChainLightningPlusCooldown = 18;

        // FocusFire
        public const uint FocusFireDuration = 100;
        public const uint FocusFireCooldown = 150;

        // RevivalTotem
        public static readonly FInt RevivalTotemRadius = new FInt(1536);
        public const uint RevivalRequiredFrames = 60;
        public const int RevivalHpPercent = 50;

        // FrostNova
        public static readonly FInt FrostNovaBaseRadius = FInt.FromInt(8);
        public const uint FrostNovaSlowFrames = 60;
        public const uint FrostNovaBaseCooldown = 60;

        // FireTrail
        public static readonly FInt FireTrailRadius = new FInt(819);
        public const uint FireTrailLifetime = 50;
        public const int FireTrailDamage = 1;
        public const uint FireTrailDamageTick = 6;
        public const uint FireTrailCooldown = 12;

        // MagnetField
        public static readonly FInt MagnetFieldRadius = FInt.FromInt(15);
        public static readonly FInt MagnetFieldForce = new FInt(2048);

        // SplitShot
        public static readonly FInt SplitShotRadius = new FInt(204);
        public const int SplitShotDamage = 2;
        public static readonly FInt SplitShotSpeed = FInt.FromInt(12);
        public const uint SplitShotLifetime = 30;
        public static readonly FInt SplitShotSplinterSpeed = FInt.FromInt(10);
        public const uint SplitShotSplinterLifetime = 20;
        public const uint SplitShotBaseCooldown = 8;

        // TwinCore
        public static readonly FInt TwinCoreRadius = new FInt(614);
        public static readonly FInt TwinCoreOffset = new FInt(1843);
        public const int TwinCoreHpPerCore = 400;
        public const int TwinCoreDamage = 20;
        public const int TwinCoreXpValue = 80;
        public const uint TwinCoreHitWindow = 40;
        public static readonly FInt TwinCoreSpeed = new FInt(1024);
        public const int TwinCoreBossGemCount = 12;

        // SplitBoss
        public static readonly FInt SplitBossRadius = new FInt(1843);
        public const int SplitBossHp = 800;
        public const int SplitBossDamage = 30;
        public const int SplitBossXpValue = 100;
        public static readonly FInt SplitBossSpeed = new FInt(1229);
        public const uint SplitBossSplitTimer = 200;
        public const uint SplitHalfDeathWindow = 60;
        public const int SplitBossBossGemCount = 16;

        // SplitHalf
        public static readonly FInt SplitHalfRadius = new FInt(1024);
        public const int SplitHalfDamage = 20;
        public static readonly FInt SplitHalfSpeed = new FInt(1536);

        public const int MaxWeaponLevel = 10;

        public static readonly FInt XpPickupRadius = new FInt(1536);
        public static readonly FInt XpMagnetRadius = FInt.FromInt(12);
        public static readonly FInt XpMagnetBaseSpeed = FInt.FromInt(8);
        public static readonly FInt XpMagnetMaxSpeed = FInt.FromInt(25);

        // State
        public uint FrameNumber;
        public uint RngState;
        public int WaveNumber;
        public uint WaveSpawnTimer, WaveSpawnRemaining;
        public FInt Dt;

        public PlayerState[] Players = new PlayerState[MaxPlayers];
        public EnemyState[] Enemies = new EnemyState[MaxEnemies];
        public ProjectileState[] Projectiles = new ProjectileState[MaxProjectiles];
        public XpGemState[] Gems = new XpGemState[MaxGems];
        public LightningFlash[] Flashes = new LightningFlash[MaxLightningFlashes];

        public int FocusFireTarget = -1;
        public uint FocusFireTimer;
        public RevivalTotemState[] RevivalTotems = new RevivalTotemState[MaxRevivalTotems];

        public void InitPlayer(int slot)
        {
            ref var p = ref Players[slot];
            p.IsActive = true; p.IsAlive = true;
            FInt angle = new FInt(slot * 1608);
            p.PosX = FInt.Cos(angle) * 2; p.PosZ = FInt.Sin(angle) * 2;
            p.FacingX = FInt.Zero; p.FacingZ = FInt.One;
            p.Hp = PlayerMaxHp; p.MaxHp = PlayerMaxHp;
            p.Xp = 0; p.Level = 1; p.XpToNextLevel = PlayerBaseXpToLevel;
            p.InvincibilityFrames = 0; p.KillCount = 0;
            p.PendingLevelUp = false; p.UpgradeChoice = 0;
            p.SetUpgradeOpts(0, 0, 0, 0);
            p.Weapon0 = new WeaponSlot { Type = WeaponType.Knife, Level = 1, Cooldown = 0 };
            p.Weapon1 = default; p.Weapon2 = default; p.Weapon3 = default;
            for (int i = 0; i < PlayerState.MaxOrbs; i++) p.SetOrb(i, default);
        }

        public int AllocEnemy() { for (int i = 0; i < MaxEnemies; i++) if (!Enemies[i].IsAlive) return i; return -1; }
        public int AllocProjectile() { for (int i = 0; i < MaxProjectiles; i++) if (!Projectiles[i].IsAlive) return i; return -1; }
        public int AllocGem() { for (int i = 0; i < MaxGems; i++) if (!Gems[i].IsAlive) return i; return -1; }
        public int AllocFlash() { for (int i = 0; i < MaxLightningFlashes; i++) if (Flashes[i].FramesLeft == 0) return i; return -1; }

        public int FindNearestPlayer(FInt x, FInt z)
        {
            int best = -1; FInt bestDist = FInt.MaxValue;
            for (int i = 0; i < MaxPlayers; i++)
            {
                ref var p = ref Players[i];
                if (!p.IsActive || !p.IsAlive) continue;
                FInt dx = p.PosX - x, dz = p.PosZ - z;
                FInt d = dx * dx + dz * dz;
                if (d < bestDist) { bestDist = d; best = i; }
            }
            return best;
        }

        public int FindNearestEnemy(FInt x, FInt z, FInt maxDist)
        {
            FInt maxDistSq = maxDist * maxDist;
            int best = -1; FInt bestDist = FInt.MaxValue;
            for (int i = 0; i < MaxEnemies; i++)
            {
                ref var e = ref Enemies[i];
                if (!e.IsAlive) continue;
                FInt dx = e.PosX - x, dz = e.PosZ - z;
                FInt d = dx * dx + dz * dz;
                if (d < maxDistSq && d < bestDist) { bestDist = d; best = i; }
            }
            return best;
        }

        public bool HasAlivePlayers()
        {
            for (int i = 0; i < MaxPlayers; i++)
                if (Players[i].IsActive && Players[i].IsAlive) return true;
            return false;
        }

        /// <summary>应用集火标记 (+50%) 和冰冻减速 (+25%) 倍率。</summary>
        public int ScaleDamage(int rawDamage, int enemyIdx)
        {
            int d = rawDamage;
            if (FocusFireTarget == enemyIdx && FocusFireTimer > 0) d = d * 3 / 2;
            if (enemyIdx >= 0 && enemyIdx < MaxEnemies && Enemies[enemyIdx].SlowFrames > 0) d = d * 5 / 4;
            return d;
        }

        public static int GetEnemyXpValue(EnemyType t)
        {
            switch (t)
            {
                case EnemyType.Bat: return BatXpValue;
                case EnemyType.SkeletonMage: return MageXpValue;
                case EnemyType.Boss: return BossXpValue;
                case EnemyType.TwinCore: return TwinCoreXpValue;
                case EnemyType.SplitBoss: return SplitBossXpValue;
                case EnemyType.SplitHalf: return SplitBossXpValue / 2;
                default: return ZombieXpValue;
            }
        }
        public static FInt GetEnemyRadius(EnemyType t)
        {
            switch (t)
            {
                case EnemyType.Bat: return BatRadius;
                case EnemyType.SkeletonMage: return MageRadius;
                case EnemyType.Boss: return BossRadius;
                case EnemyType.TwinCore: return TwinCoreRadius;
                case EnemyType.SplitBoss: return SplitBossRadius;
                case EnemyType.SplitHalf: return SplitHalfRadius;
                default: return ZombieRadius;
            }
        }
        public static int GetEnemyDamage(EnemyType t)
        {
            switch (t)
            {
                case EnemyType.Bat: return BatDamage;
                case EnemyType.SkeletonMage: return MageDamage;
                case EnemyType.Boss: return BossDamage;
                case EnemyType.TwinCore: return TwinCoreDamage;
                case EnemyType.SplitBoss: return SplitBossDamage;
                case EnemyType.SplitHalf: return SplitHalfDamage;
                default: return ZombieDamage;
            }
        }

        public uint ComputeHash()
        {
            uint h = 2166136261u;
            h = Fnv(h, FrameNumber); h = Fnv(h, RngState); h = Fnv(h, (uint)Dt.Raw);
            h = Fnv(h, (uint)WaveNumber); h = Fnv(h, WaveSpawnTimer); h = Fnv(h, WaveSpawnRemaining);
            h = Fnv(h, (uint)FocusFireTarget); h = Fnv(h, FocusFireTimer);

            for (int i = 0; i < MaxPlayers; i++)
            {
                ref var p = ref Players[i];
                h = Fnv(h, p.IsActive ? 1u : 0u); h = Fnv(h, p.IsAlive ? 1u : 0u);
                h = Fnv(h, (uint)p.PosX.Raw); h = Fnv(h, (uint)p.PosZ.Raw);
                h = Fnv(h, (uint)p.FacingX.Raw); h = Fnv(h, (uint)p.FacingZ.Raw);
                h = Fnv(h, (uint)p.Hp); h = Fnv(h, (uint)p.MaxHp);
                h = Fnv(h, (uint)p.Xp); h = Fnv(h, (uint)p.Level); h = Fnv(h, (uint)p.XpToNextLevel);
                h = Fnv(h, p.InvincibilityFrames); h = Fnv(h, (uint)p.KillCount);
                h = Fnv(h, p.PendingLevelUp ? 1u : 0u); h = Fnv(h, (uint)p.UpgradeChoice);
                h = Fnv(h, p.UpgradeOpt0); h = Fnv(h, p.UpgradeOpt1);
                h = Fnv(h, p.UpgradeOpt2); h = Fnv(h, p.UpgradeOpt3);
                for (int ws = 0; ws < PlayerState.MaxWeaponSlots; ws++)
                {
                    var w = p.GetWeapon(ws);
                    h = Fnv(h, (uint)w.Type); h = Fnv(h, (uint)w.Level); h = Fnv(h, w.Cooldown);
                }
                for (int o = 0; o < PlayerState.MaxOrbs; o++)
                {
                    var orb = p.GetOrb(o);
                    h = Fnv(h, orb.Active ? 1u : 0u); h = Fnv(h, (uint)orb.AngleDeg.Raw);
                }
            }

            for (int i = 0; i < MaxEnemies; i++)
            {
                ref var e = ref Enemies[i];
                if (!e.IsAlive) continue;
                h = Fnv(h, (uint)i); h = Fnv(h, (uint)e.Type);
                h = Fnv(h, (uint)e.PosX.Raw); h = Fnv(h, (uint)e.PosZ.Raw);
                h = Fnv(h, (uint)e.DirX.Raw); h = Fnv(h, (uint)e.DirZ.Raw);
                h = Fnv(h, (uint)e.Hp); h = Fnv(h, (uint)e.TargetPlayerId); h = Fnv(h, e.BehaviorTimer);
                h = Fnv(h, e.SlowFrames); h = Fnv(h, (uint)e.LinkedEnemyIdx); h = Fnv(h, e.HitWindowTimer);
            }

            for (int i = 0; i < MaxProjectiles; i++)
            {
                ref var p = ref Projectiles[i];
                if (!p.IsAlive) continue;
                h = Fnv(h, (uint)i); h = Fnv(h, (uint)p.Type);
                h = Fnv(h, (uint)p.PosX.Raw); h = Fnv(h, (uint)p.PosZ.Raw);
                h = Fnv(h, (uint)p.DirX.Raw); h = Fnv(h, (uint)p.DirZ.Raw);
                h = Fnv(h, (uint)p.Radius.Raw); h = Fnv(h, p.LifetimeFrames);
                h = Fnv(h, (uint)p.OwnerPlayerId); h = Fnv(h, p.DamageTick);
            }

            for (int i = 0; i < MaxGems; i++)
            {
                ref var g = ref Gems[i];
                if (!g.IsAlive) continue;
                h = Fnv(h, (uint)i); h = Fnv(h, g.Attracting ? 1u : 0u);
                h = Fnv(h, (uint)g.PosX.Raw); h = Fnv(h, (uint)g.PosZ.Raw); h = Fnv(h, (uint)g.Value);
            }

            for (int i = 0; i < MaxLightningFlashes; i++)
            {
                ref var f = ref Flashes[i];
                if (f.FramesLeft == 0) continue;
                h = Fnv(h, (uint)i); h = Fnv(h, (uint)f.PosX.Raw); h = Fnv(h, (uint)f.PosZ.Raw); h = Fnv(h, f.FramesLeft);
            }

            for (int i = 0; i < MaxRevivalTotems; i++)
            {
                ref var t = ref RevivalTotems[i];
                h = Fnv(h, t.Active ? 1u : 0u);
                if (!t.Active) continue;
                h = Fnv(h, (uint)t.PosX.Raw); h = Fnv(h, (uint)t.PosZ.Raw);
                h = Fnv(h, (uint)t.OwnerSlot); h = Fnv(h, t.ReviveProgress);
            }

            return h;
        }

        static uint Fnv(uint hash, uint value)
        {
            hash ^= value & 0xFF; hash *= 16777619u;
            hash ^= (value >> 8) & 0xFF; hash *= 16777619u;
            hash ^= (value >> 16) & 0xFF; hash *= 16777619u;
            hash ^= (value >> 24) & 0xFF; hash *= 16777619u;
            return hash;
        }
    }
}
