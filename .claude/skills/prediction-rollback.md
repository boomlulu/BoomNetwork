# 预测回滚系统技能

## 概述

预测回滚（Rollback Netcode）让客户端不等服务器确认就立刻执行，操作零延迟。
服务器帧到达后对比，预测对了继续，错了回滚修正。

## 核心组件

```
cli/Core/Prediction/
├── ISimulation.cs        ← 游戏层实现的接口
├── PredictionManager.cs  ← 预测/对比/回滚编排
├── InputBuffer.cs        ← 帧输入环形缓冲区
└── SnapshotBuffer.cs     ← 帧快照环形缓冲区
```

## ISimulation 接口

```csharp
public interface ISimulation
{
    void Simulate(FrameInput[] inputs);    // 执行一帧
    byte[] SaveState();                    // 保存快照
    void LoadState(byte[] snapshot);       // 恢复快照
}
```

游戏层实现这个接口。库不知道游戏状态长什么样。

## PredictionManager 状态

```
ConfirmedFrame: 服务器已确认的最后一帧
PredictedFrame: 本地已预测执行到的帧
AheadFrames = PredictedFrame - ConfirmedFrame

MaxPredictionFrames: 最大允许预测帧数（默认 8）
FrameIntervalMs: 服务器帧间隔（默认 50ms = 20fps）
```

## 执行流程

### 每帧 Update

```
1. 帧率节流
   _frameAccumulator += deltaTimeMs
   if _frameAccumulator < FrameIntervalMs: return（不执行）
   _frameAccumulator -= FrameIntervalMs

2. 预测窗口检查
   if AheadFrames >= MaxPredictionFrames: return（等服务器追上）

3. 预测执行
   InputBuffer.Set(nextFrame, localPlayerId, localInput)  // 存本地输入
   InputBuffer.PredictRemote(nextFrame, remotePlayerIds)   // 预测远程（沿用上一帧）
   snapshot = Simulation.SaveState()                       // 保存快照
   SnapshotBuffer.Save(nextFrame, snapshot)
   allInputs = InputBuffer.GetAll(nextFrame)
   Simulation.Simulate(allInputs)                          // 执行
   PredictedFrame = nextFrame

4. 发送输入
   SendInput(localInput)  // 发给服务器
```

### 服务器帧到达时（ProcessServerFrames）

```
while serverFrameQueue.TryDequeue(frame):
   ConfirmedFrame = frame.FrameNumber

   // 更新 InputBuffer 中该帧的真实输入
   for each input in frame.Inputs:
     InputBuffer.SetServer(frame.FrameNumber, input.PlayerId, input.Data)

   // 对比：预测的输入 vs 服务器的输入
   if InputBuffer.Matches(frame.FrameNumber):
     // 预测正确，只推进 confirmed
     continue
   else:
     // 预测错误，回滚！
     DoRollback(frame.FrameNumber)
```

### 回滚（DoRollback）

```
1. Simulation.LoadState(SnapshotBuffer.Get(confirmedFrame - 1))
2. for f = confirmedFrame to predictedFrame:
     allInputs = InputBuffer.GetAll(f)  // 用修正后的输入
     Simulation.Simulate(allInputs)
     SnapshotBuffer.Save(f, Simulation.SaveState())
3. OnRollback?.Invoke(confirmedFrame, replayCount)
```

## InputBuffer 设计

```
环形缓冲区: InputFrame[BufferSize]
每个 InputFrame: Dictionary<int playerId, byte[] data>

关键: Set 时必须复制 data（不能存引用！）

PredictRemote: 预测远程玩家输入
  策略: 沿用该玩家上一帧的输入（"重复上一帧"）
  如果上一帧没有输入: 不设置（空帧）
```

## SnapshotBuffer 设计

```
环形缓冲区: (uint frameNumber, byte[] data)[BufferSize]
BufferSize = MaxPredictionFrames * 2（至少 16）

Save: 存快照（复制 data）
Get: 取快照（返回引用，调用方不要修改）
```

## FrameSyncClient 集成

```csharp
// FrameSyncClient.cs
public PredictionManager? Prediction { get; private set; }

public void SetPrediction(PredictionManager pm)
{
    Prediction = pm;
    // 注册帧监听器（让 PM 处理服务器帧而非直接 OnFrame）
}

public void Tick(float deltaTimeMs)
{
    _connectionManager.Tick(deltaTimeMs);
    Prediction?.ProcessServerFrames();  // 处理排队的服务器帧
}

// HandlePushFrames 内:
if (Prediction != null)
    Prediction.OnServerFrame(frame);  // 入队，不立即处理
else
    OnFrame?.Invoke(frame);           // 传统模式：直接处理
```

## 已知问题

1. **帧率节流**: 预测帧率必须用 _frameAccumulator 节流到 FrameIntervalMs，不能每 Update 都预测
2. **InputBuffer 值语义**: Set 时必须 Array.Copy，不能存外部 buffer 引用
3. **同进程两客户端冲突**: 补帧和 live 帧交错导致帧号去重失效，Demo01 跳过快照
4. **FrameIntervalMs 必须和服务器一致**: 从 StartFrameSync 的 FrameSyncInitData 读取
5. **预测模式未在 Unity Demo 中验证**: enablePrediction 默认 false

## Demo02 文件

```
BoomNetworkUnity/Assets/Scripts/Demo02/
├── PredictionPersonManager.cs  ← 含预测逻辑的 PersonManager
├── PredictionGameHUD.cs        ← 含回滚统计的 HUD
└── DemoSimulation.cs           ← ISimulation 实现（平行数组，确定性遍历）
```

## DemoSimulation 设计

```csharp
public class DemoSimulation : ISimulation
{
    // 用平行数组替代 Dictionary（确定性遍历 + 缓存友好）
    int[] _playerIds;      // 排序的玩家 ID
    float[] _positionsX;   // 对应位置 X
    float[] _positionsY;   // 对应位置 Y
    int _count;

    void Simulate(FrameInput[] inputs)  // 确定性：按 playerId 排序处理
    byte[] SaveState()                  // 序列化所有位置
    void LoadState(byte[] snapshot)     // 反序列化恢复
}
```
