using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading;
using BoomNetwork.Core;
using BoomNetwork.Core.Transport;

namespace BoomNetwork.Client.Transport
{
    /// <summary>
    /// WebSocket 客户端传输层
    ///
    /// 基于 System.Net.WebSockets.ClientWebSocket 实现。
    /// 和 TcpClientTransport 实现相同的 ITransport 接口，上层无感切换。
    ///
    /// 架构与 TCP 版一致：后台线程收发，Tick 取数据投递主线程。
    /// </summary>
    public class WebSocketClientTransport : ITransport
    {
        private ClientWebSocket? _ws;
        private Thread? _recvThread;

        private int _running;           // 0=stopped, 1=running
        private int _disconnectHandled; // 防止重复触发 disconnect

        private string _lastHost = "";
        private int _lastPort;

        private readonly ConcurrentQueue<RecvChunk> _recvQueue = new();
        private readonly ConcurrentQueue<Action> _eventQueue = new();
        private readonly object _sendLock = new();

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
            Interlocked.Exchange(ref _disconnectHandled, 0);

            ThreadPool.QueueUserWorkItem(_ => DoConnect(host, port));
        }

        public void Disconnect()
        {
            Interlocked.Exchange(ref _running, 0);

            var ws = _ws;
            _ws = null;

            if (ws != null)
            {
                try
                {
                    if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
                    {
                        // 发送 Close 帧，最多等 2 秒
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", cts.Token)
                            .ConfigureAwait(false).GetAwaiter().GetResult();
                    }
                }
                catch { }

                try { ws.Dispose(); } catch { }
            }

            if (State == TransportState.Connected)
            {
                State = TransportState.Disconnected;
                _eventQueue.Enqueue(() => OnDisconnected?.Invoke());
            }
            else
            {
                State = TransportState.Disconnected;
            }
        }

        public void Reconnect()
        {
            if (string.IsNullOrEmpty(_lastHost))
                return;
            Connect(_lastHost, _lastPort);
        }

        public void Send(byte[] data, int offset, int length)
        {
            if (State != TransportState.Connected)
                return;

            var ws = _ws;
            if (ws == null) return;

            lock (_sendLock)
            {
                try
                {
                    var segment = new ArraySegment<byte>(data, offset, length);
                    ws.SendAsync(segment, WebSocketMessageType.Binary, true, CancellationToken.None)
                        .ConfigureAwait(false).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    _eventQueue.Enqueue(() => OnError?.Invoke(new NetworkError(ErrorCode.SendFailed, ex.Message)));
                    HandleDisconnect();
                }
            }
        }

        public void Tick()
        {
            while (_eventQueue.TryDequeue(out var action))
            {
                action();
            }

            while (_recvQueue.TryDequeue(out var chunk))
            {
                OnData?.Invoke(chunk.Buffer, 0, chunk.Length);
                ArrayPool<byte>.Shared.Return(chunk.Buffer);
            }
        }

        private void DoConnect(string host, int port)
        {
            try
            {
                var ws = new ClientWebSocket();
                var uri = new Uri($"ws://{host}:{port}/");

                ws.ConnectAsync(uri, CancellationToken.None)
                    .ConfigureAwait(false).GetAwaiter().GetResult();

                _ws = ws;
                Interlocked.Exchange(ref _running, 1);

                _eventQueue.Enqueue(() =>
                {
                    State = TransportState.Connected;
                    OnConnected?.Invoke();
                });

                _recvThread = new Thread(RecvLoop)
                {
                    IsBackground = true,
                    Name = "BoomNet-WsRecv"
                };
                _recvThread.Start();
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

        private void RecvLoop()
        {
            // WebSocket 是消息边界协议，但 BoomNetwork 的 LengthPrefixFraming
            // 需要流式字节，所以我们把收到的每条 WS 消息作为原始字节块入队，
            // 上层 LengthPrefixFraming 负责拼帧。
            var buffer = new byte[65536];
            try
            {
                while (Interlocked.CompareExchange(ref _running, 1, 1) == 1)
                {
                    var ws = _ws;
                    if (ws == null) break;

                    var segment = new ArraySegment<byte>(buffer);
                    var result = ws.ReceiveAsync(segment, CancellationToken.None)
                        .ConfigureAwait(false).GetAwaiter().GetResult();

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        HandleDisconnect();
                        return;
                    }

                    if (result.Count > 0)
                    {
                        var pooled = ArrayPool<byte>.Shared.Rent(result.Count);
                        Buffer.BlockCopy(buffer, 0, pooled, 0, result.Count);
                        _recvQueue.Enqueue(new RecvChunk { Buffer = pooled, Length = result.Count });
                    }
                }
            }
            catch (Exception)
            {
                if (Interlocked.CompareExchange(ref _running, 0, 0) == 1)
                {
                    HandleDisconnect();
                }
            }
        }

        private void HandleDisconnect()
        {
            if (Interlocked.CompareExchange(ref _disconnectHandled, 1, 0) != 0)
                return;

            Interlocked.Exchange(ref _running, 0);
            _eventQueue.Enqueue(() =>
            {
                State = TransportState.Disconnected;
                OnDisconnected?.Invoke();
            });
        }
    }
}
