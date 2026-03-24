# BoomNetwork 框架推广 — 紧急重要四象限

## 一、紧急且重要（立即做，阻断别人使用）

| # | 任务 | 状态 | 说明 |
|---|------|------|------|
| 1 | README + Quick Start | **已完成** | 5 分钟跑通：克隆→启动服务器→Unity 连接→方块移动 |
| 2 | Unity 集成指南 | **已完成** | 30 行代码接入，Person 生命周期 + 事件一览 |
| 3 | 服务器部署 | **已完成** | Dockerfile + docker-compose + 二进制编译 + systemd |
| 4 | API Reference | **已完成** | Person / FrameSyncClient / RoomClient 全接口 + 26 条协议命令表 |

## 二、重要不紧急（质量保障，早做少踩坑）

| # | 任务 | 状态 | 说明 |
|---|------|------|------|
| 5 | 协议文档 | 待做 | Cmd 列表 + 二进制 Wire format，非 C# 客户端可据此接入 |
| 6 | 生命周期流程图 | 待做 | 连接→房间→帧同步→断线→重连 完整状态机（Mermaid） |
| 7 | 错误码文档 | 待做 | 所有 ErrorCode + 触发场景 + 客户端推荐处理方式 |
| 8 | config.yaml 参数文档 | 待做 | 独立文档：参数含义、推荐值、参数间关系、调优指南 |
| 9 | License | **已完成** | MIT |
| 10 | 自动化测试 CI | 待做 | GitHub Actions: Go test + C# test + Unity EditMode test |
| 11 | 版本号 + CHANGELOG | 待做 | 语义化版本 + 每版变更记录 |
| 12 | 迟到者加入 (Late-Join) | **已完成** | JoinRoom 时下发 RoomSnapshot + 补帧 |

## 三、紧急不重要（快速解决，提升第一印象）

| # | 任务 | 状态 | 说明 |
|---|------|------|------|
| 13 | Demo 场景整理 | **已完成** | DemoLauncher 入口 + 场景切换菜单 |
| 14 | ServerWindow 优化 | **已完成** | HTTP /health 探测（零游戏日志）+ 流量统计面板 + 迁入 GM 工具包 |
| 15 | HUD 美化 | **已完成** | HUDStyles 共享样式 + 状态颜色语义化 + World Hash |

## 四、不紧急不重要（锦上添花，有余力再做）

| # | 任务 | 状态 | 说明 |
|---|------|------|------|
| 16 | 性能基准报告 | 待做 | N 客户端、帧率、延迟、内存的量化数据 |
| 17 | 多语言客户端示例 | 待做 | TypeScript / Lua / Go 客户端 |
| 18 | 服务器集群方案 | 待做 | 多房间服务器横向扩展、负载均衡 |
| 19 | 加密传输 | 待做 | TLS / KCP 加密选项 |
| 20 | Demo02 预测回滚完善 | 待做 | 目前只有框架代码，Demo 场景未完成 |
| 21 | Web 管理后台 | **部分完成** | Admin HTTP (:9091) + /health + /stats + GM 工具包。待做：/rooms、/kick、鉴权中间件 |

---

# BoomNetwork 框架迭代 — 紧急重要四象限

## 一、紧急且重要（影响核心功能，必须优先）

| # | 任务 | 说明 |
|---|------|------|
| F1 | ~~帧号去重与快照冲突~~ | **已修复** — LoadWorldSnapshot 重置 `_lastProcessedFrame=0` |
| F2 | ~~集成测试 Test10 稳定性~~ | **已修复** — ConnectionManager 防止 Reconnecting 状态下重复触发重连，16/16 全过 |
| F3 | ~~Late-Join 无快照兜底~~ | **已修复** — 无快照时从缓冲区最旧帧开始补帧（最佳努力） |

## 二、重要不紧急（架构改进，提升健壮性）

| # | 任务 | 说明 |
|---|------|------|
| F4 | ~~Person 重连流程收敛~~ | **已完成** — FrameSyncClient 内置 CompositeReconnectStrategy，Person 瘦身为纯适配器，不再自己管理重连 |
| F5 | **服务器推送房间完整状态** | JoinRoomRsp 带 existingPlayers 但不带状态（在线/掉线）。后续应推送每个玩家的 online/offline 状态 |
| F6 | **帧同步暂停/恢复机制** | 当前快照过期暂停是服务端静默停帧，客户端不知道。应通知客户端暂停原因 + 恢复事件 |
| F7 | **KCP 传输层测试覆盖** | TCP 路径测试充分，KCP 路径未在集成测试和 Demo 中覆盖 |
| F8 | **消息可靠性保障** | 快照上传、重连请求等关键消息无 ACK 重试机制，弱网下可能丢失 |

## 三、紧急不重要（快速改善体验）

| # | 任务 | 说明 |
|---|------|------|
| F9 | ~~Unity Demo 场景 Build Settings~~ | **已修复** — DemoSceneSetup.cs InitializeOnLoad 自动添加场景 |
| F10 | ~~ServerWindow 显示连接数/房间数~~ | **已修复** — 通过 Prometheus metrics HTTP 拉取 connections + rooms |
| F11 | ~~Drop 按钮可配置时长~~ | **已修复** — dropSeconds Inspector 字段，单个 Drop 按钮读取配置 |

## 四、不紧急不重要（技术债 + 优化）

| # | 任务 | 说明 |
|---|------|------|
| F12 | ~~FrameSyncClient 与 Person 职责边界清理~~ | **已完成** — FrameSyncClient 长生命周期拥有完整网络栈，Person 为纯代理适配器，快照回调统一由 FrameSyncClient 管理 |
| F13 | 重连时 ConnPlayerMap 多次 Store 问题 | 多次快速重连可能导致映射不一致，需要原子化处理 |
| F14 | Room tickLoop panic 恢复后的状态一致性 | recover 后 running=false 但玩家还在房间里，需要通知客户端 |
| F15 | 单元测试覆盖率提升 | 当前 Go 12 个 + C# 29 个，Room 的 stepFrame/broadcast 等热路径未覆盖 |
| F16 | autoroom 模式整合到 YAML 配置 | 目前是命令行 flag，不在 config.yaml 中 |
