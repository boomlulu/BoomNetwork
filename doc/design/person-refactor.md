# Person 与 FrameSyncClient 职责边界重构设计

> **状态：已实施（2026-03-24）**
>
> 本文档为历史设计提案。方案已全部落地：
> - FrameSyncClient 重构为长生命周期，拥有完整网络栈 → `cli/Client/FrameSync/FrameSyncClient.cs`
> - Person 瘦身为纯适配器（~80 行）→ `BoomNetworkUnity/Assets/Scripts/Demo/Network/Person.cs`
> - 集成测试 7/7 通过，FrameSync 15/15
>
> 如需了解当前 API，请查阅 [api-reference.md](../client/01-api-reference.md)

---

## 现状问题（重构前）

### 1. 每次连接重建整个网络栈

```csharp
// Person.Connect() — 每次都 new 全套
_transport = new TcpClientTransport();
_session = new NetworkSession(_transport);
_connMgr = new ConnectionManager(_session, reconnectStrategy: null);  // ← 绕过库的重连
_roomClient = new RoomClient(_session);
_frameSync = new FrameSyncClient(_session, _connMgr, ...);
```

**后果**：
- 所有状态丢失（InitData、SnapshotInterval、LastFrameNumber）
- 需要 `_cachedInitData`、`_savedFrameNumber` 等字段手动保存/恢复
- 事件重复注册（每次 Connect 都 += 一遍）

### 2. 绕过库的重连机制

Person 传 `reconnectStrategy: null`，自己在 `HandleConnected` 里发 Reconnect 消息、解析 ReconnectRsp、处理 BufferStale 降级。这些逻辑和 `CompositeReconnectStrategy` 高度重叠。

```
库的重连链路（未使用）:
  ConnectionManager → CompositeReconnectStrategy → QuickReconnect → SnapshotReconnect

Person 的重连链路（自己实现）:
  HandleConnected → SendAsync(Reconnect) → HandleReconnectResponse → BufferStale → 降级重试
```

**后果**：两套重连逻辑，维护成本 ×2，bug 修一处漏一处。

### 3. 快照在两层都处理

```
FrameSyncClient 层:
  - CheckSnapshotUpload → OnTakeSnapshot → 上传
  - HandleRoomSnapshot → OnLoadSnapshot → 迟到者加入
  - OnConnectionReconnected → OnLoadSnapshot → CompositeStrategy 路径

Person 层:
  - HandleReconnectResponse → _frameSync.OnLoadSnapshot?.Invoke() → 自己的重连路径
  - TakeSnapshot/LoadSnapshot 桥接到 FrameSyncClient
```

Person 直接调用 `_frameSync.OnLoadSnapshot` 是因为它绕过了库的重连，需要自己触发加载。

### 4. 状态机重复

```
Person:        Idle → Connecting → Connected → InRoom → Syncing → Disconnected
FrameSyncClient: Disconnected → Binding → WaitingStart → Syncing → Stopped
```

两套状态部分重叠，外部使用时要看哪个？Person.State 还是 FrameSyncClient.CurrentState？

---

## 重构方案

### 核心原则

```
FrameSyncClient = 长生命周期的网络客户端，拥有完整协议能力
Person = 薄游戏层适配器，只关心业务回调
```

### 新架构

```
Person (游戏层 — 薄包装)
  ├─ 持有 ONE FrameSyncClient（创建一次，直到彻底销毁）
  ├─ 业务回调: TakeSnapshot, LoadSnapshot, OnFrame 处理
  ├─ 不直接操作 Session/Transport/ConnectionManager
  └─ 状态由 FrameSyncClient 驱动，不维护自己的状态机

FrameSyncClient (库层 — 全能力)
  ├─ 拥有: Transport, Session, ConnectionManager, RoomClient
  ├─ 生命周期: Connect → Bind → JoinRoom → Syncing → Disconnect
  │            └─ 断线 → 自动重连 (CompositeReconnectStrategy)
  ├─ 重连内置: 快速重连 / 快照重连 / BufferStale 降级
  ├─ 快照内置: 定时上传 + 重连加载 + 迟到者加载
  └─ 配置保持: InitData / SnapshotInterval 跨重连保留
```

### 关键变更

#### 1. FrameSyncClient 不再每次重建

```csharp
// 之前: Person.Connect() 每次 new
_frameSync = new FrameSyncClient(...);

// 之后: Person 只创建一次
if (_client == null)
    _client = new FrameSyncClient(config);
_client.Connect();       // 首次连接
_client.Reconnect();     // 断线后重连（内部复用，不重建）
```

#### 2. FrameSyncClient 吸收房间管理

```csharp
// 之前: Person 操作 RoomClient
_roomClient.JoinRoom(roomId, callback);

// 之后: FrameSyncClient 统一暴露
_client.JoinRoom(roomId);
_client.CreateAndJoinRoom(maxPlayers);
_client.LeaveRoom();
_client.RequestStart(snapshot);
```

#### 3. 重连使用库内置策略

```csharp
// 之前: Person(reconnectStrategy: null) + 自己发 Reconnect
// 之后: FrameSyncClient 内置 CompositeReconnectStrategy
_client = new FrameSyncClient(config);
// 内置 CompositeReconnectStrategy（快速重连 3 次 → 快照重连 2 次）
// 断线 → 自动重连 → OnReconnected 事件
```

#### 4. Person 变为纯适配器

```csharp
public class Person
{
    private FrameSyncClient _client;

    // 创建一次
    public void Init(NetworkConfig config)
    {
        _client = new FrameSyncClient(config);
        _client.OnFrame += frame => OnFrame?.Invoke(this, frame);
        _client.OnReconnected += () => OnReconnected?.Invoke(this);
        _client.OnTakeSnapshot = () => TakeSnapshot?.Invoke();
        _client.OnLoadSnapshot = data => LoadSnapshot?.Invoke(data);
    }

    // 纯代理
    public void Connect() => _client.Connect();
    public void JoinRoom(int id) => _client.JoinRoom(id);
    public void SendInput(byte[] data) => _client.SendInput(data);
    public void Tick(float dt) => _client.Tick(dt);

    // 模拟断线（测试用）
    public void SimulateNetworkDrop() => _client.SimulateNetworkDrop();

    // 游戏回调
    public Func<byte[]> TakeSnapshot;
    public Action<byte[]> LoadSnapshot;
    public event Action<Person, FrameData> OnFrame;
}
```

### 需要改动的范围

| 组件 | 改动 |
|------|------|
| **FrameSyncClient** | 吸收 RoomClient + 内置 CompositeReconnectStrategy + Connect/Reconnect 不重建 |
| **ConnectionManager** | 支持 Reset（不重建，只断线重连） |
| **Person** | 瘦身为纯适配器，删除 HandleConnected/HandleReconnectResponse/重连逻辑 |
| **BasicPersonManager** | 简化 ConnectPerson，事件注册只做一次 |
| **MultiClientPersonManager** | 同上 |

### 状态机合并

```
之前（两套）:
  Person:        Idle → Connecting → Connected → InRoom → Syncing → Disconnected
  FrameSyncClient: Disconnected → Binding → WaitingStart → Syncing → Stopped

之后（一套）:
  FrameSyncClient: Disconnected → Connecting → Connected → InRoom → Syncing → Reconnecting
  Person.State = _client.State（直接代理，不维护自己的）
```

### 迁移策略

分两步，避免大爆炸：

**Phase 1: FrameSyncClient 吸收 RoomClient + 重连**
- FrameSyncClient 新增 JoinRoom/LeaveRoom/CreateRoom 方法
- 内置 CompositeReconnectStrategy
- Connect 不重建网络栈，支持 Reconnect

**Phase 2: Person 瘦身**
- 删除 HandleConnected/HandleReconnectResponse
- 删除 _savedFrameNumber/_cachedInitData（FrameSyncClient 内部保持）
- 状态代理到 FrameSyncClient

### 不改的

- 协议不变（Cmd 格式、Wire format 全部保持）
- Demo Manager 的 UI 逻辑不变（只是调用路径简化）
- 服务器不变
