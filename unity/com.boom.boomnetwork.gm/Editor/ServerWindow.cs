using UnityEngine;
using UnityEditor;
using System;
using System.Diagnostics;
using System.Linq;

namespace BoomNetwork.GM.Editor
{
    public class ServerWindow : EditorWindow
    {
        // ===== Config (EditorPrefs) =====
        private string _serverPath, _configFile, _addr, _proto, _adminUrl;
        private int _ppr;

        // ===== State =====
        private AdminClient _client;
        private AdminClient.HealthResult _health;
        private AdminClient.StatsResult  _stats;
        private AdminClient.MsgEntry[]   _messages = Array.Empty<AdminClient.MsgEntry>();
        private double _nextCheckTime;
        private bool _lastAlive;

        // ===== Tab =====
        private int _tab;
        private static readonly string[] TabNames = { "Dashboard", "Messages" };

        // ===== Messages page =====
        private Vector2 _msgScroll;
        private int _cmdFilter = -1; // -1 = All
        private bool _paused;
        private string[] _cmdFilterNames;
        private int[] _cmdFilterValues;

        private const double POLL_INTERVAL = 2.0;
        private const string PP = "BoomNetwork.GM.Server.";

        [MenuItem("BoomNetwork/Server Window")]
        public static void ShowWindow() => GetWindow<ServerWindow>("BoomNetwork Server");

        void OnEnable()
        {
            LoadPrefs();
            _client = new AdminClient(_adminUrl);
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
            _health = _client.FetchHealth();
            if (_health.IsOnline)
            {
                _stats = _client.FetchStats();
                if (!_paused) _messages = _client.FetchMessages(100);
            }
            else
            {
                _stats = default;
                if (!_paused) _messages = Array.Empty<AdminClient.MsgEntry>();
            }

            if (_health.IsOnline != _lastAlive || _health.IsOnline)
            {
                _lastAlive = _health.IsOnline;
                Repaint();
            }
        }

        void OnGUI()
        {
            // Status bar (always visible)
            DrawStatusBar();

            // Tab bar
            _tab = GUILayout.Toolbar(_tab, TabNames);
            EditorGUILayout.Space(2);

            switch (_tab)
            {
                case 0: DrawDashboard(); break;
                case 1: DrawMessages(); break;
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
                EditorGUILayout.LabelField(
                    $"Rooms: {_health.Rooms}  Players: {_health.Players}  Up: {_health.Uptime}");

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
        }

        static void TrafficRow(string label, long tx, long rx, bool perSec = false)
        {
            string suffix = perSec ? "/s" : "";
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, GUILayout.Width(40));
            var prev = GUI.contentColor;
            GUI.contentColor = new Color(0.4f, 0.8f, 1f);
            EditorGUILayout.LabelField($"↑ {AdminClient.FmtBytes(tx)}{suffix}", GUILayout.Width(100));
            GUI.contentColor = new Color(0.5f, 1f, 0.5f);
            EditorGUILayout.LabelField($"↓ {AdminClient.FmtBytes(rx)}{suffix}");
            GUI.contentColor = prev;
            EditorGUILayout.EndHorizontal();
        }

        // ===================== Tab 1: Messages =====================

        void DrawMessages()
        {
            // Toolbar: filter + pause
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Filter:", GUILayout.Width(40));
            int filterIdx = Array.IndexOf(_cmdFilterValues, _cmdFilter);
            if (filterIdx < 0) filterIdx = 0;
            int newIdx = EditorGUILayout.Popup(filterIdx, _cmdFilterNames, GUILayout.Width(140));
            _cmdFilter = _cmdFilterValues[newIdx];

            GUILayout.FlexibleSpace();

            if (GUILayout.Button(_paused ? "Resume" : "Pause", GUILayout.Width(60)))
                _paused = !_paused;

            EditorGUILayout.LabelField($"{_messages.Length} msgs", GUILayout.Width(60));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(2);

            // Header
            var headerStyle = EditorStyles.miniLabel;
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            EditorGUILayout.LabelField("Time",     headerStyle, GUILayout.Width(70));
            EditorGUILayout.LabelField("Dir",      headerStyle, GUILayout.Width(25));
            EditorGUILayout.LabelField("Command",  headerStyle, GUILayout.Width(120));
            EditorGUILayout.LabelField("Player",   headerStyle, GUILayout.Width(45));
            EditorGUILayout.LabelField("Size",     headerStyle);
            EditorGUILayout.EndHorizontal();

            // Scroll list
            _msgScroll = EditorGUILayout.BeginScrollView(_msgScroll);

            var filtered = _cmdFilter < 0
                ? _messages
                : _messages.Where(m => m.Cmd == _cmdFilter).ToArray();

            foreach (var msg in filtered)
            {
                EditorGUILayout.BeginHorizontal();

                // Time (HH:mm:ss)
                var dt = DateTimeOffset.FromUnixTimeMilliseconds(msg.Ts).LocalDateTime;
                EditorGUILayout.LabelField(dt.ToString("HH:mm:ss"), GUILayout.Width(70));

                // Direction arrow
                var prev = GUI.contentColor;
                GUI.contentColor = msg.Dir == "rx"
                    ? new Color(0.5f, 1f, 0.5f)  // green = from client
                    : new Color(0.4f, 0.8f, 1f);  // blue = to client
                EditorGUILayout.LabelField(msg.Dir == "rx" ? "↓" : "↑", GUILayout.Width(25));
                GUI.contentColor = prev;

                // Cmd name
                EditorGUILayout.LabelField(msg.Name, GUILayout.Width(120));

                // Player
                EditorGUILayout.LabelField(msg.Pid > 0 ? $"P{msg.Pid}" : "—", GUILayout.Width(45));

                // Size
                EditorGUILayout.LabelField(AdminClient.FmtBytes(msg.Size));

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndScrollView();
        }

        void BuildCmdFilter()
        {
            // All + common commands
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

        // ===================== Config =====================

        void DrawConfig()
        {
            EditorGUILayout.LabelField("Config", EditorStyles.boldLabel);
            _serverPath = EditorGUILayout.TextField("Server Path", _serverPath);
            _configFile = EditorGUILayout.TextField("Config File", _configFile);
            _adminUrl   = EditorGUILayout.TextField("Admin URL",   _adminUrl);

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
            if (GUILayout.Button("Start Server", GUILayout.Height(28)))
                StartServer();
            GUI.enabled = true;

            GUI.backgroundColor = _lastAlive ? new Color(1f, 0.4f, 0.3f) : Color.gray;
            GUI.enabled = _lastAlive;
            if (GUILayout.Button("Stop Server", GUILayout.Height(28)))
                StopServer();
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
                FileName  = "osascript",
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
                    FileName  = "/bin/bash",
                    Arguments = $"-c \"lsof -ti:{port} -sTCP:LISTEN | xargs kill -9 2>/dev/null\"",
                    UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true,
                })?.WaitForExit(3000);
                _lastAlive = false;
                _health = default; _stats = default;
                _messages = Array.Empty<AdminClient.MsgEntry>();
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
        }

        void SavePrefs()
        {
            EditorPrefs.SetString(PP + "serverPath", _serverPath);
            EditorPrefs.SetString(PP + "configFile", _configFile);
            EditorPrefs.SetString(PP + "addr",       _addr);
            EditorPrefs.SetString(PP + "proto",      _proto);
            EditorPrefs.SetInt(PP + "ppr",           _ppr);
            EditorPrefs.SetString(PP + "adminUrl",   _adminUrl);
        }
    }
}
