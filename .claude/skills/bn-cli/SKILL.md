---
name: bn-cli
description: "BoomNetwork C# 客户端框架完整架构。覆盖协议编解码、传输层 TCP/KCP、会话管理、连接/心跳/重连、房间匹配、帧同步、实体权威同步、轻量状态同步 10 大子系统。"
allowed-tools: ["Read", "Write", "Edit", "Bash", "Glob", "Grep", "Agent"]
---

# BoomNetwork C# 客户端框架 (cli/)

覆盖 `cli/` 目录下全部 C# 客户端代码，两个库 + 六个工具项目。

## 分层架构

```
┌─────────────────────────────────────────────┐
│               FrameSyncClient               │  Client/FrameSync — 门面 + 状态机
├───────────────┬─────────────────────────────┤
│   RoomClient  │   ConnectionManager         │  Client/Room — Client/Connection
│   房间操作    │   心跳/RTT + 重连策略链     │
├───────────────┴─────────────────────────────┤
│               NetworkSession                │  Client/Session — 序列号 + 请求响应
├─────────────────────────────────────────────┤
│  TcpClientTransport  │  KcpClientTransport  │  Client/Transport — ITransport 实现
├─────────────────────────────────────────────┤
│                   Core                      │  Core/ — 共享协议层（零依赖）
│  Message + Codec + Framing + Cmd + Error    │
└─────────────────────────────────────────────┘
```

## 项目结构

```
cli/
├── Core/              BoomNetwork.Core           共享协议类型库（零依赖）
├── Client/            BoomNetwork.Client          客户端网络栈
├── Tests/             BoomNetwork.Tests           NUnit 单元测试
├── Benchmark/         BoomNetwork.Benchmark       BenchmarkDotNet 性能测试
├── Example/           BoomNetwork.Example         Echo 集成测试
├── FrameSyncExample/  BoomNetwork.FrameSyncExample 端到端帧同步
├── KcpTest/           BoomNetwork.KcpTest         KCP 冒烟测试
└── StressTest/        BoomNetwork.StressTest      多客户端压测
```

## 命名空间索引

| 命名空间 | 目录 | 核心类型 |
|----------|------|----------|
| `BoomNetwork.Core` | Core/ | Message, ErrorCode, NetworkError |
| `BoomNetwork.Core.Codec` | Core/Codec/ | MessageCodec |
| `BoomNetwork.Core.Framing` | Core/Framing/ | RingBuffer, LengthPrefixFraming, PooledFrame |
| `BoomNetwork.Core.Transport` | Core/Transport/ | ITransport, TransportState |
| `BoomNetwork.Core.FrameSync` | Core/FrameSync/ | FrameSyncCmd, FrameSyncExtCmd, FrameDataCodec, RoomCodec, SnapshotCodec, EntityStateCodec, AuthorityTransferCodec, StateSyncCodec, **FrameEvent, FrameEventType** |
| `BoomNetwork.Client.Transport` | Client/Transport/ | TcpClientTransport, KcpClientTransport |
| `BoomNetwork.Client.Session` | Client/Session/ | NetworkSession |
| `BoomNetwork.Client.Connection` | Client/Connection/ | ConnectionManager, IReconnectStrategy, QuickReconnectStrategy, SnapshotReconnectStrategy, CompositeReconnectStrategy |
| `BoomNetwork.Client.Room` | Client/Room/ | RoomClient |
| `BoomNetwork.Client.FrameSync` | Client/FrameSync/ | FrameSyncClient |

## 参考文档

| 文档 | 内容 |
|------|------|
| [overview.md](reference/overview.md) | 分层架构总览 + 错误体系 + 依赖关系 |
| [protocol.md](reference/protocol.md) | Message 线格式 + CmdType + FlagsCmd + Cmd/ExtCmd 表 + MessageCodec |
| [framing.md](reference/framing.md) | TCP 粘包处理：RingBuffer + LengthPrefixFraming + PooledFrame |
| [transport.md](reference/transport.md) | ITransport 接口 + TCP IO 线程模型 + KCP Tick 驱动模型 |
| [session.md](reference/session.md) | NetworkSession：序列号管理 + 请求响应匹配 + 发送缓冲区 |
| [connection.md](reference/connection.md) | ConnectionManager：状态机 + 心跳/RTT + 重连策略链 (Quick->Snapshot) |
| [room.md](reference/room.md) | RoomClient + RoomCodec + 房间全协议 (Create/Join/Match/Leave) |
| [framesync.md](reference/framesync.md) | FrameSyncClient：门面状态机 + 快照 + 全 API 索引 + **帧内嵌事件 + OnHostChanged** |
| [entity-sync.md](reference/entity-sync.md) | IEntitySync + EntityStateCodec + AuthorityTransferCodec + 权威转移 |
| [state-sync.md](reference/state-sync.md) | StateSyncCodec + DataEntry + KV 操作 + 消息中继 |

## 同步规则

修改 `cli/` 下的 C# 文件后，必须同步到 Unity UPM 包：
- `cli/Core/**` → `unity/com.boom.boomnetwork/Runtime/Core/**`
- `cli/Client/**` → `unity/com.boom.boomnetwork/Runtime/Client/**`

## 不同步防御

修改帧同步相关的 Simulation 代码时，必须遵守 **bn-desync** skill 中的检查清单。
关键规则：
- Simulation 内禁止 float/double/Mathf/Math.Sin/Random
- FInt 常量用 `new FInt(raw)` 不用 `FromFloat()`
- Snapshot 序列化必须保留 slot index + 覆盖所有可变字段
- ComputeHash 必须与 Snapshot 字段一一对应
- 碰撞 resolver 内循环必须有 IsAlive 守卫
- 详见 [bn-desync SKILL](../bn-desync/SKILL.md)

## Frame-Embedded Player Events & Host Management

### 新类型（`Core/FrameSync/`）

```csharp
// FrameData 新增字段
public struct FrameData {
    public int FrameNumber;
    public List<PlayerInput> Inputs;
    public List<FrameEvent> Events;   // 新增：帧内嵌事件列表
}

// 事件结构
public struct FrameEvent {
    public byte EventType;
    public int PlayerId;
}

// 事件类型常量
public static class FrameEventType {
    public const byte PlayerJoined  = 1;
    public const byte PlayerLeft    = 2;
    public const byte PlayerOffline = 3;
    public const byte PlayerOnline  = 4;
    public const byte HostChanged   = 5;
}
```

### FrameDataCodec 变化

`Encode` 在 inputs 之后追加 `[EventCount:1][...events(5B each)...]`。
`Decode` 在读完 inputs 后检查 `offset < buf.Length` 再读 events，保证向后兼容老服务端（无事件段时不报错）。

### FrameSyncClient 新增 API

```csharp
// 事件：Host 变更时触发，参数为新 Host 的 PlayerId
public event Action<int> OnHostChanged;
```

### HandlePushFrames 派发顺序

收到帧数据后，`HandlePushFrames` 按以下顺序处理：

1. 遍历 `frame.Events`，逐条分发：
   - `PlayerJoined` / `PlayerLeft` / `PlayerOffline` / `PlayerOnline` → 触发对应已有回调
   - `HostChanged` → 触发 `OnHostChanged(playerId)`
2. 触发 `OnFrame(frame)`（事件始终在 OnFrame **之前**派发）

### 双路径：同步模式 vs 非同步模式

```
玩家 join / leave / offline / online 事件
        │
        ├── 服务器房间运行中（IsRunning == true）
        │       └─▶ 帧内嵌事件（FrameEvent）随下一帧下发
        │               客户端在 HandlePushFrames 中处理
        │
        └── 服务器房间未运行
                └─▶ ExtCmd 20-23 立即广播
                        客户端走原有 ExtCmd 处理路径（不变）
```

两条路径互斥，客户端无需区分——同步模式的事件通过帧到达，非同步模式的事件通过 ExtCmd 到达，各自独立处理。
