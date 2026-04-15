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

        /// <summary>
        /// 快速重连最长重试时间（ms），由服务器通过 StartFrameSync 下发
        /// 会传递给 QuickReconnectStrategy.TimeoutMs
        /// </summary>
        public int QuickReconnectMaxMs
        {
            get => _quickReconnectMaxMs;
            set
            {
                _quickReconnectMaxMs = value;
                // 传递给策略链中的 QuickReconnectStrategy
                if (_reconnectStrategy is CompositeReconnectStrategy composite)
                    composite.SetQuickReconnectTimeout(value);
            }
        }
        private int _quickReconnectMaxMs = 5000;

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
        /// 重连成功，携带策略返回的结果
        /// </summary>
        public event Action<ReconnectOutcome>? OnReconnected;

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
        private float _heartbeatSendTimer; // 单次心跳发送后的计时（用于 RTT）
        private bool _heartbeatActive;
        private bool _heartbeatWaitingRsp; // 是否在等待心跳回复
        private bool _intentionalDisconnect; // 主动断开标记，不触发重连
        private bool _reconnectPaused;       // 暂停自动重连（测试用）

        /// <summary>最近一次心跳 RTT（毫秒），-1 = 未测量</summary>
        public float RttMs { get; private set; } = -1;
        private int _playerId;
        private uint _lastFrameNumber;

        /// <summary>
        /// 重连期间持有的活状态，由 UpdateFrameNumber 每帧同步帧号，
        /// 策略每次 Attempt 时读取到的都是最新值。
        /// </summary>
        private ReconnectState? _activeState;

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
        /// 暂停自动重连（断开后不会尝试恢复，直到 ResumeReconnect）
        /// </summary>
        public void PauseReconnect()
        {
            _reconnectPaused = true;
        }

        /// <summary>
        /// 恢复自动重连。如果当前已断开，立即触发重连流程。
        /// </summary>
        public void ResumeReconnect()
        {
            if (!_reconnectPaused) return;
            _reconnectPaused = false;

            // 如果 pause 期间断了线，现在补触发重连
            if (CurrentState == State.Disconnected && !_intentionalDisconnect && _reconnectStrategy != null)
            {
                HandleSessionDisconnected();
            }
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
        /// 更新帧号（收帧时由上层调用）。
        /// 重连期间同步写入 _activeState.LastFrameNumber，保证下一次 Attempt
        /// 使用的是当前最新帧号，而非断线时刻的快照。
        /// </summary>
        public void UpdateFrameNumber(uint frameNumber)
        {
            _lastFrameNumber = frameNumber;
            if (_activeState != null)
                _activeState.LastFrameNumber = frameNumber;
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
                _heartbeatSendTimer = 0;
                _heartbeatWaitingRsp = true;
                _session.Send(FrameSyncCmd.Heartbeat);
            }

            if (_heartbeatWaitingRsp)
                _heartbeatSendTimer += deltaTimeMs;

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

            // 正在重连中再次断开 → 忽略，由当前策略处理
            // 必须在 _reconnectPaused 检查之前：策略内部触发的 Disconnect（如 SnapshotReconnect
            // 调用 session.Connect() → transport.Disconnect()）不能因短暂失焦导致重连被中断。
            Log($"[CM] DisconnectEvent state={CurrentState} paused={_reconnectPaused}");
            if (CurrentState == State.Reconnecting)
            {
                Log("Already reconnecting, ignoring disconnect");
                return;
            }

            // 重连暂停中 → 停在 Disconnected 状态，等 ResumeReconnect
            if (_reconnectPaused)
            {
                Log("Reconnect paused, waiting for resume");
                TransitionTo(State.Disconnected);
                return;
            }

            // 被动断线 → 触发重连
            TransitionTo(State.Reconnecting);

            _activeState = new ReconnectState
            {
                PlayerId        = _playerId,
                LastFrameNumber = _lastFrameNumber,
            };

            Log($"Starting reconnect (player={_playerId}, frame={_lastFrameNumber})");

            _reconnectStrategy.Attempt(_session, _host, _port, _activeState,
                onSuccess: outcome =>
                {
                    _activeState = null;
                    Log($"Reconnect success via {_reconnectStrategy.Name} (serverFrame={outcome.ServerFrameNumber})");
                    TransitionTo(State.Connected);
                    StartHeartbeat();
                    OnReconnected?.Invoke(outcome);
                },
                onFail: err =>
                {
                    _activeState = null;
                    Log($"[CM] ReconnectFailed code={err.Code} msg={err.Message}");
                    Log($"Reconnect failed: {err}");
                    OnError?.Invoke(new NetworkError(ErrorCode.AllStrategiesExhausted, err.Message));
                    // 标记主动断开，防止后续 TCP close 事件（如服务端 rate limit 杀连接）
                    // 绕过 "Already reconnecting" 保护再次触发 reconnect storm
                    _intentionalDisconnect = true;
                    TransitionTo(State.Disconnected);
                    OnDisconnected?.Invoke();
                });
        }

        private void HandleSessionMessage(Message msg)
        {
            if (msg.MsgType == CmdType.Core && msg.Cmd == FrameSyncCmd.HeartbeatRsp)
            {
                _heartbeatRspTimer = 0;
                if (_heartbeatWaitingRsp)
                {
                    RttMs = _heartbeatSendTimer;
                    _heartbeatWaitingRsp = false;
                }
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
