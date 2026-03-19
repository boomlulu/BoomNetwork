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
    /// </summary>
    public class NetworkSession
    {
        private readonly ITransport _transport;
        private readonly LengthPrefixFraming _framing;
        private readonly Dictionary<int, PendingRequest> _pendingRequests = new();
        private readonly List<int> _timeoutKeys = new();

        private int _nextSeq = 1;
        private byte[] _encodeBuf;

        // --- AckSeq: 客户端已确认处理到的服务器消息序号 ---
        private int _lastRecvServerSeq;

        // --- 已发送消息缓冲区（快速重连用）---
        private readonly LinkedList<SentMessage> _sentBuffer = new();
        private int _lastAckedSeq; // 服务器已确认收到的 Seq

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
        /// 发送消息（无 Seq）
        /// </summary>
        public void Send(byte cmd, byte[]? data = null, int dataLength = -1)
        {
            int len = dataLength >= 0 ? dataLength : (data?.Length ?? 0);
            var msg = new Message
            {
                Cmd = cmd,
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
                _sentBuffer.AddLast(new SentMessage
                {
                    Seq = msg.Seq,
                    EncodedData = copy,
                    EncodedLength = written,
                });

                // 控制缓冲区大小
                while (_sentBuffer.Count > SentBufferCapacity)
                    _sentBuffer.RemoveFirst();
            }
        }

        /// <summary>
        /// 发送请求并等待响应
        /// </summary>
        public int SendAsync(byte cmd, byte[]? data, float timeoutMs,
            Action<Message>? onResponse, Action<NetworkError>? onTimeout = null)
        {
            int seq = _nextSeq++;
            var msg = new Message
            {
                Cmd = cmd,
                HasSeq = true,
                Seq = seq,
                Data = data ?? Array.Empty<byte>(),
                DataLength = data?.Length ?? 0,
            };

            _pendingRequests[seq] = new PendingRequest
            {
                TimeoutMs = timeoutMs,
                ElapsedMs = 0,
                OnResponse = onResponse,
                OnTimeout = onTimeout,
            };

            SendRaw(msg);
            return seq;
        }

        /// <summary>
        /// 服务器确认已收到的 Seq，清理缓冲区中已确认的消息
        /// </summary>
        public void AckServerReceived(int ackedSeq)
        {
            _lastAckedSeq = ackedSeq;
            while (_sentBuffer.Count > 0 && _sentBuffer.First!.Value.Seq <= ackedSeq)
            {
                _sentBuffer.RemoveFirst();
            }
        }

        /// <summary>
        /// 重发所有未确认的消息（快速重连用）
        /// </summary>
        /// <returns>重发的消息数</returns>
        public int ResendUnacked()
        {
            int count = 0;
            foreach (var sent in _sentBuffer)
            {
                _transport.Send(sent.EncodedData, 0, sent.EncodedLength);
                count++;
            }
            return count;
        }

        /// <summary>
        /// 清除所有状态（超时重连用）
        /// </summary>
        public void FullReset()
        {
            _framing.Reset();
            _sentBuffer.Clear();
            _lastRecvServerSeq = 0;
            _lastAckedSeq = 0;
            CancelAllPending(ErrorCode.SessionReset, "Full reset");
        }

        /// <summary>
        /// 轻量清除（快速重连用，保留缓冲区）
        /// </summary>
        public void LightReset()
        {
            _framing.Reset();
            CancelAllPending(ErrorCode.SessionReset, "Light reset");
        }

        private void OnTransportData(byte[] data, int offset, int length)
        {
            _framing.Feed(data, offset, length);
            while (_framing.TryDequeueFrame(out var frame))
            {
                var msg = MessageCodec.Decode(frame.Span);
                frame.Dispose();

                // 跟踪服务器消息序号
                if (msg.HasSeq && msg.Seq > _lastRecvServerSeq)
                    _lastRecvServerSeq = msg.Seq;

                DispatchMessage(msg);
            }
        }

        private void DispatchMessage(Message msg)
        {
            if (msg.HasSeq && _pendingRequests.Remove(msg.Seq, out var pending))
            {
                pending.OnResponse?.Invoke(msg);
                return;
            }
            OnMessage?.Invoke(msg);
        }

        private void CheckTimeouts(float deltaTimeMs)
        {
            _timeoutKeys.Clear();

            // 阶段 1：收集所有 key（不在遍历中修改字典）
            foreach (var kvp in _pendingRequests)
                _timeoutKeys.Add(kvp.Key);

            // 阶段 2：更新计时 + 检查超时
            for (int i = _timeoutKeys.Count - 1; i >= 0; i--)
            {
                int key = _timeoutKeys[i];
                if (!_pendingRequests.TryGetValue(key, out var req))
                {
                    _timeoutKeys.RemoveAt(i);
                    continue;
                }
                req.ElapsedMs += deltaTimeMs;
                _pendingRequests[key] = req;
                if (req.ElapsedMs < req.TimeoutMs)
                    _timeoutKeys.RemoveAt(i); // 没超时，从列表移除
            }

            // 阶段 3：处理超时的
            foreach (var key in _timeoutKeys)
            {
                if (_pendingRequests.Remove(key, out var req))
                    req.OnTimeout?.Invoke(new NetworkError(ErrorCode.RequestTimeout, $"seq={key} timed out after {req.TimeoutMs}ms"));
            }
        }

        private void CancelAllPending(ErrorCode code, string reason)
        {
            foreach (var kvp in _pendingRequests)
                kvp.Value.OnTimeout?.Invoke(new NetworkError(code, reason));
            _pendingRequests.Clear();
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
