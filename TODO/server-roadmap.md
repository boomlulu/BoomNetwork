# Go 服务器路线图

> **现状评估**：技术原型（Tech Demo），不是生产级产品
>
> **核心问题**：能跑 ≠ 能上线。当前服务器可以支撑开发/演示，但距离"一个团队敢用它上线"还有明确的 gap
>
> **目标**：从"能用"到"敢用" — 让服务器成为框架的可靠基座

---

## 现状诊断：为什么还不算产品？

用生产服务器的 5 个维度评估（2026-03-26 更新）：

| 维度 | 现状 | 生产要求 | 差距 |
|------|------|---------|------|
| **可观测性** | Prometheus 10/10 指标全部接上；但仍是 stdout 日志，无结构化/级别控制 | 结构化日志 + 告警 + Grafana 模板 | 🟡 中 |
| **可靠性** | Room panic 自愈 + 僵尸清理；重连竞态已修复（CAS）；TCP/KCP 统一限流 | 优雅关闭、连接健康检测、容量上限 | 🟢 小 |
| **安全性** | 明文传输、硬编码 token、WS 无 origin 检查 | TLS/加密、环境变量管理、连接频率限制 | 🟡 中 |
| **运维友好** | systemd 已部署腾讯云（未入库）；Docker 端口遗漏；GM Deploy 一键部署可用 | systemd 模板入库、Docker 修复、sd_notify | 🟡 中 |
| **代码质量** | 热路径零分配 + Router 无锁；map 泄漏已修复；JoinRoom 错误码区分 | 测试覆盖率提升 | 🟢 小 |

**结论**：止血（S1-S6）和性能优化（S23-S26）已完成。可靠性和代码质量从 🔴 提升到 🟢。下一个瓶颈是**可观测性**（结构化日志）和**安全加固**（token 环境变量化）。

---

## 已完成 ✅

| 模块 | 内容 |
|------|------|
| 帧同步核心 | Room tick loop、ring buffer 零分配、输入收集+广播 |
| 双协议传输 | TCP（NoDelay + KeepAlive）+ KCP（低延迟调参 + 统一限流） |
| 房间管理 | 创建/加入/离开/匹配/自动分配/空房清理/30s 延迟销毁 |
| 重连支持 | 快速重连（ring buffer 回放）+ 快照重连（二级降级）+ CAS 防竞态 |
| 迟到者加入 | 快照 + StartFrameSync + catchup frames |
| Admin API | 10 个 HTTP 端点 + WebSocket 实时推送（7 topics） |
| 网络模拟 | S→C 延迟/抖动/丢包，HTTP + WS 可控，积压保护（10000 上限降级） |
| 流量统计 | Game/GM 分离、1min/5sec 窗口、per-player rate（断线自动清理） |
| 消息日志 | 100 条 ring buffer + payload 解码 + WS 实时推送 |
| Codec | 三层 CmdType + sync.Pool + 跨语言兼容测试 |
| 基础部署 | Dockerfile + docker-compose + 预编译 Linux 二进制 + 腾讯云 systemd |
| 实体权威同步 | Cmd 27/28 广播、权威释放（断线时） |
| 轻量状态同步 | StateMessage 转发 + DataMessage KV 存储/增量广播/全量同步 |
| Prometheus | 10/10 指标全部接入（connections/rooms/frames/bytes/errors/rate_limited） |
| 容错加固 | Room panic 自愈（广播 Stop + 清理映射）、JoinRoom 错误码区分（5 种） |
| 性能优化 | Router Freeze 无锁分发、/perf STW 缓存、atomic 冗余清理 |

---

## 一、紧急且重要 — 阻断上线的硬伤 ✅

> **判断标准**：这些问题在真实对局中**一定会触发**，且后果不可接受
>
> **已完成**（2026-03-26）

| # | 任务 | 问题 | 方案 | 状态 |
|---|------|------|------|------|
| S1 | **Prometheus 指标修复** | `frames_pushed`/`bytes_sent`/`bytes_received`/`message_errors`/`rate_limited` 5 个指标定义了但从未递增；`rooms_current` 从未被调用 | 在 stepFrame/rxHandler/txStats/statsConn/sendMsg 路径补上 `.Inc()`/`.Add()`；RoomManager 的 create/remove/cleanup/stopAll 全路径补上 `RoomsCurrent.Inc()/Dec()/Sub()` | ✅ |
| S2 | **KCP 限流对齐** | TCP 有 RateLimiter，KCP 完全没有。恶意客户端可无限发包 | KCP 连接创建时设置 `NewRateLimiter`，handleConn 中加 `Allow()` 检查，与 TCP 一致；KcpServer 构造函数初始化 `DefaultSecurityConfig()` | ✅ |
| S3 | **Room panic 恢复一致性** | `recover()` 后 `running=false`，但 `players` map 和 `playerRoomMap` 残留 → 玩家重连找到僵尸房间 | panic 恢复时：广播 `CmdStopFrameSync`、清空 players map、通过 `OnPanic` 回调清理 `playerRoomMap` 并调 `RoomManager.RemoveRoom()` | ✅ |
| S4 | **重连竞态修复** | 快速多次重连时 `connPlayerMap`/`playerConnMap` 可能不一致 → 旧连接和新连接争夺同一 playerId | 重连时先关闭旧连接并清理旧 connID 映射；`onClientDisconnect` 用 CAS 检查 `playerConnMap` 是否仍指向当前 conn，防止覆盖新连接 | ✅ |
| S5 | **JoinRoom 错误码区分** | "房间不存在"/"房间已满"/"未绑定" 都返回 8 字节零 → 客户端无法提示用户 | 定义 `JoinRoomResult` 枚举（Success=0/NotFound=1/Full=2/NotBound=3/BadData=4），错误码写入 byte[4]（向后兼容：PlayerId=0 仍表示失败） | ✅ |
| S6 | **playerRate map 泄漏** | `playerRate` 只增不删，长时间运行后内存持续增长 | 添加 `PlayerRates.Remove(pid)`，在 `onClientDisconnect` 中调用清理 | ✅ |

---

## 二、重要不紧急 — 让服务器"像个产品"

> **判断标准**：没有这些，能跑但不敢运维

### 2.1 可观测性

| # | 任务 | 说明 |
|---|------|------|
| S7 | **结构化日志** | `log` → `slog`（Go 1.21+ 标准库）。JSON 格式 + level 分级（Info/Warn/Error）。关键字段：roomId、playerId、cmd、latency |
| S8 | **日志级别可运行时切换** | Admin API `POST /log-level` 或 config reload，不重启切换 Debug/Info/Warn |
| S9 | **关键路径指标补全** | 帧广播延迟直方图、房间生命周期直方图、重连成功/失败计数、快照大小 gauge |
| S10 | **Grafana Dashboard 模板** | 提供 JSON 模板：连接数、房间数、帧率偏差、内存、GC pause，开箱即用 |

### 2.2 可靠性

| # | 任务 | 说明 |
|---|------|------|
| S11 | **优雅关闭完善** | 加 `sync.WaitGroup` 等待所有连接 goroutine 退出；设置 30s 超时强制退出；关闭前广播 ServerShutdown 通知客户端 |
| S12 | **Config 热重载** | `SIGHUP` 或 Admin API 触发 config reload，不重启更新帧率、限流阈值、netsim 参数等 |
| S13 | **连接健康检测** | 服务端主动检测死连接（不只依赖客户端心跳）。TCP KeepAlive 已有但 timeout 较长（60s read deadline），KCP 无此机制 |
| S14 | **房间容量上限** | 全局最大房间数、最大总连接数可配置，超出时拒绝新连接并返回明确错误 |

### 2.3 安全加固

| # | 任务 | 说明 |
|---|------|------|
| S15 | **Admin Token 环境变量化** | `config.dev.yaml` 中硬编码 `adminToken: "84224155"` → 从环境变量 `BOOM_ADMIN_TOKEN` 读取，配置文件只放占位符 |
| S16 | **SessionBind 失败断连** | 当前 token 校验失败仍保持连接（playerId=0）→ 应立即断开并记录来源 IP |
| S17 | **WebSocket Origin 白名单** | `CheckOrigin: func() { return true }` → 从 config 读取允许的 origin 列表 |
| S18 | **连接速率限制** | 单 IP 新建连接频率限制（防 SYN flood 型滥用），当前只限消息频率不限连接频率 |

### 2.4 运维友好

| # | 任务 | 说明 |
|---|------|------|
| S19 | **systemd 模板入库** | 提供 `boomnetwork.service` 模板文件（已在腾讯云用但没入仓库）|
| S20 | **Docker 端口修复** | Dockerfile/compose 暴露 9091 admin 端口；health endpoint 作为 Docker HEALTHCHECK |
| S21 | **Build 信息增强** | `/health` 返回 `buildHash`、`buildTime`、`goVersion`、`configPath`，方便远程诊断 |
| S22 | **PID 文件 / Ready 通知** | 支持 `sd_notify(READY=1)` 让 systemd 知道服务真正就绪，而非进程启动就算 ready |

---

## 三、紧急不重要 — 快速提升 ✅

> **已完成**（2026-03-26）

| # | 任务 | 方案 | 状态 |
|---|------|------|------|
| S23 | **Router RWMutex 优化** | `Freeze()` 将路由表拷贝到无锁快照，Dispatch 直接查表不加锁 | ✅ |
| S24 | **atomic 冗余清理** | `RoomManager.nextID` 去掉 `atomic.AddInt32`，改为 `rm.mu` 保护下的普通递增 | ✅ |
| S25 | **`/perf` 端点 STW 缓存** | `ReadMemStats` 结果缓存 5 秒，避免频繁请求触发 STW 影响帧同步 | ✅ |
| S26 | **netsim timer 积压保护** | `simPending` 原子计数，超过 10000 上限时降级为直接发送 | ✅ |

---

## 四、不紧急不重要 — 远期演进

| # | 任务 | 说明 |
|---|------|------|
| S27 | **TLS 支持** | TCP + Admin HTTP 的 TLS 选项；KCP 层考虑 DTLS 或应用层加密 |
| S28 | **多实例集群** | 房间分片、跨实例转移、负载均衡方案设计 |
| S29 | **Replay 录制** | 将完整帧数据落盘，支持离线回放和 debug |
| S30 | **插件系统** | 服务端 Hook 点（OnJoin / OnInput / OnFrame），用户不改源码就能加自定义逻辑 |
| S31 | **WebTransport** | HTTP/3 + WebTransport 作为第三种传输协议，面向浏览器客户端 |
| S32 | **服务端测试覆盖率 > 60%** | 重点覆盖 Room.stepFrame、broadcast、RoomManager 生命周期、reconnect 全路径 |

---

## 下一步建议

```
✅ 已完成:
  Phase A — 止血 (S1-S6):  指标修复、KCP 限流、panic 恢复、重连竞态、错误码、map 泄漏
  Phase B — 性能 (S23-S26): Router 无锁、atomic 清理、/perf 缓存、netsim 积压保护

下一步:
  Phase C — 可观测 (S7-S10):
    S7 结构化日志 → S9 指标补全 → S10 Grafana 模板
    没有可观测性 = 线上裸奔

  Phase D — 安全 + 运维 (S15-S22):
    S15 token 环境变量 → S16 bind 失败断连 → S19 systemd 入库 → S20 Docker 修复

验收标准:
  Phase C-D 完成后，服务器可以称为 "Beta 级" —
  能跑、能看（日志+指标）、能防（限流+鉴权）、能运维（一键部署+健康检查）
```
