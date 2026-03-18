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
        public Action<string>? OnTimeout;
    }

    /// <summary>
    /// 会话层 — 适配新动态包头格式
    /// </summary>
    public class NetworkSession
    {
        private readonly ITransport _transport;
        private readonly LengthPrefixFraming _framing;
        private readonly Dictionary<int, PendingRequest> _pendingRequests = new();
        private readonly List<int> _timeoutKeys = new();

        private int _nextSeq = 1;
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

        public void Tick(float deltaTimeMs)
        {
            _transport.Tick();
            CheckTimeouts(deltaTimeMs);
        }

        /// <summary>
        /// 发送消息（无 Seq，不需要回复）
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

        public void SendRaw(Message msg)
        {
            int size = MessageCodec.EncodedSize(msg);
            EnsureEncodeBuf(size);
            int written = MessageCodec.Encode(msg, _encodeBuf);
            _transport.Send(_encodeBuf, 0, written);
        }

        /// <summary>
        /// 发送请求并等待响应（带 Seq 匹配）
        /// </summary>
        public int SendAsync(byte cmd, byte[]? data, float timeoutMs,
            Action<Message>? onResponse, Action<string>? onTimeout = null)
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
                frame.Dispose();
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
            foreach (var kvp in _pendingRequests)
            {
                var req = kvp.Value;
                req.ElapsedMs += deltaTimeMs;
                _pendingRequests[kvp.Key] = req;
                if (req.ElapsedMs >= req.TimeoutMs)
                    _timeoutKeys.Add(kvp.Key);
            }
            foreach (var key in _timeoutKeys)
            {
                if (_pendingRequests.Remove(key, out var req))
                    req.OnTimeout?.Invoke($"Request seq={key} timed out after {req.TimeoutMs}ms");
            }
        }

        private void CancelAllPending(string reason)
        {
            foreach (var kvp in _pendingRequests)
                kvp.Value.OnTimeout?.Invoke(reason);
            _pendingRequests.Clear();
        }

        private void HandleDisconnected()
        {
            CancelAllPending("Connection lost");
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
