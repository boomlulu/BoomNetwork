# BoomNetwork Go 服务器审计 — 任务索引

**审计日期**: 2026-04-10  
**修复完成**: 2026-04-11  
**审计范围**: `svr/` 全部 Go 源码  
**总计发现**: 18 项隐患（17 项已修复，P3-15 待排期）

> **✅ 验收通过**（2026-04-11）: `go build ./...` ✓ · `go vet ./...` ✓ · `go test ./...` ✓ · `go test -race ./...` ✓  
> 单元测试: 新增 6 个验收测试（P0-01 × 2 / P1-04 / P1-05 / P1-06 × 2）  
> 额外发现: DisconnectPlayer PlayerReplaying 计数 bug（已修复） + AddPlayer reconnect 双重计数（已修复）

---

## 执行优先级

### 立即执行（P0 — 生产事故级）

| 编号 | 文件 | 问题 | 改动量 | 依赖 |
|------|------|------|--------|------|
| [P0-01](p0/01_replay_realtime_race.md) | room.go + main.go | Replay/实时帧竞态（4003） | ~50 行 | 无 |
| [P0-02](p0/02_matchroom_no_timeout.md) | main.go | matchRoom replay 无超时 | ~10 行 | 与 P0-01 同时执行 |
| [P0-03](p0/03_matchroom_kv_race.md) | main.go | matchRoom KV 同步并发写 conn | ~20 行 | 与 P0-01 同时执行 |

**建议**: P0-01/02/03 在同一个 PR 中一起提交，因为三者都改 `handleMatchRoom`，且 P0-02 和 P0-03 的修复需要配合 P0-01 的 `SetPlayerLive`。

### 本周执行（P1 — 高风险）

| 编号 | 文件 | 问题 | 改动量 | 依赖 |
|------|------|------|--------|------|
| [P1-04](p1/04_player_counter_overflow.md) | main.go | playerCounter int32 溢出 | ~15 行 | 需确认协议层 |
| [P1-05](p1/05_reconnect_old_conn_race.md) | main.go | 重连旧连接关闭竞态 | ~15 行 | 无 |
| [P1-06](p1/06_ip_limiter_leak.md) | security.go + main.go | IP 限流表内存泄漏 | ~40 行 | 无 |
| [P1-07](p1/07_ws_missing_write_timeout.md) | ws_server.go | WS 缺少写超时 | 1 行 | 无 |
| [P1-08](p1/08_frame_hashes_leak.md) | room.go | frameHashes 无限增长 | ~15 行 | 无 |

**建议**: P1-07 改动最小（一行），可以立即合入。P1-04 需要先确认协议层 playerId 的编码宽度。

### 排期执行（P2 — 中风险）

| 编号 | 文件 | 问题 | 改动量 | 依赖 |
|------|------|------|--------|------|
| [P2-09](p2/09_request_start_sleep.md) | main.go | RequestStart sleep 时序 | ~10 行 | 无 |
| [P2-10](p2/10_encode_pool_safety.md) | message.go | Encode pool buffer API 安全 | ~10 行 | 无 |
| [P2-11](p2/11_global_max_message_size.md) | framing.go + servers | 全局 MaxMessageSize | ~5 行 | 需确认影响面 |
| [P2-12](p2/12_room_start_broadcast_race.md) | room.go | Start 广播与 tickLoop 竞态 | ~20 行 | 无 |
| [P2-13](p2/13_reconnect_toctou.md) | room.go + main.go | IsRunning/IsGamePaused TOCTOU | ~20 行 | 无 |

### 有空再做（P3 — 低风险）

| 编号 | 文件 | 问题 | 改动量 | 依赖 |
|------|------|------|--------|------|
| [P3-14](p3/14_sendmsg_ignores_error.md) | main.go | sendMsg 忽略 error | ~20 行 | 无 |
| [P3-15](p3/15_sync_map_type_safety.md) | main.go | sync.Map 类型安全 | 大 | 无 |
| [P3-16](p3/16_health_info_leak.md) | admin.go | /health 泄露版本信息 | ~5 行 | 无 |
| [P3-17](p3/17_admin_create_room_dup_matchkey.md) | admin.go | MatchKey 重复赋值 | 1 行 | 无 |
| [P3-18](p3/18_count_online_players_atomic.md) | admin.go | 多余的 atomic 操作 | ~3 行 | 无 |

---

## Agent 执行指南

每个任务文件包含：
- 根因分析（什么代码有问题，为什么）
- 精确的文件位置和行号
- 具体的代码改动方案（可直接复制）
- 验证步骤（go vet / go test / 手动测试）

Agent 执行时应：
1. 先读取对应的任务 .md 文件
2. 按 Step 顺序执行改动
3. 运行验证步骤
4. 如有依赖项（如 P0-01 是 P0-02/03 的前置），先完成依赖

---

---

## 验收总览

每个任务文件末尾都包含完整的验收标准，包括：

### 通用验收流程

每个 PR 合入前必须满足：

1. **编译通过**: `go build ./...` 零 error
2. **静态检查**: `go vet ./...` 零 warning
3. **全量测试**: `go test ./...` 全部通过（含已有测试 + 新增测试）
4. **新增测试**: 每个 P0/P1 修复必须有对应的单元测试（文件中标注 `新增单元测试`）
5. **代码审查**: 检查项在每个任务文件中逐条列出（checkbox 格式）

### 各优先级的核心验收指标

| 优先级 | 编号 | 核心指标 |
|--------|------|----------|
| P0-01 | 4003 竞态 | 客户端不再触发 FATAL 4003 |
| P0-02 | matchRoom 超时 | pprof 中无长期存活的 replay goroutine |
| P0-03 | KV 竞态 | 客户端消息顺序：StartFrameSync → Frames → KV |
| P1-04 | counter 溢出 | 服务器可安全运行 365 天以上 |
| P1-05 | 重连竞态 | 重连后映射表无残留旧条目 |
| P1-06 | IP 泄漏 | heap_mb 在扫描场景下不持续增长 |
| P1-07 | WS 写超时 | WebSocket Send 不阻塞超过 10 秒 |
| P1-08 | hash 泄漏 | frameHashes 内存恒定，不随时间增长 |

### 集成验收（P0 全部完成后）

- [ ] 启动服务器，用压测工具模拟 50 个客户端并发加入运行中房间（matchRoom + joinRoom + reconnect 混合）
- [ ] 运行 10 分钟，客户端侧零 FATAL 4003
- [ ] 运行 10 分钟，goroutine 数量稳定（无泄漏）
- [ ] 运行 10 分钟，heap 内存稳定（无持续增长）
- [ ] `go test -race ./...` 全部通过（race detector 无报警）

---

## 相关文档

- [原始审计报告](../server_audit_findings.md)
- [4003 修复方案 v2](../desync_duplicate_frame_4003_v2.md)
- [统一投递架构方案](../framesync_unified_delivery_architecture.md)
- [二轮审计：性能优化 · 架构改进 · 隐患排查](round2_performance_architecture.md)
