// BoomNetwork VampireSurvivors Demo — Network Manager
//
// DESIGN PRINCIPLE 1 — Deterministic paths (GameState mutation):
//   All GameState mutations go through exactly two paths driven by FrameData:
//   1. Frame events (OnPlayerJoined/Left) — embedded in FrameData,
//      dispatched BEFORE OnFrame, same frame on all clients.
//   2. OnFrame → Tick → ApplyInputs — processes player inputs,
//      auto-inits players on first input appearance.
//   OnFrameSyncStart only sets up the deterministic seed and Dt.
//   No InitPlayer, no direct GameState mutation outside frame processing.
//
// DESIGN PRINCIPLE 2 — Level-Triggered Pause Convergence:
//   Game-pause state is managed via Level-Triggered State Convergence,
//   NOT edge-triggered delta tracking. On every OnFrame, we compare:
//     - wantsPause (game logic: IsAnyPlayerUpgrading)
//     - isPaused   (network state: IsGamePaused)
//   and drive toward convergence. This pattern is self-correcting and
//   handles same-tick consecutive state changes without any local memory.
//   The Update() path provides the deadlock-breaker (RequestGameResume)
//   for when the server is paused and OnFrame never fires.
//   Applies to both solo and multiplayer — pausing frame sync saves bandwidth
//   in all cases and is deadlock-safe because Update() always sends Resume.

using UnityEngine;
using BoomNetwork.Client.FrameSync;
using BoomNetwork.Core.FrameSync;
using BoomNetwork.Unity;

namespace BoomNetwork.Samples.VampireSurvivors
{
    [RequireComponent(typeof(BoomNetworkManager))]
    public class VSNetworkManager : MonoBehaviour
    {
        BoomNetworkManager _network;
        VSSimulation _sim;
        VSRenderer _renderer;
        VSUIManager _ui;

        readonly byte[] _inputBuf = new byte[VSInput.InputSize];
        float _sendTimer;
        int _localSlot = -1;
        bool _syncing;
        bool _snapshotLoaded;
        bool _desyncDetected;
        uint _desyncFrame;
        byte _pendingUpgradeChoice;
        bool _firstInputSent;
        bool _isSolo;
        string _soloKey;

        // Mobile virtual joystick — null on PC/Editor
        VSVirtualJoystick _joystick;

        void Start()
        {
            _sim = new VSSimulation();
            _network = GetComponent<BoomNetworkManager>();
            var c = _network.Client;

            c.OnFrameSyncStart += OnFrameSyncStart;
            c.OnFrameSyncStop  += OnFrameSyncStop;
            c.OnFrame          += OnFrame;
            c.OnJoinedRoom     += OnJoinedRoom;
            c.OnPlayerJoined   += OnPlayerJoined;
            c.OnPlayerLeft     += OnPlayerLeft;
            c.OnTakeSnapshot   = TakeSnapshot;
            c.OnLoadSnapshot   = LoadSnapshot;
            c.OnDesyncDetected += OnDesync;

            _ui = VSUIManager.Create();
            _ui.OnUpgradeSelected += choice => _pendingUpgradeChoice = choice;
            _ui.OnSoloClicked     += StartSolo;
            _ui.OnMultiClicked    += StartMultiplayer;

            _ui.ShowLobby(true);
        }

        void Update()
        {
            if (!_syncing) return;

            // Upgrade key presses (keyboard fallback — buttons handled via _ui.OnUpgradeSelected)
            if (_localSlot >= 0 && _localSlot < GameState.MaxPlayers
                && _sim.State.Players[_localSlot].PendingLevelUp)
            {
                if (Input.GetKeyDown(KeyCode.Alpha1)) _pendingUpgradeChoice = 1;
                if (Input.GetKeyDown(KeyCode.Alpha2)) _pendingUpgradeChoice = 2;
                if (Input.GetKeyDown(KeyCode.Alpha3)) _pendingUpgradeChoice = 4;
                if (Input.GetKeyDown(KeyCode.Alpha4)) _pendingUpgradeChoice = 8;
            }

            _sendTimer += Time.deltaTime * 1000f;
            if (_sendTimer < 50f) return;
            _sendTimer -= 50f;

            // Unified input: joystick OR keyboard, joystick takes priority when active.
            // Keyboard always available as fallback (PC + mobile with physical keyboard).
            float jx = _joystick != null ? _joystick.Direction.x : 0f;
            float jy = _joystick != null ? _joystick.Direction.y : 0f;
            bool joystickActive = jx != 0f || jy != 0f;
            float h = joystickActive ? jx : Input.GetAxisRaw("Horizontal");
            float v = joystickActive ? jy : Input.GetAxisRaw("Vertical");
            byte ability = _pendingUpgradeChoice;
            _pendingUpgradeChoice = 0;

            // First input must always be sent to trigger auto-init in ApplyInputs.
            if (!_firstInputSent)
            {
                _firstInputSent = true;
                VSInput.Encode(_inputBuf, h, v, ability);
                _network.SendInput(_inputBuf);
                return;
            }

            if (h == 0f && v == 0f && ability == 0) return;
            VSInput.Encode(_inputBuf, h, v, ability);
            _network.SendInput(_inputBuf);

            // Deadlock-breaker: while server is paused, OnFrame never fires.
            // RequestGameResume unblocks frame delivery after upgrade choice is sent.
            if (ability != 0)
            {
                Debug.Log($"[VS] Upgrade choice sent: ability={ability}, IsGamePaused={_network.Client.IsGamePaused}");
                _network.Client.RequestGameResume();
            }
        }

        // ==================== Lobby ===================================

        void StartSolo()
        {
            _isSolo = true;
            _soloKey = "solo_" + UnityEngine.Random.Range(0, 999999);
            _ui.ShowLobby(false);
            var c = _network.Client;
            c.OnConnected += SoloOnConnected;
            c.OnReady     += SoloOnReady;
            _network.Connect();
        }

        void SoloOnConnected() => _network.Client.MatchRoom(1, _soloKey);
        void SoloOnReady()     => _network.Client.RequestStart();

        void StartMultiplayer()
        {
            _isSolo = false;
            _ui.ShowLobby(false);
            _network.QuickStart();
        }

        // ==================== Network Events ====================

        void OnFrameSyncStart(FrameSyncInitData init)
        {
            FInt dt = FInt.FromInt(init.FrameInterval) / FInt.FromInt(1000);
            uint seed = (uint)(init.StartTime & 0xFFFFFFFF);

            _sim.IsMultiplayer = !_isSolo;

            if (!_snapshotLoaded)
            {
                _sim.Init(dt, seed);
                Debug.Log($"[VS] FrameSync started (Init). Pid={_network.PlayerId}, seed=0x{seed:X8}, startTime={init.StartTime}, dt={dt}, fps={init.FrameRate}");
            }
            else
            {
                _sim.State.Dt = dt;
                Debug.Log($"[VS] FrameSync started (SnapshotResume). Pid={_network.PlayerId}, snapshotFrame={_sim.State.FrameNumber}, RngState=0x{_sim.State.RngState:X8}, Wave={_sim.State.WaveNumber}, dt={dt}, fps={init.FrameRate}");
            }

            _localSlot = _sim.PidToSlot(_network.PlayerId);
            _syncing = true;

            _renderer = GetComponent<VSRenderer>();
            if (_renderer == null) _renderer = gameObject.AddComponent<VSRenderer>();
            float frameIntervalSec = init.FrameInterval / 1000f;
            _renderer.Init(_sim.State, _localSlot, frameIntervalSec);

            if (_joystick == null)
                _joystick = VSVirtualJoystick.Create();

            _ui.SetVisible(true);

            // Dump initial player state for cross-client comparison
            for (int i = 0; i < GameState.MaxPlayers; i++)
            {
                ref var p = ref _sim.State.Players[i];
                if (p.IsActive)
                    Debug.Log($"[VS] Start Player[{i}]: IsAlive={p.IsAlive}, Hp={p.Hp}, Level={p.Level}, Xp={p.Xp}, Pos=({p.PosX},{p.PosZ}), W0={p.Weapon0.Type}L{p.Weapon0.Level}");
            }
        }

        void OnFrameSyncStop()
        {
            _syncing = false;
            _ui.SetVisible(false);
        }

        void OnJoinedRoom(int roomId, int[] existingPlayerIds)
        {
            Debug.Log($"[VS] Joined room {roomId}, {existingPlayerIds.Length} existing players");
        }

        /// <summary>
        /// Frame event — embedded in FrameData, all clients process at the same frame.
        /// </summary>
        void OnPlayerJoined(int pid)
        {
            int slot = _sim.PidToSlot(pid);
            if (slot < 0 || slot >= GameState.MaxPlayers) return;

            if (_syncing && !_sim.State.Players[slot].IsActive)
                _sim.State.InitPlayer(slot);

            Debug.Log($"[VS] Player {pid} joined (slot {slot}){(_syncing ? " — initialized via frame event" : "")}");
        }

        void OnPlayerLeft(int pid)
        {
            int slot = _sim.PidToSlot(pid);
            if (slot < 0 || slot >= GameState.MaxPlayers) return;

            if (_syncing)
            {
                _sim.State.Players[slot].IsActive = false;
                _sim.State.Players[slot].IsAlive  = false;
            }
        }

        void OnFrame(FrameData frame)
        {
            if (_desyncDetected) return;

            _sim.Tick(frame);
            if (_renderer != null) _renderer.SyncVisuals();

            uint hash = _sim.State.ComputeHash();
            _network.Client.SendFrameHash(frame.FrameNumber, hash);

            // Level-Triggered Pause Convergence (see DESIGN PRINCIPLE 2 at top of file)
            // Same for solo and multiplayer: pausing frame sync saves bandwidth and
            // is deadlock-safe because Update() sends RequestGameResume after upgrade choice.
            bool wantsPause = _sim.IsAnyPlayerUpgrading();
            if (wantsPause && !_network.Client.IsGamePaused)
                _network.Client.RequestGamePause();
            else if (!wantsPause && _network.Client.IsGamePaused)
                _network.Client.RequestGameResume();

            _ui.UpdateHUD(_sim, _localSlot, (int)_network.Client.RttMs);
        }

        void OnDesync(FrameHashMismatch mismatch)
        {
            _desyncDetected = true;
            _desyncFrame = mismatch.FrameNumber;
            string detail = $"DESYNC at frame {mismatch.FrameNumber}:";
            foreach (var (pid, h) in mismatch.PlayerHashes)
                detail += $"\n  P{pid}: 0x{h:X8}";
            Debug.LogError($"[VS] {detail}");

            // Per-subsystem hash breakdown to identify diverging system
            var hd = _sim.State.ComputeHashDetailed();
            var s = _sim.State;
            Debug.LogError($"[VS] DESYNC detail (this client) frame={s.FrameNumber}:" +
                $"\n  Wave  =0x{hd.Wave:X8}  [RngState=0x{s.RngState:X8}, WaveNum={s.WaveNumber}, WaveRemaining={s.WaveSpawnRemaining}]" +
                $"\n  Players=0x{hd.Players:X8}" +
                $"\n  Enemies=0x{hd.Enemies:X8}" +
                $"\n  Proj   =0x{hd.Projectiles:X8}" +
                $"\n  Gems   =0x{hd.Gems:X8}" +
                $"\n  Misc   =0x{hd.Misc:X8}" +
                $"\n  Final  =0x{hd.Final:X8}");

            // Dump all active players
            for (int i = 0; i < GameState.MaxPlayers; i++)
            {
                ref var p = ref s.Players[i];
                if (p.IsActive)
                    Debug.LogError($"[VS] DESYNC Player[{i}]: IsAlive={p.IsAlive}, Hp={p.Hp}, Level={p.Level}, Xp={p.Xp}, Pos=({p.PosX},{p.PosZ}), W0={p.Weapon0.Type}L{p.Weapon0.Level}");
            }

            _ui.ShowDesync(mismatch.FrameNumber);
        }

        byte[] TakeSnapshot() => _syncing ? VSSnapshot.Serialize(_sim) : null;

        void LoadSnapshot(byte[] data)
        {
            _snapshotLoaded = true;
            VSSnapshot.Deserialize(data, _sim);

            if (_renderer != null) _renderer.SyncVisuals();
            var s = _sim.State;
            int activeEnemies = 0; for (int i = 0; i < GameState.MaxEnemies; i++) if (s.Enemies[i].IsAlive) activeEnemies++;
            int activeProj = 0; for (int i = 0; i < GameState.MaxProjectiles; i++) if (s.Projectiles[i].IsAlive) activeProj++;
            Debug.Log($"[VS] Snapshot loaded. Frame={s.FrameNumber}, Wave={s.WaveNumber}, RngState=0x{s.RngState:X8}, WaveRemaining={s.WaveSpawnRemaining}, Enemies={activeEnemies}, Proj={activeProj}");
        }
    }
}
