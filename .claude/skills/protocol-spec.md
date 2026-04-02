# 协议规范技能

## 线格式（Wire Format）

C# 和 Go 必须严格一致。任何修改必须双端同步。

### 包头（动态长度）

```
[FlagsCmd: 1 byte]
  bit 0:   LenSize  (0 = BodyLen 2B, 1 = BodyLen 4B)
  bit 1:   HasSeq   (0 = 无 Seq, 1 = 有 4B Seq)
  bit 2-7: Cmd      (0-63)

[BodyLen: 2 or 4 bytes, little-endian]
  LenSize=0: uint16, 最大 65535 bytes
  LenSize=1: uint32, 最大 4GB

[Seq: 0 or 4 bytes, little-endian]
  HasSeq=0: 不发 Seq（Push 消息）
  HasSeq=1: int32 Seq（Request/Response 匹配用）
```

### 包头大小组合

```
Push 消息（服务器推帧）:    FlagsCmd(1) + BodyLen(2) = 3 bytes
Request/Response:           FlagsCmd(1) + BodyLen(2) + Seq(4) = 7 bytes
大包 Push（快照）:          FlagsCmd(1) + BodyLen(4) = 5 bytes
大包 Request:               FlagsCmd(1) + BodyLen(4) + Seq(4) = 9 bytes
```

## Cmd 常量表（Core Namespace）

| Cmd | 值 | 方向 | 说明 |
|-----|---|----|------|
| SessionBind | 1 | C→S | 绑定会话 |
| SessionBindRsp | 2 | S→C | 绑定响应（playerId） |
| RequestStart | 3 | C→S | 请求开始帧同步 |
| StartFrameSync | 4 | S→C | 帧同步开始（广播） |
| FrameInput | 5 | C→S | 玩家输入 |
| PushFrames | 6 | S→C | 推帧（含帧内嵌事件） |
| Heartbeat | 7 | C→S | 心跳 |
| HeartbeatRsp | 8 | S→C | 心跳响应（含 RTT 时间戳） |
| Reconnect | 9 | C→S | 重连（带旧 playerId） |
| ReconnectRsp | 10 | S→C | 重连响应 |
| StopFrameSync | 21 | S→C | 帧同步停止 |
| GetRooms | 60 | C→S | 获取房间列表 |
| GetRoomsRsp | 61 | S→C | 房间列表响应 |
| CreateRoom | 62 | C→S | 创建房间 |
| CreateRoomRsp | 63 | S→C | 创建响应（roomId） |
| JoinRoom | 64 | C→S | 加入房间 |
| JoinRoomRsp | 65 | S→C | 加入响应（playerId） |
| LeaveRoom | 66 | C→S | 离开房间 |
| LeaveRoomRsp | 67 | S→C | 离开响应 |
| UploadSnapshot | 70 | C→S | 上传快照 |
| UploadSnapshotRsp | 71 | S→C | 上传确认 |
| MatchRoom | 72 | C→S | 匹配房间（按 matchKey） |
| MatchRoomRsp | 73 | S→C | 匹配响应（playerId + roomId） |

## ExtCmd 常量表（Extended Namespace，框架扩展）

| ExtCmd | 值 | 方向 | 说明 |
|--------|---|----|------|
| PlayerJoined | 20 | S→C | 玩家加入（非同步模式广播） |
| PlayerLeft | 21 | S→C | 玩家离开（非同步模式广播） |
| PlayerOffline | 22 | S→C | 玩家断线（非同步模式广播） |
| PlayerOnline | 23 | S→C | 玩家重连（非同步模式广播） |
| EntityState | 27 | S→C | 实体权威状态广播 |
| AuthorityTransfer | 28 | S→C | 权威转移 |
| StateSync | 30 | S→C | KV 轻量状态同步（任意键值对） |

> 同步模式下（room.IsRunning==true），玩家事件通过**帧内嵌 FrameEvent** 随帧下发（见下方）；
> 非同步模式下才走 ExtCmd 20-23 立即广播。两条路径互斥。

## 帧数据格式（PushFrames）

```
[FrameNumber: 4B uint32]
[FrameRate: 2B uint16]
[InputCount: 2B uint16]
per input:
  [PlayerId: 4B int32]
  [DataLength: 2B uint16]
  [Data: N bytes]
[EventCount: 1B uint8]          ← 向后兼容：老服务端无此段，Decode 检查 offset < len(buf)
per event (5B each):
  [EventType: 1B]
  [PlayerId: 4B int32]
```

### FrameEvent 类型常量

| 值 | 常量 | 含义 |
|----|------|------|
| 1 | PlayerJoined | 玩家加入房间 |
| 2 | PlayerLeft | 玩家离开房间 |
| 3 | PlayerOffline | 玩家断线 |
| 4 | PlayerOnline | 玩家重连恢复 |
| 5 | HostChanged | Host 变更（PlayerId = 新 Host） |

## StartFrameSync 数据格式

```
[FrameRate: 2B uint16]        (e.g., 20)
[FrameInterval: 2B uint16]    (e.g., 50 ms)
[PlayerCount: 2B uint16]
per player:
  [PlayerId: 4B int32]
```

## Reconnect 请求/响应格式

```
Request:
  [PlayerId: 4B int32]
  [LastFrame: 4B uint32]

Response:
  [Success: 1B]               (1=成功, 0=失败)
  [RoomId: 4B int32]
  [ServerFrame: 4B uint32]
  [SnapshotFrame: 4B uint32]
  [SnapshotData: N bytes]     (可选，Success=1 且有快照时)
```

## UploadSnapshot 格式

```
Request:
  [FrameNumber: 4B uint32]
  [SnapshotData: N bytes]

Response:
  (空 body，仅 Cmd 确认)
```

## 房间管理格式

```
CreateRoom Request:  [MaxPlayers: 4B int32]
CreateRoom Response: [RoomId: 4B int32]

JoinRoom Request:    [RoomId: 4B int32]
JoinRoom Response:   [PlayerId: 4B int32][RoomId: 4B int32]

LeaveRoom Request:   (空 body)
LeaveRoom Response:  (空 body)

GetRooms Response:
  [RoomCount: 2B uint16]
  per room:
    [RoomId: 4B int32]
    [PlayerCount: 2B uint16]
    [MaxPlayers: 2B uint16]
    [IsRunning: 1B]
```

## 修改协议的检查清单

1. C# FrameSyncProtocol.cs — Cmd 常量 + 编解码方法
2. Go framesync/protocol.go — Cmd 常量 + 编解码函数
3. Go cmd/framesync/main.go — router.On 注册 handler
4. UPM unity/com.boom.boomnetwork/ — 从 cli/ 复制更新
5. 跑 test.sh 确认跨语言兼容
6. 跑 verify.sh 确认联调通过
