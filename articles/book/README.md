# 书：用系统设计思维重构游戏实时网络

## 核心命题

> 如何用 K8s 级别的系统设计思维，解决游戏实时网络的确定性问题。

BoomNetwork 是论据，不是主题。

---

## 目标读者

- 游戏后端工程师（主）
- 想做实时系统的分布式工程师
- 对"确定性系统"感兴趣的架构师

---

## 初步结构

### 第一部分：为什么实时网络这么难
- 非确定性的本质（float 跨平台、时序不稳定）
- 网络延迟和游戏体验的矛盾
- 现有方案的取舍（帧同步 vs 状态同步 vs 预测回滚）

### 第二部分：确定性系统的设计哲学（核心）
- Desired State vs Actual State
- 电平触发 vs 边沿触发（Level-Triggered vs Edge-Triggered）
- 幂等性设计
- Delta 传输 vs 全量传输
- Reconciliation Loop（协调循环）
- Declarative vs Imperative（声明式 vs 命令式）

### 第三部分：BoomNetwork 的工程实践
- FInt 定点数（跨平台确定性）
- FrameSync 帧循环设计
- 512 怪零带宽：纯客户端确定性模拟
- GameState 变更的两条确定性路径
- 快照 + 反哈希 Desync 检测

---

## 验证路径（先做这个，再考虑写书）

1. 文章：「为什么我用 K8s 的思想重新设计了游戏帧同步」
   - 发布到掘金 / 知乎 / InfoQ
   - 验证命题是否有人感兴趣
2. GitHub 开源 BoomNetwork，README 讲设计哲学
3. 看转发量、Star 数、评论质量
4. 反响好 → 扩展成书

---

## 参考
- 《Designing Distributed Systems》— Brendan Burns（K8s 联合创始人）
- 《游戏引擎架构》— Jason Gregory
- 《网络多人游戏架构与编程》— Joshua Glazer
- 现有文章：`articles/why-no-rollback-cn.md`
