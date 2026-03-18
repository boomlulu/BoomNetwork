# BoomNetwork 快速入门

## 架构总览

```
C# 客户端                              Go 服务器
┌──────────────────┐              ┌──────────────────┐
│  FrameSyncClient │              │  Room (帧同步房间) │
│  (帧收发/输入)    │              │  (组帧/广播)      │
├──────────────────┤              ├──────────────────┤
│ConnectionManager │              │  Router (消息路由) │
│  (心跳/重连)      │              │                  │
├──────────────────┤              ├──────────────────┤
│ NetworkSession   │   ← TCP →   │ FrameReader/Writer│
│  (Seq/超时/缓冲)  │              │                  │
├──────────────────┤              ├──────────────────┤
│ TcpTransport     │              │  TcpServer       │
└──────────────────┘              └──────────────────┘
```

---

## 模块说明

### Transport 层 — 字节收发

管 TCP 连接和原始字节的收发。IO 线程读写 Socket，主线程通过 `Tick()` 取数据，所有回调在 `Tick` 内同步触发。

- C#: `TcpClientTransport` — 实现 `ITransport` 接口
- Go: `TcpServer` — Accept 连接，每连接一个 goroutine 读消息

### Framing 层 — 粘包/拆包

处理 TCP 字节流的消息边界问题。内部用 RingBuffer 避免数据前移开销，输出帧使用 ArrayPool 租借。

- C#: `LengthPrefixFraming` — `Feed(bytes)` 喂入，`TryDequeueFrame()` 取出完整帧
- Go: `FrameReader` / `FrameWriter` — 带 bufio 的零分配帧读写器

### Codec 层 — 消息编解码

Message 结构和字节之间的无状态转换。动态包头格式，FlagsCmd 第一个字节决定后续布局。

```
[FlagsCmd: 1B][BodyLen: 2B/4B][Seq: 0B/4B][Data: NB]
FlagsCmd: bit0=LenSize, bit1=HasSeq, bit2-7=Cmd(0-63)
```

- C#: `MessageCodec.Encode()` / `Decode()` — 静态方法，支持 ArrayPool
- Go: `codec.Encode()` / `Decode()` — 支持 sync.Pool 和零拷贝

### Session 层 — 可靠消息通信

在 Transport 之上提供 Seq 管理、请求/响应匹配、超时检测。维护已发送消息缓冲区，支持快速重连时重发未确认消息。

- C#: `NetworkSession` — `Send()` 发消息，`SendAsync()` 发请求等回复，`Tick()` 驱动
- Go: `Router` — 按 Cmd 分发到 Handler，自动回传 Seq

### ConnectionManager — 连接生命周期

管心跳和重连，上层不需要感知。心跳超时自动触发断线，断线自动委托 `IReconnectStrategy` 恢复。

- 心跳: Tick 驱动发送，收到 HeartbeatRsp 重置计时器
- 重连: 策略可插拔，默认先快速重连（3次）再快照重连（2次）

### IReconnectStrategy — 重连策略

策略模式，可替换可组合。

- `QuickReconnectStrategy` — 重建连接 + 重发未确认消息，适合短暂断线
- `SnapshotReconnectStrategy` — 全量重置 + 请求快照，适合长时间断线
- `CompositeReconnectStrategy` — 按顺序尝试多个策略，前一个失败降级到下一个

### FrameSyncClient — 帧同步

只管帧逻辑：SessionBind、收帧分发、发送输入。不管连接、心跳、重连。

- `Connect(host, port)` → 自动走 连接 → Bind → 等待开始 → 同步中
- `SendInput(data)` → 发送玩家输入
- `OnFrame` 事件 → 收到一帧数据

### Room (Go) — 帧同步房间

服务端帧同步核心。按固定帧率 Tick，收集玩家输入组帧，广播给所有玩家。

- `AddPlayer()` / `RemovePlayer()` — 管理房间玩家
- `OnInput()` — 接收玩家输入
- `Start()` — 开始推帧（20fps）

---

## 快速跑起来

### 1. 启动 Go 服务器

```bash
cd svr && go run ./cmd/framesync/ :9000
```

### 2. 跑 C# 客户端测试

```bash
cd cli && dotnet run --project FrameSyncExample
```

### 3. 一键测试（单元 + 跨语言 + 联调）

```bash
./test.sh
```

### 4. 性能报告

```bash
./bench.sh
```

### 5. 压力测试（3000 人）

```bash
cd svr && go run ./cmd/stress/ -rooms=750 -players=4 -duration=10s
```

---

## C# 客户端接入示例

```csharp
// 创建各层
var transport = new TcpClientTransport();
var session = new NetworkSession(transport);
var reconnect = CompositeReconnectStrategy.Default();
var connMgr = new ConnectionManager(session, reconnect);
var client = new FrameSyncClient(session, connMgr);

// 注册事件
client.OnBound += id => Console.WriteLine($"绑定成功: player {id}");
client.OnFrame += frame => {
    // 执行游戏逻辑帧
    GameLogic.Execute(frame);
};
client.OnReconnected += () => Console.WriteLine("重连成功");

// 连接
client.Connect("127.0.0.1", 9000);

// 游戏主循环
while (running)
{
    client.Tick(16); // 每帧 16ms
    client.SendInput(myInputData);
}
```

---

## 协议 Cmd 定义

| Cmd | 值 | 方向 | 说明 |
|-----|---|------|------|
| SessionBind | 10 | C→S | 绑定会话 |
| SessionBindRsp | 11 | S→C | 绑定响应（返回 playerId） |
| StartFrameSync | 20 | S→C | 帧同步开始（携带帧率/间隔/时间戳） |
| StopFrameSync | 21 | S→C | 帧同步结束 |
| FrameInput | 30 | C→S | 玩家输入 |
| PushFrames | 31 | S→C | 推送帧数据 |
| Heartbeat | 40 | C→S | 心跳 |
| HeartbeatRsp | 41 | S→C | 心跳响应 |
| Reconnect | 50 | C→S | 重连请求（携带 playerId） |
| ReconnectRsp | 51 | S→C | 重连响应（返回当前帧号） |

---

## 项目结构

```
BoomNetwork/
├── cli/                        C# 客户端
│   ├── Core/                   共享核心（Message, Codec, Framing, Protocol）
│   ├── Client/
│   │   ├── Transport/          TcpClientTransport
│   │   ├── Session/            NetworkSession
│   │   ├── Connection/         ConnectionManager + 重连策略
│   │   └── FrameSync/          FrameSyncClient
│   ├── Example/                Echo 测试客户端
│   ├── FrameSyncExample/       帧同步联调测试
│   ├── Benchmark/              性能基准测试
│   └── Tests/                  单元测试 + 跨语言测试
│
├── svr/                        Go 服务器
│   ├── codec/                  消息编解码 + 帧读写
│   ├── transport/              TCP 服务器
│   ├── session/                消息路由
│   ├── framesync/              帧同步协议 + Room
│   └── cmd/
│       ├── echo/               Echo 测试服务器
│       ├── framesync/          帧同步服务器
│       └── stress/             压力测试工具
│
├── test.sh                     一键测试
├── bench.sh                    一键性能报告
└── doc/                        文档
```
