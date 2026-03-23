# BoomNetwork 踩坑记录

## 协议层

### 跨语言线格式不一致
```
场景: C# 用 BinaryPrimitives，Go 用 binary.LittleEndian
风险: 字节序不一致导致数据损坏
防护: testdata/ 目录有双端编码的 fixture 文件，test.sh 自动校验
教训: 任何协议修改必须跑跨语言测试
```

### 动态包头的 FlagsCmd 位运算
```
编码: flagsCmd = (byte)((cmd << 2) | (hasSeq ? 2 : 0) | (largeLen ? 1 : 0))
解码: cmd = flagsCmd >> 2; hasSeq = (flagsCmd & 2) != 0; largeLen = (flagsCmd & 1) != 0
风险: 位运算写错导致所有消息解析失败
防护: 单元测试覆盖所有组合
```

## 网络层

### TCP 粘包/拆包
```
问题: TCP 是流协议，一次 recv 可能收到多条消息或半条消息
解决: LengthPrefixFraming + RingBuffer
      [4B LenPrefix][Body] 格式，Framing 层处理拆粘包
注意: KCP 不需要 Framing（KCP 保证消息边界）
```

### 心跳响应计时器不重置
```
现象: 连接正常但心跳超时断开
原因: HeartbeatRsp 到达时没重置 _heartbeatRspTimer
修复: ConnectionManager.HandleMessage 处理 HeartbeatRsp 时重置
教训: 心跳是双向的，发出和收到都要跟踪
```

### Session Tick 重复调用
```
现象: 消息处理两遍
原因: Person.Tick 调 _connMgr.Tick，FrameSyncClient.Tick 也调 _connMgr.Tick
修复: Person.Tick 只调 _frameSync.Tick，由 FrameSyncClient 内部调 _connMgr.Tick
教训: Tick 链路必须单一入口
```

## 帧同步层

### InputBuffer 存引用不存值
```
现象: 预测永远不匹配，每帧都回滚
原因: Set(frame, pid, input) 存了外部 buffer 的引用
修复: Set 时 Buffer.BlockCopy 复制
教训: 帧级数据必须值语义
```

### 预测帧率和服务器帧率不对齐
```
现象: 8 帧预测后停止，所有服务器帧触发回滚
原因: PredictFrame 在 Unity Update 60fps 调用，服务器 20fps
修复: PredictionManager 内部 _frameAccumulator 节流
教训: 预测频率必须等于服务器帧率
```

### 快照 + 补帧在同进程两客户端的去重冲突
```
现象: 重连后其他玩家位置被重置
原因: 两个 Person 的 OnFrame 用帧号去重
      补帧（旧帧号）被 live 帧（新帧号）的去重计数器跳过
修复: Demo01 不加载快照（接受小偏差）
      正确方案属于 Demo02 预测模式
教训: 快照恢复 + 补帧必须原子执行，不能和 live 帧交错
```

## 房间管理

### SessionBind 自动分房 + JoinRoom 重复
```
现象: 一个连接在两个房间，收到两份帧数据
原因: handleSessionBind 有 AutoAssignRoom，JoinRoom 又创建新房间
修复: handleSessionBind 不自动分房，服务器不加 -autoroom
教训: 分房入口必须唯一
```

### AutoAssignRoom 竞态
```
现象: 两个并发请求各创建一个房间
原因: 查找空房间 → 解锁 → 创建 → 加锁 的间隙
修复: createRoomLocked 在锁内完成
教训: 查找+创建必须原子
```

### 断线玩家占 AutoAssignRoom 槽位
```
现象: 新玩家被分到有"幽灵"的房间
原因: AutoAssignRoom 用 TotalPlayerCount（含断线）
修复: 改用 PlayerCount（仅在线）
教训: 在线/离线状态要区分
```

### 房间清理后映射未清除
```
现象: 重连到已删除的房间
原因: 房间清理删除了 rooms map 但没清 playerRoomMap
修复: 清理时同步清除 playerRoomMap
教训: 映射关系双向清理
```

## Unity 兼容性

### Dictionary foreach 修改
```
Unity Mono 不允许，.NET 8 可以。三阶段处理。
```

### BinaryPrimitives.WriteSingleLittleEndian
```
.NET 6+ API，Unity 没有。改 BitConverter。
```

### Nullable tuple .Value
```
Unity 2022 C# 10 更严格，必须 .Value.member。
```

### UPM .meta 文件
```
每个文件和目录都需要。Python 脚本批量生成。
```

### Unity domain reload 杀子进程
```
Go 服务器不能在 Unity Editor 内启动。改用 Terminal.app。
```

## Go 服务端

### go run 的 -addr 参数
```
Echo server 用 flag.Arg(0) 位置参数，不是 -addr flag。
Framesync server 用 -addr flag。
两个服务器的参数风格不一致，已通过 verify.sh 覆盖。
```

### Prometheus metrics 端口冲突
```
:9090 可能被其他进程占用。
日志: "[Metrics] Failed: listen tcp :9090: bind: address already in use"
不影响核心功能，只是 metrics 不可用。
```
