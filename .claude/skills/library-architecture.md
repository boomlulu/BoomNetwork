# BoomNetwork 库架构技能

## 仓库

```
/Users/boom/Demo/BoomNetwork
github: luwenyiCC/BoomNetwork
branch: dev1.0
```

## 分层架构

```
Layer 0: Transport    → 字节收发 + 连接管理
Layer 1: Framing      → 粘包/拆包 (LengthPrefix + RingBuffer)
Layer 2: Codec        → Message ↔ bytes (动态包头 FlagsCmd)
Layer 3: Session      → seq/ack + SendAsync + 超时 + 消息路由
Layer 4: Connection   → 心跳 + 重连策略 (IReconnectStrategy)
Layer 5: FrameSync    → 帧同步客户端 + 预测回滚
```

## 目录结构

```
cli/                               ← C# 客户端
├── Core/                          ← 核心库（无 Unity 依赖）
│   ├── Message.cs                 ← Message struct + ErrorCode
│   ├── Codec/MessageCodec.cs      ← 编解码（动态包头）
│   ├── Framing/
│   │   ├── LengthPrefixFraming.cs ← 粘包/拆包
│   │   └── RingBuffer.cs          ← 环形缓冲区
│   ├── FrameSync/
│   │   └── FrameSyncProtocol.cs   ← Cmd 常量 + 帧数据编解码
│   ├── Transport/
│   │   └── ITransport.cs          ← 传输层接口
│   └── Prediction/
│       ├── ISimulation.cs         ← 游戏层实现的确定性模拟接口
│       ├── PredictionManager.cs   ← 预测/对比/回滚编排
│       ├── InputBuffer.cs         ← 帧输入环形缓冲区
│       └── SnapshotBuffer.cs      ← 帧快照环形缓冲区
│
├── Client/                        ← 客户端实现
│   ├── Transport/
│   │   ├── TcpClientTransport.cs  ← TCP 传输
│   │   ├── KcpClientTransport.cs  ← KCP 传输
│   │   └── Kcp/                   ← KCP 协议实现（limpo1989/kcp-csharp）
│   ├── Session/
│   │   └── NetworkSession.cs      ← 会话层
│   ├── Connection/
│   │   ├── ConnectionManager.cs   ← 连接生命周期 + 心跳
│   │   ├── IReconnectStrategy.cs  ← 重连策略接口
│   │   ├── QuickReconnectStrategy.cs
│   │   ├── SnapshotReconnectStrategy.cs
│   │   └── CompositeReconnectStrategy.cs
│   ├── FrameSync/
│   │   └── FrameSyncClient.cs     ← 帧同步客户端
│   └── Room/
│       └── RoomClient.cs          ← 房间管理客户端
│
├── Tests/                         ← 单元测试
├── Benchmark/                     ← 性能基准
├── Example/                       ← Echo 客户端示例
├── FrameSyncExample/              ← 帧同步客户端示例
└── StressTest/                    ← C#↔Go 跨语言压测

svr/                               ← Go 服务器
├── cmd/
│   ├── echo/main.go
│   ├── framesync/main.go
│   ├── stress/main.go
│   └── kcpstress/main.go
├── codec/                         ← 消息编解码（和 C# 线格式一致）
├── framesync/                     ← 帧同步核心
│   ├── protocol.go                ← Cmd 常量 + 编解码
│   ├── room.go                    ← 房间（帧推送 + 快照 + 帧缓冲）
│   └── room_manager.go            ← 房间管理
├── session/                       ← Router + Handler
└── transport/                     ← TCP/KCP 服务器 + 安全

unity/com.boom.boomnetwork/        ← UPM 包（Unity 导入用）
├── package.json
├── Runtime/
│   ├── BoomNetwork.Runtime.asmdef
│   ├── Core/                      ← 从 cli/Core 复制
│   └── Client/                    ← 从 cli/Client 复制
└── README.md
```

## 包头格式

```
[FlagsCmd: 1B][BodyLen: 2B or 4B][Seq: 0B or 4B]

FlagsCmd:
  bit 0:   LenSize  (0=BodyLen 2B, 1=BodyLen 4B)
  bit 1:   HasSeq   (0=无 Seq, 1=有 4B Seq)
  bit 2-7: Cmd      (0-63)

推帧: FlagsCmd(1) + BodyLen(2) = 3 bytes（最小包头）
请求: FlagsCmd(1) + BodyLen(2) + Seq(4) = 7 bytes
大包: FlagsCmd(1) + BodyLen(4) = 5 bytes
```

## UPM 包同步流程

修改 cli/ 下的 C# 代码后：
1. 复制到 unity/com.boom.boomnetwork/Runtime/
2. git commit + push
3. Unity 中 Package Manager → Update BoomNetwork

注意：unity/ 下的 .meta 文件必须保留（Python 脚本生成）

## 性能基线

```
C# Encode_Small:        3.6 ns, 0 alloc
C# Decode_Small_Pooled: 12.6 ns, 0 alloc
C# Framing 100 sticky:  3.9 μs, 0 alloc
压测 4000 人 15 分钟:   20.0 fps, 200 MB heap, 126 GC
```

## 安全配置

```
MaxMessageSize:    64 KB
MaxMessagesPerSec: 500
BurstAllowance:    20
Room cleanup:      120s after all offline
Frame buffer:      200 frames (10s)
Snapshot interval:  100 frames (5s)
```
