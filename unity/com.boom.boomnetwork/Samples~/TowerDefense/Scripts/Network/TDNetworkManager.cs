// BoomNetwork TowerDefense Demo — Network Manager
//
// DESIGN PRINCIPLE — Deterministic paths (GameState mutation):
//   All GameState mutations go through one path driven by FrameData:
//   OnFrame → TDSimulation.Tick → ApplyInputs
//   No direct GameState mutation outside frame processing.
//
// SILENT WHEN IDLE (Red Line #5):
//   Input is only sent when the player clicks a cell to place/upgrade/sell a tower.
//   Idle frames send nothing. Tower placement is immediate (Red Line #4).
//
// INTERACTION MODEL:
//   Click any grid cell → context menu appears (floating above the cell).
//   Empty cell: choose tower type to build.
//   Occupied cell: upgrade (if level < 3) or sell.

using UnityEngine;
using BoomNetwork.Client.FrameSync;
using BoomNetwork.Core.FrameSync;
using BoomNetwork.Unity;

namespace BoomNetwork.Samples.TowerDefense
{
    [RequireComponent(typeof(BoomNetworkManager))]
    public class TDNetworkManager : MonoBehaviour
    {
        BoomNetworkManager _network;
        TDSimulation _sim;
        TDRenderer _renderer;

        readonly byte[] _inputBuf = new byte[TDInput.InputSize];
        bool _syncing;
        bool _snapshotLoaded;
        bool _desyncDetected;
        uint _desyncFrame;
        bool _gameOver;

        // Cell selection state
        int _selectedGx = -1, _selectedGy = -1;
        Rect _menuRect; // IMGUI coords (y from top), used to ignore menu clicks in Update
        int _mySlot = -1; // player's own slot index (resolved on FrameSyncStart)

        // Cached GUIStyles
        bool _stylesCached;
        GUIStyle _boxStyle, _titleStyle, _labelStyle, _btnStyle, _smallStyle, _btnSelectedStyle, _btnDimStyle;

        static readonly string[] TowerNames = { "", "弓箭塔", "炮台", "魔法塔", "冰霜塔", "狙击塔", "堡垒炮", "风暴塔" };
        static readonly string[] TowerIcons = { "", "↑", "●", "★", "❄", "◎", "⬡", "⚡" };
        static readonly int[]    TowerCosts = { 0,
            GameState.ArrowCost,   GameState.CannonCost,  GameState.MagicCost,
            GameState.IceCost,     GameState.SniperCost,
            GameState.FortressCost, GameState.StormCost };

        void Start()
        {
            _sim     = new TDSimulation();
            _network = GetComponent<BoomNetworkManager>();
            var c    = _network.Client;

            c.OnFrameSyncStart  += OnFrameSyncStart;
            c.OnFrameSyncStop   += OnFrameSyncStop;
            c.OnFrame           += OnFrame;
            c.OnJoinedRoom      += OnJoinedRoom;
            c.OnPlayerJoined    += OnPlayerJoined;
            c.OnPlayerLeft      += OnPlayerLeft;
            c.OnTakeSnapshot     = TakeSnapshot;
            c.OnLoadSnapshot     = LoadSnapshot;
            c.OnDesyncDetected  += OnDesync;

            _network.QuickStart();
        }

        void Update()
        {
            if (!_syncing || _renderer == null) return;

            if (Input.GetMouseButtonDown(0))
            {
                // Ignore if clicking inside the context menu
                if (IsMouseOverMenu(Input.mousePosition)) return;

                if (_renderer.TryGetGridCell(Input.mousePosition, out int gx, out int gy))
                {
                    // Toggle selection: click same cell again to deselect
                    if (_selectedGx == gx && _selectedGy == gy)
                        ClearSelection();
                    else
                    {
                        _selectedGx = gx;
                        _selectedGy = gy;
                        if (_renderer != null) _renderer.SetCellHighlight(gx, gy);
                    }
                }
                else
                {
                    ClearSelection();
                }
            }
        }

        void ClearSelection()
        {
            _selectedGx = _selectedGy = -1;
            if (_renderer != null) _renderer.SetCellHighlight(-1, -1);
        }

        bool IsMouseOverMenu(Vector2 screenPos)
        {
            if (_selectedGx < 0) return false;
            // Convert Input.mousePosition (y from bottom) to IMGUI coords (y from top)
            float guiY = Screen.height - screenPos.y;
            return _menuRect.Contains(new Vector2(screenPos.x, guiY));
        }

        void SendBuild(int gx, int gy, TowerType tt)
        {
            TDInput.Encode(_inputBuf, gx, gy, (byte)tt);
            _network.SendInput(_inputBuf);
            ClearSelection();
        }

        void SendUpgrade(int gx, int gy)
        {
            TDInput.Encode(_inputBuf, gx, gy, TDInput.UpgradeAction);
            _network.SendInput(_inputBuf);
            ClearSelection();
        }

        void SendSell(int gx, int gy)
        {
            TDInput.Encode(_inputBuf, gx, gy, TDInput.SellAction);
            _network.SendInput(_inputBuf);
            ClearSelection();
        }

        // ==================== Network Events ====================

        void OnFrameSyncStart(FrameSyncInitData init)
        {
            uint seed = (uint)(init.StartTime & 0xFFFFFFFF);

            if (!_snapshotLoaded)
                _sim.Init(seed);

            _mySlot = _sim.PidToSlot(_network.PlayerId);
            _syncing = true;

            _renderer = GetComponent<TDRenderer>();
            if (_renderer == null) _renderer = gameObject.AddComponent<TDRenderer>();
            _renderer.Init(_sim.State);

            Debug.Log($"[TD] FrameSync started. Pid={_network.PlayerId}, snapshot={_snapshotLoaded}, fps={init.FrameRate}");
        }

        void OnFrameSyncStop() { _syncing = false; }

        void OnJoinedRoom(int roomId, int[] existingPlayerIds)
        {
            Debug.Log($"[TD] Joined room {roomId}, {existingPlayerIds.Length} existing players");
        }

        void OnPlayerJoined(int pid)
        {
            _sim.PidToSlot(pid);
            Debug.Log($"[TD] Player {pid} joined");
        }

        void OnPlayerLeft(int pid)
        {
            Debug.Log($"[TD] Player {pid} left");
        }

        void OnFrame(FrameData frame)
        {
            if (_desyncDetected || _gameOver) return;

            _sim.Tick(frame);
            if (_renderer != null) _renderer.SyncVisuals();

            uint hash = _sim.State.ComputeHash();
            _network.Client.SendFrameHash(frame.FrameNumber, hash);

            if (_sim.IsGameOver() && !_gameOver)
            {
                _gameOver = true;
                _network.Client.RequestGamePause();
                Debug.Log($"[TD] Game over at frame {frame.FrameNumber}. Victory={_sim.IsVictory()}");
            }
        }

        void OnDesync(FrameHashMismatch mismatch)
        {
            _desyncDetected = true;
            _desyncFrame    = mismatch.FrameNumber;
            string detail   = $"DESYNC at frame {mismatch.FrameNumber}:";
            foreach (var (pid, h) in mismatch.PlayerHashes)
                detail += $"\n  P{pid}: 0x{h:X8}";
            Debug.LogError($"[TD] {detail}");
        }

        byte[] TakeSnapshot() => _syncing ? TDSnapshot.Serialize(_sim) : null;

        void LoadSnapshot(byte[] data)
        {
            _snapshotLoaded = true;
            TDSnapshot.Deserialize(data, _sim);
            _mySlot = _sim.PidToSlot(_network.PlayerId);
            if (_renderer != null) _renderer.SyncVisuals();
            Debug.Log($"[TD] Snapshot loaded. Frame={_sim.State.FrameNumber}, Wave={_sim.State.Wave.WaveNumber}");
        }

        // ==================== OnGUI ====================

        void OnGUI()
        {
            if (!_syncing) return;
            CacheStyles();
            DrawStatusHUD();
            if (_selectedGx >= 0) DrawCellMenu();
            DrawDesyncOverlay();
            DrawGameOverOverlay();
        }

        void CacheStyles()
        {
            if (_stylesCached) return;
            _stylesCached = true;

            _boxStyle = new GUIStyle(GUI.skin.box)
                { normal = { background = MakeTex(1, 1, new Color(0, 0, 0, 0.82f)) } };
            _titleStyle = new GUIStyle(GUI.skin.label)
                { fontStyle = FontStyle.Bold, fontSize = 14, normal = { textColor = Color.white }, richText = true };
            _labelStyle = new GUIStyle(GUI.skin.label)
                { fontSize = 12, normal = { textColor = Color.white }, richText = true };
            _btnStyle = new GUIStyle(GUI.skin.button)
                { fontSize = 13, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
            _smallStyle = new GUIStyle(GUI.skin.label)
                { fontSize = 10, normal = { textColor = new Color(0.6f, 0.6f, 0.6f) }, richText = true };
            _btnSelectedStyle = new GUIStyle(_btnStyle)
                { normal = { textColor = Color.yellow, background = MakeTex(1, 1, new Color(0.3f, 0.3f, 0f, 0.9f)) } };
            _btnDimStyle = new GUIStyle(_btnStyle)
                { normal = { textColor = new Color(0.5f, 0.5f, 0.5f) } };
        }

        void DrawStatusHUD()
        {
            var state  = _sim.State;
            int alive  = CountAliveEnemies();
            string waveInfo;
            if (state.Wave.AllWavesDone && alive == 0)
                waveInfo = "<color=yellow>所有波次已清除！</color>";
            else if (state.Wave.SpawnRemaining > 0)
                waveInfo = $"第 <b>{state.Wave.WaveNumber}</b>/{GameState.MaxWaves} 波  剩余敌人：{state.Wave.SpawnRemaining}";
            else
                waveInfo = $"第 <b>{state.Wave.WaveNumber}</b>/{GameState.MaxWaves} 波  下波倒计时：{state.Wave.InterWaveTimer / 30}s";

            float x = 10, y = 10, w = 340;
            GUI.Box(new Rect(x, y, w, 90), "", _boxStyle);
            y += 5;
            GUI.Label(new Rect(x + 5, y, w, 20),
                $"<b>塔防守卫</b>  帧：{state.FrameNumber}  延迟：{_network.Client.RttMs}ms", _titleStyle);
            y += 22;
            int myGold = (_mySlot >= 0 && _mySlot < GameState.MaxPlayers) ? state.PlayerGold[_mySlot] : 0;
            GUI.Label(new Rect(x + 5, y, w, 20),
                $"基地血量：<color=#ff8888>{state.BaseHp}</color>/3  个人金：<color=#ffdd44>{myGold}</color>  共享金：<color=#aaddff>{state.SharedGold}</color>", _labelStyle);
            y += 22;
            GUI.Label(new Rect(x + 5, y, w, 20), waveInfo, _labelStyle);
            y += 20;
            GUI.Label(new Rect(x + 5, y, w, 16),
                $"屏幕上 {alive} 只敌人，帧包大小不变（纯帧同步）", _smallStyle);
        }

        void DrawCellMenu()
        {
            int gx = _selectedGx, gy = _selectedGy;
            if (!GameState.IsInBounds(gx, gy)) return;

            ref var tower = ref _sim.State.Grid[GameState.CellIndex(gx, gy)];
            bool hasTower = tower.Type != TowerType.None;
            int myGoldMenu  = (_mySlot >= 0 && _mySlot < GameState.MaxPlayers) ? _sim.State.PlayerGold[_mySlot] : 0;
            int sharedGold  = _sim.State.SharedGold;

            Vector2 cellScreen = _renderer != null
                ? _renderer.GetCellScreenCenter(gx, gy)
                : new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);

            float guiX = cellScreen.x;
            float guiY = Screen.height - cellScreen.y;

            const float MenuW = 240f;
            // hasTower: upgrade/sell rows. empty: 5 personal + header + 2 team + header = 360
            float menuH = hasTower ? 150f : 380f;
            float px = Mathf.Clamp(guiX - MenuW * 0.5f, 5f, Screen.width  - MenuW - 5f);
            float py = Mathf.Clamp(guiY - menuH - 20f,  5f, Screen.height - menuH - 5f);

            _menuRect = new Rect(px, py, MenuW, menuH);
            GUI.Box(_menuRect, "", _boxStyle);

            float iy = py + 6;

            if (!hasTower)
            {
                // ─── 个人建造（5 individual towers） ──────────────
                GUI.Label(new Rect(px + 6, iy, MenuW - 12, 18),
                    $"<b>个人建造</b>  <color=#ffdd44>{myGoldMenu}金</color>", _titleStyle);
                iy += 20;

                for (int ti = 1; ti <= 5; ti++)
                {
                    var tt = (TowerType)ti;
                    int cost = TowerCosts[ti];
                    bool canAfford = myGoldMenu >= cost;
                    string label = $"{TowerIcons[ti]} {TowerNames[ti]}  <size=11>{cost}金</size>";
                    if (GUI.Button(new Rect(px + 6, iy, MenuW - 12, 28), label,
                            canAfford ? _btnStyle : _btnDimStyle))
                    {
                        if (canAfford) SendBuild(gx, gy, tt);
                    }
                    iy += 32;
                }

                // ─── 团队建造（Fortress + Storm, use shared gold） ──
                iy += 4;
                GUI.Label(new Rect(px + 6, iy, MenuW - 12, 18),
                    $"<b>团队建造</b>  <color=#aaddff>{sharedGold}共享金</color>", _titleStyle);
                iy += 20;

                for (int ti = 6; ti <= 7; ti++)
                {
                    var tt = (TowerType)ti;
                    int cost = TowerCosts[ti];
                    bool canAfford = sharedGold >= cost;
                    string label = $"{TowerIcons[ti]} {TowerNames[ti]}  <size=11>{cost}共享</size>";
                    if (GUI.Button(new Rect(px + 6, iy, MenuW - 12, 28), label,
                            canAfford ? _btnStyle : _btnDimStyle))
                    {
                        if (canAfford) SendBuild(gx, gy, tt);
                    }
                    iy += 32;
                }

                iy += 2;
                if (GUI.Button(new Rect(px + 6, iy, MenuW - 12, 22), "取消", _btnStyle))
                    ClearSelection();
            }
            else
            {
                // ─── 已有塔菜单 ────────────────────────────────
                bool isTeam = GameState.IsTeamTower(tower.Type);
                int goldForUpg = isTeam ? sharedGold : myGoldMenu;
                string goldTag = isTeam
                    ? $"<color=#aaddff>{sharedGold}共享</color>"
                    : $"<color=#ffdd44>{myGoldMenu}个人</color>";

                int lvl = tower.Level;
                string lvlStr = lvl >= GameState.MaxTowerLevel
                    ? "<color=#ffdd44>★ 满级</color>"
                    : $"Lv <b>{lvl}</b>";
                GUI.Label(new Rect(px + 6, iy, MenuW - 12, 18),
                    $"<b>{TowerNames[(int)tower.Type]}</b>  {lvlStr}  {goldTag}", _titleStyle);
                iy += 22;

                // Upgrade button
                if (lvl < GameState.MaxTowerLevel)
                {
                    int upgCost = GameState.GetTowerUpgradeCost(tower.Type, lvl);
                    bool canUpg = goldForUpg >= upgCost;
                    string upgLabel = $"↑ 升级到 Lv{lvl + 1}  <size=11>{upgCost}{(isTeam ? "共享" : "金")}</size>";
                    if (GUI.Button(new Rect(px + 6, iy, MenuW - 12, 34), upgLabel,
                            canUpg ? _btnSelectedStyle : _btnDimStyle))
                    {
                        if (canUpg) SendUpgrade(gx, gy);
                    }
                    iy += 38;
                }
                else
                {
                    GUI.Label(new Rect(px + 6, iy, MenuW - 12, 34),
                        "<color=#888888>已达最高等级</color>", _labelStyle);
                    iy += 38;
                }

                // Sell button
                int refund = GameState.GetSellRefund(tower.Type, lvl);
                string goldType = isTeam ? "共享" : "金";
                if (GUI.Button(new Rect(px + 6, iy, MenuW - 12, 30),
                        $"✕ 出售  <size=11>+{refund}{goldType}</size>", _btnStyle))
                    SendSell(gx, gy);
                iy += 34;

                if (GUI.Button(new Rect(px + 6, iy, MenuW - 12, 22), "取消", _btnStyle))
                    ClearSelection();
            }
        }

        void DrawDesyncOverlay()
        {
            if (!_desyncDetected) return;
            float w = 400, h = 60;
            float px = (Screen.width - w) / 2f;
            float py = Screen.height * 0.2f;
            GUI.Box(new Rect(px, py, w, h), "", _boxStyle);
            var style = new GUIStyle(GUI.skin.label)
                { fontSize = 18, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter,
                  normal = { textColor = Color.red }, richText = true };
            GUI.Label(new Rect(px, py, w, h),
                $"<b>检测到不同步</b>\n第 {_desyncFrame} 帧", style);
        }

        void DrawGameOverOverlay()
        {
            if (!_sim.IsGameOver()) return;
            string msg = _sim.IsVictory()
                ? "<color=yellow><b>胜利！</b></color>\n所有波次已通关！"
                : "<color=red><b>基地已失守</b></color>\n游戏结束";

            float w = 400, h = 80;
            float px = (Screen.width - w) / 2f;
            float py = Screen.height * 0.35f;
            GUI.Box(new Rect(px, py, w, h), "", _boxStyle);
            var style = new GUIStyle(GUI.skin.label)
                { fontSize = 22, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter,
                  normal = { textColor = Color.white }, richText = true };
            GUI.Label(new Rect(px, py, w, h), msg, style);
        }

        int CountAliveEnemies()
        {
            int c = 0;
            for (int i = 0; i < GameState.MaxEnemies; i++)
                if (_sim.State.Enemies[i].IsAlive) c++;
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
