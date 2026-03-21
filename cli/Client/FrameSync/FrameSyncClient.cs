using System;
using System.Buffers.Binary;
using BoomNetwork.Core;
using BoomNetwork.Core.FrameSync;

using BoomNetwork.Client.Session;
using BoomNetwork.Client.Connection;

namespace BoomNetwork.Client.FrameSync
{
    /// <summary>
    /// 帧同步客户端
    ///
    /// 职责: 只管帧同步逻辑
    ///   - SessionBind 协议
    ///   - 帧接收 + 分发
    ///   - 输入发送
    ///   - 帧同步状态 (WaitingStart / Syncing / Stopped)
    ///
    /// 不管: 连接、心跳、重连（由 ConnectionManager 管理）
    /// </summary>
    public class FrameSyncClient
    {
        private readonly NetworkSession _session;
        private readonly ConnectionManager _connectionManager;

        public enum State
        {
            Disconnected,
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
        public event Action<int>? OnBound;
        public event Action<FrameSyncInitData>? OnFrameSyncStart;
        public event Action<FrameData>? OnFrame;
        public event Action? OnFrameSyncStop;
        public event Action? OnDisconnected;
        public event Action? OnReconnected;
        public event Action<NetworkError>? OnError;

        // --- 内部 ---
        private int _playerId;
        private bool _frameSyncStarted;
        private Message? _pendingStartMsg;

        public ConnectionManager ConnectionManager => _connectionManager;

        private bool _skipAutoSessionBind;

        public FrameSyncClient(NetworkSession session, ConnectionManager connectionManager, bool skipAutoSessionBind = false)
        {
            _session = session;
            _connectionManager = connectionManager;
            _skipAutoSessionBind = skipAutoSessionBind;

            // 监听 Session 消息（帧数据由 Session 的 OnMessage 抛上来）
            _session.OnMessage += HandleMessage;

            // 监听 ConnectionManager 事件
            _connectionManager.OnConnected += OnConnectionEstablished;
            _connectionManager.OnDisconnected += OnConnectionLost;
            _connectionManager.OnReconnected += OnConnectionReconnected;
            _connectionManager.OnError += (err) => OnError?.Invoke(err);
            _connectionManager.OnLog += (msg) => { /* 日志级别，不作为错误传播 */ };
        }

        /// <summary>
        /// 连接并绑定
        /// </summary>
        public void Connect(string host, int port)
        {
            _connectionManager.Connect(host, port);
        }

        /// <summary>
        /// 每帧调用
        /// </summary>
        public void Tick(float deltaTimeMs)
        {
            _connectionManager.Tick(deltaTimeMs);
        }

        /// <summary>
        /// 发送玩家输入
        /// </summary>
        public void SendInput(byte[] data, int dataLength = -1)
        {
            if (CurrentState != State.Syncing) return;
            int len = dataLength >= 0 ? dataLength : data.Length;
            _session.Send(FrameSyncCmd.FrameInput, data, len);
        }

        /// <summary>
        /// 主动断开
        /// </summary>
        public void Disconnect()
        {
            _connectionManager.Disconnect();
            CurrentState = State.Disconnected;
        }

        #region ConnectionManager Callbacks

        private void OnConnectionEstablished()
        {
            if (_skipAutoSessionBind)
            {
                // 重连模式：不发 SessionBind，由 Person 发 Reconnect
                CurrentState = State.WaitingStart;
                return;
            }

            // 首次连接成功 → 发 SessionBind
            CurrentState = State.Binding;

            _session.SendAsync(FrameSyncCmd.SessionBind, null, 5000,
                onResponse: msg =>
                {
                    if (msg.DataLength >= 4)
                        _playerId = (int)BinaryPrimitives.ReadUInt32LittleEndian(msg.DataSpan);

                    _connectionManager.SetPlayerId(_playerId);
                    CurrentState = State.WaitingStart;
                    OnBound?.Invoke(_playerId);

                    if (_pendingStartMsg.HasValue)
                    {
                        HandleStartFrameSync(_pendingStartMsg.Value);
                        _pendingStartMsg = null;
                    }
                },
                onTimeout: err =>
                {
                    OnError?.Invoke(new NetworkError(ErrorCode.SessionBindTimeout, err.Message));
                    _connectionManager.Disconnect();
                });
        }

        private void OnConnectionLost()
        {
            _frameSyncStarted = false;
            CurrentState = State.Disconnected;
            OnDisconnected?.Invoke();
        }

        private void OnConnectionReconnected(ReconnectContext context)
        {
            LastFrameNumber = context.ServerFrameNumber;

            if (_frameSyncStarted)
            {
                CurrentState = State.Syncing;
            }
            else
            {
                CurrentState = State.WaitingStart;
            }

            OnReconnected?.Invoke();
        }

        #endregion

        #region Message Handling

        private void HandleMessage(Message msg)
        {
            switch (msg.Cmd)
            {
                case FrameSyncCmd.StartFrameSync:
                    if (CurrentState < State.WaitingStart)
                        _pendingStartMsg = msg;
                    else
                        HandleStartFrameSync(msg);
                    break;

                case FrameSyncCmd.PushFrames:
                    HandlePushFrames(msg);
                    break;

                case FrameSyncCmd.StopFrameSync:
                    HandleStopFrameSync();
                    break;

                // HeartbeatRsp 由 ConnectionManager 处理，不到这里
            }
        }

        private void HandleStartFrameSync(Message msg)
        {
            if (msg.DataLength >= FrameSyncInitData.Size)
                InitData = FrameSyncInitData.ReadFrom(msg.DataSpan);
            _frameSyncStarted = true;
            LastFrameNumber = 0;
            CurrentState = State.Syncing;
            OnFrameSyncStart?.Invoke(InitData ?? default);
        }

        private void HandlePushFrames(Message msg)
        {
            if (!_frameSyncStarted || msg.DataLength == 0) return;
            var frame = FrameDataCodec.Decode(msg.DataSpan);
            LastFrameNumber = frame.FrameNumber;
            _connectionManager.UpdateFrameNumber(frame.FrameNumber);
            OnFrame?.Invoke(frame);
        }

        private void HandleStopFrameSync()
        {
            _frameSyncStarted = false;
            CurrentState = State.Stopped;
            OnFrameSyncStop?.Invoke();
        }

        #endregion
    }
}
