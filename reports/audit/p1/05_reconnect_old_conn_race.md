# P1-05: handleReconnect 旧连接关闭的竞态窗口

**严重程度**: P1 — 高风险
**状态**: ✅ 已完成（2026-04-10）
**影响**: 旧连接断开前的最后一条消息可能被丢弃（偶发丢帧输入）

---

## 根因

`main.go:630-636`:

```go
if oldConn, ok := playerConnMap.Load(playerId); ok {
    oldC := oldConn.(*transport.Conn)
    if oldC != conn {
        connPlayerMap.Delete(oldC.ID)  // ← 先删映射
        oldC.Close()                    // ← 再关连接
    }
}
```

在 `Delete` 和 `Close` 之间，如果旧连接上恰好还有一条消息在 dispatch 中处理，dispatch 会发现 connId 已不存在映射，消息被静默丢弃。

## 涉及文件

| 文件 | 行号（约） | 改动类型 |
|------|-----------|----------|
| `svr/cmd/framesync/main.go` | 630-636 | 调整操作顺序 |

## 执行计划

### Step 1: 调整操作顺序

将 `connPlayerMap.Delete` 移到 `Close` 之后（或在 `onClientDisconnect` 回调中统一处理）:

```go
if oldConn, ok := playerConnMap.Load(playerId); ok {
    oldC := oldConn.(*transport.Conn)
    if oldC != conn {
        oldC.Close()                    // 先关连接
        connPlayerMap.Delete(oldC.ID)   // 连接已关闭，不会再收到消息
        connContextMap.Delete(oldC.ID)
    }
}
```

### Step 2: 注意事项

`oldC.Close()` 会触发 `onClientDisconnect` 回调。回调中已有 CAS 检查（`currentConn == conn`），如果新 conn 已经存入 `playerConnMap`，回调会跳过清理。需要确认：
- 新连接的 `playerConnMap.Store` 是否在 `oldC.Close()` 之前执行
- 如果是，回调中的 CAS 检查会正确跳过

当前代码中，`playerConnMap.Store(playerId, conn)` 在 `oldC.Close()` 之后执行（line 640）。需要调整顺序：先 Store 新 conn，再 Close 旧 conn。

### Step 3: 调整后的完整顺序

```go
// 1. 先更新映射到新连接（CAS 保护）
connPlayerMap.Store(conn.ID, playerId)
playerConnMap.Store(playerId, conn)
connContextMap.Store(conn.ID, &connContext{playerId: playerId, room: room})

// 2. 再关闭旧连接（其 onDisconnect 回调中 CAS 检查会跳过）
if oldConn, ok := playerConnMap.Load(playerId); ok {
    // 注意：此时 playerConnMap[playerId] 已指向新 conn，需要用另一种方式获取旧 conn
    // 改为在 Store 之前先取出旧 conn 引用
}
```

实际上需要重构为：

```go
// 先保存旧连接引用
var oldConn *transport.Conn
if v, ok := playerConnMap.Load(playerId); ok {
    if oc := v.(*transport.Conn); oc != conn {
        oldConn = oc
    }
}

// 更新所有映射到新连接
connPlayerMap.Store(conn.ID, playerId)
playerConnMap.Store(playerId, conn)
connContextMap.Store(conn.ID, &connContext{playerId: playerId, room: room})

// 最后关闭旧连接
if oldConn != nil {
    connPlayerMap.Delete(oldConn.ID)
    connContextMap.Delete(oldConn.ID)
    oldConn.Close()
}
```

### Step 4: 验证

- `go test ./...`
- 重点测试：快速断线重连场景，确认旧连接回调不会干扰新连接

---

## 验收标准

### 编译与测试

- [ ] `go build ./...` 编译通过
- [ ] `go test ./...` 全部通过

### 代码审查检查项

- [ ] 新连接映射 Store 在旧连接 Close 之前完成
- [ ] 旧连接的 onClientDisconnect 回调中 CAS 检查能正确跳过（不清理新连接映射）
- [ ] connPlayerMap / connContextMap 中不会残留旧 connId 条目

### 预期结果

| 场景 | 修复前 | 修复后 |
|------|--------|--------|
| 快速断线重连 | 旧 conn 断开前最后一条 FrameInput 可能被丢弃 | 旧 conn 在新映射建立后才关闭，无消息丢失窗口 |
| 旧连接 onDisconnect 回调 | 可能误清理新连接的映射（极低概率） | CAS 检查 100% 跳过 |

**核心指标**: 重连后 connPlayerMap 和 playerConnMap 中只有新连接的条目，无残留。
