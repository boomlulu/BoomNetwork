// BoomNetwork CardGame Demo — Hero Definition System (Base Infrastructure)
//
// 武将管理：每位武将的称号、技能名称、技能描述和技能效果集中在此文件。
//
// 新增武将步骤：
//   1. 在 HeroType 枚举中添加新值
//   2. 在 Heroes/ 目录下新建 XxxDefinition.cs，继承 HeroDefinition，重写所需 Hook
//   3. 在 HeroRegistry._all 和 Selectable 里各追加一行
//
// 技能 Hook（按需重写，默认无效果）：
//   GetDrawBonus          — 摸牌阶段额外摸牌数
//   CanActivate           — 主动技（出牌阶段）是否可发动
//   Activate              — 主动技执行
//   IsPendingActivation   — 主动技是否处于待操作状态
//   OnUseCard             — 使用牌时（牌已离手，Apply 前）
//   OnDesignateTarget     — 指定目标时（有目标牌，Apply 前）
//   OnBeTarget            — 成为目标时（Apply 前）
//   CanActivateOnAttack   — 攻击技（出杀后）是否可发动
//   AttackSkillNeedsJudge — 攻击技是否需要翻牌判定

using System.Collections.Generic;

namespace BoomNetwork.Samples.CardGame
{
    // ── 枚举 ──────────────────────────────────────────────────────────────────

    /// <summary>技能的触发时机类型</summary>
    public enum SkillTiming
    {
        Passive,     // 被动技：无需主动发动，条件满足时自动生效
        ActiveTurn,  // 主动技（出牌阶段）：出牌阶段可点击按钮发动
    }

    public enum HeroType : byte
    {
        None   = 0,   // 无名将（无技能）
        ZhouYu = 1,   // 周瑜
        GuanYu = 2,   // 关羽
        MaChao = 3,   // 马超
    }

    // ── 抽象基类 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 武将定义基类。每种武将对应一个子类实例，托管在 HeroRegistry 中。
    /// 子类按需重写技能 Hook，未重写的 Hook 默认返回零值（无效果）。
    /// </summary>
    public abstract class HeroDefinition
    {
        public abstract HeroType HeroType  { get; }
        public abstract string   Name      { get; }
        public abstract string   Title     { get; }
        public abstract string   SkillName { get; }
        public abstract string   SkillDesc { get; }

        // ── 出牌阶段主动技 Hook ───────────────────────────────────────────────

        public virtual SkillTiming SkillTiming => SkillTiming.Passive;

        public virtual int  GetDrawBonus(CardGameState state, int slot) => 0;
        public virtual bool CanActivate(CardGameState state, int slot)  => false;
        public virtual void Activate(CardGameState state, int slot)     { }
        public virtual bool IsPendingActivation(CardGameState state, int slot) => false;

        // ── 卡牌事件 Hook ─────────────────────────────────────────────────────

        /// <summary>
        /// 使用牌时：该玩家打出一张牌、牌已离开手牌后触发。
        /// 早于 Apply()，可读写 state。
        /// </summary>
        public virtual void OnUseCard(CardGameState state, int actorSlot, Card card) { }

        /// <summary>
        /// 指定目标时：该玩家打出有目标的牌并确定目标后触发（targetSlot >= 0）。
        /// 早于 Apply()。
        /// </summary>
        public virtual void OnDesignateTarget(CardGameState state, int actorSlot, int targetSlot, Card card) { }

        /// <summary>
        /// 成为目标时：该玩家成为一张牌的目标后触发。
        /// 早于 Apply()。
        /// </summary>
        public virtual void OnBeTarget(CardGameState state, int targetSlot, int actorSlot, Card card) { }

        // ── 攻击技 Hook（出杀后、目标响应前）───────────────────────────────────

        /// <summary>
        /// 出杀后、目标响应前是否可发动攻击技（如铁骑）。
        /// 仅攻击方可触发；每次杀只能触发一次（TieJiUsed 为 false）。
        /// </summary>
        public virtual bool CanActivateOnAttack(CardGameState state, int slot) => false;

        /// <summary>
        /// 攻击技元数据：是否需要进行判定（由 Simulation 负责翻牌）。
        /// 返回 true 时 Simulation 将翻开牌堆顶一张，红色则封锁目标的响应权。
        /// </summary>
        public virtual bool AttackSkillNeedsJudge => false;
    }

    // ── 注册表 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 武将注册表：唯一的武将元数据中心。
    /// </summary>
    public static class HeroRegistry
    {
        static readonly HeroDefinition _fallback = new GenericHeroDefinition();

        static readonly Dictionary<HeroType, HeroDefinition> _all = new()
        {
            { HeroType.None,   new GenericHeroDefinition() },
            { HeroType.ZhouYu, new ZhouYuDefinition()      },
            { HeroType.GuanYu, new GuanYuDefinition()      },
            { HeroType.MaChao, new MaChaoDefinition()      },
        };

        /// <summary>
        /// 可供玩家选择的武将列表（含"无名将"作为无技能选项）。
        /// 新增武将后在此追加。
        /// </summary>
        public static readonly HeroType[] Selectable =
        {
            HeroType.None,
            HeroType.ZhouYu,
            HeroType.GuanYu,
            HeroType.MaChao,
        };

        /// <summary>获取武将定义；找不到时返回无名将。</summary>
        public static HeroDefinition Get(HeroType type)
            => _all.TryGetValue(type, out var def) ? def : _fallback;
    }
}
