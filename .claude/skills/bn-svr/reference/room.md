# Room Management — Room/RoomManager/Player

## 文件索引

| File | 职责 |
|---|---|
| `svr/framesync/room.go` | Room 结构 + 所有方法 + Player 状态机 |
| `svr/framesync/room_manager.go` | RoomManager CRUD + 匹配 + 清理 |

## Room 结构核心字段

```go
type Room struct {
    ID              int32
    Config          RoomConfig
    mu              sync.Mutex
    players         map[int32]*Player
    running         bool
    frameNumber     uint32
    pendingInputs   []PlayerInput
    frameRing       []CachedFrame       // 环形帧缓冲
    entityAuthority map[int32]int32     // entityId → ownerPlayerId
    dataStore       map[int64]DataEntry // KV Store
    snapshot        []byte              // 最新快照数据
    snapshotFrame   uint32              // 快照对应帧号
    matchKey        string              // 匹配键
    OnPanic         func(roomID int32)  // panic 回调
}
```

## RoomConfig

| 字段 | 默认值 | 说明 |
|---|---|---|
| FrameRate | 20 | 帧率 (fps) |
| MaxPlayers | 4 | 房间最大玩家数 |
| FrameBufferSize | 2400 | 帧环形缓冲大小 |
| DisconnectKeepAlive | 120s | 断线保留时间 |
| SnapshotIntervalFrames | 100 | 快照间隔帧数 |
| QuickReconnectMaxMs | 5000 | Stage 1 重连超时 |

## Player 状态机

```go
type Player struct {
    ID             int32
    Conn           PlayerConn
    State          PlayerState
    DisconnectTime time.Time
}

type PlayerState int
const (
    PlayerOnline       PlayerState = 0
    PlayerDisconnected PlayerState = 1
)
```

```
连接建立 → PlayerOnline
  ↓ 网络断开
PlayerDisconnected（保留 DisconnectKeepAlive 秒）
  ↓ 重连成功                    ↓ 超时
PlayerOnline（旧连接关闭）    从房间移除
  ↓ 主动离开
从房间移除（立即）
```

## RoomManager

```go
type RoomManager struct {
    mu       sync.RWMutex
    rooms    map[int32]*Room
    nextID   int32
    maxRooms int
}
```

### 核心方法

| 方法 | 说明 |
|---|---|
| `CreateRoom(config)` | 创建房间，递增 nextID，检查 maxRooms 上限 |
| `CreateRoomWithMaxPlayers(n)` | 快捷创建 |
| `GetRoom(id)` | 获取房间 |
| `RemoveRoom(id)` | Stop + 从 map 移除 |
| `MatchRoom(maxPlayers, matchKey)` | 原子操作：找未满且 matchKey 匹配的房间，没有则创建 |
| `AutoAssignRoom(ppr)` | 压测模式：轮询填充房间 |
| `CleanupEmptyRooms()` | 每 10 秒调用：移除无玩家且未运行的房间 |
| `StopAll()` | 关闭时调用：Stop 所有房间 |
| `GetAllRoomInfo()` | Admin 用：返回所有房间快照信息 |

### 匹配流程 (MatchRoom)

```
MatchRoom(maxPlayers, matchKey)
  → 遍历 rooms，找 !running && playerCount < maxPlayers && matchKey 匹配
  → 找到 → 返回该房间
  → 没找到 → CreateRoom(maxPlayers) + 设置 matchKey → 返回新房间
```

matchKey 用于按游戏模式等条件隔离匹配。空 matchKey 匹配所有空 key 房间。

## Room 核心方法

| 方法 | 说明 |
|---|---|
| `AddPlayer(id, conn)` | 添加玩家，若已存在则重连（替换 Conn） |
| `RemovePlayer(id)` | 移除玩家 + 释放该玩家所有实体权威 |
| `AddInput(pid, data)` | 追加到 pendingInputs |
| `Start()` | 启动 tickLoop goroutine |
| `Stop()` | 广播 CmdStopFrameSync + 关闭 stopCh |
| `SetSnapshot(data, frame)` | 存储客户端上传的快照 |
| `PlayerCount()` | 返回当前玩家数（含断线） |
| `OnlinePlayerCount()` | 返回在线玩家数 |
| `BroadcastMessage(msg)` | 广播给所有在线玩家 |

## 容量控制

| 层级 | 限制 | 默认 |
|---|---|---|
| 全局连接 | maxConnections | 0 (无限) |
| 全局房间 | maxRooms | 0 (无限) |
| 单房间玩家 | MaxPlayers | 4 |

超限时：连接拒绝（maxConnections）、返回错误码（maxRooms/MaxPlayers）。

## 房间清理时序

```
每 10 秒: CleanupEmptyRooms()
  → 遍历 rooms
  → playerCount == 0 && !running → RemoveRoom

每 5 秒 (tickLoop 内): CleanupDisconnected()
  → 遍历 players
  → State == Disconnected && 超过 DisconnectKeepAlive
  → RemovePlayer + 广播 CmdPlayerLeft
```
