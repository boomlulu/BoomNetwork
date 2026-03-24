using UnityEngine;
using UnityEditor;
using System;
using System.Diagnostics;
using System.Linq;

namespace BoomNetwork.GM.Editor
{
    public class ServerWindow : EditorWindow
    {
        // ===== Config =====
        private string _serverPath, _configFile, _addr, _proto, _adminUrl, _adminToken;
        private int _ppr;

        // ===== State =====
        private AdminClient _client;
        private AdminClient.HealthResult _health;
        private AdminClient.StatsResult  _stats;
        private AdminClient.MsgEntry[]   _messages = Array.Empty<AdminClient.MsgEntry>();
        private AdminClient.RoomDetail[] _rooms = Array.Empty<AdminClient.RoomDetail>();
        private double _nextCheckTime;
        private bool _lastAlive;

        // ===== Tab =====
        private int _tab;
        private static readonly string[] TabNames = { "Dashboard", "Messages", "Rooms" };

        // ===== Messages page =====
        private Vector2 _msgScroll;
        private int _cmdFilter = -1;
        private bool _msgPaused;
        private bool _hideHeartbeat = true; // G11: 默认隐藏心跳
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
            _client = new AdminClient(_adminUrl);
            _client.Token = _adminToken;
            BuildCmdFilter();
            EditorApplication.update += OnEditorUpdate;
        }

        void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            SavePrefs();
            _client?.Dispose();
        }

        void OnEditorUpdate()
        {
            if (EditorApplication.timeSinceStartup < _nextCheckTime) return;
            _nextCheckTime = EditorApplication.timeSinceStartup + POLL_INTERVAL;

            _client.BaseUrl = _adminUrl;
            _client.Token = _adminToken;
            _health = _client.FetchHealth();

            if (_health.IsOnline)
            {
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
            }

            if (_health.IsOnline != _lastAlive || _health.IsOnline)
            {
                _lastAlive = _health.IsOnline;
                Repaint();
            }
        }

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
            }
        }

        // ===================== Status Bar =====================

        void DrawStatusBar()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            var prev = GUI.contentColor;
            GUI.contentColor = _lastAlive ? Color.green : Color.gray;
            EditorGUILayout.LabelField($"● {(_lastAlive ? "RUNNING" : "STOPPED")}",
                EditorStyles.boldLabel, GUILayout.Width(90));
            GUI.contentColor = prev;
            if (_lastAlive)
                EditorGUILayout.LabelField($"Rooms: {_health.Rooms}  Players: {_health.Players}  Up: {_health.Uptime}");
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

            EditorGUILayout.Space(6);
            DrawConfig();
            EditorGUILayout.Space(6);
            DrawActions();

            // G12: 手动命令区
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

            // G11: 心跳隐藏开关
            _hideHeartbeat = GUILayout.Toggle(_hideHeartbeat, "Hide HB", GUILayout.Width(65));

            GUILayout.FlexibleSpace();

            if (GUILayout.Button(_msgPaused ? "▶" : "❚❚", GUILayout.Width(30)))
                _msgPaused = !_msgPaused;

            // G10: 导出
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
                if (_hideHeartbeat && (m.Cmd == 7 || m.Cmd == 8)) return false; // G11
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

        void CopyMessages() // G10
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
            var names = new System.Collections.Generic.List<string> { "All" };
            var values = new System.Collections.Generic.List<int> { -1 };
            var cmds = new (int cmd, string name)[]
            {
                (1, "SessionBind"), (3, "RequestStart"), (5, "FrameInput"),
                (6, "PushFrames"), (7, "Heartbeat"), (9, "Reconnect"),
                (15, "JoinRoom"), (19, "PlayerJoined"), (22, "UploadSnapshot"),
            };
            foreach (var c in cmds) { names.Add(c.name); values.Add(c.cmd); }
            _cmdFilterNames = names.ToArray();
            _cmdFilterValues = values.ToArray();
        }

        // ===================== Tab 2: Rooms (G1+G2+G3) =====================

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

                // G3: Stop room button
                GUI.backgroundColor = new Color(1f, 0.4f, 0.3f);
                GUI.enabled = room.Running;
                if (GUILayout.Button("Stop", GUILayout.Width(45)))
                {
                    var r = _client.StopRoom(room.Id);
                    ShowNotification(new GUIContent(r.Ok ? $"Room {room.Id} stopped" : r.Error));
                }
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

                        // G2: Kick button
                        GUI.backgroundColor = new Color(1f, 0.6f, 0.3f);
                        if (GUILayout.Button("Kick", GUILayout.Width(40)))
                        {
                            var r = _client.KickPlayer(p.Id);
                            ShowNotification(new GUIContent(r.Ok ? $"Kicked P{p.Id}" : r.Error));
                        }
                        GUI.backgroundColor = Color.white;

                        EditorGUILayout.EndHorizontal();
                    }
                    EditorGUI.indentLevel--;
                }

                EditorGUILayout.Space(4);
            }

            EditorGUILayout.EndScrollView();
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
            var port = _addr.TrimStart(':');
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    Arguments = $"-c \"lsof -ti:{port} -sTCP:LISTEN | xargs kill -9 2>/dev/null\"",
                    UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true,
                })?.WaitForExit(3000);
                _lastAlive = false; _health = default; _stats = default;
                _messages = Array.Empty<AdminClient.MsgEntry>();
                _rooms = Array.Empty<AdminClient.RoomDetail>();
                Repaint();
                ShowNotification(new GUIContent("Server stopped"));
            }
            catch (Exception e) { UnityEngine.Debug.LogError($"[GM] Kill failed: {e.Message}"); }
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
