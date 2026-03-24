# 配置参考

## ConnectionManager

| 参数 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `HeartbeatIntervalMs` | float | 3000 | 心跳发送间隔（毫秒） |
| `HeartbeatTimeoutMs` | float | 10000 | 心跳超时判定断线（毫秒）。超过此时间没收到 HeartbeatRsp 就触发重连 |

```csharp
var cm = new ConnectionManager(session, strategy);
cm.HeartbeatIntervalMs = 3000;  // 每 3 秒发一次心跳
cm.HeartbeatTimeoutMs = 10000;  // 10 秒无回复判定断线
```

**调参建议**：
- 弱网环境（移动端）：间隔 2000，超时 8000
- 局域网/Wi-Fi：间隔 5000，超时 15000
- 测试环境：间隔 500，超时 2000（加速验证）

---

## QuickReconnectStrategy

| 参数 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `TimeoutMs` | float | 5000 | 单次快速重连超时（毫秒）。发出 Reconnect 请求后等待回复的最长时间 |

```csharp
var quick = new QuickReconnectStrategy { TimeoutMs = 5000 };
```

---

## SnapshotReconnectStrategy

| 参数 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `TimeoutMs` | float | 10000 | 单次快照重连超时（毫秒） |

```csharp
var snapshot = new SnapshotReconnectStrategy { TimeoutMs = 10000 };
```

---

## CompositeReconnectStrategy

| 参数 | 说明 |
|------|------|
| 策略链 | `(策略, 最大尝试次数)` 的有序列表 |

```csharp
// 默认: 快速重连 3 次 → 快照重连 2 次
var strategy = CompositeReconnectStrategy.Default();

// 自定义
var strategy = new CompositeReconnectStrategy(
    (new QuickReconnectStrategy { TimeoutMs = 3000 }, 3),   // 先快速，最多 3 次
    (new SnapshotReconnectStrategy { TimeoutMs = 8000 }, 2) // 再快照，最多 2 次
);
```

**调参建议**：
- 竞技游戏（断线影响大）：快速 5 次 + 快照 3 次
- 休闲游戏（断线可退出）：快速 2 次 + 快照 1 次
- 弱网环境：增大每次的 TimeoutMs

---

## NetworkSession

| 参数 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `SentBufferCapacity` | int | 256 | 已发送消息缓冲区最大容量。快速重连时从这里重发。超过上限自动丢弃最早的消息 |

```csharp
var session = new NetworkSession(transport);
session.SentBufferCapacity = 256;  // 保留最近 256 条已发送消息
```

**调参建议**：
- 20fps 帧同步：256 条 ≈ 12.8 秒的消息缓冲
- 如果快速重连窗口 > 10 秒：增大到 512

---

## Go 服务端

### TcpServer / KcpServer

| 参数 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `ReadTimeout` | Duration | 60s | 连接读超时。超过此时间无数据自动断开（防恶意占连接） |
| `WriteTimeout` | Duration | 10s | 写超时 |

```go
cfg := transport.ServerConfig{
    ReadTimeout:  60 * time.Second,
    WriteTimeout: 10 * time.Second,
}
server := transport.NewTcpServer(handler, cfg)
```

### Room

| 参数 | 说明 |
|------|------|
| `frameRate` | 帧率，创建时指定。如 `NewRoom(20)` = 20fps = 50ms/帧 |

---

## KCP 参数（Go 端 kcp-go）

Go 端 KCP 服务器在 Accept 后配置：

```go
conn.SetStreamMode(true)        // 流模式（和 C# 端对齐）
conn.SetWriteDelay(false)       // 不延迟写
conn.SetNoDelay(1, 10, 2, 1)   // nodelay=1, interval=10ms, resend=2, nc=1
conn.SetWindowSize(256, 256)    // 收发窗口 256
conn.SetMtu(1400)               // MTU
```

C# 端 KcpClientTransport 内部的 kcp-csharp 对应配置：

```csharp
// 在 KcpClientTransport.DoConnect() 内
_session.AckNoDelay = true;     // ACK 不延迟
_session.WriteDelay = false;    // 不延迟写
// KCP.NoDelay(0, 30, 2, 1) — kcp-csharp 内部默认
```

### KCP NoDelay 参数说明

`NoDelay(nodelay, interval, resend, nc)`:

| 参数 | 说明 | 建议值 |
|------|------|--------|
| nodelay | 0=正常, 1=不延迟 | 1（游戏场景） |
| interval | 内部 update 间隔(ms) | 10-30 |
| resend | 快速重传触发次数 | 2 |
| nc | 0=正常拥塞控制, 1=关闭 | 1（游戏场景关闭拥塞控制） |

### KCP vs TCP 内存对比

| | TCP per-connection | KCP per-connection |
|---|---|---|
| 堆内存 | ~17 KB | ~331 KB |
| 适用 | 大规模连接 | 延迟敏感、弱网 |

KCP 内存高因为每连接维护收发窗口缓冲区。如果连接数 > 3000 且内存有限，建议用 TCP。

---

## Go 服务端 Admin HTTP

| 参数 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `adminAddr` | string | `:9091` | Admin HTTP 地址。空 = 不启用 |
| `adminToken` | string | `""` | Bearer Token 鉴权。空 = 不鉴权 |

```yaml
# config.yaml
adminAddr: ":9091"
adminToken: "your-secret"  # 生产环境必须设置
```

9 个端点（监控 / 控制 / 诊断），完整文档见 → [gm-tools.md](gm-tools.md)

---

## 完整配置示例

```csharp
// === 生产环境 ===
// FrameSyncClient 内部自动创建 Transport + Session + ConnectionManager + 重连策略
var client = new FrameSyncClient(
    heartbeatIntervalMs: 3000,   // 每 3 秒发心跳
    heartbeatTimeoutMs: 10000    // 10 秒无回复触发重连
);
client.Connect("game.example.com", 9000);
```

```csharp
// === 测试环境（加速验证）===
var client = new FrameSyncClient(
    heartbeatIntervalMs: 500,    // 0.5 秒心跳
    heartbeatTimeoutMs: 2000     // 2 秒超时
);
```
