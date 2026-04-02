# 连接管理 — ConnectionManager

**文件**: `cli/Client/Connection/ConnectionManager.cs`, `IReconnectStrategy.cs`, `QuickReconnectStrategy.cs`, `SnapshotReconnectStrategy.cs`, `CompositeReconnectStrategy.cs`, `ReconnectContext.cs`

## ConnectionManager 状态机

```
Disconnected ─── Connect() ──→ Connecting
                                    │
                              OnConnected
                                    ↓
                               Connected ←── 重连成功
                                    │
                              被动断线 (OnDisconnected)
                                    ↓
                              Reconnecting ──→ 策略链尝试
                                    │              │
                              全部失败          成功
                                    ↓              ↓
                              Disconnected    Connected
```

## 心跳 + RTT

```csharp
// 配置
int HeartbeatIntervalMs = 3000;   // 心跳间隔
int HeartbeatTimeoutMs = 10000;   // 心跳超时 → 被动断线

// RTT 测量
int RttMs { get; }    // 最近一次心跳往返时间
```

**流程**:
1. 每 `HeartbeatIntervalMs` 发送 `FrameSyncCmd.Heartbeat`（记录发送时间）
2. 收到 `HeartbeatRsp` → 计算 RTT
3. 超过 `HeartbeatTimeoutMs` 未收到响应 → 触发被动断线 → 进入 Reconnecting

## 重连策略

### IReconnectStrategy 接口

```csharp
interface IReconnectStrategy {
    string Name { get; }
    void Attempt(NetworkSession session, string host, int port,
                 ReconnectContext context,
                 Action onSuccess, Action<string> onFail);
    void Cancel();
}
```

### ReconnectContext

```csharp
class ReconnectContext {
    int PlayerId;
    uint LastFrameNumber;        // 客户端最后收到的帧号
    uint ServerFrameNumber;      // 服务器当前帧号
    uint SnapshotFrame;          // 快照帧号（快照重连时填充）
    byte[]? SnapshotData;        // 快照数据
    bool IsSnapshotRestore;      // 是否走快照恢复
}
```

### QuickReconnectStrategy（快速路径）

```
TCP 重连 → 发送 Reconnect(playerId, lastFrame) → 等待 ReconnectRsp
  → Success: ClearSentBuffer()，回放缓冲帧
  → BufferStale: 失败，让 Composite 降级到 Snapshot
```

### SnapshotReconnectStrategy（慢速路径）

```
FullReset() → TCP 重连 → 发送 Reconnect(playerId, 0)
  → 服务器返回快照数据
  → 设置 context.IsSnapshotRestore = true
  → 上层调用 OnLoadSnapshot 恢复状态
```

### CompositeReconnectStrategy（策略链）

```csharp
// 默认策略链
(QuickReconnect, maxAttempts: 3) → (SnapshotReconnect, maxAttempts: 2)
```

- 按顺序尝试，单策略失败 `maxAttempts` 次后降级到下一个
- 全部耗尽 → 报 `AllStrategiesExhausted` 错误
- `SetQuickReconnectTimeout(ms)` 传播服务器配置

## ConnectionManager 核心 API

```csharp
class ConnectionManager {
    // 状态
    ConnectionState State { get; }    // Disconnected/Connecting/Connected/Reconnecting

    // 配置
    int HeartbeatIntervalMs;
    int HeartbeatTimeoutMs;
    int QuickReconnectMaxMs;
    int RttMs { get; }

    // 上下文注入（上层调用）
    void SetPlayerId(int pid);
    void UpdateFrameNumber(uint frame);

    // 重连控制
    void PauseReconnect();
    void ResumeReconnect();

    // 事件
    Action? OnReconnected;
    Action? OnDisconnected;
    Action<NetworkError>? OnError;
}
```

## 重连时序

```
被动断线
  → ConnectionManager 构建 ReconnectContext
  → 调用 CompositeReconnectStrategy.Attempt()
  → Quick×1: TCP重连+帧缓冲 → 失败
  → Quick×2: TCP重连+帧缓冲 → 失败
  → Quick×3: TCP重连+帧缓冲 → 失败（BufferStale）
  → Snapshot×1: 全量重连+快照 → 成功!
  → ConnectionManager.State = Connected
  → 触发 OnReconnected
```
