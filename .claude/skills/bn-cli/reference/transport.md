# 传输层 — ITransport + TCP + KCP

**文件**: `cli/Core/Transport/ITransport.cs`, `cli/Client/Transport/TcpClientTransport.cs`, `KcpClientTransport.cs`

## ITransport 接口

```csharp
interface ITransport {
    TransportState State { get; }

    void Connect(string host, int port);
    void Disconnect();
    void Reconnect(string host, int port);
    void Send(byte[] data, int length);
    void Tick();                                    // 主线程每帧调用

    event Action OnConnected;
    event Action OnDisconnected;
    event Action<byte[], int> OnData;               // (buffer, length)
    event Action<string> OnError;
}

enum TransportState { Disconnected, Connecting, Connected }
```

## TcpClientTransport — TCP 实现

### 线程模型

```
主线程                          IO 线程 (RecvLoop)
  │                               │
  ├─ Connect() ──────────────>   启动 RecvLoop
  │                               │
  ├─ Send() ──── _sendLock ──>   NetworkStream.Write()
  │                               │
  │                               ├─ NetworkStream.Read()
  │                               ├─ 切块 → RecvChunk (ArrayPool)
  │  Tick() ← ConcurrentQueue ──├─ _recvQueue.Enqueue(chunk)
  │  ├─ 出队 → OnData 回调       │
  │  ├─ 出队 → OnConnected       ├─ 异常 → 断线事件
  │  └─ 出队 → OnDisconnected    └─ 循环
```

### 关键设计

- **IO 线程**: 独立 `RecvLoop()` 阻塞读取 socket
- **线程安全**: `_sendLock` 保护发送，`ConcurrentQueue<RecvChunk>` 无锁队列传递数据
- **双重断线保护**: `Interlocked` 操作 `_running` 和 `_disconnectHandled` 防止重复触发
- **RecvChunk**: `struct { byte[] Buffer; int Length; }` — Buffer 来自 ArrayPool

### Tick() 主线程处理

```
1. 检查事件队列 (connected/disconnected/error)
2. 出队所有 RecvChunk → 逐个回调 OnData
3. 归还 RecvChunk.Buffer 到 ArrayPool
```

## KcpClientTransport — KCP/UDP 实现

### Tick 驱动模型（无独立 IO 线程）

```
主线程
  │
  ├─ Connect() → 创建 UDPSession
  │
  └─ Tick() 每帧:
       ├─ _session.Update()        // 驱动 KCP 协议（重传/拥塞控制）
       └─ while (_session.Recv())  // 出队所有完整 KCP 包
            └─ OnData 回调
```

### KCP 配置

```
AckNoDelay = true      // ACK 不延迟
WriteDelay = false     // 发送不延迟
NoDelay(0, 30, 2, 1)   // interval=30ms, resend=2, nc=1
Stream mode = enabled   // 流模式
MTU = 1400
```

### 嵌入式 KCP 库 (KcpProject/)

| 类 | 职责 |
|-----|------|
| `KCP` | 完整 KCP 协议实现 |
| `UDPSession` | Socket + KCP 胶水层 |
| `ByteBuffer` | KCP 内部缓冲区 |

## TCP vs KCP 选择

| 维度 | TCP | KCP |
|------|-----|-----|
| 可靠性 | 内核保证 | KCP 协议层保证 |
| 延迟 | 受 Nagle/ACK 延迟影响 | NoDelay + 快速重传 |
| 线程 | 独立 IO 线程 | 纯 Tick（单线程） |
| 适用 | 默认选择、稳定优先 | 低延迟要求 |
