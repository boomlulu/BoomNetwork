# 踩坑 #001：压测帧率不准确（18.7 fps vs 预期 20.0 fps）

## 现象

C# 1000 客户端连接 Go 帧同步服务器，预期每客户端收到 20.0 fps，实测只有 18.7 fps。

## 排查过程

### 第一反应：tick 间隔不精确

```csharp
// 原始代码
Thread.Sleep(16);  // 以为每 16ms tick 一次
```

`Thread.Sleep(16)` 的实际耗时 = 16ms + OS 调度延迟。加上遍历 1000 个客户端 Tick() 的 CPU 时间，实际间隔可能是 20-25ms。

**修复 1：Stopwatch 补偿**

```csharp
nextTickMs += 16;
long sleepMs = nextTickMs - sw.ElapsedMilliseconds;
if (sleepMs > 0) Thread.Sleep((int)sleepMs);
```

结果：18.7 → 19.1 fps。改善了但没解决。

### 第二反应：是不是真的丢帧了？

TCP 是可靠传输，不会丢帧。数据一定在 socket buffer 里。`Transport.Tick()` 内部用 `while (TryDequeue)` 一次性取完所有积压数据。所以即使 tick 间隔不精确，也只是处理时机延迟，**总帧数不应该少**。

这说明问题不在丢帧，在**测量**。

### 真正的根因：统计窗口不准

```
时间线:
0s    ─ 开始连接
2s    ─ 所有人 bound
2.5s  ─ 957/1000 进入 syncing（43 个客户端的房间还没凑满）
2.5s  ─ 开始统计 ← 43 个客户端还没收帧，拉低平均值
12.5s ─ 统计结束

问题: 43 个客户端前几秒没收帧，但被算进了分母
```

### 修复 2：等所有人 syncing 后再统计

```csharp
// 等所有客户端进入 FrameSync 状态
WaitFor("Syncing", () => totalSyncing >= clientCount, ...);

// 重置计数器 — 从这里开始才是纯稳定期
Interlocked.Exchange(ref totalFramesRecv, 0);
```

### 修复 3：结尾充分排空

TCP buffer 里可能还有最后几帧没取出来：

```csharp
// 测量结束后再 tick 30 轮
for (int i = 0; i < 30; i++)
{
    TickAll(clients, clientCount, 16);
    Thread.Sleep(10);
}
```

### 修复 4：用实际 elapsed 而非目标时长

```csharp
// 错: 用 10s 算，但实际运行了 10.4s（含排空）
double elapsed = durationSec;

// 对: 用实际运行时间
double elapsed = sw.Elapsed.TotalSeconds;
```

## 修复后结果

```
修复前: 18.7 fps (误差 6.5%)
修复 1: 19.1 fps (Stopwatch 补偿)
修复 2+3+4: 20.0 fps (精确)
```

## 教训

1. **TCP 不丢帧** — 如果帧率不对，先怀疑测量，不要怀疑传输
2. **压测统计窗口要干净** — 启动阶段（连接/绑定/等房间）不能算进测量期
3. **Thread.Sleep 不可信** — 游戏 tick 循环必须用 Stopwatch 补偿
4. **结尾要排空** — TCP 有发送/接收缓冲区，测量结束时 buffer 里可能还有数据
5. **分母要对** — 如果 N 个客户端只有 M 个在收帧，fps 应该除以 M 不是 N
