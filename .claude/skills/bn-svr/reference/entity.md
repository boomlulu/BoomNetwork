# Entity Authority Sync — 实体权威同步

## 文件索引

| File | 职责 |
|---|---|
| `svr/framesync/room.go` | 权威表 + 转移/释放逻辑 |
| `svr/framesync/protocol.go` | ExtCmd 25-28 编解码 |
| `svr/cmd/framesync/main.go` | handleEntityState / handleAuthorityRequest |

## 权威表

```go
room.entityAuthority map[int32]int32  // entityId → ownerPlayerId (0 = 无主)
```

服务器是权威仲裁者。所有权威变更必须经过服务器。

## 协议流程

### 权威请求 (ExtCmdAuthorityRequest = 27)

```
C→S: [entityId:4][release:1]
  release=0: 请求获取权威
  release=1: 请求释放权威
```

### 权威转移广播 (ExtCmdAuthorityTransfer = 28)

```
S→C (广播全房间): [entityId:4][newOwner:4]
  newOwner=0: 实体已释放
  newOwner>0: 新持有者 playerId
```

### 获取权威 — TryGrantAuthority

```go
func (r *Room) TryGrantAuthority(entityId, requesterId int32) (granted bool, prevOwner int32)
```

- 当前无主(0) 或已是自己 → 直接授予
- 当前有其他持有者 → **也直接授予**（先到先得，后来覆盖）
- 广播 ExtCmdAuthorityTransfer 给全房间

### 释放权威 — ReleaseAuthority

```go
func (r *Room) ReleaseAuthority(entityId, requesterId int32) bool
```

- 只有当前持有者才能释放
- 释放后设为 0，广播 ExtCmdAuthorityTransfer(newOwner=0)

### 断线释放 — ReleaseAllAuthority

```go
func (r *Room) ReleaseAllAuthority(playerId int32)
```

玩家断线时立即调用。遍历 entityAuthority，释放该玩家持有的所有实体，逐个广播。

## 实体状态中继

### 发送 (ExtCmdSendEntityState = 25)

```
C→S: [entityStateData...]
```

### 广播 (ExtCmdPushEntityState = 26)

```
S→C: [senderPid:4][entityStateData...]
```

服务器在 payload 前插入 4 字节发送者 playerId，广播给房间内除发送者外的所有在线玩家。

**注意**：状态中继无权威检查——任何玩家都可以发送实体状态。权威仅在转移协议中强制执行。客户端应自行根据权威表过滤无效状态。

## 设计要点

1. **服务器仲裁，客户端过滤**：服务器管理权威表但不校验状态消息，客户端负责忽略非权威者的状态
2. **断线即释放**：避免"死锁"——玩家断线后其实体立即可被其他玩家接管
3. **无排队机制**：同时请求同一实体，最后到达的请求生效（last-write-wins）
4. **广播而非点对点**：权威变更广播给全房间，所有客户端同步更新本地权威表
