# BoomNetwork 快速入门

## 文档阅读顺序

| 顺序 | 文档 | 内容 | 适合谁 |
|------|------|------|--------|
| 1 | [why.md](why.md) | 这是什么、解决什么问题、何时该用 | 所有人 |
| 2 | [concepts.md](concepts.md) | 帧同步、粘包、Seq、心跳、重连等核心概念 | 不熟悉游戏网络的人 |
| 3 | **quickstart.md (本文)** | 架构总览、模块说明、如何跑起来 | 所有人 |
| 4 | [sequence-diagrams.md](sequence-diagrams.md) | 6 张时序图：游戏流程、心跳、重连、Tick 内部 | 想理解运行流程的人 |
| 5 | [configuration.md](configuration.md) | 所有可配参数、默认值、调参建议 | 接入项目时查阅 |
| 6 | [architecture.md](architecture.md) | 设计原则、分层架构、数据流、线格式、内存策略 | 想深入理解或贡献代码的人 |
| 7 | [gm-tools.md](gm-tools.md) | Admin API + Unity ServerWindow 全部 GM 能力 | 运维 / 开发调试 |
| 8 | [benchmark-report.md](benchmark-report.md) | Codec 基准、TCP/KCP 压测、包头优化效果 | 关心性能的人 |
| 9 | [roadmap.md](roadmap.md) | 未来规划 v0.1 → v1.0 | 想了解方向的人 |

---

## 架构总览

```
C# 客户端                                Go 服务器
┌────────────────────┐              ┌────────────────────┐
│   FrameSyncClient  │              │   Room (帧同步房间)  │
│   (帧收发/输入)     │              │   (组帧/广播)       │
├────────────────────┤              ├────────────────────┤
│  ConnectionManager │              │   Router (消息路由)  │
│  (心跳/重连编排)    │              │                    │
├────────────────────┤              ├────────────────────┤
│  IReconnectStrategy│              │                    │
│  (Quick/Snapshot)  │              │                    │
├────────────────────┤              ├────────────────────┤
│  NetworkSession    │  ← TCP/KCP → │  FrameReader/Writer │
│  (Seq/超时/缓冲)   │              │                    │
├────────────────────┤              ├────────────────────┤
│  ITransport        │              │  Server (TCP/KCP)   │
│  (TCP / KCP)       │              │                    │
└────────────────────┘              └────────────────────┘
```

---

## 模块说明

### Transport 层 — 字节收发

管连接和原始字节的收发。主线程通过 `Tick()` 驱动，所有回调在 `Tick` 内同步触发。`ITransport` 接口统一 TCP 和 KCP。

- C#: `TcpClientTransport` — IO 线程 + ConcurrentQueue
- C#: `KcpClientTransport` — 基于 kcp-csharp，Tick 内同步 poll
- Go: `TcpServer` / `KcpServer` — `transport.NewServer("tcp"|"kcp", handler)` 一行切换

### Framing 层 — 粘包/拆包

处理字节流的消息边界。内部用 RingBuffer 避免 O(n) 数据前移，输出帧使用 ArrayPool 租借。

- C#: `LengthPrefixFraming` — `Feed(bytes)` 喂入，`TryDequeueFrame()` 取出
- Go: `FrameReader` / `FrameWriter` — 带 bufio 的零分配帧读写器

### Codec 层 — 消息编解码

Message 和字节之间的无状态转换。动态包头，FlagsCmd 第一个字节决定后续布局。

```
[FlagsCmd: 1B][ExtCmd?: 0/2/4B][BodyLen: 2B/4B][Seq: 0B/4B][Data: NB]
FlagsCmd: bit0=LenSize, bit1=HasSeq, bit2-3=CmdType(00=Core/01=Ext/10=Game), bit4-7=CoreCmd
```

详细协议分层说明见 [protocol-cmd-tiers.md](protocol-cmd-tiers.md)。

- C#: `MessageCodec.Encode()` / `Decode()` — 支持 ArrayPool 零分配
- Go: `codec.EncodeTo()` / `Decode()` — 支持 sync.Pool 和零拷贝

### Session 层 — 可靠消息通信

Seq 管理、请求/响应匹配、超时检测。维护已发送消息缓冲区，支持快速重连时重发。

- C#: `NetworkSession` — `Send()` / `SendAsync()` / `ResendUnacked()` / `Tick()`
- Go: `Router` — 按 Cmd 分发到 Handler，自动回传 Seq

### ConnectionManager — 连接生命周期

管心跳和重连编排，上层不需要感知。心跳超时自动断线，断线自动委托策略恢复。

- 状态机: `Disconnected → Connecting → Connected → Reconnecting`
- 心跳: Tick 驱动，可配置间隔和超时
- 重连: 委托给 `IReconnectStrategy`，不自己决定策略

### IReconnectStrategy — 重连策略（策略模式）

可替换可组合，遵循开闭原则。

- `QuickReconnectStrategy` — 保留缓冲区 + 重发未确认消息
- `SnapshotReconnectStrategy` — 全量重置 + 请求快照恢复
- `CompositeReconnectStrategy` — 按顺序尝试，先快速后降级为快照

### FrameSyncClient — 帧同步客户端（长生命周期）

拥有完整网络栈（Transport + Session + ConnectionManager + RoomClient），一次创建、全程复用。

- 内置连接、心跳、断线自动重连（CompositeReconnectStrategy）
- 内置房间管理：CreateRoom / JoinRoom / LeaveRoom
- 内置快照：定时上传 + 重连恢复 + 迟到者加入
- `Connect(host, port)` → 创建网络栈 → SessionBind → Connected
- `SendInput(data)` → 发送玩家输入
- `OnFrame` → 收到帧数据回调
- 状态机：`Disconnected → Connecting → Connected → InRoom → Syncing → Reconnecting`

### Room (Go) — 帧同步房间

服务端核心。按固定帧率 Tick，收集输入组帧广播。`PlayerConn` 接口解耦连接实现。

---

## 快速跑起来

```bash
# 1. 用配置文件启动（推荐）
cd svr && go run ./cmd/framesync/ -config=cmd/framesync/config.yaml

# 2. 用命令行参数启动
cd svr && go run ./cmd/framesync/ -addr=:9000 -proto=tcp -ppr=4

# 3. Admin HTTP 端点验证（完整端点列表见 gm-tools.md）
curl http://127.0.0.1:9091/health    # 健康检查
curl http://127.0.0.1:9091/stats     # 流量统计
curl http://127.0.0.1:9091/rooms     # 房间列表

# 4. C# 帧同步集成测试
cd cli && dotnet run --project FrameSyncExample

# 5. 一键全量测试 (7 项)
./test.sh

# 6. 性能基准
./bench.sh
```

---

## C# 接入示例

```csharp
// 一行创建，内部自动构建 Transport + Session + ConnectionManager + 重连策略
var client = new FrameSyncClient(heartbeatIntervalMs: 3000, heartbeatTimeoutMs: 10000);

// 注册事件
client.OnConnected += () => Console.WriteLine($"Connected as Player {client.PlayerId}");
client.OnJoinedRoom += (roomId, existing) => Console.WriteLine($"Joined room {roomId}");
client.OnFrameSyncStart += data => Console.WriteLine($"Syncing at {data.FrameRate} fps");
client.OnFrame += frame => GameLogic.Execute(frame);
client.OnReconnected += () => Console.WriteLine("Reconnected automatically");

// 快照回调（重连 + 迟到者加入时恢复状态）
client.OnTakeSnapshot = () => SerializeGameState();
client.OnLoadSnapshot = data => RestoreGameState(data);

// 连接 → 建房 → 开始
client.Connect("127.0.0.1", 9000);
// ...等 OnConnected 后：
client.CreateAndJoinRoom(4);
// ...等 OnJoinedRoom 后：
client.RequestStart();

// 游戏主循环
while (running)
{
    client.Tick(16);
    client.SendInput(myInputData);
}
```

### Unity 接入（通过 Person 薄适配器）

```csharp
// Person 是游戏层的薄包装，内部持有 FrameSyncClient
var person = new Person();
person.Connect(networkConfig);
person.OnConnected += p => Debug.Log($"Player {p.PlayerId} connected, RTT={p.RttMs}ms");
person.CreateAndJoinRoom(4);
person.RequestStart();
```

### 实体权威同步（Demo02）

```csharp
// 每个实体实现 IEntitySync 接口
// 权威实体：本地立刻执行输入 + 自动发送状态给远端
// 远端实体：Dead Reckoning + 惯性模型平滑追踪
person.RegisterAuthorityEntity(myEntitySync);
person.OnEntityState += (senderPid, entityId, data, offset, len) => {
    remoteEntity.OnRemoteState(data, offset, len, senderPid);
};
```

详细设计：[design-entity-authority-sync.md](design-entity-authority-sync.md)

---

## 协议 Cmd 定义

完整命令表见 [api-reference.md — 协议命令表](api-reference.md#协议命令表)。常用核心命令速查：

**Core Cmd（包头 3B，最高频）**

| CoreCmd | 名称 | 方向 | 说明 |
|---------|------|------|------|
| 1 | SessionBind | C→S | 绑定会话 |
| 2 | SessionBindRsp | S→C | 绑定响应 (playerId) |
| 4 | StartFrameSync | S→C | 帧同步开始 (帧率/间隔/时间戳) |
| 6 | FrameInput | C→S | 玩家输入 |
| 7 | PushFrames | S→C | 推送帧数据 |
| 8 | Heartbeat | C→S | 心跳 |
| 10 | Reconnect | C→S | 重连请求 (playerId) |

**Extended Cmd（包头 5B，房间/快照/实体同步）**

| ExtCmd | 名称 | 方向 | 说明 |
|--------|------|------|------|
| 3 | CreateRoom | C→S | 创建房间 |
| 5 | JoinRoom | C→S | 加入房间 |
| 40 | SendEntityState | C→S | 发送实体权威状态 |
| 41 | PushEntityState | S→C | 广播实体权威状态 |

---

## 项目结构

```
BoomNetwork/
├── cli/                             C# 客户端
│   ├── Core/                        共享核心（Codec/Framing/FrameSync/Transport）
│   ├── Client/                      客户端实现
│   │   ├── Transport/               TcpClientTransport + KcpClientTransport
│   │   ├── Session/                 NetworkSession (Seq/超时/缓冲)
│   │   ├── Connection/              ConnectionManager + 重连策略
│   │   ├── Room/                    RoomClient
│   │   └── FrameSync/              FrameSyncClient（长生命周期，拥有完整网络栈）
│   ├── FrameSyncExample/            帧同步集成测试 (15 项)
│   ├── StressTest/                  压力测试客户端
│   ├── KcpTest/                     KCP 单元测试 (7 项)
│   └── Tests/                       单元测试 (29 项) + 跨语言兼容
│
├── svr/                             Go 服务器
│   ├── codec/                       编解码 + 帧读写
│   ├── transport/                   TcpServer + KcpServer
│   ├── session/                     Router 消息路由
│   ├── framesync/                   协议定义 + Room + RoomManager
│   └── cmd/framesync/               帧同步服务器
│       ├── main.go                  消息处理 + 路由
│       ├── admin.go                 Admin HTTP（/health /stats）
│       └── stats.go                 流量统计（环形缓冲区）
│
├── unity/
│   ├── com.boom.boomnetwork/        核心 UPM 包（生产用）
│   │   └── Runtime/Client/          FrameSyncClient + BoomNetworkManager
│   └── com.boom.boomnetwork.gm/    GM 工具 UPM 包（Editor-only，开发用）
│       └── Editor/                  AdminClient + ServerWindow
│
├── doc/                             文档
├── test.sh                          一键全量测试 (7/7)
└── bench.sh                         一键性能报告
```
