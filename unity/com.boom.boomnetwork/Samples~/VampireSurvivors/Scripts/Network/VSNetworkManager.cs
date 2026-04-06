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

            // Unified input: virtual joystick on mobile, keyboard on PC.
            float h = _joystick != null ? _joystick.Direction.x : Input.GetAxisRaw("Horizontal");
            float v = _joystick != null ? _joystick.Direction.y : Input.GetAxisRaw("Vertical");
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
            // Solo mode skips this — the server is never paused in solo.
            if (ability != 0 && !_isSolo)
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
            }
            else
            {
                _sim.State.Dt = dt;
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

            Debug.Log($"[VS] FrameSync started. Pid={_network.PlayerId}, Slot={_localSlot}, snapshot={_snapshotLoaded}, dt={dt}, fps={init.FrameRate}");
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
            // Solo mode: no network pause needed — only 1 player, no deadlock possible.
            // Simulation still freezes locally (IsAnyPlayerUpgrading guard in Tick).
            bool wantsPause = _sim.IsAnyPlayerUpgrading();
            if (!_isSolo)
            {
                if (wantsPause && !_network.Client.IsGamePaused)
                    _network.Client.RequestGamePause();
                else if (!wantsPause && _network.Client.IsGamePaused)
                    _network.Client.RequestGameResume();
            }

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

            _ui.ShowDesync(mismatch.FrameNumber);
        }

        byte[] TakeSnapshot() => _syncing ? VSSnapshot.Serialize(_sim) : null;

        void LoadSnapshot(byte[] data)
        {
            _snapshotLoaded = true;
            VSSnapshot.Deserialize(data, _sim);

            if (_renderer != null) _renderer.SyncVisuals();
            Debug.Log($"[VS] Snapshot loaded. Frame={_sim.State.FrameNumber}, Wave={_sim.State.WaveNumber}");
        }
    }
}
