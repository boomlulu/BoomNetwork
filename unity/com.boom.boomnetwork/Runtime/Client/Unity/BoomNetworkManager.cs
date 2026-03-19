using UnityEngine;
using BoomNetwork.Core;
using BoomNetwork.Core.FrameSync;
using BoomNetwork.Client.Transport;
using BoomNetwork.Client.Session;
using BoomNetwork.Client.Connection;
using BoomNetwork.Client.FrameSync;

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
    ///   Awake   → 创建网络组件
    ///   Update  → 驱动 Tick
    ///   OnDestroy → 断开连接
    /// </summary>
    public class BoomNetworkManager : MonoBehaviour
    {
        [Header("Server")]
        [SerializeField] private string host = "127.0.0.1";
        [SerializeField] private int port = 9000;
        [SerializeField] private TransportType transportType = TransportType.TCP;

        [Header("Heartbeat")]
        [SerializeField] private float heartbeatIntervalMs = 3000;
        [SerializeField] private float heartbeatTimeoutMs = 10000;

        [Header("Reconnect")]
        [SerializeField] private int quickReconnectAttempts = 3;
        [SerializeField] private float quickReconnectTimeoutMs = 5000;
        [SerializeField] private int snapshotReconnectAttempts = 2;
        [SerializeField] private float snapshotReconnectTimeoutMs = 10000;

        [Header("Debug")]
        [SerializeField] private bool logEnabled = true;

        public enum TransportType { TCP, KCP }

        // --- 公开属性 ---

        /// <summary>
        /// 帧同步客户端（注册 OnFrame / OnBound / OnReconnected 等事件）
        /// </summary>
        public FrameSyncClient Client { get; private set; }

        /// <summary>
        /// 连接管理器（查询连接状态）
        /// </summary>
        public ConnectionManager Connection { get; private set; }

        /// <summary>
        /// 网络会话（底层消息收发）
        /// </summary>
        public NetworkSession Session { get; private set; }

        /// <summary>
        /// 是否已连接
        /// </summary>
        public bool IsConnected => Connection?.CurrentState == ConnectionManager.State.Connected;

        /// <summary>
        /// 是否在帧同步中
        /// </summary>
        public bool IsSyncing => Client?.CurrentState == FrameSyncClient.State.Syncing;

        private void Awake()
        {
            CreateComponents();
        }

        private void Update()
        {
            Client?.Tick(Time.deltaTime * 1000f);
        }

        private void OnDestroy()
        {
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
                CreateComponents();

            Client.Connect(host, port);

            if (logEnabled)
                Debug.Log($"[BoomNetwork] Connecting to {host}:{port} via {transportType}");
        }

        /// <summary>
        /// 断开连接
        /// </summary>
        public void Disconnect()
        {
            Client?.Disconnect();
        }

        /// <summary>
        /// 发送玩家输入
        /// </summary>
        public void SendInput(byte[] data, int dataLength = -1)
        {
            Client?.SendInput(data, dataLength);
        }

        private void CreateComponents()
        {
            // Transport
            Core.Transport.ITransport transport = transportType switch
            {
                TransportType.KCP => new KcpClientTransport(),
                _ => new TcpClientTransport(),
            };

            // Session
            Session = new NetworkSession(transport);

            // Reconnect Strategy
            var strategy = new CompositeReconnectStrategy(
                (new QuickReconnectStrategy { TimeoutMs = quickReconnectTimeoutMs }, quickReconnectAttempts),
                (new SnapshotReconnectStrategy { TimeoutMs = snapshotReconnectTimeoutMs }, snapshotReconnectAttempts)
            );

            // ConnectionManager
            Connection = new ConnectionManager(Session, strategy);
            Connection.HeartbeatIntervalMs = heartbeatIntervalMs;
            Connection.HeartbeatTimeoutMs = heartbeatTimeoutMs;

            // FrameSyncClient
            Client = new FrameSyncClient(Session, Connection);

            // Debug logging
            if (logEnabled)
            {
                Client.OnBound += id => Debug.Log($"[BoomNetwork] Bound as player {id}");
                Client.OnFrameSyncStart += data =>
                    Debug.Log($"[BoomNetwork] FrameSync started (rate={data.FrameRate}, interval={data.FrameInterval}ms)");
                Client.OnFrameSyncStop += () => Debug.Log("[BoomNetwork] FrameSync stopped");
                Client.OnReconnected += () => Debug.Log("[BoomNetwork] Reconnected");
                Client.OnDisconnected += () => Debug.Log("[BoomNetwork] Disconnected");
                Client.OnError += err => Debug.LogWarning($"[BoomNetwork] {err}");
                Connection.OnLog += msg => Debug.Log(msg);
            }
        }
    }
}
