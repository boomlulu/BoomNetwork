# Web Platform Support TODO

> BoomNetwork WebGL 支持实施清单
> Created: 2026-03-30

---

## P0 — 必须做（WebGL 能跑起来）

- [x] **1. WebGL-safe WebSocket Transport** _(done 2026-03-30)_
  - `cli/Client/Transport/WebGLWebSocketTransport.cs` — 纯 `Tick()` 驱动，`#if UNITY_WEBGL && !UNITY_EDITOR`
  - `unity/.../Runtime/Plugins/WebGL/BoomNetworkWS.jslib` — JS interop 桥接浏览器原生 WebSocket
  - Poll 模式：JS `onmessage` → 队列 → C# `Tick()` 轮询，无线程无 Task
  - 已同步到 `unity/.../Runtime/Client/Transport/WebGLWebSocketTransport.cs`

- [x] **2. Transport 注入（去掉硬编码）** _(done 2026-03-30)_
  - `FrameSyncClient` 构造函数新增 `Func<ITransport>? transportFactory` 参数
  - `CreateDefaultTransport()` 静态方法：`#if UNITY_WEBGL` → WebGL transport，否则 TCP
  - 字段类型 `TcpClientTransport?` → `ITransport?`，零破坏性变更（默认值不变）

- [x] **3. 条件编译隔离** _(done 2026-03-30)_
  - `TcpClientTransport.cs` — `#if !UNITY_WEBGL || UNITY_EDITOR`
  - `KcpClientTransport.cs` — `#if !UNITY_WEBGL || UNITY_EDITOR`
  - `WebSocketClientTransport.cs`（桌面版）— `#if !UNITY_WEBGL || UNITY_EDITOR`
  - `Kcp/KCP.cs`, `Kcp/ByteBuffer.cs`, `Kcp/UDPSession.cs` — 同上
  - 已同步到 `unity/com.boom.boomnetwork/Runtime/` 对应路径

## P1 — 应该做（稳定性和安全）

- [x] **4. WSS (TLS) 支持** _(done 2026-03-30)_
  - `WebGLWebSocketTransport` 增加 `ForceWss` 属性，支持强制 wss://
  - `docs/deploy/nginx-wss.conf` — 完整 nginx 反代配置（TLS 终止 + WebSocket upgrade + 静态资源）
  - 腾讯云实际部署：待配证书后验证

- [x] **5. CORS / CSP 配置** _(done 2026-03-30)_
  - `SecurityConfig` 新增 `AllowedOrigins []string` 字段
  - `WsServer` 升级为实例级 `upgrader`，`SetSecurity` 时更新 Origin 检查
  - `checkOrigin()` 函数：空列表=允许所有（向后兼容），非空=白名单
  - nginx 配置已包含 CORS headers + COOP/COEP（WebGL SharedArrayBuffer 需要）

- [x] **6. 重连策略 WebGL 验证** _(done 2026-03-30)_
  - `QuickReconnectStrategy` / `SnapshotReconnectStrategy` 代码审计通过
  - 全部事件驱动（OnConnected 回调 + SendAsync），无阻塞调用
  - `Tick()` 单线程模型天然兼容，无需代码修改
  - 端到端验证：待 WebGL build 后实测

- [x] **7. IL2CPP / AOT 兼容性检查** _(done 2026-03-30)_
  - 全量 grep 确认：零反射使用（无 GetType/typeof/Activator/MethodInfo/dynamic）
  - codec 全手写二进制，泛型仅 `ArrayPool<byte>` 和 `Func<ITransport>`（AOT safe）
  - 无需 `link.xml`（无 stripping 风险）
  - 实际 WebGL build：待验证

## P2 — 加分项（降低入门门槛）

- [x] **8. Web Demo 页面** _(done 2026-03-30)_
  - `web-demo/index.html` — 自适应 canvas + 进度条 + 全屏按钮
  - 占位符模板，放入 Unity WebGL build 文件即可使用
  - nginx 配置已包含静态资源托管 + COOP/COEP headers

---

## 下一步（需要 Unity 编辑器操作）

1. 在 Unity 中切换到 WebGL 平台，确认编译通过
2. 选 VampireSurvivors Demo 做 WebGL build
3. 将 Build/ 输出放入 `web-demo/Build/`，更新 index.html 中的文件名
4. 腾讯云部署 nginx + 证书，验证 wss:// 连接
5. 端到端测试：浏览器 → wss → nginx → WsServer → 帧同步
