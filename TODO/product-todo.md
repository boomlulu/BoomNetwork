# 产品 TODO

> 从 Beta → 开源推广 → 行业标杆的完整待办
>
> 标记：🤖 = Claude 可自主完成 | 👤 = 需要你参与 | ⏳ = 进行中

---

## 已完成 ✅

- [x] 🤖 llms.txt + llms-full.txt + cheatsheet（AI 可发现性）
- [x] 🤖 goreleaser + GitHub Actions CI/CD
- [x] 🤖 GitHub Release v0.1.0（5 平台二进制）
- [x] 🤖 CI badge + Release badge + License badge
- [x] 🤖 Docker 镜像发布流水线（ghcr.io）
- [x] 🤖 Mermaid 生命周期状态图
- [x] 🤖 测试覆盖提升（Go 30%→70%, C# 15%→50%）
- [x] 🤖 go.mod module path 修正（boom → boomlulu）
- [x] 🤖 golangci-lint 加到 CI
- [x] 🤖 CONTRIBUTING.md + Issue/PR 模板
- [x] 🤖 Makefile（test/build/run/lint/docker/clean）
- [x] 🤖 GitHub repo topics + description
- [x] 🤖 GitHub Discussions 开启
- [x] 🤖 Step-by-step 教程（doc/tutorial.md）
- [x] 🤖 性能基准报告（doc/performance.md）
- [x] 🤖 docker-compose.yml
- [x] 🤖 技术文章初稿（中文 + 英文）
- [x] 🤖 github-release skill（踩坑知识库）
- [x] 👤 README 重写（设计哲学 + 核心能力 + 示例表）
- [x] 👤 仓库迁移 luwenyiCC → boomlulu
- [x] 👤 GitHub Actions 权限修复

---

## 你需要做的 👤

### P0 — 立即可做

- [ ] 👤 **教程截图** — 打开 doc/tutorial.md，跑一遍教程流程，替换 5 个 `📸 截图占位`
- [ ] 👤 **发布中文文章** — articles/why-no-rollback-cn.md → 掘金 / 知乎 / SegmentFault
- [ ] 👤 **发布英文文章** — articles/why-no-rollback-en.md → dev.to / Medium / r/gamedev
- [ ] 👤 **创建 Discord 服务器** — 社区种子，README 加邀请链接
- [ ] 👤 **打新 Release tag** — 当前 dev1.0 包含大量改进，打 v0.2.0 触发新的 Release + Docker 镜像

### P1 — 本周

- [ ] 👤 **录"AI 做多人游戏"视频** — 脚本建议：让 Claude 生成完整多人游戏代码 → 5 分钟跑通 → 发 B 站/YouTube
- [ ] 👤 **找 3-5 个外部用户试用** — 收集反馈，这比任何功能都重要
- [ ] 👤 **提交 awesome-go PR** — https://github.com/avelino/awesome-go （Game Development 分类）
- [ ] 👤 **提交 awesome-unity PR** — https://github.com/RyanNielson/awesome-unity （Networking 分类）

### P2 — 本月

- [ ] 👤 **微信/QQ 技术群** — 中文开发者社区
- [ ] 👤 **Game Jam 赞助** — 用 BoomNetwork 做多人游戏的 jam
- [ ] 👤 **OpenUPM 发布** — Unity 开发者标准搜索入口（需注册 openupm.com）

---

## Claude 可自主完成的 🤖

### P0 — 下次 session 优先

- [ ] 🤖 **codecov 测试覆盖率 badge** — 加到 CI + README
- [ ] 🤖 **SECURITY.md** — 安全策略文档（企业用户信任信号）
- [ ] 🤖 **FAQ 文档** — 从 Discussions 和常见问题整理
- [ ] 🤖 **API 稳定性承诺文档** — 版本策略、breaking change 规则

### P1 — 技术增强

- [ ] 🤖 **WebSocket Transport** — 浏览器/微信小游戏接入（生态扩展核心）
- [ ] 🤖 **TLS 支持** — TCP + Admin HTTP 加密（生产环境硬性要求）
- [ ] 🤖 **TypeScript SDK** — Web/Cocos 开发者（最大增量用户群）
- [ ] 🤖 **权威转移 Phase 2** — 运行时权威转移完整实现
- [ ] 🤖 **帧录制与回放** — desync 排查 + 自动化测试 + 比赛录像

### P2 — 体验打磨

- [ ] 🤖 **Template 项目** — clone 即用的 Unity 模板仓库
- [ ] 🤖 **在线 Playground** — WebGL + WebSocket 零安装体验
- [ ] 🤖 **更多 Demo** — 赛车、塔防、棋牌等不同类型验证
- [ ] 🤖 **CLI 工具** — `boom init` / `boom server` / `boom deploy`
- [ ] 🤖 **服务端插件系统** — Hook 点 + Lua/WASM 脚本

### P3 — 行业定位

- [ ] 🤖 **自权威网络模型白皮书** — 学术级形式化定义
- [ ] 🤖 **AI NPC 协议** — LLM 驱动的 NPC 作为虚拟玩家加入房间
- [ ] 🤖 **AI Desync Detective** — Replay 录制 → AI 自动分析 desync 原因
- [ ] 🤖 **多语言 SDK** — Go / Lua / Godot
- [ ] 🤖 **集群方案** — 多实例分片 + K8s Helm Chart
- [ ] 🤖 **BoomNetwork Cloud** — 可选托管服务（商业化路径）

---

## 产品完成度追踪

```
Session 开始:  技术 ~80%  产品 ~15%
Session 结束:  技术 ~85%  产品 ~60%

发现:  ████████░░ 70%   (llms.txt + topics + articles draft)
体验:  ██████░░░░ 60%   (tutorial + docker + 3 startup options)
信任:  █████░░░░░ 45%   (CI badge + perf report + tests)
采用:  ██░░░░░░░░ 20%   (仅 C#/Unity，缺 TS/Web/TLS)
留存:  █░░░░░░░░░ 15%   (Discussions 开了，缺 Discord/FAQ)
```

---

## 下一个里程碑

**v0.2.0 — "别人能用"（产品 70%）**

验收标准：一个没接触过 BoomNetwork 的 Unity 开发者，从 GitHub 首页到两个角色同步移动 < 10 分钟。

关键卡点：
1. 教程有截图（👤）
2. v0.2.0 Release 有 Docker 镜像（👤 打 tag）
3. 至少 1 篇文章发布（👤）
4. Discord 可加入（👤）
