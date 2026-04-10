# P2-10: Encode 返回 pool buffer 引用，使用不当可导致数据竞态

**严重程度**: P2 — 中风险
**状态**: 待执行
**影响**: 未来新增调用方若忘记 PutBuf 或 PutBuf 后继续访问 buf，会导致数据损坏

---

## 根因

`codec/message.go:121-138`:

`Encode()` 返回 pool buffer 的直接引用。调用方必须在使用完后调用 `PutBuf()` 归还，且归还后不得再访问。这是一个 API 陷阱。

当前唯一调用路径 `WriteMessage` 是安全的（Write 后立即 PutBuf），但 API 设计对未来维护者不友好。

## 涉及文件

| 文件 | 改动类型 |
|------|----------|
| `svr/codec/message.go` | 文档注释 + 可选重构 |

## 执行计划

### 方案 A: 最小改动——增强文档注释

```go
// Encode 编码 Message 到字节切片（从 bufPool 取出的复用缓冲区）。
//
// 重要：返回的 buffer 必须通过 PutBuf() 归还。
// 归还后不得再访问 buffer 或其子切片，否则会导致数据竞态。
//
// 典型用法：
//   buf := Encode(msg)
//   _, err := conn.Write(buf)
//   PutBuf(buf)
func Encode(msg *Message) []byte {
```

### 方案 B: 更安全的 API 设计

提供一个回调式 API 避免泄漏：

```go
// WithEncoded 编码 msg 并在回调中提供 buffer，回调返回后 buffer 自动归还。
// 这是 Encode + PutBuf 的安全封装。
func WithEncoded(msg *Message, fn func(buf []byte)) {
    buf := Encode(msg)
    fn(buf)
    PutBuf(buf)
}
```

### 验证

- `go test ./...`
- `go vet ./...`

---

## 验收标准

### 编译与测试

- [ ] `go build ./...` 编译通过
- [ ] `go test ./...` 全部通过（含 bench_test.go）

### 代码审查检查项

- [ ] `Encode()` 函数注释明确标注 buffer 生命周期和 PutBuf 要求
- [ ] （如果实施方案 B）`WithEncoded` 函数存在且有对应测试

### 预期结果

API 使用约束在文档层面显式化，新开发者不会误用 Encode 返回的 buffer。
