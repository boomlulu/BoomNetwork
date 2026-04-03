// BoomNetwork TowerDefense Demo — Deterministic Simulation Coordinator
//
// Tick order per frame:
//   ApplyInputs → (if tower placed) PathSystem.Rebuild → WaveSystem → TowerSystem → EnemySystem
//
// ECONOMY (Option D — Layered):
//   Personal gold  → deducted/refunded for individual towers (Arrow/Cannon/Magic/Ice/Sniper)
//   Shared gold    → deducted/refunded for team towers (Fortress/Storm)
//   OwnerId        → recorded on tower so TowerSystem knows where to route kill rewards

using BoomNetwork.Core.FrameSync;

namespace BoomNetwork.Samples.TowerDefense
{
    public class TDSimulation
    {
        public readonly GameState State = new GameState();

        readonly int[] _pidSlotMap = new int[256];
        int _nextSlot;

        // Allocating lookup — only call inside ApplyInputs (deterministic frame processing).
        public int PidToSlot(int pid)
        {
            if (pid < 0 || pid >= _pidSlotMap.Length) return -1;
            if (_pidSlotMap[pid] < 0 && _nextSlot < GameState.MaxPlayers)
                _pidSlotMap[pid] = _nextSlot++;
            return _pidSlotMap[pid];
        }

        // Read-only lookup — safe to call from network callbacks.
        public int LookupSlot(int pid)
        {
            if (pid < 0 || pid >= _pidSlotMap.Length) return -1;
            return _pidSlotMap[pid];
        }

        public void GetPidMap(out int[] map, out int nextSlot) { map = _pidSlotMap; nextSlot = _nextSlot; }
        public void SetPidMap(int[] map, int nextSlot)
        {
            System.Array.Copy(map, _pidSlotMap, System.Math.Min(map.Length, _pidSlotMap.Length));
            _nextSlot = nextSlot;
        }

        public void Init(uint rngSeed)
        {
            State.FrameNumber  = 0;
            State.RngState     = rngSeed == 0 ? 0xDEADBEEFu : rngSeed;
            State.BaseHp       = 3;
            State.SpeedMode    = TDInput.SpeedNormal;
            State.SpeedCounter = 0;

            // Layered economy starting values
            for (int p = 0; p < GameState.MaxPlayers; p++)
                State.PlayerGold[p] = GameState.InitialPersonalGold;
            State.SharedGold = GameState.InitialSharedGold;

            for (int i = 0; i < GameState.GridSize;   i++) State.Grid[i]    = default;
            for (int i = 0; i < GameState.MaxEnemies; i++) State.Enemies[i] = default;

            State.Wave = new WaveState
            {
                WaveNumber     = 0,
                SpawnRemaining = 0,
                InterWaveTimer = GameState.InterWaveFrames,
                AllWavesDone   = false,
            };

            WaveSystem.SpawnTickCounter = 0;
            PathSystem.Rebuild(State);

            for (int i = 0; i < _pidSlotMap.Length; i++) _pidSlotMap[i] = -1;
            _nextSlot = 0;
        }

        public void Tick(FrameData frame)
        {
            State.FrameNumber = frame.FrameNumber;
            bool flowDirty = ApplyInputs(frame);
            if (flowDirty) PathSystem.Rebuild(State);
            if (IsGameOver()) return;

            int steps = GetSimSteps();
            for (int s = 0; s < steps; s++)
            {
                WaveSystem.Tick(State);
                TowerSystem.Tick(State);
                EnemySystem.Tick(State);
            }
        }

        int GetSimSteps()
        {
            switch (State.SpeedMode)
            {
                case TDInput.SpeedSlow: // 0.25x: advance 1 step every 4 network frames
                    State.SpeedCounter = (byte)((State.SpeedCounter + 1) & 3);
                    return State.SpeedCounter == 0 ? 1 : 0;
                case TDInput.Speed2x: return 2;
                case TDInput.Speed3x: return 3;
                default:              return 1; // SpeedNormal
            }
        }

        public bool IsGameOver()
            => State.BaseHp <= 0 || (State.Wave.AllWavesDone && CountAliveEnemies() == 0);

        public bool IsVictory()
            => State.Wave.AllWavesDone && CountAliveEnemies() == 0 && State.BaseHp > 0;

        int CountAliveEnemies()
        {
            int c = 0;
            for (int i = 0; i < GameState.MaxEnemies; i++)
                if (State.Enemies[i].IsAlive) c++;
            return c;
        }

        bool ApplyInputs(FrameData frame)
        {
            if (frame.Inputs == null) return false;
            bool flowDirty = false;

            for (int i = 0; i < frame.Inputs.Length; i++)
            {
                ref var input = ref frame.Inputs[i];
                if (input.Data == null || input.Data.Length < TDInput.InputSize) continue;

                TDInput.Decode(input.Data, 0, out int gx, out int gy, out TowerType towerType);
                if (towerType == TowerType.None) continue;

                // Resolve player slot for this input
                int slot = PidToSlot(input.PlayerId);

                // ── Sell ──────────────────────────────────────────────────
                if ((byte)towerType == TDInput.SellAction)
                {
                    if (!GameState.IsInBounds(gx, gy)) continue;
                    int idx = GameState.CellIndex(gx, gy);
                    ref var t = ref State.Grid[idx];
                    if (t.Type == TowerType.None) continue;

                    int refund = GameState.GetSellRefund(t.Type, t.Level);
                    if (GameState.IsTeamTower(t.Type))
                        State.SharedGold += refund;
                    else if (t.OwnerId >= 0 && t.OwnerId < GameState.MaxPlayers)
                        State.PlayerGold[t.OwnerId] += refund;
                    else
                        State.SharedGold += refund; // fallback

                    State.Grid[idx] = default;
                    flowDirty = true;
                    continue;
                }

                // ── Upgrade ───────────────────────────────────────────────
                if ((byte)towerType == TDInput.UpgradeAction)
                {
                    if (!GameState.IsInBounds(gx, gy)) continue;
                    int idx = GameState.CellIndex(gx, gy);
                    ref var t = ref State.Grid[idx];
                    if (t.Type == TowerType.None || t.Level >= GameState.MaxTowerLevel) continue;

                    int upgCost = GameState.GetTowerUpgradeCost(t.Type, t.Level);
                    // Upgrade cost drawn from same pool as build cost
                    if (GameState.IsTeamTower(t.Type))
                    {
                        if (State.SharedGold < upgCost) continue;
                        State.SharedGold -= upgCost;
                    }
                    else
                    {
                        if (slot < 0 || slot >= GameState.MaxPlayers) continue;
                        if (State.PlayerGold[slot] < upgCost) continue;
                        State.PlayerGold[slot] -= upgCost;
                    }
                    t.Level++;
                    continue;
                }

                // ── Speed change ──────────────────────────────────────────
                if ((byte)towerType == TDInput.SpeedAction)
                {
                    if (gx <= TDInput.Speed3x)
                        State.SpeedMode = (byte)gx;
                    continue;
                }

                // ── Start next wave immediately ───────────────────────────
                if ((byte)towerType == TDInput.StartWaveAction)
                {
                    if (State.Wave.SpawnRemaining == 0 && !State.Wave.AllWavesDone
                        && State.Wave.WaveNumber < GameState.MaxWaves)
                        State.Wave.InterWaveTimer = 1; // next Tick will start the wave
                    continue;
                }

                // ── Place tower ───────────────────────────────────────────
                if (!State.CanBuildAt(gx, gy)) continue;
                int cost = GameState.GetTowerCost(towerType);
                bool isTeam = GameState.IsTeamTower(towerType);

                if (isTeam)
                {
                    if (State.SharedGold < cost) continue;
                    State.SharedGold -= cost;
                }
                else
                {
                    if (slot < 0 || slot >= GameState.MaxPlayers) continue;
                    if (State.PlayerGold[slot] < cost) continue;
                    State.PlayerGold[slot] -= cost;
                }

                int cellIdx = GameState.CellIndex(gx, gy);
                State.Grid[cellIdx] = new Tower
                {
                    Type           = towerType,
                    CooldownFrames = 0,
                    Level          = 1,
                    OwnerId        = isTeam ? GameState.TeamOwner : slot,
                };
                flowDirty = true;
            }

            return flowDirty;
        }
    }
}
