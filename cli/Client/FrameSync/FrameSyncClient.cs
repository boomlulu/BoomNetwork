using System;
using BoomNetwork.Core;
using BoomNetwork.Core.Codec;
using BoomNetwork.Core.FrameSync;
using BoomNetwork.Client.Session;

namespace BoomNetwork.Client.FrameSync
{
    /// <summary>
    /// 帧同步客户端
    ///
    /// 职责:
    ///   - 连接服务器 + SessionBind
    ///   - 发送玩家输入 (FrameInput)
    ///   - 接收帧数据 (PushFrames) → 回调执行
    ///   - 管理帧同步状态
    ///
    /// 使用方式:
    ///   每帧调用 Tick(dt)，帧执行通过 OnFrame 回调通知。
    /// </summary>
    public class FrameSyncClient
    {
        private readonly NetworkSession _session;

        private int _playerId;
        private bool _frameSyncStarted;

        // 编码缓冲区
        private readonly byte[] _inputBuf = new byte[4096];

        public enum State
        {
            Disconnected,
            Connecting,
            Binding,
            WaitingStart,
            Syncing,
            Stopped,
        }

        public State CurrentState { get; private set; } = State.Disconnected;
        public int PlayerId => _playerId;
        public FrameSyncInitData? InitData { get; private set; }
        public uint LastFrameNumber { get; private set; }

        // --- 事件 ---

        /// <summary>
        /// SessionBind 完成，参数: playerId
        /// </summary>
        public event Action<int>? OnBound;

        /// <summary>
        /// 帧同步开始
        /// </summary>
        public event Action<FrameSyncInitData>? OnFrameSyncStart;

        /// <summary>
        /// 收到一帧数据
        /// </summary>
        public event Action<FrameData>? OnFrame;

        /// <summary>
        /// 帧同步结束
        /// </summary>
        public event Action? OnFrameSyncStop;

        /// <summary>
        /// 连接断开
        /// </summary>
        public event Action? OnDisconnected;

        /// <summary>
        /// 错误
        /// </summary>
        public event Action<string>? OnError;

        public FrameSyncClient(NetworkSession session)
        {
            _session = session;
            _session.OnMessage += HandleMessage;
            _session.OnDisconnected += HandleDisconnected;
            _session.OnError += (err) => OnError?.Invoke(err);
        }

        /// <summary>
        /// 连接并绑定会话
        /// </summary>
        public void Connect(string host, int port)
        {
            CurrentState = State.Connecting;
            _session.OnConnected += OnConnected;
            _session.Connect(host, port);
        }

        /// <summary>
        /// 每帧调用
        /// </summary>
        public void Tick(float deltaTimeMs)
        {
            _session.Tick(deltaTimeMs);
        }

        /// <summary>
        /// 发送玩家输入
        /// </summary>
        public void SendInput(byte[] data, int dataLength = -1)
        {
            if (CurrentState != State.Syncing)
                return;

            int len = dataLength >= 0 ? dataLength : data.Length;
            _session.Send(FrameSyncCmd.FrameInput, data, len);
        }

        /// <summary>
        /// 断开连接
        /// </summary>
        public void Disconnect()
        {
            _session.Disconnect();
            CurrentState = State.Disconnected;
        }

        private void OnConnected()
        {
            _session.OnConnected -= OnConnected;
            CurrentState = State.Binding;

            // 发送 SessionBind
            _session.SendAsync(FrameSyncCmd.SessionBind, null, 5000,
                onResponse: msg =>
                {
                    if (msg.DataLength >= 4)
                    {
                        _playerId = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(msg.DataSpan);
                    }
                    CurrentState = State.WaitingStart;
                    OnBound?.Invoke(_playerId);

                    // 如果在 bind 之前收到了 StartFrameSync，现在处理
                    if (_pendingStartMsg.HasValue)
                    {
                        HandleStartFrameSync(_pendingStartMsg.Value);
                        _pendingStartMsg = null;
                    }
                },
                onTimeout: err =>
                {
                    OnError?.Invoke($"SessionBind timeout: {err}");
                    CurrentState = State.Disconnected;
                });
        }

        private Message? _pendingStartMsg;

        private void HandleMessage(Message msg)
        {
            switch (msg.Cmd)
            {
                case FrameSyncCmd.StartFrameSync:
                    if (CurrentState < State.WaitingStart)
                    {
                        // 还没 bind 完，缓存起来
                        _pendingStartMsg = msg;
                    }
                    else
                    {
                        HandleStartFrameSync(msg);
                    }
                    break;

                case FrameSyncCmd.PushFrames:
                    HandlePushFrames(msg);
                    break;

                case FrameSyncCmd.StopFrameSync:
                    HandleStopFrameSync();
                    break;
            }
        }

        private void HandleStartFrameSync(Message msg)
        {
            if (msg.DataLength >= FrameSyncInitData.Size)
            {
                InitData = FrameSyncInitData.ReadFrom(msg.DataSpan);
            }
            _frameSyncStarted = true;
            LastFrameNumber = 0;
            CurrentState = State.Syncing;
            OnFrameSyncStart?.Invoke(InitData ?? default);
        }

        private void HandlePushFrames(Message msg)
        {
            if (!_frameSyncStarted || msg.DataLength == 0)
                return;

            var frame = FrameDataCodec.Decode(msg.DataSpan);
            LastFrameNumber = frame.FrameNumber;
            OnFrame?.Invoke(frame);
        }

        private void HandleStopFrameSync()
        {
            _frameSyncStarted = false;
            CurrentState = State.Stopped;
            OnFrameSyncStop?.Invoke();
        }

        private void HandleDisconnected()
        {
            _frameSyncStarted = false;
            CurrentState = State.Disconnected;
            OnDisconnected?.Invoke();
        }
    }
}
