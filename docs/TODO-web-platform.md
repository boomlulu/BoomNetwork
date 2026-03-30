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

- [ ] **2. Transport 注入（去掉硬编码）**
  - `FrameSyncClient.CreateNetworkStack()` 改为接受 `ITransport` 工厂或配置枚举
  - `#if UNITY_WEBGL` 自动选择 `WebGLWebSocketTransport`
  - 非 WebGL 平台保持现有默认（TCP）不变
  - 文件: `cli/Client/FrameSyncClient.cs`

- [ ] **3. 条件编译隔离**
  - `TcpClientTransport.cs` — 加 `#if !UNITY_WEBGL`
  - `KcpClientTransport.cs` — 加 `#if !UNITY_WEBGL`
  - `WebSocketClientTransport.cs`（桌面版）— 加 `#if !UNITY_WEBGL`
  - KCP 相关文件（`Kcp/` 目录）— 加 `#if !UNITY_WEBGL`
  - 确保 WebGL build 只编译 `WebGLWebSocketTransport`
  - 同步到 `unity/com.boom.boomnetwork/Runtime/` 对应路径

## P1 — 应该做（稳定性和安全）

- [ ] **4. WSS (TLS) 支持**
  - 服务器部署 nginx/caddy 反代，终止 TLS 后转发到 WsServer
  - 提供 nginx 配置示例（`docs/deploy/nginx-wss.conf`）
  - 客户端 `WebGLWebSocketTransport` 支持 `wss://` scheme
  - 腾讯云 124.220.6.174 实际部署验证

- [ ] **5. CORS / CSP 配置**
  - Admin HTTP API 加 CORS headers（如果 Web 客户端需要调用）
  - 提供 CSP 策略示例（允许 `wss://` 连接）
  - Go 服务器 `WsServer` 检查 Origin header 防止跨站劫持

- [ ] **6. 重连策略 WebGL 验证**
  - `CompositeReconnectStrategy` 在 WebGL 单线程下跑通
  - `ConnectionManager` 心跳/超时在 WebSocket 断连场景下正常工作
  - 断网 → 恢复 → 自动重连 端到端验证

- [ ] **7. IL2CPP / AOT 兼容性检查**
  - WebGL build 跑通（IL2CPP stripping 不误删）
  - 检查 codec 路径无反射、无未约束泛型虚方法
  - `link.xml` 按需添加保留规则
  - 实际 WebGL build + 运行验证

## P2 — 加分项（降低入门门槛）

- [ ] **8. Web Demo 页面**
  - 选一个 Demo（VampireSurvivors 最佳展示效果）做 WebGL build
  - 简单 HTML 托管页（自适应 canvas + loading 进度条）
  - 部署到腾讯云或 GitHub Pages，一个链接就能玩
  - README 加入 "Live Demo" 链接

---

## 实施顺序

```
1 → 2 → 3 → WebGL build 验证 → 7 → 6 → 4 → 5 → 8
```

Task 1-3 是串行依赖（先有 transport，再注入，再隔离编译）。
Task 4-7 可以并行。Task 8 最后做，需要前面全部就绪。
