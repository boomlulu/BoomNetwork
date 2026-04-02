# Admin/GM — HTTP REST + WebSocket Hub

## 文件索引

| File | 职责 |
|---|---|
| `svr/cmd/framesync/admin.go` | HTTP REST 端点 + 中间件 |
| `svr/cmd/framesync/admin_ws.go` | WebSocket GMHub + GMConn + 话题推送 |
| `svr/cmd/framesync/admin_ws_msg.go` | WS MessagePack 消息类型定义 |
| `svr/cmd/framesync/stats.go` | TrafficTracker + msgRing + playerRate |

## HTTP REST 端点

默认监听 `:9091`。除 `/health` 外全部需要 `Authorization: Bearer <token>`。

| Method | Path | Auth | 说明 |
|---|---|---|---|
| GET | `/health` | No | 健康检查：status/rooms/players/uptime/buildHash/goVersion |
| GET | `/stats` | Yes | 流量统计：rx/tx total + 1min + 5sec 窗口 |
| GET | `/messages?limit=N` | Yes | 最近 N 条网络消息（max 500）from msgRing |
| GET | `/rooms` | Yes | 全部房间列表 + 玩家状态/帧号/暂停状态 |
| POST | `/kick/{pid}` | Yes | 踢出玩家：移除房间 + 关闭连接 + 广播 PlayerLeft |
| POST | `/rooms/stop/{id}` | Yes | 优雅停止房间：room.Stop() + 清理 + 移除 |
| POST | `/rooms/kill/{id}` | Yes | 强制杀房间：关闭所有玩家连接 + Stop |
| POST | `/rooms/create?max_players=N&match_key=K` | Yes | Admin 创建房间 |
| GET | `/players/{pid}` | Yes | 玩家详情：在线状态/房间/状态/最近消息 |
| GET | `/perf` | Yes | Go 运行时：goroutines/heap MB/GC count/GC pause（5s 缓存） |
| GET | `/rates` | Yes | Top 20 玩家消息速率（5s 窗口） |
| GET/POST | `/netsim` | Yes | 获取/设置网络模拟参数 |
| GET/POST | `/log-level` | Yes | 获取/设置 slog 日志级别 |
| POST | `/config/reload` | Yes | 热重载配置文件（仅 logLevel + maxMessageSize） |

## WebSocket GM Channel

路径：`/ws`（同一 `:9091` 端口）

### 连接生命周期

```
WS Upgrade → 5s 内必须 auth → auth 成功加入 GMHub
  → 订阅 topics → 接收定期推送 + 实时消息
  → 90s 读超时 + 45s 服务端 Ping
  → 断开 → 从 GMHub 移除
```

### 消息格式

二进制 MessagePack 编码的 `GMEnvelope`：

```go
type GMEnvelope struct {
    Type    string `msgpack:"type"`     // 消息类型
    ID      string `msgpack:"id"`       // RPC 请求 ID
    Topic   string `msgpack:"topic"`    // 话题名
    Payload []byte `msgpack:"payload"`  // MessagePack 编码的负载
}
```

### C→S 消息类型

| Type | 说明 |
|---|---|
| `auth` | 认证：Payload = token 字符串 |
| `sub` | 订阅话题：Topic = 话题名 |
| `unsub` | 取消订阅 |
| `rpc` | 远程调用：Topic = 操作名，Payload = 参数 |
| `ping` | 心跳 |

### S→C 消息类型

| Type | 说明 |
|---|---|
| `auth_ok` | 认证成功 |
| `auth_err` | 认证失败 |
| `push` | 话题推送数据 |
| `rsp` | RPC 响应 |
| `err` | RPC 错误 |
| `pong` | 心跳回复 |

### 话题推送频率

| Topic | 频率 | 内容 |
|---|---|---|
| `health` | 2s | 同 /health 端点 |
| `stats` | 2s | 同 /stats 端点 |
| `rooms` | 2s | 同 /rooms 端点 |
| `perf` | 5s | 同 /perf 端点 |
| `rates` | 5s | 同 /rates 端点 |
| `netsim` | 5s | 同 /netsim 端点 |
| `messages` | 实时 | 每条网络消息实时推送 |

### RPC 操作

| Topic | 参数 | 说明 |
|---|---|---|
| `kick` | `{pid: int32}` | 踢出玩家 |
| `stop_room` | `{id: int32}` | 优雅停止房间 |
| `kill_room` | `{id: int32}` | 强制杀房间 |
| `create_room` | `{max_players: int, match_key: string}` | 创建房间 |
| `netsim` | `{latency, jitter, loss, enabled}` | 设置网络模拟 |

## GMHub 架构

```go
type GMHub struct {
    mu    sync.RWMutex
    conns map[*GMConn]struct{}
}
```

话题订阅使用 `uint32` 原子位掩码，每个 topic 占一个 bit。
`Publish(topic, data)` 遍历所有连接，仅发送给订阅了该 topic 的连接。
每个 GMConn 有 64-cap 的 `sendCh`，满则跳过（防止慢消费者阻塞）。

## Stats 组件

### TrafficTracker

60-slot 环形缓冲，每秒记录 RX/TX 字节数。提供 1 分钟和 5 秒窗口统计。

### msgRing

100-entry 环形日志，记录最近的网络消息（方向/命令/大小/时间/玩家ID）。
可选 `notifyCh` 用于 WebSocket 实时推送。

### playerRate

Per-player 60-slot 环形，记录每秒消息数。`TopPlayers(N)` 返回消息速率最高的 N 个玩家。
