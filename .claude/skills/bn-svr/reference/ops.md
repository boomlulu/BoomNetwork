# Ops — systemd / Docker / Prometheus / 信号处理

## 文件索引

| File | 职责 |
|---|---|
| `svr/cmd/framesync/main.go` | systemd notify + 信号处理 + 优雅关闭 |
| `svr/framesync/metrics.go` | Prometheus 指标定义 |
| `svr/Dockerfile` | 多阶段构建 |
| `svr/docker-compose.yml` | 端口映射 + 配置挂载 |

## systemd 集成

`sdNotifyReady()` — 当 `NOTIFY_SOCKET` 环境变量存在时（systemd `Type=notify`），发送 `READY=1`。
调用时机：所有监听器启动完成后。
无 NOTIFY_SOCKET 时无操作（Docker/裸进程部署）。

配合 systemd unit file 使用：
```ini
[Service]
Type=notify
ExecStart=/usr/local/bin/framesync -config /etc/boomnetwork/config.yaml
```

## 信号处理

| Signal | 行为 |
|---|---|
| SIGINT / SIGTERM | 优雅关闭（30s 超时） |
| SIGHUP | 热重载配置（仅 logLevel + maxMessageSize） |

### 优雅关闭流程

```
1. 广播 CmdServerShutdown 给所有已连接玩家
2. 取消 Admin 服务器 context
3. roomMgr.StopAll()（每个房间广播 CmdStopFrameSync）
4. server.Close()（关闭监听器 + 所有连接）
5. server.Wait()（30s 超时等待所有连接关闭）
```

## Docker 部署

### Dockerfile（多阶段构建）

```
Stage 1: golang:1.24-alpine
  → go build -ldflags="-s -w" → /app/framesync

Stage 2: alpine:3.20
  → COPY --from=builder /app/framesync
  → EXPOSE 9000 9090 9091
  → HEALTHCHECK: curl -sf http://localhost:9091/health (30s interval, 5s timeout, 3 retries)
```

CGO_ENABLED=0，静态链接。`-s -w` 去除符号表和调试信息。

### docker-compose.yml

```yaml
ports:
  - "9000:9000"   # Game (TCP/KCP)
  - "9090:9090"   # Prometheus metrics
  - "9091:9091"   # Admin HTTP+WS
volumes:
  - ./configs/config.dev.yaml:/etc/boomnetwork/config.yaml
```

## Prometheus 指标

默认监听 `:9090/metrics`。全部使用 `promauto` 自动注册。

### 连接指标

| Metric | Type | 说明 |
|---|---|---|
| `boom_connections_total` | Counter | 累计连接数 |
| `boom_connections_current` | Gauge | 当前连接数 |

### 房间指标

| Metric | Type | 说明 |
|---|---|---|
| `boom_rooms_current` | Gauge | 当前房间数 |
| `boom_room_lifetime_seconds` | Histogram | 房间存活时长 (1s~3600s) |

### 帧同步指标

| Metric | Type | 说明 |
|---|---|---|
| `boom_frames_pushed_total` | Counter | 累计推送帧数 |
| `boom_frame_broadcast_latency_seconds` | Histogram | 帧广播延迟 (1ms~100ms) |
| `boom_inputs_received_total` | Counter | 累计收到的输入数 |

### 流量指标

| Metric | Type | 说明 |
|---|---|---|
| `boom_bytes_sent_total` | Counter | 累计发送字节数 |
| `boom_bytes_received_total` | Counter | 累计接收字节数 |

### 重连指标

| Metric | Type | 说明 |
|---|---|---|
| `boom_reconnect_success_total` | Counter | 重连成功次数 |
| `boom_reconnect_fail_total` | Counter | 重连失败次数 |

### 快照指标

| Metric | Type | 说明 |
|---|---|---|
| `boom_snapshot_size_bytes` | Gauge | 最新快照大小 |

### 安全/错误指标

| Metric | Type | 说明 |
|---|---|---|
| `boom_message_errors_total` | Counter | 消息处理错误 |
| `boom_room_panics_total` | Counter | Room tickLoop panic 次数 |
| `boom_auth_failures_total` | Counter | 认证失败次数 |
| `boom_rate_limited_total` | Counter | 限流断开次数 |

## Build Version 注入

```go
var BuildHash string   // -ldflags "-X main.BuildHash=xxx"
var BuildTime string   // -ldflags "-X main.BuildTime=xxx"
```

启动时日志输出，`/health` 端点返回。

## Panic Recovery

`tickLoop()` 内 `defer recover()`：
1. 广播 CmdStopFrameSync
2. 清理玩家状态（playerRoomMap/playerConnMap）
3. 调用 `room.OnPanic` 回调（从 RoomManager 移除）
4. `boom_room_panics_total++`

单个 Room panic 不影响其他 Room 和服务器运行。
