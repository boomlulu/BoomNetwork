using System;
using System.Collections.Generic;
using BoomNetwork.Core;
using BoomNetwork.Core.Codec;
using BoomNetwork.Core.Framing;
using BoomNetwork.Core.Transport;

namespace BoomNetwork.Client.Session
{
    /// <summary>
    /// 异步请求结果
    /// </summary>
    public struct AsyncResult<T>
    {
        public bool Ok;
        public T Result;
        public string Error;
    }

    /// <summary>
    /// 待完成的异步请求
    /// </summary>
    internal class PendingRequest
    {
        public int Seq;
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
    ///   - SendAsync 请求/响应匹配（通过 ClientSeq）
    ///   - Tick 驱动的超时检测（不用 async/await）
    ///   - 消息分发
    ///
    /// 不管:
    ///   - 消息含义（心跳、帧数据等由上层处理）
    ///   - 重连策略（由上层决定）
    /// </summary>
    public class NetworkSession
    {
        private readonly ITransport _transport;
        private readonly LengthPrefixFraming _framing;
        private readonly List<PendingRequest> _pendingRequests = new();
        private readonly byte[] _encodeBuf = new byte[65536];

        private int _nextClientSeq = 1;

        /// <summary>
        /// 收到消息事件（非 SendAsync 的响应会触发此事件）
        /// </summary>
        public event Action<Message>? OnMessage;

        /// <summary>
        /// 连接成功
        /// </summary>
        public event Action? OnConnected;

        /// <summary>
        /// 连接断开
        /// </summary>
        public event Action? OnDisconnected;

        /// <summary>
        /// 错误
        /// </summary>
        public event Action<string>? OnError;

        public TransportState State => _transport.State;

        public NetworkSession(ITransport transport)
        {
            _transport = transport;
            _framing = new LengthPrefixFraming();

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
        public void Send(uint cmd, byte[]? data = null)
        {
            var msg = new Message
            {
                Version = 0,
                Cmd = cmd,
                ClientSeq = _nextClientSeq++,
                ServerSeq = 0,
                Data = data ?? Array.Empty<byte>(),
            };
            SendRaw(msg);
        }

        /// <summary>
        /// 发送原始消息（不修改 seq）
        /// </summary>
        public void SendRaw(Message msg)
        {
            int size = MessageCodec.EncodedSize(msg);
            var buf = size <= _encodeBuf.Length ? _encodeBuf : new byte[size];
            int written = MessageCodec.Encode(msg, buf);
            _transport.Send(buf, 0, written);
        }

        /// <summary>
        /// 发送请求并等待响应（Tick 驱动，不阻塞）
        ///
        /// 用法:
        ///   session.SendAsync(cmd, data, 3000,
        ///       onResponse: msg => { /* 成功 */ },
        ///       onTimeout:  err => { /* 超时 */ });
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
            };

            _pendingRequests.Add(new PendingRequest
            {
                Seq = seq,
                TimeoutMs = timeoutMs,
                ElapsedMs = 0,
                OnResponse = onResponse,
                OnTimeout = onTimeout,
            });

            SendRaw(msg);
            return seq;
        }

        /// <summary>
        /// 清除所有缓冲区和待处理请求
        /// </summary>
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
                var msg = MessageCodec.Decode(frame);
                DispatchMessage(msg);
            }
        }

        private void DispatchMessage(Message msg)
        {
            // 尝试匹配 pending request（通过 ClientSeq）
            for (int i = 0; i < _pendingRequests.Count; i++)
            {
                if (_pendingRequests[i].Seq == msg.ClientSeq)
                {
                    var pending = _pendingRequests[i];
                    _pendingRequests.RemoveAt(i);
                    pending.OnResponse?.Invoke(msg);
                    return;
                }
            }

            // 不是 pending response，作为普通消息分发
            OnMessage?.Invoke(msg);
        }

        private void CheckTimeouts(float deltaTimeMs)
        {
            for (int i = _pendingRequests.Count - 1; i >= 0; i--)
            {
                _pendingRequests[i].ElapsedMs += deltaTimeMs;
                if (_pendingRequests[i].ElapsedMs >= _pendingRequests[i].TimeoutMs)
                {
                    var pending = _pendingRequests[i];
                    _pendingRequests.RemoveAt(i);
                    pending.OnTimeout?.Invoke($"Request seq={pending.Seq} timed out after {pending.TimeoutMs}ms");
                }
            }
        }

        private void CancelAllPending(string reason)
        {
            foreach (var p in _pendingRequests)
            {
                p.OnTimeout?.Invoke(reason);
            }
            _pendingRequests.Clear();
        }

        private void HandleDisconnected()
        {
            CancelAllPending("Connection lost");
            OnDisconnected?.Invoke();
        }
    }
}
