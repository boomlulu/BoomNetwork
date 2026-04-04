#if !UNITY_WEBGL || UNITY_EDITOR
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;
using BoomNetwork.Core;
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
    ///
    /// 线程安全说明（C2 修复）：
    ///   _stream 声明为 volatile，确保 RecvLoop（recv 线程）立即可见 Disconnect() 写入的 null，
    ///   不依赖 stream.Read() 抛异常才能退出循环。
    ///
    ///   Send() 将 _stream 捕获移入 _sendLock 内（TOCTOU 消除）；
    ///   Disconnect() 在 lock(_sendLock) 内置空 _stream（极短临界区，仅 swap），
    ///   Close() 在锁外执行，不阻塞正在进行的 Send()。
    ///
    ///   保证：
    ///     - Send 看到非 null → 在同一把锁保护下完成写，Disconnect 只能在写完后关流
    ///     - Send 看到 null → 立即返回，无异常
    ///     - RecvLoop 即时感知 null，无需依赖 IOException 兜底
    /// </summary>
    public class TcpClientTransport : ITransport
    {
        private TcpClient? _client;

        // C2 fix: volatile 保证跨线程可见性（RecvLoop 及时看到 Disconnect 置 null）
        private volatile NetworkStream? _stream;

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

            // C2 fix: 在 _sendLock 内置空 _stream，保证：
            //   若 Send() 正持锁在写，则等写完后再置空，Close() 在锁外执行
            //   若 Send() 还未进锁，置空后 Send() 捕获到 null 直接返回
            NetworkStream? stream;
            TcpClient? client;
            lock (_sendLock)
            {
                stream = _stream;
                client = _client;
                _stream = null;
                _client = null;
            }

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

            // C2 fix: 将 _stream 捕获移入 _sendLock 内，消除锁外读→锁内用的 TOCTOU 窗口
            lock (_sendLock)
            {
                var stream = _stream;
                if (stream == null) return;

                try
                {
                    stream.Write(data, offset, length);
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
                    OnError?.Invoke(new NetworkError(ErrorCode.ConnectFailed, ex.Message));
                });
            }
        }

        private void RecvLoop()
        {
            var buffer = new byte[8192];
            try
            {
                // _stream 为 volatile，Disconnect() 置 null 后此处立即可见，无需依赖 IOException 退出
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
#endif
