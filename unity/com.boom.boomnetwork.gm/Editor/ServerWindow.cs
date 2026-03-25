using UnityEngine;
using UnityEditor;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace BoomNetwork.GM.Editor
{
    public class ServerWindow : EditorWindow
    {
        // ===== Config =====
        private string _serverPath, _configFile, _addr, _proto, _adminUrl, _adminToken;
        private int _ppr;

        // ===== Clients =====
        private AdminClient _client;       // HTTP fallback
        private AdminWsClient _wsClient;   // WebSocket 主通道

        // ===== State =====
        private AdminClient.HealthResult _health;
        private AdminClient.StatsResult  _stats;
        private AdminClient.MsgEntry[]   _messages = Array.Empty<AdminClient.MsgEntry>();
        private AdminClient.RoomDetail[] _rooms = Array.Empty<AdminClient.RoomDetail>();
        private AdminClient.NetSimResult _netSim;
        private bool _netSimEnabled;
        private int _netSimLatency, _netSimJitter, _netSimLoss;
        private bool _netSimDirty; // UI 值和服务器值不同步
        private double _nextCheckTime;
        private double _nextPingTime;
        private bool _lastAlive;
        private string _lastAdminUrl, _lastAdminToken; // 检测配置变更

        // WS 消息缓冲（追加模式，最多保留 200 条）
        private readonly List<AdminClient.MsgEntry> _wsMsgBuffer = new List<AdminClient.MsgEntry>();
        private const int WS_MSG_BUFFER_MAX = 200;

        // ===== Tab =====
        private int _tab;
        private static readonly string[] TabNames = { "Dashboard", "Messages", "Rooms", "Deploy" };

        // ===== Deploy =====
        private DeployTool _deployTool;
        private DeployProfile _deployProfile;
        private int _deployProfileIndex;
        private string[] _deployProfileNames;
        private Vector2 _deployLogScroll;
        private string _newEnvName = "";

        // ===== Server Switcher =====
        private int _serverSwitcherIdx;
        private string[] _serverSwitcherNames;

        // ===== Messages page =====
        private Vector2 _msgScroll;
        private int _cmdFilter = -1;
        private bool _msgPaused;
        private bool _hideHeartbeat = true;
        private string[] _cmdFilterNames;
        private int[] _cmdFilterValues;

        // ===== Rooms page =====
        private Vector2 _roomScroll;

        private const double POLL_INTERVAL = 2.0;
        private const string PP = "BoomNetwork.GM.Server.";

        [MenuItem("BoomNetwork/Server Window")]
        public static void ShowWindow() => GetWindow<ServerWindow>("BoomNetwork Server");

        void OnEnable()
        {
            LoadPrefs();
            _client = new AdminClient(_adminUrl) { Token = _adminToken };
            _wsClient = new AdminWsClient();
            _deployTool = new DeployTool(() => Repaint());
            LoadDeployProfiles();
            BuildServerSwitcher();
            BuildCmdFilter();
            EditorApplication.update += OnEditorUpdate;
        }

        void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            SavePrefs();
            SaveDeployProfile();
            _deployTool?.Dispose();
            _wsClient?.Dispose();
            _client?.Dispose();
        }

        // ===================== 主循环 =====================

        void OnEditorUpdate()
        {
            // 检测配置变更 → 重连 WS
            if (_adminUrl != _lastAdminUrl || _adminToken != _lastAdminToken)
            {
                _lastAdminUrl = _adminUrl;
                _lastAdminToken = _adminToken;
                _client.BaseUrl = _adminUrl;
                _client.Token = _adminToken;
                _wsClient.Disconnect();
            }

            bool dirty = false;
            bool wsConnected = _wsClient.IsConnected;

            // ===== WS 模式：drain 入站队列 =====
            if (wsConnected)
            {
                while (_wsClient.Inbound.TryDequeue(out var env))
                {
                    ApplyEnvelope(env);
                    dirty = true;
                }

                // 心跳
                if (EditorApplication.timeSinceStartup > _nextPingTime)
                {
                    _nextPingTime = EditorApplication.timeSinceStartup + 30.0;
                    _wsClient.SendPing();
                }
            }

            // ===== HTTP Fallback 或 WS 连接尝试 =====
            if (EditorApplication.timeSinceStartup >= _nextCheckTime)
            {
                _nextCheckTime = EditorApplication.timeSinceStartup + POLL_INTERVAL;

                if (!wsConnected)
                {
                    // 尝试 HTTP 探测 + WS 连接
                    _health = _client.FetchHealth();

                    if (_health.IsOnline)
                    {
                        // 服务器在线且 WS 未连接/未连接中 → 启动 WS
                        if (!_wsClient.IsConnecting)
                            _wsClient.Connect(_adminUrl, _adminToken);

                        // HTTP fallback 拉数据
                        _stats = _client.FetchStats();
                        if (_tab == 1 && !_msgPaused)
                            _messages = _client.FetchMessages(100);
                        if (_tab == 2)
                            _rooms = _client.FetchRooms();
                    }
                    else
                    {
                        _stats = default;
                        _messages = Array.Empty<AdminClient.MsgEntry>();
                        _rooms = Array.Empty<AdminClient.RoomDetail>();
                        _wsMsgBuffer.Clear();
                    }
                    dirty = true;
                }
            }

            // Deploy tool tick
            _deployTool?.Tick();

            // 更新存活状态
            bool alive = wsConnected || _health.IsOnline;
            if (alive != _lastAlive || dirty)
            {
                _lastAlive = alive;
                Repaint();
            }
        }

        // ===================== WS Push 应用 =====================

        void ApplyEnvelope(GmEnvelope env)
        {
            if (env.Type == "push")
            {
                var payload = env.DecodePayload();
                if (payload == null) return;

                switch (env.Topic)
                {
                    case GmTopics.Health:
                        var hp = GmHealthPush.From(payload);
                        _health = new AdminClient.HealthResult
                        {
                            IsOnline = true,
                            Rooms = hp.Rooms,
                            Players = hp.Players,
                            Uptime = hp.Uptime,
                        };
                        break;

                    case GmTopics.Stats:
                        var sp = GmStatsPush.From(payload);
                        _stats = new AdminClient.StatsResult
                        {
                            HasData = true,
                            GameRxTotal = sp.GameRxTotal, GameTxTotal = sp.GameTxTotal,
                            GameRx1Min = sp.GameRx1Min, GameTx1Min = sp.GameTx1Min,
                            GameRx5Sec = sp.GameRx5Sec, GameTx5Sec = sp.GameTx5Sec,
                            GmRxTotal = sp.GmRxTotal, GmTxTotal = sp.GmTxTotal,
                            GmRx1Min = sp.GmRx1Min, GmTx1Min = sp.GmTx1Min,
                            GmRx5Sec = sp.GmRx5Sec, GmTx5Sec = sp.GmTx5Sec,
                        };
                        break;

                    case GmTopics.Messages:
                        if (!_msgPaused)
                        {
                            var me = GmMsgEntry.From(payload);
                            _wsMsgBuffer.Add(new AdminClient.MsgEntry
                            {
                                Ts = me.Ts, Dir = me.Dir, Cmd = me.Cmd,
                                Name = me.Name, Pid = me.Pid, Size = me.Size,
                            });
                            if (_wsMsgBuffer.Count > WS_MSG_BUFFER_MAX)
                                _wsMsgBuffer.RemoveAt(0);
                            _messages = _wsMsgBuffer.ToArray();
                        }
                        break;

                    case GmTopics.Rooms:
                        // Rooms push 是数组，payload 直接就是 msgpack array
                        var roomsArr = MsgPackLite.Decode(env.Payload);
                        if (roomsArr is List<object> roomList)
                        {
                            var rooms = new List<AdminClient.RoomDetail>();
                            foreach (var item in roomList)
                            {
                                if (item is Dictionary<string, object> rm)
                                {
                                    var grd = GmRoomDetail.From(rm);
                                    var rd = new AdminClient.RoomDetail
                                    {
                                        Id = grd.Id, Running = grd.Running, Paused = grd.Paused,
                                        FrameNumber = grd.FrameNumber, FrameRate = grd.FrameRate,
                                        MaxPlayers = grd.MaxPlayers, OnlineCount = grd.OnlineCount,
                                        TotalPlayers = grd.TotalPlayers,
                                    };
                                    if (grd.Players != null)
                                    {
                                        rd.Players = new AdminClient.PlayerInfo[grd.Players.Length];
                                        for (int i = 0; i < grd.Players.Length; i++)
                                            rd.Players[i] = new AdminClient.PlayerInfo { Id = grd.Players[i].Id, State = grd.Players[i].State };
                                    }
                                    else rd.Players = Array.Empty<AdminClient.PlayerInfo>();
                                    rooms.Add(rd);
                                }
                            }
                            _rooms = rooms.ToArray();
                        }
                        break;

                    case GmTopics.Netsim:
                        var ns = payload;
                        _netSim = new AdminClient.NetSimResult
                        {
                            HasData     = true,
                            Enabled     = MsgPackLite.GetBool(ns, "enabled"),
                            LatencyMs   = MsgPackLite.GetInt(ns, "latency_ms"),
                            JitterMs    = MsgPackLite.GetInt(ns, "jitter_ms"),
                            LossPercent = MsgPackLite.GetInt(ns, "loss_percent"),
                            Dropped     = MsgPackLite.GetLong(ns, "stats_dropped"),
                            Delayed     = MsgPackLite.GetLong(ns, "stats_delayed"),
                        };
                        // 服务器推送时只更新显示，不覆盖用户正在编辑的值
                        if (!_netSimDirty)
                        {
                            _netSimEnabled = _netSim.Enabled;
                            _netSimLatency = _netSim.LatencyMs;
                            _netSimJitter  = _netSim.JitterMs;
                            _netSimLoss    = _netSim.LossPercent;
                        }
                        break;
                }
            }
            else if (env.Type == "rsp")
            {
                // RPC 响应
                var payload = env.DecodePayload();
                if (payload != null && MsgPackLite.GetBool(payload, "ok"))
                    ShowNotification(new GUIContent($"{env.Topic}: OK"));
            }
            else if (env.Type == "err")
            {
                var payload = env.DecodePayload();
                var msg = MsgPackLite.GetString(payload, "error", "unknown error");
                ShowNotification(new GUIContent($"Error: {msg}"));
            }
        }

        // ===================== GUI =====================

        void OnGUI()
        {
            DrawStatusBar();
            _tab = GUILayout.Toolbar(_tab, TabNames);
            EditorGUILayout.Space(2);
            switch (_tab)
            {
                case 0: DrawDashboard(); break;
                case 1: DrawMessages(); break;
                case 2: DrawRooms(); break;
                case 3: DrawDeploy(); break;
            }
        }

        // ===================== Status Bar =====================

        void DrawStatusBar()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();

            // ===== 服务器切换器 =====
            if (_serverSwitcherNames != null && _serverSwitcherNames.Length > 0)
            {
                int newIdx = EditorGUILayout.Popup(_serverSwitcherIdx, _serverSwitcherNames, GUILayout.Width(148));
                if (newIdx != _serverSwitcherIdx)
                {
                    _serverSwitcherIdx = newIdx;
                    ApplyServerProfile(newIdx);
                }
            }

            // ===== 状态指示 =====
            var prev = GUI.contentColor;
            GUI.contentColor = _lastAlive ? Color.green : Color.gray;
            EditorGUILayout.LabelField($"● {(_lastAlive ? "RUNNING" : "STOPPED")}",
                EditorStyles.boldLabel, GUILayout.Width(90));
            GUI.contentColor = prev;

            if (_lastAlive)
            {
                EditorGUILayout.LabelField($"Rooms: {_health.Rooms}  Players: {_health.Players}  Up: {_health.Uptime}");

                GUILayout.FlexibleSpace();
                bool wsOn = _wsClient != null && _wsClient.IsConnected;
                prev = GUI.contentColor;
                GUI.contentColor = wsOn ? Color.cyan : Color.yellow;
                EditorGUILayout.LabelField(wsOn ? "WS" : "HTTP", EditorStyles.miniLabel, GUILayout.Width(30));
                GUI.contentColor = prev;
            }
            EditorGUILayout.EndHorizontal();
        }

        // ===================== Tab 0: Dashboard =====================

        void DrawDashboard()
        {
            if (_lastAlive && _stats.HasData)
            {
                EditorGUILayout.LabelField("Game Traffic", EditorStyles.boldLabel);
                TrafficRow("Total", _stats.GameTxTotal, _stats.GameRxTotal);
                TrafficRow("1 min", _stats.GameTx1Min,  _stats.GameRx1Min);
                TrafficRow("5 sec", _stats.GameTx5Sec / 5, _stats.GameRx5Sec / 5, true);

                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("GM Traffic", EditorStyles.boldLabel);
                TrafficRow("Total", _stats.GmTxTotal, _stats.GmRxTotal);
                TrafficRow("1 min", _stats.GmTx1Min,  _stats.GmRx1Min);
                TrafficRow("5 sec", _stats.GmTx5Sec / 5, _stats.GmRx5Sec / 5, true);
            }

            // ===== Network Simulation =====
            if (_lastAlive)
                DrawNetSim();

            EditorGUILayout.Space(6);
            DrawConfig();
            EditorGUILayout.Space(6);
            DrawActions();

            // 手动命令区
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Manual Command", EditorStyles.boldLabel);
            var cmd = BuildCommand();
            EditorGUILayout.SelectableLabel(cmd, EditorStyles.textField, GUILayout.Height(20));
            if (GUILayout.Button("Copy Command"))
            {
                GUIUtility.systemCopyBuffer = cmd;
                ShowNotification(new GUIContent("Copied!"));
            }
        }

        static void TrafficRow(string label, long tx, long rx, bool perSec = false)
        {
            string suffix = perSec ? "/s" : "";
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, GUILayout.Width(40));
            var prev = GUI.contentColor;
            GUI.contentColor = new Color(0.4f, 0.8f, 1f);
            EditorGUILayout.LabelField($"S→C {AdminClient.FmtBytes(tx)}{suffix}", GUILayout.Width(110));
            GUI.contentColor = new Color(0.5f, 1f, 0.5f);
            EditorGUILayout.LabelField($"C→S {AdminClient.FmtBytes(rx)}{suffix}");
            GUI.contentColor = prev;
            EditorGUILayout.EndHorizontal();
        }

        // ===================== Network Simulation =====================

        void DrawNetSim()
        {
            EditorGUILayout.Space(4);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Network Simulation", EditorStyles.boldLabel);

            // 统计
            if (_netSim.HasData)
            {
                var prev = GUI.contentColor;
                GUI.contentColor = _netSim.Enabled ? Color.yellow : Color.gray;
                EditorGUILayout.LabelField(
                    _netSim.Enabled ? $"ON  dropped:{_netSim.Dropped} delayed:{_netSim.Delayed}" : "OFF",
                    EditorStyles.miniLabel);
                GUI.contentColor = prev;
            }
            EditorGUILayout.EndHorizontal();

            // 控制
            EditorGUI.BeginChangeCheck();

            _netSimEnabled = EditorGUILayout.Toggle("Enabled", _netSimEnabled);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Latency", GUILayout.Width(55));
            _netSimLatency = EditorGUILayout.IntSlider(_netSimLatency, 0, 500);
            EditorGUILayout.LabelField("ms", GUILayout.Width(20));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Jitter", GUILayout.Width(55));
            _netSimJitter = EditorGUILayout.IntSlider(_netSimJitter, 0, 200);
            EditorGUILayout.LabelField("ms", GUILayout.Width(20));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Loss", GUILayout.Width(55));
            _netSimLoss = EditorGUILayout.IntSlider(_netSimLoss, 0, 50);
            EditorGUILayout.LabelField("%", GUILayout.Width(20));
            EditorGUILayout.EndHorizontal();

            if (EditorGUI.EndChangeCheck())
                _netSimDirty = true;

            // Apply 按钮
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            GUI.enabled = _netSimDirty;
            GUI.backgroundColor = _netSimDirty ? Color.yellow : Color.gray;
            if (GUILayout.Button("Apply", GUILayout.Width(60)))
            {
                if (_wsClient != null && _wsClient.IsConnected)
                {
                    _wsClient.SendRpc("netsim", new Dictionary<string, object>
                    {
                        ["enabled"] = _netSimEnabled,
                        ["latency_ms"] = _netSimLatency,
                        ["jitter_ms"] = _netSimJitter,
                        ["loss_percent"] = _netSimLoss,
                    });
                }
                else
                {
                    var r = _client.SetNetSim(_netSimEnabled, _netSimLatency, _netSimJitter, _netSimLoss);
                    if (!r.Ok)
                        ShowNotification(new GUIContent($"NetSim error: {r.Error}"));
                }
                _netSimDirty = false;
            }
            GUI.backgroundColor = Color.white;
            GUI.enabled = true;

            // Reset 按钮
            if (GUILayout.Button("Reset", GUILayout.Width(50)))
            {
                _netSimEnabled = false;
                _netSimLatency = 0;
                _netSimJitter = 0;
                _netSimLoss = 0;
                _netSimDirty = true;
            }
            EditorGUILayout.EndHorizontal();
        }

        // ===================== Tab 1: Messages =====================

        void DrawMessages()
        {
            // Toolbar
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Filter:", GUILayout.Width(38));
            int filterIdx = Array.IndexOf(_cmdFilterValues, _cmdFilter);
            if (filterIdx < 0) filterIdx = 0;
            int newIdx = EditorGUILayout.Popup(filterIdx, _cmdFilterNames, GUILayout.Width(130));
            _cmdFilter = _cmdFilterValues[newIdx];

            _hideHeartbeat = GUILayout.Toggle(_hideHeartbeat, "Hide HB", GUILayout.Width(65));

            GUILayout.FlexibleSpace();

            if (GUILayout.Button(_msgPaused ? "▶" : "❚❚", GUILayout.Width(30)))
                _msgPaused = !_msgPaused;

            if (GUILayout.Button("Copy", GUILayout.Width(40)))
                CopyMessages();

            EditorGUILayout.LabelField($"{_messages.Length}", GUILayout.Width(30));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(2);

            // Header
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            EditorGUILayout.LabelField("Time",    EditorStyles.miniLabel, GUILayout.Width(60));
            EditorGUILayout.LabelField("Dir",     EditorStyles.miniLabel, GUILayout.Width(22));
            EditorGUILayout.LabelField("Command", EditorStyles.miniLabel, GUILayout.Width(115));
            EditorGUILayout.LabelField("P",       EditorStyles.miniLabel, GUILayout.Width(30));
            EditorGUILayout.LabelField("Size",    EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();

            _msgScroll = EditorGUILayout.BeginScrollView(_msgScroll);

            var filtered = _messages.Where(m =>
            {
                if (_hideHeartbeat && (m.Cmd == 7 || m.Cmd == 8)) return false;
                if (_cmdFilter >= 0 && m.Cmd != _cmdFilter) return false;
                return true;
            }).ToArray();

            foreach (var msg in filtered)
            {
                EditorGUILayout.BeginHorizontal();
                var dt = DateTimeOffset.FromUnixTimeMilliseconds(msg.Ts).LocalDateTime;
                EditorGUILayout.LabelField(dt.ToString("HH:mm:ss"), GUILayout.Width(60));

                var prev = GUI.contentColor;
                GUI.contentColor = msg.Dir == "rx" ? new Color(0.5f, 1f, 0.5f) : new Color(0.4f, 0.8f, 1f);
                EditorGUILayout.LabelField(msg.Dir == "rx" ? "↓" : "↑", GUILayout.Width(22));
                GUI.contentColor = prev;

                EditorGUILayout.LabelField(msg.Name, GUILayout.Width(115));
                EditorGUILayout.LabelField(msg.Pid > 0 ? $"P{msg.Pid}" : "—", GUILayout.Width(30));
                EditorGUILayout.LabelField(AdminClient.FmtBytes(msg.Size));
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndScrollView();
        }

        void CopyMessages()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Time\tDir\tCommand\tPlayer\tSize");
            foreach (var m in _messages)
            {
                var dt = DateTimeOffset.FromUnixTimeMilliseconds(m.Ts).LocalDateTime;
                sb.AppendLine($"{dt:HH:mm:ss}\t{m.Dir}\t{m.Name}\tP{m.Pid}\t{m.Size}");
            }
            GUIUtility.systemCopyBuffer = sb.ToString();
            ShowNotification(new GUIContent($"Copied {_messages.Length} messages"));
        }

        void BuildCmdFilter()
        {
            var names = new List<string> { "All" };
            var values = new List<int> { -1 };
            var cmds = new (int cmd, string name)[]
            {
                (1, "SessionBind"), (3, "RequestStart"), (5, "FrameInput"),
                (6, "PushFrames"), (7, "Heartbeat"), (9, "Reconnect"),
                (15, "JoinRoom"), (19, "PlayerJoined"), (22, "UploadSnapshot"),
                (27, "SendEntityState"), (28, "PushEntityState"),
            };
            foreach (var c in cmds) { names.Add(c.name); values.Add(c.cmd); }
            _cmdFilterNames = names.ToArray();
            _cmdFilterValues = values.ToArray();
        }

        // ===================== Tab 2: Rooms =====================

        void DrawRooms()
        {
            if (!_lastAlive) { EditorGUILayout.HelpBox("Server offline", MessageType.Warning); return; }

            if (_rooms.Length == 0)
            {
                EditorGUILayout.LabelField("No rooms", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            _roomScroll = EditorGUILayout.BeginScrollView(_roomScroll);

            foreach (var room in _rooms)
            {
                // Room header
                EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
                var statusColor = room.Running ? (room.Paused ? Color.yellow : Color.green) : Color.gray;
                var prev = GUI.contentColor;
                GUI.contentColor = statusColor;
                string status = room.Running ? (room.Paused ? "PAUSED" : "RUNNING") : "WAITING";
                EditorGUILayout.LabelField($"Room {room.Id}  [{status}]  F#{room.FrameNumber}  {room.OnlineCount}/{room.MaxPlayers}",
                    EditorStyles.boldLabel);
                GUI.contentColor = prev;

                // Stop room button
                GUI.backgroundColor = new Color(1f, 0.4f, 0.3f);
                GUI.enabled = room.Running;
                if (GUILayout.Button("Stop", GUILayout.Width(45)))
                    DoStopRoom(room.Id);
                GUI.enabled = true;
                GUI.backgroundColor = Color.white;
                EditorGUILayout.EndHorizontal();

                // Player list
                if (room.Players != null)
                {
                    EditorGUI.indentLevel++;
                    foreach (var p in room.Players)
                    {
                        EditorGUILayout.BeginHorizontal();
                        bool online = p.State == 0;
                        var pPrev = GUI.contentColor;
                        GUI.contentColor = online ? Color.green : Color.red;
                        EditorGUILayout.LabelField($"P{p.Id} {(online ? "online" : "OFFLINE")}",
                            GUILayout.Width(120));
                        GUI.contentColor = pPrev;

                        // Kick button
                        GUI.backgroundColor = new Color(1f, 0.6f, 0.3f);
                        if (GUILayout.Button("Kick", GUILayout.Width(40)))
                            DoKickPlayer(p.Id);
                        GUI.backgroundColor = Color.white;

                        EditorGUILayout.EndHorizontal();
                    }
                    EditorGUI.indentLevel--;
                }

                EditorGUILayout.Space(4);
            }

            EditorGUILayout.EndScrollView();
        }

        // ===================== RPC: WS 优先，HTTP 降级 =====================

        void DoKickPlayer(int pid)
        {
            if (_wsClient != null && _wsClient.IsConnected)
            {
                _wsClient.SendRpc("kick", new Dictionary<string, object> { ["pid"] = pid });
            }
            else
            {
                var r = _client.KickPlayer(pid);
                ShowNotification(new GUIContent(r.Ok ? $"Kicked P{pid}" : r.Error));
            }
        }

        void DoStopRoom(int roomId)
        {
            if (_wsClient != null && _wsClient.IsConnected)
            {
                _wsClient.SendRpc("stop_room", new Dictionary<string, object> { ["room_id"] = roomId });
            }
            else
            {
                var r = _client.StopRoom(roomId);
                ShowNotification(new GUIContent(r.Ok ? $"Room {roomId} stopped" : r.Error));
            }
        }

        // ===================== Config =====================

        void DrawConfig()
        {
            EditorGUILayout.LabelField("Config", EditorStyles.boldLabel);
            _serverPath = EditorGUILayout.TextField("Server Path", _serverPath);
            _configFile = EditorGUILayout.TextField("Config File", _configFile);
            _adminUrl   = EditorGUILayout.TextField("Admin URL",   _adminUrl);
            _adminToken = EditorGUILayout.TextField("Admin Token",  _adminToken);

            EditorGUILayout.BeginHorizontal();
            _addr  = EditorGUILayout.TextField("Address", _addr);
            _proto = EditorGUILayout.TextField("Proto",   _proto);
            _ppr   = EditorGUILayout.IntField("PPR",      _ppr);
            EditorGUILayout.EndHorizontal();
        }

        void DrawActions()
        {
            EditorGUILayout.BeginHorizontal();
            GUI.backgroundColor = _lastAlive ? Color.gray : Color.green;
            GUI.enabled = !_lastAlive;
            if (GUILayout.Button("Start Server", GUILayout.Height(28))) StartServer();
            GUI.enabled = true;
            GUI.backgroundColor = _lastAlive ? new Color(1f, 0.4f, 0.3f) : Color.gray;
            GUI.enabled = _lastAlive;
            if (GUILayout.Button("Stop Server", GUILayout.Height(28))) StopServer();
            GUI.enabled = true;
            GUI.backgroundColor = Color.white;
            EditorGUILayout.EndHorizontal();
        }

        // ===================== Server Control =====================

        string BuildCommand() =>
            string.IsNullOrEmpty(_configFile)
                ? $"cd {_serverPath} && go run ./cmd/framesync/ -addr={_addr} -proto={_proto} -ppr={_ppr}"
                : $"cd {_serverPath} && go run ./cmd/framesync/ -config={_configFile}";

        void StartServer()
        {
            var cmd = BuildCommand();
            Process.Start(new ProcessStartInfo
            {
                FileName = "osascript",
                Arguments = $"-e 'tell application \"Terminal\" to do script \"{cmd}\"'",
                UseShellExecute = false, CreateNoWindow = true,
            });
            ShowNotification(new GUIContent("Server starting..."));
            _nextCheckTime = EditorApplication.timeSinceStartup + 3.0;
        }

        void StopServer()
        {
            // 断开 WS
            _wsClient?.Disconnect();

            var gamePort = _addr.TrimStart(':');
            var adminPort = "9091";
            try { var uri = new Uri(_adminUrl); adminPort = uri.Port.ToString(); } catch { }

            try
            {
                var killCmd = $"lsof -ti:{gamePort},{adminPort} -sTCP:LISTEN | sort -u | xargs kill -9 2>/dev/null; sleep 0.3; " +
                              $"lsof -ti:{gamePort},{adminPort} -sTCP:LISTEN | sort -u | xargs kill -9 2>/dev/null";
                Process.Start(new ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    Arguments = $"-c \"{killCmd}\"",
                    UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true,
                })?.WaitForExit(3000);

                System.Threading.Thread.Sleep(500);
                var check = _client.FetchHealth();
                if (check.IsOnline)
                {
                    ShowNotification(new GUIContent("Kill failed — server still responding"));
                    UnityEngine.Debug.LogWarning("[GM] Server still alive after kill. Try manually: " +
                        $"lsof -ti:{gamePort},{adminPort} | xargs kill -9");
                    return;
                }

                _lastAlive = false; _health = default; _stats = default;
                _messages = Array.Empty<AdminClient.MsgEntry>();
                _rooms = Array.Empty<AdminClient.RoomDetail>();
                _wsMsgBuffer.Clear();
                Repaint();
                ShowNotification(new GUIContent("Server stopped"));
            }
            catch (Exception e) { UnityEngine.Debug.LogError($"[GM] Kill failed: {e.Message}"); }
        }

        // ===================== Tab 3: Deploy =====================

        void DrawDeploy()
        {
            // ===== Profile 选择器 =====
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Profile:", GUILayout.Width(50));
            int newIdx = EditorGUILayout.Popup(_deployProfileIndex, _deployProfileNames, GUILayout.Width(150));
            if (newIdx != _deployProfileIndex)
            {
                SaveDeployProfile();
                _deployProfileIndex = newIdx;
                DeployProfile.SetActiveIndex(newIdx);
                _deployProfile = DeployProfile.Load(newIdx);
            }

            if (GUILayout.Button("+ Local", GUILayout.Width(60)))
            {
                SaveDeployProfile();
                var p = DeployProfile.CreateDefaultLocal();
                int count = DeployProfile.GetProfileCount();
                DeployProfile.Save(count, p);
                DeployProfile.SetProfileCount(count + 1);
                _deployProfileIndex = count;
                DeployProfile.SetActiveIndex(count);
                _deployProfile = p;
                RefreshProfileNames();
            }
            if (GUILayout.Button("+ SSH", GUILayout.Width(55)))
            {
                SaveDeployProfile();
                var p = DeployProfile.CreateDefaultRemote();
                int count = DeployProfile.GetProfileCount();
                DeployProfile.Save(count, p);
                DeployProfile.SetProfileCount(count + 1);
                _deployProfileIndex = count;
                DeployProfile.SetActiveIndex(count);
                _deployProfile = p;
                RefreshProfileNames();
            }
            GUI.enabled = DeployProfile.GetProfileCount() > 1;
            if (GUILayout.Button("Del", GUILayout.Width(35)))
            {
                DeployProfile.Delete(_deployProfileIndex);
                _deployProfileIndex = DeployProfile.GetActiveIndex();
                _deployProfile = DeployProfile.Load(_deployProfileIndex);
                RefreshProfileNames();
            }
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();

            if (_deployProfile == null) return;

            EditorGUILayout.Space(4);

            // ===== 基本信息 =====
            _deployProfile.Name = EditorGUILayout.TextField("Name", _deployProfile.Name);
            _deployProfile.Type = (DeployProfileType)EditorGUILayout.EnumPopup("Type", _deployProfile.Type);

            _deployProfile.ConfigFile = EditorGUILayout.TextField("Config", _deployProfile.ConfigFile);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("New Env:", GUILayout.Width(55));
            _newEnvName = EditorGUILayout.TextField(_newEnvName, GUILayout.Width(100));
            if (GUILayout.Button("Create", GUILayout.Width(55)) && !string.IsNullOrEmpty(_newEnvName))
            {
                var path = DeployTool.CreateEnvConfig(_serverPath, _newEnvName);
                if (path != null)
                {
                    _deployProfile.ConfigFile = $"configs/config.{_newEnvName}.yaml";
                    ShowNotification(new GUIContent($"Created: {path}"));
                }
                else
                    ShowNotification(new GUIContent("Config already exists"));
                _newEnvName = "";
            }
            EditorGUILayout.EndHorizontal();

            _deployProfile.HealthUrl   = EditorGUILayout.TextField("Health URL",   _deployProfile.HealthUrl);
            _deployProfile.AdminToken  = EditorGUILayout.TextField("Admin Token",  _deployProfile.AdminToken);
            _deployProfile.HealthTimeoutSec = EditorGUILayout.IntField("Health Timeout (s)", _deployProfile.HealthTimeoutSec);

            // ===== Build Target =====
            EditorGUILayout.Space(4);
            if (_deployProfile.Type == DeployProfileType.Local)
            {
                EditorGUILayout.LabelField("Build Target",
                    $"{DeployProfile.DetectLocalOS()}/{DeployProfile.DetectLocalArch()} (auto)", EditorStyles.miniLabel);
            }
            else
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Target OS", GUILayout.Width(65));
                int osIdx = Array.IndexOf(DeployProfile.OSOptions, _deployProfile.TargetOS);
                if (osIdx < 0) osIdx = 0;
                osIdx = EditorGUILayout.Popup(osIdx, DeployProfile.OSOptions, GUILayout.Width(80));
                _deployProfile.TargetOS = DeployProfile.OSOptions[osIdx];

                EditorGUILayout.LabelField("Arch", GUILayout.Width(35));
                int archIdx = Array.IndexOf(DeployProfile.ArchOptions, _deployProfile.TargetArch);
                if (archIdx < 0) archIdx = 0;
                archIdx = EditorGUILayout.Popup(archIdx, DeployProfile.ArchOptions, GUILayout.Width(70));
                _deployProfile.TargetArch = DeployProfile.ArchOptions[archIdx];
                EditorGUILayout.EndHorizontal();
            }

            // ===== Remote SSH =====
            if (_deployProfile.Type == DeployProfileType.RemoteSSH)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Remote SSH", EditorStyles.boldLabel);

                EditorGUILayout.BeginHorizontal();
                _deployProfile.SshHost = EditorGUILayout.TextField("Host", _deployProfile.SshHost);
                EditorGUILayout.LabelField("Port", GUILayout.Width(30));
                _deployProfile.SshPort = EditorGUILayout.TextField(_deployProfile.SshPort, GUILayout.Width(50));
                EditorGUILayout.EndHorizontal();

                _deployProfile.SshUser = EditorGUILayout.TextField("User", _deployProfile.SshUser);
                _deployProfile.SshKeyPath = EditorGUILayout.TextField("SSH Key", _deployProfile.SshKeyPath);
                _deployProfile.RemoteBinaryPath = EditorGUILayout.TextField("Remote Bin", _deployProfile.RemoteBinaryPath);
                _deployProfile.RemoteConfigPath = EditorGUILayout.TextField("Remote Cfg", _deployProfile.RemoteConfigPath);

                EditorGUILayout.BeginHorizontal();
                _deployProfile.SystemdService = EditorGUILayout.TextField("Systemd", _deployProfile.SystemdService);
                if (GUILayout.Button("Gen .service", GUILayout.Width(85)))
                {
                    var path = DeployTool.GenSystemdService(_serverPath, _deployProfile);
                    ShowNotification(new GUIContent($"Generated: {path}"));
                }
                EditorGUILayout.EndHorizontal();
            }

            // ===== Deploy 按钮 + 状态 =====
            EditorGUILayout.Space(6);
            EditorGUILayout.BeginHorizontal();

            bool running = _deployTool != null && _deployTool.IsRunning;
            GUI.enabled = !running;
            GUI.backgroundColor = running ? Color.gray : Color.green;
            if (GUILayout.Button(running ? "Deploying..." : "Deploy", GUILayout.Height(28)))
            {
                SaveDeployProfile();
                _deployTool.StartDeploy(_deployProfile, _serverPath);
            }
            GUI.backgroundColor = Color.white;
            GUI.enabled = true;

            // Stage 指示
            if (_deployTool != null && _deployTool.Stage != DeployStage.Idle)
            {
                var stageColor = _deployTool.Stage == DeployStage.Done ? Color.green :
                                 _deployTool.Stage == DeployStage.Failed ? Color.red : Color.yellow;
                var prev = GUI.contentColor;
                GUI.contentColor = stageColor;
                EditorGUILayout.LabelField($"● {_deployTool.Stage}", EditorStyles.boldLabel, GUILayout.Width(120));
                GUI.contentColor = prev;
            }
            EditorGUILayout.EndHorizontal();

            // ===== 日志面板 =====
            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Log", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Clear", GUILayout.Width(45))) _deployTool?.ClearLog();
            if (GUILayout.Button("Copy", GUILayout.Width(40)))
            {
                if (_deployTool != null)
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var line in _deployTool.LogLines) sb.AppendLine(line);
                    GUIUtility.systemCopyBuffer = sb.ToString();
                    ShowNotification(new GUIContent("Log copied"));
                }
            }
            EditorGUILayout.EndHorizontal();

            _deployLogScroll = EditorGUILayout.BeginScrollView(_deployLogScroll);
            if (_deployTool != null)
            {
                foreach (var line in _deployTool.LogLines)
                {
                    var prev = GUI.contentColor;
                    if (line.Contains("[Error]") || line.Contains("FAILED"))
                        GUI.contentColor = Color.red;
                    else if (line.Contains("[Build]"))
                        GUI.contentColor = new Color(0.7f, 0.7f, 0.7f);
                    else if (line.Contains("[Upload]"))
                        GUI.contentColor = Color.yellow;
                    else if (line.Contains("[Health]"))
                        GUI.contentColor = Color.cyan;
                    else if (line.Contains("[Deploy] Completed"))
                        GUI.contentColor = Color.green;
                    EditorGUILayout.LabelField(line, EditorStyles.miniLabel);
                    GUI.contentColor = prev;
                }
            }
            EditorGUILayout.EndScrollView();
        }

        // ===== Server Switcher =====

        void BuildServerSwitcher()
        {
            int count = DeployProfile.GetProfileCount();
            _serverSwitcherNames = new string[count];
            _serverSwitcherIdx = 0;
            for (int i = 0; i < count; i++)
            {
                var p = DeployProfile.Load(i);
                string icon = p.Type == DeployProfileType.Local ? "◉" : "☁";
                _serverSwitcherNames[i] = $"{icon} {p.Name}";
                if (p.HealthUrl == _adminUrl)
                    _serverSwitcherIdx = i;
            }
        }

        void ApplyServerProfile(int idx)
        {
            var p = DeployProfile.Load(idx);
            _adminUrl   = p.HealthUrl;
            _adminToken = p.AdminToken;
            SavePrefs();
        }

        // ===== Deploy Profile 管理 =====

        void LoadDeployProfiles()
        {
            int count = DeployProfile.GetProfileCount();
            if (count == 0)
            {
                // 第一次：创建默认 Local profile
                var p = DeployProfile.CreateDefaultLocal();
                DeployProfile.Save(0, p);
                DeployProfile.SetProfileCount(1);
                count = 1;
            }
            _deployProfileIndex = DeployProfile.GetActiveIndex();
            if (_deployProfileIndex >= count) _deployProfileIndex = 0;
            _deployProfile = DeployProfile.Load(_deployProfileIndex);
            RefreshProfileNames();
        }

        void RefreshProfileNames()
        {
            int count = DeployProfile.GetProfileCount();
            _deployProfileNames = new string[count];
            for (int i = 0; i < count; i++)
            {
                var p = DeployProfile.Load(i);
                _deployProfileNames[i] = $"{p.Name} ({p.Type})";
            }
            BuildServerSwitcher();
        }

        void SaveDeployProfile()
        {
            if (_deployProfile != null)
            {
                DeployProfile.Save(_deployProfileIndex, _deployProfile);
                BuildServerSwitcher();
            }
        }

        // ===================== EditorPrefs =====================

        void LoadPrefs()
        {
            _serverPath = EditorPrefs.GetString(PP + "serverPath", "/Users/boom/Demo/BoomNetwork/svr");
            _configFile = EditorPrefs.GetString(PP + "configFile", "cmd/framesync/config.yaml");
            _addr       = EditorPrefs.GetString(PP + "addr",       ":9000");
            _proto      = EditorPrefs.GetString(PP + "proto",      "tcp");
            _ppr        = EditorPrefs.GetInt(PP + "ppr",           2);
            _adminUrl   = EditorPrefs.GetString(PP + "adminUrl",   "http://127.0.0.1:9091");
            _adminToken = EditorPrefs.GetString(PP + "adminToken", "");
        }

        void SavePrefs()
        {
            EditorPrefs.SetString(PP + "serverPath", _serverPath);
            EditorPrefs.SetString(PP + "configFile", _configFile);
            EditorPrefs.SetString(PP + "addr",       _addr);
            EditorPrefs.SetString(PP + "proto",      _proto);
            EditorPrefs.SetInt(PP + "ppr",           _ppr);
            EditorPrefs.SetString(PP + "adminUrl",   _adminUrl);
            EditorPrefs.SetString(PP + "adminToken", _adminToken);
        }
    }
}
