# Codec — Wire Protocol & Frame I/O

## 文件索引

| File | 职责 |
|---|---|
| `svr/codec/message.go` | Message 结构、三层命令编解码、工厂方法 |
| `svr/codec/framing.go` | FrameReader/FrameWriter 带缓冲帧分割 |

## 三层命令空间

```
FlagsCmd (1 byte):
  Bit 0:   LenSize    (0=2B body长度, 1=4B body长度, 阈值 65530)
  Bit 1:   HasSeq     (可选 4B 序列号，用于请求/响应配对)
  Bit 2-3: CmdType    (00=Core, 01=Extended, 10=Game)
  Bit 4-7: CoreCmd    (仅 CmdType=Core 时有效)
```

### Header 大小

| CmdType | Header | Body 前缀 | 用途 |
|---|---|---|---|
| Core (00) | 3B: FlagsCmd + 2B len | 无 | 帧输入/推送等热路径 |
| Extended (01) | 3B + 2B ExtCmd in body | ExtCmd(uint16) | 框架扩展：房间/实体/快照 |
| Game (10) | 3B + 4B GameCmd in body | GameCmd(uint32) | 业务层自定义命令 |

所有值 **小端序 (Little-Endian)**。

## Message 结构

```go
type Message struct {
    CmdType  CmdType   // Core/Extended/Game
    Cmd      byte      // Core 命令 (0-15)
    ExtCmd   uint16    // Extended 命令
    GameCmd  uint32    // Game 命令
    Seq      int32     // 序列号 (-1 = 无)
    Data     []byte    // 负载
}
```

### Core 命令常量 (Cmd)

| Cmd | 名称 | 方向 | 说明 |
|---|---|---|---|
| 1 | CmdHeartbeat | C↔S | 心跳，客户端发起，服务器原样回复 |
| 2 | CmdSessionBind | C→S | 会话绑定 + 认证 Token |
| 3 | CmdSendInput | C→S | 发送帧输入数据 |
| 4 | CmdPushFrames | S→C | 推送一帧或多帧数据 |
| 5 | CmdStartFrameSync | S→C | 开始帧同步 + InitData(24B) |
| 6 | CmdStopFrameSync | S→C | 停止帧同步 |
| 7 | CmdPlayerJoined | S→C | 玩家加入房间广播 |
| 8 | CmdPlayerLeft | S→C | 玩家离开房间广播 |
| 9 | CmdError | S→C | 错误码推送 |
| 10 | CmdServerShutdown | S→C | 服务器关闭通知 |
| 11 | CmdPing | C↔S | 延迟测量 Ping/Pong |

### Extended 命令常量 (ExtCmd)

| ExtCmd | 名称 | 方向 | 说明 |
|---|---|---|---|
| 1 | ExtCmdGetRooms | C→S | 查询房间列表 |
| 2 | ExtCmdGetRoomsRsp | S→C | 房间列表响应 |
| 3 | ExtCmdJoinRoom | C→S | 加入指定房间 |
| 4 | ExtCmdJoinRoomRsp | S→C | 加入房间响应 |
| 5 | ExtCmdLeaveRoom | C→S | 离开房间 |
| 6 | ExtCmdLeaveRoomRsp | S→C | 离开房间响应 |
| 7 | ExtCmdStartGame | C→S | 请求开始游戏 |
| 8 | ExtCmdMatchRoom | C→S | 匹配房间（自动创建/加入） |
| 9 | ExtCmdMatchRoomRsp | S→C | 匹配房间响应 |
| 10 | ExtCmdCreateRoom | C→S | 创建房间 |
| 11 | ExtCmdCreateRoomRsp | S→C | 创建房间响应 |
| 20 | ExtCmdRoomSnapshot | C→S | 上传房间快照 |
| 21 | ExtCmdPushSnapshot | S→C | 推送快照给重连客户端 |
| 25 | ExtCmdSendEntityState | C→S | 发送实体状态 |
| 26 | ExtCmdPushEntityState | S→C | 广播实体状态 |
| 27 | ExtCmdAuthorityRequest | C→S | 请求实体权威 |
| 28 | ExtCmdAuthorityTransfer | S→C | 广播权威变更 |
| 30 | ExtCmdRoomDataPut | C→S | KV Store 写入 |
| 31 | ExtCmdRoomDataGet | C→S | KV Store 读取 |
| 32 | ExtCmdRoomDataGetRsp | S→C | KV Store 读取响应 |
| 33 | ExtCmdRoomDataChanged | S→C | KV Store 变更广播 |
| 34 | ExtCmdRoomDataDelete | C→S | KV Store 删除 |

## FrameReader

```go
type FrameReader struct {
    reader   *bufio.Reader  // 8KB bufio
    frameBuf []byte         // 可复用内部缓冲
}
```

- `ReadMessage() (*Message, error)` — 零拷贝，Data 指向内部 frameBuf（下次 Read 会覆盖）
- `ReadMessageCopy() (*Message, error)` — 安全拷贝，Data 独立分配（用于异步处理）

读取流程：读 1B FlagsCmd → 解析 LenSize/HasSeq/CmdType → 读 2B/4B bodyLen → 读 body → 解码 Seq/ExtCmd/GameCmd/Data。

超过 `MaxMessageSize`(默认 64KB) 的帧触发错误，调用方断连。

## FrameWriter

```go
type FrameWriter struct {
    writer *bufio.Writer  // 8KB bufio
    buf    []byte         // 可复用编码缓冲
}
```

- `WriteMessage(msg *Message) error` — 编码到内部 buf，写入 bufio
- `Flush() error` — 刷新 bufio 到底层 net.Conn

编码流程：构建 FlagsCmd → 写 bodyLen(2B/4B) → 写 Seq(可选) → 写 ExtCmd/GameCmd(按类型) → 写 Data。
