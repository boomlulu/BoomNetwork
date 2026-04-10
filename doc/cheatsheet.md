# BoomNetwork Cheatsheet

## Lifecycle State Machine

```
Disconnected ──Connect()──► Connecting ──SessionBind──► Connected
                                                          │
                                              MatchRoom()/JoinRoom()
                                                          │
                                                          ▼
                     Reconnecting ◄──────────────────  InRoom
                          │                              │
                          │                        RequestStart()
                          │                              │
                          ▼                              ▼
                     Connected/InRoom ◄────────────  Syncing
                     (auto re-enter)           (OnFrame loop active)
```

## Core API Quick Reference

### Connection
| Method | When |
|--------|------|
| `Connect(host, port)` | Start connection |
| `Tick(dtMs)` | **Every frame** in Update() |
| `Disconnect()` | Leave gracefully (reconnectable) |
| `DisconnectAndClear()` | Leave permanently |

### Room
| Method | When |
|--------|------|
| `MatchRoom(max, key?)` | Auto-match: join or create |
| `CreateRoom(max, cb?)` | Create specific room |
| `JoinRoom(roomId)` | Join specific room |
| `LeaveRoom()` | Leave room |
| `GetRooms(cb)` | List rooms |

### Frame Sync
| Method | When |
|--------|------|
| `RequestStart()` | Host starts sync |
| `SendInput(data)` | **Only when player has input** |
| `RequestStop()` | Host stops sync |

### Entity Authority
| Method | When |
|--------|------|
| `RegisterAuthorityEntity(e)` | Spawn owned entity |
| `UnregisterAuthorityEntity(id)` | Destroy owned entity |
| `RequestAuthorityTransfer(id)` | Grab entity ownership |
| `ReleaseAuthority(id)` | Release entity |

### State Sync (no frame sync)
| Method | When |
|--------|------|
| `SendStateMessage(data)` | Broadcast (not stored) |
| `SetData(key, value)` | Persistent KV store |
| `DeleteData(key)` | Delete KV entry |
| `SendGameMessage(cmd, data)` | Custom game message |

## Events Quick Reference

| Event | Args | When |
|-------|------|------|
| `OnConnected` | — | Session bound |
| `OnJoinedRoom` | roomId, playerIds[] | Entered room |
| `OnReady` | — | First join or reconnect done |
| `OnFrameSyncStart` | initData | Sync started |
| **`OnFrame`** | **frameData** | **New frame — main game loop** |
| `OnFrameSyncStop` | — | Sync stopped |
| `OnPlayerJoinedMsg` | playerId | New player (lobby, ExtCmd) |
| `OnPlayerJoinedFrame` | playerId | New player (game sim, FrameEvent) |
| `OnPlayerLeftMsg` | playerId | Player left (lobby) |
| `OnPlayerLeftFrame` | playerId | Player left (game sim) |
| `OnPlayerOfflineMsg` | playerId | Player disconnected (lobby) |
| `OnPlayerOfflineFrame` | playerId | Player disconnected (game sim) |
| `OnPlayerOnlineMsg` | playerId | Player reconnected (lobby) |
| `OnPlayerOnlineFrame` | playerId | Player reconnected (game sim) |
| `OnHostChanged` | playerId | New host elected |
| `OnEntityState` | pid, eid, data, off, len | Remote entity update |
| `OnStateMessage` | pid, data | Broadcast received |
| `OnDataChanged` | ver, pid, key, val | KV changed |
| `OnReconnected` | — | Reconnect succeeded |
| `OnDisconnected` | — | Connection lost |
| `OnError` | error | Network error |
| `OnDesyncDetected` | mismatch | Hash mismatch |

## The OnFrame Contract (Local-First)

```csharp
// In Update():
if (HasInput()) {
    client.SendInput(input);     // 1. send to server
    ApplyInput(myId, input);     // 2. execute locally NOW
}

// In OnFrame:
foreach (var inp in frame.Inputs)
    if (inp.PlayerId != myId)    // 3. skip self (already done in step 2)
        ApplyInput(inp.PlayerId, inp.DataSpan);
```

## Five Design Red Lines

1. **No rollback in core** — never predict/rollback
2. **Zero-latency own input** — execute immediately, don't wait for server
3. **Local-First OnFrame** — skip own input in OnFrame
4. **Silent When Idle** — no input = no SendInput call
5. **Deterministic OnFrame** — all shared state changes inside OnFrame only
6. **Single Writer per Connection** — 每个连接只能有一个 goroutine 负责写入。deliveryLoop 是唯一的发送路径，消灭所有并发写 conn 的竞态。
7. **No Band-Aid Fixes** — 用 `time.Sleep` / 加锁延迟 / 重试循环规避竞态是补丁，不是修复。所有时序问题必须通过正确的架构保证（如 PlayerReplaying 状态、deliveryLoop 单写者、GetState 原子读）来解决。

## Server Config (key params)

```yaml
frameRate: 20                # ticks/sec
frameBufferSize: 2400        # ring buffer (frames)
snapshotIntervalFrames: 100  # snapshot upload interval
quickReconnectMaxMs: 5000    # fast reconnect window
disconnectKeepSec: 120       # slot reservation after disconnect
adminAddr: ":9091"           # admin HTTP
```

## Admin Endpoints

```
GET  /health              GET  /stats               GET  /rooms
GET  /rooms/inspect/{id}  GET  /rooms/replay/{id}   POST /rooms/stop/{id}
POST /rooms/kill/{id}     POST /rooms/stop-all       POST /rooms/create
POST /kick/{pid}          POST /broadcast            GET  /players/{pid}
GET  /perf                GET  /rates                GET  /netsim
POST /netsim              POST /config/reload        GET/POST /log-level
GET  /messages            GET  /logs
```

## Core Cmd 表（S→C 单向推送）

| Cmd | 名称 | 说明 |
|-----|------|------|
| 12 | ServerShutdown | 服务器即将关闭，客户端应保存状态并断线 |
| 13 | RateLimitWarning | 消息速率接近上限，请求客户端降速 |
| 14 | Kicked | 你已被踢出，附带 1 字节原因码（0=Admin，1=速率超限，2=超时） |

## UPM Install

```json
"com.boom.boomnetwork": "https://github.com/boomlulu/BoomNetwork.git?path=unity/com.boom.boomnetwork#dev1.0"
```
