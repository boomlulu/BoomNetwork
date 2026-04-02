# 协议层 — Message + Cmd + Codec

**文件**: `cli/Core/Message.cs`, `cli/Core/Codec/MessageCodec.cs`, `cli/Core/FrameSync/FrameSyncProtocol.cs`

## Message 结构体

```csharp
struct Message {
    MessageFlags MsgType;     // FlagsCmd 字节的标志位
    byte Cmd;                 // Core 命令 (CmdType.Core)
    ushort ExtCmd;            // Extended 命令 (CmdType.Extended)
    uint GameCmd;             // Game 命令 (CmdType.Game)
    int Seq;                  // 序列号（可选）
    bool HasSeq;              // 是否携带序列号
    byte[] Data;              // 负载数据
    int DataLength;           // 负载长度
}
```

## 线格式 (Wire Format, Little-Endian)

```
[FlagsCmd:1B][BodyLen:2B or 4B][Seq:0 or 4B][ExtCmd/GameCmd:0/2/4B][Data:NB]
```

### FlagsCmd 字节编码

```
bit 7-4: Cmd (Core 命令值，CmdType.Core 时有效)
bit 3-2: CmdType (00=Core, 01=Extended, 10=Game)
bit 1:   HasSeq (1=携带 4B 序列号)
bit 0:   LenSize4 (0=2B 长度, 1=4B 长度)
```

### 三层命令包头大小

| CmdType | 值 | 最小包头 | 用途 |
|---------|-----|----------|------|
| Core | 00 | 3B (FlagsCmd+BodyLen2) | 高频：心跳、帧输入、帧推送 |
| Extended | 01 | 5B (+ExtCmd 2B) | 房间管理、快照、实体同步 |
| Game | 10 | 7B (+GameCmd 4B) | 自定义游戏消息，服务器透传 |

## Core 命令表 (FrameSyncCmd)

| 名称 | 值 | 方向 | 说明 |
|------|-----|------|------|
| SessionBind | 1 | C→S | 绑定会话 |
| SessionBindRsp | 2 | S→C | 返回 PlayerId |
| RequestStart | 3 | C→S | 请求开始帧同步（附初始快照） |
| StartFrameSync | 4 | S→C | 帧同步开始（附 FrameSyncInitData） |
| StopFrameSync | 5 | S→C | 帧同步停止 |
| FrameInput | 6 | C→S | 发送帧输入 |
| PushFrames | 7 | S→C | 推送帧数据（可多帧批量） |
| Heartbeat | 8 | C→S | 心跳 |
| HeartbeatRsp | 9 | S→C | 心跳响应 |
| Reconnect | 10 | C→S | 重连请求 |
| ReconnectRsp | 11 | S→C | 重连响应 |
| ServerShutdown | 12 | S→C | 服务器关闭通知 |

## Extended 命令表 (FrameSyncExtCmd)

### 房间管理 (1-10)

| 名称 | 值 | 方向 | 格式 |
|------|-----|------|------|
| GetRooms | 1 | C→S | (无数据) |
| GetRoomsRsp | 2 | S→C | `[Count:2] + N×[RoomId:4][PlayerCount:2][MaxPlayers:2][Running:1]` |
| CreateRoom | 3 | C→S | `[MaxPlayers:2]` |
| CreateRoomRsp | 4 | S→C | `[RoomId:4]` |
| JoinRoom | 5 | C→S | `[RoomId:4]` |
| JoinRoomRsp | 6 | S→C | `[PlayerId:4][RoomId:4][PlayerCount:2][PlayerIds:4×N]` |
| LeaveRoom | 7 | C→S | (无数据) |
| LeaveRoomRsp | 8 | S→C | (无数据) |
| MatchRoom | 9 | C→S | `[MaxPlayers:2][MatchKeyLen:2][MatchKey:N]` |
| MatchRoomRsp | 10 | S→C | 同 JoinRoomRsp 格式 |

### 服务器推送 (20-24)

| 名称 | 值 | 方向 | 格式 |
|------|-----|------|------|
| PlayerJoined | 20 | S→C | `[PlayerId:4]` |
| PlayerLeft | 21 | S→C | `[PlayerId:4]` |
| PlayerOffline | 22 | S→C | `[PlayerId:4]` |
| PlayerOnline | 23 | S→C | `[PlayerId:4]` |
| RoomSnapshot | 24 | S→C | 迟到加入/重连快照 |

### 快照 (30-31)

| 名称 | 值 | 方向 | 格式 |
|------|-----|------|------|
| UploadSnapshot | 30 | C→S | `[FrameNumber:4][Data:N]` |
| RequestSnapshot | 31 | C→S | 请求快照 |

### 实体同步 (40-42)

| 名称 | 值 | 方向 | 格式 |
|------|-----|------|------|
| EntityState | 40 | C→S | `[Count:2] + N×[EntityId:4][StateSize:2][State:N]` |
| EntityStateBroadcast | 41 | S→C | `[SenderPid:4][Count:2] + N×[EntityId:4][StateSize:2][State:N]` |
| AuthorityTransfer | 42 | 双向 | `[EntityId:4][Release:1]` (请求) / `[EntityId:4][NewOwner:4]` (结果) |

### 轻量状态同步 (50-55)

| 名称 | 值 | 方向 | 说明 |
|------|-----|------|------|
| StateMessage | 50 | C→S | 发送状态消息 |
| PushStateMessage | 51 | S→C | 广播状态消息 |
| SetData | 52 | C→S | 设置 KV 数据 |
| DeleteData | 53 | C→S | 删除 KV 数据 |
| PushData | 54 | S→C | KV 数据变更推送 |
| PushDataSync | 55 | S→C | KV 全量同步 |

## MessageCodec

**文件**: `cli/Core/Codec/MessageCodec.cs`

```csharp
static class MessageCodec {
    static int Encode(in Message msg, Span<byte> buffer);    // → 写入字节数
    static Message Decode(ReadOnlySpan<byte> data);           // 普通解码
    static Message Decode(ReadOnlySpan<byte> data, bool usePool); // ArrayPool 解码
    static int PeekFrameSize(ReadOnlySpan<byte> data);        // 窥探完整帧大小
    static int EncodedSize(in Message msg);                    // 预计算编码大小
    static void ReturnData(ref Message msg);                   // 归还 ArrayPool 数据
}
```

## FrameSyncInitData

```csharp
struct FrameSyncInitData {
    int FrameRate;             // 帧率（默认 20）
    int FrameInterval;         // 帧间隔 ms（默认 50）
    long StartTime;            // 服务器启动时间戳
    int SnapshotInterval;      // 快照间隔帧数（默认 100）
    int QuickReconnectMaxMs;   // 快速重连窗口 ms（默认 5000）
}
// 16 字节定长，backward compat
```

## FrameData + FrameDataCodec

```csharp
struct FrameData {
    uint FrameNumber;
    PlayerInput[] Inputs;      // { PlayerId (int), Data (byte[]), DataLength (int) }
}

static class FrameDataCodec {
    // Wire: [FrameNumber:4][InputCount:2] + N×[PlayerId:4][DataLen:2][Data:N]
    static byte[] Encode(in FrameData frame);
    static FrameData Decode(ReadOnlySpan<byte> data);
}
```
