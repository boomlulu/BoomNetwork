using System;
using System.Buffers.Binary;
using BoomNetwork.Core;
using BoomNetwork.Core.FrameSync;
using BoomNetwork.Core.Prediction;
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
        public FrameSyncInitData? InitData { get; private set; }
        public bool HasPreviousIdentity => PlayerId > 0 && RoomId > 0;

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
        public event Action? OnReconnected;
        public event Action? OnDisconnected;
        public event Action<int>? OnLeftRoom;                 // oldPlayerId
        public event Action<NetworkError>? OnError;
        public event Action<string>? OnLog;

        // --- 快照回调 ---
        public Func<byte[]?>? OnTakeSnapshot;
        public Action<byte[]>? OnLoadSnapshot;

        // --- 配置 ---
        public uint SnapshotInterval { get; set; } = 100;
        public PredictionManager? Prediction { get; set; }

        // --- 实体权威同步 ---
        /// <summary>远端实体状态到达 (senderPid, entityId, data, offset, length)</summary>
        public event Action<int, int, byte[], int, int>? OnEntityState;
        private readonly System.Collections.Generic.List<IEntitySync> _authorityEntities = new();
        private byte[]? _entityStateBuf;

        /// <summary>注册本地管理的实体（每帧自动发送其状态）</summary>
        public void RegisterAuthorityEntity(IEntitySync entity)
        {
            _authorityEntities.Add(entity);
            if (_entityStateBuf == null || _entityStateBuf.Length < 1 + _authorityEntities.Count * (6 + 64))
                _entityStateBuf = new byte[1 + _authorityEntities.Count * (6 + 128)];
        }

        /// <summary>注销实体</summary>
        public void UnregisterAuthorityEntity(int entityId)
        {
            _authorityEntities.RemoveAll(e => e.EntityId == entityId);
        }

        // --- 内部网络栈（创建一次，不重建）---
        private TcpClientTransport? _transport;
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

        public FrameSyncClient(float heartbeatIntervalMs = 3000, float heartbeatTimeoutMs = 10000)
        {
            _heartbeatIntervalMs = heartbeatIntervalMs;
            _heartbeatTimeoutMs = heartbeatTimeoutMs;
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
            Prediction?.ProcessServerFrames();
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
        public void SimulateNetworkDrop()
        {
            _transport?.Disconnect();
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

        public void LeaveRoom()
        {
            if (CurrentState != State.InRoom && CurrentState != State.Syncing) return;
            int oldPlayerId = PlayerId;
            _roomClient?.LeaveRoom(() =>
            {
                Log($"Left room {RoomId}");
                RoomId = 0;
                _frameSyncStarted = false;
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

        public void SendInput(byte[] data, int dataLength = -1)
        {
            if (CurrentState != State.Syncing) return;
            int len = dataLength >= 0 ? dataLength : data.Length;
            _session?.Send(FrameSyncCmd.FrameInput, data, len);
            SendAuthorityEntityStates();
        }

        private void SendAuthorityEntityStates()
        {
            if (_authorityEntities.Count == 0 || _session == null) return;
            // 确保 buffer 够大
            int maxSize = 1 + _authorityEntities.Count * (6 + 128);
            if (_entityStateBuf == null || _entityStateBuf.Length < maxSize)
                _entityStateBuf = new byte[maxSize];
            int written = EntityStateCodec.Encode(
                _entityStateBuf, 0,
                _authorityEntities.ToArray(), _authorityEntities.Count);
            _session.Send(FrameSyncCmd.SendEntityState, _entityStateBuf, written);
        }

        public void PredictWithInput(float deltaTimeMs, byte[] localInput)
        {
            if (CurrentState != State.Syncing || Prediction == null) return;
            if (Prediction.UpdatePrediction(deltaTimeMs, localInput))
                SendInput(localInput);
        }

        // ===================== Network Stack =====================

        private void CreateNetworkStack()
        {
            _transport = new TcpClientTransport();
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
            CurrentState = State.Disconnected;
            Log("Disconnected");
            OnDisconnected?.Invoke();
        }

        private void HandleReconnected(ReconnectContext context)
        {
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
            switch (msg.Cmd)
            {
                case FrameSyncCmd.StartFrameSync:
                    if (CurrentState < State.Connected)
                        _pendingStartMsg = msg;
                    else
                        HandleStartFrameSync(msg);
                    break;

                case FrameSyncCmd.PlayerJoined:
                    if (msg.DataLength >= 4)
                        OnPlayerJoined?.Invoke(BinaryPrimitives.ReadInt32LittleEndian(msg.DataSpan));
                    break;

                case FrameSyncCmd.PlayerLeft:
                    if (msg.DataLength >= 4)
                        OnPlayerLeft?.Invoke(BinaryPrimitives.ReadInt32LittleEndian(msg.DataSpan));
                    break;

                case FrameSyncCmd.PlayerOffline:
                    if (msg.DataLength >= 4)
                        OnPlayerOffline?.Invoke(BinaryPrimitives.ReadInt32LittleEndian(msg.DataSpan));
                    break;

                case FrameSyncCmd.PlayerOnline:
                    if (msg.DataLength >= 4)
                        OnPlayerOnline?.Invoke(BinaryPrimitives.ReadInt32LittleEndian(msg.DataSpan));
                    break;

                case FrameSyncCmd.RoomSnapshot:
                    HandleRoomSnapshot(msg);
                    break;

                case FrameSyncCmd.PushEntityState:
                    HandlePushEntityState(msg);
                    break;

                case FrameSyncCmd.PushFrames:
                    HandlePushFrames(msg);
                    break;

                case FrameSyncCmd.StopFrameSync:
                    HandleStopFrameSync();
                    break;
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

            if (Prediction != null)
            {
                Prediction.OnServerFrame(frame);
                LastFrameNumber = Prediction.PredictedFrame;
                _connMgr?.UpdateFrameNumber(Prediction.ConfirmedFrame);
                OnFrame?.Invoke(frame);
            }
            else
            {
                LastFrameNumber = frame.FrameNumber;
                _connMgr?.UpdateFrameNumber(frame.FrameNumber);
                OnFrame?.Invoke(frame);
            }

            CheckSnapshotUpload();
        }

        private void CheckSnapshotUpload()
        {
            if (SnapshotInterval == 0 || OnTakeSnapshot == null) return;
            uint boundary = (LastFrameNumber / SnapshotInterval) * SnapshotInterval;
            if (boundary == 0 || boundary == _lastSnapshotFrame) return;

            var data = OnTakeSnapshot();
            if (data == null || data.Length == 0) return;

            _lastSnapshotFrame = boundary;
            var encoded = SnapshotCodec.EncodeUploadSnapshot(LastFrameNumber, data);
            _session?.Send(FrameSyncCmd.UploadSnapshot, encoded);
        }

        private void HandleStopFrameSync()
        {
            _frameSyncStarted = false;
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

        private void Log(string msg) => OnLog?.Invoke($"[FrameSyncClient] {msg}");
    }
}
