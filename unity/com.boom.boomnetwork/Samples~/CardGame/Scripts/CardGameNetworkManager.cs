// BoomNetwork CardGame Demo — Network Manager + UI
//
// 流程：连接 → 房间大厅 → 房间内准备 → 房主开始游戏 → 对战
//
// 准备机制：通过 SendStateMessage 广播 Ready 状态（非持久），
//   新玩家加入时各方重新广播自己的状态以确保同步。
// 开始游戏：仅房主可点击，且需要房间内恰好 2 名玩家全部准备。

using System;
using System.Collections.Generic;
using UnityEngine;
using BoomNetwork.Client.FrameSync;
using BoomNetwork.Core.FrameSync;
using BoomNetwork.Unity;

namespace BoomNetwork.Samples.CardGame
{
    [RequireComponent(typeof(BoomNetworkManager))]
    public class CardGameNetworkManager : MonoBehaviour
    {
        const int MaxPlayers     = 2;
        const string MatchKey    = "cardgame";
        // StateMessage type byte for ready broadcast
        const byte MsgReady      = 0x52;

        BoomNetworkManager _net;
        CardGameSimulation _sim;

        // ── Lobby state ───────────────────────────────────────────────────────
        readonly List<int>      _roomPlayers = new();
        RoomInfo[]              _rooms       = Array.Empty<RoomInfo>();
        int                     _hostPlayerId = -1;
        readonly Dictionary<int, bool> _readyMap = new();
        bool                    _myReady;
        int                     _roomId = -1;
        string                  _connectStatus = "";
        Vector2                 _roomScroll;

        bool IsHost => _hostPlayerId == _net.PlayerId;
        bool AllReady
        {
            get
            {
                if (_roomPlayers.Count != MaxPlayers || _readyMap.Count != MaxPlayers) return false;
                foreach (var r in _readyMap.Values) if (!r) return false;
                return true;
            }
        }

        // ── Game state ────────────────────────────────────────────────────────
        int  _localSlot = -1;
        bool _syncing;
        bool _snapshotLoaded;

        // ── Hero select state ─────────────────────────────────────────────────
        HeroType _localHeroSelection = HeroType.None;
        bool     _heroConfirmSent;

        // ── Drag state ────────────────────────────────────────────────────────
        int     _dragCardIdx = -1;
        Vector2 _dragPos;
        bool    _dragging;
        const int DragCtrlId = 9001;

        // ── Pending action ────────────────────────────────────────────────────
        bool       _actionQueued;
        CardAction _queuedAction;
        int        _queuedCardIdx;

        // ── Recent card display ───────────────────────────────────────────────
        struct RecentCardEntry { public Card Card; public string Action; public float ExpireTime; }
        readonly List<RecentCardEntry> _recentCards = new();

        // ── Styles ────────────────────────────────────────────────────────────
        bool     _stylesCached;
        GUIStyle _bgStyle;
        GUIStyle _labelStyle, _titleStyle, _statusStyle, _btnStyle, _btnGreen, _btnRed;
        GUIStyle _cardNormal, _cardKill, _cardDodge, _cardBack, _cardEquip, _cardSpell;
        GUIStyle _hpBoxStyle, _infoBoxStyle, _panelStyle;
        GUIStyle _dropZone, _dropZoneHot;
        GUIStyle _readyStyle, _notReadyStyle;
        GUIStyle _equipFilled, _equipEmpty, _judgeZone, _heroStyle;
        GUIStyle _heroCardNormal, _heroCardSelected;
        GUIStyle _skillBtnActive, _skillBtnPassive;

        // ── Card layout ───────────────────────────────────────────────────────
        const float CW = 72f, CH = 100f, CGap = 10f, CStride = 82f;

        // ─────────────────────────────────────────────────────────────────────
        void Start()
        {
            _sim = new CardGameSimulation();
            _net = GetComponent<BoomNetworkManager>();
            _sim.OnCardEvent += (card, action) =>
                _recentCards.Add(new RecentCardEntry
                    { Card = card, Action = action, ExpireTime = Time.realtimeSinceStartup + 3f });
            var c = _net.Client;

            // ── Connection ────────────────────────────────────────────────────
            c.OnConnected += () =>
            {
                _connectStatus = "已连接";
                RefreshRooms();
            };
            c.OnDisconnected += () =>
            {
                _connectStatus = "已断线";
                ResetLobby();
            };

            // ── Room lifecycle ────────────────────────────────────────────────
            c.OnJoinedRoom += (roomId, existing) =>
            {
                _roomId = roomId;
                _roomPlayers.Clear();
                _roomPlayers.AddRange(existing);
                _roomPlayers.Add(_net.PlayerId);
                _hostPlayerId = existing.Length == 0 ? _net.PlayerId : existing[0];
                _readyMap.Clear();
                _myReady = false;

                // Register slot mapping in join order so both clients agree:
                //   existing players first (they joined earlier) → slot 0, 1...
                //   then self (joined last among known players at this moment).
                // This makes the room creator consistently slot 0 (first turn).
                foreach (var pid in existing) _sim.PidToSlot(pid);
                _sim.PidToSlot(_net.PlayerId);
            };
            c.OnPlayerJoinedMsg += pid =>
            {
                if (!_roomPlayers.Contains(pid)) _roomPlayers.Add(pid);
                // Register slot for any player who joins after us.
                _sim.PidToSlot(pid);
                // Re-broadcast my ready state so the new joiner is in sync.
                if (_myReady) BroadcastReady(true);
            };
            c.OnPlayerLeftMsg += pid =>
            {
                _roomPlayers.Remove(pid);
                _readyMap.Remove(pid);
            };
            c.OnHostChanged += newHost => _hostPlayerId = newHost;
            c.OnLeftRoom    += _ =>
            {
                ResetLobby();
                RefreshRooms();
            };

            // ── Ready state via StateMessage ──────────────────────────────────
            c.OnStateMessage += OnStateMessage;

            // ── Frame sync (game) ─────────────────────────────────────────────
            c.OnFrameSyncStart += OnFrameSyncStart;
            c.OnFrameSyncStop  += () => { _syncing = false; };
            c.OnFrame          += frame => _sim.Tick(frame);
            c.OnTakeSnapshot    = () => _syncing ? _sim.Serialize() : null;
            c.OnLoadSnapshot    = data => { _snapshotLoaded = true; _sim.Deserialize(data); };

            // Auto-connect on launch.
            _connectStatus = "连接中...";
            _net.Connect();
        }

        void Update()
        {
            // 过期清理
            float now = Time.realtimeSinceStartup;
            for (int i = _recentCards.Count - 1; i >= 0; i--)
                if (_recentCards[i].ExpireTime <= now)
                    _recentCards.RemoveAt(i);

            if (!_syncing || !_actionQueued) return;
            _net.SendInput(new byte[] { (byte)_queuedAction, (byte)_queuedCardIdx });
            _actionQueued = false;
        }

        // ── Frame sync callbacks ──────────────────────────────────────────────

        void OnFrameSyncStart(FrameSyncInitData init)
        {
            _localSlot          = _sim.PidToSlot(_net.PlayerId);
            _localHeroSelection = HeroType.None;
            _heroConfirmSent    = false;
            _recentCards.Clear();
            if (!_snapshotLoaded)
                _sim.Init((uint)(init.StartTime & 0xFFFFFFFF));
            _syncing = true;
        }

        void QueueAction(CardAction action, int cardIdx)
        {
            _queuedAction  = action;
            _queuedCardIdx = cardIdx;
            _actionQueued  = true;
        }

        // ── Ready system ──────────────────────────────────────────────────────

        // Message layout: [MsgReady:1][pid:4][isReady:1] = 6 bytes
        void BroadcastReady(bool ready)
        {
            var msg = new byte[6];
            msg[0] = MsgReady;
            BitConverter.TryWriteBytes(msg.AsSpan(1, 4), _net.PlayerId);
            msg[5] = ready ? (byte)1 : (byte)0;
            _net.Client.SendStateMessage(msg);
        }

        void OnStateMessage(int senderId, byte[] data)
        {
            if (data == null || data.Length < 6 || data[0] != MsgReady) return;
            int  pid   = BitConverter.ToInt32(data, 1);
            bool ready = data[5] == 1;
            if (ready) _readyMap[pid]  = true;
            else       _readyMap.Remove(pid);
        }

        void ToggleReady()
        {
            _myReady = !_myReady;
            BroadcastReady(_myReady);
            // Update local map immediately so local display is instant.
            if (_myReady) _readyMap[_net.PlayerId]  = true;
            else          _readyMap.Remove(_net.PlayerId);
        }

        // ── Misc helpers ──────────────────────────────────────────────────────

        void RefreshRooms() =>
            _net.Client.GetRooms(rooms => _rooms = rooms ?? Array.Empty<RoomInfo>());

        void ResetLobby()
        {
            _roomPlayers.Clear();
            _readyMap.Clear();
            _myReady      = false;
            _hostPlayerId = -1;
            _roomId       = -1;
            _syncing      = false;
            _localSlot    = -1;
            _snapshotLoaded = false;
            _dragging             = false;
            _dragCardIdx          = -1;
            _actionQueued         = false;
            _localHeroSelection   = HeroType.None;
            _heroConfirmSent      = false;
        }

        // ─────────────────────────────────────────────────────────────────────
        // OnGUI — routing
        // ─────────────────────────────────────────────────────────────────────

        void OnGUI()
        {
            CacheStyles();
            GUI.Box(new Rect(0, 0, Screen.width, Screen.height), GUIContent.none, _bgStyle);

            var state = _net.Client.CurrentState;

            if (_syncing)
            {
                DrawGame();
                return;
            }

            switch (state)
            {
                case FrameSyncClient.State.Disconnected:
                case FrameSyncClient.State.Connecting:
                    DrawConnectScreen();
                    break;
                case FrameSyncClient.State.Connected:
                    DrawRoomListScreen();
                    break;
                case FrameSyncClient.State.InRoom:
                    DrawLobbyScreen();
                    break;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Lobby screens
        // ─────────────────────────────────────────────────────────────────────

        void DrawConnectScreen()
        {
            float w = 360f, h = 140f;
            float x = (Screen.width  - w) / 2f;
            float y = (Screen.height - h) / 2f;

            GUI.Box(new Rect(x, y, w, h), GUIContent.none, _panelStyle);
            GUI.Label(new Rect(x, y + 10f, w, 36f), "三国杀 — 联机对战", _titleStyle);
            GUI.Label(new Rect(x, y + 50f, w, 28f), _connectStatus, _statusStyle);

            var state = _net.Client.CurrentState;
            if (state == FrameSyncClient.State.Disconnected)
            {
                if (GUI.Button(new Rect(x + w / 2f - 70f, y + 90f, 140f, 36f), "重新连接", _btnStyle))
                {
                    _connectStatus = "连接中...";
                    _net.Connect();
                }
            }
        }

        void DrawRoomListScreen()
        {
            float w = 480f, h = 400f;
            float x = (Screen.width  - w) / 2f;
            float y = (Screen.height - h) / 2f;

            GUI.Box(new Rect(x, y, w, h), GUIContent.none, _panelStyle);

            // Title
            GUI.Label(new Rect(x, y + 10f, w, 36f), "房间大厅", _titleStyle);
            GUI.Label(new Rect(x + 10f, y + 46f, w - 20f, 22f),
                      $"玩家ID: {_net.PlayerId}  |  {_connectStatus}", _labelStyle);

            // Room list header
            GUI.Label(new Rect(x + 10f, y + 74f, w - 20f, 22f),
                      $"可加入的房间 ({_rooms.Length}):", _labelStyle);

            // Scrollable room list
            Rect listRect   = new Rect(x + 10f, y + 96f, w - 20f, 180f);
            Rect contentRect = new Rect(0, 0, listRect.width - 20f, Mathf.Max(180f, _rooms.Length * 44f));
            _roomScroll = GUI.BeginScrollView(listRect, _roomScroll, contentRect);

            if (_rooms.Length == 0)
            {
                GUI.Label(new Rect(0, 10f, contentRect.width, 30f), "暂无房间，请创建", _labelStyle);
            }
            else
            {
                for (int i = 0; i < _rooms.Length; i++)
                {
                    var r    = _rooms[i];
                    float ry = i * 44f;
                    bool full = r.PlayerCount >= r.MaxPlayers;
                    string state = r.Running ? "游戏中" : (full ? "已满" : "等待中");
                    GUI.Label(new Rect(0, ry + 8f, contentRect.width - 90f, 28f),
                              $"房间 #{r.RoomId}  {r.PlayerCount}/{r.MaxPlayers}  [{state}]",
                              _labelStyle);
                    if (!r.Running && !full)
                    {
                        if (GUI.Button(new Rect(contentRect.width - 85f, ry + 4f, 80f, 32f), "加入", _btnStyle))
                            _net.Client.JoinRoom(r.RoomId);
                    }
                }
            }

            GUI.EndScrollView();

            // Action buttons
            float btnY = y + h - 65f;
            if (GUI.Button(new Rect(x + 10f,       btnY, 140f, 44f), "刷新房间", _btnStyle))
                RefreshRooms();
            if (GUI.Button(new Rect(x + 165f,      btnY, 140f, 44f), "创建房间", _btnGreen))
                _net.Client.CreateRoom(MaxPlayers, _ => RefreshRooms());
            if (GUI.Button(new Rect(x + 320f,      btnY, 140f, 44f), "快速匹配", _btnStyle))
                _net.Client.MatchRoom(MaxPlayers, MatchKey);
        }

        void DrawLobbyScreen()
        {
            float w = 420f, h = 340f;
            float x = (Screen.width  - w) / 2f;
            float y = (Screen.height - h) / 2f;

            GUI.Box(new Rect(x, y, w, h), GUIContent.none, _panelStyle);

            GUI.Label(new Rect(x, y + 10f, w, 36f), $"房间 #{_roomId}", _titleStyle);
            GUI.Label(new Rect(x + 10f, y + 46f, w - 20f, 22f),
                      $"玩家ID: {_net.PlayerId}  |  等待 {MaxPlayers} 人全部准备", _labelStyle);

            // Player list
            float listY = y + 76f;
            for (int i = 0; i < _roomPlayers.Count; i++)
            {
                int  pid    = _roomPlayers[i];
                bool ready  = _readyMap.TryGetValue(pid, out bool r) && r;
                bool isMe   = pid == _net.PlayerId;
                bool isHost = pid == _hostPlayerId;

                string tag  = (isHost ? " [房主]" : "") + (isMe ? " (你)" : "");
                GUI.Label(new Rect(x + 20f, listY, 220f, 32f),
                          $"玩家 {pid}{tag}", _titleStyle);
                GUI.Label(new Rect(x + 250f, listY, 140f, 32f),
                          ready ? "✓ 已准备" : "○ 未准备",
                          ready ? _readyStyle : _notReadyStyle);
                listY += 36f;
            }

            // Waiting hint when < 2 players
            if (_roomPlayers.Count < MaxPlayers)
            {
                GUI.Label(new Rect(x + 10f, listY + 6f, w - 20f, 28f),
                          "等待另一名玩家加入...", _labelStyle);
                listY += 34f;
            }

            // Ready toggle button
            float btnAreaY = y + h - 90f;
            string readyLabel = _myReady ? "取消准备" : "准备";
            GUIStyle readyBtnStyle = _myReady ? _btnRed : _btnGreen;
            if (GUI.Button(new Rect(x + 20f, btnAreaY, 160f, 46f), readyLabel, readyBtnStyle))
                ToggleReady();

            // Start Game — host only, all ready required
            if (IsHost)
            {
                bool canStart = AllReady;
                GUI.enabled = canStart;
                if (GUI.Button(new Rect(x + 200f, btnAreaY, 180f, 46f), "开始游戏 ▶", _btnGreen))
                    _net.Client.RequestStart();
                GUI.enabled = true;

                if (!canStart)
                    GUI.Label(new Rect(x + 200f, btnAreaY + 50f, 180f, 24f),
                              "等待所有人准备", _labelStyle);
            }

            // Leave room
            if (GUI.Button(new Rect(x + w - 110f, y + h - 36f, 100f, 28f), "离开房间", _btnStyle))
                _net.Client.LeaveRoom();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Game screen
        // ─────────────────────────────────────────────────────────────────────

        void DrawGame()
        {
            if (_localSlot < 0)
            {
                CenterLabel("初始化中...");
                return;
            }

            if (_sim.State.InHeroSelect)
            {
                DrawHeroSelectScreen();
                return;
            }

            var s   = _sim.State;
            float cx = Screen.width  / 2f;
            float cy = Screen.height / 2f;
            int   opp = 1 - _localSlot;

            // ── Opponent ──────────────────────────────────────────────────────
            float oppInfoY  = 10f;
            float oppEquipY = oppInfoY  + 64f;   // 装备区（信息栏 58px + 6px 间距）
            float oppHandY  = oppEquipY + 58f;   // 手牌（背面）
            float oppJudgeY = oppHandY  + CH + 6f; // 判定区

            DrawPlayerInfo(opp, cx, oppInfoY, "对手");
            DrawEquipZone(opp, cx, oppEquipY);
            DrawHand(opp, cx, oppHandY, faceUp: false);
            DrawJudgmentZone(opp, cx, oppJudgeY);

            // ── Drop zone：拖到此处出牌（所有可用牌均适用） ─────────────────
            Rect     dropZone    = new Rect(cx - 220f, oppHandY - 8f, 440f, CH + 16f);
            Card dragCard = (_dragging && _dragCardIdx >= 0
                          && _dragCardIdx < s.Players[_localSlot].Hand.Count)
                ? s.Players[_localSlot].Hand[_dragCardIdx]
                : Card.None;
            var  localCtx = new UseContext(s, _localSlot);
            bool canDrag  = !dragCard.IsNone
                         && !s.WaitingForGuoHe
                         && (CardRegistry.CanUse(dragCard.Type, localCtx)
                             || (s.WuShengActive && dragCard.IsRed));

            if (canDrag)
            {
                bool   hot      = dropZone.Contains(_dragPos);
                string cardName = CardRegistry.GetName(dragCard.Type);
                GUI.Box(dropZone,
                        hot ? $"松手打出【{cardName}】！" : $"拖到上方打出【{cardName}】",
                        hot ? _dropZoneHot : _dropZone);
            }

            // ── Middle info ───────────────────────────────────────────────────
            DrawMiddle(cx, cy, s);

            // ── Self ──────────────────────────────────────────────────────────
            float myInfoY  = Screen.height - 64f;  // 信息栏 58px，留 6px 底边距
            float myJudgeY = myInfoY  - 32f;
            float myEquipY = myJudgeY - 58f;
            float myHandY  = myEquipY - CH - 6f;

            DrawJudgmentZone(_localSlot, cx, myJudgeY);
            DrawEquipZone(_localSlot, cx, myEquipY);
            DrawHand(_localSlot, cx, myHandY, faceUp: true);
            DrawPlayerInfo(_localSlot, cx, myInfoY, "你");

            // ── Floating dragged card ─────────────────────────────────────────
            if (_dragging && _dragCardIdx >= 0 && _dragCardIdx < s.Players[_localSlot].Hand.Count)
                DrawCardAt(s.Players[_localSlot].Hand[_dragCardIdx],
                           _dragPos.x - CW / 2f, _dragPos.y - CH / 2f, playable: true);

            // ── 技能按钮区 ────────────────────────────────────────────────────
            DrawSkillButtons(myHandY, s);

            // ── 最近出牌/弃牌/判定展示 ────────────────────────────────────────
            DrawRecentCards();

            // ── 过河拆桥选牌面板（覆盖层，在最上层绘制） ─────────────────────
            DrawGuoHeSelectPanel(s);

            HandleDrag(dropZone, canDrag, s);
        }

        // ── Hero Select Screen ────────────────────────────────────────────────

        void DrawHeroSelectScreen()
        {
            var  s            = _sim.State;
            int  opp          = 1 - _localSlot;
            bool myConfirmed  = s.HeroConfirmed[_localSlot];
            bool oppConfirmed = s.HeroConfirmed[opp];

            float pw = 680f, ph = 460f;
            float px = (Screen.width  - pw) / 2f;
            float py = (Screen.height - ph) / 2f;

            GUI.Box(new Rect(px, py, pw, ph), GUIContent.none, _panelStyle);
            GUI.Label(new Rect(px, py + 10f, pw, 36f), "选择武将", _titleStyle);

            // ── Hero cards ───────────────────────────────────────────────────
            var heroes = HeroRegistry.Selectable;
            const float HCW = 140f, HCH = 200f, HCGap = 20f;
            float totalW = heroes.Length * HCW + (heroes.Length - 1) * HCGap;
            float cardsX = px + (pw - totalW) / 2f;
            float cardsY = py + 56f;

            for (int i = 0; i < heroes.Length; i++)
            {
                var    heroType   = heroes[i];
                var    hero       = HeroRegistry.Get(heroType);
                float  cx         = cardsX + i * (HCW + HCGap);
                bool   isSelected = _localHeroSelection == heroType;
                var    cardStyle  = isSelected ? _heroCardSelected : _heroCardNormal;

                if (!myConfirmed && GUI.Button(new Rect(cx, cardsY, HCW, HCH), GUIContent.none, cardStyle))
                    _localHeroSelection = heroType;
                else
                    GUI.Box(new Rect(cx, cardsY, HCW, HCH), GUIContent.none, cardStyle);

                // Hero info
                GUI.Label(new Rect(cx + 6f, cardsY + 8f,  HCW - 12f, 30f), hero.Name,  _titleStyle);
                if (!string.IsNullOrEmpty(hero.Title))
                    GUI.Label(new Rect(cx + 6f, cardsY + 38f, HCW - 12f, 22f), hero.Title, _labelStyle);

                if (!string.IsNullOrEmpty(hero.SkillName))
                {
                    GUI.Label(new Rect(cx + 6f, cardsY + 64f, HCW - 12f, 22f),
                              $"【{hero.SkillName}】", _heroStyle);
                    GUI.Label(new Rect(cx + 6f, cardsY + 86f, HCW - 12f, 100f),
                              hero.SkillDesc, _labelStyle);
                }
                else
                {
                    GUI.Label(new Rect(cx + 6f, cardsY + 64f, HCW - 12f, 22f),
                              "（无技能）", _labelStyle);
                }

                if (isSelected)
                    GUI.Label(new Rect(cx, cardsY + HCH - 28f, HCW, 24f), "▼ 已选择", _heroStyle);
            }

            // ── Status row ───────────────────────────────────────────────────
            float statusY = cardsY + HCH + 16f;

            string myStatus  = myConfirmed
                ? $"✓ 你已确认：{HeroRegistry.Get(s.Players[_localSlot].Hero).Name}"
                : "请选择武将，然后点击「确认」";
            string oppStatus = oppConfirmed
                ? $"✓ 对手已确认"
                : "○ 对手选择中...";

            GUI.Label(new Rect(px + 20f, statusY,       pw / 2f - 30f, 28f), myStatus,  _labelStyle);
            GUI.Label(new Rect(px + pw / 2f, statusY,   pw / 2f - 20f, 28f), oppStatus, _labelStyle);

            // ── Confirm button ───────────────────────────────────────────────
            if (!myConfirmed)
            {
                if (GUI.Button(new Rect(px + pw / 2f - 90f, statusY + 34f, 180f, 46f),
                               "确认选择", _btnGreen))
                {
                    if (!_heroConfirmSent)
                    {
                        _heroConfirmSent = true;
                        QueueAction(CardAction.SelectHero, (int)_localHeroSelection);
                    }
                }
            }
            else
            {
                GUI.Label(new Rect(px + pw / 2f - 140f, statusY + 34f, 280f, 46f),
                          "等待对手确认...", _statusStyle);
            }
        }

        void DrawPlayerInfo(int slot, float cx, float y, string label)
        {
            var  p    = _sim.State.Players[slot];
            var  hero = HeroRegistry.Get(p.Hero);
            bool act  = _sim.State.ActiveSlot == slot && !_sim.State.IsGameOver;
            string turn = act ? "  ◄ 行动中" : "";
            string hp   = "";
            for (int i = 0; i < p.HP;              i++) hp += "♥ ";
            for (int i = p.HP; i < PlayerState.MaxHP; i++) hp += "♡ ";

            GUI.Box(new Rect(cx - 210f, y, 420f, 58f), GUIContent.none, _hpBoxStyle);
            GUI.Label(new Rect(cx - 200f, y + 2f,  220f, 28f), $"{label} (P{slot + 1}){turn}", _titleStyle);
            GUI.Label(new Rect(cx +  30f, y + 2f,  170f, 28f), hp, _titleStyle);

            // 武将信息
            string heroLine = hero.HeroType == HeroType.None
                ? "<color=#888888>无名将</color>"
                : $"<color=#FFD700>{hero.Name}</color>  " +
                  $"<color=#AADDFF>【{hero.SkillName}】</color>" +
                  $"<color=#CCCCCC>{hero.SkillDesc}</color>";
            GUI.Label(new Rect(cx - 200f, y + 30f, 400f, 24f), heroLine, _heroStyle);
        }

        void DrawHand(int slot, float cx, float y, bool faceUp)
        {
            var  hand    = _sim.State.Players[slot].Hand;
            bool isLocal = slot == _localSlot;
            var  s       = _sim.State;

            if (hand.Count == 0)
            {
                GUI.Label(new Rect(cx - 60f, y + 30f, 120f, 30f), "(无手牌)", _labelStyle);
                return;
            }

            // 用 CardRegistry 统一判断 PlayCard 合法性（含连弩无限杀等效果）
            var  useCtx    = isLocal ? new UseContext(s, slot) : default;
            bool canDiscard = isLocal
                          && s.TurnPhase == TurnPhase.Discard && s.SubPhase == SubPhase.During
                          && s.ActiveSlot == _localSlot
                          && s.Players[slot].Hand.Count > s.HandLimit(slot);

            float totalW = hand.Count * CStride - CGap;
            float startX = cx - totalW / 2f;

            for (int i = 0; i < hand.Count; i++)
            {
                if (_dragging && isLocal && i == _dragCardIdx) continue;

                float x    = startX + i * CStride;
                var   rect = new Rect(x, y, CW, CH);

                if (!faceUp) { GUI.Box(rect, "?", _cardBack); continue; }

                var  card       = hand[i];
                bool isEquip      = CardRegistry.Get(card.Type) is EquipmentDefinition;
                bool canPlayAsKill = isLocal && s.WuShengActive && card.IsRed;
                bool canPlay      = isLocal && (CardRegistry.CanUse(card.Type, useCtx) || canPlayAsKill);
                bool playable   = canPlay || canDiscard;
                DrawCardAt(card, x, y, playable);

                if (playable && Event.current.type == EventType.MouseDown
                    && rect.Contains(Event.current.mousePosition))
                {
                    if (canDiscard && !canPlay)
                    {
                        // 弃牌阶段：点击直接弃牌
                        QueueAction(CardAction.DiscardCard, i);
                        Event.current.Use();
                    }
                    else
                    {
                        // 所有可出的牌均需拖拽到上方出牌区打出
                        _dragCardIdx          = i;
                        _dragging             = true;
                        _dragPos              = Event.current.mousePosition;
                        GUIUtility.hotControl = DragCtrlId;
                        Event.current.Use();
                    }
                }
            }
        }

        static readonly string[] EquipSlotLabels = { "武器", "防具", "进攻马", "防御马" };
        const float EW = 100f, EH = 46f, EGap = 6f;

        void DrawEquipZone(int slot, float cx, float y)
        {
            var p    = _sim.State.Players[slot];
            float totalW = 4 * EW + 3 * EGap;
            float startX = cx - totalW / 2f;

            for (int e = 0; e < 4; e++)
            {
                float    ex    = startX + e * (EW + EGap);
                var      equip = (EquipSlot)e;
                bool     has   = p.HasEquip(equip);
                Card     ec    = p.GetEquip(equip);
                string   label = has
                    ? $"{EquipSlotLabels[e]}\n{CardRegistry.GetName(ec.Type)} {ec.SuitSymbol}{ec.RankName}"
                    : $"[{EquipSlotLabels[e]}]";
                GUI.Box(new Rect(ex, y, EW, EH), label, has ? _equipFilled : _equipEmpty);
            }
        }

        void DrawJudgmentZone(int slot, float cx, float y)
        {
            var cards = _sim.State.Players[slot].Judgment;
            float w   = 440f, h = 26f;
            float x   = cx - w / 2f;
            string content = cards.Count == 0
                ? "判定区：(空)"
                : "判定区：" + string.Join(" ", cards.ConvertAll(c => $"{c.SuitSymbol}{c.RankName}{CardRegistry.GetName(c.Type)}"));
            GUI.Box(new Rect(x, y, w, h), content, _judgeZone);
        }

        // ── 过河拆桥选牌面板 ─────────────────────────────────────────────────

        void DrawGuoHeSelectPanel(CardGameState s)
        {
            if (!s.WaitingForGuoHe) return;

            // 非操作方：显示等待提示
            if (s.GuoHeActorSlot != _localSlot)
            {
                float lw = 400f, lh = 48f;
                float lx = (Screen.width - lw) / 2f;
                float ly = Screen.height / 2f - lh / 2f;
                GUI.Box(new Rect(lx, ly, lw, lh), "等待对手选择要弃置的牌...", _infoBoxStyle);
                return;
            }

            int targetSlot = s.GuoHeTargetSlot;
            var target     = s.Players[targetSlot];

            bool hasHand     = target.Hand.Count > 0;
            bool hasEquip    = System.Array.Exists(target.Equips, e => !e.IsNone);
            bool hasJudgment = target.Judgment.Count > 0;
            int  sections    = (hasHand ? 1 : 0) + (hasEquip ? 1 : 0) + (hasJudgment ? 1 : 0);

            float ph  = 64f + sections * (CH + 44f);
            float pw  = 620f;
            float px  = (Screen.width  - pw) / 2f;
            float py  = (Screen.height - ph) / 2f;

            // 半透明遮罩
            var oldColor = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.Box(new Rect(0, 0, Screen.width, Screen.height), GUIContent.none, _bgStyle);
            GUI.color = oldColor;

            GUI.Box(new Rect(px, py, pw, ph), GUIContent.none, _panelStyle);
            GUI.Label(new Rect(px, py + 10f, pw, 32f), "过河拆桥 — 选择要弃置的牌", _titleStyle);

            float rowY = py + 50f;
            const float labelW = 60f;

            if (hasHand)
            {
                GUI.Label(new Rect(px + 12f, rowY + (CH - 22f) / 2f, labelW, 22f), "手牌", _labelStyle);
                float startX = px + 12f + labelW + 8f;
                for (int i = 0; i < target.Hand.Count; i++)
                {
                    float ex = startX + i * CStride;
                    if (GUI.Button(new Rect(ex, rowY, CW, CH), "?", _cardBack))
                        QueueAction(CardAction.SelectTargetCard, 0x00 | i);
                }
                rowY += CH + 12f;
            }

            if (hasEquip)
            {
                GUI.Label(new Rect(px + 12f, rowY + (CH - 22f) / 2f, labelW, 22f), "装备区", _labelStyle);
                float startX = px + 12f + labelW + 8f;
                int col = 0;
                for (int e = 0; e < 4; e++)
                {
                    var equip = target.Equips[e];
                    if (equip.IsNone) continue;
                    float   ex    = startX + col * CStride;
                    bool    isRed = equip.Suit == Suit.Heart || equip.Suit == Suit.Diamond;
                    string  suit  = isRed
                        ? $"<color=#FF4444>{equip.SuitSymbol}</color>"
                        : equip.SuitSymbol;
                    string  label = $"{suit}{equip.RankName}\n{CardRegistry.GetName(equip.Type)}";
                    if (GUI.Button(new Rect(ex, rowY, CW, CH), label, _cardEquip))
                        QueueAction(CardAction.SelectTargetCard, 0x10 | e);
                    col++;
                }
                rowY += CH + 12f;
            }

            if (hasJudgment)
            {
                GUI.Label(new Rect(px + 12f, rowY + (CH - 22f) / 2f, labelW, 22f), "判定区", _labelStyle);
                float startX = px + 12f + labelW + 8f;
                for (int i = 0; i < target.Judgment.Count; i++)
                {
                    var    c     = target.Judgment[i];
                    float  ex    = startX + i * CStride;
                    bool   isRed = c.Suit == Suit.Heart || c.Suit == Suit.Diamond;
                    string suit  = isRed
                        ? $"<color=#FF4444>{c.SuitSymbol}</color>"
                        : c.SuitSymbol;
                    string label = $"{suit}{c.RankName}\n{CardRegistry.GetName(c.Type)}";
                    if (GUI.Button(new Rect(ex, rowY, CW, CH), label, _cardSpell))
                        QueueAction(CardAction.SelectTargetCard, 0x20 | i);
                }
            }
        }

        // ── 技能按钮区（右下角，与手牌行对齐）────────────────────────────────

        void DrawSkillButtons(float handY, CardGameState s)
        {
            if (_localSlot < 0) return;

            const float bw = 164f, bh = 58f;
            float bx = Screen.width - bw - 10f;

            // 遍历当前武将的技能（每位武将目前只有一个技能）
            var def = HeroRegistry.Get(s.Players[_localSlot].Hero);
            if (string.IsNullOrEmpty(def.SkillName)) return;

            bool isActive = def.SkillTiming == SkillTiming.ActiveTurn;
            string label  = $"<b>【{def.SkillName}】</b>\n{def.SkillDesc}";

            if (isActive)
            {
                if (def.IsPendingActivation(s, _localSlot))
                {
                    // 技能已发动，等待玩家选择红色牌
                    string pendingLabel = $"<b>【{def.SkillName}】</b>\n→ 请选择红色牌当杀";
                    GUI.Box(new Rect(bx, handY, bw, bh), pendingLabel, _skillBtnActive);
                }
                else
                {
                    bool canAct = s.TurnPhase == TurnPhase.Play
                               && s.SubPhase  == SubPhase.During
                               && !s.WaitingForResponse
                               && s.ActiveSlot == _localSlot
                               && def.CanActivate(s, _localSlot);
                    GUI.enabled = canAct;
                    if (GUI.Button(new Rect(bx, handY, bw, bh), label, _skillBtnActive))
                        QueueAction(CardAction.UseSkill, 0);
                    GUI.enabled = true;
                }
            }
            else
            {
                GUI.Box(new Rect(bx, handY, bw, bh), label, _skillBtnPassive);
            }
        }

        // ── 最近出牌展示（屏幕右侧，3 秒后消失）────────────────────────────────

        void DrawRecentCards()
        {
            if (_recentCards.Count == 0) return;

            // 每张牌占 CH 高度，间距 6px；按屏幕高度动态限制最多显示数量
            const float gap   = 6f;
            float entryH      = CH + gap;
            int   maxVisible  = Mathf.Max(1, Mathf.FloorToInt((Screen.height - 20f) / entryH));
            int   count       = Mathf.Min(_recentCards.Count, maxVisible);
            float totalH      = count * entryH - gap;
            float startY      = (Screen.height - totalH) / 2f;
            float x           = Screen.width - CW - 14f;

            // 从最新的开始显示（列表末尾 = 最新）
            int startIdx = _recentCards.Count - count;
            for (int i = 0; i < count; i++)
            {
                var   rc   = _recentCards[startIdx + i];
                float y    = startY + i * entryH;
                float fade = Mathf.Clamp01((rc.ExpireTime - Time.realtimeSinceStartup) / 0.6f);

                bool   isRed    = rc.Card.Suit == Suit.Heart || rc.Card.Suit == Suit.Diamond;
                string suitMark = isRed
                    ? $"<color=#FF4444>{rc.Card.SuitSymbol}</color>"
                    : rc.Card.SuitSymbol;
                string typeName = CardRegistry.GetName(rc.Card.Type);
                string label    = $"{suitMark}{rc.Card.RankName}\n{typeName}\n<size=11>{rc.Action}</size>";

                GUIStyle style = rc.Card.Type switch
                {
                    CardType.Kill  => _cardKill,
                    CardType.Dodge => _cardDodge,
                    _ => CardRegistry.Get(rc.Card.Type) is EquipmentDefinition
                             ? _cardEquip : _cardSpell,
                };

                var oldColor = GUI.color;
                GUI.color = new Color(1f, 1f, 1f, fade);
                GUI.Box(new Rect(x, y, CW, CH), label, style);
                GUI.color = oldColor;
            }
        }

        void DrawCardAt(Card card, float x, float y, bool playable)
        {
            GUIStyle style;
            string   typeName = CardRegistry.GetName(card.Type);
            // 花色用颜色区分：红色（♥♦）/ 白色（♠♣）
            bool     isRed    = card.Suit == Suit.Heart || card.Suit == Suit.Diamond;
            string   suitMark = isRed
                ? $"<color=#FF4444>{card.SuitSymbol}</color>"
                : card.SuitSymbol;
            string   label    = $"{suitMark}{card.RankName}\n{typeName}";

            switch (card.Type)
            {
                case CardType.Kill:
                    style = playable ? _cardKill  : _cardNormal; break;
                case CardType.Dodge:
                    style = playable ? _cardDodge : _cardNormal; break;
                default:
                    bool isEquip = CardRegistry.Get(card.Type) is EquipmentDefinition;
                    style = playable
                        ? (isEquip ? _cardEquip : _cardSpell)
                        : _cardNormal;
                    break;
            }
            GUI.Box(new Rect(x, y, CW, CH), label, style);
        }

        void DrawMiddle(float cx, float cy, CardGameState s)
        {
            float bw = 500f, bh = 110f;
            float bx  = cx - bw / 2f;
            float by  = cy - 70f;
            GUI.Box(new Rect(bx, by, bw, bh), GUIContent.none, _infoBoxStyle);

            bool myTurn     = s.ActiveSlot == _localSlot
                           && s.TurnPhase == TurnPhase.Play && s.SubPhase == SubPhase.During
                           && !s.WaitingForResponse;
            bool isDefender = s.WaitingForResponse
                           && s.PendingAttackSlot >= 0 && s.PendingAttackSlot != _localSlot;
            bool isAttacker = s.WaitingForResponse && s.PendingAttackSlot == _localSlot;
            bool myDiscard  = s.ActiveSlot == _localSlot
                           && s.TurnPhase == TurnPhase.Discard && s.SubPhase == SubPhase.During
                           && s.Players[_localSlot].Hand.Count > s.HandLimit(_localSlot);

            // Phase name indicator
            string phaseName = s.TurnPhase switch
            {
                TurnPhase.TurnStart => "回合开始",
                TurnPhase.Judgment  => "判定阶段",
                TurnPhase.Draw      => "摸牌阶段",
                TurnPhase.Play      => "出牌阶段",
                TurnPhase.Discard   => "弃牌阶段",
                TurnPhase.TurnEnd   => "回合结束",
                _                   => "—",
            };
            string subName = s.SubPhase switch
            {
                SubPhase.Before => "开始前",
                SubPhase.During => "时",
                SubPhase.After  => "后",
                _               => "",
            };
            string activePlayer = s.ActiveSlot == _localSlot ? "你的回合" : "对手回合";

            string phaseText;
            if (s.IsGameOver)
                phaseText = s.WinnerSlot == _localSlot
                    ? "<color=#FFD700>★ 你赢了！</color>"
                    : "<color=#FF4444>✗ 你输了...</color>";
            else if (isDefender)
            {
                bool hasArmor = _localSlot >= 0
                    && s.Players[_localSlot].GetEquip(EquipSlot.Armor).Type == CardType.BaGuaZhen;
                phaseText = hasArmor
                    ? "<color=#FF8C00>你被攻击！</color>  拖动 <color=#4488FF>闪</color> 抵消，点 [八卦阵] 判定，或点 [受伤]"
                    : "<color=#FF8C00>你被攻击！</color>  拖动 <color=#4488FF>闪</color> 抵消，或点 [受伤]";
            }
            else if (isAttacker)
            {
                bool canTieJi = _localSlot >= 0
                    && HeroRegistry.Get(s.Players[_localSlot].Hero).CanActivateOnAttack(s, _localSlot);
                phaseText = canTieJi
                    ? "等待对手出闪...  点 [铁骑] 判定封锁"
                    : "等待对手出闪...";
            }
            else if (myDiscard)
                phaseText = $"弃牌阶段：手牌超限 ({s.Players[_localSlot].Hand.Count}/{s.HandLimit(_localSlot)})，请点击手牌弃牌";
            else if (myTurn && s.WuShengActive)
                phaseText = "<color=#FFD060>武圣</color>：选择一张高亮红色牌，拖向对手区域当「杀」打出";
            else if (myTurn)
                phaseText = "你的回合：拖牌到对手区域出牌，点击装备牌装备，或 [结束出牌]";
            else
                phaseText = s.ActiveSlot == _localSlot ? "等待自动推进..." : "等待对手出牌...";

            GUI.Label(new Rect(bx + 10f, by + 6f,  bw - 20f, 36f), phaseText, _statusStyle);
            GUI.Label(new Rect(bx + 10f, by + 44f, 280f,     22f),
                      $"{activePlayer}  [{phaseName} · {subName}]  帧：{s.FrameNumber}", _labelStyle);
            GUI.Label(new Rect(bx + 10f, by + 68f, 200f,     22f),
                      $"牌堆：{s.DeckRemaining}/{CardGameState.DeckSize}  RTT: {_net.Client.RttMs} ms", _labelStyle);

            bool canPass = !s.IsGameOver && (myTurn || isDefender || myDiscard);
            if (canPass)
            {
                string passLabel = isDefender ? "受伤 (不出闪)" : myDiscard ? "跳过弃牌" : "结束出牌";
                if (GUI.Button(new Rect(bx + bw - 155f, by + 33f, 145f, 44f), passLabel, _btnStyle))
                    QueueAction(CardAction.Pass, 0);
            }

            // 八卦阵：防具被动判定按钮（响应阶段，且已装备八卦阵）
            bool canTriggerArmor = !s.IsGameOver && isDefender && _localSlot >= 0
                && s.Players[_localSlot].GetEquip(EquipSlot.Armor).Type == CardType.BaGuaZhen;
            if (canTriggerArmor)
            {
                if (GUI.Button(new Rect(bx + bw - 310f, by + 33f, 145f, 44f), "八卦阵 (判定)", _btnStyle))
                    QueueAction(CardAction.ActivateArmor, 0);
            }

            // 铁骑：攻击技判定按钮（已出杀等待响应，且武将可触发）
            bool canTieJiBtn = !s.IsGameOver && isAttacker && _localSlot >= 0
                && HeroRegistry.Get(s.Players[_localSlot].Hero).CanActivateOnAttack(s, _localSlot);
            if (canTieJiBtn)
            {
                if (GUI.Button(new Rect(bx + bw - 310f, by + 33f, 145f, 44f), "铁骑 (判定)", _btnStyle))
                    QueueAction(CardAction.UseAttackSkill, 0);
            }
        }

        void HandleDrag(Rect dropZone, bool canDrag, CardGameState s)
        {
            if (!_dragging) return;
            var ev = Event.current;

            if (ev.type == EventType.MouseDrag && GUIUtility.hotControl == DragCtrlId)
            {
                _dragPos = ev.mousePosition;
                ev.Use();
            }
            else if (ev.type == EventType.MouseUp)
            {
                GUIUtility.hotControl = 0;

                if (canDrag && _dragCardIdx >= 0 && _localSlot >= 0
                    && _dragCardIdx < s.Players[_localSlot].Hand.Count
                    && dropZone.Contains(ev.mousePosition))
                {
                    QueueAction(CardAction.PlayCard, _dragCardIdx);
                }

                _dragging    = false;
                _dragCardIdx = -1;
                ev.Use();
            }
        }

        void CenterLabel(string text)
        {
            float w = 400f, h = 40f;
            GUI.Label(new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h),
                      text, _statusStyle);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Style cache
        // ─────────────────────────────────────────────────────────────────────

        void CacheStyles()
        {
            if (_stylesCached) return;
            _stylesCached = true;

            _bgStyle    = MakeBoxStyle(new Color(0.06f, 0.10f, 0.06f, 0.96f));
            _panelStyle = MakeBoxStyle(new Color(0.05f, 0.08f, 0.05f, 0.92f));
            _panelStyle.border = new RectOffset(6, 6, 6, 6);

            _labelStyle = new GUIStyle(GUI.skin.label)
                { fontSize = 13, richText = true, alignment = TextAnchor.MiddleLeft };
            _labelStyle.normal.textColor = new Color(0.78f, 0.78f, 0.78f);

            _titleStyle = new GUIStyle(GUI.skin.label)
                { fontSize = 16, fontStyle = FontStyle.Bold, richText = true, alignment = TextAnchor.MiddleCenter };
            _titleStyle.normal.textColor = Color.white;

            _statusStyle = new GUIStyle(GUI.skin.label)
                { fontSize = 15, fontStyle = FontStyle.Bold, richText = true, alignment = TextAnchor.MiddleCenter };
            _statusStyle.normal.textColor = Color.white;

            _btnStyle = new GUIStyle(GUI.skin.button) { fontSize = 13, fontStyle = FontStyle.Bold };

            _btnGreen = new GUIStyle(_btnStyle);
            _btnGreen.normal.background   = MakeTex(new Color(0.15f, 0.50f, 0.15f));
            _btnGreen.hover.background    = MakeTex(new Color(0.20f, 0.65f, 0.20f));
            _btnGreen.normal.textColor    = Color.white;
            _btnGreen.hover.textColor     = Color.white;

            _btnRed = new GUIStyle(_btnStyle);
            _btnRed.normal.background  = MakeTex(new Color(0.50f, 0.12f, 0.12f));
            _btnRed.hover.background   = MakeTex(new Color(0.65f, 0.18f, 0.18f));
            _btnRed.normal.textColor   = Color.white;
            _btnRed.hover.textColor    = Color.white;

            _readyStyle    = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold };
            _readyStyle.normal.textColor = new Color(0.3f, 1f, 0.3f);

            _notReadyStyle = new GUIStyle(GUI.skin.label) { fontSize = 14 };
            _notReadyStyle.normal.textColor = new Color(0.6f, 0.6f, 0.6f);

            // Cards
            _cardNormal = MakeCardStyle(new Color(0.82f, 0.76f, 0.60f), new Color(0.15f, 0.15f, 0.15f), 22);
            _cardKill   = MakeCardStyle(new Color(1.0f,  0.86f, 0.86f), new Color(0.65f, 0.05f, 0.05f), 28);
            _cardKill.fontStyle = FontStyle.Bold;
            _cardDodge  = MakeCardStyle(new Color(0.85f, 0.93f, 1.0f),  new Color(0.05f, 0.30f, 0.70f), 28);
            _cardDodge.fontStyle = FontStyle.Bold;
            _cardEquip  = MakeCardStyle(new Color(0.85f, 1.0f,  0.85f), new Color(0.05f, 0.45f, 0.05f), 18);
            _cardEquip.fontStyle = FontStyle.Bold;
            _cardSpell  = MakeCardStyle(new Color(0.96f, 0.88f, 1.0f),  new Color(0.45f, 0.05f, 0.65f), 14);
            _cardSpell.fontStyle = FontStyle.Bold;
            _cardBack   = MakeCardStyle(new Color(0.18f, 0.28f, 0.62f), Color.white, 22);

            // 装备区格子
            _equipFilled = new GUIStyle(GUI.skin.box)
                { fontSize = 12, alignment = TextAnchor.MiddleCenter, wordWrap = true };
            _equipFilled.normal.background = MakeTex(new Color(0.20f, 0.45f, 0.20f, 0.85f));
            _equipFilled.normal.textColor  = Color.white;

            _equipEmpty = new GUIStyle(GUI.skin.box)
                { fontSize = 11, alignment = TextAnchor.MiddleCenter };
            _equipEmpty.normal.background = MakeTex(new Color(0.10f, 0.10f, 0.10f, 0.55f));
            _equipEmpty.normal.textColor  = new Color(0.45f, 0.45f, 0.45f);

            // 武将技能提示
            _heroStyle = new GUIStyle(GUI.skin.label) { fontSize = 11, richText = true };
            _heroStyle.normal.textColor = new Color(1.0f, 0.85f, 0.40f);

            // 选武将面板 — 未选中 / 已选中
            _heroCardNormal = MakeBoxStyle(new Color(0.10f, 0.14f, 0.22f, 0.90f));
            _heroCardNormal.fontSize  = 14;
            _heroCardNormal.wordWrap  = true;

            _heroCardSelected = MakeBoxStyle(new Color(0.20f, 0.36f, 0.58f, 0.95f));
            _heroCardSelected.fontSize = 14;
            _heroCardSelected.wordWrap = true;
            // 选中时加亮边框感（用 border 偏移）
            _heroCardSelected.border   = new RectOffset(4, 4, 4, 4);

            // 判定区横条
            _judgeZone = new GUIStyle(GUI.skin.box)
                { fontSize = 12, alignment = TextAnchor.MiddleLeft };
            _judgeZone.normal.background = MakeTex(new Color(0.30f, 0.24f, 0.05f, 0.55f));
            _judgeZone.normal.textColor  = new Color(0.9f, 0.85f, 0.6f);

            _hpBoxStyle   = MakeBoxStyle(new Color(0f, 0f, 0f, 0.50f));
            _infoBoxStyle = MakeBoxStyle(new Color(0f, 0f, 0f, 0.42f));

            // 技能按钮 — 被动（展示框）/ 主动（可点击按钮）
            _skillBtnPassive = new GUIStyle(GUI.skin.box)
                { fontSize = 12, wordWrap = true, alignment = TextAnchor.UpperCenter, richText = true };
            _skillBtnPassive.normal.background = MakeTex(new Color(0.08f, 0.10f, 0.08f, 0.82f));
            _skillBtnPassive.normal.textColor  = new Color(0.70f, 0.65f, 0.45f);

            _skillBtnActive = new GUIStyle(GUI.skin.button)
                { fontSize = 13, fontStyle = FontStyle.Bold, wordWrap = true,
                  alignment = TextAnchor.UpperCenter, richText = true };
            _skillBtnActive.normal.background  = MakeTex(new Color(0.55f, 0.40f, 0.05f, 0.90f));
            _skillBtnActive.hover.background   = MakeTex(new Color(0.72f, 0.55f, 0.10f, 0.95f));
            _skillBtnActive.active.background  = MakeTex(new Color(0.90f, 0.70f, 0.15f, 1.00f));
            _skillBtnActive.normal.textColor   = Color.white;
            _skillBtnActive.hover.textColor    = Color.white;

            _dropZone    = MakeBoxStyle(new Color(0.75f, 0.20f, 0.20f, 0.30f));
            _dropZone.fontSize  = 14;
            _dropZone.alignment = TextAnchor.MiddleCenter;
            _dropZone.normal.textColor = new Color(1f, 0.9f, 0.9f);

            _dropZoneHot = MakeBoxStyle(new Color(1.0f, 0.25f, 0.25f, 0.60f));
            _dropZoneHot.fontSize   = 18;
            _dropZoneHot.fontStyle  = FontStyle.Bold;
            _dropZoneHot.alignment  = TextAnchor.MiddleCenter;
            _dropZoneHot.normal.textColor = Color.white;
        }

        static GUIStyle MakeBoxStyle(Color bg)
        {
            var s = new GUIStyle(GUI.skin.box);
            s.normal.background = MakeTex(bg);
            return s;
        }

        static GUIStyle MakeCardStyle(Color bg, Color text, int fontSize)
        {
            var s = new GUIStyle(GUI.skin.box)
                { fontSize = fontSize, alignment = TextAnchor.MiddleCenter, richText = true };
            s.normal.background = MakeTex(bg);
            s.normal.textColor  = text;
            return s;
        }

        static Texture2D MakeTex(Color col)
        {
            var tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, col);
            tex.Apply();
            return tex;
        }
    }
}
