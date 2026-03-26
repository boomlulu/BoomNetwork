# Unity 集成指南

从零接入 BoomNetwork，实现多人帧同步。

## 1. 安装

### UPM Git URL（推荐）

Unity 菜单 `Window > Package Manager > + > Add package from git URL`，填入：

```
https://github.com/luwenyiCC/BoomNetwork.git?path=unity/com.boom.boomnetwork#dev1.0
```

或直接编辑 `Packages/manifest.json`：

```json
{
  "dependencies": {
    "com.boom.boomnetwork": "https://github.com/luwenyiCC/BoomNetwork.git?path=unity/com.boom.boomnetwork#dev1.0"
  }
}
```

### 本地开发（修改源码）

如果需要修改 BoomNetwork 源码，用本地路径：

```json
"com.boom.boomnetwork": "file:///你的路径/BoomNetwork/unity/com.boom.boomnetwork"
```

## 2. 核心概念

```
Person          一个完整的网络客户端身份（封装了下面所有组件）
├ NetworkSession    消息收发、Seq 管理
├ ConnectionManager 心跳、断线检测
├ RoomClient        房间 CRUD
└ FrameSyncClient   帧同步状态机、快照上传
```

**Person 是面向业务的入口**，封装了连接→房间→帧同步→重连的完整生命周期。
FrameSyncClient / RoomClient 是底层组件，除非有特殊需求，否则直接用 Person。

## 3. 最小接入（30 行代码）

### 3.1 创建 Person 并连接

```csharp
using BoomNetworkDemo; // Person 在 Demo 工程中，可复制到你的项目

var person = new Person();

// 监听事件
person.OnConnected += p => Debug.Log("Connected");
person.OnJoinedRoom += p => Debug.Log($"Joined room as P{p.PlayerId}");
person.OnFrameSyncStart += (p, data) => Debug.Log($"Syncing at {data.FrameRate}fps");
person.OnFrame += (p, frame) => {
    // frame.FrameNumber: 帧号
    // frame.Inputs: 本帧所有玩家的输入
    foreach (var input in frame.Inputs)
    {
        // input.PlayerId: 玩家 ID
        // input.Data: 输入数据（你定义的格式）
    }
};
person.OnDisconnected += p => Debug.Log("Disconnected");

// 连接
var config = ScriptableObject.CreateInstance<NetworkConfig>();
config.host = "127.0.0.1";
config.port = 9000;
person.Connect(config);
```

### 3.2 每帧 Tick

```csharp
void Update()
{
    person.Tick(Time.deltaTime * 1000);
}
```

### 3.3 加入房间 + 开始帧同步

```csharp
// 连接成功后
person.OnConnected += p => {
    p.CreateAndJoinRoom(4);  // 创建最多 4 人的房间
    // 或 p.JoinRoom(roomId); 加入已有房间
};

// 加入房间后
person.OnJoinedRoom += p => {
    p.RequestStart();  // 请求开始帧同步
};
```

### 3.4 发送输入

```csharp
// 帧同步运行中，发送玩家输入
byte[] inputData = EncodeYourInput(moveDir, actions);
person.SendInput(inputData);
```

**重要**：输入数据格式由你定义，BoomNetwork 只负责传输。所有客户端必须用相同的编解码。

## 4. 快照（重连恢复）

注册快照回调，BoomNetwork 自动按服务器配置的间隔上传：

```csharp
// 序列化当前游戏状态
person.TakeSnapshot = () => {
    // 返回你的游戏状态二进制数据
    return SerializeGameState();
};

// 重连时恢复游戏状态
person.LoadSnapshot = data => {
    DeserializeGameState(data);
};
```

快照间隔由服务器 `config.yaml` 的 `snapshotIntervalFrames` 控制，客户端不需要硬编码。

快照有三个用途：
- **定时上传**：每 N 帧自动上传到服务器，供重连恢复用
- **重连恢复**：断线重连时服务器下发最新快照 + 补帧
- **迟到者加入**：中途加入运行中的房间时，服务器自动下发快照 + 补帧，无需额外代码

## 5. Person 生命周期

```
         Connect()
            │
            ▼
      ┌─ Connecting ─┐
      │               │ SessionBind 成功
      │               ▼
      │          Connected
      │               │
      │         JoinRoom() / CreateAndJoinRoom()
      │               │
      │               ▼
      │           InRoom
      │               │
      │         RequestStart()
      │               │
      │               ▼
      │           Syncing ◄──── OnFrame 持续触发
      │               │
      │          断线 / LeaveRoom()
      │               │
      │               ▼
      └────────► Disconnected
                      │
                Connect() (带身份重连)
                      │
                      ▼
                  Connecting → Reconnected → Syncing
```

## 6. 事件一览

| 事件 | 触发时机 | 参数 |
|------|---------|------|
| `OnConnected` | 首次连接成功 | Person |
| `OnJoinedRoom` | 加入房间成功 | Person |
| `OnReady` | 首次加入 或 重连成功 | Person |
| `OnFrameSyncStart` | 帧同步开始 | Person, FrameSyncInitData |
| `OnFrame` | 收到服务器帧 | Person, FrameData |
| `OnRemotePlayerJoined` | 其他玩家加入 | Person, int playerId |
| `OnRemotePlayerLeft` | 其他玩家离开 | Person, int playerId |
| `OnReconnected` | 重连成功 | Person |
| `OnDisconnected` | 断开连接 | Person |
| `OnLeftRoom` | 离开房间 | Person, int oldPlayerId |
| `OnLog` | 内部日志 | Person, string msg |

## 7. 帧数据结构

```csharp
struct FrameData
{
    uint FrameNumber;        // 帧号（从 1 开始递增）
    PlayerInput[] Inputs;    // 本帧所有玩家输入
}

struct PlayerInput
{
    int PlayerId;            // 玩家 ID
    byte[] Data;             // 输入数据（你定义的格式）
    int DataLength;          // 有效长度
}
```

## 8. 多客户端测试（ParrelSync）

安装 [ParrelSync](https://github.com/VeriorPies/ParrelSync) 后：

1. `ParrelSync > Clones Manager > Create New Clone`
2. 主编辑器：Connect → Create Room → Join → Start Game
3. 克隆编辑器：填 Room ID → Connect → Join
4. 两个编辑器同步运行，用 Drop1s / Drop8s 测试重连

## 9. 常见问题

### 连接失败

- 确认服务器已启动：`go run ./cmd/framesync/ -config config.yaml`
- 确认端口匹配：服务器默认 9000，客户端 `NetworkConfig.port` 也是 9000
- 防火墙 / 云服务器安全组放行端口

### 帧号不同步

- 确保所有客户端的帧处理逻辑是确定性的（相同输入 → 相同结果）
- 不要在帧逻辑中使用 `Random`、`Time.deltaTime`、`Physics` 等非确定性 API
- 用 HUD 上的 World Hash 对比两端状态

### 重连后状态不对

- 检查 `TakeSnapshot` / `LoadSnapshot` 是否序列化了完整游戏状态
- 检查服务器日志 `Snapshot updated at frame X` 确认快照上传成功

### Package 更新后 Unity 没生效

- UPM git 引用有缓存，`Window > Package Manager > 左上角刷新` 或删除 `Library/PackageCache` 后重启
