using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;
using BoomNetwork.Core.Transport;

namespace BoomNetwork.Client.Transport
{
    /// <summary>
    /// 收到的数据块（从 ArrayPool 租借）
    /// </summary>
    internal struct RecvChunk
    {
        public byte[] Buffer;
        public int Length;
    }

    /// <summary>
    /// TCP 客户端传输层
    ///
    /// IO 线程负责 socket 读写，主线程通过 Tick() 取数据。
    /// 收到的数据使用 ArrayPool 租借，Tick 处理完后归还。
    /// </summary>
    public class TcpClientTransport : ITransport
    {
        private TcpClient? _client;
        private NetworkStream? _stream;
        private Thread? _recvThread;

        // 用 int + Interlocked 代替 volatile bool，避免竞态
        private int _running; // 0=stopped, 1=running
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
        public event Action<string>? OnError;

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

            var stream = _stream;
            var client = _client;
            _stream = null;
            _client = null;

            try { stream?.Close(); } catch { }
            try { client?.Close(); } catch { }

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

            var stream = _stream;
            if (stream == null) return;

            lock (_sendLock)
            {
                try
                {
                    stream.Write(data, offset, length);
                }
                catch (Exception ex)
                {
                    _eventQueue.Enqueue(() => OnError?.Invoke($"Send failed: {ex.Message}"));
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
                var client = new TcpClient();
                client.NoDelay = true;
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                client.Connect(host, port);

                _client = client;
                _stream = client.GetStream();
                Interlocked.Exchange(ref _running, 1);

                _eventQueue.Enqueue(() =>
                {
                    State = TransportState.Connected;
                    OnConnected?.Invoke();
                });

                _recvThread = new Thread(RecvLoop)
                {
                    IsBackground = true,
                    Name = "BoomNet-Recv"
                };
                _recvThread.Start();
            }
            catch (Exception ex)
            {
                _eventQueue.Enqueue(() =>
                {
                    State = TransportState.Disconnected;
                    OnError?.Invoke($"Connect failed: {ex.Message}");
                });
            }
        }

        private void RecvLoop()
        {
            var buffer = new byte[8192];
            try
            {
                while (Interlocked.CompareExchange(ref _running, 1, 1) == 1)
                {
                    var stream = _stream;
                    if (stream == null) break;

                    int bytesRead = stream.Read(buffer, 0, buffer.Length);
                    if (bytesRead == 0)
                    {
                        HandleDisconnect();
                        return;
                    }

                    // 从 ArrayPool 租借，拷贝数据，入队
                    var pooled = ArrayPool<byte>.Shared.Rent(bytesRead);
                    Buffer.BlockCopy(buffer, 0, pooled, 0, bytesRead);
                    _recvQueue.Enqueue(new RecvChunk { Buffer = pooled, Length = bytesRead });
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
            // 确保只触发一次
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
