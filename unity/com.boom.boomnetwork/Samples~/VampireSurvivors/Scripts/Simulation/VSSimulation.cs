// BoomNetwork VampireSurvivors Demo — Deterministic Simulation (Fixed-Point)
//
// Upgrade pause: when ANY player has PendingLevelUp, simulation freezes.
// Only upgrade-choice inputs are processed during pause.

using BoomNetwork.Core.FrameSync;

namespace BoomNetwork.Samples.VampireSurvivors
{
    public class VSSimulation
    {
        public readonly GameState State = new GameState();

        static readonly WeaponType[] UpgradePool =
            { WeaponType.Knife, WeaponType.Orb, WeaponType.Lightning, WeaponType.HolyWater };

        public void Init(FInt dt, uint rngSeed)
        {
            State.Dt = dt;
            State.RngState = rngSeed == 0 ? 0xDEADBEEFu : rngSeed;
            State.WaveNumber = 0;
            State.WaveSpawnTimer = 40;
            State.WaveSpawnRemaining = 0;
            State.FrameNumber = 0;
        }

        public void Tick(FrameData frame)
        {
            State.FrameNumber = frame.FrameNumber;

            // Always process inputs (upgrade choices must go through even when paused)
            ApplyInputs(frame);

            // If any player is choosing an upgrade, freeze the simulation
            if (IsAnyPlayerUpgrading())
                return;

            // Normal simulation
            WaveSystem.Tick(State);
            EnemySystem.Tick(State);
            WeaponSystem.Tick(State);
            CollisionSystem.CachePositions(State);
            CollisionSystem.Rebuild(State);
            CollisionSystem.Resolve(State);

            for (int i = 0; i < GameState.MaxPlayers; i++)
            {
                ref var p = ref State.Players[i];
                if (p.InvincibilityFrames > 0) p.InvincibilityFrames--;
            }
        }

        bool IsAnyPlayerUpgrading()
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
                int slot = input.PlayerId - 1;
                if (slot < 0 || slot >= GameState.MaxPlayers) continue;
                ref var player = ref State.Players[slot];

                // Auto-init: first input from this player → deterministic spawn
                // All clients see the same FrameData, so this is frame-exact.
                if (!player.IsActive)
                    State.InitPlayer(slot);

                if (!player.IsAlive) continue;

                VSInput.Decode(input.Data, 0, out FInt dirX, out FInt dirZ, out byte abilityMask);

                // Upgrade choice is always processed
                if (player.PendingLevelUp && abilityMask > 0)
                {
                    ApplyUpgrade(ref player, abilityMask);
                    player.PendingLevelUp = false;
                }

                // Movement only when NOT paused
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
            int choice = -1;
            if ((abilityMask & 1) != 0) choice = 0;
            else if ((abilityMask & 2) != 0) choice = 1;
            else if ((abilityMask & 4) != 0) choice = 2;
            else if ((abilityMask & 8) != 0) choice = 3;
            if (choice < 0 || choice >= UpgradePool.Length) return;

            WeaponType wtype = UpgradePool[choice];
            int existingSlot = player.FindWeaponSlot(wtype);
            if (existingSlot >= 0)
            {
                var w = player.GetWeapon(existingSlot);
                if (w.Level < 5) w.Level++;
                player.SetWeapon(existingSlot, w);
            }
            else
            {
                int emptySlot = player.FindEmptyWeaponSlot();
                if (emptySlot >= 0)
                {
                    var w = new WeaponSlot { Type = wtype, Level = 1, Cooldown = 0 };
                    player.SetWeapon(emptySlot, w);
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
