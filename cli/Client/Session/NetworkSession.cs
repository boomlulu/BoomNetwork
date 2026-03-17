using System;
using System.Buffers;
using System.Collections.Generic;
using BoomNetwork.Core;
using BoomNetwork.Core.Codec;
using BoomNetwork.Core.Framing;
using BoomNetwork.Core.Transport;

namespace BoomNetwork.Client.Session
{
    /// <summary>
    /// 待完成的异步请求
    /// </summary>
    internal struct PendingRequest
    {
        public float TimeoutMs;
        public float ElapsedMs;
        public Action<Message>? OnResponse;
        public Action<string>? OnTimeout;
    }

    /// <summary>
    /// 会话层 — 在 Transport + Framing + Codec 之上提供可靠消息通信
    ///
    /// 职责:
    ///   - 自动管理 ClientSeq 编号
    ///   - SendAsync 请求/响应匹配（Dictionary O(1) 查找）
    ///   - Tick 驱动的超时检测
    ///   - 消息分发
    /// </summary>
    public class NetworkSession
    {
        private readonly ITransport _transport;
        private readonly LengthPrefixFraming _framing;
        private readonly Dictionary<int, PendingRequest> _pendingRequests = new();
        private readonly List<int> _timeoutKeys = new(); // 复用列表，避免每帧分配

        private int _nextClientSeq = 1;

        // 发送缓冲区，按需扩展
        private byte[] _encodeBuf;

        public event Action<Message>? OnMessage;
        public event Action? OnConnected;
        public event Action? OnDisconnected;
        public event Action<string>? OnError;

        public TransportState State => _transport.State;

        public NetworkSession(ITransport transport)
        {
            _transport = transport;
            _framing = new LengthPrefixFraming();
            _encodeBuf = ArrayPool<byte>.Shared.Rent(4096);

            _transport.OnConnected += () => OnConnected?.Invoke();
            _transport.OnDisconnected += HandleDisconnected;
            _transport.OnError += (err) => OnError?.Invoke(err);
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
            CancelAllPending("Disconnected");
        }

        public void Reconnect()
        {
            _framing.Reset();
            _transport.Reconnect();
        }

        /// <summary>
        /// 每帧调用。驱动 Transport + 超时检测。
        /// </summary>
        public void Tick(float deltaTimeMs)
        {
            _transport.Tick();
            CheckTimeouts(deltaTimeMs);
        }

        /// <summary>
        /// 发送消息（自动分配 ClientSeq）
        /// </summary>
        public void Send(uint cmd, byte[]? data = null, int dataLength = -1)
        {
            int len = data?.Length ?? 0;
            if (dataLength >= 0) len = dataLength;

            var msg = new Message
            {
                Version = 0,
                Cmd = cmd,
                ClientSeq = _nextClientSeq++,
                ServerSeq = 0,
                Data = data ?? Array.Empty<byte>(),
                DataLength = len,
            };
            SendRaw(msg);
        }

        /// <summary>
        /// 发送原始消息
        /// </summary>
        public void SendRaw(Message msg)
        {
            int size = MessageCodec.EncodedSize(msg);
            EnsureEncodeBuf(size);
            int written = MessageCodec.Encode(msg, _encodeBuf);
            _transport.Send(_encodeBuf, 0, written);
        }

        /// <summary>
        /// 发送请求并等待响应（Tick 驱动，回调式）
        /// </summary>
        public int SendAsync(uint cmd, byte[]? data, float timeoutMs,
            Action<Message>? onResponse, Action<string>? onTimeout = null)
        {
            int seq = _nextClientSeq++;
            var msg = new Message
            {
                Version = 0,
                Cmd = cmd,
                ClientSeq = seq,
                ServerSeq = 0,
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

        public void Clear()
        {
            _framing.Reset();
            CancelAllPending("Session cleared");
        }

        private void OnTransportData(byte[] data, int offset, int length)
        {
            _framing.Feed(data, offset, length);

            while (_framing.TryDequeueFrame(out var frame))
            {
                var msg = MessageCodec.Decode(frame.Span);
                frame.Dispose(); // 归还 framing 的 ArrayPool buffer
                DispatchMessage(msg);
            }
        }

        private void DispatchMessage(Message msg)
        {
            if (_pendingRequests.Remove(msg.ClientSeq, out var pending))
            {
                pending.OnResponse?.Invoke(msg);
                return;
            }

            OnMessage?.Invoke(msg);
        }

        private void CheckTimeouts(float deltaTimeMs)
        {
            _timeoutKeys.Clear();

            foreach (var kvp in _pendingRequests)
            {
                var req = kvp.Value;
                req.ElapsedMs += deltaTimeMs;
                _pendingRequests[kvp.Key] = req; // struct 需要写回

                if (req.ElapsedMs >= req.TimeoutMs)
                {
                    _timeoutKeys.Add(kvp.Key);
                }
            }

            foreach (var key in _timeoutKeys)
            {
                if (_pendingRequests.Remove(key, out var req))
                {
                    req.OnTimeout?.Invoke($"Request seq={key} timed out after {req.TimeoutMs}ms");
                }
            }
        }

        private void CancelAllPending(string reason)
        {
            foreach (var kvp in _pendingRequests)
            {
                kvp.Value.OnTimeout?.Invoke(reason);
            }
            _pendingRequests.Clear();
        }

        private void HandleDisconnected()
        {
            CancelAllPending("Connection lost");
            OnDisconnected?.Invoke();
        }

        private void EnsureEncodeBuf(int size)
        {
            if (_encodeBuf.Length >= size)
                return;
            ArrayPool<byte>.Shared.Return(_encodeBuf);
            _encodeBuf = ArrayPool<byte>.Shared.Rent(size);
        }
    }
}
