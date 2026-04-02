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
        private double _stopCooldownUntil; // Stop 后抑制重连的截止时间

        // Perf (Monitor tab)
        private GmPerfPush _perf, _prevPerf;
        private bool _perfHasData;
        private bool _perfFoldout = true;

        // Hot Players / Rates (Monitor tab)
        private List<GmPlayerRate> _hotPlayers = new List<GmPlayerRate>();
        private bool _hotPlayersFoldout = true;

        // Log Level (Control tab)
        private string _logLevel = "";
        private string _logLevelPending = "";
        private static readonly string[] LogLevelOptions = { "DEBUG", "INFO", "WARN", "ERROR" };

        // Room Inspect (Rooms tab, Phase 3)
        private readonly HashSet<int> _expandedRooms = new HashSet<int>();
        private readonly Dictionary<int, GmRoomInspect> _roomInspectCache = new Dictionary<int, GmRoomInspect>();

        // Monitor scroll
        private Vector2 _monitorScroll;

        // Charts (Monitor tab, Phase 4)
        private const int CHART_SAMPLES = 60;
        private readonly float[] _chartGameTx = new float[CHART_SAMPLES];
        private readonly float[] _chartGameRx = new float[CHART_SAMPLES];
        private readonly float[] _chartPlayers = new float[CHART_SAMPLES];
        private readonly float[] _chartHeap = new float[CHART_SAMPLES];
        private readonly float[] _chartGoroutines = new float[CHART_SAMPLES];
        private readonly float[] _chartGcPause = new float[CHART_SAMPLES];
        private int _chartHead;
        private bool _chartFoldout = true;

        // WS 消息缓冲（追加模式，最多保留 200 条）
        private readonly List<AdminClient.MsgEntry> _wsMsgBuffer = new List<AdminClient.MsgEntry>();
        private const int WS_MSG_BUFFER_MAX = 200;

        // ===== Tab =====
        private int _tab;
        private static readonly string[] TabNames = { "Monitor", "Messages", "Rooms", "Control", "Deploy", "Logs" };

        // ===== Logs Tab =====
        private readonly List<GmLogEntry> _logEntries = new List<GmLogEntry>();
        private const int LOG_BUFFER_MAX = 500;
        private Vector2 _logScroll;
        private bool _logAutoScroll = true;
        private int _logLevelFilter; // 0=All, 1=INFO+, 2=WARN+, 3=ERROR
        private static readonly string[] LogLevelFilterNames = { "All", "INFO+", "WARN+", "ERROR" };

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
        private string _filterPlayer = "";
        private string _filterRoom = "";
        private string _filterMatchKey = "";
        private int _filterDir; // 0=All, 1=↓rx, 2=↑tx
        private static readonly string[] DirFilterNames = { "All", "↓ rx", "↑ tx" };

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
            // 启动时同步当前 profile 的 token（EditorPrefs 可能是旧值）
            ApplyServerProfile(_serverSwitcherIdx);
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
                        // Stop 冷却期内不重连（等 SSH stop 命令完成）
                        if (EditorApplication.timeSinceStartup < _stopCooldownUntil)
                        {
                            _health = default; // 冷却期内视为离线，防止状态栏闪现 RUNNING
                            goto skipReconnect;
                        }

                        // 服务器在线且 WS 未连接/未连接中 → 启动 WS
                        if (!_wsClient.IsConnecting)
                            _wsClient.Connect(_adminUrl, _adminToken);

                        // HTTP fallback 拉数据
                        _stats = _client.FetchStats();
                        if (_tab == 1 && !_msgPaused)
                            _messages = _client.FetchMessages(100);
                        if (_tab == 2)
                            _rooms = _client.FetchRooms();
                        if (_tab == 0)
                        {
                            var pr = _client.FetchPerf();
                            if (pr.HasData)
                            {
                                _prevPerf = _perf;
                                _perfHasData = true;
                                _perf = new GmPerfPush
                                {
                                    Goroutines = pr.Goroutines, HeapMB = pr.HeapMB, SysMB = pr.SysMB,
                                    GCCount = pr.GCCount, GCPauseUs = pr.GCPauseUs,
                                    Rooms = pr.Rooms, Players = pr.Players,
                                };
                            }
                            var rr = _client.FetchRates();
                            if (rr.HasData)
                                _hotPlayers = rr.Top;
                        }
                        if (string.IsNullOrEmpty(_logLevel))
                        {
                            var lr = _client.GetLogLevel();
                            if (lr.HasData) { _logLevel = lr.Level; _logLevelPending = lr.Level; }
                        }
                    }
                    else
                    {
                        _stats = default;
                        _messages = Array.Empty<AdminClient.MsgEntry>();
                        _rooms = Array.Empty<AdminClient.RoomDetail>();
                        _wsMsgBuffer.Clear(); _logEntries.Clear();
                        _perfHasData = false; _perf = default; _prevPerf = default;
                        _hotPlayers.Clear();
                        _logLevel = ""; _logLevelPending = "";
                        _expandedRooms.Clear(); _roomInspectCache.Clear();
                        Array.Clear(_chartGameTx, 0, CHART_SAMPLES); Array.Clear(_chartGameRx, 0, CHART_SAMPLES);
                        Array.Clear(_chartPlayers, 0, CHART_SAMPLES); Array.Clear(_chartHeap, 0, CHART_SAMPLES);
                        Array.Clear(_chartGoroutines, 0, CHART_SAMPLES); Array.Clear(_chartGcPause, 0, CHART_SAMPLES);
                        _chartHead = 0;
                    }
                    skipReconnect:
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
                // 部分 topic（rooms 等）的 payload 是 array 不是 map，DecodePayload 会返回 null
                // 先尝试 DecodePayload，null 时也继续处理（由各 case 自行解码）
                var payload = env.DecodePayload();

                switch (env.Topic)
                {
                    case GmTopics.Health:
                        if (payload == null) break;
                        var hp = GmHealthPush.From(payload);
                        _health = new AdminClient.HealthResult
                        {
                            IsOnline = true,
                            Rooms = hp.Rooms,
                            Players = hp.Players,
                            Uptime = hp.Uptime,
                        };
                        _chartPlayers[_chartHead] = hp.Players;
                        break;

                    case GmTopics.Stats:
                        if (payload == null) break;
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
                        ChartPush(sp.GameTx5Sec / 5f, sp.GameRx5Sec / 5f);
                        break;

                    case GmTopics.Messages:
                        if (payload == null) break;
                        if (!_msgPaused)
                        {
                            var me = GmMsgEntry.From(payload);
                            _wsMsgBuffer.Add(new AdminClient.MsgEntry
                            {
                                Ts = me.Ts, Dir = me.Dir, Cmd = me.Cmd,
                                Name = me.Name, Pid = me.Pid, Size = me.Size,
                                RoomID = me.RoomID, MatchKey = me.MatchKey,
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
                                        TotalPlayers = grd.TotalPlayers, MatchKey = grd.MatchKey ?? "",
                                        EmptyAt = grd.EmptyAt,
                                        EmptyGraceSec = grd.EmptyGraceSec,
                                        DisconnectKeepSec = grd.DisconnectKeepSec,
                                        CreatedAt = grd.CreatedAt,
                                        HadPlayer = grd.HadPlayer,
                                        RoomCleanupSec = grd.RoomCleanupSec,
                                    };
                                    if (grd.Players != null)
                                    {
                                        rd.Players = new AdminClient.PlayerInfo[grd.Players.Length];
                                        for (int i = 0; i < grd.Players.Length; i++)
                                            rd.Players[i] = new AdminClient.PlayerInfo
                                            {
                                                Id = grd.Players[i].Id,
                                                State = grd.Players[i].State,
                                                DisconnectTime = grd.Players[i].DisconnectTime,
                                            };
                                    }
                                    else rd.Players = Array.Empty<AdminClient.PlayerInfo>();
                                    rooms.Add(rd);
                                }
                            }
                            rooms.Sort((a, b) => a.Id.CompareTo(b.Id));
                            _rooms = rooms.ToArray();
                        }
                        break;

                    case GmTopics.Netsim:
                        if (payload == null) break;
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

                    case GmTopics.Perf:
                        if (payload == null) break;
                        _prevPerf = _perf;
                        _perf = GmPerfPush.From(payload);
                        _perfHasData = true;
                        _chartHeap[_chartHead] = (float)_perf.HeapMB;
                        _chartGoroutines[_chartHead] = _perf.Goroutines;
                        _chartGcPause[_chartHead] = _perf.GCPauseUs / 1000f;
                        break;

                    case GmTopics.Rates:
                        if (payload == null) break;
                        var rp = GmRatesPush.From(payload);
                        _hotPlayers = rp.Top ?? new List<GmPlayerRate>();
                        break;

                    case GmTopics.Logs:
                        if (payload == null) break;
                        var le = GmLogEntry.From(payload);
                        if (PassesLogLevelFilter(le.Level))
                        {
                            _logEntries.Add(le);
                            if (_logEntries.Count > LOG_BUFFER_MAX)
                                _logEntries.RemoveAt(0);
                        }
                        break;
                }
            }
            else if (env.Type == "rsp")
            {
                var payload = env.DecodePayload();
                if (payload == null) return;

                if (env.Topic == "inspect_room")
                {
                    var inspect = GmRoomInspect.From(payload);
                    if (inspect.HasData)
                        _roomInspectCache[inspect.Id] = inspect;
                }
                else if (MsgPackLite.GetBool(payload, "ok"))
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
                case 0: DrawMonitor(); break;
                case 1: DrawMessages(); break;
                case 2: DrawRooms(); break;
                case 3: DrawControl(); break;
                case 4: DrawDeploy(); break;
                case 5: DrawLogs(); break;
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

        // ===================== Tab 0: Monitor =====================

        void DrawMonitor()
        {
            _monitorScroll = EditorGUILayout.BeginScrollView(_monitorScroll);

            // ===== Traffic =====
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

            // ===== Traffic Charts =====
            if (_lastAlive && _stats.HasData)
            {
                EditorGUILayout.Space(6);
                _chartFoldout = EditorGUILayout.Foldout(_chartFoldout, "Traffic Charts", true);
                if (_chartFoldout)
                {
                    var txRect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none,
                        GUILayout.Height(50), GUILayout.ExpandWidth(true));
                    DrawSparkline(txRect, _chartGameTx, _chartHead, CHART_SAMPLES,
                        new Color(0.4f, 0.8f, 1f), new Color(0.2f, 0.4f, 0.6f, 0.3f), "TX B/s");

                    EditorGUILayout.Space(2);
                    var rxRect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none,
                        GUILayout.Height(50), GUILayout.ExpandWidth(true));
                    DrawSparkline(rxRect, _chartGameRx, _chartHead, CHART_SAMPLES,
                        new Color(0.5f, 1f, 0.5f), new Color(0.25f, 0.5f, 0.25f, 0.3f), "RX B/s");

                    EditorGUILayout.Space(2);
                    var pRect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none,
                        GUILayout.Height(50), GUILayout.ExpandWidth(true));
                    DrawSparkline(pRect, _chartPlayers, _chartHead, CHART_SAMPLES,
                        Color.yellow, new Color(0.5f, 0.5f, 0f, 0.2f), "Players");
                }
            }

            // ===== Runtime Performance (summary + sparkline detail) =====
            if (_lastAlive && _perfHasData)
            {
                EditorGUILayout.Space(6);

                // Summary line — always visible
                EditorGUILayout.BeginHorizontal();
                var gcMs = _perf.GCPauseUs / 1000f;
                var summaryLabel = $"Runtime   Goroutines:{_perf.Goroutines}  Heap:{_perf.HeapMB:F1}MB  Sys:{_perf.SysMB:F1}MB  GC:{gcMs:F1}ms";

                // Color the summary based on worst metric
                var prevC = GUI.contentColor;
                GUI.contentColor = _perf.Goroutines > 200 || _perf.HeapMB > 100 || gcMs > 5
                    ? Color.red
                    : _perf.Goroutines > 100 || _perf.HeapMB > 50 || gcMs > 2
                        ? Color.yellow
                        : Color.white;
                EditorGUILayout.LabelField(summaryLabel, EditorStyles.boldLabel);
                GUI.contentColor = prevC;
                EditorGUILayout.EndHorizontal();

                // Foldout for sparkline detail
                _perfFoldout = EditorGUILayout.Foldout(_perfFoldout, "Detail", true);
                if (_perfFoldout)
                {
                    var grRect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none,
                        GUILayout.Height(44), GUILayout.ExpandWidth(true));
                    DrawSparkline(grRect, _chartGoroutines, _chartHead, CHART_SAMPLES,
                        Color.cyan, new Color(0f, 0.5f, 0.5f, 0.2f), "Goroutines");

                    EditorGUILayout.Space(2);
                    var hRect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none,
                        GUILayout.Height(44), GUILayout.ExpandWidth(true));
                    DrawSparkline(hRect, _chartHeap, _chartHead, CHART_SAMPLES,
                        new Color(1f, 0.6f, 0.4f), new Color(0.5f, 0.3f, 0.2f, 0.3f), "Heap MB");

                    EditorGUILayout.Space(2);
                    var gcRect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none,
                        GUILayout.Height(44), GUILayout.ExpandWidth(true));
                    DrawSparkline(gcRect, _chartGcPause, _chartHead, CHART_SAMPLES,
                        new Color(1f, 0.4f, 0.4f), new Color(0.5f, 0.2f, 0.2f, 0.2f), "GC Pause ms");
                }
            }

            // ===== Hot Players =====
            if (_lastAlive && _hotPlayers.Count > 0)
            {
                EditorGUILayout.Space(6);
                _hotPlayersFoldout = EditorGUILayout.Foldout(_hotPlayersFoldout,
                    $"Hot Players (top {_hotPlayers.Count})", true);
                if (_hotPlayersFoldout)
                {
                    EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
                    EditorGUILayout.LabelField("PID", EditorStyles.miniLabel, GUILayout.Width(50));
                    EditorGUILayout.LabelField("Msg/5s", EditorStyles.miniLabel, GUILayout.Width(60));
                    EditorGUILayout.LabelField("", GUILayout.ExpandWidth(true));
                    EditorGUILayout.EndHorizontal();

                    foreach (var hp in _hotPlayers)
                    {
                        EditorGUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField($"P{hp.Pid}", GUILayout.Width(50));

                        var prev = GUI.contentColor;
                        GUI.contentColor = hp.MsgPer5Sec > 20 ? Color.red
                                         : hp.MsgPer5Sec > 10 ? Color.yellow
                                         : Color.white;
                        EditorGUILayout.LabelField($"{hp.MsgPer5Sec}", GUILayout.Width(60));
                        GUI.contentColor = prev;

                        GUI.backgroundColor = new Color(1f, 0.6f, 0.3f);
                        if (GUILayout.Button("Kick", GUILayout.Width(40)))
                            DoKickPlayer(hp.Pid);
                        GUI.backgroundColor = Color.white;
                        EditorGUILayout.EndHorizontal();
                    }
                }
            }
            else if (_lastAlive && _perfHasData)
            {
                EditorGUILayout.Space(4);
                var prev = GUI.contentColor;
                GUI.contentColor = Color.gray;
                EditorGUILayout.LabelField("No hot players", EditorStyles.miniLabel);
                GUI.contentColor = prev;
            }

            EditorGUILayout.EndScrollView();
        }

        static void PerfRow(string label, int current, int prev, string suffix = "")
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, GUILayout.Width(90));
            string trend = current > prev ? " ▲" : current < prev ? " ▼" : "";
            var tc = GUI.contentColor;
            GUI.contentColor = current > prev ? new Color(1f, 0.6f, 0.4f)
                             : current < prev ? new Color(0.6f, 1f, 0.6f)
                             : Color.white;
            EditorGUILayout.LabelField($"{current}{suffix}{trend}");
            GUI.contentColor = tc;
            EditorGUILayout.EndHorizontal();
        }

        static void PerfRowDouble(string label, string display, double current, double prev)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, GUILayout.Width(90));
            string trend = current > prev + 0.01 ? " ▲" : current < prev - 0.01 ? " ▼" : "";
            var tc = GUI.contentColor;
            GUI.contentColor = current > prev + 0.01 ? new Color(1f, 0.6f, 0.4f)
                             : current < prev - 0.01 ? new Color(0.6f, 1f, 0.6f)
                             : Color.white;
            EditorGUILayout.LabelField($"{display}{trend}");
            GUI.contentColor = tc;
            EditorGUILayout.EndHorizontal();
        }

        // ===================== Chart Helpers (Phase 4) =====================

        void ChartPush(float tx, float rx)
        {
            _chartGameTx[_chartHead] = tx;
            _chartGameRx[_chartHead] = rx;
            _chartHead = (_chartHead + 1) % CHART_SAMPLES;
        }

        static void DrawSparkline(Rect rect, float[] ring, int head, int count, Color lineColor, Color fillColor, string label)
        {
            if (Event.current.type != EventType.Repaint) return;

            // Find max for Y-axis scaling
            float max = 1f;
            for (int i = 0; i < count; i++)
                if (ring[i] > max) max = ring[i];

            // Background
            EditorGUI.DrawRect(rect, new Color(0.15f, 0.15f, 0.15f, 0.8f));

            // Draw polyline
            var points = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                int idx = (head + i) % count;
                float x = rect.x + (float)i / (count - 1) * rect.width;
                float y = rect.yMax - (ring[idx] / max) * (rect.height - 4) - 2;
                points[i] = new Vector3(x, y, 0);
            }

            // Fill area
            GUI.BeginClip(rect);
            UnityEditor.Handles.color = fillColor;
            for (int i = 0; i < count - 1; i++)
            {
                var p1 = points[i] - new Vector3(rect.x, rect.y, 0);
                var p2 = points[i + 1] - new Vector3(rect.x, rect.y, 0);
                var b1 = new Vector3(p1.x, rect.height, 0);
                var b2 = new Vector3(p2.x, rect.height, 0);
                UnityEditor.Handles.DrawAAConvexPolygon(p1, p2, b2, b1);
            }
            GUI.EndClip();

            // Line on top
            UnityEditor.Handles.color = lineColor;
            UnityEditor.Handles.DrawAAPolyLine(2f, points);

            // Label + current value
            int lastIdx = (head - 1 + count) % count;
            float current = ring[lastIdx];
            var labelStyle = EditorStyles.miniLabel;
            var prev = GUI.contentColor;
            GUI.contentColor = Color.white;
            GUI.Label(new Rect(rect.x + 4, rect.y + 1, rect.width, 14), $"{label}: {current:F0}", labelStyle);
            GUI.Label(new Rect(rect.x + 4, rect.yMax - 14, rect.width, 14), $"max: {max:F0}", labelStyle);
            GUI.contentColor = prev;
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

        // ===================== Tab 3: Control =====================

        void DrawControl()
        {
            // ===== Network Simulation =====
            if (_lastAlive)
                DrawNetSim();

            // ===== Log Level =====
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Log Level", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();

            int curIdx = Array.IndexOf(LogLevelOptions, _logLevelPending.ToUpperInvariant());
            if (curIdx < 0) curIdx = 1; // default INFO
            int newLvlIdx = EditorGUILayout.Popup(curIdx, LogLevelOptions, GUILayout.Width(80));
            _logLevelPending = LogLevelOptions[newLvlIdx];

            bool lvlDirty = !string.Equals(_logLevelPending, _logLevel, StringComparison.OrdinalIgnoreCase);
            GUI.enabled = lvlDirty && _lastAlive;
            GUI.backgroundColor = lvlDirty ? Color.yellow : Color.gray;
            if (GUILayout.Button("Apply", GUILayout.Width(50)))
            {
                var r = _client.SetLogLevel(_logLevelPending.ToLowerInvariant());
                if (r.Ok) { _logLevel = _logLevelPending; ShowNotification(new GUIContent("Log level set")); }
                else ShowNotification(new GUIContent($"Error: {r.Error}"));
            }
            GUI.backgroundColor = Color.white;
            GUI.enabled = true;

            if (!string.IsNullOrEmpty(_logLevel))
            {
                var prev = GUI.contentColor;
                GUI.contentColor = Color.gray;
                EditorGUILayout.LabelField($"(server: {_logLevel})", EditorStyles.miniLabel);
                GUI.contentColor = prev;
            }
            EditorGUILayout.EndHorizontal();

            // ===== Config Reload =====
            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            GUI.enabled = _lastAlive;
            GUI.backgroundColor = new Color(0.6f, 0.8f, 1f);
            if (GUILayout.Button("Reload Config", GUILayout.Width(110)))
            {
                var r = _client.ReloadConfig();
                ShowNotification(new GUIContent(r.Ok ? "Config reloaded" : $"Error: {r.Error}"));
            }
            GUI.backgroundColor = Color.white;
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();

            // ===== Config + Actions + Manual Command =====
            EditorGUILayout.Space(6);
            DrawConfig();
            EditorGUILayout.Space(6);
            DrawActions();

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

        // ===================== Tab 1: Messages =====================

        void DrawMessages()
        {
            // Toolbar row 1: Cmd filter + HB + controls
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Cmd:", GUILayout.Width(30));
            int filterIdx = Array.IndexOf(_cmdFilterValues, _cmdFilter);
            if (filterIdx < 0) filterIdx = 0;
            int newIdx = EditorGUILayout.Popup(filterIdx, _cmdFilterNames, GUILayout.Width(120));
            _cmdFilter = _cmdFilterValues[newIdx];

            _hideHeartbeat = GUILayout.Toggle(_hideHeartbeat, "Hide HB", GUILayout.Width(65));

            GUILayout.FlexibleSpace();

            if (GUILayout.Button(_msgPaused ? "▶" : "❚❚", GUILayout.Width(30)))
                _msgPaused = !_msgPaused;

            if (GUILayout.Button("Copy", GUILayout.Width(40)))
                CopyMessages();

            EditorGUILayout.LabelField($"{_messages.Length}", GUILayout.Width(30));
            EditorGUILayout.EndHorizontal();

            // Toolbar row 2: Dir + Player + Room + MatchKey + Clear
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Dir:", GUILayout.Width(24));
            _filterDir = EditorGUILayout.Popup(_filterDir, DirFilterNames, GUILayout.Width(55));

            EditorGUILayout.LabelField("Player:", GUILayout.Width(42));
            _filterPlayer = EditorGUILayout.TextField(_filterPlayer, GUILayout.Width(40));

            EditorGUILayout.LabelField("Room:", GUILayout.Width(36));
            _filterRoom = EditorGUILayout.TextField(_filterRoom, GUILayout.Width(40));

            EditorGUILayout.LabelField("Key:", GUILayout.Width(26));
            _filterMatchKey = EditorGUILayout.TextField(_filterMatchKey, GUILayout.Width(60));

            if (GUILayout.Button("Clear", GUILayout.Width(42)))
            {
                _filterDir = 0;
                _filterPlayer = "";
                _filterRoom = "";
                _filterMatchKey = "";
                _cmdFilter = -1;
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(2);

            // Header
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            EditorGUILayout.LabelField("Time",    EditorStyles.miniLabel, GUILayout.Width(60));
            EditorGUILayout.LabelField("Dir",     EditorStyles.miniLabel, GUILayout.Width(22));
            EditorGUILayout.LabelField("Command", EditorStyles.miniLabel, GUILayout.Width(115));
            EditorGUILayout.LabelField("Room",    EditorStyles.miniLabel, GUILayout.Width(40));
            EditorGUILayout.LabelField("P",       EditorStyles.miniLabel, GUILayout.Width(30));
            EditorGUILayout.LabelField("Size",    EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();

            _msgScroll = EditorGUILayout.BeginScrollView(_msgScroll);

            var filtered = _messages.Where(m =>
            {
                if (_hideHeartbeat && (m.Cmd == 7 || m.Cmd == 8)) return false;
                if (_cmdFilter >= 0 && m.Cmd != _cmdFilter) return false;
                if (_filterDir == 1 && m.Dir != "rx") return false;
                if (_filterDir == 2 && m.Dir != "tx") return false;
                if (_filterPlayer.Length > 0 && !m.Pid.ToString().Contains(_filterPlayer)) return false;
                if (_filterRoom.Length > 0 && !m.RoomID.ToString().Contains(_filterRoom)) return false;
                if (_filterMatchKey.Length > 0 && (m.MatchKey == null || !m.MatchKey.Contains(_filterMatchKey))) return false;
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
                EditorGUILayout.LabelField(msg.RoomID > 0 ? $"R{msg.RoomID}" : "—", GUILayout.Width(40));
                EditorGUILayout.LabelField(msg.Pid > 0 ? $"P{msg.Pid}" : "—", GUILayout.Width(30));
                EditorGUILayout.LabelField(AdminClient.FmtBytes(msg.Size));
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndScrollView();
        }

        void CopyMessages()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Time\tDir\tCommand\tRoom\tPlayer\tMatchKey\tSize");
            foreach (var m in _messages)
            {
                var dt = DateTimeOffset.FromUnixTimeMilliseconds(m.Ts).LocalDateTime;
                var room = m.RoomID > 0 ? $"R{m.RoomID}" : "—";
                var key = m.MatchKey ?? "";
                sb.AppendLine($"{dt:HH:mm:ss}\t{m.Dir}\t{m.Name}\t{room}\tP{m.Pid}\t{key}\t{m.Size}");
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

        private int _createRoomMaxPlayers = 2;
        private string _createRoomMatchKey = "";

        void DrawRooms()
        {
            if (!_lastAlive) { EditorGUILayout.HelpBox("Server offline", MessageType.Warning); return; }

            // Create room toolbar
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Max:", GUILayout.Width(30));
            _createRoomMaxPlayers = EditorGUILayout.IntField(_createRoomMaxPlayers, GUILayout.Width(30));
            EditorGUILayout.LabelField("Key:", GUILayout.Width(26));
            _createRoomMatchKey = EditorGUILayout.TextField(_createRoomMatchKey, GUILayout.Width(80));
            GUI.backgroundColor = new Color(0.4f, 0.9f, 0.4f);
            if (GUILayout.Button("Create Room", GUILayout.Width(90)))
                DoCreateRoom(_createRoomMaxPlayers, _createRoomMatchKey);
            GUI.backgroundColor = Color.white;
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField($"{_rooms.Length} rooms", GUILayout.Width(60));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(2);

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
                EditorGUILayout.LabelField(
                    $"Room {room.Id}  [{status}]  F#{room.FrameNumber}  {room.OnlineCount}/{room.MaxPlayers}p  {room.FrameRate}fps",
                    EditorStyles.boldLabel);
                GUI.contentColor = prev;

                // Stop room button
                GUI.backgroundColor = new Color(1f, 0.4f, 0.3f);
                GUI.enabled = room.Running;
                if (GUILayout.Button("Stop", GUILayout.Width(45)))
                    DoStopRoom(room.Id);
                GUI.enabled = true;

                // Kill room button (force destroy)
                GUI.backgroundColor = new Color(0.8f, 0.1f, 0.1f);
                if (GUILayout.Button("Kill", GUILayout.Width(40)))
                    DoKillRoom(room.Id);
                GUI.backgroundColor = Color.white;
                EditorGUILayout.EndHorizontal();

                // Sub-row: Total players + MatchKey + Paused warning
                EditorGUILayout.BeginHorizontal();
                EditorGUI.indentLevel++;
                var subPrev = GUI.contentColor;
                GUI.contentColor = Color.gray;
                EditorGUILayout.LabelField($"Total: {room.TotalPlayers}", EditorStyles.miniLabel, GUILayout.Width(70));
                GUI.contentColor = subPrev;

                if (!string.IsNullOrEmpty(room.MatchKey))
                {
                    subPrev = GUI.contentColor;
                    GUI.contentColor = new Color(0.6f, 0.8f, 1f);
                    EditorGUILayout.LabelField($"[{room.MatchKey}]", EditorStyles.miniLabel, GUILayout.Width(100));
                    GUI.contentColor = subPrev;
                }

                if (room.Paused)
                {
                    subPrev = GUI.contentColor;
                    GUI.contentColor = Color.yellow;
                    EditorGUILayout.LabelField("SNAPSHOT PAUSED", EditorStyles.miniLabel);
                    GUI.contentColor = subPrev;
                }

                // 房间销毁倒计时（所有玩家已离开）
                if (room.EmptyAt > 0 && room.EmptyGraceSec > 0)
                {
                    long nowMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    float destroyIn = (room.EmptyGraceSec * 1000f - (nowMs - room.EmptyAt)) / 1000f;
                    subPrev = GUI.contentColor;
                    GUI.contentColor = new Color(1f, 0.5f, 0.1f);
                    string destroyLabel = destroyIn > 0
                        ? $"DESTROY IN {destroyIn:F0}s"
                        : "DESTROY PENDING";
                    EditorGUILayout.LabelField(destroyLabel, EditorStyles.miniLabel);
                    GUI.contentColor = subPrev;
                }
                // 从未有玩家加入的空房间销毁倒计时（兜底清理）
                else if (!room.HadPlayer && room.CreatedAt > 0 && room.RoomCleanupSec > 0)
                {
                    long nowMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    float destroyIn = (room.RoomCleanupSec * 1000f - (nowMs - room.CreatedAt)) / 1000f;
                    subPrev = GUI.contentColor;
                    GUI.contentColor = new Color(1f, 0.5f, 0.1f);
                    string destroyLabel = destroyIn > 0
                        ? $"DESTROY IN {destroyIn:F0}s"
                        : "DESTROY PENDING";
                    EditorGUILayout.LabelField(destroyLabel, EditorStyles.miniLabel);
                    GUI.contentColor = subPrev;
                }

                EditorGUI.indentLevel--;
                EditorGUILayout.EndHorizontal();

                // Detail foldout (Phase 3)
                bool wasExpanded = _expandedRooms.Contains(room.Id);
                EditorGUI.indentLevel++;
                bool isExpanded = EditorGUILayout.Foldout(wasExpanded, "Detail", true);
                EditorGUI.indentLevel--;

                if (isExpanded != wasExpanded)
                {
                    if (isExpanded)
                    {
                        _expandedRooms.Add(room.Id);
                        // Request inspect data
                        if (_wsClient != null && _wsClient.IsConnected)
                            _wsClient.SendRpc("inspect_room", new Dictionary<string, object> { ["room_id"] = room.Id });
                        else
                        {
                            var ir = _client.FetchRoomInspect(room.Id);
                            if (ir.HasData)
                                _roomInspectCache[room.Id] = new GmRoomInspect
                                {
                                    HasData = true, Id = room.Id,
                                    FrameBufferLen = ir.FrameBufferLen, FrameBufferCap = ir.FrameBufferCap,
                                    OldestBufferedFrame = ir.OldestBufferedFrame,
                                    SnapshotFrame = ir.SnapshotFrame, SnapshotSizeBytes = ir.SnapshotSizeBytes,
                                    SnapshotStaleFrames = ir.SnapshotStaleFrames, DataVersion = ir.DataVersion,
                                    EntityAuthority = ir.EntityAuthority, KVEntries = ir.KVEntries,
                                };
                        }
                    }
                    else _expandedRooms.Remove(room.Id);
                }

                if (isExpanded && _roomInspectCache.TryGetValue(room.Id, out var insp))
                {
                    DrawRoomInspectDetail(insp);

                    // Refresh button
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Space(20);
                    if (GUILayout.Button("Refresh", GUILayout.Width(60)))
                    {
                        if (_wsClient != null && _wsClient.IsConnected)
                            _wsClient.SendRpc("inspect_room", new Dictionary<string, object> { ["room_id"] = room.Id });
                        else
                        {
                            var ir2 = _client.FetchRoomInspect(room.Id);
                            if (ir2.HasData)
                                _roomInspectCache[room.Id] = new GmRoomInspect
                                {
                                    HasData = true, Id = room.Id,
                                    FrameBufferLen = ir2.FrameBufferLen, FrameBufferCap = ir2.FrameBufferCap,
                                    OldestBufferedFrame = ir2.OldestBufferedFrame,
                                    SnapshotFrame = ir2.SnapshotFrame, SnapshotSizeBytes = ir2.SnapshotSizeBytes,
                                    SnapshotStaleFrames = ir2.SnapshotStaleFrames, DataVersion = ir2.DataVersion,
                                    EntityAuthority = ir2.EntityAuthority, KVEntries = ir2.KVEntries,
                                };
                        }
                    }
                    EditorGUILayout.EndHorizontal();
                }
                else if (isExpanded)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.LabelField("Loading...", EditorStyles.miniLabel);
                    EditorGUI.indentLevel--;
                }

                // Player list
                if (room.Players != null)
                {
                    EditorGUI.indentLevel++;
                    long nowMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    foreach (var p in room.Players)
                    {
                        EditorGUILayout.BeginHorizontal();
                        bool online = p.State == 0;
                        var pPrev = GUI.contentColor;

                        string playerLabel;
                        if (online)
                        {
                            GUI.contentColor = Color.green;
                            playerLabel = $"P{p.Id} online";
                        }
                        else if (p.DisconnectTime > 0 && room.DisconnectKeepSec > 0)
                        {
                            float kickIn = (room.DisconnectKeepSec * 1000f - (nowMs - p.DisconnectTime)) / 1000f;
                            GUI.contentColor = Color.red;
                            playerLabel = kickIn > 0
                                ? $"P{p.Id} OFFLINE  kick in {kickIn:F0}s"
                                : $"P{p.Id} OFFLINE  kicking...";
                        }
                        else
                        {
                            GUI.contentColor = Color.red;
                            playerLabel = $"P{p.Id} OFFLINE";
                        }

                        EditorGUILayout.LabelField(playerLabel, GUILayout.Width(190));
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

        void DrawRoomInspectDetail(GmRoomInspect insp)
        {
            EditorGUI.indentLevel += 2;

            // Frame Buffer — progress bar style
            float bufPct = insp.FrameBufferCap > 0 ? (float)insp.FrameBufferLen / insp.FrameBufferCap : 0;
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Frame Buffer:", GUILayout.Width(90));
            var barRect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.Height(14), GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(barRect, new Color(0.2f, 0.2f, 0.2f));
                var fillColor = bufPct > 0.9f ? Color.red : bufPct > 0.7f ? Color.yellow : Color.green;
                EditorGUI.DrawRect(new Rect(barRect.x, barRect.y, barRect.width * bufPct, barRect.height), fillColor);
            }
            EditorGUILayout.LabelField($"{insp.FrameBufferLen}/{insp.FrameBufferCap} ({bufPct:P0})", EditorStyles.miniLabel, GUILayout.Width(100));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField($"Oldest Frame: F#{insp.OldestBufferedFrame}", EditorStyles.miniLabel);

            // Snapshot
            var snapColor = insp.SnapshotStaleFrames > 200 ? Color.red : insp.SnapshotStaleFrames > 50 ? Color.yellow : Color.white;
            var prevC = GUI.contentColor;
            GUI.contentColor = snapColor;
            EditorGUILayout.LabelField(
                $"Snapshot: F#{insp.SnapshotFrame}  {AdminClient.FmtBytes(insp.SnapshotSizeBytes)}  Stale: {insp.SnapshotStaleFrames} frames",
                EditorStyles.miniLabel);
            GUI.contentColor = prevC;

            // Entity Authority
            if (insp.EntityAuthority != null && insp.EntityAuthority.Length > 0)
            {
                EditorGUILayout.LabelField($"Entity Authority ({insp.EntityAuthority.Length}):", EditorStyles.miniLabel);
                EditorGUI.indentLevel++;
                var sb = new System.Text.StringBuilder();
                foreach (var ea in insp.EntityAuthority)
                {
                    if (sb.Length > 0) sb.Append("  ");
                    sb.Append($"E{ea.EntityId}→{(ea.OwnerId > 0 ? $"P{ea.OwnerId}" : "none")}");
                }
                EditorGUILayout.LabelField(sb.ToString(), EditorStyles.miniLabel);
                EditorGUI.indentLevel--;
            }
            else
            {
                prevC = GUI.contentColor;
                GUI.contentColor = Color.gray;
                EditorGUILayout.LabelField("No entities", EditorStyles.miniLabel);
                GUI.contentColor = prevC;
            }

            // KV Store
            if (insp.KVEntries != null && insp.KVEntries.Length > 0)
            {
                EditorGUILayout.LabelField($"KV Store (v{insp.DataVersion}, {insp.KVEntries.Length} entries):", EditorStyles.miniLabel);
                EditorGUI.indentLevel++;
                var sb = new System.Text.StringBuilder();
                foreach (var kv in insp.KVEntries)
                {
                    if (sb.Length > 0) sb.Append("  ");
                    sb.Append($"P{kv.PlayerId}:K{kv.Key}=[{(kv.Value != null ? kv.Value.Length : 0)}B]");
                }
                EditorGUILayout.LabelField(sb.ToString(), EditorStyles.miniLabel);
                EditorGUI.indentLevel--;
            }
            else
            {
                prevC = GUI.contentColor;
                GUI.contentColor = Color.gray;
                EditorGUILayout.LabelField($"KV Store (v{insp.DataVersion}): Empty", EditorStyles.miniLabel);
                GUI.contentColor = prevC;
            }

            EditorGUI.indentLevel -= 2;
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

        void DoKillRoom(int roomId)
        {
            if (_wsClient != null && _wsClient.IsConnected)
            {
                _wsClient.SendRpc("kill_room", new Dictionary<string, object> { ["room_id"] = roomId });
            }
            else
            {
                var r = _client.KillRoom(roomId);
                ShowNotification(new GUIContent(r.Ok ? $"Room {roomId} killed" : r.Error));
            }
        }

        void DoCreateRoom(int maxPlayers, string matchKey)
        {
            if (_wsClient != null && _wsClient.IsConnected)
            {
                var payload = new Dictionary<string, object> { ["max_players"] = maxPlayers };
                if (!string.IsNullOrEmpty(matchKey)) payload["match_key"] = matchKey;
                _wsClient.SendRpc("create_room", payload);
                ShowNotification(new GUIContent($"[WS] Create room sent (max={maxPlayers})"));
            }
            else
            {
                var r = _client.CreateRoom(maxPlayers, matchKey);
                ShowNotification(new GUIContent(r.Ok ? $"[HTTP] Room created" : $"[HTTP] {r.Error}"));
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
            var profile = GetActiveProfile();
            bool isRemote = profile != null && profile.Type == DeployProfileType.RemoteSSH;

            EditorGUILayout.BeginHorizontal();
            GUI.backgroundColor = _lastAlive ? Color.gray : Color.green;
            GUI.enabled = !_lastAlive;
            if (GUILayout.Button(isRemote ? "Start (SSH)" : "Start Server", GUILayout.Height(28)))
                StartServer();
            GUI.enabled = true;
            GUI.backgroundColor = _lastAlive ? new Color(1f, 0.4f, 0.3f) : Color.gray;
            GUI.enabled = _lastAlive;
            if (GUILayout.Button(isRemote ? "Stop (SSH)" : "Stop Server", GUILayout.Height(28)))
                StopServer();
            GUI.enabled = true;
            GUI.backgroundColor = Color.white;
            EditorGUILayout.EndHorizontal();

            if (isRemote && profile != null)
            {
                var prev = GUI.contentColor;
                GUI.contentColor = Color.gray;
                EditorGUILayout.LabelField($"  {profile.SshUser}@{profile.SshHost}  systemd: {(string.IsNullOrEmpty(profile.SystemdService) ? "nohup" : profile.SystemdService)}", EditorStyles.miniLabel);
                GUI.contentColor = prev;
            }
        }

        // ===================== Server Control =====================

        string BuildCommand() =>
            string.IsNullOrEmpty(_configFile)
                ? $"cd {_serverPath} && go run ./cmd/framesync/ -addr={_addr} -proto={_proto} -ppr={_ppr}"
                : $"cd {_serverPath} && go run ./cmd/framesync/ -config={_configFile}";

        void StartServer()
        {
            var profile = GetActiveProfile();
            if (profile != null && profile.Type == DeployProfileType.RemoteSSH)
            {
                string cmd = string.IsNullOrEmpty(profile.SystemdService)
                    ? $"nohup {profile.RemoteBinaryPath} -config {profile.RemoteConfigPath} > /tmp/framesync.log 2>&1 &"
                    : $"sudo systemctl start {profile.SystemdService}";
                RunSshAsync(profile, cmd);
                _stopCooldownUntil = 0; // 清除 Stop 冷却，允许 Health 检测到上线后立即重连
                ShowNotification(new GUIContent($"Starting remote... ({profile.SshHost})"));
                _nextCheckTime = EditorApplication.timeSinceStartup + 4.0;
            }
            else
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
        }

        void StopServer()
        {
            _wsClient?.Disconnect();

            var profile = GetActiveProfile();
            if (profile != null && profile.Type == DeployProfileType.RemoteSSH)
            {
                // 远程 SSH Stop
                string cmd = string.IsNullOrEmpty(profile.SystemdService)
                    ? $"pkill -f '{System.IO.Path.GetFileName(profile.RemoteBinaryPath)}' || true"
                    : $"sudo systemctl stop {profile.SystemdService}";
                RunSshAsync(profile, cmd);
                _stopCooldownUntil = EditorApplication.timeSinceStartup + 10.0; // 10s 内不重连
                // 清空本地状态，让 health 轮询自然检测到下线
                _lastAlive = false; _health = default; _stats = default;
                _messages = Array.Empty<AdminClient.MsgEntry>();
                _rooms = Array.Empty<AdminClient.RoomDetail>();
                _wsMsgBuffer.Clear();
                _perfHasData = false; _perf = default; _prevPerf = default;
                _hotPlayers.Clear();
                _logLevel = ""; _logLevelPending = "";
                _expandedRooms.Clear(); _roomInspectCache.Clear();
                Array.Clear(_chartGameTx, 0, CHART_SAMPLES); Array.Clear(_chartGameRx, 0, CHART_SAMPLES);
                Array.Clear(_chartPlayers, 0, CHART_SAMPLES); Array.Clear(_chartHeap, 0, CHART_SAMPLES);
                _chartHead = 0;
                Repaint();
                ShowNotification(new GUIContent($"Stopping remote... ({profile.SshHost})"));
            }
            else
            {
                // 本地 Stop
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
                        UnityEngine.Debug.LogWarning($"[GM] Server still alive after kill. Try manually: lsof -ti:{gamePort},{adminPort} | xargs kill -9");
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

        DeployProfile GetActiveProfile()
        {
            int count = DeployProfile.GetProfileCount();
            if (count == 0 || _serverSwitcherIdx >= count) return null;
            return DeployProfile.Load(_serverSwitcherIdx);
        }

        /// <summary>在后台线程执行 SSH 命令，结果输出到 Unity Console</summary>
        static void RunSshAsync(DeployProfile p, string remoteCmd)
        {
            var keyPath = p.SshKeyPath.Trim().Replace("~",
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile));

            var args = $"-i \"{keyPath}\" -p {p.SshPort.Trim()} " +
                       $"-o StrictHostKeyChecking=no -o ConnectTimeout=10 " +
                       $"{p.SshUser.Trim()}@{p.SshHost.Trim()} \"{remoteCmd}\"";

            UnityEngine.Debug.Log($"[GM-SSH] /usr/bin/ssh {args}");

            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "/usr/bin/ssh", Arguments = args,
                        UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardOutput = true, RedirectStandardError = true,
                    };
                    var proc = Process.Start(psi);
                    if (proc == null)
                    {
                        UnityEngine.Debug.LogError("[GM-SSH] Process.Start returned null");
                        return;
                    }
                    string stdout = proc.StandardOutput.ReadToEnd();
                    string stderr = proc.StandardError.ReadToEnd();
                    proc.WaitForExit(15000);
                    int code = proc.ExitCode;

                    if (!string.IsNullOrEmpty(stdout))
                        UnityEngine.Debug.Log($"[GM-SSH] stdout: {stdout.Trim()}");
                    if (!string.IsNullOrEmpty(stderr))
                        UnityEngine.Debug.LogWarning($"[GM-SSH] stderr: {stderr.Trim()}");
                    UnityEngine.Debug.Log($"[GM-SSH] exit code: {code}");
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogError($"[GM-SSH] Exception: {ex.Message}");
                }
            });
        }

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

        // ===================== Logs Tab =====================

        void DrawLogs()
        {
            // Toolbar
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("Level:", GUILayout.Width(40));
                int newFilter = EditorGUILayout.Popup(_logLevelFilter, LogLevelFilterNames, GUILayout.Width(80));
                if (newFilter != _logLevelFilter)
                {
                    _logLevelFilter = newFilter;
                    // 重新过滤: 清空并等 WS backfill
                    _logEntries.Clear();
                }
                GUILayout.FlexibleSpace();
                _logAutoScroll = GUILayout.Toggle(_logAutoScroll, "Auto-scroll");
                if (GUILayout.Button("Clear", GUILayout.Width(60)))
                    _logEntries.Clear();
            }

            // Log list
            _logScroll = EditorGUILayout.BeginScrollView(_logScroll);
            var defaultColor = GUI.color;
            foreach (var e in _logEntries)
            {
                GUI.color = LogLevelColor(e.Level);
                var dt = System.DateTimeOffset.FromUnixTimeMilliseconds(e.Ts).ToLocalTime().ToString("HH:mm:ss");
                string text = string.IsNullOrEmpty(e.Attrs)
                    ? $"[{dt}] [{e.Level,-5}] {e.Msg}"
                    : $"[{dt}] [{e.Level,-5}] {e.Msg}  {e.Attrs}";
                EditorGUILayout.LabelField(text, EditorStyles.wordWrappedMiniLabel);
            }
            GUI.color = defaultColor;
            EditorGUILayout.EndScrollView();

            if (_logAutoScroll && Event.current.type == EventType.Repaint)
                _logScroll.y = float.MaxValue;
        }

        bool PassesLogLevelFilter(string level)
        {
            if (_logLevelFilter == 0) return true;
            int entryLvl = LogLevelRank(level);
            int filterLvl = _logLevelFilter; // 1=INFO, 2=WARN, 3=ERROR
            return entryLvl >= filterLvl;
        }

        static int LogLevelRank(string level)
        {
            if (level == null) return 0;
            switch (level.ToUpperInvariant())
            {
                case "DEBUG": return 0;
                case "INFO":  return 1;
                case "WARN":  return 2;
                case "ERROR": return 3;
                default:      return 0;
            }
        }

        static Color LogLevelColor(string level)
        {
            if (level == null) return Color.white;
            switch (level.ToUpperInvariant())
            {
                case "DEBUG": return new Color(0.7f, 0.7f, 0.7f);
                case "INFO":  return new Color(0.85f, 0.85f, 0.85f);
                case "WARN":  return new Color(1f, 0.78f, 0.31f);
                case "ERROR": return new Color(1f, 0.31f, 0.31f);
                default:      return Color.white;
            }
        }
    }
}
