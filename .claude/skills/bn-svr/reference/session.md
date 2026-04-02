# Session — Message Router

## 文件索引

| File | 职责 |
|---|---|
| `svr/session/session.go` | Router 结构、三级注册、Freeze 机制、Handler 类型 |

## Router 结构

```go
type Handler func(conn *transport.Conn, msg *codec.Message) *codec.Message

type Router struct {
    mu          sync.RWMutex
    core        map[byte]Handler      // Core 命令 → Handler
    ext         map[uint16]Handler    // Extended 命令 → Handler
    gameHandler Handler               // Game 命令统一入口
    frozen      bool
    frozenCore  map[byte]Handler      // Freeze 后的快照（无锁读）
    frozenExt   map[uint16]Handler
}
```

## 注册 API

```go
router.RegisterCore(cmd byte, handler Handler)     // 注册 Core 命令
router.RegisterExt(extCmd uint16, handler Handler)  // 注册 Extended 命令
router.SetGameHandler(handler Handler)              // 设置 Game 命令统一处理器
router.Freeze()                                     // 冻结，之后所有 Dispatch 走快照 map，无锁
```

## 分发流程

```
收到 Message
  → CmdType == Core?    → frozenCore[msg.Cmd]
  → CmdType == Extended? → frozenExt[msg.ExtCmd]
  → CmdType == Game?    → gameHandler
  → Handler 返回非 nil *Message → 自动回发给客户端
```

`AsTransportHandler()` 将 Router 转为 `transport.Handler func(conn *Conn, msg *Message)`，自动处理回发逻辑。

## main.go 中的 Handler 注册总览

### Core Handlers

| Cmd | Handler 函数 | 说明 |
|---|---|---|
| CmdHeartbeat | `handleHeartbeat` | 原样回复 |
| CmdSessionBind | `handleSessionBind` | 认证 + connPlayerMap 注册 |
| CmdSendInput | `handleInput` | 查 playerRoomMap → room.AddInput |
| CmdPing | `handlePing` | 原样回复（RTT 测量） |

### Extended Handlers

| ExtCmd | Handler 函数 | 说明 |
|---|---|---|
| ExtCmdJoinRoom | `handleJoinRoom` | 加入指定房间 |
| ExtCmdLeaveRoom | `handleLeaveRoom` | 离开房间 |
| ExtCmdStartGame | `handleStartGame` | 启动帧同步 |
| ExtCmdMatchRoom | `handleMatchRoom` | 自动匹配/创建房间 |
| ExtCmdCreateRoom | `handleCreateRoom` | 手动创建房间 |
| ExtCmdGetRooms | `handleGetRooms` | 查询房间列表 |
| ExtCmdRoomSnapshot | `handleSnapshot` | 接收客户端快照 |
| ExtCmdSendEntityState | `handleEntityState` | 实体状态中继广播 |
| ExtCmdAuthorityRequest | `handleAuthorityRequest` | 实体权威请求 |
| ExtCmdRoomDataPut | `handleRoomDataPut` | KV 写入 + 广播 |
| ExtCmdRoomDataGet | `handleRoomDataGet` | KV 读取 |
| ExtCmdRoomDataDelete | `handleRoomDataDelete` | KV 删除 + 广播 |

### 全局状态 Map

```go
var connPlayerMap sync.Map  // connID(int) → playerId(int32)
var playerRoomMap sync.Map  // playerId(int32) → *Room
var playerConnMap sync.Map  // playerId(int32) → *transport.Conn
```

这三个 Map 在 Handler 中维护，是连接 ↔ 玩家 ↔ 房间的桥梁。
