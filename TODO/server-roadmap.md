# Go 服务器路线图

> **定位**：BoomNetwork 帧同步框架的官方 Go 服务器实现
>
> **现状**：核心功能完备，止血和性能优化已完成，**可观测性和安全加固**是下一个瓶颈
>
> **目标**：从"能用"到"敢用" — 让服务器成为框架的可靠基座
>
> **核心哲学**：自权威、不回滚、冲突仲裁 — 详见 [doc/core-philosophy.md](../doc/core-philosophy.md)

---

## 现状诊断

> 最后更新：2026-03-26

| 维度 | 现状 | 差距 |
|------|------|------|
| **可观测性** | Prometheus 11 项指标全部接入；stdout `log` 日志，无结构化/级别控制；无 Grafana 模板 | 🟡 结构化日志 + 直方图指标 + Grafana |
| **可靠性** | Room panic 自愈（广播 Stop + 清理映射）；重连 CAS 防竞态；TCP/KCP 统一限流；60s ReadDeadline 兜底死连接 | 🟢 优雅关闭广播 + 容量上限 |
| **安全性** | Admin Bearer Token 鉴权；但 token 来自配置文件/flag（非环境变量）；WS `CheckOrigin: return true`；SessionBind 失败不断连；无 per-IP 连接频率限制 | 🟡 env token + 失败断连 + origin 白名单 |
| **运维** | 腾讯云 systemd 已部署（模板未入库）；GM Deploy 一键发布可用；Dockerfile 缺 9091 端口和 HEALTHCHECK；`/health` 不含 build 信息 | 🟡 systemd 入库 + Docker 修复 + /health 增强 |
| **代码质量** | 热路径零分配；Router Freeze 无锁分发；/perf STW 缓存；netsim 积压保护；map 泄漏已修复；JoinRoom 5 种错误码 | 🟢 测试覆盖率待提升 |

---

## 已完成 ✅

### 核心功能

| 模块 | 内容 |
|------|------|
| 帧同步核心 | Room tick loop、ring buffer 零分配、输入收集+广播、快照新鲜度监控（3 间隔未收到 → 暂停） |
| 双协议传输 | TCP（NoDelay + KeepAlive 30s）+ KCP（低延迟调参、统一 RateLimiter） |
| 房间管理 | 创建/加入/离开/匹配（MatchKey）/自动分配/空房清理/30s 延迟销毁 |
| 重连支持 | 快速重连（ring buffer 回放）+ 快照重连（二级降级）+ CAS 防竞态 + 旧连接自动关闭 |
| 迟到者加入 | 快照 → StartFrameSync → catchup frames + KV 全量同步 |
| 实体权威同步 | Cmd 27/28 广播、权威转移（grant/release）、断线释放全部权威 |
| 轻量状态同步 | StateMessage 转发（Cmd 50/51）+ DataMessage KV 存储/增量广播/全量同步（Cmd 52-55） |

### 运维与工具

| 模块 | 内容 |
|------|------|
| Admin API | 11 个 HTTP 端点（health/stats/messages/rooms/stop/kill/create/kick/players/perf/rates/netsim）+ WebSocket 实时推送 |
| 网络模拟 | S→C 延迟/抖动/丢包，HTTP + WS Dashboard 滑块控制，积压保护（10000 pending 上限降级） |
| 流量统计 | Game/GM 分离、60s ring buffer、1min/5sec 窗口、per-player rate（断线自动清理） |
| 消息日志 | 100 条 ring buffer + 三层 CmdType payload 解码 + WS 实时推送 + 关键消息详情（G7） |
| Codec | 三层 CmdType（Core/Extended/Game）+ sync.Pool + 跨语言兼容测试 |
| 部署 | Dockerfile + docker-compose + 预编译 Linux 二进制 + 腾讯云 systemd + GM Deploy 一键发布 |

### 止血修复（S1-S6，2026-03-26）

| # | 修复 | 方案 |
|---|------|------|
| S1 | Prometheus 指标修复 | `FramesPushed`/`BytesSent`/`BytesReceived`/`MessageErrors`/`RateLimited` 全部接入；`RoomsCurrent` 在 create/remove/cleanup/stopAll 全路径补齐 |
| S2 | KCP 限流对齐 | KCP 连接创建时 `NewRateLimiter` + `Allow()` 检查；`DefaultSecurityConfig()` 初始化 |
| S3 | Room panic 自愈 | 广播 `CmdStopFrameSync` → 清空 players → `OnPanic` 回调清理 `playerRoomMap` + `RemoveRoom` |
| S4 | 重连竞态修复 | 重连时关闭旧连接 + 清理旧 connID；`onClientDisconnect` CAS 检查防覆盖新连接 |
| S5 | JoinRoom 错误码 | `JoinRoomResult` 枚举（NotFound=1/Full=2/NotBound=3/BadData=4），byte[4] 向后兼容；C# 客户端解码 |
| S6 | playerRate 泄漏 | `PlayerRates.Remove(pid)` + `onClientDisconnect` 调用 |

### 性能优化（S23-S26，2026-03-26）

| # | 优化 | 方案 |
|---|------|------|
| S23 | Router 无锁 | `Freeze()` 冻结路由表到无锁快照，Dispatch 零锁开销 |
| S24 | atomic 冗余 | `RoomManager.nextID` 去掉 `atomic`，`mu` 保护下普通递增 |
| S25 | /perf STW 缓存 | `ReadMemStats` 结果缓存 5 秒 |
| S26 | netsim 积压保护 | `simPending` 原子计数，超 10000 降级为直接发送 |

---

## 待做：可观测性（Phase C）

> 没有可观测性 = 线上裸奔

| # | 任务 | 现状 | 目标 |
|---|------|------|------|
| S7 | **结构化日志** | 全部用 `log` + `fmt.Printf`（transport 层），无级别、无结构化字段 | `log/slog`（Go 1.21+ 标准库），JSON 格式，关键字段：roomId/playerId/cmd/latency |
| S8 | **日志级别运行时切换** | 无 | Admin API `POST /log-level` + `slog.LevelVar`，不重启切换 Debug/Info/Warn |
| S9 | **关键路径指标补全** | 只有 Counter/Gauge，无直方图 | 帧广播延迟 Histogram、房间生命周期 Histogram、重连成功/失败 Counter、快照大小 Gauge |
| S10 | **Grafana Dashboard 模板** | 无 | JSON 模板：连接数、房间数、帧率偏差、内存、GC pause，开箱即用 |

---

## 待做：可靠性加固（Phase C）

| # | 任务 | 现状 | 目标 |
|---|------|------|------|
| S11 | **优雅关闭** | 收到信号 → `StopAll` → `server.Close()` 直接断连，无等待/广播 | `sync.WaitGroup` 等待 goroutine 退出 + 30s 超时 + 广播 ServerShutdown |
| S12 | **Config 热重载** | 启动时读一次，不可更新 | `SIGHUP` 或 Admin API 触发 reload（帧率/限流/netsim 参数） |
| S13 | **连接健康检测** | TCP: KeepAlive 30s + ReadDeadline 60s；KCP: 仅 ReadDeadline 60s；无应用层心跳超时 | 服务端心跳超时检测，主动踢掉无心跳客户端 |
| S14 | **容量上限** | 无全局限制，CreateRoom 硬编码 `maxPlayers≤100` | 可配置 `maxRooms` + `maxConnections`，超出拒绝并返回明确错误 |

---

## 待做：安全加固（Phase D）

| # | 任务 | 现状 | 目标 |
|---|------|------|------|
| S15 | **Admin Token 环境变量化** | token 从 flag `-admin-token` 或 YAML `adminToken` 读取，无 env 支持 | 优先 `BOOM_ADMIN_TOKEN` env → YAML fallback，配置文件不存明文 token |
| S16 | **SessionBind 失败断连** | 校验失败返回 `playerId=0` 但**不关闭连接**，客户端可继续发消息 | 失败后立即 `conn.Close()` + 记录来源 IP |
| S17 | **WebSocket Origin 白名单** | `CheckOrigin: func() { return true }`（`admin_ws.go:216`） | 从 config 读取允许的 origin 列表 |
| S18 | **Per-IP 连接频率限制** | `acceptLoop` 无条件接受所有连接；只有 per-conn 消息频率限制 | 单 IP 新建连接速率限制（防 SYN flood） |

---

## 待做：运维友好（Phase D）

| # | 任务 | 现状 | 目标 |
|---|------|------|------|
| S19 | **systemd 模板入库** | 腾讯云已用 `/etc/systemd/system/boomnetwork.service`，但**未提交到仓库** | `deploy/boomnetwork.service` 入库 |
| S20 | **Docker 完善** | `EXPOSE 9000 9090`，**缺 9091**（Admin）；无 `HEALTHCHECK` | 加 `EXPOSE 9091` + `HEALTHCHECK CMD curl -f http://localhost:9091/health` |
| S21 | **`/health` 增强** | 返回 `status/rooms/players/uptime`；`BuildHash`/`BuildTime` 存在但未接入 | 加 `buildHash`/`buildTime`/`goVersion`/`configPath` 字段 |
| S22 | **sd_notify** | 无 systemd 就绪通知 | `sd_notify(READY=1)` 让 systemd 知道服务真正就绪 |

---

## 远期演进

| # | 任务 | 说明 |
|---|------|------|
| S27 | **TLS 支持** | TCP + Admin HTTP 的 TLS 选项；KCP 层考虑 DTLS 或应用层加密 |
| S28 | **多实例集群** | 房间分片、跨实例转移、负载均衡方案设计 |
| S29 | **Replay 录制** | 完整帧数据落盘，支持离线回放和 debug |
| S30 | **插件系统** | 服务端 Hook 点（OnJoin / OnInput / OnFrame），不改源码加自定义逻辑 |
| S31 | **WebTransport** | HTTP/3 + WebTransport 作为第三种传输协议，面向浏览器客户端 |
| S32 | **测试覆盖率 > 60%** | 重点覆盖 Room.stepFrame、broadcast、RoomManager 生命周期、reconnect 全路径 |

---

## 路线总览

```
✅ Phase A — 止血 (S1-S6)    2026-03-26 完成
✅ Phase B — 性能 (S23-S26)   2026-03-26 完成

→ Phase C — 可观测 + 可靠性 (S7-S14)
    S7 slog 结构化日志 → S9 直方图指标 → S10 Grafana → S11 优雅关闭

→ Phase D — 安全 + 运维 (S15-S22)
    S15 env token → S16 失败断连 → S19 systemd 入库 → S20 Docker → S21 /health

  Phase E — 远期 (S27-S32)

验收标准:
  Phase C-D 完成后 → "Beta 级"
  能跑 + 能看（slog + Grafana）+ 能防（限流 + 鉴权 + 断连）+ 能运维（一键部署 + 健康探针）
```
