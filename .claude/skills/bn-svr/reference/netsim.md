# NetSim — 网络状态模拟

## 文件索引

| File | 职责 |
|---|---|
| `svr/cmd/framesync/netsim.go` | NetSimConfig + simConn + GlobalNetSim |

## 用途

开发/测试环境下模拟网络不良状况（延迟、抖动、丢包），验证客户端重连、插值、预测等逻辑。
仅影响 S→C 方向（服务器发送给客户端的消息）。

## NetSimConfig

```go
type NetSimConfig struct {
    LatencyMs   atomic.Int32  // 基础延迟 (ms)
    JitterMs    atomic.Int32  // 抖动范围 (ms)，实际延迟 = Latency ± rand(Jitter)
    LossPercent atomic.Int32  // 丢包率 (0-100)
    Enabled     atomic.Int32  // 0=关闭, 1=启用
}

var GlobalNetSim NetSimConfig  // 全局单例
```

所有字段使用 `atomic.Int32`，支持运行时无锁修改。

## simConn — 装饰器

```go
type simConn struct {
    inner   framesync.PlayerConn
    pending int32  // 当前排队的延迟发送数
}
```

实现 `PlayerConn` 接口。`Send()` 流程：

```
1. Enabled == 0 → 直接 inner.Send()（零开销）
2. rand(100) < LossPercent → 丢弃消息（return nil）
3. delay = LatencyMs + rand(-JitterMs, +JitterMs)
4. delay <= 0 → 直接 inner.Send()
5. pending >= simPendingMax(10000) → 直接 inner.Send()（安全阀）
6. atomic.Add(&pending, 1) → time.AfterFunc(delay, inner.Send)
```

### 安全阀

`simPendingMax = 10000`：防止高延迟 + 高帧率导致 timer goroutine 堆积。
超过阈值后降级为直接发送。

## 控制方式

### Admin HTTP

```
GET  /netsim → 返回当前配置 {latency_ms, jitter_ms, loss_percent, enabled}
POST /netsim → 设置配置（JSON body，部分更新）
```

### Admin WebSocket RPC

```
GMEnvelope{Type: "rpc", Topic: "netsim", Payload: {latency, jitter, loss, enabled}}
```

### WebSocket 推送

`netsim` topic 每 5 秒推送当前配置给订阅的 GM 客户端。

## Unity 端集成

Unity ServerWindow Dashboard Tab 提供滑块控制：
- Latency: 0~500ms
- Jitter: 0~200ms
- Loss: 0~50%
- Enable/Disable 开关

实时通过 WebSocket RPC 下发到服务器。
