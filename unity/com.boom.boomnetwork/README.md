# BoomNetwork Unity Package

帧同步网络库的 Unity 封装。

## 安装

Unity Package Manager → Add package from git URL:

```
https://github.com/luwenyiCC/BoomNetwork.git?path=unity/com.boom.boomnetwork#dev1.0
```

### GM 工具包（可选，Editor-only）

开发期使用，不进客户端 build。提供 ServerWindow（服务器控制 + 流量统计）等 Editor 工具。

```
https://github.com/luwenyiCC/BoomNetwork.git?path=unity/com.boom.boomnetwork.gm#dev1.0
```

## 两种接入方式

### 方式一：BoomNetworkManager（拖组件）

```csharp
var network = GetComponent<BoomNetworkManager>();
network.Client.OnFrame += frame => { /* 执行游戏逻辑 */ };
network.Connect();
network.SendInput(myInputBytes);
```

Inspector 参数：

| 参数 | 默认值 | 说明 |
|------|--------|------|
| Host | 127.0.0.1 | 服务器地址 |
| Port | 9000 | 服务器端口 |
| Heartbeat Interval | 3000ms | 心跳发送间隔 |
| Heartbeat Timeout | 10000ms | 心跳超时判定断线 |
| Log Enabled | true | 是否输出 Debug.Log |

### 方式二：Person 薄适配器（推荐用于 Demo / 多客户端）

```csharp
var person = new Person();
person.Connect(networkConfig);
person.OnConnected += p => Debug.Log($"Player {p.PlayerId}");
person.OnFrame += (p, frame) => ApplyFrame(frame);
person.CreateAndJoinRoom(4);
person.RequestStart();

// 游戏循环
person.Tick(Time.deltaTime * 1000f);
person.SendInput(inputBytes);
```

Person 是纯代理，所有网络能力来自内部的 FrameSyncClient。

### 方式三：实体权威同步（推荐用于需要预测的游戏）

每个实体实现 `IEntitySync` 接口，权威实体本地立刻响应，远端通过 Dead Reckoning + 惯性模型平滑追踪。

```csharp
// 注册权威实体（每帧自动发送状态）
person.RegisterAuthorityEntity(myEntity); // myEntity : IEntitySync

// 接收远端实体状态
person.OnEntityState += (senderPid, entityId, data, offset, len) =>
    remoteEntity.OnRemoteState(data, offset, len, senderPid);

// 延迟查看
Debug.Log($"RTT: {person.RttMs}ms");
```

## 服务器

```bash
# 用配置文件启动（推荐）
cd svr && go run ./cmd/framesync/ -config=cmd/framesync/config.yaml

# 验证 Admin HTTP
curl http://127.0.0.1:9091/health
curl http://127.0.0.1:9091/stats
```

## GM 工具包

安装 `com.boom.boomnetwork.gm` 后，菜单栏出现 **BoomNetwork / Server Window**：

- 服务器状态：RUNNING / STOPPED + 房间数 + 玩家数 + 运行时长
- 流量统计：总量 / 近 1 分钟 / 近 5 秒速率
- 一键启动 / 停止服务器
- 通过 HTTP /health 探测，不走 TCP 游戏协议，零日志噪声
