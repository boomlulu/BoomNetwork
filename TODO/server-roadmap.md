# Go 服务器路线图

> **定位**：BoomNetwork 帧同步框架的官方 Go 服务器实现
>
> **核心哲学**：自权威、不回滚、冲突仲裁 — 详见 [doc/core-philosophy.md](../doc/core-philosophy.md)
>
> **当前阶段**：Phase A-D 全部完成（26 项），服务器达到 **Beta 级** — 能跑、能看、能扛、能防、能运维

---

## 服务器概览

| 指标 | 数据 |
|------|------|
| 代码规模 | **6,500+ 行**生产代码，44 个源文件 |
| 测试 | **48 项**通过（帧同步集成 + Codec 跨语言 + KCP Echo + 协议兼容） |
| 协议命令 | **12** Core + **22** Extended = **34** 条协议命令，3 层 CmdType 分级 |
| Prometheus 指标 | **16 项**（11 Counter + 3 Gauge + 2 Histogram），零僵尸指标 |
| Admin API | **15 个** HTTP 端点 + WebSocket 实时推送 |
| 日志 | 全量 `log/slog` 结构化 JSON，**零残留** `log.Printf`，运行时级别切换 |
| 安全 | env token 覆盖 + 鉴权失败断连 + WS Origin 白名单 + per-IP 连接频率限制 |
| 传输协议 | TCP（NoDelay + KeepAlive）+ KCP（低延迟调参），统一限流 + per-IP 限流 |
| 部署 | systemd（Type=notify + sd_notify）+ Docker（HEALTHCHECK）+ GM Deploy 一键发布 |
| 生产验证 | 腾讯云 124.220.6.174，`/health` 返回 buildHash/buildTime/goVersion |

---

## 技术亮点

### 帧同步核心 — 零分配热路径

| 设计 | 实现 |
|------|------|
| Ring buffer 帧缓存 | 固定大小环形缓冲区，`stepFrame()` 复用 slot 的 `[]byte`，不触发 GC |
| 编码缓冲区复用 | `frameBuf` + `broadcastSlice` 在锁内复用，锁外广播 |
| Router 无锁分发 | `Freeze()` 冻结路由表到无锁快照，每条消息省一次 `RLock` |
| sync.Pool Codec | 消息编解码器通过 `sync.Pool` 复用，跨语言兼容（C# ↔ Go fixture 测试验证） |

### 重连机制 — 二级降级 + CAS 防竞态

```
快速重连（ring buffer 回放）
  ↓ 缓冲区过期
快照重连（全量快照 + catchup frames）
  ↓ 房间已清理
重连失败（明确错误码）
```

- 重连时**关闭旧连接** + 清理旧 connID 映射
- `onClientDisconnect` **CAS 检查** `playerConnMap` 是否仍指向当前 conn，防止重连后旧连接断开覆盖新映射
- Prometheus `ReconnectSuccess` / `ReconnectFail` 计数，运维可监控重连健康度

### 容错 — panic 自愈 + 优雅关闭

| 场景 | 处理 |
|------|------|
| Room tickLoop panic | `recover()` → 广播 `CmdStopFrameSync` → 清空 players → `OnPanic` 回调清理全局映射 → `RemoveRoom` |
| 服务器关闭 | 广播 `CmdServerShutdown(12)` → cancel admin → `StopAll` → `server.Close()` → `WaitGroup` 30s 超时排空 |
| 连接死亡 | TCP: KeepAlive 30s + ReadDeadline 60s；KCP: ReadDeadline 60s。静默连接 60s 内自动断开 |
| 容量溢出 | `maxRooms` + `maxConnections` 可配置，`acceptLoop` 超限直接拒绝关闭 |

### 可观测性 — 16 项指标 + slog JSON + Grafana

**Prometheus 指标全景**：

| 类别 | 指标 | 类型 |
|------|------|------|
| 连接 | `connections_total` / `connections_current` | Counter / Gauge |
| 房间 | `rooms_current` / `room_lifetime_seconds` | Gauge / Histogram |
| 帧同步 | `frames_pushed` / `frame_broadcast_latency_seconds` / `inputs_received` | Counter / Histogram / Counter |
| 流量 | `bytes_sent` / `bytes_received` | Counter |
| 重连 | `reconnect_success` / `reconnect_fail` | Counter |
| 快照 | `snapshot_size_bytes` | Gauge |
| 错误 | `message_errors` / `room_panics` / `auth_failures` / `rate_limited` | Counter |

**日志**：全量 `log/slog` 结构化 JSON 输出，支持运行时级别切换（`POST /log-level`），`slog.LevelVar` 原子操作。

**Grafana**：`deploy/grafana/boomnetwork-dashboard.json` 提供 5 行 15+ 查询面板，开箱即用。

### GM 工具 — 15 端点 + WebSocket 实时调试

| 能力 | 端点 |
|------|------|
| 健康检查（免鉴权） | `GET /health` |
| 流量统计（Game/GM 分离） | `GET /stats` |
| 消息日志（100 条 + payload 解码） | `GET /messages` |
| 房间管理 | `GET /rooms` / `POST /rooms/stop` / `POST /rooms/kill` / `POST /rooms/create` |
| 踢人 | `POST /kick/{id}` |
| 玩家详情 | `GET /players/{id}` |
| 性能指标（STW 缓存 5s） | `GET /perf` |
| 消息速率 Top N | `GET /rates` |
| 网络模拟（延迟/抖动/丢包） | `POST /netsim` |
| 日志级别切换 | `POST /log-level` |
| 配置热重载 | `POST /config/reload` |
| WebSocket 实时推送 | `GET /ws`（7 topics） |

### 协议设计 — 三层 CmdType 分级

```
Core (0-15)       — 3B 包头，高频帧同步命令（输入/推帧/心跳/重连）
Extended (uint16)  — 5B 包头，房间/快照/实体/状态同步
Game (uint32)      — 7B 包头，用户自定义透传，服务器零解析
```

- JoinRoom **5 种错误码**（NotFound/Full/NotBound/BadData/Success），向后兼容
- 实体权威 grant/release/bulk-release-on-disconnect
- 轻量状态同步：StateMessage 转发 + DataMessage KV（版本号增量广播 + 全量同步）

### 安全 — 四层防护

| 层级 | 机制 | 实现 |
|------|------|------|
| **连接层** | Per-IP 频率限制 | `acceptLoop` 中 per-IP 计数，超限（10 conn/sec/IP）直接拒绝关闭 |
| **消息层** | Per-conn 速率限制 | TCP/KCP 统一 `RateLimiter`，超限断连 |
| **鉴权层** | Token 验证 + 失败断连 | `BOOM_ADMIN_TOKEN` env 优先覆盖；SessionBind 失败后 100ms 延迟 `conn.Close()` |
| **WebSocket** | Origin 白名单 | `allowedOrigins` 配置项，空则允许所有（向后兼容） |

### 运维 — 一键部署 + 全链路可观测

| 能力 | 实现 |
|------|------|
| systemd | `deploy/boomnetwork.service`（Type=notify），`sd_notify(READY=1)` 就绪通知 |
| Docker | `EXPOSE 9000/9090/9091`，`HEALTHCHECK curl /health` |
| /health | 返回 status/rooms/players/uptime/**buildHash**/**buildTime**/**goVersion** |
| 配置热重载 | `SIGHUP` + `POST /config/reload`，热更 LogLevel/MaxMessageSize |

### 性能优化

| 优化 | 效果 |
|------|------|
| Router `Freeze()` | Dispatch 从 RLock 查表 → 无锁直接查表 |
| `/perf` STW 缓存 | `ReadMemStats` 从每次请求触发 STW → 5s 缓存 |
| netsim 积压保护 | `time.AfterFunc` 从无限堆积 → 10000 上限降级直发 |
| `RoomManager.nextID` | 双重同步（mutex + atomic）→ 单 mutex |
| playerRate 清理 | 断线时 `delete` 防止 map 无限增长 |

---

## 协作方法论

> 本次开发实践沉淀的协作技巧

### 审计驱动开发

**先审计再动手**。每个 Phase 开始前，用 Explore agent 逐项核实代码现状，精确到文件:行号。避免修"已经修过的"或漏"以为没问题的"。

实例：Phase A 开始前审计 S1-S6，发现 `RoomsCurrent` 比文档描述更严重 —— 不是"只增不减"而是"**从未调用**"，6 个创建/销毁路径全部遗漏。

### 四象限优先级矩阵

用艾森豪威尔矩阵（紧急/重要）对 32 项任务分类：
- **紧急且重要**（S1-S6）：先止血，不留定时炸弹
- **紧急不重要**（S23-S26）：快速性能优化，改动小见效快
- **重要不紧急**（S7-S22）：按 Phase C/D 分批推进
- **不紧急不重要**（S27-S32）：远期演进，不分散当前精力

### 并行 Agent 加速

| 策略 | 场景 | 效果 |
|------|------|------|
| 3 agent 并行迁移 slog | S7：transport/session + framesync + cmd/framesync 三个包无交叉 | 70+ 处 log 调用并行修改，零冲突 |
| 3 agent 并行实现 Phase C | S8+S12 / S9+S14 / S11+S13 按依赖分组 | 6 项 feature 同时推进 |
| 2 agent 并行实现 Phase D | S15-S18 安全 / S19-S22 运维 按职责分组 | 8 项 feature 同时推进 |
| 机械改动交给 agent | slog 迁移、指标接入等模式化修改 | 主对话专注架构决策和集成验证 |

### 文档即代码

- **roadmap 实时更新**：每完成一个 Phase，立即更新"已完成"列表 + "现状诊断"评级
- **待做项带"现状"列**：不只写"要做什么"，还写"现在是什么样" — 精确到 `admin_ws.go:216 CheckOrigin return true`
- **交叉引用**：framework-roadmap 的 N14/N15 标记 `→ server-roadmap S3/S4`，避免重复记录

---

## 已完成 Phase 总览

### Phase A — 止血（S1-S6，2026-03-26）

6 项 P0/P1 bug fix：Prometheus 指标修复、KCP 限流对齐、Room panic 自愈、重连竞态修复、JoinRoom 错误码、playerRate 泄漏。

### Phase B — 性能（S23-S26，2026-03-26）

4 项性能优化：Router 无锁、atomic 冗余清理、/perf STW 缓存、netsim 积压保护。

### Phase C — 可观测 + 可靠性（S7-S14，2026-03-26）

8 项 feature：slog 结构化日志、运行时日志级别、5 项新指标（2 Histogram + 2 Counter + 1 Gauge）、Grafana Dashboard、优雅关闭（WaitGroup + ServerShutdown 广播）、配置热重载（SIGHUP + API）、连接健康检测文档、容量上限（maxRooms + maxConnections）。

---

### Phase D — 安全 + 运维（S15-S22，2026-03-26）

8 项安全加固 + 运维完善：

| # | 任务 | 方案 |
|---|------|------|
| S15 | Admin Token 环境变量化 | `BOOM_ADMIN_TOKEN` / `BOOM_AUTH_TOKEN` env 优先覆盖 config/flag |
| S16 | SessionBind 失败断连 | 返回错误响应后 100ms 延迟 `conn.Close()` + slog.Warn 记录来源 IP |
| S17 | WebSocket Origin 白名单 | `allowedOrigins` 配置项，空=全部允许（向后兼容） |
| S18 | Per-IP 连接频率限制 | `IPRateLimiter` 10 conn/sec/IP，`acceptLoop` 超限直接拒绝 |
| S19 | systemd 模板入库 | `deploy/boomnetwork.service`（Type=notify + Restart=on-failure） |
| S20 | Docker 完善 | `EXPOSE 9091` + `HEALTHCHECK curl /health` + `apk add curl` |
| S21 | /health 增强 | 新增 `buildHash` / `buildTime` / `goVersion` 字段 |
| S22 | sd_notify | `sdNotifyReady()` 写 `READY=1` 到 `NOTIFY_SOCKET`（非 systemd 环境自动跳过） |

---

## 远期演进（Phase E）

| # | 任务 | 说明 |
|---|------|------|
| S27 | TLS 支持 | TCP + Admin HTTP TLS；KCP 考虑 DTLS |
| S28 | 多实例集群 | 房间分片、跨实例转移、负载均衡 |
| S29 | Replay 录制 | 完整帧数据落盘，离线回放 |
| S30 | 插件系统 | OnJoin/OnInput/OnFrame Hook 点 |
| S31 | WebTransport | HTTP/3 面向浏览器客户端 |
| S32 | 测试覆盖率 > 60% | Room.stepFrame、reconnect 全路径 |

---

## 路线总览

```
✅ Phase A — 止血 (S1-S6)            2026-03-26
✅ Phase B — 性能 (S23-S26)           2026-03-26
✅ Phase C — 可观测+可靠性 (S7-S14)    2026-03-26
✅ Phase D — 安全+运维 (S15-S22)       2026-03-26

  Phase E — 远期 (S27-S32)
```

**当前成熟度：Beta 级** — Phase A-D 共 26 项全部完成：

| 维度 | 能力 |
|------|------|
| **能跑** | 帧同步核心 + TCP/KCP 双协议 + 二级降级重连 + 实体权威 + 轻量状态同步 |
| **能看** | 16 项 Prometheus 指标（含 2 Histogram）+ slog JSON 日志 + Grafana 模板 + 运行时级别切换 |
| **能扛** | panic 自愈 + 优雅关闭（ServerShutdown 广播 + 30s drain）+ 容量上限 + CAS 重连 |
| **能防** | env token + 鉴权失败断连 + WS Origin 白名单 + per-IP 连接限流 + per-conn 消息限流 |
| **能运维** | systemd（sd_notify）+ Docker（HEALTHCHECK）+ /health（build 信息）+ 配置热重载 + GM 15 端点 |
