#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Buffers;
using System.Runtime.InteropServices;
using BoomNetwork.Core;
using BoomNetwork.Core.Transport;

namespace BoomNetwork.Client.Transport
{
    /// <summary>
    /// WebGL 专用 WebSocket 传输层
    ///
    /// 通过 JS interop (.jslib) 桥接浏览器原生 WebSocket。
    /// 纯 Tick() 驱动，无线程、无 Task — 适配 WebGL 单线程环境。
    ///
    /// 架构: JS 侧 onmessage 推入队列，C# Tick() 轮询取出。
    /// </summary>
    public class WebGLWebSocketTransport : ITransport
    {
        [DllImport("__Internal")] private static extern int BoomNetworkWS_Connect(string url);
        [DllImport("__Internal")] private static extern int BoomNetworkWS_GetState(int id);
        [DllImport("__Internal")] private static extern int BoomNetworkWS_Send(int id, byte[] buf, int len);
        [DllImport("__Internal")] private static extern int BoomNetworkWS_Poll(int id, byte[] outBuf, int maxLen);
        [DllImport("__Internal")] private static extern int BoomNetworkWS_GetError(int id, byte[] outBuf, int maxLen);
        [DllImport("__Internal")] private static extern void BoomNetworkWS_Close(int id);

        // JS 侧状态值，与 jslib 中的 state 字段对应
        private const int JsStateDisconnected = 0;
        private const int JsStateConnecting = 1;
        private const int JsStateConnected = 2;

        private int _socketId = -1;
        private int _lastJsState = JsStateDisconnected;

        private string _lastHost = "";
        private int _lastPort;

        private readonly byte[] _recvBuf = new byte[65536];
        private readonly byte[] _errorBuf = new byte[512];

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

            var scheme = port == 443 ? "wss" : "ws";
            var url = $"{scheme}://{host}:{port}/";
            _socketId = BoomNetworkWS_Connect(url);
            _lastJsState = JsStateConnecting;
        }

        public void Disconnect()
        {
            if (_socketId >= 0)
            {
                BoomNetworkWS_Close(_socketId);
                _socketId = -1;
            }

            if (State == TransportState.Connected)
            {
                State = TransportState.Disconnected;
                _lastJsState = JsStateDisconnected;
                OnDisconnected?.Invoke();
            }
            else
            {
                State = TransportState.Disconnected;
                _lastJsState = JsStateDisconnected;
            }
        }

        public void Reconnect()
        {
            if (string.IsNullOrEmpty(_lastHost)) return;
            Connect(_lastHost, _lastPort);
        }

        public void Send(byte[] data, int offset, int length)
        {
            if (State != TransportState.Connected || _socketId < 0)
                return;

            // jslib 需要从 offset 开始的数据。如果 offset == 0 直接传；
            // 否则拷贝到临时 buffer（WebGL 单线程，无竞争）
            byte[] buf;
            if (offset == 0)
            {
                buf = data;
            }
            else
            {
                buf = ArrayPool<byte>.Shared.Rent(length);
                Buffer.BlockCopy(data, offset, buf, 0, length);
            }

            int ret = BoomNetworkWS_Send(_socketId, buf, length);

            if (offset != 0)
                ArrayPool<byte>.Shared.Return(buf);

            if (ret < 0)
            {
                OnError?.Invoke(new NetworkError(ErrorCode.SendFailed, "WebGL WebSocket send failed"));
                HandleDisconnect();
            }
        }

        public void Tick()
        {
            if (_socketId < 0) return;

            // 1. 检查状态变化
            int jsState = BoomNetworkWS_GetState(_socketId);
            if (jsState != _lastJsState)
            {
                int prev = _lastJsState;
                _lastJsState = jsState;

                if (jsState == JsStateConnected && prev == JsStateConnecting)
                {
                    State = TransportState.Connected;
                    OnConnected?.Invoke();
                }
                else if (jsState == JsStateDisconnected)
                {
                    CheckError();
                    HandleDisconnect();
                    return;
                }
            }

            // 2. 检查连接中的错误（如连接失败）
            if (State == TransportState.Connecting && jsState == JsStateDisconnected)
            {
                CheckError();
                State = TransportState.Disconnected;
                return;
            }

            // 3. 轮询接收数据
            if (State != TransportState.Connected) return;

            while (true)
            {
                int n = BoomNetworkWS_Poll(_socketId, _recvBuf, _recvBuf.Length);
                if (n <= 0) break;

                var pooled = ArrayPool<byte>.Shared.Rent(n);
                Buffer.BlockCopy(_recvBuf, 0, pooled, 0, n);
                OnData?.Invoke(pooled, 0, n);
                ArrayPool<byte>.Shared.Return(pooled);
            }
        }

        private void HandleDisconnect()
        {
            if (State != TransportState.Connected && State != TransportState.Connecting)
                return;

            var wasConnected = State == TransportState.Connected;
            State = TransportState.Disconnected;

            if (_socketId >= 0)
            {
                BoomNetworkWS_Close(_socketId);
                _socketId = -1;
            }

            if (wasConnected)
            {
                OnDisconnected?.Invoke();
            }
        }

        private void CheckError()
        {
            if (_socketId < 0) return;

            int errLen = BoomNetworkWS_GetError(_socketId, _errorBuf, _errorBuf.Length);
            if (errLen > 0)
            {
                var msg = System.Text.Encoding.UTF8.GetString(_errorBuf, 0, errLen);
                OnError?.Invoke(new NetworkError(ErrorCode.ConnectFailed, msg));
            }
        }
    }
}
#endif
