// BoomNetwork VampireSurvivors Demo — Deterministic Simulation (Fixed-Point)
//
// DESIGN PRINCIPLE 1 — GameState mutation 只通过两条确定性路径。
// DESIGN PRINCIPLE 2 — Level-Triggered Pause Convergence。

using BoomNetwork.Core.FrameSync;

namespace BoomNetwork.Samples.VampireSurvivors
{
    public class VSSimulation
    {
        public readonly GameState State = new GameState();

        readonly int[] _pidSlotMap = new int[256];
        int _nextSlot;

        public int PidToSlot(int pid)
        {
            if (pid < 0 || pid >= _pidSlotMap.Length) return -1;
            if (_pidSlotMap[pid] < 0 && _nextSlot < GameState.MaxPlayers)
                _pidSlotMap[pid] = _nextSlot++;
            return _pidSlotMap[pid];
        }

        public void GetPidMap(out int[] map, out int nextSlot) { map = _pidSlotMap; nextSlot = _nextSlot; }
        public void SetPidMap(int[] map, int nextSlot)
        {
            System.Array.Copy(map, _pidSlotMap, System.Math.Min(map.Length, _pidSlotMap.Length));
            _nextSlot = nextSlot;
        }

        public void Init(FInt dt, uint rngSeed)
        {
            State.Dt = dt;
            State.RngState = rngSeed == 0 ? 0xDEADBEEFu : rngSeed;
            State.WaveNumber = 0;
            State.WaveSpawnTimer = 40;
            State.WaveSpawnRemaining = 0;
            State.FrameNumber = 0;
            State.FocusFireTarget = -1;
            State.FocusFireTimer = 0;
            for (int i = 0; i < _pidSlotMap.Length; i++) _pidSlotMap[i] = -1;
            _nextSlot = 0;
        }

        public void Tick(FrameData frame)
        {
            State.FrameNumber = frame.FrameNumber;

            ApplyInputs(frame);

            if (IsAnyPlayerUpgrading()) return;

            WaveSystem.Tick(State);
            EnemySystem.Tick(State);
            WeaponSystem.Tick(State);
            CollisionSystem.AttractGems(State);
            CollisionSystem.CachePositions(State);
            CollisionSystem.Rebuild(State);
            CollisionSystem.Resolve(State);

            for (int i = 0; i < GameState.MaxPlayers; i++)
            {
                ref var p = ref State.Players[i];
                if (p.InvincibilityFrames > 0) p.InvincibilityFrames--;
            }
        }

        public bool IsAnyPlayerUpgrading()
        {
            for (int i = 0; i < GameState.MaxPlayers; i++)
                if (State.Players[i].IsActive && State.Players[i].IsAlive && State.Players[i].PendingLevelUp)
                    return true;
            return false;
        }

        void ApplyInputs(FrameData frame)
        {
            if (frame.Inputs == null) return;
            bool paused = IsAnyPlayerUpgrading();

            for (int i = 0; i < frame.Inputs.Length; i++)
            {
                ref var input = ref frame.Inputs[i];
                int slot = PidToSlot(input.PlayerId);
                if (slot < 0 || slot >= GameState.MaxPlayers) continue;
                ref var player = ref State.Players[slot];

                if (!player.IsActive) State.InitPlayer(slot);
                if (!player.IsAlive) continue;

                VSInput.Decode(input.Data, 0, out FInt dirX, out FInt dirZ, out byte abilityMask);

                if (player.PendingLevelUp && abilityMask > 0)
                {
                    ApplyUpgrade(ref player, abilityMask);
                    player.PendingLevelUp = false;
                }

                if (!paused && (dirX != FInt.Zero || dirZ != FInt.Zero))
                {
                    FInt lenSq = FInt.LengthSqr(dirX, dirZ);
                    if (lenSq > FInt.Epsilon)
                    {
                        FInt invLen = FInt.InvSqrt(lenSq);
                        player.FacingX = dirX * invLen;
                        player.FacingZ = dirZ * invLen;
                    }
                    player.PosX = player.PosX + player.FacingX * GameState.PlayerSpeed * State.Dt;
                    player.PosZ = player.PosZ + player.FacingZ * GameState.PlayerSpeed * State.Dt;

                    FInt limit = GameState.ArenaHalfSize - GameState.PlayerRadius;
                    player.PosX = FInt.Clamp(player.PosX, -limit, limit);
                    player.PosZ = FInt.Clamp(player.PosZ, -limit, limit);
                }
            }
        }

        void ApplyUpgrade(ref PlayerState player, byte abilityMask)
        {
            // 解码选择（bit 0→opt0, bit 1→opt1, bit 2→opt2, bit 3→opt3）
            int choice = -1;
            if ((abilityMask & 1) != 0) choice = 0;
            else if ((abilityMask & 2) != 0) choice = 1;
            else if ((abilityMask & 4) != 0) choice = 2;
            else if ((abilityMask & 8) != 0) choice = 3;
            if (choice < 0 || choice > 3) return;

            // 使用 PlayerState 中存储的随机选项（确定性）
            WeaponType wtype = (WeaponType)player.GetUpgradeOpt(choice);
            if (wtype == WeaponType.None) return;

            int existingSlot = player.FindWeaponSlot(wtype);
            if (existingSlot >= 0)
            {
                var w = player.GetWeapon(existingSlot);
                if (w.Level < GameState.MaxWeaponLevel) w.Level++;
                player.SetWeapon(existingSlot, w);
            }
            else
            {
                int emptySlot = player.FindEmptyWeaponSlot();
                if (emptySlot >= 0)
                {
                    var w = new WeaponSlot { Type = wtype, Level = 1, Cooldown = 0 };
                    player.SetWeapon(emptySlot, w);

                    // 初始化特殊武器
                    if (wtype == WeaponType.Orb)
                    {
                        player.SetOrb(0, new OrbState { Active = true, AngleDeg = FInt.Zero });
                        player.SetOrb(1, new OrbState { Active = true, AngleDeg = FInt.FromInt(180) });
                    }
                }
            }
        }
    }
}
