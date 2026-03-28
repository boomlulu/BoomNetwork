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
| `OnPlayerJoined` | playerId | New player |
| `OnPlayerLeft` | playerId | Player left |
| `OnPlayerOffline` | playerId | Player disconnected |
| `OnPlayerOnline` | playerId | Player reconnected |
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
GET  /health         GET  /stats          GET  /rooms
GET  /rooms/inspect/{id}                  POST /rooms/{id}/kick/{pid}
POST /rooms/{id}/stop                     POST /netsim
GET  /netsim         POST /config/reload  POST /loglevel
```

## UPM Install

```json
"com.boom.boomnetwork": "https://github.com/boomlulu/BoomNetwork.git?path=unity/com.boom.boomnetwork#dev1.0"
```
