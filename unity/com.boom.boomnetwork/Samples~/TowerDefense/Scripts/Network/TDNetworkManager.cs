// BoomNetwork TowerDefense Demo — Network Manager
//
// UI State Machine:
//   Connecting → Lobby (room list) → InRoom (waiting) → InGame (playing)
//
// DESIGN PRINCIPLE — Deterministic paths (GameState mutation):
//   All GameState mutations go through one path driven by FrameData:
//   OnFrame → TDSimulation.Tick → ApplyInputs
//   No direct GameState mutation outside frame processing.
//
// SILENT WHEN IDLE (Red Line #5):
//   Input is only sent when the player clicks a cell to place/upgrade/sell a tower.
//   Idle frames send nothing. Tower placement is immediate (Red Line #4).

using System.Collections.Generic;
using UnityEngine;
using BoomNetwork.Client.FrameSync;
using BoomNetwork.Core.FrameSync;
using BoomNetwork.Unity;

namespace BoomNetwork.Samples.TowerDefense
{
    [RequireComponent(typeof(BoomNetworkManager))]
    public class TDNetworkManager : MonoBehaviour
    {
        // ==================== State Machine ====================

        enum UIState { Connecting, Lobby, InRoom, InGame }
        UIState _uiState = UIState.Connecting;

        // ==================== Fields ====================

        BoomNetworkManager _network;
        TDSimulation _sim;
        TDRenderer _renderer;

        readonly byte[] _inputBuf = new byte[TDInput.InputSize];
        bool _syncing;
        bool _snapshotLoaded;
        bool _desyncDetected;
        uint _desyncFrame;
        bool _gameOver;
        bool _isRestarting;

        // Lobby state
        RoomInfo[] _rooms = new RoomInfo[0];
        bool _fetchingRooms;

        // InRoom state
        readonly List<int> _roomPlayers = new List<int>();

        // InGame cell selection
        int _selectedGx = -1, _selectedGy = -1;
        Rect _menuRect;
        int _mySlot = -1;
        bool _menuDidSlowDown;

        // Cached GUIStyles (built once)
        bool _stylesCached;
        GUIStyle _boxStyle, _titleStyle, _labelStyle, _btnStyle, _smallStyle,
                 _btnSelectedStyle, _btnDimStyle, _btnRedStyle;

        static readonly string[] TowerNames = { "", "弓箭塔", "炮台", "魔法塔", "冰霜塔", "狙击塔", "堡垒炮", "风暴塔" };
        static readonly string[] TowerIcons = { "", "↑", "●", "★", "❄", "◎", "⬡", "⚡" };
        static readonly int[]    TowerCosts = { 0,
            GameState.ArrowCost,   GameState.CannonCost,  GameState.MagicCost,
            GameState.IceCost,     GameState.SniperCost,
            GameState.FortressCost, GameState.StormCost };

        // ==================== Unity Lifecycle ====================

        void Start()
        {
            _sim     = new TDSimulation();
            _network = GetComponent<BoomNetworkManager>();

            // Connect() creates the Client if it doesn't exist yet, then connects.
            // Wire events after to ensure Client is non-null.
            _network.Connect();
            var c = _network.Client;

            c.OnConnected      += OnConnected;
            c.OnFrameSyncStart += OnFrameSyncStart;
            c.OnFrameSyncStop  += OnFrameSyncStop;
            c.OnFrame          += OnFrame;
            c.OnJoinedRoom     += OnJoinedRoom;
            c.OnPlayerJoinedMsg += OnPlayerJoined;
            c.OnPlayerLeftMsg   += OnPlayerLeft;
            c.OnLeftRoom       += OnLeftRoom;
            c.OnTakeSnapshot    = TakeSnapshot;
            c.OnLoadSnapshot    = LoadSnapshot;
            c.OnDesyncDetected += OnDesync;
        }

        void Update()
        {
            if (_uiState != UIState.InGame || _renderer == null) return;

            if (Input.GetMouseButtonDown(0))
            {
                if (IsMouseOverMenu(Input.mousePosition)) return;

                if (_renderer.TryGetGridCell(Input.mousePosition, out int gx, out int gy))
                {
                    if (_selectedGx == gx && _selectedGy == gy)
                        ClearSelection();
                    else
                    {
                        _selectedGx = gx;
                        _selectedGy = gy;
                        if (_renderer != null) _renderer.SetCellHighlight(gx, gy);
                        if (!_menuDidSlowDown)
                        {
                            SendSpeedAction(TDInput.SpeedSlow);
                            _menuDidSlowDown = true;
                        }
                    }
                }
                else
                {
                    ClearSelection();
                }
            }
        }

        // ==================== Input Helpers ====================

        void ClearSelection()
        {
            if (_menuDidSlowDown)
            {
                SendSpeedAction(TDInput.SpeedNormal);
                _menuDidSlowDown = false;
            }
            _selectedGx = _selectedGy = -1;
            if (_renderer != null) _renderer.SetCellHighlight(-1, -1);
        }

        bool IsMouseOverMenu(Vector2 screenPos)
        {
            if (_selectedGx < 0) return false;
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

        void SendSpeedAction(byte speedMode)
        {
            TDInput.Encode(_inputBuf, speedMode, 0, TDInput.SpeedAction);
            _network.SendInput(_inputBuf);
        }

        void SendStartWave()
        {
            TDInput.Encode(_inputBuf, 0, 0, TDInput.StartWaveAction);
            _network.SendInput(_inputBuf);
        }

        void StartRestart()
        {
            _isRestarting = true;
            _network.Client.RequestStop();
        }

        void LeaveGame()
        {
            ClearSelection();
            _network.Client.LeaveRoom(); // → OnLeftRoom
        }

        // ==================== Network Events ====================

        void OnConnected()
        {
            _uiState = UIState.Lobby;
            FetchRooms();
        }

        void FetchRooms()
        {
            if (_fetchingRooms) return;
            _fetchingRooms = true;
            _network.Client.GetRooms(rooms =>
            {
                _rooms = rooms ?? new RoomInfo[0];
                _fetchingRooms = false;
            });
        }

        void OnJoinedRoom(int roomId, int[] existingPlayerIds)
        {
            _uiState = UIState.InRoom;
            _roomPlayers.Clear();
            foreach (var pid in existingPlayerIds) _roomPlayers.Add(pid);
            _roomPlayers.Add(_network.PlayerId);
            Debug.Log($"[TD] Joined room {roomId}, existing={existingPlayerIds.Length}");
        }

        void OnPlayerJoined(int pid)
        {
            if (_uiState == UIState.InRoom && !_roomPlayers.Contains(pid))
                _roomPlayers.Add(pid);
            Debug.Log($"[TD] Player {pid} joined");
        }

        void OnPlayerLeft(int pid)
        {
            _roomPlayers.Remove(pid);
            Debug.Log($"[TD] Player {pid} left");
        }

        void OnLeftRoom(int oldPlayerId)
        {
            _uiState       = UIState.Lobby;
            _syncing       = false;
            _gameOver      = false;
            _desyncDetected = false;
            _snapshotLoaded = false;
            _mySlot        = -1;
            _roomPlayers.Clear();
            ClearSelection();
            FetchRooms();
            Debug.Log($"[TD] Left room. Was pid={oldPlayerId}");
        }

        void OnFrameSyncStart(FrameSyncInitData init)
        {
            uint seed = (uint)(init.StartTime & 0xFFFFFFFF);

            if (!_snapshotLoaded)
                _sim.Init(seed);

            _mySlot   = _sim.LookupSlot(_network.PlayerId);
            _syncing  = true;
            _uiState  = UIState.InGame;
            _gameOver = false;

            _renderer = GetComponent<TDRenderer>();
            if (_renderer == null) _renderer = gameObject.AddComponent<TDRenderer>();
            _renderer.Init(_sim.State);

            Debug.Log($"[TD] FrameSync started. Pid={_network.PlayerId} snapshot={_snapshotLoaded} fps={init.FrameRate}");
        }

        void OnFrameSyncStop()
        {
            _syncing = false;

            if (_isRestarting)
            {
                _isRestarting   = false;
                _gameOver       = false;
                _desyncDetected = false;
                _snapshotLoaded = false;
                _mySlot         = -1;
                ClearSelection();
                _network.Client.RequestStart();
                Debug.Log("[TD] Restart requested — waiting for OnFrameSyncStart");
                return;
            }

            // Non-restart stop (e.g. host disconnect) → back to InRoom
            _uiState = UIState.InRoom;
        }

        void OnFrame(FrameData frame)
        {
            if (_desyncDetected || _gameOver) return;

            _sim.Tick(frame);
            if (_mySlot < 0) _mySlot = _sim.LookupSlot(_network.PlayerId);
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
            _mySlot = _sim.LookupSlot(_network.PlayerId);
            if (_renderer != null) _renderer.SyncVisuals();
            Debug.Log($"[TD] Snapshot loaded. Frame={_sim.State.FrameNumber} Wave={_sim.State.Wave.WaveNumber}");
        }

        // ==================== OnGUI ====================

        void OnGUI()
        {
            CacheStyles();

            switch (_uiState)
            {
                case UIState.Connecting: DrawConnectingUI(); break;
                case UIState.Lobby:      DrawLobbyUI();      break;
                case UIState.InRoom:     DrawRoomBadge(); DrawInRoomUI();  break;
                case UIState.InGame:
                    DrawRoomBadge();
                    DrawStatusHUD();
                    DrawTopRightHUD();
                    if (_selectedGx >= 0) DrawCellMenu();
                    DrawDesyncOverlay();
                    DrawGameOverOverlay();
                    break;
            }
        }

        // ==================== Style Cache ====================

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
            _btnRedStyle = new GUIStyle(_btnStyle)
                { normal = { textColor = new Color(1f, 0.4f, 0.4f) } };
        }

        // ==================== Connecting UI ====================

        void DrawConnectingUI()
        {
            var style = new GUIStyle(GUI.skin.label)
                { fontSize = 18, alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } };
            GUI.Label(new Rect(0, 0, Screen.width, Screen.height), "连接中…", style);
        }

        // ==================== Lobby UI ====================

        void DrawLobbyUI()
        {
            const float W      = 560f;
            const float RowH   = 34f;
            const float Pad    = 10f;

            int count = _rooms.Length;

            // ── Pre-calculate total height ────────────────────────────────
            float titleH  = 32f;
            float colH    = (!_fetchingRooms && count > 0) ? RowH : 0f; // column-header row
            float bodyH   = RowH;                                         // empty/loading msg OR rows
            if (!_fetchingRooms && count > 0) bodyH = count * RowH;
            float footerH = 44f;
            float totalH  = Pad + titleH + Pad + colH + bodyH + Pad + footerH + Pad;

            float px = (Screen.width  - W) / 2f;
            float py = (Screen.height - totalH) / 2f;

            GUI.Box(new Rect(px, py, W, totalH), "", _boxStyle);

            // ── Draw sequentially, advancing y after each element ─────────
            float y = py + Pad;

            // Title
            GUI.Label(new Rect(px + Pad, y, W - Pad * 2, titleH),
                "<b>塔防守卫  —  房间列表</b>", _titleStyle);
            y += titleH + Pad;

            // Body
            if (_fetchingRooms)
            {
                GUI.Label(new Rect(px + Pad, y, W - Pad * 2, RowH), "加载中…", _labelStyle);
                y += RowH;
            }
            else if (count == 0)
            {
                GUI.Label(new Rect(px + Pad, y, W - Pad * 2, RowH),
                    "<color=#888888>暂无房间，点「创建房间」开始</color>", _labelStyle);
                y += RowH;
            }
            else
            {
                // Column headers
                GUI.Label(new Rect(px + Pad,       y, 80,  RowH), "<b>房间</b>",  _labelStyle);
                GUI.Label(new Rect(px + Pad + 90,  y, 100, RowH), "<b>人数</b>",  _labelStyle);
                GUI.Label(new Rect(px + Pad + 200, y, 120, RowH), "<b>状态</b>",  _labelStyle);
                y += RowH;

                foreach (var room in _rooms)
                {
                    bool full    = room.PlayerCount >= room.MaxPlayers;
                    bool canJoin = !room.Running && !full;
                    string status = room.Running ? "<color=#ffaa44>游戏中</color>"
                                  : full         ? "<color=#ff6666>已满</color>"
                                  :                "<color=#88ff88>等待中</color>";

                    GUI.Label(new Rect(px + Pad,       y, 80,  RowH), $"#{room.RoomId}",                      _labelStyle);
                    GUI.Label(new Rect(px + Pad + 90,  y, 100, RowH), $"{room.PlayerCount}/{room.MaxPlayers}", _labelStyle);
                    GUI.Label(new Rect(px + Pad + 200, y, 120, RowH), status,                                  _labelStyle);

                    if (GUI.Button(new Rect(px + W - Pad - 80, y + 2, 80, RowH - 4), "加入",
                            canJoin ? _btnStyle : _btnDimStyle))
                    {
                        if (canJoin) _network.Client.JoinRoom(room.RoomId);
                    }
                    y += RowH;
                }
            }

            y += Pad; // gap before footer

            // Footer: create + refresh (always at computed y, never overlaps)
            if (GUI.Button(new Rect(px + Pad, y, 160, footerH - 8), "＋ 创建房间", _btnStyle))
                _network.Client.CreateAndJoinRoom(4);

            if (GUI.Button(new Rect(px + W - Pad - 90, y, 90, footerH - 8), "刷新",
                    _fetchingRooms ? _btnDimStyle : _btnStyle))
            {
                if (!_fetchingRooms) FetchRooms();
            }
        }

        // ==================== InRoom UI ====================

        void DrawInRoomUI()
        {
            float w = 400, h = 220;
            float px = (Screen.width - w) / 2f;
            float py = (Screen.height - h) / 2f;
            GUI.Box(new Rect(px, py, w, h), "", _boxStyle);

            float y = py + 10;

            GUI.Label(new Rect(px + 10, y, w - 20, 24),
                $"<b>房间 #{_network.Client.RoomId}  —  等待开始</b>", _titleStyle);
            y += 28;

            GUI.Label(new Rect(px + 10, y, w - 20, 20),
                $"玩家 ({_roomPlayers.Count}/{4})：", _labelStyle);
            y += 22;

            foreach (var pid in _roomPlayers)
            {
                string me = pid == _network.PlayerId ? " <color=#ffdd44>（我）</color>" : "";
                GUI.Label(new Rect(px + 20, y, w - 40, 20), $"• 玩家 {pid}{me}", _labelStyle);
                y += 22;
            }

            y = py + h - 86;

            // Start game button
            if (GUI.Button(new Rect(px + 20, y, w - 40, 36), "▶ 开始游戏", _btnStyle))
                _network.Client.RequestStart();
            y += 44;

            // Leave room button
            if (GUI.Button(new Rect(px + 20, y, w - 40, 30), "← 退出房间", _btnDimStyle))
                _network.Client.LeaveRoom();
        }

        // ==================== Room Badge (top-left, InRoom + InGame) ====================

        void DrawRoomBadge()
        {
            int roomId = _network.Client.RoomId;
            var style = new GUIStyle(GUI.skin.label)
                { fontSize = 11, normal = { textColor = new Color(0.7f, 0.9f, 1f) } };
            GUI.Label(new Rect(10, 4, 120, 18), $"房间 #{roomId}", style);
        }

        // ==================== InGame HUD ====================

        void DrawStatusHUD()
        {
            var state = _sim.State;
            int alive = CountAliveEnemies();
            string waveInfo;
            if (state.Wave.AllWavesDone && alive == 0)
                waveInfo = "<color=yellow>所有波次已清除！</color>";
            else if (state.Wave.SpawnRemaining > 0)
                waveInfo = $"第 <b>{state.Wave.WaveNumber}</b>/{GameState.MaxWaves} 波  剩余敌人：{state.Wave.SpawnRemaining}";
            else
                waveInfo = $"第 <b>{state.Wave.WaveNumber}</b>/{GameState.MaxWaves} 波  下波倒计时：{state.Wave.InterWaveTimer / 30}s";

            float x = 10, y = 22, w = 340; // y=22 to clear the room badge
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

        void DrawTopRightHUD()
        {
            var state     = _sim.State;
            byte curSpd   = state.SpeedMode;
            bool betweenWaves = state.Wave.SpawnRemaining == 0 && !state.Wave.AllWavesDone
                                && state.Wave.WaveNumber < GameState.MaxWaves;

            const float BtnW = 72f, BtnH = 30f, Pad = 6f;
            // 4 buttons: x2, x3, 开始波次, 退出
            float startX = Screen.width - Pad - (BtnW + Pad) * 4;
            float y = Pad;

            bool is2x = curSpd == TDInput.Speed2x;
            if (GUI.Button(new Rect(startX, y, BtnW, BtnH), "<b>x2</b>",
                    is2x ? _btnSelectedStyle : _btnStyle))
                SendSpeedAction(is2x ? TDInput.SpeedNormal : TDInput.Speed2x);

            bool is3x = curSpd == TDInput.Speed3x;
            float x3 = startX + BtnW + Pad;
            if (GUI.Button(new Rect(x3, y, BtnW, BtnH), "<b>x3</b>",
                    is3x ? _btnSelectedStyle : _btnStyle))
                SendSpeedAction(is3x ? TDInput.SpeedNormal : TDInput.Speed3x);

            float xW = x3 + BtnW + Pad;
            string waveLabel = betweenWaves ? $"▶ 第{state.Wave.WaveNumber + 1}波" : "▶ 开始";
            if (GUI.Button(new Rect(xW, y, BtnW, BtnH), waveLabel,
                    betweenWaves ? _btnStyle : _btnDimStyle))
            {
                if (betweenWaves) SendStartWave();
            }

            // 退出游戏 (top-right corner)
            float xExit = xW + BtnW + Pad;
            if (GUI.Button(new Rect(xExit, y, BtnW, BtnH), "退出", _btnRedStyle))
                LeaveGame();
        }

        void DrawCellMenu()
        {
            int gx = _selectedGx, gy = _selectedGy;
            if (!GameState.IsInBounds(gx, gy)) return;

            ref var tower   = ref _sim.State.Grid[GameState.CellIndex(gx, gy)];
            bool hasTower   = tower.Type != TowerType.None;
            int myGoldMenu  = (_mySlot >= 0 && _mySlot < GameState.MaxPlayers) ? _sim.State.PlayerGold[_mySlot] : 0;
            int sharedGold  = _sim.State.SharedGold;

            Vector2 cellScreen = _renderer != null
                ? _renderer.GetCellScreenCenter(gx, gy)
                : new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);

            float guiX  = cellScreen.x;
            float guiY  = Screen.height - cellScreen.y;

            const float MenuW = 240f;
            float menuH = hasTower ? 150f : 380f;
            float px = Mathf.Clamp(guiX - MenuW * 0.5f, 5f, Screen.width  - MenuW - 5f);
            float py = Mathf.Clamp(guiY - menuH - 20f,  5f, Screen.height - menuH - 5f);

            _menuRect = new Rect(px, py, MenuW, menuH);
            GUI.Box(_menuRect, "", _boxStyle);

            float iy = py + 6;

            if (!hasTower)
            {
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
                bool isTeam   = GameState.IsTeamTower(tower.Type);
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
            GUI.Label(new Rect(px, py, w, h), $"<b>检测到不同步</b>\n第 {_desyncFrame} 帧", style);
        }

        void DrawGameOverOverlay()
        {
            if (!_gameOver) return;
            bool victory = _sim.IsVictory();
            string msg = victory
                ? "<color=yellow><b>胜利！</b></color>\n所有波次已通关！"
                : "<color=red><b>基地已失守</b></color>\n游戏结束";

            float w = 400, h = victory ? 120 : 170;
            float px = (Screen.width - w) / 2f;
            float py = Screen.height * 0.35f;
            GUI.Box(new Rect(px, py, w, h), "", _boxStyle);
            var style = new GUIStyle(GUI.skin.label)
                { fontSize = 22, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter,
                  normal = { textColor = Color.white }, richText = true };
            GUI.Label(new Rect(px, py, w, 70), msg, style);

            float btnY = py + 80;
            if (!victory)
            {
                if (GUI.Button(new Rect(px + 20, btnY, w - 40, 36), "再来一局", _btnStyle))
                    StartRestart();
                btnY += 44;
            }
            if (GUI.Button(new Rect(px + 20, btnY, w - 40, 36), "退出游戏", _btnRedStyle))
                LeaveGame();
        }

        // ==================== Helpers ====================

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
