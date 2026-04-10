# P3-18: countOnlinePlayers 对局部变量使用多余的 atomic

**严重程度**: P3 — 低风险
**状态**: ✅ 已完成（2026-04-10）
**影响**: 无害，但代码可读性差

---

## 根因

`admin.go:497-504`:
```go
func countOnlinePlayers() int {
    var count int64
    connPlayerMap.Range(func(_, _ any) bool {
        atomic.AddInt64(&count, 1)  // count 是局部变量，Range 回调串行执行
        return true
    })
    return int(count)
}
```

`sync.Map.Range` 的回调是串行调用的，`count` 是栈上局部变量，不需要 atomic。

## 执行计划

```go
func countOnlinePlayers() int {
    count := 0
    connPlayerMap.Range(func(_, _ any) bool {
        count++
        return true
    })
    return count
}
```

### 验证

- `go test ./...`

---

## 验收标准

- [ ] `atomic.AddInt64` 已替换为 `count++`
- [ ] `go vet ./...` 无 warning
- [ ] `/stats` 端点返回的 `active_connections` 数值与实际在线人数一致
