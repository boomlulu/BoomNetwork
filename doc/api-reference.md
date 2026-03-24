# API Reference

> 本文档覆盖 **客户端 API**（Person / FrameSyncClient / ConnectionManager）。
> **服务器 Admin API**（/health /rooms /kick 等 9 个 GM 端点）见 → [gm-tools.md](gm-tools.md)

## Person（推荐入口）

`Person` 封装了完整的网络客户端身份，管理 连接→房间→帧同步→重连 全流程。

### 状态

```csharp
public enum PersonState { Idle, Connecting, Connected, InRoom, Syncing, Disconnected }
```

### 属性

| 属性 | 类型 | 说明 |
|------|------|------|
| `State` | `PersonState` | 当前状态 |
| `PlayerId` | `int` | 服务器分配的玩家 ID |
| `RoomId` | `int` | 当前房间 ID |
| `FrameNumber` | `uint` | 当前帧号 |
| `HasPreviousIdentity` | `bool` | 断线后是否保留了身份（可重连） |

### 方法

| 方法 | 说明 |
|------|------|
| `Connect(NetworkConfig config)` | 连接服务器。有身份时自动走重连流程 |
| `CreateAndJoinRoom(int maxPlayers)` | 创建房间并自动加入 |
| `JoinRoom(int roomId)` | 加入指定房间。回调中会触发 `OnRemotePlayerJoined` 通知已有玩家 |
| `LeaveRoom()` | 离开当前房间 |
| `RequestStart()` | 请求开始帧同步。自动携带初始快照 |
| `SendInput(byte[] data)` | 发送玩家输入（帧同步运行中） |
| `PredictWithInput(float deltaTimeMs, byte[] data)` | 预测模式：喂入输入并本地预测 |
| `Tick(float deltaTimeMs)` | **每帧调用**，驱动网络收发和心跳 |
| `SimulateNetworkDrop()` | 测试用：只断 TCP，保留身份，触发断线流程 |
| `Disconnect()` | 断开连接，保留身份（可重连） |
| `DisconnectAndClear()` | 断开连接，清除身份（不可重连） |
| `GetRooms(Action<RoomInfo[]>)` | 获取房间列表 |
| `CreateRoom(int maxPlayers, Action<int>)` | 创建房间，回调返回 roomId |
| `GetFrameSyncInitData()` | 获取服务器下发的帧同步配置 |
| `SetPrediction(PredictionManager)` | 设置预测管理器（启用预测模式） |
| `ClearPrediction()` | 关闭预测模式 |

### 事件

| 事件 | 签名 | 触发时机 |
|------|------|---------|
| `OnConnected` | `Action<Person>` | 首次连接成功（非重连） |
| `OnJoinedRoom` | `Action<Person>` | 加入房间成功 |
| `OnReady` | `Action<Person>` | 首次加入 或 重连成功（统一入口） |
| `OnFrameSyncStart` | `Action<Person, FrameSyncInitData>` | 帧同步开始 |
| `OnFrame` | `Action<Person, FrameData>` | 收到服务器帧 |
| `OnRemotePlayerJoined` | `Action<Person, int>` | 其他玩家加入房间（playerId） |
| `OnRemotePlayerLeft` | `Action<Person, int>` | 其他玩家离开房间（playerId） |
| `OnRemotePlayerOffline` | `Action<Person, int>` | 其他玩家临时掉线（playerId） |
| `OnRemotePlayerOnline` | `Action<Person, int>` | 其他玩家恢复在线（playerId） |
| `OnReconnected` | `Action<Person>` | 重连成功 |
| `OnDisconnected` | `Action<Person>` | 断开连接 |
| `OnLeftRoom` | `Action<Person, int>` | 离开房间（oldPlayerId） |
| `OnLog` | `Action<Person, string>` | 内部日志 |

### 回调

| 回调 | 类型 | 说明 |
|------|------|------|
| `TakeSnapshot` | `Func<byte[]>` | 游戏层实现：序列化当前状态。自动按服务器配置的间隔调用 |
| `LoadSnapshot` | `Action<byte[]>` | 游戏层实现：从快照恢复状态。重连时调用 |

---

## FrameSyncClient（核心组件）

长生命周期的帧同步客户端，拥有完整网络栈。一次创建、全程复用，不因断线重建。

> 直接使用或通过 Person 薄适配器使用。

### 构造函数

```csharp
// 内部自动创建 Transport + Session + ConnectionManager + CompositeReconnectStrategy + RoomClient
var client = new FrameSyncClient(heartbeatIntervalMs: 3000, heartbeatTimeoutMs: 10000);
```

### 状态

```csharp
public enum State { Disconnected, Connecting, Connected, InRoom, Syncing, Reconnecting }
```

### 属性

| 属性 | 类型 | 说明 |
|------|------|------|
| `CurrentState` | `State` | 当前状态 |
| `PlayerId` | `int` | SessionBind 分配的玩家 ID |
| `RoomId` | `int` | 当前房间 ID |
| `InitData` | `FrameSyncInitData?` | 服务器下发的帧同步配置 |
| `LastFrameNumber` | `uint` | 最后处理的帧号 |
| `SnapshotInterval` | `uint` | 快照上传间隔（帧数），由服务器下发覆盖 |
| `HasPreviousIdentity` | `bool` | 断线后是否保留身份（PlayerId > 0 且 RoomId > 0） |
| `Prediction` | `PredictionManager?` | 预测管理器（null=传统模式） |

### 方法

| 方法 | 说明 |
|------|------|
| **生命周期** | |
| `Connect(string host, int port)` | 连接服务器（首次创建网络栈，后续复用） |
| `Tick(float deltaTimeMs)` | **每帧调用**，驱动网络收发和心跳 |
| `Disconnect()` | 断开连接，保留身份（可重连） |
| `DisconnectAndClear()` | 断开连接，清除身份（不可重连） |
| `SimulateNetworkDrop()` | 测试用：只断 TCP，触发正常断线→重连 |
| **房间** | |
| `GetRooms(Action<RoomInfo[]>)` | 获取房间列表 |
| `CreateRoom(int maxPlayers, Action<int>?)` | 创建房间 |
| `JoinRoom(int roomId)` | 加入指定房间 |
| `CreateAndJoinRoom(int maxPlayers)` | 创建并加入（二合一） |
| `LeaveRoom()` | 离开当前房间 |
| **帧同步** | |
| `RequestStart()` | 请求开始帧同步（自动携带初始快照） |
| `SendInput(byte[] data, int dataLength)` | 发送玩家输入 |
| `PredictWithInput(float deltaTimeMs, byte[] localInput)` | 预测模式每帧调用 |

### 事件

| 事件 | 签名 | 触发时机 |
|------|------|---------|
| `OnConnected` | `Action` | SessionBind 成功 |
| `OnJoinedRoom` | `Action<int, int[]>` | 加入房间（roomId, existingPlayerIds） |
| `OnReady` | `Action` | 首次加入 或 重连成功（统一入口） |
| `OnFrameSyncStart` | `Action<FrameSyncInitData>` | 帧同步开始 |
| `OnFrame` | `Action<FrameData>` | 收到服务器帧 |
| `OnFrameSyncStop` | `Action` | 帧同步结束 |
| `OnPlayerJoined` | `Action<int>` | 其他玩家加入 |
| `OnPlayerLeft` | `Action<int>` | 其他玩家离开 |
| `OnPlayerOffline` | `Action<int>` | 其他玩家临时掉线 |
| `OnPlayerOnline` | `Action<int>` | 其他玩家恢复在线 |
| `OnReconnected` | `Action` | 断线自动重连成功 |
| `OnDisconnected` | `Action` | 连接断开（所有策略耗尽） |
| `OnLeftRoom` | `Action<int>` | 离开房间（oldPlayerId） |
| `OnError` | `Action<NetworkError>` | 错误 |
| `OnLog` | `Action<string>` | 内部日志（含 ConnectionManager） |

### 快照回调

| 回调 | 类型 | 说明 |
|------|------|------|
| `OnTakeSnapshot` | `Func<byte[]?>` | 序列化当前状态。自动按服务器配置的间隔调用 |
| `OnLoadSnapshot` | `Action<byte[]>` | 恢复状态。重连 / 迟到加入时调用 |

---

## ConnectionManager（底层组件）

连接生命周期、心跳、重连编排。

### 属性

| 属性 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `HeartbeatIntervalMs` | `float` | `3000` | 心跳发送间隔 |
| `HeartbeatTimeoutMs` | `float` | `10000` | 心跳超时时间 |
| `QuickReconnectMaxMs` | `int` | `5000` | 快速重连超时（服务器下发） |
| `CurrentState` | `State` | `Disconnected` | 当前连接状态 |

### 方法

| 方法 | 说明 |
|------|------|
| `Connect(string host, int port)` | 连接 |
| `Disconnect()` | 主动断开（不触发重连） |
| `Tick(float deltaTimeMs)` | 每帧调用 |
| `SetPlayerId(int playerId)` | 设置玩家 ID |
| `UpdateFrameNumber(uint frameNumber)` | 更新帧号（重连用） |

---

## 数据结构

### FrameSyncInitData

```csharp
// 服务器在 StartFrameSync 中下发
struct FrameSyncInitData
{
    int FrameRate;               // 帧率（如 20）
    int FrameInterval;           // 帧间隔 ms（如 50）
    long StartTime;              // 服务器开始时间戳 ms
    int SnapshotInterval;        // 快照间隔（帧数）
    int QuickReconnectMaxMs;     // 快速重连超时 ms
}
```

### FrameData

```csharp
struct FrameData
{
    uint FrameNumber;            // 帧号（从 1 递增）
    PlayerInput[] Inputs;        // 本帧所有玩家输入
}

struct PlayerInput
{
    int PlayerId;                // 玩家 ID
    byte[] Data;                 // 输入数据（游戏层定义格式）
    int DataLength;              // 有效数据长度
    ReadOnlySpan<byte> DataSpan; // 零拷贝访问
}
```

### RoomInfo

```csharp
struct RoomInfo
{
    int RoomId;
    int PlayerCount;
    int MaxPlayers;
    bool Running;                // 帧同步是否已开始
}
```

### ReconnectResult

```csharp
static class ReconnectResult
{
    const byte Fail = 0;         // 通用失败
    const byte Success = 1;      // 成功
    const byte BufferStale = 2;  // 帧缓冲区过期，需降级到快照重连
}
```

### ISnapshotable

```csharp
// 游戏层可选实现（Person 用 TakeSnapshot/LoadSnapshot 回调替代）
interface ISnapshotable
{
    byte[] TakeSnapshot();       // 序列化当前状态
    void LoadSnapshot(byte[] data); // 恢复状态
}
```

---

## 协议命令表

| Cmd | 名称 | 方向 | 说明 |
|-----|------|------|------|
| 1 | SessionBind | C→S | 客户端绑定 |
| 2 | SessionBindRsp | S→C | 返回 playerId |
| 3 | RequestStart | C→S | 请求开始帧同步（可携带初始快照） |
| 4 | StartFrameSync | S→C | 帧同步开始（广播，携带 InitData） |
| 5 | FrameInput | C→S | 玩家输入 |
| 6 | PushFrames | S→C | 推送帧数据 |
| 7 | Heartbeat | C→S | 心跳 |
| 8 | HeartbeatRsp | S→C | 心跳响应 |
| 9 | Reconnect | C→S | 重连请求 |
| 10 | ReconnectRsp | S→C | 重连响应（含快照） |
| 11 | GetRooms | C→S | 获取房间列表 |
| 12 | GetRoomsRsp | S→C | 房间列表 |
| 13 | CreateRoom | C→S | 创建房间 |
| 14 | CreateRoomRsp | S→C | 返回 roomId |
| 15 | JoinRoom | C→S | 加入房间 |
| 16 | JoinRoomRsp | S→C | 返回 playerId + roomId + existingPlayers |
| 17 | LeaveRoom | C→S | 离开房间 |
| 18 | LeaveRoomRsp | S→C | 确认 |
| 19 | PlayerJoined | S→C | 推送：玩家加入 |
| 20 | PlayerLeft | S→C | 推送：玩家离开 |
| 21 | StopFrameSync | S→C | 帧同步结束 |
| 22 | UploadSnapshot | C→S | 上传快照 |
| 24 | PlayerOffline | S→C | 推送：玩家临时掉线 |
| 25 | PlayerOnline | S→C | 推送：玩家恢复在线 |
| 26 | RoomSnapshot | S→C | 推送：房间快照（迟到者加入） |
| 23 | UploadSnapshotRsp | S→C | 上传确认 |
