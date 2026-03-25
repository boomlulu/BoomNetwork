# BoomNetwork 路线图

> **核心定位**：通用帧同步框架，先自己用 → 验证 → 开源推广
>
> **当前阶段**：技术基础设施已完成，下一步是**降低接入复杂度**
>
> **第一性原理**：框架的价值 = 用户能多快跑通第一个多人游戏

---

## 已完成 ✅

### 技术基础设施

| 模块 | 完成内容 |
|------|---------|
| 传输层 | TCP + KCP 双协议，C# 客户端 + Go 服务器 |
| 帧同步 | 帧收发、输入广播、快照上传/恢复、迟到者加入 |
| 重连 | CompositeReconnectStrategy（快速重连 + 快照降级） |
| 房间管理 | 创建/加入/离开/列表，autoroom 模式 |
| 预测回滚 | PredictionManager 框架（Demo02 待完善） |
| FrameSyncClient 重构 | 长生命周期，内置完整网络栈，Person 瘦身为纯适配器 |

### GM 工具

| 功能 | 端点/UI |
|------|---------|
| 健康检查 | GET /health（免鉴权）|
| 流量统计（Game + GM 分离） | GET /stats |
| 消息日志（100 条 + payload 解码） | GET /messages |
| 房间列表 + 玩家在线状态 | GET /rooms |
| 踢人 / 停房间 | POST /kick/{pid}、/rooms/stop/{id} |
| 玩家详情 / 性能 / 限流 | GET /players/{pid}、/perf、/rates |
| Admin 鉴权 | Bearer Token middleware |
| Unity ServerWindow 三页面板 | Dashboard / Messages / Rooms |

### 文档

| 文档 | 状态 |
|------|------|
| quickstart.md | ✅ 架构 + 模块 + 接入示例 |
| api-reference.md | ✅ Person + FrameSyncClient 全接口 |
| configuration.md | ✅ 所有可配参数 + 调参建议 |
| gm-tools.md | ✅ 9 端点 + Unity UI + 鉴权 |
| architecture.md | ✅ 分层设计 + 数据流 |
| concepts.md | ✅ 帧同步核心概念 |
| deployment.md | ✅ Docker + 二进制 + systemd |

### 质量保障

| 项 | 状态 |
|----|------|
| C# 单元测试 29 项 | ✅ |
| Go 单元测试 12 项 | ✅ |
| 跨语言兼容测试 | ✅ C# ↔ Go fixture |
| 帧同步集成测试 15 项 | ✅ 心跳 + 快速重连 + 快照重连 |
| KCP Echo 测试 7 项 | ✅ |

---

## 一、紧急且重要 — 降低接入门槛（阻断推广的障碍）

> **判断标准**：新用户 clone 后能否 5 分钟跑通多人游戏？

| # | 任务 | 说明 | 状态 |
|---|------|------|------|
| N1 | **服务器一键启动** | 提供预编译二进制（macOS/Linux/Windows）+ `./boom-server` 零依赖启动。Docker 镜像打包。目标：不装 Go 也能跑 | 待做 |
| N2 | **Quickstart 教程重写** | 从"文档"升级为"教程"：① clone ② 启动服务器 ③ 打开 Unity Demo ④ 看到两个方块移动。每步带截图/GIF，预估时间标注 | 待做 |
| N3 | **Demo 即模板** | 现有 Demo01 整理为可直接复制的项目模板。新用户 fork 后改 OnFrame 和 SendInput 即可做自己的游戏 | 待做 |
| N4 | **生命周期流程图** | Mermaid 状态机：连接→绑定→建房→入房→帧同步→断线→重连。一图胜千字，放在 quickstart 最醒目位置 | 待做 |

## 二、重要不紧急 — 框架健壮性（决定能否上生产）

| # | 任务 | 说明 | 状态 |
|---|------|------|------|
| N5 | **错误码文档** | 所有 ErrorCode + 触发场景 + 客户端推荐处理方式 | 待做 |
| N6 | **config.yaml 参数指南** | 参数含义、推荐值、参数间关系（如 frameBufferSize ≥ disconnectKeepSec × frameRate） | 待做 |
| N7 | **KCP 集成测试覆盖** | TCP 路径测试充分，KCP 路径未在帧同步集成测试和 Demo 中覆盖 | 待做 |
| N8 | **版本号 + CHANGELOG** | 语义化版本 v0.1.0 → v0.2.0，每版变更记录 | 待做 |
| N9 | **F5 服务器推送完整房间状态** | JoinRoomRsp 带 existingPlayers 但不带在线/离线状态 | 待做 |
| N10 | **F6 帧同步暂停/恢复通知** | 快照过期暂停时客户端不知道，应通知暂停原因 + 恢复事件 | 待做 |
| N11 | **F8 关键消息 ACK 重试** | 快照上传、重连请求等弱网下可能丢失 | 待做 |
| N12 | **性能基准报告** | N 客户端 × 帧率 × 延迟 × 内存的量化数据，给用户信心 | 待做 |

## 三、紧急不重要 — 快速完善

| # | 任务 | 说明 | 状态 |
|---|------|------|------|
| N13 | **Demo02 预测回滚完善** | 框架代码已有，Demo 场景未完成 | 待做 |
| N14 | **F13 重连 ConnPlayerMap 原子化** | 多次快速重连可能映射不一致 | 待做 |
| N15 | **F14 Room panic 恢复一致性** | recover 后 running=false 但玩家还在 | 待做 |
| N16 | **F16 autoroom 整合到 config.yaml** | 目前是命令行 flag，不在配置中 | 待做 |
| N17 | **单元测试覆盖率提升** | Room stepFrame/broadcast 热路径 | 待做 |

## 四、不紧急不重要 — 远期愿景

| # | 任务 | 说明 | 状态 |
|---|------|------|------|
| N18 | 多语言客户端示例 | TypeScript / Lua / Go 客户端 | 待做 |
| N19 | 服务器集群方案 | 多房间服务器横向扩展、负载均衡 | 待做 |
| N20 | 加密传输 | TLS / KCP 加密选项 | 待做 |
| N21 | 匹配系统 | 按技能/延迟匹配，排队等待 | 待做 |
| N22 | 协议文档（Wire Format） | 二进制格式文档，非 C# 客户端据此接入 | 待做 |

---

## 下一步建议

```
优先做 N1-N4（第一象限）：
  N4 流程图 → N2 教程重写 → N1 一键启动 → N3 模板化 Demo

验收标准：
  一个没接触过 BoomNetwork 的 Unity 开发者
  clone → 5 分钟内看到两个角色在屏幕上移动
  不需要问任何人
```
