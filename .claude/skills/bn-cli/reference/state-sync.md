# 轻量状态同步 — StateSyncCodec + DataEntry

**文件**: `cli/Core/FrameSync/FrameSyncProtocol.cs`

## 概念

轻量状态同步 = KV 存储 + 消息中继。两种模式：
1. **消息模式** — 一次性消息广播（如聊天、事件通知）
2. **KV 模式** — 持久化键值存储（如玩家属性、房间设置）

## DataEntry

```csharp
struct DataEntry {
    int PlayerId;
    string Key;
    byte[] Value;
}
```

## StateSyncCodec

### 消息模式 (ExtCmd 50/51)

```csharp
// 发送状态消息 (C→S)
static byte[] EncodeStateMsg(byte[] data);

// 接收广播 (S→C)
static (int senderId, byte[] data) DecodePushStateMsg(ReadOnlySpan<byte> buf);
```

### KV 模式 — 写入 (ExtCmd 52/53)

```csharp
// 设置 KV (C→S)
static byte[] EncodeSetData(string key, byte[] value);

// 删除 KV (C→S)
static byte[] EncodeDeleteData(string key);
```

### KV 模式 — 接收 (ExtCmd 54/55)

```csharp
// 增量推送 (S→C) — 单条或多条变更
static DataEntry[] DecodePushData(ReadOnlySpan<byte> buf);

// 全量同步 (S→C) — 所有 KV 数据
static DataEntry[] DecodePushDataSync(ReadOnlySpan<byte> buf);
```

## FrameSyncClient 状态同步 API

```csharp
// 消息模式
void SendStateMessage(byte[] data);

// KV 模式
void SetData(string key, byte[] value);
void DeleteData(string key);
void RequestDataSync();    // 请求全量同步

// 事件
Action<int, byte[]>? OnStateMessage;    // (senderPid, data)
Action<DataEntry[]>? OnDataChanged;     // 增量变更
Action<DataEntry[]>? OnDataSynced;      // 全量同步
```

## 使用场景

| 场景 | 模式 | 示例 |
|------|------|------|
| 聊天消息 | 消息 (StateMessage) | 广播聊天文本 |
| 事件通知 | 消息 (StateMessage) | 游戏内事件广播 |
| 玩家昵称 | KV (SetData) | 持久化，新玩家可通过 DataSync 获取 |
| 房间设置 | KV (SetData) | 地图选择、游戏模式等 |
| 准备状态 | KV (SetData) | 大厅内各玩家准备状态 |
| 玩家退出 | KV (DeleteData) | 清理离开玩家的数据 |

## 数据流

### 消息模式

```
客户端 A → SendStateMessage(data) → ExtCmd 50 → 服务器
服务器 → ExtCmd 51 (附 senderPid) → 广播给其他玩家
其他客户端 → OnStateMessage(pid, data)
```

### KV 模式

```
客户端 A → SetData("name", bytes) → ExtCmd 52 → 服务器存储
服务器 → ExtCmd 54 (增量) → 广播给房间所有玩家
所有客户端 → OnDataChanged([DataEntry])

新玩家加入 → RequestDataSync() → ExtCmd 请求
服务器 → ExtCmd 55 (全量) → 返回所有 KV
新玩家 → OnDataSynced([DataEntry])
```
