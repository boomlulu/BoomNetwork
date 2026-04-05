#if !UNITY_WEBGL || UNITY_EDITOR
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Threading;
using BoomNetwork.Core;
using BoomNetwork.Core.Transport;
using KcpProject;

namespace BoomNetwork.Client.Transport
{
    /// <summary>
    /// KCP 客户端传输层
    ///
    /// 基于 kcp-csharp (limpo1989/kcp-csharp) 实现。
    /// 和 TcpClientTransport 实现相同的 ITransport 接口，上层无感切换。
    ///
    /// 关键区别:
    ///   - TCP: IO 线程收发，Tick 取数据
    ///   - KCP: Tick 内同步调 UDP poll + KCP update + recv（不需要额外线程）
    /// </summary>
    public class KcpClientTransport : ITransport
    {
        private IUDPSession? _session;

        private string _lastHost = "";
        private int _lastPort;
        private readonly byte[] _recvBuf = new byte[65536];
        // H2 fix: 后台 DoConnect 通过此队列将事件（Connected/Error）投递回 Tick 线程
        private readonly ConcurrentQueue<Action> _eventQueue = new();

        public TransportState State { get; private set; } = TransportState.Disconnected;

        /// <summary>
        /// 注入自定义 IUDPSession（用于单元测试 mock 或高级自定义场景）。
        /// 注入后 State 直接设为 Connected，不执行 DNS 解析或 Socket 操作。
        /// </summary>
        public KcpClientTransport(IUDPSession session)
        {
            _session = session;
            State = TransportState.Connected;
        }

        /// <summary>默认构造函数，使用真实 UDPSession。</summary>
        public KcpClientTransport() { }

        public event Action? OnConnected;
        public event Action? OnDisconnected;
        public event Action<byte[], int, int>? OnData;
        public event Action<NetworkError>? OnError;

        public void Connect(string host, int port)
        {
            Disconnect();

            _lastHost = host;
            _lastPort = port;
            State = TransportState.Connecting;

            // H2 fix: DNS 解析（UDPSession.Connect 内部调 Dns.GetHostEntry）可能阻塞数百 ms，
            // 放到后台线程避免卡主线程（与 TcpClientTransport 对称）。
            ThreadPool.QueueUserWorkItem(_ => DoConnect(host, port));
        }

        private void DoConnect(string host, int port)
        {
            try
            {
                var session = new UDPSession();
                session.AckNoDelay = true;
                session.WriteDelay = false;
                session.Connect(host, port); // DNS + socket bind 在后台线程执行

                _session = session;

                _eventQueue.Enqueue(() =>
                {
                    State = TransportState.Connected;
                    OnConnected?.Invoke();
                });
            }
            catch (Exception ex)
            {
                _eventQueue.Enqueue(() =>
                {
                    State = TransportState.Disconnected;
                    OnError?.Invoke(new NetworkError(ErrorCode.ConnectFailed, ex.Message));
                });
            }
        }

        public void Disconnect()
        {
            if (_session != null)
            {
                _session.Close();
                _session = null;
            }

            if (State == TransportState.Connected)
            {
                State = TransportState.Disconnected;
                OnDisconnected?.Invoke();
            }
            else
            {
                State = TransportState.Disconnected;
            }
        }

        public void Reconnect()
        {
            if (string.IsNullOrEmpty(_lastHost)) return;
            Connect(_lastHost, _lastPort);
        }

        public void Send(byte[] data, int offset, int length)
        {
            if (State != TransportState.Connected || _session == null)
                return;

            try
            {
                // C2 fix: Send 返回 0 表示 KCP 发送窗口已满，数据未发出，必须通知上层。
                // 修复前：返回值被静默忽略，游戏输入丢失且无任何错误提示。
                int sent = _session.Send(data, offset, length);
                if (sent == 0)
                {
                    OnError?.Invoke(new NetworkError(ErrorCode.SendFailed,
                        "KCP send window full: input dropped. Consider reducing send rate or increasing window size."));
                }
            }
            catch (Exception ex)
            {
                OnError?.Invoke(new NetworkError(ErrorCode.SendFailed, ex.Message));
                HandleDisconnect();
            }
        }

        /// <summary>
        /// 每帧调用。KCP 需要在 Tick 里:
        /// 1. 排出事件队列（H2 fix: DoConnect 通过 eventQueue 回调主线程）
        /// 2. Update() — 驱动 KCP 内部状态机（重传、ACK 等）
        /// 3. Recv() — 从 UDP socket poll 数据，经 KCP 解包后取出
        /// </summary>
        public void Tick()
        {
            // H2 fix: 先排出后台线程通过 _eventQueue 投递的事件（Connected/Error 等）
            while (_eventQueue.TryDequeue(out var action))
                action();

            if (_session == null || State != TransportState.Connected)
                return;

            try
            {
                // 驱动 KCP 内部状态（重传检测等）
                _session.Update();

                // 尝试接收数据
                while (true)
                {
                    int n = _session.Recv(_recvBuf, 0, _recvBuf.Length);
                    if (n <= 0) break;

                    // 拷贝到 pooled buffer 再回调
                    var pooled = ArrayPool<byte>.Shared.Rent(n);
                    Buffer.BlockCopy(_recvBuf, 0, pooled, 0, n);
                    OnData?.Invoke(pooled, 0, n);
                    ArrayPool<byte>.Shared.Return(pooled);
                }
            }
            catch (Exception ex)
            {
                OnError?.Invoke(new NetworkError(ErrorCode.TransportError, ex.Message));
                HandleDisconnect();
            }
        }

        private void HandleDisconnect()
        {
            if (State != TransportState.Connected) return;
            _session?.Close();
            _session = null;
            State = TransportState.Disconnected;
            OnDisconnected?.Invoke();
        }
    }
}
#endif
