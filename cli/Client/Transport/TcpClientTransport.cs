using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;
using BoomNetwork.Core.Transport;

namespace BoomNetwork.Client.Transport
{
    /// <summary>
    /// TCP 客户端传输层
    ///
    /// IO 线程负责 socket 读写，主线程通过 Tick() 取数据。
    /// 线程同步通过 ConcurrentQueue 实现。
    /// </summary>
    public class TcpClientTransport : ITransport
    {
        private TcpClient? _client;
        private NetworkStream? _stream;
        private Thread? _recvThread;
        private volatile bool _running;

        private string _lastHost = "";
        private int _lastPort;

        private readonly ConcurrentQueue<byte[]> _recvQueue = new();
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

            // 在后台线程执行连接
            var connectThread = new Thread(() => DoConnect(host, port))
            {
                IsBackground = true,
                Name = "BoomNet-Connect"
            };
            connectThread.Start();
        }

        public void Disconnect()
        {
            _running = false;
            try { _stream?.Close(); } catch { }
            try { _client?.Close(); } catch { }
            _stream = null;
            _client = null;

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
            if (State != TransportState.Connected || _stream == null)
                return;

            lock (_sendLock)
            {
                try
                {
                    _stream.Write(data, offset, length);
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
            // 处理事件
            while (_eventQueue.TryDequeue(out var action))
            {
                action();
            }

            // 处理收到的数据
            while (_recvQueue.TryDequeue(out var data))
            {
                OnData?.Invoke(data, 0, data.Length);
            }
        }

        private void DoConnect(string host, int port)
        {
            try
            {
                var client = new TcpClient();
                client.NoDelay = true;
                client.Connect(host, port);

                _client = client;
                _stream = client.GetStream();
                _running = true;

                _eventQueue.Enqueue(() =>
                {
                    State = TransportState.Connected;
                    OnConnected?.Invoke();
                });

                // 启动接收线程
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
                while (_running && _stream != null)
                {
                    int bytesRead = _stream.Read(buffer, 0, buffer.Length);
                    if (bytesRead == 0)
                    {
                        // 服务器关闭连接
                        HandleDisconnect();
                        return;
                    }

                    var data = new byte[bytesRead];
                    Buffer.BlockCopy(buffer, 0, data, 0, bytesRead);
                    _recvQueue.Enqueue(data);
                }
            }
            catch (Exception)
            {
                if (_running)
                {
                    HandleDisconnect();
                }
            }
        }

        private void HandleDisconnect()
        {
            if (!_running) return;
            _running = false;
            _eventQueue.Enqueue(() =>
            {
                State = TransportState.Disconnected;
                OnDisconnected?.Invoke();
            });
        }
    }
}
