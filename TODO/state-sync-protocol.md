# 轻量状态同步协议 (StateMessage + DataMessage)

> 版本: 1.0 | 日期: 2026-03-26 | 分支: dev1.0

## 1. 概述

帧同步（20fps 逐帧推送）适用于战斗阶段。非战斗阶段（选人/等待/大厅/结算）使用
本文档定义的轻量状态同步，两套系统共存于同一个房间。

```
房间生命周期:
  创建 → [状态同步] 选人/等待 → [帧同步] 战斗 → [状态同步] 结算 → 销毁
              ↑                        ↑
         按需发送，0 固定开销      20fps 逐帧推送
```

## 2. 两种消息类型

| | StateMessage | DataMessage |
|---|---|---|
| 语义 | 事件/通知（fire-and-forget） | 持续状态（KV 存储） |
| 服务器行为 | 纯转发，不存储 | 存储 KV，增量广播 |
| 生命周期 | 发完即弃 | 持久化到房间销毁 |
| 典型用途 | "我选好了" / 聊天 / 表情 | 角色=战士 / 准备=true / 分数=100 |
| 新玩家加入 | 收不到历史消息 | 自动收到当前完整 KV 快照 |
| 带宽模式 | 有事才发 | 有变化才发（增量） |

## 3. ExtCmd 分配

| 值 | 常量名 | 方向 | 说明 |
|----|--------|------|------|
| 50 | SendStateMsg | C→S | 发送状态消息 |
| 51 | PushStateMsg | S→C | 转发状态消息给其他玩家 |
| 52 | SetData | C→S | 设置/删除 KV 数据 |
| 53 | PushData | S→C | 增量广播 KV 变更 |
| 54 | RequestDataSync | C→S | 请求全量 KV 同步 |
| 55 | PushDataSync | S→C | 全量 KV 快照 |

## 4. Wire 协议 (All Little-Endian)

### 4.1 StateMessage

**SendStateMsg (C→S, ExtCmd=50):**
```
[Data: NB]                        // 原始 payload，服务器透传
```

**PushStateMsg (S→C, ExtCmd=51):**
```
[PlayerId:  4B int32]             // 发送者 ID
[Data:      NB]                   // 原始 payload（msg.Data 剩余部分）
```

### 4.2 DataMessage

**SetData (C→S, ExtCmd=52):**
```
[Key:       4B int32]             // 键（int 类型，快速 hash）
[ValueLen:  2B uint16]            // 值长度（0 = 删除该键）
[Value:     ValueLen bytes]       // 值
```

**PushData (S→C, ExtCmd=53) — 增量:**
```
[Version:   4B uint32]            // 本次变更的版本号（单调递增）
[PlayerId:  4B int32]             // 归属玩家
[Key:       4B int32]             // 键
[ValueLen:  2B uint16]            // 值长度（0 = 删除）
[Value:     ValueLen bytes]       // 值
```

**RequestDataSync (C→S, ExtCmd=54):**
```
(空 payload)
```

**PushDataSync (S→C, ExtCmd=55) — 全量快照:**
```
[Version:    4B uint32]           // 当前版本号
[EntryCount: 2B uint16]           // 条目数
for each entry:
  [PlayerId: 4B int32]            // 归属玩家
  [Key:      4B int32]            // 键
  [ValueLen: 2B uint16]           // 值长度
  [Value:    ValueLen bytes]      // 值
```

## 5. KV 存储模型

### 5.1 数据模型

```
Room.dataStore: map[int64]DataEntry
  复合键 = int64(playerId) << 32 | int64(uint32(key))
  避免 struct key 的 hash 开销

Room.dataVersion: uint32
  全房间单调递增，每次写入 +1
```

每个玩家拥有自己的 key 空间：玩家 A 的 key=1 和玩家 B 的 key=1 是不同的条目。

### 5.2 版本间隙检测（客户端）

```
收到 PushData(version):
  if version == lastSyncVersion + 1:
    正常应用增量，lastSyncVersion = version
  else if version > lastSyncVersion + 1:
    中间丢数据 → 发送 RequestDataSync 请求全量
  else:
    旧版本消息，忽略（可能是重传）

收到 PushDataSync(version):
  全量覆盖本地数据，lastSyncVersion = version
```

### 5.3 自动全量同步触发点

| 触发场景 | 方向 | 说明 |
|----------|------|------|
| 新玩家加入房间 | S→C | dataStore 非空时自动发 PushDataSync |
| 客户端检测版本间隙 | C→S→C | 客户端发 RequestDataSync，服务器回 PushDataSync |
| 断线重连成功 | S→C | 重连流程中自动发 PushDataSync |

### 5.4 玩家离开清理

```
玩家离开房间:
  1. 服务器遍历 dataStore，找出该玩家的所有条目
  2. 逐条删除，每条 version++
  3. 广播 PushData(ValueLen=0) 给剩余玩家
  4. 客户端收到 PlayerLeft 后也可本地清理（兜底）
```

## 6. 文件索引

| 文件 | 职责 |
|------|------|
| `svr/framesync/protocol.go` | ExtCmd 常量 + Encode/Decode 函数 |
| `svr/framesync/room.go` | DataEntry + dataStore + SetData/GetDataSnapshot/ClearPlayerData |
| `svr/cmd/framesync/main.go` | handleSendStateMsg / handleSetData / handleRequestDataSync |
| `cli/Core/FrameSync/FrameSyncProtocol.cs` | ExtCmd 常量 + StateSyncCodec |
| `cli/Client/FrameSync/FrameSyncClient.cs` | 事件 + API + 版本检测 |
