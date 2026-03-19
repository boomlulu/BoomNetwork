# BoomNetwork Unity Package

帧同步网络库的 Unity 封装。

## 安装

Unity Package Manager → Add package from git URL:

```
https://github.com/luwenyiCC/BoomNetwork.git?path=unity/com.boom.boomnetwork
```

或者本地安装：Add package from disk → 选择 `unity/com.boom.boomnetwork/package.json`

## 快速接入

1. 创建空 GameObject
2. 添加 `BoomNetworkManager` 组件
3. Inspector 中设置 Host / Port / Transport Type
4. 通过代码注册事件和发送输入：

```csharp
var network = GetComponent<BoomNetworkManager>();

// 注册帧回调
network.Client.OnFrame += frame => {
    // 执行游戏逻辑
};

// 连接
network.Connect();

// 发送输入
network.SendInput(myInputBytes);
```

## Inspector 参数

| 参数 | 默认值 | 说明 |
|------|--------|------|
| Host | 127.0.0.1 | 服务器地址 |
| Port | 9000 | 服务器端口 |
| Transport Type | TCP | TCP 或 KCP |
| Heartbeat Interval | 3000ms | 心跳发送间隔 |
| Heartbeat Timeout | 10000ms | 心跳超时判定断线 |
| Quick Reconnect Attempts | 3 | 快速重连尝试次数 |
| Snapshot Reconnect Attempts | 2 | 快照重连尝试次数 |
| Log Enabled | true | 是否输出 Debug.Log |

## 服务器

```bash
cd svr && go run ./cmd/framesync/ :9000
```
