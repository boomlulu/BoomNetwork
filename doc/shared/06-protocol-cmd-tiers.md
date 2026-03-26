# 三层 Cmd 分级协议

## 设计动机

框架消息（连接、心跳、帧同步）与游戏消息（技能、聊天、自定义事件）的生命周期完全不同：

- 框架消息由服务器解析、处理、路由
- 游戏消息服务器**不需要理解**，只做转发

将两者混在同一个 Cmd 空间会导致：添加任何游戏功能都要改服务器代码。

## 三层架构

```
┌─────────────────────────────────────────────────────────────┐
│                     FlagsCmd (1 byte)                        │
├───────┬───────┬─────────┬───────────────────────────────────┤
│ bit 0 │ bit 1 │ bit 2-3 │ bit 4-7                           │
│LenSize│HasSeq │ CmdType │ CoreCmd (仅 CmdType=00)           │
└───────┴───────┴─────────┴───────────────────────────────────┘

CmdType=00  Core      → Cmd 在 FlagsCmd 高 4 位 (0-15)
CmdType=01  Extended  → ExtCmd uint16 在 body 内
CmdType=10  Game      → GameCmd uint32 在 body 内
CmdType=11  Reserved
```

## Wire Format

### Core 消息（包头 3-9 字节）
```
[FlagsCmd:1][BodyLen:2/4][Seq:0/4][Data:N]
```
- 最高频：FrameInput、PushFrames、Heartbeat
- Cmd 就在 FlagsCmd 高 4 位，**零额外开销**

### Extended 消息（包头 5-11 字节）
```
[FlagsCmd:1][BodyLen:2/4][Seq:0/4][ExtCmd:2][Data:N]
```
- 中频：房间管理、快照、实体同步
- ExtCmd uint16，65536 个命令空间

### Game 消息（包头 7-13 字节）
```
[FlagsCmd:1][BodyLen:2/4][Seq:0/4][GameCmd:4][Data:N]
```
- 游戏自定义：服务器**原样转发**（prepend senderPid）
- GameCmd uint32，42 亿个命令空间
- **添加游戏功能永远不需要改服务器**

## 命令分配

### Core (0-15)

| Cmd | 名称 | 方向 |
|-----|------|------|
| 1 | SessionBind | C→S |
| 2 | SessionBindRsp | S→C |
| 3 | RequestStart | C→S |
| 4 | StartFrameSync | S→C (broadcast) |
| 5 | StopFrameSync | 双向 |
| 6 | FrameInput | C→S |
| 7 | PushFrames | S→C (broadcast) |
| 8 | Heartbeat | C→S |
| 9 | HeartbeatRsp | S→C |
| 10 | Reconnect | C→S |
| 11 | ReconnectRsp | S→C |
| 12-15 | 保留 | — |

### Extended (uint16)

| ExtCmd | 名称 | 方向 |
|--------|------|------|
| 1-10 | 房间管理 (GetRooms~MatchRoomRsp) | C↔S |
| 20-24 | 服务器推送 (PlayerJoined~RoomSnapshot) | S→C |
| 30-31 | 快照 (Upload/Rsp) | C↔S |
| 40-42 | 实体同步 (SendEntityState~AuthorityTransfer) | C↔S |

### Game (uint32)

由游戏层自行定义，服务器透传。转发时自动在 Data 前插入 senderPid (4 bytes)。

## 包头大小对比

| 场景 | 旧格式 | 新格式 | 变化 |
|------|--------|--------|------|
| FrameInput (最高频) | 3B | 3B | 不变 |
| Heartbeat | 3B | 3B | 不变 |
| JoinRoom | 3B | 5B | +2B |
| 游戏消息 | 不支持 | 7B | 新增 |

核心路径零开销，非核心路径多 2 字节，换来框架/游戏完全解耦。

## Client API

```csharp
// Core (框架内部使用)
_session.Send(FrameSyncCmd.FrameInput, inputData);

// Extended (框架扩展)
_session.SendExt(FrameSyncExtCmd.JoinRoom, roomData);

// Game (游戏自定义)
client.SendGameMessage(gameCmd, data);

// 接收游戏消息
client.OnGameMessage += (gameCmd, senderPid, data, offset) => { ... };
```

## Server 路由

```go
router.OnCore(framesync.CmdFrameInput, handleFrameInput)     // Core
router.OnExt(framesync.ExtCmdJoinRoom, handleJoinRoom)        // Extended
router.OnGame(handleGameRelay)                                 // Game → 自动转发
```
