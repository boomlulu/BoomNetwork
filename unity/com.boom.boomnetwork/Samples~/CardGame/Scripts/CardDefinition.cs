// BoomNetwork CardGame Demo — Card Definition System (Base Infrastructure)
//
// 职责分离：卡牌的元数据、使用合法性、效果逻辑全部在此文件集中管理。
// CardGameSimulation 只负责状态机和 I/O；具体每张牌"能不能用、怎么用"
// 一律委托给 CardRegistry。
//
// 新增卡牌步骤：
//   1. 在 CardType 枚举中添加新值（CardGameSimulation.cs）
//   2. 在 Cards/ 目录下新建 XxxDefinition.cs，继承 CardDefinition 或 EquipmentDefinition
//   3. 在 CardRegistry._all 里注册一行
//
// 无 Unity 依赖，可在纯 C# 环境下单元测试。

using System.Collections.Generic;

namespace BoomNetwork.Samples.CardGame
{
    // ── 枚举 ──────────────────────────────────────────────────────────────────

    /// <summary>卡牌可使用的时机阶段</summary>
    public enum UseTiming
    {
        ActiveTurn,   // 出牌阶段（己方回合，Phase == PlayerTurn）
        Response,     // 响应阶段（成为某张牌的目标时）
        AnyTime,      // 任意时机（随时可打出）
    }

    /// <summary>目标选择规则</summary>
    public enum TargetType
    {
        None,         // 无目标（效果直接作用于使用者或全场）
        Opponent,     // 必须选择一名对手
        Self,         // 必须选择自身
        Any,          // 可选择任意角色（含自己）
    }

    // ── 上下文 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 卡牌使用上下文：调用 CanUse 时传入，描述当前的游戏快照。
    /// 只读结构体，不包含任何引用，可安全地在纯逻辑层传递。
    /// </summary>
    public readonly struct UseContext
    {
        public readonly int       ActorSlot;              // 试图使用卡牌的玩家 slot
        public readonly TurnPhase TurnPhase;              // 当前主阶段
        public readonly SubPhase  SubPhase;               // 当前子阶段
        public readonly bool      WaitingForResponse;     // 「杀」已出，等待响应
        public readonly int       ActiveSlot;             // 当前回合玩家 slot
        public readonly int       PendingAttackSlot;      // 出「杀」的一方（-1=无）
        public readonly bool      HasPlayedKill;          // 本回合是否已出过「杀」
        public readonly bool      ActorHasZhuGeLianNu;   // 使用者是否装备了诸葛连弩
        public readonly bool      TieJiBlocked;          // 铁骑判定成功，闪被封锁
        public readonly int       DistanceToOpponent;    // 使用者到对手的距离（基础1，进攻马-1，防御马+1）

        public UseContext(CardGameState s, int actorSlot)
        {
            ActorSlot            = actorSlot;
            TurnPhase            = s.TurnPhase;
            SubPhase             = s.SubPhase;
            WaitingForResponse   = s.WaitingForResponse;
            ActiveSlot           = s.ActiveSlot;
            PendingAttackSlot    = s.PendingAttackSlot;
            HasPlayedKill        = s.HasPlayedKill;
            ActorHasZhuGeLianNu  = s.Players[actorSlot].GetEquip(EquipSlot.Weapon).Type == CardType.ZhuGeLianNu;
            TieJiBlocked         = s.TieJiBlocked;
            int oppSlot          = 1 - actorSlot;
            bool actorHasOffHorse = s.Players[actorSlot].GetEquip(EquipSlot.OffHorse).Type == CardType.ChiTu;
            bool oppHasDefHorse   = s.Players[oppSlot].GetEquip(EquipSlot.DefHorse).Type == CardType.JueYing;
            DistanceToOpponent   = 1 - (actorHasOffHorse ? 1 : 0) + (oppHasDefHorse ? 1 : 0);
        }
    }

    // ── 效果结果 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Apply() 返回值：描述卡牌效果执行后需要 Simulation 执行的后续操作。
    /// Simulation 负责处理 EndTurn 和伤害结算（检测胜负）。
    /// </summary>
    public readonly struct ApplyResult
    {
        /// <summary>效果结算后是否立即结束本回合（轮到下家）</summary>
        public readonly bool EndTurn;

        /// <summary>对目标（targetSlot）造成的直接伤害点数，0 = 无伤害</summary>
        public readonly int Damage;

        /// <summary>
        /// 从目标手牌中随机弃掉一张（由 Simulation 用 RNG 执行，
        /// 若手牌为空则无效果）。
        /// </summary>
        public readonly bool DiscardOneFromTarget;

        /// <summary>
        /// 需要使用者从目标的手牌/装备/判定区中指定一张牌弃置。
        /// Simulation 设置 WaitingForGuoHe 标志，等待 SelectTargetCard 动作。
        /// </summary>
        public readonly bool NeedsTargetCardSelect;

        public static readonly ApplyResult None               = new(false, 0);
        public static readonly ApplyResult JustEndTurn        = new(true,  0);
        public static readonly ApplyResult DiscardTarget      = new(false, 0, true);
        public static readonly ApplyResult SelectFromTarget   = new(false, 0, false, true);

        public ApplyResult(bool endTurn, int damage, bool discardOneFromTarget = false, bool needsTargetCardSelect = false)
        {
            EndTurn               = endTurn;
            Damage                = damage;
            DiscardOneFromTarget  = discardOneFromTarget;
            NeedsTargetCardSelect = needsTargetCardSelect;
        }
    }

    // ── 抽象基类 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 卡牌定义基类。每种卡牌类型对应一个子类实例，托管在 CardRegistry 中。
    ///
    /// 子类只需声明：
    ///   • CardType / Name / Description — 元数据
    ///   • Timing / TargetRule          — 静态约束
    ///   • CanUse(ctx)                  — 动态合法性校验
    ///   • Apply(state, actor, target)  — 效果执行（调用前已通过 CanUse 校验，
    ///                                    且卡牌已从手牌移除）
    /// </summary>
    public abstract class CardDefinition
    {
        public abstract CardType   CardType    { get; }
        public abstract string     Name        { get; }
        public abstract string     Description { get; }
        public abstract UseTiming  Timing      { get; }
        public abstract TargetType TargetRule  { get; }

        public abstract bool CanUse(UseContext ctx);

        public abstract ApplyResult Apply(CardGameState state, int actorSlot, int targetSlot, Card playedCard);
    }

    // ── 注册表 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 卡牌注册表：唯一的卡牌元数据中心。
    ///
    /// CardGameSimulation 通过 CardRegistry.Get(cardType) 查询定义，
    /// UI 通过 CardRegistry.GetName / GetDescription 获取显示文本。
    ///
    /// 扩展时只需在 _all 中追加一行。
    /// </summary>
    public static class CardRegistry
    {
        static readonly Dictionary<CardType, CardDefinition> _all = new()
        {
            { CardType.Kill,          new KillDefinition()          },
            { CardType.Dodge,         new DodgeDefinition()         },
            { CardType.ZhuGeLianNu,   new ZhuGeLianNuDefinition()   },
            { CardType.GuoHeChaiQiao, new GuoHeChaiQiaoDefinition() },
            { CardType.LeBuSiShu,     new LeBuSiShuDefinition()     },
            { CardType.BaGuaZhen,     new BaGuaZhenDefinition()     },
            { CardType.ChiTu,         new ChiTuDefinition()         },
            { CardType.JueYing,       new JueYingDefinition()       },
        };

        /// <summary>获取卡牌定义；找不到时返回 null。</summary>
        public static CardDefinition Get(CardType type)
            => _all.TryGetValue(type, out var def) ? def : null;

        /// <summary>获取卡牌显示名称。</summary>
        public static string GetName(CardType type)
            => Get(type)?.Name ?? type.ToString();

        /// <summary>获取卡牌描述文本。</summary>
        public static string GetDescription(CardType type)
            => Get(type)?.Description ?? string.Empty;

        /// <summary>在给定上下文中该卡牌是否合法可用（快捷方法）。</summary>
        public static bool CanUse(CardType type, UseContext ctx)
            => Get(type)?.CanUse(ctx) ?? false;
    }
}
