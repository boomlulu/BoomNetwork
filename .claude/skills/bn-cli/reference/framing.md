# TCP 帧处理 — RingBuffer + LengthPrefixFraming + PooledFrame

**文件**: `cli/Core/Framing/RingBuffer.cs`, `LengthPrefixFraming.cs`, `PooledFrame.cs`

## 核心问题

TCP 是流协议，接收数据存在粘包（多个消息粘在一起）和拆包（一个消息分多次到达）。
`LengthPrefixFraming` 通过长度前缀重组完整消息帧。

## RingBuffer

追加-尾 / 消费-头 的环形缓冲区。

```csharp
class RingBuffer {
    RingBuffer(int initialCapacity = 4096);

    void Write(ReadOnlySpan<byte> data);  // 追加到尾部，自动扩容（2倍增长）
    ReadOnlySpan<byte> ReadableSpan;       // 可读区域 [_head.._tail)
    void Consume(int count);               // 消费头部 count 字节
    void Reset();                          // 重置读写位置

    int ReadableLength { get; }
    int Capacity { get; }
}
```

**惰性压缩**: 当 `_head > Capacity/2` 时，将可读数据移动到缓冲区开头。

## LengthPrefixFraming

TCP 流 → 完整消息帧。

```csharp
class LengthPrefixFraming {
    LengthPrefixFraming(int initialCapacity = 8192);

    void Feed(ReadOnlySpan<byte> data);             // 喂入原始 TCP 数据
    bool TryDequeueFrame(out PooledFrame frame);     // 尝试取出一个完整帧
    int PendingFrames { get; }                        // 待消费帧数
    void Reset();                                     // 重置
}
```

**工作流程**:
1. `TcpClientTransport.RecvLoop()` 从 socket 读取原始字节
2. `NetworkSession.OnRawData()` 调用 `Feed()` 喂入
3. 循环调用 `TryDequeueFrame()` 直到返回 false
4. 每个 `PooledFrame` 是一个完整的 Message 编码

**帧格式识别**: 内部调用 `MessageCodec.PeekFrameSize()` 判断是否有足够字节。

## PooledFrame

ArrayPool 租借的帧数据，必须 Dispose 归还。

```csharp
struct PooledFrame : IDisposable {
    byte[] Buffer;             // ArrayPool<byte> 租借的缓冲区
    int Length;                // 有效数据长度
    ReadOnlySpan<byte> Span;   // Buffer[0..Length]

    void Dispose();            // 归还 Buffer 到 ArrayPool
}
```

**重要**: 使用 `using` 或手动 `Dispose()` 确保归还，避免内存泄漏。

## 数据流

```
Socket → byte[] → RingBuffer.Write() → PeekFrameSize() → 有完整帧?
                                         ↓ Yes              ↓ No
                                  PooledFrame (ArrayPool)   等待更多数据
                                         ↓
                                  MessageCodec.Decode()
                                         ↓
                                     Message 结构体
```
