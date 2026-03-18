using System;
using System.Buffers.Binary;
using BoomNetwork.Core;
using BoomNetwork.Core.FrameSync;
using BoomNetwork.Client.Session;

namespace BoomNetwork.Client.FrameSync
{
    /// <summary>
    /// 帧同步客户端
    ///
    /// 职责: 连接 / SessionBind / 心跳 / 帧收发 / 断线重连
    /// 每帧调用 Tick(dt) 驱动。
    /// </summary>
    public class FrameSyncClient
    {
        private readonly NetworkSession _session;

        public enum State
        {
            Disconnected,
            Connecting,
            Binding,
            WaitingStart,
            Syncing,
            Stopped,
            Reconnecting,
        }

        // --- 配置 ---
        public float HeartbeatIntervalMs { get; set; } = 3000;   // 心跳发送间隔
        public float HeartbeatTimeoutMs { get; set; } = 10000;   // 心跳超时判定断线
        public int MaxReconnectAttempts { get; set; } = 5;       // 最大重连次数
        public float ReconnectIntervalMs { get; set; } = 2000;   // 重连间隔

        // --- 状态 ---
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
        public event Action<string>? OnError;

        // --- 内部状态 ---
        private int _playerId;
        private bool _frameSyncStarted;
        private Message? _pendingStartMsg;

        // 心跳
        private float _heartbeatTimer;        // 距上次发心跳的时间
        private float _lastHeartbeatRspTime;  // 距上次收到心跳响应的时间
        private bool _heartbeatActive;

        // 重连
        private string _lastHost = "";
        private int _lastPort;
        private int _reconnectAttempts;
        private float _reconnectTimer;

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
            _lastHost = host;
            _lastPort = port;
            _reconnectAttempts = 0;
            CurrentState = State.Connecting;
            _session.OnConnected += OnFirstConnected;
            _session.Connect(host, port);
        }

        /// <summary>
        /// 每帧调用
        /// </summary>
        public void Tick(float deltaTimeMs)
        {
            _session.Tick(deltaTimeMs);
            TickHeartbeat(deltaTimeMs);
            TickReconnect(deltaTimeMs);
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
        /// 主动断开（不触发重连）
        /// </summary>
        public void Disconnect()
        {
            StopHeartbeat();
            _session.Disconnect();
            CurrentState = State.Disconnected;
        }

        #region Heartbeat

        private void StartHeartbeat()
        {
            _heartbeatActive = true;
            _heartbeatTimer = 0;
            _lastHeartbeatRspTime = 0;
        }

        private void StopHeartbeat()
        {
            _heartbeatActive = false;
        }

        private void TickHeartbeat(float deltaTimeMs)
        {
            if (!_heartbeatActive)
                return;

            _heartbeatTimer += deltaTimeMs;
            _lastHeartbeatRspTime += deltaTimeMs;

            // 发送心跳
            if (_heartbeatTimer >= HeartbeatIntervalMs)
            {
                _heartbeatTimer = 0;
                _session.Send(FrameSyncCmd.Heartbeat);
            }

            // 超时检测
            if (_lastHeartbeatRspTime >= HeartbeatTimeoutMs)
            {
                StopHeartbeat();
                OnError?.Invoke($"Heartbeat timeout ({HeartbeatTimeoutMs}ms)");
                _session.Disconnect(); // 触发 HandleDisconnected → 自动重连
            }
        }

        private void HandleHeartbeatRsp()
        {
            _lastHeartbeatRspTime = 0;
        }

        #endregion

        #region Reconnect

        private void TickReconnect(float deltaTimeMs)
        {
            if (CurrentState != State.Reconnecting)
                return;

            _reconnectTimer += deltaTimeMs;
            if (_reconnectTimer >= ReconnectIntervalMs)
            {
                _reconnectTimer = 0;
                AttemptReconnect();
            }
        }

        private void StartReconnect()
        {
            if (string.IsNullOrEmpty(_lastHost) || _playerId == 0)
            {
                // 没连接过或没绑定过，不重连
                CurrentState = State.Disconnected;
                OnDisconnected?.Invoke();
                return;
            }

            if (_reconnectAttempts >= MaxReconnectAttempts)
            {
                CurrentState = State.Disconnected;
                OnError?.Invoke($"Reconnect failed after {MaxReconnectAttempts} attempts");
                OnDisconnected?.Invoke();
                return;
            }

            CurrentState = State.Reconnecting;
            _reconnectTimer = ReconnectIntervalMs; // 立刻触发第一次
        }

        private void AttemptReconnect()
        {
            _reconnectAttempts++;
            OnError?.Invoke($"Reconnecting... attempt {_reconnectAttempts}/{MaxReconnectAttempts}");

            _session.OnConnected += OnReconnectConnected;
            _session.Connect(_lastHost, _lastPort);
        }

        private void OnReconnectConnected()
        {
            _session.OnConnected -= OnReconnectConnected;

            // 发送重连请求（携带 playerId）
            var data = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(data, _playerId);

            _session.SendAsync(FrameSyncCmd.Reconnect, data, 5000,
                onResponse: msg =>
                {
                    // 重连成功，服务器返回当前帧号
                    if (msg.DataLength >= 4)
                    {
                        uint serverFrame = BinaryPrimitives.ReadUInt32LittleEndian(msg.DataSpan);
                        LastFrameNumber = serverFrame;
                    }

                    _reconnectAttempts = 0;
                    if (_frameSyncStarted)
                    {
                        CurrentState = State.Syncing;
                    }
                    else
                    {
                        CurrentState = State.WaitingStart;
                    }

                    StartHeartbeat();
                    OnReconnected?.Invoke();
                },
                onTimeout: err =>
                {
                    OnError?.Invoke($"Reconnect bind timeout: {err}");
                    _session.Disconnect();
                    // HandleDisconnected 会再次触发 StartReconnect
                });
        }

        #endregion

        #region Connection Callbacks

        private void OnFirstConnected()
        {
            _session.OnConnected -= OnFirstConnected;
            CurrentState = State.Binding;

            _session.SendAsync(FrameSyncCmd.SessionBind, null, 5000,
                onResponse: msg =>
                {
                    if (msg.DataLength >= 4)
                    {
                        _playerId = (int)BinaryPrimitives.ReadUInt32LittleEndian(msg.DataSpan);
                    }
                    CurrentState = State.WaitingStart;
                    _reconnectAttempts = 0;
                    StartHeartbeat();
                    OnBound?.Invoke(_playerId);

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

        private void HandleDisconnected()
        {
            StopHeartbeat();
            bool wasSyncing = _frameSyncStarted;

            if (CurrentState == State.Disconnected)
                return; // 主动断开，不重连

            if (CurrentState == State.Reconnecting)
            {
                // 重连中又断了，继续重连循环
                return;
            }

            // 被动断线 → 触发重连
            StartReconnect();
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

                case FrameSyncCmd.HeartbeatRsp:
                    HandleHeartbeatRsp();
                    break;
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

        #endregion
    }
}
