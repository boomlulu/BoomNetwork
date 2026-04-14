using System;
using UnityEngine;
using BoomNetwork.Client.FrameSync;
using BoomNetwork.Client.Transport;
using BoomNetwork.Core;
using BoomNetwork.Core.FrameSync;
using BoomNetwork.Core.Transport;

namespace BoomNetwork.Unity
{
    /// <summary>
    /// 传输协议选择
    /// </summary>
    public enum TransportType
    {
        /// <summary>自动选择：WebGL → WebSocket，其他平台 → TCP</summary>
        Auto,
        /// <summary>强制 TCP（不支持 WebGL）</summary>
        TCP,
        /// <summary>强制 WebSocket（适合 WebGL 或穿墙场景）</summary>
        WebSocket,
        /// <summary>强制 KCP（低延迟 UDP，不支持 WebGL）</summary>
        KCP,
    }

    /// <summary>
    /// BoomNetwork Unity 封装
    ///
    /// 拖到 GameObject 上即可使用。
    /// 在 Inspector 中配置服务器地址和网络参数。
    /// 通过 Client 属性访问 FrameSyncClient 注册事件。
    ///
    /// 生命周期:
    ///   Awake   → 创建 FrameSyncClient
    ///   Update  → 驱动 Tick
    ///   OnDestroy → 断开连接
    /// </summary>
    public class BoomNetworkManager : MonoBehaviour
    {
        [Header("Server")]
        [SerializeField] private string host = "124.220.6.174";
        [SerializeField] private int port = 9000;

        [Header("Transport")]
        [Tooltip("Auto: WebGL→WebSocket, 其他→TCP。手动选择可覆盖平台默认值。")]
        [SerializeField] private TransportType transportType = TransportType.Auto;

        [Header("Heartbeat")]
        [SerializeField] private float heartbeatIntervalMs = 3000;
        [Tooltip("心跳超时时长（ms）。Android 建议设为 15000，因移动网络 RTT 波动较大。")]
        [SerializeField] private float heartbeatTimeoutMs = 10000;

        [Header("Room")]
        [SerializeField] private int maxPlayers = 4;
        [SerializeField] private bool autoStart = true;
        [Tooltip("匹配 key，相同 key 才能匹配到一起（避免不同 demo 串房）")]
        [SerializeField] private string matchKey = "";

        [Header("Debug")]
        [SerializeField] private bool logEnabled = true;

        // --- 公开属性 ---

        /// <summary>当前玩家 ID</summary>
        public int PlayerId => Client?.PlayerId ?? 0;

        /// <summary>匹配 key（Inspector 配置）</summary>
        public string MatchKey { get => matchKey; set => matchKey = value; }

        /// <summary>
        /// 帧同步客户端（注册 OnConnected / OnFrame / OnReconnected 等事件）
        /// </summary>
        public FrameSyncClient Client { get; private set; }

        /// <summary>
        /// 是否已连接
        /// </summary>
        public bool IsConnected => Client?.CurrentState >= FrameSyncClient.State.Connected;

        /// <summary>
        /// 是否在帧同步中
        /// </summary>
        public bool IsSyncing => Client?.CurrentState == FrameSyncClient.State.Syncing;

        private bool _quickStartWired;

        private void Awake()
        {
            CreateClient();
        }

        private void Update()
        {
            Client?.Tick(Time.deltaTime * 1000f);
        }

        // H1 Android: 后台/前台切换时暂停/恢复自动重连。
        // 在 Android 上，App 进入后台后 TCP 连接可能被系统中断，但重连需要网络权限已就绪。
        // OnApplicationPause(true)  → 暂停重连（防止后台无限重试耗电）
        // OnApplicationPause(false) → 恢复重连（回到前台，补触发断线重连）
        // Editor 下跳过 Pause：Editor 失焦（切窗口）不等同于 App 后台，不应阻断重连。
        private void OnApplicationPause(bool pauseStatus)
        {
#if !UNITY_EDITOR
            if (pauseStatus)
                Client?.PauseReconnect();
            else
                Client?.ResumeReconnect();
#endif
        }

        // OnApplicationFocus 与 OnApplicationPause 语义互补：
        // 部分 Android 版本 Focus 比 Pause 更早/晚触发，双钩子确保覆盖。
        // Editor 下跳过 Pause：同上。
        private void OnApplicationFocus(bool hasFocus)
        {
#if !UNITY_EDITOR
            if (!hasFocus)
                Client?.PauseReconnect();
            else
                Client?.ResumeReconnect();
#endif
        }

        private void OnDestroy()
        {
            // M6: 取消所有事件订阅，防止 GC 无法回收 Client 和 MonoBehaviour
            if (Client != null)
            {
                if (logEnabled)
                {
                    Client.OnConnected -= LogConnected;
                    Client.OnJoinedRoom -= LogJoinedRoom;
                    Client.OnFrameSyncStart -= LogFrameSyncStart;
                    Client.OnFrameSyncStop -= LogFrameSyncStop;
                    Client.OnReconnected -= LogReconnected;
                    Client.OnDisconnected -= LogDisconnected;
                    Client.OnError -= LogError;
                    Client.OnLog -= LogMsg;
                }
                if (_quickStartWired)
                {
                    Client.OnConnected -= QuickStartOnConnected;
                    Client.OnReady -= QuickStartOnReady;
                }
            }
            Client?.Disconnect();
        }

        /// <summary>
        /// 连接服务器
        /// </summary>
        public void Connect()
        {
            Connect(host, port);
        }

        /// <summary>
        /// 连接指定地址
        /// </summary>
        public void Connect(string serverHost, int serverPort)
        {
            host = serverHost;
            port = serverPort;

            if (Client == null)
                CreateClient();

            Client.Connect(host, port);

            if (logEnabled)
                Debug.Log($"[BoomNetwork] Connecting to {host}:{port}");
        }

        /// <summary>
        /// 断开连接
        /// </summary>
        public void Disconnect()
        {
            Client?.Disconnect();
        }

        /// <summary>
        /// 一键启动：连接 → 匹配房间 → 开始帧同步
        ///
        /// 最简接入方式，适合 Hello World 和快速原型。
        /// 连接后自动 MatchRoom（有空位加入，否则创建），入房后自动 RequestStart。
        /// </summary>
        public void QuickStart()
        {
            if (Client == null)
                CreateClient();

            if (!_quickStartWired)
            {
                _quickStartWired = true;
                // M6: 命名方法，OnDestroy 可以 -= 取消订阅
                Client.OnConnected += QuickStartOnConnected;
                Client.OnReady += QuickStartOnReady;
            }

            Connect();
        }

        // M6: named handlers for QuickStart (so OnDestroy can unsubscribe)
        private void QuickStartOnConnected() =>
            Client.MatchRoom(maxPlayers, string.IsNullOrEmpty(matchKey) ? null : matchKey);
        private void QuickStartOnReady() { if (autoStart) Client.RequestStart(); }

        /// <summary>
        /// 发送玩家输入
        /// </summary>
        public void SendInput(byte[] data, int dataLength = -1)
        {
            Client?.SendInput(data, dataLength);
        }

        private Func<ITransport>? BuildTransportFactory()
        {
            switch (transportType)
            {
                case TransportType.TCP:
                    return () => new TcpClientTransport();
                case TransportType.WebSocket:
#if UNITY_WEBGL && !UNITY_EDITOR
                    return () => new WebGLWebSocketTransport();
#else
                    return () => new WebSocketClientTransport();
#endif
                case TransportType.KCP:
                    return () => new KcpClientTransport();
                case TransportType.Auto:
                default:
                    return null; // FrameSyncClient 按平台自动选择
            }
        }

        private void CreateClient()
        {
            Client = new FrameSyncClient(heartbeatIntervalMs, heartbeatTimeoutMs, BuildTransportFactory());

            if (logEnabled)
            {
                // M6: 命名方法，OnDestroy 可以 -= 取消订阅，避免闭包持有 this 导致 GC 泄漏
                Client.OnConnected += LogConnected;
                Client.OnJoinedRoom += LogJoinedRoom;
                Client.OnFrameSyncStart += LogFrameSyncStart;
                Client.OnFrameSyncStop += LogFrameSyncStop;
                Client.OnReconnected += LogReconnected;
                Client.OnDisconnected += LogDisconnected;
                Client.OnError += LogError;
                Client.OnLog += LogMsg;
            }
        }

        // M6: named log handlers
        private void LogConnected() => Debug.Log($"[BoomNetwork] Connected (Player {Client.PlayerId})");
        private void LogJoinedRoom(int roomId, int[] existing) =>
            Debug.Log($"[BoomNetwork] Joined room {roomId} (existing: [{string.Join(",", existing)}])");
        private void LogFrameSyncStart(FrameSyncInitData data) =>
            Debug.Log($"[BoomNetwork] FrameSync started (rate={data.FrameRate}, interval={data.FrameInterval}ms)");
        private void LogFrameSyncStop() => Debug.Log("[BoomNetwork] FrameSync stopped");
        private void LogReconnected() => Debug.Log("[BoomNetwork] Reconnected");
        private void LogDisconnected() => Debug.Log("[BoomNetwork] Disconnected");
        private void LogError(NetworkError err) => Debug.LogWarning($"[BoomNetwork] [{err.Code}] {err.Message}");
        private void LogMsg(string msg) => Debug.Log(msg);

        // --- 右上角 Build 号显示 ---
        private GUIStyle _buildLabelStyle;

        private void OnGUI()
        {
            if (_buildLabelStyle == null)
                _buildLabelStyle = new GUIStyle
                {
                    fontSize = 22,
                    alignment = TextAnchor.UpperRight,
                    normal = { textColor = new Color(1f, 1f, 1f, 0.55f) },
                    fontStyle = FontStyle.Bold,
                };
            GUI.Label(new Rect(Screen.width - 178f, 8f, 170f, 30f),
                $"BN {BoomNetworkBuild.Label}", _buildLabelStyle);
        }
    }
}
