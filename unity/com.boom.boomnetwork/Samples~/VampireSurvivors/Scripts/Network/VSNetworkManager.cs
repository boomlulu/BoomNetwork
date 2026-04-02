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

        readonly byte[] _inputBuf = new byte[VSInput.InputSize];
        float _sendTimer;
        int _localSlot = -1;
        bool _syncing;
        bool _snapshotLoaded;
        bool _desyncDetected;
        uint _desyncFrame;
        byte _pendingUpgradeChoice;
        bool _firstInputSent;

        // Cached GUIStyles
        bool _stylesCached;
        GUIStyle _boxStyle, _titleStyle, _labelStyle, _btnStyle, _smallStyle, _pauseStyle;

        static readonly string[] WeaponNames = { "", "Knife", "Orb", "Lightning", "Holy Water" };
        static readonly string[] WeaponIcons = { "", "\ud83d\udde1", "\ud83d\udd2e", "\u26a1", "\ud83d\udca7" };

        void Start()
        {
            _sim = new VSSimulation();
            _network = GetComponent<BoomNetworkManager>();
            var c = _network.Client;

            c.OnFrameSyncStart += OnFrameSyncStart;
            c.OnFrameSyncStop += OnFrameSyncStop;
            c.OnFrame += OnFrame;
            c.OnJoinedRoom += OnJoinedRoom;
            c.OnPlayerJoined += OnPlayerJoined;
            c.OnPlayerLeft += OnPlayerLeft;
            c.OnTakeSnapshot = TakeSnapshot;
            c.OnLoadSnapshot = LoadSnapshot;
            c.OnDesyncDetected += OnDesync;

            _network.QuickStart();
        }

        void Update()
        {
            if (!_syncing) return;

            // Upgrade key presses
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

            float h = Input.GetAxisRaw("Horizontal");
            float v = Input.GetAxisRaw("Vertical");
            byte ability = _pendingUpgradeChoice;
            _pendingUpgradeChoice = 0;

            // First input must always be sent to trigger auto-init in ApplyInputs.
            // "Silent When Idle" would otherwise delay player spawn indefinitely.
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

        // ==================== Network Events ====================

        void OnFrameSyncStart(FrameSyncInitData init)
        {
            FInt dt = FInt.FromInt(init.FrameInterval) / FInt.FromInt(1000);
            uint seed = (uint)(init.StartTime & 0xFFFFFFFF);

            if (!_snapshotLoaded)
            {
                // Fresh start — Init sets Dt, RngState, wave timers.
                // NO InitPlayer here. Players are initialized through two
                // deterministic paths only:
                //   a) OnPlayerJoined frame event (joins during sync)
                //   b) ApplyInputs auto-init (first input in FrameData)
                _sim.Init(dt, seed);
            }
            else
            {
                // Late join — snapshot has the complete game state.
                // Only apply Dt from InitData (snapshot already has correct
                // RngState, wave state, and all active players).
                _sim.State.Dt = dt;
            }

            // Map our PlayerId to a local slot (0-3). PidToSlot assigns
            // slots deterministically in order of first appearance.
            _localSlot = _sim.PidToSlot(_network.PlayerId);
            _syncing = true;

            _renderer = GetComponent<VSRenderer>();
            if (_renderer == null) _renderer = gameObject.AddComponent<VSRenderer>();
            float frameIntervalSec = init.FrameInterval / 1000f;
            _renderer.Init(_sim.State, _localSlot, frameIntervalSec);

            Debug.Log($"[VS] FrameSync started. Pid={_network.PlayerId}, Slot={_localSlot}, snapshot={_snapshotLoaded}, dt={dt}, fps={init.FrameRate}");
        }

        void OnFrameSyncStop() { _syncing = false; }

        void OnJoinedRoom(int roomId, int[] existingPlayerIds)
        {
            Debug.Log($"[VS] Joined room {roomId}, {existingPlayerIds.Length} existing players");
        }

        /// <summary>
        /// Frame event — embedded in FrameData, all clients process at the same frame.
        /// During sync: deterministic. Before sync: ExtCmd, only used for tracking.
        /// </summary>
        void OnPlayerJoined(int pid)
        {
            int slot = _sim.PidToSlot(pid);
            if (slot < 0 || slot >= GameState.MaxPlayers) return;

            // During sync, frame events guarantee same-frame delivery.
            // InitPlayer here is deterministic — all clients execute at the same frame.
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
                _sim.State.Players[slot].IsAlive = false;
            }
        }

        void OnFrame(FrameData frame)
        {
            if (_desyncDetected) return;

            _sim.Tick(frame);
            if (_renderer != null) _renderer.SyncVisuals();

            uint hash = _sim.State.ComputeHash();
            _network.Client.SendFrameHash(frame.FrameNumber, hash);

            // Level-Triggered Pause Convergence (see DESIGN PRINCIPLE 2 at top of file):
            // Compare game's desired pause state vs network's actual pause state,
            // and drive toward convergence every frame. No local memory needed.
            // IsGamePaused is the authoritative source of truth for network state.
            bool wantsPause = _sim.IsAnyPlayerUpgrading();
            if (wantsPause && !_network.Client.IsGamePaused)
                _network.Client.RequestGamePause();
            else if (!wantsPause && _network.Client.IsGamePaused)
                _network.Client.RequestGameResume();
        }

        void OnDesync(FrameHashMismatch mismatch)
        {
            _desyncDetected = true;
            _desyncFrame = mismatch.FrameNumber;
            string detail = $"DESYNC at frame {mismatch.FrameNumber}:";
            foreach (var (pid, h) in mismatch.PlayerHashes)
                detail += $"\n  P{pid}: 0x{h:X8}";
            Debug.LogError($"[VS] {detail}");
        }

        // Only take snapshots after sync has started — the initial
        // RequestStart snapshot would capture uninitialized state
        // (before Init sets the RNG seed), causing late-join desync.
        byte[] TakeSnapshot() => _syncing ? VSSnapshot.Serialize(_sim) : null;

        void LoadSnapshot(byte[] data)
        {
            _snapshotLoaded = true;
            VSSnapshot.Deserialize(data, _sim);

            // No InitPlayer, no state mutation. The snapshot is the
            // complete authoritative state. Players who join after the
            // snapshot will be initialized via frame events or auto-init.

            if (_renderer != null) _renderer.SyncVisuals();
            Debug.Log($"[VS] Snapshot loaded. Frame={_sim.State.FrameNumber}, Wave={_sim.State.WaveNumber}");
        }

        // ==================== OnGUI ====================

        void OnGUI()
        {
            if (!_syncing) return;
            CacheStyles();
            DrawStatusHUD();
            DrawDesyncOverlay();
            DrawPauseOverlay();
            DrawUpgradePanel();
        }

        void CacheStyles()
        {
            if (_stylesCached) return;
            _stylesCached = true;

            _boxStyle = new GUIStyle(GUI.skin.box)
                { normal = { background = MakeTex(1, 1, new Color(0, 0, 0, 0.7f)) } };
            _titleStyle = new GUIStyle(GUI.skin.label)
                { fontStyle = FontStyle.Bold, fontSize = 14, normal = { textColor = Color.white }, richText = true };
            _labelStyle = new GUIStyle(GUI.skin.label)
                { fontSize = 12, normal = { textColor = Color.white }, richText = true };
            _btnStyle = new GUIStyle(GUI.skin.button)
                { fontSize = 13, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
            _smallStyle = new GUIStyle(GUI.skin.label)
                { fontSize = 10, normal = { textColor = new Color(0.6f, 0.6f, 0.6f) }, richText = true };
            _pauseStyle = new GUIStyle(GUI.skin.label)
                { fontSize = 20, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter,
                  normal = { textColor = new Color(1f, 1f, 0.3f) }, richText = true };
        }

        void DrawStatusHUD()
        {
            var state = _sim.State;
            int aliveEnemies = 0;
            for (int i = 0; i < GameState.MaxEnemies; i++)
                if (state.Enemies[i].IsAlive) aliveEnemies++;

            float w = 360, y = 10, x = 10;
            GUI.Box(new Rect(x, y, w, 30 + CountActivePlayers() * 22 + 30), "", _boxStyle);
            y += 5;

            GUI.Label(new Rect(x + 5, y, w, 20),
                $"<b>Vampire Survivors</b>  F:{state.FrameNumber}  RTT:{_network.Client.RttMs}ms", _titleStyle);
            y += 20;
            GUI.Label(new Rect(x + 5, y, w, 18),
                $"Wave {state.WaveNumber}  Enemies: {aliveEnemies}/{GameState.MaxEnemies}", _labelStyle);
            y += 20;

            for (int i = 0; i < GameState.MaxPlayers; i++)
            {
                ref var p = ref state.Players[i];
                if (!p.IsActive) continue;
                string me = (i == _localSlot) ? "\u2605" : " ";
                string hp = p.IsAlive ? $"<color=#88ff88>HP {p.Hp}/{p.MaxHp}</color>" : "<color=red>DEAD</color>";
                string upgrading = p.PendingLevelUp ? " <color=yellow>[CHOOSING...]</color>" : "";
                string weapons = GetWeaponString(ref p);
                GUI.Label(new Rect(x + 5, y, w, 20),
                    $"{me}P{i + 1} {hp} Lv{p.Level} K:{p.KillCount} {weapons}{upgrading}", _labelStyle);
                y += 22;
            }

            y += 4;
            GUI.Label(new Rect(x + 5, y, w, 16),
                $"{aliveEnemies} enemies, 0 extra bandwidth (pure FrameSync)", _smallStyle);
        }

        void DrawDesyncOverlay()
        {
            if (!_desyncDetected) return;
            float w = 400, h = 60;
            float px = (Screen.width - w) / 2f;
            float py = Screen.height * 0.2f;
            GUI.Box(new Rect(px, py, w, h), "", _boxStyle);
            GUI.Label(new Rect(px, py, w, h),
                $"<color=red><b>DESYNC DETECTED</b></color>\nFrame {_desyncFrame} \u2014 State hashes differ. Game paused.", _pauseStyle);
        }

        void DrawPauseOverlay()
        {
            int upgradingSlot = -1;
            for (int i = 0; i < GameState.MaxPlayers; i++)
            {
                if (_sim.State.Players[i].IsActive && _sim.State.Players[i].PendingLevelUp)
                { upgradingSlot = i; break; }
            }
            if (upgradingSlot < 0) return;
            if (upgradingSlot == _localSlot) return;

            float w = 300, h = 50;
            float px = (Screen.width - w) / 2f;
            float py = Screen.height * 0.3f;
            GUI.Box(new Rect(px, py, w, h), "", _boxStyle);
            GUI.Label(new Rect(px, py, w, h),
                $"PAUSED\nP{upgradingSlot + 1} is choosing an upgrade...", _pauseStyle);
        }

        void DrawUpgradePanel()
        {
            if (_localSlot < 0 || _localSlot >= GameState.MaxPlayers) return;
            ref var player = ref _sim.State.Players[_localSlot];
            if (!player.PendingLevelUp) return;

            float panelW = 400, panelH = 220;
            float px = (Screen.width - panelW) / 2f;
            float py = (Screen.height - panelH) / 2f;

            GUI.Box(new Rect(px, py, panelW, panelH), "", _boxStyle);
            GUI.Label(new Rect(px + 10, py + 10, panelW, 30),
                $"<color=yellow><b>LEVEL UP! (Lv.{player.Level})</b></color>  Choose upgrade:", _titleStyle);

            float btnY = py + 50;
            for (int i = 0; i < 4; i++)
            {
                WeaponType wt = (WeaponType)(i + 1);
                int existingSlot = player.FindWeaponSlot(wt);
                string label = existingSlot >= 0
                    ? $"[{i + 1}] {WeaponIcons[(int)wt]} {WeaponNames[(int)wt]} Lv{player.GetWeapon(existingSlot).Level} \u2192 Lv{player.GetWeapon(existingSlot).Level + 1}"
                    : $"[{i + 1}] {WeaponIcons[(int)wt]} {WeaponNames[(int)wt]} (NEW)";

                if (GUI.Button(new Rect(px + 10, btnY, panelW - 20, 36), label, _btnStyle))
                    _pendingUpgradeChoice = (byte)(1 << i);
                btnY += 40;
            }
        }

        string GetWeaponString(ref PlayerState p)
        {
            string s = "";
            for (int i = 0; i < PlayerState.MaxWeaponSlots; i++)
            {
                var w = p.GetWeapon(i);
                if (w.Type == WeaponType.None) continue;
                if (s.Length > 0) s += " ";
                s += $"{WeaponIcons[(int)w.Type]}{w.Level}";
            }
            return s;
        }

        int CountActivePlayers()
        {
            int c = 0;
            for (int i = 0; i < GameState.MaxPlayers; i++)
                if (_sim.State.Players[i].IsActive) c++;
            return c;
        }

        static Texture2D MakeTex(int w, int h, Color col)
        {
            var pix = new Color[w * h];
            for (int i = 0; i < pix.Length; i++) pix[i] = col;
            var tex = new Texture2D(w, h);
            tex.SetPixels(pix); tex.Apply();
            return tex;
        }
    }
}
