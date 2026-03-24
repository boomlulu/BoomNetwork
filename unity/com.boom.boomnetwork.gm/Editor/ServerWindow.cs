using UnityEngine;
using UnityEditor;
using System.Diagnostics;

namespace BoomNetwork.GM.Editor
{
    /// <summary>
    /// BoomNetwork Server 控制面板
    ///
    /// 功能：
    ///   - 服务器状态（在线/离线，房间数，玩家数，运行时长）
    ///   - 流量统计（总量 / 近1分钟 / 近5秒速率）
    ///   - 一键启动/停止服务器
    ///   - 手动命令复制
    ///
    /// 健康检查走 HTTP /health，不走 TCP 游戏协议，零日志噪声。
    /// </summary>
    public class ServerWindow : EditorWindow
    {
        // ===== Config (EditorPrefs 持久化) =====
        private string _serverPath;
        private string _configFile;
        private string _addr;
        private string _proto;
        private int    _ppr;
        private string _adminUrl;

        // ===== State =====
        private AdminClient _client;
        private AdminClient.HealthResult _health;
        private AdminClient.StatsResult  _stats;
        private double _nextCheckTime;
        private bool   _lastAlive;

        private const double POLL_INTERVAL = 2.0;
        private const string PREF_PREFIX = "BoomNetwork.GM.Server.";

        [MenuItem("BoomNetwork/Server Window")]
        public static void ShowWindow() => GetWindow<ServerWindow>("BoomNetwork Server");

        void OnEnable()
        {
            LoadPrefs();
            _client = new AdminClient(_adminUrl);
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
            if (_health.IsOnline) _stats = _client.FetchStats();
            else _stats = default;

            if (_health.IsOnline != _lastAlive || _health.IsOnline)
            {
                _lastAlive = _health.IsOnline;
                Repaint();
            }
        }

        void OnGUI()
        {
            DrawStatus();
            DrawTraffic();
            EditorGUILayout.Space(6);
            DrawConfig();
            EditorGUILayout.Space(8);
            DrawActions();
            EditorGUILayout.Space(4);
            DrawCommand();
        }

        // ===================== GUI Sections =====================

        void DrawStatus()
        {
            EditorGUILayout.Space(4);
            var prev = GUI.contentColor;
            GUI.contentColor = _lastAlive ? Color.green : Color.gray;
            EditorGUILayout.LabelField($"● {(_lastAlive ? "RUNNING" : "STOPPED")}", EditorStyles.boldLabel);
            GUI.contentColor = prev;

            if (_lastAlive)
                EditorGUILayout.LabelField(
                    $"  Rooms: {_health.Rooms}   Players: {_health.Players}   Uptime: {_health.Uptime}");
        }

        void DrawTraffic()
        {
            if (!_lastAlive || !_stats.HasData) return;

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Traffic", EditorStyles.boldLabel);

            TrafficRow("Total", _stats.TxTotal, _stats.RxTotal);
            TrafficRow("1 min", _stats.Tx1Min,  _stats.Rx1Min);
            TrafficRow("5 sec", _stats.Tx5Sec / 5, _stats.Rx5Sec / 5, perSec: true);
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
            if (GUILayout.Button("Start Server", GUILayout.Height(30)))
                StartServer();
            GUI.enabled = true;

            GUI.backgroundColor = _lastAlive ? new Color(1f, 0.4f, 0.3f) : Color.gray;
            GUI.enabled = _lastAlive;
            if (GUILayout.Button("Stop Server", GUILayout.Height(30)))
                StopServer();
            GUI.enabled = true;

            GUI.backgroundColor = Color.white;
            EditorGUILayout.EndHorizontal();
        }

        void DrawCommand()
        {
            EditorGUILayout.LabelField("Manual Command", EditorStyles.boldLabel);
            var cmd = BuildCommand();
            EditorGUILayout.SelectableLabel(cmd, EditorStyles.textField, GUILayout.Height(20));
            if (GUILayout.Button("Copy Command"))
            {
                GUIUtility.systemCopyBuffer = cmd;
                ShowNotification(new GUIContent("Copied!"));
            }
        }

        // ===================== Server Control =====================

        string BuildCommand() =>
            string.IsNullOrEmpty(_configFile)
                ? $"cd {_serverPath} && go run ./cmd/framesync/ -addr={_addr} -proto={_proto} -ppr={_ppr}"
                : $"cd {_serverPath} && go run ./cmd/framesync/ -config={_configFile}";

        void StartServer()
        {
            var cmd    = BuildCommand();
            var script = $"tell application \"Terminal\" to do script \"{cmd}\"";
            Process.Start(new ProcessStartInfo
            {
                FileName  = "osascript",
                Arguments = $"-e '{script}'",
                UseShellExecute = false,
                CreateNoWindow  = true,
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
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow         = true,
                })?.WaitForExit(3000);

                _lastAlive = false;
                _health = default;
                _stats  = default;
                Repaint();
                ShowNotification(new GUIContent("Server stopped"));
            }
            catch (System.Exception e)
            {
                UnityEngine.Debug.LogError($"[GM] Kill port failed: {e.Message}");
            }
        }

        // ===================== EditorPrefs =====================

        void LoadPrefs()
        {
            _serverPath = EditorPrefs.GetString(PREF_PREFIX + "serverPath", "/Users/boom/Demo/BoomNetwork/svr");
            _configFile = EditorPrefs.GetString(PREF_PREFIX + "configFile", "cmd/framesync/config.yaml");
            _addr       = EditorPrefs.GetString(PREF_PREFIX + "addr",      ":9000");
            _proto      = EditorPrefs.GetString(PREF_PREFIX + "proto",     "tcp");
            _ppr        = EditorPrefs.GetInt(PREF_PREFIX + "ppr",          2);
            _adminUrl   = EditorPrefs.GetString(PREF_PREFIX + "adminUrl",  "http://127.0.0.1:9091");
        }

        void SavePrefs()
        {
            EditorPrefs.SetString(PREF_PREFIX + "serverPath", _serverPath);
            EditorPrefs.SetString(PREF_PREFIX + "configFile", _configFile);
            EditorPrefs.SetString(PREF_PREFIX + "addr",       _addr);
            EditorPrefs.SetString(PREF_PREFIX + "proto",      _proto);
            EditorPrefs.SetInt(PREF_PREFIX + "ppr",           _ppr);
            EditorPrefs.SetString(PREF_PREFIX + "adminUrl",   _adminUrl);
        }
    }
}
