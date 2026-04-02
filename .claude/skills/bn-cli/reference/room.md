# 房间系统 — RoomClient + RoomCodec

**文件**: `cli/Client/Room/RoomClient.cs`, `cli/Core/FrameSync/FrameSyncProtocol.cs` (RoomCodec/RoomInfo)

## RoomClient API

```csharp
class RoomClient {
    // 查询
    void GetRooms(Action<RoomInfo[]> onResult);

    // 创建
    void CreateRoom(int maxPlayers, Action<int>? onCreated);  // → roomId

    // 加入
    void JoinRoom(int roomId, Action<int, int, int[]>? onJoined);  // → (playerId, roomId, existingPlayerIds)

    // 匹配（原子查找/创建）
    void MatchRoom(int maxPlayers, string? matchKey, Action<int, int, int[]>? onJoined);

    // 离开
    void LeaveRoom(Action? onLeft);

    // 服务器推送事件
    event Action<int>? OnPlayerJoined;    // playerId
    event Action<int>? OnPlayerLeft;      // playerId
    event Action<NetworkError>? OnError;
}
```

所有方法内部调用 `NetworkSession.SendExtAsync()`，自动匹配响应。

## RoomCodec 编解码

```csharp
static class RoomCodec {
    // 编码（C→S）
    static byte[] EncodeCreateRoom(int maxPlayers);
    static byte[] EncodeMatchRoom(int maxPlayers, string? matchKey);
    static byte[] EncodeJoinRoom(int roomId);

    // 解码（S→C）
    static RoomInfo[] DecodeRoomList(ReadOnlySpan<byte> buf);
    static int DecodeCreateRoomRsp(ReadOnlySpan<byte> buf);
    static (int playerId, int roomId, int[] existingPlayers) DecodeJoinRoomRsp(ReadOnlySpan<byte> buf);
    static int DecodePlayerId(ReadOnlySpan<byte> buf);
}
```

## RoomInfo

```csharp
struct RoomInfo {
    int RoomId;
    int PlayerCount;
    int MaxPlayers;
    bool Running;
    string MatchKey;
}
```

## Wire 协议

见 [protocol.md](protocol.md) Extended 命令表 — 房间管理 (1-10) 和服务器推送 (20-24)。

## MatchRoom 匹配规则

服务器匹配条件（`RoomManager.MatchRoom`）：
1. **matchKey 相同** — 不同 key 永远不匹配
2. **maxPlayers 相同** — 上限必须一致
3. **房间未满** — `TotalPlayerCount < maxPlayers`
4. **原子操作** — 查找 + 创建在同一把锁内

空字符串也是合法 matchKey — 空与空匹配。

## 房间生命周期

```
创建 → 玩家加入 → RequestStart → 帧同步运行 → 玩家离开 → 空房清理
                                      ↕
                              掉线 → DisconnectPlayer (保留位置)
                              重连 → AddPlayer (恢复位置)
```

- **LeaveRoom 不清理 connPlayerMap** — 玩家可直接加入新房间
- **掉线保护期**: `DisconnectKeepAlive` (默认 120s)
- **空房延迟清理**: 30s

## FrameSyncClient 房间封装

`FrameSyncClient` 在 `RoomClient` 之上封装了状态校验：

| 方法 | 前置状态 | 后置状态 |
|------|----------|----------|
| `MatchRoom(max, key?)` | Connected | InRoom |
| `JoinRoom(roomId)` | Connected | InRoom |
| `CreateAndJoinRoom(max)` | Connected | InRoom |
| `LeaveRoom()` | InRoom/Syncing | Connected |
