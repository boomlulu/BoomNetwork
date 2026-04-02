# FrameSync — 帧同步核心

## 文件索引

| File | 职责 |
|---|---|
| `svr/framesync/room.go` | Room 全部逻辑：tickLoop、stepFrame、重连、快照、KV |
| `svr/framesync/protocol.go` | 协议常量 + 二进制编解码函数 |

## tickLoop — 帧驱动主循环

每个 Running 的 Room 有一个独立 goroutine：

```
ticker(1000/frameRate ms)  → stepFrame()
cleanupTicker(5s)          → CleanupDisconnected()
stopCh                     → 退出
```

`tickLoop` 内有 `defer recover()`，panic 时：广播 CmdStopFrameSync → 清理玩家状态 → 调用 `room.OnPanic` 回调 → 递增 `boom_room_panics_total`。

## stepFrame — 热路径

```
1. 快照过期检查: snapshotStaleFrames >= SnapshotIntervalFrames * 3
   → snapshotPaused = true，跳过本帧（等待客户端上传快照）
2. frameNumber++
3. 排空 pendingInputs → 构建 FrameData
4. 编码到 frameBuf（复用 []byte，仅容量增长）
5. 写入 frameRing 环形缓冲（复用 EncodedData slice）
6. 收集在线玩家到 broadcastSlice（复用 slice）
7. 释放 room.mu
8. 广播 CmdPushFrames 给每个 PlayerConn.Send()（锁外执行）
9. 记录 FramesBroadcastLatency Prometheus histogram
```

关键性能设计：
- `frameBuf`、`broadcastSlice` 在 Room 生命周期内复用，零分配热路径
- 广播在锁外执行，避免 Send 阻塞影响帧间隔
- `frameRing` 是定长环形缓冲，slot 内的 `EncodedData` 原地复用

## 帧环形缓冲 (frameRing)

```go
type CachedFrame struct {
    FrameNumber  uint32
    EncodedData  []byte  // 编码后的帧数据
}

frameRing []CachedFrame  // 固定大小，默认 2400（120s @ 20fps）
```

写入：`frameRing[frameNumber % len(frameRing)]`，覆盖旧数据。
读取：重连时从 `lastFrame+1` 开始逐帧读取。

## 重连机制 — 两阶段

### Stage 1: 快速重连 (Fast Reconnect)

```
客户端 JoinRoom(lastFrame > 0)
  → 检查 lastFrame 是否在 frameRing 中
  → 在：发送 lastFrame+1 到当前帧的所有缓存帧
  → 不在：返回 ReconnectFailBufferStale，客户端回退到 Stage 2
```

条件：`lastFrame` 未被环形缓冲覆盖。
时间窗口：`frameBufferSize / frameRate` 秒（默认 2400/20 = 120 秒）。

### Stage 2: 快照重连 (Snapshot Reconnect)

```
客户端 JoinRoom(lastFrame = 0)
  → 发送最新 snapshot 数据（ExtCmdPushSnapshot）
  → 发送 CmdStartFrameSync + InitData(24B)
  → 发送 snapshotFrame+1 到当前帧的所有缓存帧
```

快照由客户端定期上传（`ExtCmdRoomSnapshot`），服务器存储最新快照 + 对应帧号。

### Late-Join (中途加入)

与 Stage 2 相同流程，额外：发送前有 10ms 延迟，确保 JoinRoomRsp 先到达客户端。

## InitData — 帧同步启动参数

```
24 bytes, little-endian:
[FrameRate:4][FrameInterval:4][StartTime:8][SnapshotInterval:4][QuickReconnectMaxMs:4]
```

在 CmdStartFrameSync 的 Data 中发送。

## FrameData / PlayerInput 编码

```go
type FrameData struct {
    FrameNumber uint32
    Inputs      []PlayerInput
}

type PlayerInput struct {
    PlayerID int32
    Data     []byte
}
```

编码格式：`[FrameNumber:4][InputCount:2][PlayerID:4 + DataLen:2 + Data:N]...`

## 快照暂停机制

当 `snapshotStaleFrames >= SnapshotIntervalFrames * 3` 时，帧同步暂停（不推进帧号）。
等待任一客户端上传新快照后恢复。防止快照过期导致重连无法使用。

## KV Store (Room Data)

```go
type DataEntry struct {
    PlayerId int32
    Key      int32
    Value    []byte
}
dataStore map[int64]DataEntry  // key = int64(playerId)<<32 | int64(dataKey)
```

操作：Put（写入 + 广播 ExtCmdRoomDataChanged）、Get（读取 + 响应）、Delete（删除 + 广播）。
用于存储房间级共享数据（如设置、投票等）。
