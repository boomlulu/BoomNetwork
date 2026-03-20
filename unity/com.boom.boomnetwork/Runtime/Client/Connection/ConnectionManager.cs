using System;
using BoomNetwork.Core;
using BoomNetwork.Core.FrameSync;
using BoomNetwork.Client.Session;

namespace BoomNetwork.Client.Connection
{
    /// <summary>
    /// 连接管理器
    ///
    /// 职责:
    ///   - 连接生命周期: Connect / Disconnect
    ///   - 心跳: 自动发送 + 超时检测（上层不感知）
    ///   - 重连编排: 断线 → 通过 IReconnectStrategy 尝试恢复
    ///   - 状态机: Disconnected → Connecting → Connected → Reconnecting
    ///
    /// 不管: 帧同步逻辑
    /// </summary>
    public class ConnectionManager
    {
        public enum State
        {
            Disconnected,
            Connecting,
            Connected,
            Reconnecting,
        }

        private readonly NetworkSession _session;
        private readonly IReconnectStrategy _reconnectStrategy;

        // --- 配置 ---
        public float HeartbeatIntervalMs { get; set; } = 3000;
        public float HeartbeatTimeoutMs { get; set; } = 10000;

        // --- 状态 ---
        public State CurrentState { get; private set; } = State.Disconnected;

        // --- 事件 ---
        /// <summary>
        /// 首次连接成功
        /// </summary>
        public event Action? OnConnected;

        /// <summary>
        /// 连接断开（所有重连策略都失败后）
        /// </summary>
        public event Action? OnDisconnected;

        /// <summary>
        /// 重连成功
        /// </summary>
        public event Action<ReconnectContext>? OnReconnected;

        /// <summary>
        /// 状态变化日志
        /// </summary>
        public event Action<NetworkError>? OnError;
        public event Action<string>? OnLog;

        // --- 内部 ---
        private string _host = "";
        private int _port;
        private float _heartbeatTimer;
        private float _heartbeatRspTimer;
        private bool _heartbeatActive;
        private bool _intentionalDisconnect; // 主动断开标记，不触发重连
        private int _playerId;
        private uint _lastFrameNumber;

        public NetworkSession Session => _session;

        public ConnectionManager(NetworkSession session, IReconnectStrategy? reconnectStrategy = null)
        {
            _session = session;
            _reconnectStrategy = reconnectStrategy;

            _session.OnConnected += HandleSessionConnected;
            _session.OnDisconnected += HandleSessionDisconnected;
            _session.OnMessage += HandleSessionMessage;
        }

        /// <summary>
        /// 连接服务器
        /// </summary>
        public void Connect(string host, int port)
        {
            _host = host;
            _port = port;
            _intentionalDisconnect = false;
            TransitionTo(State.Connecting);
            _session.Connect(host, port);
        }

        /// <summary>
        /// 主动断开（不触发重连）
        /// </summary>
        public void Disconnect()
        {
            _intentionalDisconnect = true;
            StopHeartbeat();
            _reconnectStrategy?.Cancel();
            _session.Disconnect();
            TransitionTo(State.Disconnected);
        }

        /// <summary>
        /// 每帧调用
        /// </summary>
        public void Tick(float deltaTimeMs)
        {
            _session.Tick(deltaTimeMs);
            TickHeartbeat(deltaTimeMs);
        }

        /// <summary>
        /// 设置 playerId（绑定成功后由上层调用）
        /// </summary>
        public void SetPlayerId(int playerId)
        {
            _playerId = playerId;
        }

        /// <summary>
        /// 更新帧号（收帧时由上层调用）
        /// </summary>
        public void UpdateFrameNumber(uint frameNumber)
        {
            _lastFrameNumber = frameNumber;
        }

        #region Heartbeat

        private void StartHeartbeat()
        {
            _heartbeatActive = true;
            _heartbeatTimer = 0;
            _heartbeatRspTimer = 0;
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
            _heartbeatRspTimer += deltaTimeMs;

            if (_heartbeatTimer >= HeartbeatIntervalMs)
            {
                _heartbeatTimer = 0;
                _session.Send(FrameSyncCmd.Heartbeat);
            }

            if (_heartbeatRspTimer >= HeartbeatTimeoutMs)
            {
                Log($"Heartbeat timeout ({HeartbeatTimeoutMs}ms)");
                StopHeartbeat();
                OnError?.Invoke(new NetworkError(ErrorCode.HeartbeatTimeout, $"No response for {HeartbeatTimeoutMs}ms"));
                _intentionalDisconnect = false;
                _session.Disconnect(); // 触发 HandleSessionDisconnected → 重连
            }
        }

        #endregion

        #region Session Callbacks

        private void HandleSessionConnected()
        {
            if (CurrentState == State.Connecting)
            {
                TransitionTo(State.Connected);
                StartHeartbeat();
                OnConnected?.Invoke();
            }
            // Reconnecting 状态的连接成功由 ReconnectStrategy 内部处理
        }

        private void HandleSessionDisconnected()
        {
            StopHeartbeat();

            if (_intentionalDisconnect)
            {
                TransitionTo(State.Disconnected);
                OnDisconnected?.Invoke();
                return;
            }

            // 没有重连策略 → 直接断开
            if (_reconnectStrategy == null)
            {
                Log("No reconnect strategy, staying disconnected");
                TransitionTo(State.Disconnected);
                OnDisconnected?.Invoke();
                return;
            }

            // 被动断线 → 触发重连
            TransitionTo(State.Reconnecting);

            var context = new ReconnectContext
            {
                PlayerId = _playerId,
                LastFrameNumber = _lastFrameNumber,
            };

            Log($"Starting reconnect (player={_playerId}, frame={_lastFrameNumber})");

            _reconnectStrategy.Attempt(_session, _host, _port, context,
                onSuccess: () =>
                {
                    Log($"Reconnect success via {_reconnectStrategy.Name} (serverFrame={context.ServerFrameNumber})");
                    TransitionTo(State.Connected);
                    StartHeartbeat();
                    OnReconnected?.Invoke(context);
                },
                onFail: err =>
                {
                    Log($"Reconnect failed: {err}");
                    OnError?.Invoke(new NetworkError(ErrorCode.AllStrategiesExhausted, err.Message));
                    TransitionTo(State.Disconnected);
                    OnDisconnected?.Invoke();
                });
        }

        private void HandleSessionMessage(Message msg)
        {
            if (msg.Cmd == FrameSyncCmd.HeartbeatRsp)
            {
                _heartbeatRspTimer = 0;
            }
        }

        #endregion

        private void TransitionTo(State newState)
        {
            if (CurrentState == newState) return;
            Log($"State: {CurrentState} → {newState}");
            CurrentState = newState;
        }

        private void Log(string msg)
        {
            OnLog?.Invoke($"[ConnectionManager] {msg}");
        }
    }
}
