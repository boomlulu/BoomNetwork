# P3-17: handleAdminCreateRoom 重复赋值 MatchKey

**严重程度**: P3 — 低风险
**状态**: ✅ 已完成（2026-04-10）
**影响**: 无害但多余，且外层赋值无锁保护

---

## 根因

`admin.go:482-487`:
```go
room := roomMgr.CreateRoomWithMaxPlayers(maxPlayers, matchKey)
// ...
room.MatchKey = matchKey  // 重复赋值，CreateRoomWithMaxPlayers 内部已设置
```

## 执行计划

删除 `admin.go:487` 的 `room.MatchKey = matchKey`。

### 验证

- `go test ./...`

---

## 验收标准

- [ ] `admin.go` 中 `room.MatchKey = matchKey` 重复赋值行已删除
- [ ] `go test ./...` 全部通过
- [ ] Admin API `/rooms/create?match_key=xxx` 创建的房间 MatchKey 正确
