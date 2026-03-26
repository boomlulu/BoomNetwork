# BoomNetwork

Unity 多人帧同步网络框架 — C# 客户端 + Go 服务器。

内置房间管理、快照重连、断线恢复、实体权威同步。

## 特性

- **帧同步核心** — 服务器定频 tick，收集输入→组帧→广播，客户端确定性执行
- **TCP / KCP 双传输** — TCP 稳定优先，KCP 低延迟优先，运行时可切换
- **房间管理** — 创建/加入/离开/列表查询，支持中途加入（已有玩家列表同步）
- **两级断线重连** — 快速重连（补帧恢复）→ 快照重连（加载快照+补帧），自动降级
- **快照系统** — 客户端定时上传快照，服务器存储最新，重连/迟到者恢复用
- **实体权威同步** — 管理者发权威状态，远端 Dead Reckoning + 惯性追踪，无需确定性
- **零分配热路径** — 帧编码/广播复用缓冲区，无 GC 压力
- **YAML 配置** — 服务器全部参数可配置，关键参数下发客户端

## 项目结构

```
BoomNetwork/
├── cli/                          # C# 客户端库
│   ├── Core/                     #   协议、编解码、实体同步
│   ├── Client/                   #   连接、会话、帧同步、房间、重连策略
│   ├── Tests/                    #   单元测试 (NUnit)
│   └── FrameSyncExample/         #   控制台集成测试
├── svr/                          # Go 服务器
│   ├── cmd/framesync/            #   主程序 + 配置
│   ├── framesync/                #   房间、协议、帧缓冲
│   ├── transport/                #   TCP/KCP 传输层
│   ├── codec/                    #   消息编解码
│   └── session/                  #   消息路由
├── unity/com.boom.boomnetwork/   # Unity Package (UPM)
└── TODO/                         # 路线图
```

## 快速开始

### 环境要求

- Go 1.22+
- .NET 8 SDK（跑 cli 测试用，Unity 项目不需要）
- Unity 2022.3+

### 1. 启动服务器

```bash
cd svr
go run ./cmd/framesync/ -config cmd/framesync/config.yaml
```

看到 `[FrameSync Server] Running on :9000` 即成功。

首次使用可生成默认配置：

```bash
go run ./cmd/framesync/ -gen-config    # 生成 config.yaml
```

### 2. Unity 接入

在 Unity 项目的 `Packages/manifest.json` 中添加：

```json
"com.boom.boomnetwork": "https://github.com/luwenyiCC/BoomNetwork.git?path=unity/com.boom.boomnetwork#dev1.0"
```

### 3. 最小代码示例

```csharp
using BoomNetwork.Client.FrameSync;

// 一行创建，内部自动构建完整网络栈
var client = new FrameSyncClient(heartbeatIntervalMs: 3000, heartbeatTimeoutMs: 10000);

// 监听事件
client.OnConnected += () => Debug.Log($"Connected as P{client.PlayerId}");
client.OnFrame += frame => {
    // 处理帧数据：frame.FrameNumber, frame.Inputs
};

// 连接
client.Connect("127.0.0.1", 9000);

// 每帧调用
void Update() {
    client.Tick(Time.deltaTime * 1000);
}
```

生产环境推荐使用 `Person` 封装类（见 Demo 工程），它封装了完整的连接→房间→帧同步→重连流程。

### 4. 跑测试验证

```bash
# Go 服务端测试
cd svr && go test ./...

# C# 客户端测试
cd cli && dotnet test

# 集成测试（需要先启动服务器）
cd svr && go run ./cmd/framesync/ -autoroom -config cmd/framesync/config.yaml &
cd cli && dotnet run --project FrameSyncExample
```

## 服务器配置

配置文件 `config.yaml`，关键参数：

```yaml
frameRate: 20               # 帧率（帧/秒）
frameBufferSize: 2400        # 环形帧缓冲区（帧数），决定重连补帧窗口
snapshotIntervalFrames: 100  # 快照间隔（帧数），下发给客户端
quickReconnectMaxMs: 5000    # 快速重连超时（ms），下发给客户端
disconnectKeepSec: 120       # 断线玩家保留时长（秒），即最大重连窗口
```

完整参数见 [svr/cmd/framesync/config.yaml](svr/cmd/framesync/config.yaml)。

## 架构概览

```
┌──────────────┐     TCP/KCP      ┌──────────────────────┐
│  Unity 客户端  │ ◄──────────────► │     Go 服务器          │
│              │                  │                      │
│  Person      │  SessionBind     │  Session Router      │
│  ├ FrameSync │  FrameInput ──►  │  ├ Room Manager      │
│  ├ RoomClient│  ◄── PushFrames  │  │  ├ Room            │
│  └ ConnMgr   │  Reconnect ──►   │  │  │  ├ TickLoop     │
│     └ Strategy│  ◄── ReconnectRsp│  │  │  ├ FrameRing    │
│              │                  │  │  │  └ Snapshot      │
│  Snapshot ──►│  UploadSnapshot  │  │  └ Player[]        │
│  ◄── Load    │                  │  └ ConnPlayerMap      │
└──────────────┘                  └──────────────────────┘
```

### 重连流程

```
断线 → Stage 1: 快速重连（补帧，无感恢复）
         │ 失败（缓冲区溢出 BufferStale）
         ▼
       Stage 2: 快照重连（加载快照 + 补帧追赶）
         │ 失败
         ▼
       彻底断开
```

## Demo 工程

Unity Demo 在独立仓库 [BoomNetworkUnity](https://github.com/luwenyiCC/BoomNetworkUnity)：

| 场景 | 说明 |
|------|------|
| Demo01-Basic | 单编辑器双 Person，基础帧同步 |
| Demo01.1-MultiClient | ParrelSync 多编辑器，快照重连测试 |
| Demo02-EntitySync | 实体权威同步（单编辑器双人） |

## 开发

```bash
# 服务器开发
cd svr && go build ./cmd/framesync/
cd svr && go test ./...

# 客户端开发
cd cli && dotnet build
cd cli && dotnet test

# 生成默认配置
cd svr && go run ./cmd/framesync/ -gen-config
```

## 文档

- [Unity 集成指南](doc/unity-integration.md) — 从零接入，30 行代码跑通
- [API Reference](doc/api-reference.md) — Person / FrameSyncClient / RoomClient 全接口 + 协议命令表
- [服务器部署指南](doc/deployment.md) — Docker / 二进制 / systemd
- [框架路线图](doc/roadmap.md) — 四象限规划

## License

MIT
