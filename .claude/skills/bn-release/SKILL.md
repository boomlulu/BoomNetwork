---
name: bn-release
description: GitHub Release 发布流水线（goreleaser + GitHub Actions）。覆盖 CI/CD 配置、跨平台构建、tag 触发、常见坑和排查清单。用于新项目配置发布流水线或排查 Release 失败。
triggers:
  - goreleaser
  - github release
  - github actions
  - CI/CD
  - tag
---

# GitHub Release 发布技能

## 架构概览

```
git tag v0.1.0 && git push origin v0.1.0
        │
        ▼
.github/workflows/release.yml (on: push tags: v*)
        │
        ▼
goreleaser-action@v6 → 读 .goreleaser.yaml
        │
        ├── before.hooks (go mod tidy)
        ├── builds (CGO_ENABLED=0, ldflags 注入版本)
        ├── archives (tar.gz/zip + 附件)
        ├── checksum
        └── release → GitHub Release 页面 + 二进制下载
```

## BoomNetwork 当前配置

- `.goreleaser.yaml` — 在 repo 根目录，Go 代码在 `svr/` 子目录
- `.github/workflows/release.yml` — tag push 触发
- `.github/workflows/ci.yml` — push/PR 触发 Go test + C# test
- 构建目标：Linux amd64/arm64 + macOS amd64/arm64 + Windows amd64
- 附件：config.yaml + boomnetwork.service + README + LICENSE
- GitHub 账户：boomlulu（从 luwenyiCC 迁移）
- SSH key：`~/.ssh/id_ed25519_boomlulu`（SSH config: `github.com-boomlulu`）
- Remote URL：`git@github.com-boomlulu:boomlulu/BoomNetwork.git`

## 踩坑速查表

| 坑名 | 症状关键词 | 一行修复方案 |
|------|-----------|-------------|
| 坑1: goreleaser v2 format 废弃 | `DEPRECATED`, `goreleaser check` 失败 | 把 `archives.format` 改为 `archives.formats`（数组），`format_overrides` 同理 |
| 坑2: hooks 不支持 shell 内建 | `exec: "cd": executable file not found` | 用 `bash -c "cd svr && go mod tidy"` 包裹 |
| 坑3: Go module 在子目录 | `go.mod not found` | 设 `builds.dir: svr`，`main` 相对于 dir；`archives.files.src` 相对于 repo 根 |
| 坑4: 同名 tag 重建不触发 | workflow 未触发，无新 Run | 换新版本号，不要删建同名 tag |
| 坑5: 账户 billing 锁 | `account locked due to billing issue`, `runner_id: 0` | 检查 github.com/settings/billing 解锁付款方式 |
| 坑6: Workflow permissions 只读 | CI 过但 Release 失败，`403` 创建 release | 仓库 Settings → Actions → General → Workflow permissions → Read and write |
| 坑7: 仓库转移后 owner 未更新 | goreleaser `403`，尝试旧 owner | `grep -rn "旧owner"` 全局替换，涉及 `.goreleaser.yaml` / `package.json` / docs |
| 坑8: SSH 多账户冲突 | `Key is already in use` | 生成新 key → 加入新账户 → `~/.ssh/config` 配 Host 别名 → 更新 remote URL |

## 排查 Release 失败的决策树

```
Release workflow 失败
  │
  ├── runner_id: 0, steps: []
  │   ├── "billing issue" → 检查 github.com/settings/billing
  │   ├── "Actions disabled" → Settings → Actions → Allow all
  │   └── permissions → Workflow permissions → Read and write
  │
  ├── runner_id > 0, 某个 step 失败
  │   ├── "Run GoReleaser" 失败
  │   │   ├── "cd: executable not found" → bash -c 包裹
  │   │   ├── "go.mod not found" → 检查 builds.dir 配置
  │   │   ├── "release already exists" → 删除旧 release 或换版本号
  │   │   └── "403 Forbidden" → Workflow permissions / owner 不匹配
  │   ├── "Setup Go" 失败 → 检查 go-version 和 cache-dependency-path
  │   └── "Checkout" 失败 → 检查 fetch-depth: 0
  │
  └── workflow 未触发
      ├── tag 格式不匹配 → 确认 on.push.tags 和 tag 名一致
      ├── 同名 tag 重建 → 换新版本号
      └── workflow 文件不在默认分支 → 确认 .github/workflows/ 在 tag 指向的 commit 上
```

## 发布新版本 Checklist

```
□ 代码已合并到目标分支
□ CHANGELOG.md 已更新
□ goreleaser check 本地验证通过
□ CI 测试通过
□ git tag vX.Y.Z && git push origin vX.Y.Z
□ 检查 GitHub Actions → Release workflow 状态
□ 确认 Release 页面有 5 个二进制 + checksums.txt
□ 下载一个验证：./boomnetwork-server -config config.yaml
```
