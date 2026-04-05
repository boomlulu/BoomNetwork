# 平台发布指南

BoomNetwork 支持 Android 和 WebGL 部署。本文记录两个平台的构建配置要点、注意事项和常见坑。

---

## Android

### 权限

UPM 包内已内置 `Plugins/Android/AndroidManifest.xml`，Unity 构建时会自动合并：

```xml
<uses-permission android:name="android.permission.INTERNET" />
<uses-permission android:name="android.permission.ACCESS_NETWORK_STATE" />
```

无需手动配置，开箱即用。

### 构建设置

| 选项 | 推荐值 | 说明 |
|------|--------|------|
| Scripting Backend | IL2CPP | 性能更好，AOT 编译 |
| Target Architecture | ARM64 + ARMv7 | 覆盖主流机型 |
| Minimum API Level | 26 (Android 8.0) | 支持最广且 API 稳定 |
| Internet Access | Required | 自动，权限已在 Manifest 中声明 |
| Write Permission | External (SDCard) | 仅日志输出需要 |

### 心跳超时

`BoomNetworkManager` → **Heartbeat Timeout Ms** 建议设为 **15000**（15 秒）。

移动网络（4G/5G）RTT 波动大，默认 10s 在弱网时会误判超时触发重连。

### 后台/前台切换

框架已在 `BoomNetworkManager` 中实现 `OnApplicationPause` / `OnApplicationFocus`：
- 进入后台 → 自动暂停重连（节约电量，防止后台无限重试）
- 回到前台 → 自动恢复重连，若断线则触发重连流程

无需额外配置。

### 传输协议选择

Android 支持 TCP、KCP 和 WebSocket 三种协议：

```
BoomNetworkManager → Transport → TransportType
  Auto        → TCP（默认）
  KCP         → 低延迟，UDP，适合实时对战
  WebSocket   → 若网络有防火墙屏蔽 TCP 非标准端口时使用
```

---

## WebGL

### 服务器端配置

WebGL 运行在浏览器中，**只能使用 WebSocket**（浏览器不支持裸 TCP/UDP）。

服务器默认在 **`:9001`** 额外监听 WebSocket 端口：

```yaml
# config.yaml
addr: ":9000"    # TCP/KCP 主端口（原生客户端）
wsAddr: ":9001"  # WebSocket 端口（WebGL 专用）
```

两个端口共享完全相同的路由逻辑，原生客户端和 WebGL 客户端可以同房联机。

### Unity 构建设置

| 选项 | 推荐值 | 说明 |
|------|--------|------|
| Compression Format | Brotli | 减小包体，需 Web Server 支持 |
| Publishing Settings → Memory Size | 256 MB | 推荐最低值（复杂场景用 512 MB） |
| Exception Support | Explicitly Thrown | 性能与可调试性的平衡点 |
| Enable Exceptions | None（Release）/ ExplicitlyThrown（Dev） | |

> **内存配置**：PlayerPrefs 中的 `memorySize` 影响 Emscripten 堆大小。MinecraftDemo 含大型地形，建议 512 MB。

### HTTPS 与 wss://

- 若 WebGL 部署在 **HTTPS** 站点，必须使用 `wss://`（否则浏览器拒绝混合内容）
- 框架双层自动处理：
  1. C# 层：`port == 443` 或 `ForceWss = true` → 直接构造 `wss://`
  2. JS 层（jslib）：检测 `window.location.protocol === "https:"` 自动升级

**典型部署**：Nginx 反向代理 443→9001，客户端只需填 `port=443`，框架自动走 wss。

```nginx
server {
    listen 443 ssl;
    location /ws {
        proxy_pass http://127.0.0.1:9001;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "Upgrade";
    }
}
```

### 鼠标锁定（Pointer Lock）

浏览器安全策略要求 `Cursor.lockState = Locked` **必须**在用户手势（点击/键盘）的回调中调用。

`MinecraftDemo` 已适配：WebGL 下首次点击时锁定，非 WebGL 下 `Start()` 即锁定。

其他 Demo 若需鼠标锁定，请遵循同样模式：

```csharp
#if UNITY_WEBGL && !UNITY_EDITOR
    private bool _waitingForClick;

    void Start() { _waitingForClick = true; }

    void Update()
    {
        if (_waitingForClick && Input.GetMouseButtonDown(0))
        {
            _waitingForClick = false;
            Cursor.lockState = CursorLockMode.Locked;
        }
    }
#else
    void Start() { Cursor.lockState = CursorLockMode.Locked; }
#endif
```

### 消息队列

JS 侧 `onmessage` 队列上限 512 条，C# Tick 每帧最多处理 64 条。超出时旧消息被丢弃，服务端快照机制保证状态一致性。

### WebGL 不支持的功能

| 功能 | 原因 | 替代 |
|------|------|------|
| TCP Transport | 浏览器无 TCP API | WebSocket（自动切换） |
| KCP Transport | 浏览器无 UDP API | WebSocket |
| `System.Threading` | Emscripten 单线程 | `WebGLWebSocketTransport` 无线程设计 |
| `Task` / `async` | 不可靠 | 改用 Tick 轮询 |

---

## 参考

- [Unity WebGL 内存](https://docs.unity3d.com/Manual/webgl-memory.html)
- [Android 权限](https://developer.android.com/guide/topics/manifest/uses-permission-element)
- [Pointer Lock API](https://developer.mozilla.org/en-US/docs/Web/API/Pointer_Lock_API)
- BoomNetwork 架构：[doc/shared/07-architecture.md](07-architecture.md)
