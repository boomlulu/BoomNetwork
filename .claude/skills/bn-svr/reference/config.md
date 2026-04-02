# Config — 配置系统

## 文件索引

| File | 职责 |
|---|---|
| `svr/cmd/framesync/config.go` | ServerConfig 结构 + YAML 加载/保存 + 默认值 |
| `svr/configs/config.dev.yaml` | 开发环境配置模板 |

## 优先级（高 → 低）

```
1. 环境变量: BOOM_ADMIN_TOKEN, BOOM_AUTH_TOKEN
2. YAML 配置文件: -config <path>
3. CLI 命令行标志
4. DefaultConfig() 内置默认值
```

## ServerConfig 全字段

| 字段 | YAML Key | CLI Flag | 默认值 | 说明 |
|---|---|---|---|---|
| Addr | `addr` | `-addr` | `:9000` | 游戏服务器监听地址 |
| Proto | `proto` | `-proto` | `tcp` | 传输协议: tcp / kcp |
| AuthToken | `authToken` | `-auth-token` | `""` | 客户端认证 Token（空=无认证） |
| MetricsAddr | `metricsAddr` | `-metrics-addr` | `:9090` | Prometheus 监听地址 |
| AdminAddr | `adminAddr` | `-admin-addr` | `:9091` | Admin HTTP+WS 监听地址 |
| AdminToken | `adminToken` | `-admin-token` | `""` | Admin Bearer Token |
| PlayersPerRoom | `playersPerRoom` | `-players-per-room` | `4` | 默认房间最大玩家数 |
| FrameRate | `frameRate` | `-frame-rate` | `20` | 帧率 (fps) |
| FrameBufferSize | `frameBufferSize` | `-frame-buffer-size` | `2400` | 帧环形缓冲大小 |
| SnapshotIntervalFrames | `snapshotIntervalFrames` | `-snapshot-interval` | `100` | 快照上传间隔帧数 |
| QuickReconnectMaxMs | `quickReconnectMaxMs` | `-quick-reconnect-max-ms` | `5000` | Stage 1 重连超时(ms) |
| DisconnectKeepSec | `disconnectKeepSec` | `-disconnect-keep-sec` | `120` | 断线保留时间(秒) |
| RoomCleanupSec | `roomCleanupSec` | — | `30` | 空房间清理延迟(秒) |
| MaxRooms | `maxRooms` | `-max-rooms` | `0` | 最大房间数（0=无限） |
| MaxConnections | `maxConnections` | `-max-connections` | `0` | 最大连接数（0=无限） |
| MaxMessageSize | `maxMessageSize` | `-max-message-size` | `65536` | 最大消息大小(字节) |
| MaxMessagesPerSec | `maxMessagesPerSec` | `-max-messages-per-sec` | `100` | 每连接消息速率限制 |
| AllowedOrigins | `allowedOrigins` | — | `[]` | WS Origin 白名单 |
| LogLevel | `logLevel` | `-log-level` | `info` | 日志级别 |

## 热重载

支持两种触发方式：
1. `SIGHUP` 信号
2. `POST /config/reload` Admin 端点

**仅以下字段支持热重载：**
- `logLevel` → 立即切换 slog 级别
- `maxMessageSize` → 更新 `codec.MaxMessageSize` 全局变量

其他字段需要重启服务器生效。

## 生成默认配置

```bash
./framesync -gen-config config.yaml
```

生成带注释的完整 YAML 模板，所有字段使用默认值。

## 环境变量覆盖

```bash
export BOOM_AUTH_TOKEN="game-secret"     # 覆盖 authToken
export BOOM_ADMIN_TOKEN="admin-secret"   # 覆盖 adminToken
```

适用场景：容器部署（Docker/K8s secrets）、systemd EnvironmentFile。
优先级最高，即使 YAML 中有配置也会被覆盖。
