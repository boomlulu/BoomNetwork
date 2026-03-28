# BoomNetwork

帧驱动状态同步框架 — 让多人游戏的网络层**消失**。

C# 客户端 + Go 服务器 + Unity UPM 包。不绑定游戏类型，不要求理解回滚。

## 设计哲学

```
1. 不回滚             — 没有预测/回滚机制，逻辑天然简单
2. 不要求理解回滚      — 接入者只需 SendInput + OnFrame
3. 接入三步走          — Connect → MatchRoom → SendInput，30 行代码跑通
4. 本地零延迟          — 操作即响应，帧同步在幕后对齐
5. 无事不发包          — Silent When Idle，空闲时零网络开销
```

## 核心能力

| 能力 | 说明 |
|------|------|
| 帧同步 | 服务器 20fps tick → 收集输入 → 组帧 → 广播，客户端确定性执行 |
| 帧内嵌事件 | PlayerJoined/Left/Offline/Online/HostChanged 嵌入 FrameData，所有客户端同帧处理 |
| 房主管理 | 自动选举 + 掉线转移 + HostChanged 事件通知 |
| 两级断线重连 | 快速补帧（无感） → 快照恢复（秒级），自动降级 |
| 实体权威同步 | 权威方发状态，远端 Dead Reckoning + 惯性追踪 |
| 轻量状态同步 | KV Store + 消息中继，适合非帧同步数据 |
| 帧 Hash 校验 | 客户端每帧上报状态 Hash，服务器比对检测不同步 |
| 双传输协议 | TCP 稳定优先 / KCP 低延迟优先 |
| GM 工具 | Admin HTTP + WebSocket，实时监控、网络模拟、一键部署 |
| 零分配热路径 | 帧编码/广播复用缓冲区，无 GC 压力 |

## 项目结构

```
BoomNetwork/
├── cli/                            C# 客户端库 (.NET 8)
│   ├── Core/                         协议、编解码、帧事件
│   ├── Client/                       连接、会话、帧同步、房间、重连
│   ├── Tests/                        单元测试 (NUnit)
│   └── FrameSyncExample/             控制台集成测试
├── svr/                            Go 服务器
│   ├── cmd/framesync/                入口 + 配置 + Admin + NetSim
│   ├── framesync/                    房间、协议、帧缓冲、帧事件
│   ├── transport/                    TCP/KCP + 安全限流
│   ├── codec/                        消息编解码
│   └── session/                      三级消息路由
├── unity/
│   ├── com.boom.boomnetwork/       Unity Package (UPM) — 核心
│   │   └── Samples~/                 9 个示例
│   └── com.boom.boomnetwork.gm/    Unity Package — GM 工具
└── reports/                        性能测试报告
```

## 快速开始

### 1. 启动服务器

```bash
cd svr && go run ./cmd/framesync/ -config cmd/framesync/config.yaml
# → [FrameSync Server] Running on :9000
```

### 2. Unity 接入

`Packages/manifest.json` 添加：
```json
"com.boom.boomnetwork": "https://github.com/boomlulu/BoomNetwork.git?path=unity/com.boom.boomnetwork#dev1.0"
```

### 3. 最小代码

```csharp
var client = new FrameSyncClient();
client.OnFrame += frame => { /* frame.Inputs — 所有玩家输入 */ };
client.Connect("127.0.0.1", 9000);

// 每帧
client.Tick(Time.deltaTime * 1000);
client.SendInput(myInputBytes);
```

## 示例 (Samples~)

| 示例 | 展示能力 | 复杂度 |
|------|---------|--------|
| HelloWorld | 连接 + 匹配 + 收发帧 | 入门 |
| RoomLobby | 房间列表 + 创建/加入/离开 | 入门 |
| ChatRoom | 轻量状态同步（消息广播） | 入门 |
| StateSyncMove | KV 状态同步（位置广播） | 基础 |
| Reconnect | 两级断线重连完整流程 | 基础 |
| EntitySync | 实体权威同步 + Dead Reckoning | 进阶 |
| AuthorityTransfer | 权威转移协议 | 进阶 |
| **MinecraftDemo** | 混合同步：帧同步(方块) + 实体同步(移动) + 快照(增量) | 综合 |
| **VampireSurvivors** | 纯帧同步弹幕生存 — 512 怪零额外带宽 | 综合 |

### VampireSurvivors Demo

展示帧同步的极限场景：512 个怪物 + 256 个投射物 + 4 个玩家，**网络只传输操控输入**。

- FInt 22.10 定点数确保跨平台确定性（硬编码 SinTable，二进制 Sqrt，零 float）
- 空间哈希碰撞（20x20 网格，O(1) 查询）
- 4 种武器 + Boss 波次 + 升级系统
- 击杀爆炸 + 屏幕震动 + 宝石磁铁 + 浮动伤害数字
- 不同步检测：每帧 Hash 比对 + desync 自动暂停

### MinecraftDemo

展示三种同步模式的混合使用：

- 方块操作 → 帧同步（确定性执行顺序）
- 玩家移动 → 实体权威同步（自权威零延迟）
- 世界恢复 → 快照（dirty block tracking，增量序列化）
- 完整体素引擎：Greedy Meshing + Simplex 噪声 + Voxel AABB 物理

## 架构

```
┌──────────────────┐     TCP/KCP      ┌──────────────────────────┐
│    Unity 客户端    │ ◄──────────────► │        Go 服务器           │
│                  │                  │                          │
│  FrameSyncClient │  SessionBind     │  Session Router (3-tier)  │
│  ├ RoomClient    │  SendInput  ──►  │  ├ RoomManager            │
│  ├ ConnMgr       │  ◄── PushFrames  │  │  └ Room                │
│  │  └ Reconnect  │                  │  │     ├ tickLoop (20fps) │
│  └ EntitySync    │  FrameHash  ──►  │  │     ├ frameRing (2400) │
│                  │  ◄── Mismatch    │  │     ├ pendingEvents     │
│  OnTakeSnapshot ─┤──► Upload        │  │     └ snapshot          │
│  OnLoadSnapshot ◄┤──  Push          │  └ Admin HTTP + WS        │
└──────────────────┘                  └──────────────────────────┘
```

### 帧事件系统

```
玩家 join/leave/offline/online
        │
        ├── 房间运行中 → 帧内嵌事件（FrameEvent）
        │                 → 所有客户端同帧处理，确定性保证
        │
        └── 房间未运行 → ExtCmd 立即广播
```

### 重连流程

```
断线 → Stage 1: 快速补帧（帧缓冲内，无感恢复）
         ↓ 缓冲区溢出
       Stage 2: 快照重连（加载快照 + 补帧追赶）
         ↓ 失败
       彻底断开
```

## 测试

```bash
# Go 服务端（29 个测试，含 1000 帧压力测试）
cd svr && go test ./...

# C# 客户端
cd cli && dotnet test
```

## 服务器配置

```yaml
frameRate: 20                # 帧率
frameBufferSize: 2400        # 帧缓冲（决定快速重连窗口 = 2400/20 = 120秒）
snapshotIntervalFrames: 100  # 快照间隔
maxPlayers: 4                # 每房间最大玩家数
disconnectKeepSec: 120       # 断线保留时长
```

## 生产部署

- 腾讯云验证通过（systemd 托管）
- GM 工具支持 SSH 远程启停 + 一键发布流水线
- 网络模拟（延迟/抖动/丢包注入）用于测试弱网表现

## License

MIT
