# BoomNetwork 架构设计

## 设计原则

本项目在开发过程中遵循以下原则，每次重构都以此为检验标准：

| 原则 | 如何体现 |
|------|---------|
| **SRP 单一职责** | 每个类只管一件事：Transport 管字节，Session 管消息，ConnectionManager 管连接，FrameSyncClient 管帧 |
| **OCP 开闭原则** | 加新传输协议 (KCP) 不改已有代码，只新增 `KcpClientTransport`；加新重连策略不改 `ConnectionManager` |
| **DIP 依赖倒置** | 上层依赖接口 (`ITransport`, `IReconnectStrategy`)，不依赖具体实现 |
| **策略模式** | 重连策略可插拔：`QuickReconnect` / `SnapshotReconnect` / `Composite` |
| **零分配热路径** | Codec 用 Span + ArrayPool，Framing 用 RingBuffer + PooledFrame，Transport 用 ArrayPool |

---

## 分层架构

```
    职责边界                    C# 客户端                 Go 服务器

┌─ 帧同步层 ─────┐    FrameSyncClient              Room
│ 帧收发/输入     │    - OnFrame 回调               - 收集输入
│ SessionBind    │    - SendInput                  - 组帧广播
│ 帧同步状态      │    - 5 个状态                    - Tick 驱动
├─ 连接管理层 ───┤    ConnectionManager             (服务端无需)
│ 心跳           │    - Tick 驱动心跳
│ 重连编排        │    - 委托 IReconnectStrategy
│ 状态机         │    - 4 个状态
├─ 重连策略层 ───┤    IReconnectStrategy            (服务端响应即可)
│ 可插拔         │    ├ QuickReconnect
│ 可组合         │    ├ SnapshotReconnect
│                │    └ Composite
├─ 会话层 ───────┤    NetworkSession                Router
│ Seq 管理       │    - SendAsync + 超时             - Cmd 分发
│ 请求/响应匹配   │    - AckSeq 跟踪                  - 自动回传 Seq
│ 消息缓冲       │    - 已发送缓冲区 (重发用)
├─ Codec 层 ────┤    MessageCodec                  codec.Encode/Decode
│ 编解码         │    - 动态包头                     - sync.Pool
│ 无状态         │    - Span + ArrayPool            - 零拷贝
├─ Framing 层 ──┤    LengthPrefixFraming           FrameReader/Writer
│ 粘包/拆包      │    - RingBuffer                  - bufio
│                │    - PooledFrame                 - 复用内部 buffer
├─ Transport 层 ┤    ITransport                    Server
│ 字节收发       │    ├ TcpClientTransport          ├ TcpServer
│ 连接管理       │    └ KcpClientTransport          └ KcpServer
└────────────────┘
```

---

## 数据流

### 发送路径

```
游戏代码
  → FrameSyncClient.SendInput(data)
  → NetworkSession.Send(cmd, data)
  → MessageCodec.Encode(msg, buffer)
  → ITransport.Send(buffer)
  → TCP socket / KCP session
```

### 接收路径

```
TCP socket / KCP session
  → ITransport.Tick() → OnData(bytes)
  → LengthPrefixFraming.Feed(bytes) → TryDequeueFrame()
  → MessageCodec.Decode(frame) → Message
  → NetworkSession.DispatchMessage()
    ├→ 匹配 PendingRequest → onResponse 回调
    └→ OnMessage 事件 → FrameSyncClient.HandleMessage()
        ├→ PushFrames → OnFrame 回调
        ├→ StartFrameSync → 状态切换
        └→ HeartbeatRsp → ConnectionManager 重置计时器
```

### 重连流程

```
心跳超时
  → ConnectionManager 检测断线
  → 委托 CompositeReconnectStrategy
  → 第一轮: QuickReconnectStrategy (最多 3 次)
      ├→ Session.LightReset() (保留缓冲区)
      ├→ Transport.Reconnect()
      ├→ 发送 Reconnect(playerId)
      ├→ 成功: ResendUnacked() → 继续帧同步
      └→ 失败: 降级到下一策略
  → 第二轮: SnapshotReconnectStrategy (最多 2 次)
      ├→ Session.FullReset() (清空一切)
      ├→ Transport.Connect()
      ├→ 发送 Reconnect(playerId)
      ├→ 成功: 加载快照 → 从快照帧继续
      └→ 失败: 通知上层 OnDisconnected
```

---

## 线格式

```
动态包头:
[FlagsCmd: 1 byte]
  bit 0:   LenSize  (0 = BodyLen 2B, 最大 64KB)
                     (1 = BodyLen 4B, 最大 4GB)
  bit 1:   HasSeq   (0 = 无 Seq 字段)
                     (1 = 有 Seq 4B)
  bit 2-7: Cmd      (0-63)

[BodyLen: 2 or 4 bytes, little-endian]
[Seq: 0 or 4 bytes, little-endian]  (仅 HasSeq=1)
[Data: BodyLen - sizeof(Seq) bytes]

包头大小:
  推帧 (最高频):  FlagsCmd(1) + BodyLen(2)           = 3 bytes
  请求/响应:      FlagsCmd(1) + BodyLen(2) + Seq(4)   = 7 bytes
  大包 (>64KB):   FlagsCmd(1) + BodyLen(4)           = 5 bytes
  大包+Seq:       FlagsCmd(1) + BodyLen(4) + Seq(4)   = 9 bytes
```

---

## 内存管理策略

| 层 | C# 策略 | Go 策略 |
|---|---------|---------|
| Transport | ArrayPool 租借 recv buffer，Tick 后归还 | — |
| Framing | RingBuffer 避免 BlockCopy；PooledFrame 用 ArrayPool | FrameReader 复用内部 buffer |
| Codec | Encode 用调用方 buffer；Decode 可选 ArrayPool | Encode 用 sync.Pool；Decode 零拷贝引用原 buffer |
| Session | 发送缓冲区复用 ArrayPool；PendingRequest 用 struct | — |

热路径（每帧 20 次的收发）目标：C# 端 Encode 零分配，Decode+Pool 零分配，Framing 零分配。

---

## 测试体系

| 测试类型 | 覆盖范围 | 命令 |
|---------|---------|------|
| C# 单元测试 (19 项) | Codec 编解码 + Framing 粘包拆包 | `cd cli && dotnet test` |
| Go 单元测试 | Codec + Framing + 多消息读写 | `cd svr && go test ./codec/` |
| 跨语言兼容 | C# 编码 → Go 解码 / Go 编码 → C# 解码 | `./test.sh` |
| TCP Echo 联调 | Session SendAsync + 超时 + 并发 | `./test.sh` |
| KCP 单元测试 (7 项) | 基本/大小包/高频/粘包/混合 | `dotnet run --project KcpTest` |
| 帧同步联调 (9 项) | Bind + 开始 + 收帧 + 心跳 + 断线重连 | `dotnet run --project FrameSyncExample` |
| TCP 压测 | 3000 人 750 房 10 秒 | `go run ./cmd/stress/` |
| KCP 压测 | 1000 人 250 房 10 秒 | `go run ./cmd/kcpstress/` |
| 性能基准 | Codec + Framing 的 ns 级基准 | `./bench.sh` |
