# 实体权威同步 — IEntitySync + EntityStateCodec + AuthorityTransfer

**文件**: `cli/Core/FrameSync/FrameSyncProtocol.cs`

## 概念

实体权威同步 = 某个客户端"拥有"某个实体的状态，其他客户端接收同步。
与帧同步互补：帧同步同步输入，实体同步同步状态。

## IEntitySync 接口

```csharp
interface IEntitySync {
    int EntityId { get; }
    int StateSize { get; }                                    // 状态字节数
    void WriteState(byte[] buffer, int offset);               // 写入当前状态
    void OnRemoteState(byte[] buffer, int offset, int length, int senderPlayerId);  // 接收远程状态
}
```

游戏层实现此接口（如 `NetworkTransformSync`），框架层自动采集和分发。

## EntityStateCodec

### 编码 (C→S, ExtCmd 40)

```csharp
static byte[] Encode(IList<IEntitySync> entities);
// Wire: [Count:2] + N × [EntityId:4][StateSize:2][State:N]
```

- `FrameSyncClient.SendInput()` 时自动调用
- 只编码已注册的权威实体（`RegisterAuthorityEntity` 注册的）

### 解码 (S→C, ExtCmd 41)

```csharp
static (int senderPid, int entityCount) Decode(ReadOnlySpan<byte> data);
// Wire: [SenderPid:4][Count:2] + N × [EntityId:4][StateSize:2][State:N]
```

- 服务器广播时附加发送者 PlayerId
- 接收端跳过自己发送的数据（通过 senderPid 比较）

## 权威转移 (AuthorityTransferCodec, ExtCmd 42)

### 请求权威

```csharp
static byte[] EncodeRequest(int entityId, byte release);
// release=0: 请求获取权威
// release=1: 主动释放权威

static (int entityId, byte release) DecodeRequest(ReadOnlySpan<byte> data);
```

### 转移结果

```csharp
static byte[] EncodeResult(int entityId, int newOwner);
// 服务器广播给所有玩家

static (int entityId, int newOwner) DecodeResult(ReadOnlySpan<byte> data);
```

### 权威转移流程

```
客户端 A (当前权威)                 服务器                     客户端 B
                                                              │
                                                    RequestAuthorityTransfer(entityId)
                                    ←─── ExtCmd42(entityId, release=0) ─────┘
                                    │
                              更新 entityAuthority[entityId] = B
                                    │
  ←── ExtCmd42(entityId, newOwner=B) ──────────────────────────────────→
  OnAuthorityChanged(entityId, B)                            OnAuthorityChanged(entityId, B)
```

### 释放权威

```
客户端 A (当前权威)                 服务器
  │
  ReleaseAuthority(entityId)
  ──── ExtCmd42(entityId, release=1) ───→
                                    │
                              删除 entityAuthority[entityId]
                                    │
  ←── ExtCmd42(entityId, newOwner=0) ──→ 广播
```

## FrameSyncClient 实体 API

```csharp
// 注册/注销权威实体
void RegisterAuthorityEntity(IEntitySync entity);
void UnregisterAuthorityEntity(int entityId);

// 权威转移
void RequestAuthorityTransfer(int entityId);
void ReleaseAuthority(int entityId);

// 事件
Action<int, int>? OnAuthorityChanged;   // (entityId, newOwner)
Action<int, byte[], int>? OnEntityState; // (senderPid, data, length)
```

## 数据流

```
权威客户端:
  每帧 SendInput() → EntityStateCodec.Encode(registeredEntities) → C→S ExtCmd 40

服务器:
  收到 ExtCmd 40 → 附加 senderPid → 广播 ExtCmd 41 给房间其他玩家

非权威客户端:
  收到 ExtCmd 41 → EntityStateCodec.Decode() → 逐实体调用 IEntitySync.OnRemoteState()
```
