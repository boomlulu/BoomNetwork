# CLI 架构总览

## 两层库结构

### Core/ — 共享协议层（零外部依赖）

纯类型定义 + 编解码，客户端和服务器共享：

```
Core/
├── Message.cs           MessageFlags, CmdType, Message 结构体
├── ErrorCode.cs         ErrorCategory, ErrorCode, NetworkError
├── Transport/
│   └── ITransport.cs    ITransport 接口, TransportState 枚举
├── Codec/
│   └── MessageCodec.cs  Message ↔ byte[] 编解码
├── Framing/
│   ├── RingBuffer.cs    追加/消费环形缓冲
│   ├── LengthPrefixFraming.cs  TCP 粘包/拆包处理
│   └── PooledFrame.cs   ArrayPool 租借帧
└── FrameSync/
    └── FrameSyncProtocol.cs  全部帧同步协议：
        - FrameSyncCmd (Core 命令 1-12)
        - FrameSyncExtCmd (Extended 命令)
        - FrameSyncInitData
        - FrameData + FrameDataCodec
        - RoomInfo + RoomCodec
        - SnapshotCodec
        - IEntitySync + EntityStateCodec
        - AuthorityTransferCodec
        - DataEntry + StateSyncCodec
```

### Client/ — 客户端网络栈（依赖 Core）

分层构建，每层只依赖下层：

```
Client/
├── Transport/
│   ├── TcpClientTransport.cs   TCP（IO 线程 + 主线程 Tick）
│   ├── KcpClientTransport.cs   KCP/UDP（纯 Tick 驱动）
│   └── KcpProject/             嵌入式 KCP 实现
├── Session/
│   └── NetworkSession.cs       序列号 + 请求响应 + 粘包处理
├── Connection/
│   ├── ConnectionManager.cs    状态机 + 心跳 + 重连编排
│   ├── IReconnectStrategy.cs   重连策略接口
│   ├── QuickReconnectStrategy.cs   快速重连（帧缓冲回放）
│   ├── SnapshotReconnectStrategy.cs 快照重连（全量恢复）
│   ├── CompositeReconnectStrategy.cs 策略链 Quick×3 → Snapshot×2
│   └── ReconnectContext.cs     重连上下文
├── Room/
│   └── RoomClient.cs           房间操作封装
└── FrameSync/
    └── FrameSyncClient.cs      顶层门面（状态机 + 全 API）
```

## 错误体系

### ErrorCategory 分类

| 分类 | 基值 | 含义 |
|------|------|------|
| Transport | 1000 | 传输层错误 |
| Session | 2000 | 会话层错误 |
| Connection | 3000 | 连接管理错误 |
| FrameSync | 4000 | 帧同步错误 |

### ErrorCode 枚举

| 错误码 | 值 | 分类 | 含义 |
|--------|-----|------|------|
| ConnectFailed | 1001 | Transport | 连接失败 |
| SendFailed | 1002 | Transport | 发送失败 |
| ConnectionDropped | 1003 | Transport | 连接断开 |
| TransportError | 1004 | Transport | 传输错误 |
| RequestTimeout | 2001 | Session | 请求超时 |
| SessionReset | 2002 | Session | 会话重置 |
| HeartbeatTimeout | 3001 | Connection | 心跳超时 |
| ReconnectFailed | 3002 | Connection | 重连失败 |
| AllStrategiesExhausted | 3003 | Connection | 所有重连策略耗尽 |
| SessionBindTimeout | 3004 | Connection | SessionBind 超时 |
| SessionBindFailed | 3005 | Connection | SessionBind 失败 |
| JoinRoomFailed | 4001 | FrameSync | 加入房间失败 |
| RoomNotFound | 4002 | FrameSync | 房间不存在 |
| RoomFull | 4003 | FrameSync | 房间已满 |
| RoomNotBound | 5004 | FrameSync | 未绑定房间 |

### NetworkError 结构体

```csharp
struct NetworkError {
    ErrorCode Code;
    string Message;
    ErrorCategory Category;   // 由 Code 推导
    bool IsTransport;          // Category == Transport
    bool IsRecoverable;        // ConnectFailed/HeartbeatTimeout/ReconnectFailed 可恢复
}
```

## 依赖方向（严禁反向依赖）

```
FrameSyncClient → RoomClient + ConnectionManager
ConnectionManager → NetworkSession + IReconnectStrategy
NetworkSession → ITransport + MessageCodec + LengthPrefixFraming
TcpClientTransport / KcpClientTransport → ITransport (Core)
```
