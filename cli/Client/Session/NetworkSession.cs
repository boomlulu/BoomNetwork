using System;
using System.Buffers;
using System.Collections.Generic;
using BoomNetwork.Core;
using BoomNetwork.Core.Codec;
using BoomNetwork.Core.Framing;
using BoomNetwork.Core.Transport;

namespace BoomNetwork.Client.Session
{
    internal struct PendingRequest
    {
        public float TimeoutMs;
        public float ElapsedMs;
        public Action<Message>? OnResponse;
        public Action<NetworkError>? OnTimeout;
    }

    /// <summary>
    /// 已发送的消息缓冲（用于快速重连时重发）
    /// </summary>
    internal struct SentMessage
    {
        public int Seq;
        public byte[] EncodedData;
        public int EncodedLength;
    }

    /// <summary>
    /// 会话层
    ///
    /// 职责:
    ///   - 消息收发 + Seq 管理
    ///   - SendAsync 请求/响应匹配
    ///   - AckSeq 跟踪（客户端已确认处理到的服务器消息序号）
    ///   - 已发送消息缓冲区（支持快速重连时重发）
    ///   - Tick 驱动超时检测
    ///
    /// 不管: 心跳、重连策略、帧同步
    ///
    /// 线程安全说明：
    ///   _pendingRequests 由 PendingRequestTable 封装，内部使用 SpinLock。
    ///   主线程负责 Add / DrainTimeouts；收包线程负责 TryComplete。
    ///   所有 callback 均在持锁状态外调用。
    /// </summary>
    public class NetworkSession
    {
        private readonly ITransport _transport;
        private readonly LengthPrefixFraming _framing;

        // C1 fix: 替换为 SpinLock 封装的 PendingRequestTable，主线程+收包线程安全并发
        private readonly PendingRequestTable _pendingRequests = new();

        // 超时 callback 暂存列表（仅主线程 CheckTimeouts 使用，无需同步）
        private readonly List<PendingRequest> _timedOutRequests = new();
        // CancelAll 暂存列表（调用方临时使用，用后清空）
        private readonly List<PendingRequest> _cancelledRequests = new();

        private int _nextSeq = 1;
        private byte[] _encodeBuf;

        // --- AckSeq: 客户端已确认处理到的服务器消息序号 ---
        private int _lastRecvServerSeq;

        // --- 已发送消息缓冲区（快速重连用）---
        // P1-6: 改用 Queue<T>（循环数组）替代 LinkedList<T>
        //   LinkedList: 每个节点独立堆分配 + 指针追踪，缓存不友好
        //   Queue<T>:    连续循环数组，Enqueue/Dequeue O(1)，缓存局部性优秀
        private readonly Queue<SentMessage> _sentBuffer = new();

        /// <summary>
        /// 已发送缓冲区最大容量（超过后丢弃最早的）
        /// </summary>
        public int SentBufferCapacity { get; set; } = 256;

        // --- 事件 ---
        public event Action<Message>? OnMessage;
        public event Action? OnConnected;
        public event Action? OnDisconnected;
        public event Action<NetworkError>? OnError;

        // --- 状态 ---
        public TransportState State => _transport.State;
        public ITransport Transport => _transport;

        /// <summary>
        /// 客户端已确认处理到的服务器消息序号
        /// </summary>
        public int LastRecvServerSeq => _lastRecvServerSeq;

        /// <summary>
        /// 客户端下一个要发的 Seq
        /// </summary>
        public int NextSeq => _nextSeq;

        public NetworkSession(ITransport transport)
        {
            _transport = transport;
            _framing = new LengthPrefixFraming();
            _encodeBuf = ArrayPool<byte>.Shared.Rent(4096);

            _transport.OnConnected += () => OnConnected?.Invoke();
            _transport.OnDisconnected += HandleDisconnected;
            _transport.OnError += (err) => OnError?.Invoke(err); // 透传 transport 错误
            _transport.OnData += OnTransportData;
        }

        public void Connect(string host, int port)
        {
            _framing.Reset();
            _transport.Connect(host, port);
        }

        public void Disconnect()
        {
            _transport.Disconnect();
            CancelAllPending(ErrorCode.SessionReset, "Disconnected");
        }

        public void Reconnect()
        {
            _framing.Reset();
            _transport.Reconnect();
        }

        public void Tick(float deltaTimeMs)
        {
            _transport.Tick();
            CheckTimeouts(deltaTimeMs);
        }

        /// <summary>
        /// 发送 Core 消息（无 Seq）
        /// </summary>
        public void Send(byte cmd, byte[]? data = null, int dataLength = -1)
        {
            int len = dataLength >= 0 ? dataLength : (data?.Length ?? 0);
            var msg = new Message
            {
                MsgType = CmdType.Core,
                Cmd = cmd,
                HasSeq = false,
                Seq = 0,
                Data = data ?? Array.Empty<byte>(),
                DataLength = len,
            };
            SendRaw(msg);
        }

        /// <summary>
        /// 发送 Extended 消息（无 Seq）
        /// </summary>
        public void SendExt(ushort extCmd, byte[]? data = null, int dataLength = -1)
        {
            int len = dataLength >= 0 ? dataLength : (data?.Length ?? 0);
            var msg = new Message
            {
                MsgType = CmdType.Extended,
                ExtCmd = extCmd,
                HasSeq = false,
                Seq = 0,
                Data = data ?? Array.Empty<byte>(),
                DataLength = len,
            };
            SendRaw(msg);
        }

        /// <summary>
        /// 发送 Game 消息（无 Seq）
        /// </summary>
        public void SendGame(uint gameCmd, byte[]? data = null, int dataLength = -1)
        {
            int len = dataLength >= 0 ? dataLength : (data?.Length ?? 0);
            var msg = new Message
            {
                MsgType = CmdType.Game,
                GameCmd = gameCmd,
                HasSeq = false,
                Seq = 0,
                Data = data ?? Array.Empty<byte>(),
                DataLength = len,
            };
            SendRaw(msg);
        }

        /// <summary>
        /// 发送原始消息并缓冲（如果有 Seq）
        /// </summary>
        public void SendRaw(Message msg)
        {
            int size = MessageCodec.EncodedSize(msg);
            EnsureEncodeBuf(size);
            int written = MessageCodec.Encode(msg, _encodeBuf);
            _transport.Send(_encodeBuf, 0, written);

            // 有 Seq 的消息放入已发送缓冲区
            if (msg.HasSeq)
            {
                var copy = new byte[written];
                Buffer.BlockCopy(_encodeBuf, 0, copy, 0, written);
                _sentBuffer.Enqueue(new SentMessage
                {
                    Seq = msg.Seq,
                    EncodedData = copy,
                    EncodedLength = written,
                });

                // 控制缓冲区大小：超容时从队头移除最旧的消息
                while (_sentBuffer.Count > SentBufferCapacity)
                    _sentBuffer.Dequeue();
            }
        }

        /// <summary>
        /// 发送 Core 请求并等待响应
        /// </summary>
        public int SendAsync(byte cmd, byte[]? data, float timeoutMs,
            Action<Message>? onResponse, Action<NetworkError>? onTimeout = null)
        {
            int seq = _nextSeq++;
            var msg = new Message
            {
                MsgType = CmdType.Core,
                Cmd = cmd,
                HasSeq = true,
                Seq = seq,
                Data = data ?? Array.Empty<byte>(),
                DataLength = data?.Length ?? 0,
            };

            _pendingRequests.Add(seq, new PendingRequest
            {
                TimeoutMs = timeoutMs,
                ElapsedMs = 0,
                OnResponse = onResponse,
                OnTimeout = onTimeout,
            });

            SendRaw(msg);
            return seq;
        }

        /// <summary>
        /// 发送 Extended 请求并等待响应
        /// </summary>
        public int SendExtAsync(ushort extCmd, byte[]? data, float timeoutMs,
            Action<Message>? onResponse, Action<NetworkError>? onTimeout = null)
        {
            int seq = _nextSeq++;
            var msg = new Message
            {
                MsgType = CmdType.Extended,
                ExtCmd = extCmd,
                HasSeq = true,
                Seq = seq,
                Data = data ?? Array.Empty<byte>(),
                DataLength = data?.Length ?? 0,
            };

            _pendingRequests.Add(seq, new PendingRequest
            {
                TimeoutMs = timeoutMs,
                ElapsedMs = 0,
                OnResponse = onResponse,
                OnTimeout = onTimeout,
            });

            SendRaw(msg);
            return seq;
        }

        /// <summary>
        /// 清空已发送缓冲区（重连成功后调用）
        /// </summary>
        public void ClearSentBuffer()
        {
            _sentBuffer.Clear();
        }

        /// <summary>
        /// 清除所有状态（超时重连用）
        /// </summary>
        public void FullReset()
        {
            _framing.Reset();
            _sentBuffer.Clear();
            _lastRecvServerSeq = 0;
            CancelAllPending(ErrorCode.SessionReset, "Full reset");
        }

        /// <summary>
        /// 轻量清除（快速重连用）
        /// </summary>
        public void LightReset()
        {
            _framing.Reset();
            _sentBuffer.Clear();
            CancelAllPending(ErrorCode.SessionReset, "Light reset");
        }

        private void OnTransportData(byte[] data, int offset, int length)
        {
            _framing.Feed(data, offset, length);
            while (_framing.TryDequeueFrame(out var frame))
            {
                var msg = MessageCodec.Decode(frame.Span, usePool: true);
                frame.Dispose();

                // 跟踪服务器消息序号
                if (msg.HasSeq && msg.Seq > _lastRecvServerSeq)
                    _lastRecvServerSeq = msg.Seq;

                // H4: try/finally 确保 ArrayPool buffer 即使 callback 抛出也能归还
                try
                {
                    DispatchMessage(msg);
                }
                finally
                {
                    // 归还 ArrayPool buffer（上层如需保留 data 要自己 copy）
                    MessageCodec.ReturnData(ref msg);
                }
            }
        }

        // 收包线程：原子取出 pending → 锁外调用 callback
        private void DispatchMessage(Message msg)
        {
            if (msg.HasSeq && _pendingRequests.TryComplete(msg.Seq, out var pending))
            {
                pending.OnResponse?.Invoke(msg);
                return;
            }
            OnMessage?.Invoke(msg);
        }

        // 主线程：推进计时，锁外触发已超时 callback
        private void CheckTimeouts(float deltaTimeMs)
        {
            _timedOutRequests.Clear();
            _pendingRequests.DrainTimeouts(deltaTimeMs, _timedOutRequests);

            foreach (var req in _timedOutRequests)
                req.OnTimeout?.Invoke(new NetworkError(ErrorCode.RequestTimeout,
                    $"request timed out after {req.TimeoutMs}ms"));
        }

        // 主线程或收包线程：取出所有 pending → 锁外触发 callback
        private void CancelAllPending(ErrorCode code, string reason)
        {
            _cancelledRequests.Clear();
            _pendingRequests.CancelAll(_cancelledRequests);

            foreach (var req in _cancelledRequests)
                req.OnTimeout?.Invoke(new NetworkError(code, reason));
        }

        private void HandleDisconnected()
        {
            CancelAllPending(ErrorCode.ConnectionDropped, "Connection lost");
            OnDisconnected?.Invoke();
        }

        private void EnsureEncodeBuf(int size)
        {
            if (_encodeBuf.Length >= size) return;
            ArrayPool<byte>.Shared.Return(_encodeBuf);
            _encodeBuf = ArrayPool<byte>.Shared.Rent(size);
        }
    }
}
