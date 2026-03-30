using System;
using System.Buffers.Binary;
using BoomNetwork.Core;
using BoomNetwork.Core.FrameSync;
using BoomNetwork.Core.Transport;
using BoomNetwork.Client.Transport;
using BoomNetwork.Client.Session;
using BoomNetwork.Client.Connection;
using BoomNetwork.Client.Room;

namespace BoomNetwork.Client.FrameSync
{
    /// <summary>
    /// 帧同步客户端 — 长生命周期，拥有完整网络能力
    ///
    /// 职责:
    ///   - 连接 + SessionBind
    ///   - 房间管理（创建/加入/离开/列表）
    ///   - 帧同步（帧收发、快照上传、初始快照）
    ///   - 断线自动重连（CompositeReconnectStrategy）
    ///   - 玩家状态推送（Join/Left/Offline/Online）
    ///
    /// 不重建: Connect/Reconnect 复用内部网络栈
    /// </summary>
    public class FrameSyncClient
    {
        public enum State
        {
            Disconnected,
            Connecting,
            Connected,     // SessionBind 完成，可操作房间
            InRoom,        // 已加入房间，等待开始
            Syncing,       // 帧同步进行中
            Reconnecting,  // 断线重连中
        }

        // --- 状态 ---
        public State CurrentState { get; private set; } = State.Disconnected;
        public int PlayerId { get; private set; }
        public int RoomId { get; private set; }
        public uint LastFrameNumber { get; private set; }

        /// <summary>最近心跳 RTT（毫秒），-1 = 未测量</summary>
        public float RttMs => _connMgr?.RttMs ?? -1;
        public FrameSyncInitData? InitData { get; private set; }
        public bool HasPreviousIdentity => PlayerId > 0 && RoomId > 0;

        /// <summary>服务器是否处于游戏级暂停（客户端请求的暂停，暂停期间不推帧）</summary>
        public bool IsGamePaused { get; private set; }

        // --- 事件 ---
        public event Action? OnConnected;
        public event Action<int, int[]>? OnJoinedRoom;       // roomId, existingPlayerIds
        public event Action? OnReady;                         // 首次加入 或 重连成功
        public event Action<FrameSyncInitData>? OnFrameSyncStart;
        public event Action<FrameData>? OnFrame;
        public event Action? OnFrameSyncStop;
        public event Action<int>? OnPlayerJoined;
        public event Action<int>? OnPlayerLeft;
        public event Action<int>? OnPlayerOffline;
        public event Action<int>? OnPlayerOnline;
        public event Action<int>? OnHostChanged;  // int = new host PlayerId
        public event Action? OnReconnected;
        public event Action? OnDisconnected;
        public event Action<int>? OnLeftRoom;                 // oldPlayerId

        // --- 轻量状态同步事件 ---
        /// <summary>收到其他玩家的状态消息 (playerId, data)</summary>
        public event Action<int, byte[]>? OnStateMessage;
        /// <summary>KV 数据增量变更 (version, playerId, key, value)。value 为空表示删除</summary>
        public event Action<uint, int, int, byte[]>? OnDataChanged;
        /// <summary>全量 KV 同步完成 (version, entries)</summary>
        public event Action<uint, DataEntry[]>? OnDataSynced;
        public event Action<NetworkError>? OnError;
        public event Action<string>? OnLog;

        /// <summary>服务器警告：消息速率接近上限，请降速发包</summary>
        public event Action? OnRateLimitWarning;

        /// <summary>服务器帧同步暂停（如快照过期）</summary>
        public event Action<FrameSyncPauseReason>? OnFrameSyncPaused;
        /// <summary>服务器帧同步恢复</summary>
        public event Action? OnFrameSyncResumed;

        /// <summary>帧 hash 不匹配（不同步检测）</summary>
        public event Action<FrameHashMismatch>? OnDesyncDetected;

        // --- 快照回调 ---
        public Func<byte[]?>? OnTakeSnapshot;
        public Action<byte[]>? OnLoadSnapshot;

        // --- 配置 ---
        public uint SnapshotInterval { get; set; } = 100;

        // --- 实体权威同步 ---
        /// <summary>远端实体状态到达 (senderPid, entityId, data, offset, length)</summary>
        public event Action<int, int, byte[], int, int>? OnEntityState;
        /// <summary>权威变更通知 (entityId, newOwnerPlayerId)。0 = unclaimed。</summary>
        public event Action<int, int>? OnAuthorityChanged;

        // --- 游戏自定义消息 ---
        /// <summary>收到游戏自定义消息 (gameCmd, senderPid, data, offset)</summary>
        public event Action<uint, int, byte[], int>? OnGameMessage;
        private readonly System.Collections.Generic.List<IEntitySync> _authorityEntities = new();
        private byte[]? _entityStateBuf;

        /// <summary>注册本地管理的实体（每帧自动发送其状态），幂等</summary>
        public void RegisterAuthorityEntity(IEntitySync entity)
        {
            // 幂等：已注册的 entityId 不重复添加
            for (int i = 0; i < _authorityEntities.Count; i++)
                if (_authorityEntities[i].EntityId == entity.EntityId)
                    return;

            _authorityEntities.Add(entity);
            if (_entityStateBuf == null || _entityStateBuf.Length < 1 + _authorityEntities.Count * (6 + 64))
                _entityStateBuf = new byte[1 + _authorityEntities.Count * (6 + 128)];
        }

        /// <summary>注销实体</summary>
        public void UnregisterAuthorityEntity(int entityId)
        {
            _authorityEntities.RemoveAll(e => e.EntityId == entityId);
        }

        /// <summary>请求获取 entityId 的权威（Extended Cmd AuthorityTransfer, release=0）</summary>
        public void RequestAuthorityTransfer(int entityId)
        {
            if (_session == null) return;
            _session.SendExt(FrameSyncExtCmd.AuthorityTransfer,
                AuthorityTransferCodec.EncodeRequest(entityId, false));
            Log($"RequestAuthorityTransfer entity={entityId}");
        }

        /// <summary>主动释放 entityId 的权威（Extended Cmd AuthorityTransfer, release=1）</summary>
        public void ReleaseAuthority(int entityId)
        {
            if (_session == null) return;
            _session.SendExt(FrameSyncExtCmd.AuthorityTransfer,
                AuthorityTransferCodec.EncodeRequest(entityId, true));
            Log($"ReleaseAuthority entity={entityId}");
        }

        /// <summary>发送游戏自定义消息（Game Cmd，服务器透传给同房间其他玩家）</summary>
        public void SendGameMessage(uint gameCmd, byte[] data)
        {
            if (_session == null) return;
            _session.SendGame(gameCmd, data);
        }

        // --- 轻量状态同步 API ---

        /// <summary>发送状态消息（服务器转发给同房其他玩家，不存储）</summary>
        public void SendStateMessage(byte[] data)
        {
            _session?.SendExt(FrameSyncExtCmd.SendStateMsg, data);
        }

        /// <summary>设置 KV 数据（服务器存储并增量广播）</summary>
        public void SetData(int key, byte[] value)
        {
            _session?.SendExt(FrameSyncExtCmd.SetData, StateSyncCodec.EncodeSetData(key, value));
        }

        /// <summary>删除 KV 数据</summary>
        public void DeleteData(int key)
        {
            _session?.SendExt(FrameSyncExtCmd.SetData, StateSyncCodec.EncodeDeleteData(key));
        }

        /// <summary>请求全量 KV 同步（版本间隙时自动调用，也可手动调用）</summary>
        public void RequestDataSync()
        {
            _session?.SendExt(FrameSyncExtCmd.RequestDataSync, Array.Empty<byte>());
        }

        // --- 内部网络栈（创建一次，不重建）---
        private ITransport? _transport;
        private NetworkSession? _session;
        private ConnectionManager? _connMgr;
        private RoomClient? _roomClient;

        private string _host = "";
        private int _port;
        private float _heartbeatIntervalMs;
        private float _heartbeatTimeoutMs;
        private bool _frameSyncStarted;
        private uint _lastSnapshotFrame;
        private Message? _pendingStartMsg;

        // --- 快照上传 ACK 重试 ---
        private int _snapshotRetryCount;
        private float _snapshotRetryTimer;  // 倒计时 ms，<=0 表示不在等待
        private uint _pendingSnapshotFrame;
        private byte[]? _pendingSnapshotData; // null = 无待重试快照
        private const int MaxSnapshotRetries = 3;
        private static readonly float[] SnapshotRetryDelays = { 1000f, 2000f, 4000f };
        private uint _dataSyncVersion;  // 轻量状态同步版本跟踪
        private readonly Func<ITransport>? _transportFactory;

        /// <summary>
        /// 创建帧同步客户端。
        /// transportFactory 可选：不传则自动选择（WebGL → WebGLWebSocketTransport，其他 → TcpClientTransport）。
        /// </summary>
        public FrameSyncClient(float heartbeatIntervalMs = 3000, float heartbeatTimeoutMs = 10000,
            Func<ITransport>? transportFactory = null)
        {
            _heartbeatIntervalMs = heartbeatIntervalMs;
            _heartbeatTimeoutMs = heartbeatTimeoutMs;
            _transportFactory = transportFactory;
        }

        // ===================== Lifecycle =====================

        /// <summary>
        /// 连接服务器。首次创建网络栈，后续复用
        /// </summary>
        public void Connect(string host, int port)
        {
            _host = host;
            _port = port;

            if (_transport == null)
                CreateNetworkStack();

            CurrentState = State.Connecting;
            _connMgr!.Connect(host, port);
            Log($"Connecting to {host}:{port}...");
        }

        /// <summary>
        /// 每帧调用
        /// </summary>
        public void Tick(float deltaTimeMs)
        {
            _connMgr?.Tick(deltaTimeMs);
            TickSnapshotRetry(deltaTimeMs);
        }

        private void TickSnapshotRetry(float deltaTimeMs)
        {
            if (_pendingSnapshotData == null || _snapshotRetryTimer <= 0) return;
            _snapshotRetryTimer -= deltaTimeMs;
            if (_snapshotRetryTimer <= 0)
                SendSnapshotWithRetry();
        }

        /// <summary>
        /// 主动断开（保留身份，可重连）
        /// </summary>
        public void Disconnect()
        {
            _connMgr?.Disconnect();
            DestroyNetworkStack();
            CurrentState = State.Disconnected;
        }

        /// <summary>
        /// 彻底断开（清除身份，不可重连）
        /// </summary>
        public void DisconnectAndClear()
        {
            Disconnect();
            PlayerId = 0;
            RoomId = 0;
            LastFrameNumber = 0;
            _frameSyncStarted = false;
            InitData = null;
        }

        /// <summary>
        /// 测试用：只断 TCP，保留身份，触发正常断线→重连流程
        /// </summary>
        /// <summary>
        /// 测试用：只断 TCP，保留身份，触发正常断线→重连流程
        /// </summary>
        public void SimulateNetworkDrop()
        {
            _transport?.Disconnect();
        }

        /// <summary>
        /// 测试用：断开连接并暂停自动重连。调用 ResumeReconnect 后恢复。
        /// </summary>
        public void SimulateNetworkDropAndPause()
        {
            _connMgr?.PauseReconnect();
            _transport?.Disconnect();
        }

        /// <summary>
        /// 恢复自动重连（配合 SimulateNetworkDropAndPause 使用）
        /// </summary>
        public void ResumeReconnect()
        {
            _connMgr?.ResumeReconnect();
        }

        // ===================== Room =====================

        public void GetRooms(Action<RoomInfo[]> onResult)
        {
            _roomClient?.GetRooms(onResult);
        }

        public void CreateRoom(int maxPlayers, Action<int>? onCreated = null)
        {
            _roomClient?.CreateRoom(maxPlayers, onCreated);
        }

        public void JoinRoom(int roomId)
        {
            if (CurrentState != State.Connected) return;

            _roomClient?.JoinRoom(roomId, (pid, rid, existingPlayers) =>
            {
                PlayerId = pid;
                RoomId = rid;
                _connMgr?.SetPlayerId(pid);
                CurrentState = State.InRoom;
                Log($"Joined room {rid} as Player {pid} (existing: [{string.Join(",", existingPlayers)}])");
                OnJoinedRoom?.Invoke(rid, existingPlayers);
                OnReady?.Invoke();
            });
        }

        public void CreateAndJoinRoom(int maxPlayers)
        {
            if (CurrentState != State.Connected) return;
            CreateRoom(maxPlayers, roomId =>
            {
                Log($"Room {roomId} created");
                JoinRoom(roomId);
            });
        }

        /// <summary>
        /// 匹配房间：有空位就加入，否则创建新房间（一步完成）
        /// </summary>
        /// <param name="matchKey">匹配 key，相同 key 才能匹配到一起（避免不同 demo 串房）</param>
        public void MatchRoom(int maxPlayers, string? matchKey = null)
        {
            if (CurrentState != State.Connected) return;

            _roomClient?.MatchRoom(maxPlayers, matchKey, (pid, rid, existingPlayers) =>
            {
                PlayerId = pid;
                RoomId = rid;
                _connMgr?.SetPlayerId(pid);
                CurrentState = State.InRoom;
                Log($"Matched room {rid} as Player {pid} (existing: [{string.Join(",", existingPlayers)}])");
                OnJoinedRoom?.Invoke(rid, existingPlayers);
                OnReady?.Invoke();
            });
        }

        public void LeaveRoom()
        {
            if (CurrentState != State.InRoom && CurrentState != State.Syncing) return;
            int oldPlayerId = PlayerId;
            _roomClient?.LeaveRoom(() =>
            {
                Log($"Left room {RoomId}");
                RoomId = 0;
                _frameSyncStarted = false;
                _authorityEntities.Clear();
                CurrentState = State.Connected;
                OnLeftRoom?.Invoke(oldPlayerId);
            });
        }

        // ===================== Frame Sync =====================

        /// <summary>
        /// 请求开始帧同步。自动取初始快照附带
        /// </summary>
        public void RequestStart()
        {
            if (CurrentState != State.InRoom && CurrentState != State.Connected) return;
            var snapshot = OnTakeSnapshot?.Invoke();
            _session?.Send(FrameSyncCmd.RequestStart, snapshot);
            Log($"Requested start (initial snapshot: {snapshot?.Length ?? 0} bytes)");
        }

        /// <summary>
        /// 请求停止帧同步（所有玩家回到 InRoom 状态）
        /// </summary>
        public void RequestStop()
        {
            if (CurrentState != State.Syncing) return;
            _session?.Send(FrameSyncCmd.StopFrameSync, null);
            Log("Requested stop");
        }

        public void SendInput(byte[] data, int dataLength = -1)
        {
            if (CurrentState != State.Syncing) return;
            int len = dataLength >= 0 ? dataLength : data.Length;
            _session?.Send(FrameSyncCmd.FrameInput, data, len);
            SendAuthorityEntityStates();
        }

        /// <summary>
        /// Manually send authority entity states without sending a frame input.
        /// Use this when the player's position changed but there is no game input to send.
        /// </summary>
        public void SendAuthorityEntityStates()
        {
            if (_authorityEntities.Count == 0 || _session == null) return;
            // 确保 buffer 够大
            int maxSize = 1 + _authorityEntities.Count * (6 + 128);
            if (_entityStateBuf == null || _entityStateBuf.Length < maxSize)
                _entityStateBuf = new byte[maxSize];
            int written = EntityStateCodec.Encode(
                _entityStateBuf, 0,
                _authorityEntities, _authorityEntities.Count);
            _session.SendExt(FrameSyncExtCmd.SendEntityState, _entityStateBuf, written);
        }

        /// <summary>
        /// Send frame state hash for desync detection.
        /// Call after each OnFrame with a hash of your game state.
        /// Server compares hashes from all clients; mismatch triggers OnDesyncDetected.
        /// </summary>
        public void SendFrameHash(uint frameNumber, uint hash)
        {
            if (CurrentState != State.Syncing || IsGamePaused) return;
            var buf = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(buf, frameNumber);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), hash);
            _session?.SendExt(FrameSyncExtCmd.FrameHash, buf);
        }

        /// <summary>请求服务器暂停帧同步（停推帧，零游戏流量）。暂停期间输入仍缓存，恢复后第一帧带上。
        /// 服务器侧幂等：已暂停时重复调用无副作用。</summary>
        public void RequestGamePause()
        {
            if (CurrentState != State.Syncing) return;
            _session?.SendExt(FrameSyncExtCmd.RequestGamePause, null);
        }

        /// <summary>请求服务器恢复帧同步。
        /// 服务器侧幂等：未暂停时重复调用无副作用。</summary>
        public void RequestGameResume()
        {
            if (CurrentState != State.Syncing) return;
            _session?.SendExt(FrameSyncExtCmd.RequestGameResume, null);
        }

        // ===================== Network Stack =====================

        private static ITransport CreateDefaultTransport()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return new WebGLWebSocketTransport();
#else
            return new TcpClientTransport();
#endif
        }

        private void CreateNetworkStack()
        {
            _transport = _transportFactory != null ? _transportFactory() : CreateDefaultTransport();
            _session = new NetworkSession(_transport);

            var reconnectStrategy = new CompositeReconnectStrategy(
                (new QuickReconnectStrategy { TimeoutMs = 5000 }, 3),
                (new SnapshotReconnectStrategy { TimeoutMs = 10000 }, 2)
            );

            _connMgr = new ConnectionManager(_session, reconnectStrategy);
            _connMgr.HeartbeatIntervalMs = _heartbeatIntervalMs;
            _connMgr.HeartbeatTimeoutMs = _heartbeatTimeoutMs;

            _connMgr.OnConnected += HandleConnected;
            _connMgr.OnDisconnected += HandleDisconnected;
            _connMgr.OnReconnected += HandleReconnected;
            _connMgr.OnError += err => OnError?.Invoke(err);
            _connMgr.OnLog += msg => Log(msg);

            _session.OnMessage += HandleMessage;

            _roomClient = new RoomClient(_session);
        }

        private void DestroyNetworkStack()
        {
            _transport = null;
            _session = null;
            _connMgr = null;
            _roomClient = null;
        }

        // ===================== ConnectionManager Callbacks =====================

        private void HandleConnected()
        {
            // SessionBind
            CurrentState = State.Connecting;

            _session!.SendAsync(FrameSyncCmd.SessionBind, null, 5000,
                onResponse: msg =>
                {
                    if (msg.DataLength >= 4)
                        PlayerId = (int)BinaryPrimitives.ReadUInt32LittleEndian(msg.DataSpan);

                    _connMgr!.SetPlayerId(PlayerId);
                    CurrentState = State.Connected;
                    Log($"Bound as Player {PlayerId}");
                    OnConnected?.Invoke();

                    if (_pendingStartMsg.HasValue)
                    {
                        HandleStartFrameSync(_pendingStartMsg.Value);
                        _pendingStartMsg = null;
                    }
                },
                onTimeout: err =>
                {
                    OnError?.Invoke(new NetworkError(ErrorCode.SessionBindTimeout, err.Message));
                    _connMgr?.Disconnect();
                });
        }

        private void HandleDisconnected()
        {
            _frameSyncStarted = false;
            IsGamePaused = false;
            _pendingSnapshotData = null;
            _snapshotRetryCount = 0;
            _snapshotRetryTimer = 0;
            CurrentState = State.Disconnected;
            Log("Disconnected");
            OnDisconnected?.Invoke();
        }

        private void HandleReconnected(ReconnectContext context)
        {
            _pendingSnapshotData = null;
            _snapshotRetryCount = 0;
            _snapshotRetryTimer = 0;

            if (context.IsSnapshotRestore && context.SnapshotData != null)
            {
                OnLoadSnapshot?.Invoke(context.SnapshotData);
                LastFrameNumber = context.SnapshotFrame;
                _lastSnapshotFrame = context.SnapshotFrame;
                Log($"Snapshot restored (frame {context.SnapshotFrame})");
            }
            else
            {
                LastFrameNumber = context.ServerFrameNumber;
            }

            // 恢复服务器配置（跨重连保留）
            if (InitData.HasValue && InitData.Value.SnapshotInterval > 0)
                SnapshotInterval = (uint)InitData.Value.SnapshotInterval;

            if (_frameSyncStarted || context.ServerFrameNumber > 0)
            {
                _frameSyncStarted = true;
                CurrentState = State.Syncing;
            }
            else
            {
                CurrentState = State.Connected;
            }

            Log($"Reconnected (serverFrame={context.ServerFrameNumber}, snapshot={context.IsSnapshotRestore})");
            OnReconnected?.Invoke();
            OnReady?.Invoke();
        }

        // ===================== Message Handling =====================

        private void HandleMessage(Message msg)
        {
            if (msg.MsgType == CmdType.Core)
            {
                switch (msg.Cmd)
                {
                    case FrameSyncCmd.StartFrameSync:
                        if (CurrentState < State.Connected)
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

                    case FrameSyncCmd.RateLimitWarning:
                        Log("Rate limit warning received from server");
                        OnRateLimitWarning?.Invoke();
                        break;
                }
            }
            else if (msg.MsgType == CmdType.Extended)
            {
                switch (msg.ExtCmd)
                {
                    case FrameSyncExtCmd.PlayerJoined:
                        if (msg.DataLength >= 4)
                            OnPlayerJoined?.Invoke(BinaryPrimitives.ReadInt32LittleEndian(msg.DataSpan));
                        break;

                    case FrameSyncExtCmd.PlayerLeft:
                        if (msg.DataLength >= 4)
                            OnPlayerLeft?.Invoke(BinaryPrimitives.ReadInt32LittleEndian(msg.DataSpan));
                        break;

                    case FrameSyncExtCmd.PlayerOffline:
                        if (msg.DataLength >= 4)
                            OnPlayerOffline?.Invoke(BinaryPrimitives.ReadInt32LittleEndian(msg.DataSpan));
                        break;

                    case FrameSyncExtCmd.PlayerOnline:
                        if (msg.DataLength >= 4)
                            OnPlayerOnline?.Invoke(BinaryPrimitives.ReadInt32LittleEndian(msg.DataSpan));
                        break;

                    case FrameSyncExtCmd.RoomSnapshot:
                        HandleRoomSnapshot(msg);
                        break;

                    case FrameSyncExtCmd.PushEntityState:
                        HandlePushEntityState(msg);
                        break;

                    case FrameSyncExtCmd.AuthorityTransfer:
                        HandleAuthorityTransferResult(msg);
                        break;

                    case FrameSyncExtCmd.PushStateMsg:
                        HandlePushStateMsg(msg);
                        break;
                    case FrameSyncExtCmd.PushData:
                        HandlePushData(msg);
                        break;
                    case FrameSyncExtCmd.PushDataSync:
                        HandlePushDataSync(msg);
                        break;

                    case FrameSyncExtCmd.FrameSyncPaused:
                    {
                        var reason = msg.DataLength >= 1
                            ? (FrameSyncPauseReason)msg.DataSpan[0]
                            : FrameSyncPauseReason.SnapshotStale;
                        Log($"FrameSync paused by server, reason={reason}");
                        if (reason == FrameSyncPauseReason.GamePause)
                            IsGamePaused = true;
                        OnFrameSyncPaused?.Invoke(reason);
                        break;
                    }
                    case FrameSyncExtCmd.FrameSyncResumed:
                        Log("FrameSync resumed by server");
                        IsGamePaused = false;
                        OnFrameSyncResumed?.Invoke();
                        break;

                    case FrameSyncExtCmd.FrameHashMismatch:
                        if (msg.DataLength >= 5)
                            OnDesyncDetected?.Invoke(FrameHashMismatch.Decode(msg.DataSpan));
                        break;
                }
            }
            else if (msg.MsgType == CmdType.Game)
            {
                HandleGameMessage(msg);
            }
        }

        private void HandleGameMessage(Message msg)
        {
            // senderPid is encoded as first 4 bytes of Data by the server when broadcasting
            if (msg.DataLength >= 4)
            {
                int senderPid = BinaryPrimitives.ReadInt32LittleEndian(msg.DataSpan);
                OnGameMessage?.Invoke(msg.GameCmd, senderPid, msg.Data, 4);
            }
            else
            {
                OnGameMessage?.Invoke(msg.GameCmd, 0, msg.Data, 0);
            }
        }

        private void HandleRoomSnapshot(Message msg)
        {
            if (msg.DataLength < 4) return;
            uint snapshotFrame = BinaryPrimitives.ReadUInt32LittleEndian(msg.DataSpan);
            byte[] snapshotData = msg.DataSpan.Slice(4).ToArray();
            if (snapshotData.Length > 0)
            {
                OnLoadSnapshot?.Invoke(snapshotData);
                LastFrameNumber = snapshotFrame;
                _lastSnapshotFrame = snapshotFrame;
            }
        }

        private void HandleStartFrameSync(Message msg)
        {
            if (msg.DataLength >= FrameSyncInitData.LegacySize)
                InitData = FrameSyncInitData.ReadFrom(msg.DataSpan);

            var init = InitData ?? default;
            if (init.SnapshotInterval > 0)
                SnapshotInterval = (uint)init.SnapshotInterval;
            if (init.QuickReconnectMaxMs > 0 && _connMgr != null)
                _connMgr.QuickReconnectMaxMs = init.QuickReconnectMaxMs;

            _frameSyncStarted = true;
            LastFrameNumber = 0;
            _lastSnapshotFrame = 0;
            CurrentState = State.Syncing;
            OnFrameSyncStart?.Invoke(init);
        }

        private void HandlePushFrames(Message msg)
        {
            if (!_frameSyncStarted || msg.DataLength == 0) return;
            var frame = FrameDataCodec.Decode(msg.DataSpan);

            LastFrameNumber = frame.FrameNumber;
            _connMgr?.UpdateFrameNumber(frame.FrameNumber);

            // Dispatch frame events BEFORE OnFrame (game layer sees events in the same frame)
            if (frame.Events != null)
            {
                for (int i = 0; i < frame.Events.Length; i++)
                {
                    ref var evt = ref frame.Events[i];
                    switch (evt.EventType)
                    {
                        case FrameEventType.PlayerJoined:
                            OnPlayerJoined?.Invoke(evt.PlayerId);
                            break;
                        case FrameEventType.PlayerLeft:
                            OnPlayerLeft?.Invoke(evt.PlayerId);
                            break;
                        case FrameEventType.PlayerOffline:
                            OnPlayerOffline?.Invoke(evt.PlayerId);
                            break;
                        case FrameEventType.PlayerOnline:
                            OnPlayerOnline?.Invoke(evt.PlayerId);
                            break;
                        case FrameEventType.HostChanged:
                            OnHostChanged?.Invoke(evt.PlayerId);
                            break;
                    }
                }
            }

            OnFrame?.Invoke(frame);

            CheckSnapshotUpload();
        }

        private void CheckSnapshotUpload()
        {
            if (SnapshotInterval == 0 || OnTakeSnapshot == null) return;
            if (_pendingSnapshotData != null) return; // 已有重试中的快照
            uint boundary = (LastFrameNumber / SnapshotInterval) * SnapshotInterval;
            if (boundary == 0 || boundary == _lastSnapshotFrame) return;

            var data = OnTakeSnapshot();
            if (data == null || data.Length == 0) return;

            _lastSnapshotFrame = boundary;
            _pendingSnapshotFrame = boundary;
            _pendingSnapshotData = SnapshotCodec.EncodeUploadSnapshot(LastFrameNumber, data);
            _snapshotRetryCount = 0;
            SendSnapshotWithRetry();
        }

        private void SendSnapshotWithRetry()
        {
            if (_session == null || _pendingSnapshotData == null) return;
            _snapshotRetryTimer = 0;
            _session.SendExtAsync(
                FrameSyncExtCmd.UploadSnapshot,
                _pendingSnapshotData,
                3000f,
                onResponse: msg =>
                {
                    bool accepted = msg.DataLength >= 1 && msg.DataSpan[0] == 1;
                    if (accepted)
                    {
                        _pendingSnapshotData = null;
                        _snapshotRetryCount = 0;
                    }
                    else
                    {
                        Log($"Snapshot upload rejected (frame={_pendingSnapshotFrame}), scheduling retry {_snapshotRetryCount + 1}/{MaxSnapshotRetries}");
                        ScheduleSnapshotRetry();
                    }
                },
                onTimeout: _ =>
                {
                    Log($"Snapshot upload timeout (frame={_pendingSnapshotFrame}), scheduling retry {_snapshotRetryCount + 1}/{MaxSnapshotRetries}");
                    ScheduleSnapshotRetry();
                });
        }

        private void ScheduleSnapshotRetry()
        {
            if (_snapshotRetryCount >= MaxSnapshotRetries)
            {
                Log($"Snapshot upload failed after {MaxSnapshotRetries} retries, giving up");
                _pendingSnapshotData = null;
                _snapshotRetryCount = 0;
                return;
            }
            _snapshotRetryTimer = SnapshotRetryDelays[_snapshotRetryCount];
            _snapshotRetryCount++;
        }

        private void HandleStopFrameSync()
        {
            _frameSyncStarted = false;
            IsGamePaused = false;
            CurrentState = State.InRoom;
            OnFrameSyncStop?.Invoke();
        }

        private void HandlePushEntityState(Message msg)
        {
            if (msg.DataLength < 5) return;
            EntityStateCodec.Decode(msg.DataSpan, (senderPid, entityId, data, offset, length) =>
            {
                OnEntityState?.Invoke(senderPid, entityId, data, offset, length);
            });
        }

        private void HandleAuthorityTransferResult(Message msg)
        {
            if (msg.DataLength < AuthorityTransferCodec.ResultSize) return;
            var (entityId, newOwnerPlayerId) = AuthorityTransferCodec.DecodeResult(msg.DataSpan);
            Log($"AuthorityTransferResult entity={entityId} newOwner={newOwnerPlayerId}");
            OnAuthorityChanged?.Invoke(entityId, newOwnerPlayerId);
        }

        // ===================== 轻量状态同步 Handler =====================

        private void HandlePushStateMsg(Message msg)
        {
            if (msg.DataLength < 4) return;
            var (playerId, data) = StateSyncCodec.DecodePushStateMsg(msg.DataSpan);
            OnStateMessage?.Invoke(playerId, data);
        }

        private void HandlePushData(Message msg)
        {
            if (msg.DataLength < 14) return;
            var (version, playerId, key, value) = StateSyncCodec.DecodePushData(msg.DataSpan);

            if (version == _dataSyncVersion + 1)
            {
                _dataSyncVersion = version;
                OnDataChanged?.Invoke(version, playerId, key, value);
            }
            else if (version > _dataSyncVersion + 1)
            {
                // 版本间隙 → 请求全量同步
                Log($"DataSync version gap: expected {_dataSyncVersion + 1}, got {version}, requesting full sync");
                RequestDataSync();
            }
            // else: 旧版本消息，忽略
        }

        private void HandlePushDataSync(Message msg)
        {
            if (msg.DataLength < 6) return;
            var (version, entries) = StateSyncCodec.DecodePushDataSync(msg.DataSpan);
            _dataSyncVersion = version;
            OnDataSynced?.Invoke(version, entries);
        }

        private void Log(string msg) => OnLog?.Invoke($"[FrameSyncClient] {msg}");
    }
}
