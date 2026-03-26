# BoomNetwork 下一阶段路线图

> **背景**：服务器 Phase A-D（26 项）全部完成，已达 Beta 级
>
> **目标**：从 Beta → **Production Ready** → **开源推广** → **行业标杆**
>
> **对标**：Photon Fusion / Netcode for GameObjects / Nakama / Colyseus / Mirror / Agones
>
> **核心哲学不变**：自权威、不回滚、冲突仲裁 — 简单、直接、够用

---

## 竞品分析：BoomNetwork vs 行业标杆

> 客观定位：知道自己在哪，才知道往哪走

| 维度 | Photon Fusion | Mirror | Netcode for GO | Nakama | Colyseus | **BoomNetwork** |
|------|:---:|:---:|:---:|:---:|:---:|:---:|
| **同步模型** | Tick-based 状态同步 | SyncVar + RPC | 服务器权威 | 自定义 | 房间状态自动 delta | **自权威帧同步** |
| **预测回滚** | 内置 | 无 | 内置 | N/A | 无 | **核心层无，中间件可选** |
| **传输协议** | UDP (自研) | 可插拔 | Unity Transport | WebSocket | WebSocket | **TCP + KCP** |
| **服务器语言** | C# (云端) | C# | C# | Go | Node.js | **Go** |
| **客户端 SDK** | C#/Unity | C#/Unity | C#/Unity | 多语言 | JS/C#/等 | **C#/Unity** |
| **帧同步** | Quantum(确定性) | 无 | 无 | 无 | 无 | **核心能力** |
| **GM 工具** | Cloud Dashboard | 无 | 无 | Console | Monitor | **15 端点 + Unity 面板** |
| **可观测性** | Cloud 指标 | 无 | 无 | Prometheus | 基础 | **16 Prometheus + Grafana + slog** |
| **部署** | 托管云 | 自建 | 自建 | K8s/Docker | Heroku/Docker | **systemd + Docker + GM Deploy** |
| **开源** | 否 | 是 | 部分 | 是 | 是 | **是（计划中）** |
| **定价** | CCU 计费 | 免费 | 免费 | 免费+托管 | 免费+托管 | **免费** |

### BoomNetwork 的差异化优势

1. **帧同步原生** — Photon Quantum 做确定性帧同步但闭源收费；Mirror/Netcode 没有帧同步；BoomNetwork 是**开源帧同步框架的空白地带**
2. **自权威零延迟** — 不走"服务器权威→客户端预测→回滚修正"的重路径，**接入复杂度低一个数量级**
3. **Go 服务器** — 比 C#/Node.js 更适合高并发长连接场景（goroutine 模型 + 零 GC 热路径）
4. **GM 工具内置** — 竞品要么没有（Mirror），要么需要付费 Dashboard（Photon），BoomNetwork **开箱即用**

### BoomNetwork 的差距

1. **接入门槛**：缺一键启动 + 5 分钟教程（对标 Colyseus 的 `npm create colyseus-app`）
2. **多语言客户端**：仅 C#，缺 TypeScript/Go/Lua（对标 Nakama 的 7 语言 SDK）
3. **横向扩展**：单实例，缺集群/分片（对标 Agones 的 K8s Fleet 编排）
4. **匹配系统**：仅 MatchKey 简单匹配，缺 ELO/延迟/队列（对标 Open Match）
5. **社区生态**：无 npm/NuGet 包、无 Discord、无贡献者指南

---

## 路线图：四个阶段

```
Phase 1 — 开源就绪 (Open Source Ready)     目标：让别人能用
Phase 2 — 生产加固 (Production Hardening)   目标：让别人敢用
Phase 3 — 生态构建 (Ecosystem Building)     目标：让别人想用
Phase 4 — 行业标杆 (Industry Benchmark)     目标：让别人必须考虑
```

---

## Phase 1 — 开源就绪

> **核心问题**：一个陌生开发者 clone 后能否 5 分钟跑通？
>
> **对标**：Colyseus 的 `npm create colyseus-app` / Mirror 的 "NetworkManager 拖拽即用"
>
> **验收标准**：一个没接触过 BoomNetwork 的 Unity 开发者，clone → 5 分钟看到两个角色移动

### 1.1 一键体验（阻断推广的第一道门槛）

| # | 任务 | 说明 | 对标 |
|---|------|------|------|
| P1 | **预编译二进制发布** | GitHub Release 提供 macOS/Linux/Windows 二进制（`goreleaser`），`./boom-server` 零依赖启动 | Nakama 预编译 |
| P2 | **Docker 镜像发布** | `docker run boomnetwork/server` 一行启动，推送到 Docker Hub / GitHub Container Registry | Colyseus Docker |
| P3 | **5 分钟教程** | 从"文档"升级为"教程"：① clone ② 启动服务器 ③ 打开 Unity Demo ④ 看到移动。每步带截图/GIF | Mirror Quick Start |
| P4 | **生命周期流程图** | Mermaid 状态机：连接→绑定→建房→入房→帧同步→断线→重连。放在 README 最醒目位置 | Netcode 架构图 |
| P5 | **Demo 即模板** | Demo01 整理为可复制的项目模板，改 `OnFrame` + `SendInput` 即可做自己的游戏 | Mirror Examples |

### 1.2 开源基础设施

| # | 任务 | 说明 | 对标 |
|---|------|------|------|
| P6 | **语义化版本** | `v0.1.0` 打 tag，CHANGELOG.md，GitHub Release | 所有主流框架 |
| P7 | **CI/CD 流水线** | GitHub Actions：lint + test + build + 跨平台二进制 + Docker 镜像 | Mirror CI |
| P8 | **贡献者指南** | CONTRIBUTING.md + Issue 模板 + PR 模板 + 行为准则 | Nakama CONTRIBUTING |
| P9 | **协议文档（Wire Format）** | 二进制协议完整文档，非 C# 客户端据此实现 SDK | Photon Protocol |
| P10 | **错误码文档** | 所有 ErrorCode + 触发场景 + 推荐处理方式 | — |

### 1.3 核心体验打磨

| # | 任务 | 说明 |
|---|------|------|
| P11 | **性能基准报告** | N 客户端 × 帧率 × 延迟 × 内存的量化数据，与 Photon/Mirror 横向对比给用户信心 |
| P12 | **KCP 集成测试** | TCP 路径测试充分，KCP 路径补齐帧同步集成测试和 Demo |
| P13 | **帧同步暂停/恢复通知** | 快照过期暂停时通知客户端原因 + 恢复事件（`CmdFrameSyncPaused/Resumed`） |

---

## Phase 2 — 生产加固

> **核心问题**：一个团队敢用它上线吗？
>
> **对标**：Photon 的全球基础设施 / Nakama 的集群方案 / Agones 的 K8s 编排

### 2.1 传输层升级

| # | 任务 | 说明 | 对标 |
|---|------|------|------|
| P14 | **TLS 支持** | TCP + Admin HTTP 的 TLS 选项；KCP 层应用层加密 | Nakama TLS |
| P15 | **WebSocket 传输** | 第三种传输协议，面向浏览器/微信小游戏客户端 | Colyseus WebSocket |
| P16 | **传输层抽象** | `Transport` 接口统一 TCP/KCP/WebSocket/WebTransport，用户可自定义 | Mirror Transport |

### 2.2 匹配与房间增强

| # | 任务 | 说明 | 对标 |
|---|------|------|------|
| P17 | **匹配系统 v2** | ELO 评分 + 延迟匹配 + 队列等待 + 匹配超时 + 取消 | Open Match |
| P18 | **房间状态推送** | JoinRoomRsp 带完整玩家状态（在线/离线/权威持有） | Photon Room Properties |
| P19 | **观战模式** | 只读连接，收帧但不发输入，延迟 N 帧防作弊 | 常见竞技需求 |
| P20 | **Replay 录制与回放** | 服务端完整帧数据落盘 + 回放 API + 客户端播放器 | Photon Quantum Replay |

### 2.3 横向扩展

| # | 任务 | 说明 | 对标 |
|---|------|------|------|
| P21 | **多实例集群** | 房间分片、实例间 RPC（gRPC/NATS）、负载均衡 | Nakama Cluster |
| P22 | **K8s 就绪** | Helm Chart + liveness/readiness probe + HPA 自动扩缩 | Agones Fleet |
| P23 | **全局匹配器** | 跨实例匹配，玩家路由到最近/最空的实例 | Agones Allocator |
| P24 | **Redis 状态共享** | 跨实例的 playerRoomMap + 匹配队列 | Colyseus Redis |

### 2.4 冲突仲裁系统（核心哲学落地）

| # | 任务 | 说明 | 对标 |
|---|------|------|------|
| P25 | **IConflictResolver 接口** | 可插拔仲裁策略（ShooterAuthority / TargetAuthority / HostAuthority / ServerAuthority） | 核心哲学第三公理 |
| P26 | **服务器端仲裁** | 专用服务器模式下的命中判定 + 伤害仲裁 | Photon Fusion Host Mode |
| P27 | **仲裁结果广播** | 仲裁结果 → 全房间广播 → 客户端视觉修正（无回滚） | 核心哲学 |

---

## Phase 3 — 生态构建

> **核心问题**：开发者为什么选 BoomNetwork 而不是其他框架？
>
> **对标**：Photon 的生态系统 / Nakama 的多语言 SDK / Mirror 的社区贡献

### 3.1 多语言客户端 SDK

| # | 任务 | 目标用户 | 对标 |
|---|------|---------|------|
| P28 | **TypeScript SDK** | Cocos Creator / Web 游戏 / 微信小游戏 | Nakama JS SDK |
| P29 | **Go SDK** | 服务器间通信 / 压测工具 / 机器人 | Nakama Go SDK |
| P30 | **Lua SDK** | Cocos2d-x / 自研引擎 | Nakama Lua SDK |
| P31 | **Godot SDK** | Godot 引擎用户（增长最快的游戏引擎） | Mirror Godot 社区 |

### 3.2 插件与扩展

| # | 任务 | 说明 | 对标 |
|---|------|------|------|
| P32 | **服务端插件系统** | Hook 点（OnJoin / OnInput / OnFrame / OnLeave），Lua/Go 脚本 | Nakama Runtime |
| P33 | **回滚中间件** | GGPO 风格的可选回滚层，面向格斗/硬核 FPS 用户 | GGPO / Photon Quantum |
| P34 | **持久化中间件** | 房间状态落盘（SQLite/PostgreSQL），支持服务器重启后恢复 | Nakama Storage |
| P35 | **聊天/社交** | 房间内文字聊天 + 全局频道 | Nakama Chat |

### 3.3 开发者体验

| # | 任务 | 说明 | 对标 |
|---|------|------|------|
| P36 | **Unity Package Manager 发布** | 通过 OpenUPM 或 Unity Asset Store 安装，非 git URL | Mirror UPM |
| P37 | **CLI 工具** | `boom init` 创建项目 / `boom server` 启动 / `boom deploy` 部署 | Colyseus CLI |
| P38 | **在线 Playground** | 浏览器内可运行的帧同步 Demo（WebSocket + WebGL） | Colyseus Arena |
| P39 | **视频教程系列** | YouTube/B站系列：从零开始用 BoomNetwork 做一个多人游戏 | Mirror 社区教程 |

### 3.4 社区运营

| # | 任务 | 说明 |
|---|------|------|
| P40 | **Discord / QQ 群** | 开发者社区 + 技术支持 + 展示墙 |
| P41 | **Awesome BoomNetwork** | 社区项目、教程、中间件索引 |
| P42 | **Game Jam 赞助** | 用 BoomNetwork 做多人游戏的 Game Jam，种子用户 |

---

## Phase 4 — 行业标杆

> **核心问题**：BoomNetwork 能代表一种技术方向吗？
>
> **对标**：ECS 之于 Unity DOTS / Rollback 之于 GGPO

### 4.1 技术创新

| # | 任务 | 说明 |
|---|------|------|
| P43 | **自权威网络模型白皮书** | 学术级论文，形式化定义自权威模型的 correctness guarantee |
| P44 | **自适应帧率** | 根据房间负载/网络质量动态调整帧率（20fps ↔ 60fps） |
| P45 | **预测性流量控制** | 基于 RTT + 丢包率的自适应发送策略，减少弱网下的带宽浪费 |
| P46 | **分布式确定性帧同步** | 可选模式：全确定性（定点数）+ 服务器验证，面向竞技公平 |

### 4.2 平台扩展

| # | 任务 | 说明 |
|---|------|------|
| P47 | **WebTransport** | HTTP/3 + WebTransport，比 WebSocket 低延迟 |
| P48 | **QUIC 传输** | 替代 TCP 的现代传输协议 |
| P49 | **边缘计算部署** | Cloudflare Workers / AWS Lambda@Edge / Fly.io 边缘节点 |
| P50 | **全球多区域** | 自动区域路由 + 跨区域房间迁移 |

---

## 里程碑时间线

```
2026 Q2 — Phase 1: 开源就绪
  v0.1.0 发布 + GitHub Release + 5 分钟教程 + 性能基准
  验收：陌生开发者 5 分钟跑通

2026 Q3 — Phase 2: 生产加固
  TLS + WebSocket + 匹配 v2 + Replay + 仲裁系统
  验收：一个真实游戏项目用 BoomNetwork 上线

2026 Q4 — Phase 3: 生态构建
  TypeScript SDK + 插件系统 + CLI + OpenUPM
  验收：GitHub 100+ star，5+ 社区项目

2027 H1 — Phase 4: 行业标杆
  白皮书 + 集群方案 + 全球部署
  验收：被游戏开发社区认可为"自权威帧同步"的参考实现
```

---

## 优先级排序原则

> 复用 Phase A-D 验证过的协作方法论

1. **第一性原理**：框架的价值 = 用户能多快跑通。一切优先级围绕降低接入门槛
2. **审计驱动**：每个 Phase 开始前，先 benchmark 竞品的同等功能，明确差距再动手
3. **并行 Agent**：无依赖的任务并行执行，依赖链上的任务串行
4. **文档即代码**：每完成一项，立即更新 roadmap + CHANGELOG + 对应文档
5. **用户验证**：Phase 1/2 找 3-5 个外部用户试用，收集反馈后再进 Phase 3

---

## 与现有 roadmap 的关系

```
server-roadmap.md  Phase A-D (S1-S26)  ✅ 已完成 — 服务器 Beta 级
                   Phase E (S27-S32)    → 合并到本文档 Phase 2

framework-roadmap.md  N1-N4            → 合并到本文档 Phase 1.1
                      N5-N12           → 合并到本文档 Phase 1.2-1.3
                      N16-N22          → 合并到本文档 Phase 2-3

本文档 (next-roadmap.md)               → 统一的下一阶段路线图
```
