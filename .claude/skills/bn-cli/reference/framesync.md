# 帧同步门面 — FrameSyncClient

**文件**: `cli/Client/FrameSync/FrameSyncClient.cs`

## 定位

顶层门面（Facade），组合所有子系统，提供统一 API。应用层只需操作此类。

## 状态机

```
Disconnected ─── Connect() ──→ Connecting
                                    │
                              SessionBind + Rsp
                                    ↓
                               Connected ←── LeaveRoom
                                    │
                              MatchRoom / JoinRoom / CreateAndJoinRoom
                                    ↓
                                 InRoom ←── 重连恢复
                                    │
                              收到 StartFrameSync
                                    ↓
                                Syncing
                                    │
                              StopFrameSync / Disconnect
                                    ↓
                              Disconnected
```

重连时 `Reconnecting` 是 `ConnectionManager` 的子状态，`FrameSyncClient` 保持 InRoom/Syncing。

## 内部组合

```csharp
FrameSyncClient 构建时创建（第一次 Connect 时初始化）:
  TcpClientTransport → NetworkSession → ConnectionManager(CompositeReconnectStrategy)
                                       → RoomClient
```

**长生命周期**: 网络栈在 `Connect()` 时创建，跨 Reconnect 复用（不重建）。

## 完整 API 索引

### 生命周期

```csharp
void Connect(string host, int port);
void Tick(float deltaTime);              // 必须每帧调用
void Disconnect();
void DisconnectAndClear();               // 断开 + 清除所有状态
```

### 房间

```csharp
void GetRooms(Action<RoomInfo[]> onResult);
void CreateRoom(int max, Action<int>? onCreated);
void JoinRoom(int roomId);
void CreateAndJoinRoom(int maxPlayers);
void MatchRoom(int maxPlayers, string? matchKey = null);
void LeaveRoom();
```

### 帧同步

```csharp
void RequestStart(byte[]? snapshot = null);     // 附初始快照
void RequestStop();
void SendInput(byte[] data);                     // 发送帧输入（同时自动发送实体状态）
```

### 实体权威同步

```csharp
void RegisterAuthorityEntity(IEntitySync entity);
void UnregisterAuthorityEntity(int entityId);
void RequestAuthorityTransfer(int entityId);
void ReleaseAuthority(int entityId);
```

### 轻量状态同步

```csharp
void SendStateMessage(byte[] data);
void SetData(string key, byte[] value);
void DeleteData(string key);
void RequestDataSync();
```

### 自定义游戏消息

```csharp
void SendGameMessage(uint gameCmd, byte[]? data = null);
```

### 快照

```csharp
Func<byte[]?>? OnTakeSnapshot;       // 上层提供快照采集
Action<byte[]>? OnLoadSnapshot;      // 上层提供快照恢复
```

### 配置

```csharp
int HeartbeatIntervalMs { get; set; }    // 默认 3000
int HeartbeatTimeoutMs { get; set; }     // 默认 10000
int RttMs { get; }                        // 最近 RTT
```

### 调试

```csharp
void SimulateNetworkDrop();               // 模拟断线
void SimulateNetworkDropAndPause();       // 模拟断线 + 暂停重连
void ResumeReconnect();                   // 恢复重连
```

## 事件回调

| 事件 | 签名 | 触发时机 |
|------|------|----------|
| OnConnected | `Action` | SessionBind 成功 |
| OnJoinedRoom | `Action<int, int, int[]>` | 加入房间 (pid, roomId, existingPids) |
| OnReady | `Action` | 房间就绪（可 RequestStart） |
| OnFrameSyncStart | `Action<FrameSyncInitData>` | 帧同步开始 |
| OnFrame | `Action<FrameData>` | 收到帧数据 |
| OnFrameSyncStop | `Action` | 帧同步停止 |
| OnPlayerJoined | `Action<int>` | 新玩家加入 |
| OnPlayerLeft | `Action<int>` | 玩家离开 |
| OnPlayerOffline | `Action<int>` | 玩家掉线 |
| OnPlayerOnline | `Action<int>` | 玩家恢复 |
| OnReconnected | `Action<ReconnectContext>` | 重连成功 |
| OnDisconnected | `Action` | 断线 |
| OnLeftRoom | `Action` | 离开房间 |
| OnStateMessage | `Action<int, byte[]>` | 状态消息 (pid, data) |
| OnDataChanged | `Action<DataEntry[]>` | KV 数据变更 |
| OnDataSynced | `Action<DataEntry[]>` | KV 全量同步 |
| OnEntityState | `Action<int, byte[], int>` | 实体状态 (senderPid, data, len) |
| OnAuthorityChanged | `Action<int, int>` | 权威变更 (entityId, newOwner) |
| OnGameMessage | `Action<uint, byte[], int>` | 自定义消息 (gameCmd, data, len) |
| OnError | `Action<NetworkError>` | 错误 |
| OnLog | `Action<string>` | 日志 |

## 快照协议

### SnapshotCodec

```csharp
static class SnapshotCodec {
    // 上传快照 (C→S, ExtCmd 30)
    static byte[] EncodeUploadSnapshot(uint frameNumber, byte[] data);
    // Wire: [FrameNumber:4][Data:N]

    // 重连响应中的快照 (S→C, Cmd 11)
    static (uint frame, byte[] data) DecodeReconnectRsp(ReadOnlySpan<byte> buf);
}
```

### 快照流程

```
定时上传:
  帧同步运行时，每 SnapshotInterval 帧 → OnTakeSnapshot() → EncodeUploadSnapshot → 服务器缓存

快照重连:
  SnapshotReconnectStrategy → 服务器返回最新快照 → OnLoadSnapshot() → 恢复状态
```
