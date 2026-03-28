# GM 工具参考

> 一句话：通过 HTTP Admin API + Unity Editor 面板，对运行中的帧同步服务器进行**监控、控制、诊断**。

## 1. 启用（1 分钟）

### 服务器侧

```yaml
# config.yaml
adminAddr: ":9091"        # Admin HTTP 端口（空 = 不启用）
adminToken: "your-secret" # Bearer Token 鉴权（空 = 不鉴权）
```

或命令行：

```bash
go run ./cmd/framesync/ -config=cmd/framesync/config.yaml
# 或
go run ./cmd/framesync/ -admin=:9091 -admin-token=your-secret
```

验证：`curl http://127.0.0.1:9091/health`

### Unity 侧

1. 安装 GM 工具包（UPM）：
   ```
   https://github.com/boomlulu/BoomNetwork.git?path=unity/com.boom.boomnetwork.gm#dev1.0
   ```
2. 菜单 **BoomNetwork → Server Window**
3. Config 区填 Admin URL + Admin Token

---

## 2. 能力总览

| 类别 | 能力 | 端点 | 鉴权 |
|------|------|------|------|
| **监控** | 服务器存活 | `GET /health` | 免鉴权 |
| | 流量统计（Game + GM） | `GET /stats` | 需鉴权 |
| | 最近 100 条网络消息 | `GET /messages` | 需鉴权 |
| | 房间列表 + 玩家详情 | `GET /rooms` | 需鉴权 |
| **控制** | 踢出玩家 | `POST /kick/{pid}` | 需鉴权 |
| | 强制停止房间 | `POST /rooms/stop/{id}` | 需鉴权 |
| **控制** | 网络状态模拟（延迟/抖动/丢包） | `GET/POST /netsim` | 需鉴权 |
| | 日志级别热调 | `GET/POST /log-level` | 需鉴权 |
| | 配置热重载 | `POST /config/reload` | 需鉴权 |
| | 创建房间 | `POST /rooms/create` | 需鉴权 |
| | 强制销毁房间 | `POST /rooms/kill/{id}` | 需鉴权 |
| **诊断** | 房间深度检视 | `GET /rooms/inspect/{id}` | 需鉴权 |
| | 单玩家详情 + 最近消息 | `GET /players/{pid}` | 需鉴权 |
| | 服务器性能（内存/GC/goroutine） | `GET /perf` | 需鉴权 |
| | 玩家消息速率 Top 20 | `GET /rates` | 需鉴权 |

---

## 3. 端点详解

### 监控

#### GET /health

服务器是否在线。健康检查探针用，**不需要鉴权**。

```json
{"status":"ok","rooms":3,"players":12,"uptime":"1h23m45s"}
```

#### GET /stats

流量统计，区分游戏协议流量（TCP/KCP）和 GM 管理流量（HTTP）。

```json
{
  "game_rx_total": 123456,  "game_tx_total": 234567,
  "game_rx_1min":  1234,    "game_tx_1min":  2345,
  "game_rx_5sec":  123,     "game_tx_5sec":  234,
  "gm_rx_total":   5678,    "gm_tx_total":   6789,
  "gm_rx_1min":    567,     "gm_tx_1min":    678,
  "gm_rx_5sec":    56,      "gm_tx_5sec":    67
}
```

- `game_*`：TCP/KCP 协议字节（帧推送、心跳、输入）
- `gm_*`：HTTP Admin 请求/响应字节
- `*_1min` / `*_5sec`：滑动窗口（环形缓冲区 60 个 1 秒槽）

#### GET /messages?limit=100

最近 N 条网络消息（时间倒序）。

```json
[
  {"ts":1711324800000,"dir":"tx","cmd":6,"name":"PushFrames","pid":1,"size":6,"detail":""},
  {"ts":1711324800000,"dir":"rx","cmd":7,"name":"Heartbeat","pid":1,"size":0,"detail":""},
  {"ts":1711324799500,"dir":"tx","cmd":10,"name":"ReconnectRsp","pid":2,"size":48,"detail":"status=ok"}
]
```

| 字段 | 说明 |
|------|------|
| `dir` | `rx` = 服务器收到（C→S），`tx` = 服务器发出（S→C） |
| `name` | 协议命令名（人可读） |
| `pid` | 涉及的玩家 ID（0 = 未知） |
| `detail` | 关键消息的 payload 解码摘要（Reconnect / JoinRoom / StartFrameSync / ReconnectRsp） |

#### GET /rooms

所有房间详情 + 每个玩家的在线/离线状态。

```json
[{
  "id": 1,
  "running": true,
  "paused": false,
  "frame_number": 1234,
  "frame_rate": 20,
  "max_players": 4,
  "online_count": 3,
  "total_players": 4,
  "players": [
    {"id": 1, "state": 0},
    {"id": 2, "state": 0},
    {"id": 3, "state": 0},
    {"id": 4, "state": 1, "disconnect_time": 1711324750000}
  ]
}]
```

- `state`：0 = 在线，1 = 离线（等待重连）
- `disconnect_time`：离线时间戳（unix ms），在线时为 0

---

### 控制

#### POST /kick/{pid}

踢出指定玩家。从房间移除 + 断开 TCP 连接 + 广播 PlayerLeft。

```bash
curl -X POST -H "Authorization: Bearer your-secret" http://127.0.0.1:9091/kick/4
```

```json
{"ok":true,"kicked":4,"room":1}
```

#### POST /rooms/stop/{id}

强制停止房间的帧同步。停止 Tick → 广播 StopFrameSync → 清理玩家映射 → 移除房间。

```bash
curl -X POST -H "Authorization: Bearer your-secret" http://127.0.0.1:9091/rooms/stop/1
```

```json
{"ok":true,"stopped":1}
```

---

#### GET/POST /netsim

模拟网络延迟、抖动、丢包（S→C 出站方向）。

```bash
# 查看当前配置
curl http://127.0.0.1:9091/netsim

# 开启：100ms 延迟 + 20ms 抖动 + 5% 丢包
curl -X POST http://127.0.0.1:9091/netsim \
  -d '{"enabled":true,"latency_ms":100,"jitter_ms":20,"loss_percent":5}'

# 关闭
curl -X POST http://127.0.0.1:9091/netsim -d '{"enabled":false}'
```

```json
{
  "enabled": true,
  "latency_ms": 100,
  "jitter_ms": 20,
  "loss_percent": 5,
  "stats_dropped": 12,
  "stats_delayed": 456
}
```

Unity ServerWindow Control Tab 中也有滑块控制面板，实时调整。

#### GET/POST /log-level

运行时热调日志级别。

```bash
# 查看当前级别
curl http://127.0.0.1:9091/log-level
# => {"level":"INFO"}

# 修改为 DEBUG
curl -X POST http://127.0.0.1:9091/log-level -d '{"level":"debug"}'
# => {"ok":true,"level":"debug"}
```

可选值：`DEBUG` / `INFO` / `WARN` / `ERROR`。等效于发送 `SIGHUP` 后重新读取配置。

#### POST /config/reload

运行时热重载配置文件（等效 `SIGHUP`）。热更新 `logLevel` 和 `maxMessageSize`。

```bash
curl -X POST http://127.0.0.1:9091/config/reload
# => {"ok":true,"level":"INFO"}
```

#### POST /rooms/create?max_players=N&match_key=K

手动创建房间。

```bash
curl -X POST "http://127.0.0.1:9091/rooms/create?max_players=4&match_key=pvp"
# => {"ok":true,"room_id":5}
```

#### POST /rooms/kill/{id}

强制销毁房间：立即断开所有玩家连接，不广播 StopFrameSync。

```bash
curl -X POST http://127.0.0.1:9091/rooms/kill/1
# => {"ok":true,"killed":1}
```

---

### 诊断

#### GET /rooms/inspect/{id}

单房间深度检视。返回帧缓冲、快照、实体权威、KV 数据仓全量信息。

```bash
curl http://127.0.0.1:9091/rooms/inspect/1
```

```json
{
  "ok": true, "id": 1, "running": true, "paused": false,
  "frame_number": 1234, "frame_rate": 20,
  "frame_buffer_len": 800, "frame_buffer_cap": 2400,
  "oldest_buffered_frame": 200,
  "snapshot_frame": 1100, "snapshot_size_bytes": 2345,
  "snapshot_stale_frames": 34,
  "data_version": 12,
  "entity_authority": [{"entity_id":1,"owner_id":2}],
  "kv_entries": [{"player_id":1,"key":0,"value":"AQID"}]
}
```

也可通过 WS RPC `inspect_room` (`{room_id: 1}`) 获取 MessagePack 版本。

#### GET /players/{pid}

单个玩家的完整状态 + 该玩家最近 20 条消息。

```json
{
  "id": 4,
  "online": true,
  "room": 1,
  "room_running": true,
  "room_frame": 1234,
  "state": 0,
  "recent_messages": [
    {"ts":1711324800000,"dir":"rx","cmd":5,"name":"FrameInput","pid":4,"size":8}
  ]
}
```

适用场景：玩家反馈异常时，快速定位其连接状态和最近行为。

#### GET /perf

服务器运行时性能快照。

```json
{
  "goroutines": 42,
  "heap_mb": 12.34,
  "sys_mb": 45.67,
  "gc_count": 15,
  "gc_pause_us": 234,
  "rooms": 3,
  "players": 12
}
```

| 字段 | 说明 |
|------|------|
| `goroutines` | 当前 goroutine 数（过高可能有泄漏） |
| `heap_mb` | Go 堆内存（MB） |
| `gc_pause_us` | 最近一次 GC 暂停（微秒） |

#### GET /rates

最近 5 秒消息速率 Top 20 玩家。用于识别消息洪水（外挂/异常客户端）。

```json
[
  {"pid":4,"msg_5sec":150},
  {"pid":2,"msg_5sec":100}
]
```

正常值参考：20fps 帧同步 × 1 条输入/帧 = 100 条/5 秒。显著高于此值应关注。

---

## 4. Unity ServerWindow

安装 GM 包后菜单 **BoomNetwork → Server Window** 打开，五个 Tab（按观察者视角组织）：

| Tab | 定位 | 内容 |
|-----|------|------|
| **Monitor** | 纯观察 | Game/GM 流量分离 · Runtime Performance（Goroutines/Heap/GC，趋势箭头）· Hot Players Top N（消息速率高亮 + Kick） |
| **Messages** | 线级观察 | 最近 200 条消息表格。Cmd/Dir/Player/Room/MatchKey 多维过滤、隐藏心跳、暂停/恢复、导出剪贴板 |
| **Rooms** | 实体管理 | 房间列表 + fps/MatchKey/Paused 标签 + 在线/离线状态。Create/Stop/Kill Room，Kick Player |
| **Control** | 纯操作 | NetSim 滑块 · Log Level 下拉 · Reload Config · 服务器配置 · Start/Stop（本地 + SSH）· 手动命令 |
| **Deploy** | 部署流水线 | 多 Profile（Local/SSH）· Build → Upload → Stop → Start → Verify · systemd .service 生成 |

### 配置项（EditorPrefs 跨会话持久化）

| 项 | 默认值 | 说明 |
|----|--------|------|
| Server Path | `/Users/boom/Demo/BoomNetwork/svr` | Go 服务器源码路径 |
| Config File | `cmd/framesync/config.yaml` | 服务器配置文件 |
| Admin URL | `http://127.0.0.1:9091` | Admin HTTP 地址 |
| Admin Token | (空) | Bearer Token，与服务器 adminToken 一致 |

---

## 5. 鉴权

| 规则 | 说明 |
|------|------|
| `/health` 免鉴权 | 健康检查探针需要无障碍访问 |
| 其余端点需 `Authorization: Bearer {token}` | token 不匹配返回 `401 {"error":"invalid or missing token"}` |
| `adminToken: ""` 时不启用鉴权 | 开发环境可不设 token |
| 生产环境必须设置 token | 否则任何人可以踢人/停房间 |

---

## 6. 架构

```
Unity Editor                      Go Server :9091
─────────────                     ─────────────────
ServerWindow                      admin.go + admin_ws.go
  Tab 0 Monitor  ←── WS push ──→  health / stats / perf / rates
  Tab 1 Messages ←── WS push ──→  messages (实时)
  Tab 2 Rooms    ←── WS push ──→  rooms (2s)
       Kick/Stop/Kill ── WS RPC ──→ kick / stop_room / kill_room / create_room
  Tab 3 Control  ── HTTP POST ──→  /log-level / /config/reload / /netsim
       Start/Stop ── SSH/Local ──→ systemctl / go run
  Tab 4 Deploy   ── go build + SCP + SSH ──→ 编译上传启动

AdminWsClient.cs (主通道)    AdminClient.cs (HTTP fallback)
  MessagePack 二进制帧           JSON REST, Bearer Token auth
  订阅 7 个 topic                 2s 轮询（WS 断开时启用）
```

数据流：

```
Game 协议 (TCP :9000)         Admin HTTP (:9091)
  rxHandler → GameStats         gmTrafficMiddleware → GmStats
  txStats   → GameStats         countingWriter → GmStats
  statsConn → GameStats
       ↓                              ↓
    MsgLog (100 条环形)           /stats JSON 响应
    PlayerRates (per-player)
```
