---
name: bn-refactor
description: "跨仓库 API 破坏性变更安全守则。覆盖 return null 陷阱、消费者审计、UPM .meta、StopServer 端口杀进程。用于 BoomNetwork 库 API 重构时防止静默失败。"
allowed-tools: ["Read", "Write", "Edit", "Bash", "Glob", "Grep", "Agent"]
---

# 跨仓库 API 重构安全守则

## 场景
对被多个仓库/消费者依赖的库进行 API 破坏性变更时使用。

---

## Rule 1：删除 API 必须用编译错误引导，禁止 return null

**错误做法（静默失败）**
```csharp
public OldClass GetOldThing() {
    return null; // 向后兼容 ← 永远不要这样做
}
```

**正确做法**
```csharp
// 选项A：直接删除 → 编译报错 → 立刻找到所有调用点
// 选项B：明确抛出，让失败可见
[Obsolete("Use NewMethod() instead", error: true)]
public OldClass GetOldThing() => throw new NotSupportedException("Use NewMethod()");
```

---

## Rule 2：跨仓库重构后必须审计所有消费者

库测试通过 ≠ 消费者正常。每次破坏性 API 变更后：

**Checklist（提交前执行）**
```bash
# 在所有消费者仓库中搜索被删除/改变的 API
grep -rn "旧方法名\|旧构造函数\|旧事件名" /path/to/consumer/Assets --include="*.cs"
```

涉及本项目的消费者：
- `BoomNetworkUnity/` → `Assets/Scripts/`（Demo 层）
- `BoomNetworkUnity/` → `Assets/Tests/`（测试层）

---

## Rule 3：破坏性变更的提交不算完成，直到消费者也修复

**完成标准**
- [ ] 库侧测试通过
- [ ] 消费者仓库编译无报错
- [ ] 消费者中无旧 API 调用残留（grep 验证）
- [ ] 两个仓库都已 push

---

## Rule 4：null-conditional `?.` 调用的危险性

`obj?.Method()` 在 obj 为 null 时**静默跳过**，不报错、不日志。
如果 obj 预期不为 null，要用 `obj.Method()`（让 NullReferenceException 暴露问题）。

---

## Rule 5：事后复盘要执行"影响范围全搜索"

重构完成后，用被改变的关键词搜索全项目：
```bash
grep -rn "OnBound\|GetRoomClient\|new FrameSyncClient(" /消费者路径 --include="*.cs"
```

---

## Rule 6：共享 UI/逻辑要提取基类，不要在 Demo 间复制粘贴

**问题**：每个 Demo Manager 都重写 PersonSlot 表格 + Room 管理 → 一改全改，容易遗漏

**正确做法**：提取 `DemoManagerBase` 基类
- 共享: PersonSlot 表格、Room 管理、Connect/Disconnect、Snapshot、Log
- 子类只 override: OnSpawnEntity / UpdateSlotInput / OnFrame / OnFrameSyncStart

**本项目已有实例**：
```
DemoManagerBase
  ├── BasicPersonManager (Demo01) — 传统帧同步
  ├── MultiClientPersonManager (Demo01.1) — ParrelSync 多编辑器（继承 DemoManagerBase）
  ├── EntitySyncDemoManager (Demo02) — 实体权威同步（单编辑器双人）
  └── EntitySyncMultiClientManager (Demo03) — 实体权威同步（ParrelSync 双编辑器）
```

---

## Rule 7：UPM 包文件都需要 .meta

Unity 不为 package 自动生成 .meta。新建 UPM 包时：
- 每个 .cs、.asmdef、文件夹都需要对应 .meta
- 没有 .meta → 脚本不编译 → MenuItem 不注册 → 功能"消失"
- 用 python3 生成 GUID：`python3 -c "import uuid; print(uuid.uuid4().hex)"`

---

## Rule 8：StopServer 杀进程时加 -sTCP:LISTEN

`lsof -ti:9000 | xargs kill -9` 会杀掉所有连接该端口的进程（包括 Unity 客户端）。
必须加 `-sTCP:LISTEN` 只匹配监听端（服务器）。

