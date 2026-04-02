---
name: bn-svr
description: BoomNetwork Go 服务器完整架构。涵盖传输层、编解码、会话路由、帧同步、房间管理、实体权威、Admin/GM、安全、运维、网络模拟、配置 11 大子系统。
allowed-tools:
  - Bash
  - Read
  - Edit
  - Write
  - Glob
  - Grep
  - Agent
---

# BoomNetwork Go Server — 完整架构 Skill

## Slash Commands

| Command | Description |
|---|---|
| `/svr overview` | 打印服务器整体架构图 + 启动流程 + 包依赖关系 |
| `/svr transport` | 传输层：TCP/KCP Server、Conn 抽象、连接生命周期 |
| `/svr codec` | 编解码：三层命令空间、线格式、FrameReader/FrameWriter |
| `/svr session` | 会话路由：Router 三级分发、Handler 签名、Freeze 机制 |
| `/svr framesync` | 帧同步核心：tickLoop、stepFrame、帧环形缓冲、重连 |
| `/svr room` | 房间管理：Room/RoomManager/Player 生命周期、匹配 |
| `/svr entity` | 实体权威同步：权威表、转移协议、状态中继 |
| `/svr admin` | Admin/GM：HTTP REST 端点、WebSocket Hub、Stats |
| `/svr security` | 安全：认证、限流、Origin 白名单、消息大小限制 |
| `/svr ops` | 运维：systemd、Docker、Prometheus、信号处理、优雅关闭 |
| `/svr netsim` | 网络模拟：延迟/抖动/丢包注入 |
| `/svr config` | 配置系统：YAML/CLI/环境变量优先级、热重载 |
| `/svr add-cmd` | 引导：添加一个新的协议命令（Core/Extended/Game） |
| `/svr debug` | 引导：排查常见服务器问题（连接断开/帧卡顿/内存泄漏） |

## Architecture Overview

```
┌─────────────────────────────────────────────────────────┐
│                     cmd/framesync/main.go               │
│  (启动入口: 配置加载 → 组件装配 → 监听 → 信号处理)       │
└────────┬───────────┬──────────┬──────────┬──────────────┘
         │           │          │          │
    ┌────▼────┐ ┌────▼────┐ ┌──▼───┐ ┌───▼────┐
    │transport│ │ session │ │admin │ │  ops   │
    │TCP/KCP  │ │ Router  │ │HTTP  │ │metrics │
    │  Conn   │ │ 3-tier  │ │ WS   │ │systemd │
    └────┬────┘ └────┬────┘ └──┬───┘ └────────┘
         │           │         │
    ┌────▼────┐ ┌────▼─────────▼──────────────┐
    │  codec  │ │       framesync             │
    │ Frame   │ │ Room · RoomManager · Player │
    │ R/W     │ │ tickLoop · entityAuthority  │
    └─────────┘ │ protocol · metrics          │
                └─────────────────────────────┘
```

## Package Map

| Package | Path | 职责 |
|---|---|---|
| `codec` | `svr/codec/` | 二进制线格式：帧分割、编解码、sync.Pool 复用 |
| `framesync` | `svr/framesync/` | 核心逻辑：Room、RoomManager、协议常量、Prometheus 指标 |
| `session` | `svr/session/` | 消息路由：三级 Router（Core/Extended/Game）、冻结快照 |
| `transport` | `svr/transport/` | 网络服务器：TCP/KCP、Conn 抽象、限流、安全 |
| `main` | `svr/cmd/framesync/` | 入口二进制：配置、Handler 注册、Admin、Stats、NetSim |

## Key Source Files

| File | Lines | 核心内容 |
|---|---|---|
| `cmd/framesync/main.go` | ~600 | 启动装配 + 全部 Handler 注册 + 信号处理 |
| `cmd/framesync/config.go` | ~200 | ServerConfig + YAML 加载/保存 + 默认值 |
| `cmd/framesync/admin.go` | ~350 | Admin HTTP REST 14 端点 |
| `cmd/framesync/admin_ws.go` | ~400 | WebSocket GMHub + GMConn + 话题推送 |
| `cmd/framesync/admin_ws_msg.go` | ~100 | WS MessagePack 消息类型定义 |
| `cmd/framesync/stats.go` | ~250 | TrafficTracker + msgRing + playerRate |
| `cmd/framesync/netsim.go` | ~100 | NetSimConfig + simConn 延迟注入 |
| `framesync/room.go` | ~700 | Room 全部逻辑 + tickLoop + 重连 |
| `framesync/room_manager.go` | ~200 | RoomManager CRUD + 匹配 + 清理 |
| `framesync/protocol.go` | ~400 | Cmd/ExtCmd 常量 + 二进制编解码函数 |
| `framesync/metrics.go` | ~60 | Prometheus 指标定义 |
| `codec/message.go` | ~200 | Message 结构 + 编解码 + 工厂方法 |
| `codec/framing.go` | ~200 | FrameReader/FrameWriter + 缓冲 I/O |
| `session/session.go` | ~120 | Router + Handler 注册 + Freeze |
| `transport/tcp_server.go` | ~250 | TcpServer + acceptLoop + handleConn |
| `transport/kcp_server.go` | ~200 | KcpServer (镜像 TCP) |
| `transport/security.go` | ~150 | SecurityConfig + RateLimiter + IPRateLimiter |

## 11 Subsystems Quick Index

1. **Transport** — TCP/KCP 双协议、Conn 读写、连接生命周期 → [reference/transport.md](reference/transport.md)
2. **Codec** — 三层命令空间线格式、帧分割、零拷贝读 → [reference/codec.md](reference/codec.md)
3. **Session** — Router 三级分发、Handler 签名、Freeze 无锁快照 → [reference/session.md](reference/session.md)
4. **FrameSync** — tickLoop 帧驱动、stepFrame 热路径、重连两阶段、**帧内嵌事件 + Host 管理** → [reference/framesync.md](reference/framesync.md)
5. **Room** — Room/RoomManager/Player 生命周期、匹配、容量控制、**pendingEvents 队列** → [reference/room.md](reference/room.md)
6. **Entity** — 权威表、转移协议、状态中继、断线释放 → [reference/entity.md](reference/entity.md)
7. **Admin** — HTTP REST 14 端点 + WebSocket GMHub 话题推送 → [reference/admin.md](reference/admin.md)
8. **Security** — 客户端/Admin 认证、限流三层、Origin 白名单 → [reference/security.md](reference/security.md)
9. **Ops** — systemd notify、Docker、Prometheus 17 指标、优雅关闭 → [reference/ops.md](reference/ops.md)
10. **NetSim** — simConn 延迟/抖动/丢包注入、安全阀 → [reference/netsim.md](reference/netsim.md)
11. **Config** — YAML/CLI/环境变量三级优先级、热重载 → [reference/config.md](reference/config.md)

## Key Rules

1. **所有 Handler 在 main.go 注册**，通过 `router.RegisterCore/RegisterExt/SetGameHandler` 注册后 `Freeze()` 冻结
2. **Room 是并发隔离单元**，每个 Room 一个 goroutine（tickLoop），Room 内操作通过 `room.mu` 同步
3. **PlayerConn 接口是装饰器链**：`transport.Conn` → `statsConn`（统计） → `simConn`（网络模拟）
4. **三个全局 sync.Map** 管理连接-玩家-房间映射：`connPlayerMap`、`playerRoomMap`、`playerConnMap`
5. **帧环形缓冲** 是固定大小，旧帧被覆盖；重连时若请求帧已覆盖则回退到快照重连
6. **ExtCmd 命名空间** 用于框架扩展命令（房间/实体/快照），**GameCmd 命名空间** 保留给业务层
7. **Admin 端点 /health 无需认证**，其余全部需要 Bearer Token
8. **热重载仅支持 logLevel 和 maxMessageSize**，其他配置需重启
9. **玩家事件双路径**：房间运行中 → 帧内嵌事件；房间未运行 → ExtCmd 广播（两条路径互斥，见下方）
10. **不同步防御**：客户端 Simulation 代码的确定性由 [bn-desync SKILL](../bn-desync/SKILL.md) 约束。服务端保证：帧事件嵌入 FrameData 确保同帧到达、stepFrame 顺序确定、pendingEvents 队列 FIFO

## Frame-Embedded Player Events & Host Management

### FrameEvent 线格式

`FrameData` 增加尾部事件段（向后兼容：DecodeFrameData 先读 inputs，再 `if offset < len(buf)` 才读 events）：

```
[FrameNumber:4][InputCount:2][...inputs...][EventCount:1][...events(5B each)...]
每条 FrameEvent = [EventType:1][PlayerId:4]
```

### 事件类型常量（`framesync/protocol.go`）

| 常量 | 值 | 含义 |
|---|---|---|
| `FrameEventPlayerJoined` | 1 | 玩家加入房间 |
| `FrameEventPlayerLeft` | 2 | 玩家离开房间 |
| `FrameEventPlayerOffline` | 3 | 玩家断线 |
| `FrameEventPlayerOnline` | 4 | 玩家重连恢复 |
| `FrameEventHostChanged` | 5 | Host 发生变更（附带新 Host PlayerId） |

### Room 新增字段与方法（`framesync/room.go`）

```go
// 字段
pendingEvents []FrameEvent   // 当前帧待下发的事件队列
hostPlayerId  int32           // 当前 Host 玩家 ID（0 = 无 Host）

// 方法
func (r *Room) EnqueueEvent(eventType byte, playerId int32)  // 追加事件到队列
func (r *Room) HostPlayerId() int32                          // 读取当前 Host（加锁）
func (r *Room) electHost()                                   // 选取首个 online 玩家为 Host
func (r *Room) setHost(id int32)                             // 设置 Host + 自动 EnqueueEvent(HostChanged)
```

`stepFrame()` 在构建 `FrameData` 时将 `pendingEvents` 整体拼入，发送后清空队列。

### 双路径：同步模式 vs 非同步模式

```
玩家 join / leave / offline / online
        │
        ├── room.IsRunning() == true
        │       └─▶ room.EnqueueEvent(...)   // 下一帧随帧下发，有序、确定性
        │
        └── room.IsRunning() == false
                └─▶ ExtCmd 广播（Cmd 20-23） // 立即广播，无帧序号保证
```

### Host 选举规则

1. 第一个加入房间的玩家自动成为 Host（`setHost` 在 join 处理时调用）
2. Host 断线或主动离开 → `electHost()` 从当前 online 玩家中取第一个设为新 Host
3. 所有玩家全部离线后再有人重连 → `electHost()` 再次执行，首个重连者成为 Host
4. Host 变更始终通过 `FrameEventHostChanged` 事件通知所有客户端（sync 模式）或 ExtCmd（非 sync 模式）
