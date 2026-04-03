// BoomNetwork TowerDefense Demo — Game State (Fixed-Point)
//
// 20×20 grid. Enemies pour in from all four edges. Defend the 2×2 center base.
//
// ECONOMY — Option D (Layered):
//   Personal Gold  : earned from kills (75% to the tower owner). Used for the 5 individual towers.
//   Shared Gold    : 25% of every kill flows here. Used for 2 powerful team towers.
//   TeamOwner(-1)  : towers placed with shared gold; reward flows back to shared.

namespace BoomNetwork.Samples.TowerDefense
{
    public enum TowerType : byte
    {
        None = 0,
        // ── Individual towers (cost personal gold) ──────────────────
        Arrow   = 1,
        Cannon  = 2,
        Magic   = 3,
        Ice     = 4,
        Sniper  = 5,
        // ── Team towers (cost shared gold) ──────────────────────────
        Fortress = 6,   // 大范围重炮，AoE 伤害
        Storm    = 7,   // 连锁闪电，打 2-4 个目标
    }

    public enum EnemyType : byte { Basic = 0, Fast = 1, Tank = 2, Armored = 3, Elite = 4 }

    public struct Tower
    {
        public TowerType Type;
        public int CooldownFrames;
        public int Level;           // 1-3
        public int OwnerId;         // 0-3 = player slot, TeamOwner(-1) = team tower
    }

    public struct Enemy
    {
        public bool IsAlive;
        public EnemyType Type;
        public FInt PosX, PosZ;
        public int Hp;
        public int SlowFrames;
    }

    public struct WaveState
    {
        public int WaveNumber;
        public int SpawnRemaining;
        public int InterWaveTimer;
        public bool AllWavesDone;
    }

    public class GameState
    {
        // ==================== Map ====================
        public const int GridW = 20;
        public const int GridH = 20;
        public const int GridSize = GridW * GridH;

        public const int BaseCX = 9;
        public const int BaseCY = 9;

        // ==================== Capacities ====================
        public const int MaxPlayers = 4;
        public const int MaxEnemies = 512;
        public const int MaxWaves   = 10;

        // ==================== Economy ====================
        public const int TeamOwner         = -1;   // OwnerId for team towers
        public const int InitialPersonalGold = 100; // per player at game start
        public const int InitialSharedGold   = 50;  // team treasury at start
        // Kill reward split: 75% → tower owner personal, 25% → shared
        public const int PersonalRewardPct = 75;

        // ==================== Individual Tower params ====================
        public const int ArrowCost   = 50;
        public const int CannonCost  = 100;
        public const int MagicCost   = 80;
        public const int IceCost     = 90;
        public const int SniperCost  = 200;

        public const int ArrowCooldown  = 15;
        public const int CannonCooldown = 35;
        public const int MagicCooldown  = 30;
        public const int IceCooldown    = 25;
        public const int SniperCooldown = 60;

        public static readonly FInt ArrowRange   = FInt.FromInt(3);
        public static readonly FInt CannonRange  = FInt.FromInt(8);
        public static readonly FInt CannonMinRange = FInt.FromInt(3);
        public static readonly FInt MagicRange   = FInt.FromInt(4);
        public static readonly FInt IceRange     = FInt.FromInt(4);
        public static readonly FInt SniperRange  = FInt.FromInt(12);

        public static readonly FInt CannonAoeRadius = FInt.FromFloat(1.8f);
        public static readonly FInt IceAoeRadius    = FInt.FromFloat(2.0f);

        public const int ArrowDamage     = 1;
        public const int CannonDamage    = 3;
        public const int MagicDamage     = 1;
        public const int MagicSlowFrames = 60;
        public const int IceSlowFrames   = 90;

        // ==================== Team Tower params ====================
        // 堡垒炮：重型 AoE，伤高、射程中等
        public const int FortressCost     = 300;
        public const int FortressCooldown = 45;
        public static readonly FInt FortressRange     = FInt.FromInt(6);
        public static readonly FInt FortressAoeRadius = FInt.FromFloat(2.5f);
        // Damage: level 1/2/3 = 8/14/20

        // 风暴塔：连锁闪电，打多目标
        public const int StormCost     = 250;
        public const int StormCooldown = 18;
        public static readonly FInt StormRange = FInt.FromInt(5);
        // Chain targets: level 1/2/3 = 2/3/4
        // Damage: level 1/2/3 = 3/5/7

        // ==================== Enemy params ====================
        public static readonly FInt BasicSpeed   = FInt.FromFloat(0.020f);
        public static readonly FInt FastSpeed    = FInt.FromFloat(0.040f);
        public static readonly FInt TankSpeed    = FInt.FromFloat(0.010f);
        public static readonly FInt ArmoredSpeed = FInt.FromFloat(0.012f);
        public static readonly FInt EliteSpeed   = FInt.FromFloat(0.055f);

        public const int BasicHp    = 3;
        public const int FastHp     = 2;
        public const int TankHp     = 10;
        public const int ArmoredHp  = 15;
        public const int EliteHp    = 6;

        public const int BasicDamage   = 1;
        public const int FastDamage    = 1;
        public const int TankDamage    = 2;
        public const int ArmoredDamage = 3;
        public const int EliteDamage   = 2;

        public const int BasicReward   = 10;
        public const int FastReward    = 15;
        public const int TankReward    = 30;
        public const int ArmoredReward = 35;
        public const int EliteReward   = 30;

        // ==================== Wave timing ====================
        public const int InterWaveFrames = 300;

        // ==================== State ====================
        public uint FrameNumber;
        public uint RngState;
        public int BaseHp = 3;
        // Speed control (synchronized via SpeedAction input)
        public byte SpeedMode    = 1; // 0=0.25x 1=1x 2=2x 3=3x (mirrors TDInput.SpeedXxx)
        public byte SpeedCounter;     // sub-frame counter for 0.25x mode

        // Layered economy
        public int[] PlayerGold = new int[MaxPlayers]; // personal, hashed
        public int SharedGold;                         // team, hashed

        public Tower[] Grid    = new Tower[GridSize];
        public Enemy[] Enemies = new Enemy[MaxEnemies];
        public WaveState Wave;

        // ==================== Static helpers ====================

        public static bool IsTeamTower(TowerType t) => t == TowerType.Fortress || t == TowerType.Storm;

        public static int CellIndex(int x, int y) => y * GridW + x;

        public static bool IsBase(int x, int y)
            => x >= BaseCX && x < BaseCX + 2 && y >= BaseCY && y < BaseCY + 2;

        public static bool IsInBounds(int x, int y)
            => x >= 0 && x < GridW && y >= 0 && y < GridH;

        public bool CanBuildAt(int x, int y)
        {
            if (!IsInBounds(x, y)) return false;
            if (IsBase(x, y)) return false;
            if (Grid[CellIndex(x, y)].Type != TowerType.None) return false;
            return true;
        }

        public int AllocEnemy()
        {
            for (int i = 0; i < MaxEnemies; i++)
                if (!Enemies[i].IsAlive) return i;
            return -1;
        }

        // ==================== Tower stat helpers ====================

        public static int GetTowerCost(TowerType t)
        {
            switch (t)
            {
                case TowerType.Cannon:   return CannonCost;
                case TowerType.Magic:    return MagicCost;
                case TowerType.Ice:      return IceCost;
                case TowerType.Sniper:   return SniperCost;
                case TowerType.Fortress: return FortressCost;
                case TowerType.Storm:    return StormCost;
                default:                 return ArrowCost;
            }
        }

        public static FInt GetTowerRange(TowerType t)
        {
            switch (t)
            {
                case TowerType.Cannon:   return CannonRange;
                case TowerType.Magic:    return MagicRange;
                case TowerType.Ice:      return IceRange;
                case TowerType.Sniper:   return SniperRange;
                case TowerType.Fortress: return FortressRange;
                case TowerType.Storm:    return StormRange;
                default:                 return ArrowRange;
            }
        }

        // Level-aware range: +0.5 per level
        public static FInt GetTowerRange(TowerType t, int level)
        {
            FInt b = GetTowerRange(t);
            return level <= 1 ? b : new FInt(b.Raw + (level - 1) * 512);
        }

        public static int GetTowerCooldown(TowerType t)
        {
            switch (t)
            {
                case TowerType.Cannon:   return CannonCooldown;
                case TowerType.Magic:    return MagicCooldown;
                case TowerType.Ice:      return IceCooldown;
                case TowerType.Sniper:   return SniperCooldown;
                case TowerType.Fortress: return FortressCooldown;
                case TowerType.Storm:    return StormCooldown;
                default:                 return ArrowCooldown;
            }
        }

        // Level-aware cooldown: -20% per level
        public static int GetTowerCooldown(TowerType t, int level)
        {
            int b = GetTowerCooldown(t);
            return b - (level - 1) * (b / 5);
        }

        // Level-aware damage
        public static int GetTowerDamage(TowerType t, int level)
        {
            switch (t)
            {
                case TowerType.Cannon:   return 1 + level * 2;   // 3/5/7
                case TowerType.Ice:      return 0;
                case TowerType.Sniper:   return 2 + level * 4;   // 6/10/14
                case TowerType.Fortress: return 2 + level * 6;   // 8/14/20
                case TowerType.Storm:    return 1 + level * 2;   // 3/5/7
                default:                 return level;            // Arrow/Magic: 1/2/3
            }
        }

        // Storm chain target count per level: 2/3/4
        public static int GetStormChainCount(int level) => 1 + level;

        // Level-aware AoE radii
        public static FInt GetCannonAoeRadius(int level)
            => level <= 1 ? CannonAoeRadius : new FInt(CannonAoeRadius.Raw + (level - 1) * 410);

        public static FInt GetFortressAoeRadius(int level)
            => level <= 1 ? FortressAoeRadius : new FInt(FortressAoeRadius.Raw + (level - 1) * 512);

        public static FInt GetIceAoeRadius(int level)
            => level <= 1 ? IceAoeRadius : new FInt(IceAoeRadius.Raw + (level - 1) * 512);

        public static int GetMagicSlowFrames(int level) => MagicSlowFrames + (level - 1) * 30;
        public static int GetIceSlowFrames(int level)   => IceSlowFrames   + (level - 1) * 30;

        // Upgrade cost: L1→L2 = base, L2→L3 = 2×base
        public static int GetTowerUpgradeCost(TowerType t, int currentLevel)
        {
            int b = GetTowerCost(t);
            return currentLevel == 1 ? b : b * 2;
        }

        // Sell refund = 50% of total invested
        public static int GetSellRefund(TowerType t, int level)
        {
            int b = GetTowerCost(t);
            int total = b;
            if (level >= 2) total += GetTowerUpgradeCost(t, 1);
            if (level >= 3) total += GetTowerUpgradeCost(t, 2);
            return total / 2;
        }

        public const int MaxTowerLevel = 3;

        // ==================== Enemy stat helpers ====================

        public static FInt GetEnemySpeed(EnemyType t)
        {
            switch (t)
            {
                case EnemyType.Fast:    return FastSpeed;
                case EnemyType.Tank:    return TankSpeed;
                case EnemyType.Armored: return ArmoredSpeed;
                case EnemyType.Elite:   return EliteSpeed;
                default:                return BasicSpeed;
            }
        }

        public static int GetEnemyHp(EnemyType t)
        {
            switch (t)
            {
                case EnemyType.Tank:    return TankHp;
                case EnemyType.Fast:    return FastHp;
                case EnemyType.Armored: return ArmoredHp;
                case EnemyType.Elite:   return EliteHp;
                default:                return BasicHp;
            }
        }

        public static int GetEnemyDamage(EnemyType t)
        {
            switch (t)
            {
                case EnemyType.Tank:    return TankDamage;
                case EnemyType.Fast:    return FastDamage;
                case EnemyType.Armored: return ArmoredDamage;
                case EnemyType.Elite:   return EliteDamage;
                default:                return BasicDamage;
            }
        }

        public static int GetEnemyReward(EnemyType t)
        {
            switch (t)
            {
                case EnemyType.Tank:    return TankReward;
                case EnemyType.Fast:    return FastReward;
                case EnemyType.Armored: return ArmoredReward;
                case EnemyType.Elite:   return EliteReward;
                default:                return BasicReward;
            }
        }

        // Cell center in world coords
        public static FInt CellCenterX(int cx) => FInt.FromInt(cx) + FInt.Half;
        public static FInt CellCenterZ(int cy) => FInt.FromInt(cy) + FInt.Half;

        // ==================== ComputeHash ====================

        public uint ComputeHash()
        {
            uint h = 2166136261u;

            h = Fnv(h, FrameNumber);
            h = Fnv(h, RngState);
            h = Fnv(h, (uint)BaseHp);
            h = Fnv(h, SpeedMode);
            h = Fnv(h, SpeedCounter);
            h = Fnv(h, (uint)SharedGold);
            for (int p = 0; p < MaxPlayers; p++)
                h = Fnv(h, (uint)PlayerGold[p]);
            h = Fnv(h, (uint)Wave.WaveNumber);
            h = Fnv(h, (uint)Wave.SpawnRemaining);
            h = Fnv(h, (uint)Wave.InterWaveTimer);
            h = Fnv(h, Wave.AllWavesDone ? 1u : 0u);

            for (int i = 0; i < GridSize; i++)
            {
                ref var t = ref Grid[i];
                h = Fnv(h, (uint)t.Type);
                h = Fnv(h, (uint)t.CooldownFrames);
                h = Fnv(h, (uint)t.Level);
                h = Fnv(h, (uint)(t.OwnerId + 2)); // shift -1 → 1 to stay positive
            }

            for (int i = 0; i < MaxEnemies; i++)
            {
                ref var e = ref Enemies[i];
                if (!e.IsAlive) continue;
                h = Fnv(h, (uint)i);
                h = Fnv(h, (uint)e.Type);
                h = Fnv(h, (uint)e.PosX.Raw);
                h = Fnv(h, (uint)e.PosZ.Raw);
                h = Fnv(h, (uint)e.Hp);
                h = Fnv(h, (uint)e.SlowFrames);
            }

            return h;
        }

        static uint Fnv(uint hash, uint value)
        {
            hash ^= value & 0xFF;         hash *= 16777619u;
            hash ^= (value >> 8) & 0xFF;  hash *= 16777619u;
            hash ^= (value >> 16) & 0xFF; hash *= 16777619u;
            hash ^= (value >> 24) & 0xFF; hash *= 16777619u;
            return hash;
        }
    }
}
