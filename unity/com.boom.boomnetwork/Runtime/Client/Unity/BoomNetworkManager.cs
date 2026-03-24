using UnityEngine;
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
    ///   Awake   → 创建 FrameSyncClient
    ///   Update  → 驱动 Tick
    ///   OnDestroy → 断开连接
    /// </summary>
    public class BoomNetworkManager : MonoBehaviour
    {
        [Header("Server")]
        [SerializeField] private string host = "127.0.0.1";
        [SerializeField] private int port = 9000;

        [Header("Heartbeat")]
        [SerializeField] private float heartbeatIntervalMs = 3000;
        [SerializeField] private float heartbeatTimeoutMs = 10000;

        [Header("Debug")]
        [SerializeField] private bool logEnabled = true;

        // --- 公开属性 ---

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
                Client.OnConnected += () => Debug.Log($"[BoomNetwork] Connected (Player {Client.PlayerId})");
                Client.OnJoinedRoom += (roomId, existing) =>
                    Debug.Log($"[BoomNetwork] Joined room {roomId} (existing: [{string.Join(",", existing)}])");
                Client.OnFrameSyncStart += data =>
                    Debug.Log($"[BoomNetwork] FrameSync started (rate={data.FrameRate}, interval={data.FrameInterval}ms)");
                Client.OnFrameSyncStop += () => Debug.Log("[BoomNetwork] FrameSync stopped");
                Client.OnReconnected += () => Debug.Log("[BoomNetwork] Reconnected");
                Client.OnDisconnected += () => Debug.Log("[BoomNetwork] Disconnected");
                Client.OnError += err => Debug.LogWarning($"[BoomNetwork] {err}");
                Client.OnLog += msg => Debug.Log(msg);
            }
        }
    }
}
