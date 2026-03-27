# GM 面板规划

> **目标**：让 GM 面板成为 BoomNetwork 服务器的「单一控制台」— 所有运维、调试、调优操作都能在 Unity Editor 内完成，无需 SSH / curl / Grafana。

---

## 现状覆盖率

### ✅ 已覆盖（5 Tab，Phase 1 完成后）

| Tab | 覆盖的服务器能力 |
|-----|-----------------|
| **Monitor** | `/health` 状态 · `/stats` 流量统计 · `/perf` 运行时性能(Foldout) · `/rates` 消息速率 Top N(Foldout) |
| **Messages** | `/messages` 消息日志 · WS 实时推送 · Cmd/Dir/Player/Room/MatchKey 多维过滤 |
| **Rooms** | `/rooms` 房间列表 · Create/Stop/Kill Room · Kick Player · fps/MatchKey/Paused 标签 |
| **Control** | `/netsim` 网络模拟 · `/log-level` 日志级别热调 · `/config/reload` 配置热重载 · 配置展示 · Start/Stop |
| **Deploy** | 编译 · 上传 · 停止 · 启动 · 健康验证 · 多 Profile（Local/SSH）· systemd 生成 |

### ❌ 服务器已有但 GM 面板未暴露

| 服务器能力 | 端点 | 现状 |
|-----------|------|------|
| **单玩家详情** | `GET /players/{pid}` | 无 UI 入口 |
| **Prometheus 指标** | `:9090/metrics` | 完全未对接 |

### ❌ 服务器有数据但无管理/可视化

| 能力 | 说明 |
|------|------|
| **实体权威表** | `room.entityAuthority` — 谁持有哪个实体，但无法在面板看到/干预 |
| **KV 数据仓** | `room.dataStore` — 房间内 KV 全量，但无法浏览/编辑 |
| **帧缓冲区状态** | `room.frameBuf` — 最老帧号/容量/快照帧号，但无展示 |
| **重连统计** | Prometheus `reconnect_success/fail_total`，但面板没有 |
| **快照状态** | `room.snapshotPaused` / 快照大小 / 最后快照帧，但无展示 |
| **连接限流统计** | `rate_limited_total` / 被限流玩家，但无展示 |
| **匹配键分布** | 哪些 matchKey 有多少房间/玩家，但无统计视图 |

---

## 规划：5 个阶段

### Phase 1 — 补全已有数据的 UI + Tab 重组 ✅（2026-03-27 完成）

> Tab 按观察者视角重组：Monitor | Messages | Rooms | Control | Deploy

#### 1.1 Monitor Tab（原 Dashboard 观察部分）

- [x] **Performance 面板**（Foldout 折叠区域）
  - Goroutines 数 · Heap MB · Sys MB · GC 次数 · GC 暂停 ms
  - 数据来源：WS `perf` topic + HTTP `/perf` fallback
  - 数字 + 趋势箭头 ▲/▼（对比上次值）

- [x] **Hot Players 面板**（Foldout 折叠区域）
  - Top N 消息速率玩家列表：PID · msg/5s · 快捷 Kick 按钮
  - 数据来源：WS `rates` topic + HTTP `/rates` fallback
  - 阈值高亮：>20 红色、>10 黄色

#### 1.2 Control Tab（原 Dashboard 操作部分拆出）

- [x] **Log Level 下拉框**：DEBUG / INFO / WARN / ERROR
  - `GET /log-level` 初始化，`POST /log-level` 修改（HTTP-only）

- [x] **Reload Config 按钮**
  - `POST /config/reload`，成功/失败 toast 提示

- [x] NetSim 滑块（从 Dashboard 迁移）
- [x] Config 字段 + Start/Stop + Manual Command（从 Dashboard 迁移）

#### 1.3 Rooms Tab 增强

- [x] 房间卡片增加 `{fps}` 帧率
- [x] **快照状态**：`snapshotPaused` 黄色 `SNAPSHOT PAUSED` 警告
- [x] **MatchKey** 蓝色 badge
- [x] `Total: N` 玩家总数（含离线）
- [x] `GmRoomDetail.MatchKey` 字段补全（WS + HTTP 双通道）

---

### Phase 2 — Players Tab（工作量：中）

> 新增第 5 个 Tab，专注玩家维度。

#### 2.1 玩家列表

- [ ] **Players Tab**：全服在线玩家列表
  - 列：PID · 状态（Online/Offline/Disconnected）· Room ID · msg/s · 连接时长
  - 排序：按 PID / msg 速率 / 连接时长
  - 搜索：PID 精确搜索
  - 数据来源：从 `/rooms` 响应中聚合，或新增 `GET /players` 端点

#### 2.2 玩家详情面板

- [ ] 点击玩家行 → 展开详情侧边栏
  - 调用 `GET /players/{pid}`
  - 展示：在线状态 · 所在房间 · 房间帧号 · 断线时间 · 最近 20 条消息
  - 操作按钮：Kick · 跳转到该玩家所在房间

#### 2.3 服务器端新增

- [ ] `GET /players` — 全量玩家列表端点（目前只有 `/players/{pid}` 单查）
- [ ] WS `players` topic — 定期推送在线玩家摘要

---

### Phase 3 — 房间深度检视（工作量：中）

> 从「列表管理」升级到「单房间深度调试」。

#### 3.1 房间详情面板

- [ ] 点击房间 → 展开详情视图，包含：
  - **基础信息**：Room ID · MatchKey · 帧率 · 当前帧号 · 创建时间 · 运行时长
  - **帧缓冲**：最老帧号 · 缓冲容量 · 快照帧号 · 快照大小（bytes）
  - **玩家表格**：PID · 状态 · 权威实体数 · 最后输入帧号
  - **实体权威表**：EntityID → Owner PID · 可手动 Release

#### 3.2 KV Data Store 浏览器

- [ ] 房间详情内嵌 KV 面板
  - 显示所有 KV entries：Player · Key · Value（hex + 尝试解码） · Version
  - 支持：删除单条 · 清空某玩家所有 KV

#### 3.3 服务器端新增

- [ ] `GET /rooms/{id}` — 单房间详情端点（含 entity authority + frame buffer stats + KV 摘要）
- [ ] WS RPC `release_authority` — GM 强制释放实体权威
- [ ] WS RPC `delete_kv` — GM 删除 KV 条目

---

### Phase 4 — 可视化与监控（工作量：中-大）

> 从「数字」升级到「图表」，让趋势一目了然。

#### 4.1 实时图表（Dashboard）

- [ ] **流量曲线**：Game TX/RX 60 秒滑动窗口折线图
- [ ] **连接数曲线**：在线玩家数 + 房间数 60 秒趋势
- [ ] **帧广播延迟**：P50 / P95 / P99（需服务器新增推送）
- [ ] 使用 Unity `GL.Begin` / IMGUI 或 UIElements 绘制，不依赖第三方图表库

#### 4.2 事件时间线（Messages Tab 增强）

- [ ] **Timeline 视图**（可选切换）：横轴时间，纵轴玩家
  - 每个消息画为一个点/线段，颜色 = Cmd 类型
  - 鼠标悬停显示详情
  - 帮助可视化「谁在什么时候发了什么」

#### 4.3 Prometheus 指标聚合

- [ ] Dashboard 新增 **Metrics 折叠区**
  - 展示关键 Prometheus 计数器的增量：`reconnect_success/fail`、`auth_failures`、`rate_limited`、`room_panics`
  - 定期 GET `:9090/metrics`，解析 Prometheus text format
  - 或新增服务器端 `/metrics/summary` JSON 端点避免解析

#### 4.4 服务器端新增

- [ ] WS `broadcast_latency` topic — 推送帧广播延迟分位数
- [ ] `GET /metrics/summary` — JSON 格式的关键指标摘要

---

### Phase 5 — 高级运维（工作量：大）

> 生产环境加固，面向多服务器 / 集群场景。

#### 5.1 多服务器管理

- [ ] ServerWindow 支持同时连接多个 Deploy Profile
- [ ] 顶部 Tab 变为：每个服务器一个 Tab Group，或统一 Dashboard 聚合多服聚合视图
- [ ] 对比视图：多服流量/连接/房间并排

#### 5.2 告警规则

- [ ] **Alert Rules 面板**（Settings 区域）
  - 可配置阈值：玩家消息速率 > N → 高亮/通知
  - 房间 `snapshotPaused` 持续 > 10s → 告警
  - Goroutines > N → 告警
  - 连接数接近 `maxConnections` → 告警
  - 通知方式：Unity Editor 通知 + Console.LogWarning

#### 5.3 操作审计日志

- [ ] GM 操作（Kick / Stop / Kill / Create / NetSim 变更）记录到服务器端日志
- [ ] 新增 `GET /audit-log` 端点 + GM 面板展示
- [ ] 每条记录：时间 · 操作者 · 操作 · 目标 · 结果

#### 5.4 Replay / 录像

- [ ] 服务器端可选录制帧数据到文件
- [ ] GM 面板触发录制 Start/Stop
- [ ] 下载录像文件用于离线回放调试

---

## 优先级矩阵

| Phase | 价值 | 工作量 | 建议优先级 |
|-------|------|--------|-----------|
| **Phase 1** — 补全 UI + Tab 重组 | ⭐⭐⭐⭐ 低垂果实 | 🔧 小（纯 UI） | **✅ 已完成 2026-03-27** |
| **Phase 2** — Players Tab | ⭐⭐⭐⭐ 调试必备 | 🔧🔧 中 | **P1 — 下一个** |
| **Phase 3** — 房间深度 | ⭐⭐⭐ 深度调试 | 🔧🔧 中 | **P1** |
| **Phase 4** — 可视化 | ⭐⭐⭐ 体验提升 | 🔧🔧🔧 中大 | **P2** |
| **Phase 5** — 高级运维 | ⭐⭐ 生产加固 | 🔧🔧🔧🔧 大 | **P3 — 按需** |

---

## 完成后的 Tab 结构预览

```
ServerWindow
├── Dashboard        (Phase 1 增强: +Perf +HotPlayers +LogLevel +Reload +图表)
├── Messages         (Phase 4 增强: +Timeline 视图)
├── Players          (Phase 2 新增: 全服玩家列表 + 详情)
├── Rooms            (Phase 1+3 增强: +快照状态 +详情面板 +权威表 +KV)
├── Deploy           (现有，已完善)
└── Settings/Alerts  (Phase 5: 告警规则 + 审计日志)
```
