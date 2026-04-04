using UnityEngine;
using BoomNetwork.Client.FrameSync;
using BoomNetwork.Core.FrameSync;

namespace BoomNetwork.Unity
{
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
        [SerializeField] private string host = ""; // L5: 不预填生产地址，避免开发者误连线上
        [SerializeField] private int port = 9000;

        [Header("Heartbeat")]
        [SerializeField] private float heartbeatIntervalMs = 3000;
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

        private void CreateClient()
        {
            Client = new FrameSyncClient(heartbeatIntervalMs, heartbeatTimeoutMs);

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
        private void LogError(string err) => Debug.LogWarning($"[BoomNetwork] {err}");
        private void LogMsg(string msg) => Debug.Log(msg);
    }
}
