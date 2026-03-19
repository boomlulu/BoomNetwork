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
        private UDPSession? _session;

        private string _lastHost = "";
        private int _lastPort;
        private readonly byte[] _recvBuf = new byte[65536];

        public TransportState State { get; private set; } = TransportState.Disconnected;

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

            try
            {
                _session = new UDPSession();
                _session.AckNoDelay = true;
                _session.WriteDelay = false;
                _session.Connect(host, port);

                State = TransportState.Connected;
                OnConnected?.Invoke();
            }
            catch (Exception ex)
            {
                State = TransportState.Disconnected;
                OnError?.Invoke(new NetworkError(ErrorCode.ConnectFailed, ex.Message));
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
                _session.Send(data, offset, length);
            }
            catch (Exception ex)
            {
                OnError?.Invoke(new NetworkError(ErrorCode.SendFailed, ex.Message));
                HandleDisconnect();
            }
        }

        /// <summary>
        /// 每帧调用。KCP 需要在 Tick 里:
        /// 1. Update() — 驱动 KCP 内部状态机（重传、ACK 等）
        /// 2. Recv() — 从 UDP socket poll 数据，经 KCP 解包后取出
        /// </summary>
        public void Tick()
        {
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
