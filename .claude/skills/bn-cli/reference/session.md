# 会话层 — NetworkSession

**文件**: `cli/Client/Session/NetworkSession.cs`

## 职责

在 ITransport 之上提供：
1. **序列号管理** — 自增 Seq，跟踪服务器最后收到的 Seq
2. **请求/响应匹配** — 通过 Seq 关联请求和响应，支持超时
3. **TCP 粘包处理** — 内置 LengthPrefixFraming
4. **发送缓冲区** — 保存已发送消息，用于快速重连时重发

## 核心 API

```csharp
class NetworkSession {
    // 构造
    NetworkSession(ITransport transport);

    // 发送（无响应）
    void Send(byte cmd, byte[]? data = null);
    void SendExt(ushort extCmd, byte[]? data = null);
    void SendGame(uint gameCmd, byte[]? data = null);

    // 发送（有响应，Seq 自动分配）
    void SendAsync(byte cmd, byte[]? data, int timeoutMs,
                   Action<Message> onResponse, Action? onTimeout);
    void SendExtAsync(ushort extCmd, byte[]? data, int timeoutMs,
                      Action<Message> onResponse, Action? onTimeout);

    // 驱动
    void Tick(int deltaTimeMs);    // 检查超时 + 处理接收

    // 重置
    void LightReset();    // 清除 pending requests，保留 sent buffer
    void FullReset();     // 清除一切，重置 seq

    // 发送缓冲区
    void ClearSentBuffer();

    // 回调
    Action<Message>? OnMessage;    // 非 response 的消息回调
}
```

## 序列号机制

```
发送:
  _nextSeq++ → 写入 Message.Seq → 加入 _pendingRequests[seq]

接收:
  解码 Message → 有 Seq?
    → 查找 _pendingRequests[seq] → 命中: 调用 onResponse，移除
    → 未命中 / 无 Seq: 调用 OnMessage 回调
```

## 请求超时检测

```csharp
struct PendingRequest {
    int TimeoutMs;
    int ElapsedMs;
    Action<Message> OnResponse;
    Action? OnTimeout;
}
```

`Tick(deltaTimeMs)` 遍历 `_pendingRequests`，累加 `ElapsedMs`，超时则调用 `OnTimeout` 并移除。

## 发送缓冲区（快速重连）

```csharp
struct SentMessage {
    int Seq;
    byte[] EncodedData;    // 已编码的完整帧
    int EncodedLength;
}
```

- 容量 256 条（LinkedList，FIFO 淘汰）
- 快速重连成功后，重发所有 buffered 消息
- `ClearSentBuffer()` 在重连成功后调用

## 粘包处理流程

```
ITransport.OnData(raw bytes)
  → _framing.Feed(data)
  → while (_framing.TryDequeueFrame(out frame))
      → MessageCodec.Decode(frame.Span)
      → 路由到 pending response 或 OnMessage
      → frame.Dispose()
```
