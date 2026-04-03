// BoomNetwork CardGame Demo — Simulation
//
// 回合结构（参考《三国杀》）：
//   TurnStart → Judgment → Draw → Play → Discard → TurnEnd
//   每个主阶段含 Before / During / After 三个子阶段
//
// 自动推进规则：
//   非交互子阶段（如 Draw.During）在同一 Tick 内立即推进；
//   交互子阶段（Play.During、Discard.During 手牌超限时）等待玩家输入。
//
// WaitingForResponse：叠加在 Play.During 上的特殊标志，
//   表示「杀」已打出、正等待目标出「闪」或受伤。
//   响应完成后清除该标志，Play.During 继续。

using System;
using System.Collections.Generic;
using BoomNetwork.Core.FrameSync;

namespace BoomNetwork.Samples.CardGame
{
    public enum Suit : byte { Spade = 0, Heart = 1, Diamond = 2, Club = 3 }

    /// <summary>
    /// 一张具体的牌：牌型 + 花色 + 点数。
    /// 点数：1=A，2-10，11=J，12=Q，13=K。
    /// </summary>
    public readonly struct Card
    {
        public static readonly Card None = new(CardType.None, Suit.Spade, 0);

        public readonly CardType Type;
        public readonly Suit     Suit;
        public readonly byte     Rank;

        public Card(CardType type, Suit suit, byte rank) { Type = type; Suit = suit; Rank = rank; }

        public bool IsNone => Type == CardType.None;

        public string RankName
        {
            get
            {
                return Rank switch { 1 => "A", 11 => "J", 12 => "Q", 13 => "K", _ => Rank.ToString() };
            }
        }

        public string SuitSymbol
        {
            get
            {
                var s = Suit;
                return s switch
                {
                    Suit.Spade   => "♠",
                    Suit.Heart   => "♥",
                    Suit.Diamond => "♦",
                    Suit.Club    => "♣",
                    _            => "?",
                };
            }
        }

        public bool IsRed { get { var s = Suit; return s == Suit.Heart || s == Suit.Diamond; } }
    }

    public enum CardType : byte
    {
        Kill = 0, Dodge = 1,
        ZhuGeLianNu    = 6,   // 诸葛连弩：武器，装备后无限出杀
        GuoHeChaiQiao  = 7,   // 过河拆桥：弃掉目标一张牌
        LeBuSiShu      = 8,   // 乐不思蜀：延时锦囊，放入目标判定区，判定非♥则跳过出牌阶段
        BaGuaZhen      = 9,   // 八卦阵：防具，被杀时可判定，红色视为使用了闪
        ChiTu          = 10,  // 赤兔：进攻马，与其他角色计算距离 -1
        JueYing        = 11,  // 绝影：防御马，其他角色计算与你的距离 +1
        None = 0xFF,
    }

    public enum EquipSlot : byte { Weapon = 0, Armor = 1, OffHorse = 2, DefHorse = 3 }

    /// <summary>回合的六个主阶段</summary>
    public enum TurnPhase : byte
    {
        TurnStart = 0,  // 回合开始
        Judgment  = 1,  // 判定阶段（当前无延时牌，自动跳过）
        Draw      = 2,  // 摸牌阶段（During 时自动摸 DrawPerTurn 张）
        Play      = 3,  // 出牌阶段（During 需玩家操作）
        Discard   = 4,  // 弃牌阶段（手牌超限时 During 需玩家弃牌）
        TurnEnd   = 5,  // 回合结束
    }

    /// <summary>每个主阶段的三个子阶段</summary>
    public enum SubPhase : byte
    {
        Before = 0,  // 开始前
        During = 1,  // 时
        After  = 2,  // 后
    }

    public enum CardAction : byte
    {
        None        = 0,
        PlayCard    = 1,  // data[1] = 手牌下标；通过 CardRegistry 查卡牌类型
        Pass        = 2,  // 结束出牌阶段 / 受伤（WaitingForResponse 时）
        DiscardCard = 3,  // data[1] = 手牌下标；仅在 Discard.During 有效
        SelectHero  = 4,  // data[1] = HeroType 字节值；仅在 InHeroSelect 阶段有效
        UseSkill         = 5,  // data[1] = 0（保留）；出牌阶段发动主动技能
        ActivateArmor    = 6,  // data[1] = 0（保留）；响应阶段触发防具技能（八卦阵）
        UseAttackSkill   = 7,  // data[1] = 0（保留）；出杀后、目标响应前触发武将攻击技（铁骑）
        SelectTargetCard = 8,  // data[1] = 目标牌编码：0x00-0x0F=手牌, 0x10-0x13=装备槽, 0x20-0x2F=判定区
    }

    public class PlayerState
    {
        public int HP = 3;
        public const int MaxHP = 3;
        public readonly List<Card> Hand     = new();
        public readonly List<Card> Judgment = new();         // 判定区（暂无延时牌）
        public readonly Card[]     Equips   = new Card[4];   // 索引 = (int)EquipSlot

        public HeroType Hero = HeroType.None;   // 武将

        public PlayerState() { Array.Fill(Equips, Card.None); }

        public Card GetEquip(EquipSlot slot)          => Equips[(int)slot];
        public void SetEquip(EquipSlot slot, Card c)  => Equips[(int)slot] = c;
        public bool HasEquip(EquipSlot slot)           => !Equips[(int)slot].IsNone;
    }

    public class CardGameState
    {
        public const int DeckSize             = 46;
        public const int KillCount            = 20;
        public const int DodgeCount           = 8;
        public const int ZhuGeLianNuCount     = 4;
        public const int GuoHeChaiQiaoCount   = 4;
        public const int LeBuSiShuCount       = 4;
        public const int BaGuaZhenCount       = 2;
        public const int ChiTuCount           = 2;
        public const int JueYingCount         = 2;
        public const int InitialHand   = 4;
        public const int DrawPerTurn   = 2;

        public readonly Card[]        Deck    = new Card[DeckSize];
        public readonly PlayerState[] Players = { new(), new() };

        public int       DeckTop            = 0;
        public int       ActiveSlot         = 0;
        public TurnPhase TurnPhase          = TurnPhase.TurnStart;
        public SubPhase  SubPhase           = SubPhase.Before;

        // 叠加状态：「杀」已出，等待目标响应
        public bool WaitingForResponse = false;
        public int  PendingAttackSlot  = -1;   // 出「杀」的一方

        public bool HasPlayedKill = false;     // 本回合已出过「杀」
        public bool IsGameOver    = false;
        public int  WinnerSlot    = -1;
        public int  FrameNumber   = 0;

        // 选武将阶段
        public bool   InHeroSelect    = true;
        public bool[] HeroConfirmed   = new bool[2];

        // 主动技能状态
        public bool WuShengActive = false;  // 关羽武圣已发动，等待玩家选择红色牌当杀

        // 延时锦囊判定结果
        public bool SkipPlayPhase = false;  // 乐不思蜀判定非♥，本回合跳过出牌阶段

        // 铁骑状态（随杀的响应周期存在）
        public bool TieJiUsed    = false;   // 本次杀已触发铁骑（成功或失败），不可再触发
        public bool TieJiBlocked = false;   // 铁骑判定成功，目标本次不能使用闪

        // 过河拆桥：等待使用者选择目标角色的一张牌弃置
        public bool WaitingForGuoHe  = false;
        public int  GuoHeActorSlot   = -1;  // 出牌方（需要做选择）
        public int  GuoHeTargetSlot  = -1;  // 被选牌的一方

        public int DeckRemaining => Math.Max(0, DeckSize - DeckTop);

        /// <summary>弃牌阶段手牌上限 = 当前体力值（最低 1）</summary>
        public int HandLimit(int slot) => Math.Max(1, Players[slot].HP);
    }

    public class CardGameSimulation
    {
        public CardGameState State = new();

        readonly int[] _pidToSlot = { -1, -1 };
        int  _slotCount;
        uint _rngState;

        /// <summary>
        /// 卡牌事件通知（非确定性，仅供 UI 展示，不影响游戏逻辑）。
        /// 参数：(card, action) — action 为 "使用" / "弃牌" / "判定"。
        /// </summary>
        public event Action<Card, string> OnCardEvent;

        // ── Init ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 新局初始化：洗牌、发牌，然后自动推进到第一个交互阶段（Play.During）。
        /// </summary>
        public void Init(uint seed)
        {
            _rngState = seed == 0 ? 54321u : seed;

            // 构建带花色+点数的完整牌堆（花色顺序：♠♥♦♣）
            // 杀 A-5 × 4 花色 = 20；闪 6-7 × 4 = 8；
            // 连弩 Q × 4；过河拆桥 K × 4；乐不思蜀 2 × 4 = 4；八卦阵 ♠♣2 × 2
            // 赤兔（进攻马）♦♣5 × 2；绝影（防御马）♠♥9 × 2
            var deck = State.Deck;
            int idx = 0;
            Suit[] suits = { Suit.Spade, Suit.Heart, Suit.Diamond, Suit.Club };

            for (byte r = 1; r <= 5; r++)
                foreach (var s in suits) deck[idx++] = new Card(CardType.Kill, s, r);

            for (byte r = 6; r <= 7; r++)
                foreach (var s in suits) deck[idx++] = new Card(CardType.Dodge, s, r);

            foreach (var s in suits)  deck[idx++] = new Card(CardType.ZhuGeLianNu,    s, 12);
            foreach (var s in suits)  deck[idx++] = new Card(CardType.GuoHeChaiQiao,  s, 13);
            foreach (var s in suits)  deck[idx++] = new Card(CardType.LeBuSiShu,      s,  2);
            deck[idx++] = new Card(CardType.BaGuaZhen, Suit.Spade,   2);
            deck[idx++] = new Card(CardType.BaGuaZhen, Suit.Club,    2);
            deck[idx++] = new Card(CardType.ChiTu,     Suit.Diamond, 5);
            deck[idx++] = new Card(CardType.ChiTu,     Suit.Club,    5);
            deck[idx++] = new Card(CardType.JueYing,   Suit.Spade,   9);
            deck[idx++] = new Card(CardType.JueYing,   Suit.Heart,   9);

            // Fisher-Yates 确定性洗牌
            for (int i = CardGameState.DeckSize - 1; i > 0; i--)
            {
                int j = (int)(NextRng() % (uint)(i + 1));
                (deck[i], deck[j]) = (deck[j], deck[i]);
            }

            State.DeckTop = 0;

            // 玩家将在选武将阶段自行选择，此处仅重置
            for (int s = 0; s < 2; s++)
            {
                State.Players[s].Hero = HeroType.None;
                State.Players[s].HP   = PlayerState.MaxHP;
                State.Players[s].Hand.Clear();
                State.Players[s].Judgment.Clear();
                Array.Fill(State.Players[s].Equips, Card.None);
            }

            State.ActiveSlot          = 0;
            State.TurnPhase           = TurnPhase.TurnStart;
            State.SubPhase            = SubPhase.Before;
            State.WaitingForResponse  = false;
            State.PendingAttackSlot   = -1;
            State.HasPlayedKill       = false;
            State.IsGameOver          = false;
            State.WinnerSlot          = -1;
            State.FrameNumber         = 0;
            State.InHeroSelect        = true;
            State.HeroConfirmed[0]    = false;
            State.HeroConfirmed[1]    = false;
            State.WuShengActive       = false;
            State.SkipPlayPhase       = false;
            State.TieJiUsed           = false;
            State.TieJiBlocked        = false;
            State.WaitingForGuoHe     = false;
            State.GuoHeActorSlot      = -1;
            State.GuoHeTargetSlot     = -1;
            // AdvanceAutoPhases 在双方选完武将后由 ApplySelectHero 触发
        }

        // ── Slot mapping ───────────────────────────────────────────────────────

        public int PidToSlot(int pid)
        {
            for (int i = 0; i < _slotCount; i++)
                if (_pidToSlot[i] == pid) return i;
            if (_slotCount >= 2) return -1;
            _pidToSlot[_slotCount] = pid;
            return _slotCount++;
        }

        int GetSlotForPid(int pid)
        {
            for (int i = 0; i < _slotCount; i++)
                if (_pidToSlot[i] == pid) return i;
            return -1;
        }

        // ── Tick ───────────────────────────────────────────────────────────────

        public void Tick(FrameData frame)
        {
            State.FrameNumber = (int)frame.FrameNumber;
            if (State.IsGameOver) return;

            foreach (var inp in frame.Inputs)
            {
                if (inp.Data == null || inp.Data.Length < 2) continue;
                int slot = GetSlotForPid(inp.PlayerId);
                if (slot < 0) continue;
                ApplyAction(slot, (CardAction)inp.Data[0], inp.Data[1]);
                if (State.IsGameOver) return;
            }

            // 输入处理完毕后，自动推进非交互阶段
            AdvanceAutoPhases();
        }

        // ── Action dispatch ───────────────────────────────────────────────────

        void ApplyAction(int slot, CardAction action, int cardIdx)
        {
            // 过河拆桥等待选牌期间，只允许使用者执行 SelectTargetCard
            if (State.WaitingForGuoHe)
            {
                if (action == CardAction.SelectTargetCard)
                    ApplySelectTargetCard(slot, (byte)cardIdx);
                return;
            }

            switch (action)
            {
                case CardAction.PlayCard:        ApplyPlayCard(slot, cardIdx);    break;
                case CardAction.Pass:            ApplyPass(slot);                 break;
                case CardAction.DiscardCard:     ApplyDiscardCard(slot, cardIdx); break;
                case CardAction.SelectHero:      ApplySelectHero(slot, cardIdx);  break;
                case CardAction.UseSkill:        ApplyUseSkill(slot);             break;
                case CardAction.ActivateArmor:   ApplyActivateArmor(slot);        break;
                case CardAction.UseAttackSkill:  ApplyUseAttackSkill(slot);       break;
            }
        }

        void ApplySelectHero(int slot, int heroTypeValue)
        {
            if (!State.InHeroSelect)       return;
            if (State.HeroConfirmed[slot]) return;   // 已确认，忽略重复输入

            State.Players[slot].Hero  = (HeroType)heroTypeValue;
            State.HeroConfirmed[slot] = true;

            if (State.HeroConfirmed[0] && State.HeroConfirmed[1])
            {
                // 双方均已选定武将：发初始手牌，进入正式游戏
                for (int s = 0; s < 2; s++)
                    for (int i = 0; i < CardGameState.InitialHand; i++)
                        State.Players[s].Hand.Add(DrawCard());
                State.InHeroSelect = false;
                AdvanceAutoPhases();
            }
        }

        void ApplyUseSkill(int slot)
        {
            if (State.InHeroSelect) return;
            if (State.TurnPhase != TurnPhase.Play)   return;
            if (State.SubPhase  != SubPhase.During)  return;
            if (State.WaitingForResponse)             return;
            if (slot != State.ActiveSlot)             return;

            var def = HeroRegistry.Get(State.Players[slot].Hero);
            if (!def.CanActivate(State, slot)) return;
            def.Activate(State, slot);
        }

        void ApplyActivateArmor(int slot)
        {
            // 仅在等待响应（被「杀」攻击）时可触发
            if (!State.WaitingForResponse) return;
            int defender = 1 - State.PendingAttackSlot;
            if (slot != defender) return;
            // 必须装备八卦阵
            if (State.Players[slot].GetEquip(EquipSlot.Armor).Type != CardType.BaGuaZhen) return;

            // 翻开牌堆顶一张牌进行判定
            var drawn = DrawCard();
            OnCardEvent?.Invoke(drawn, "判定");
            if (drawn.IsRed)
            {
                // 判定为红色：视为使用了一张「闪」，攻击被抵消
                State.WaitingForResponse = false;
                State.PendingAttackSlot  = -1;
            }
            // 判定失败（非红色）：什么都不做，玩家仍需出闪或选择受伤
        }

        void ApplyUseAttackSkill(int slot)
        {
            // 出杀后、目标响应前才可触发；且必须是攻击方；每次杀只能触发一次
            if (!State.WaitingForResponse)             return;
            if (slot != State.PendingAttackSlot)       return;

            var def = HeroRegistry.Get(State.Players[slot].Hero);
            if (!def.CanActivateOnAttack(State, slot)) return;

            State.TieJiUsed = true;   // 本次杀不可再次触发

            if (def.AttackSkillNeedsJudge)
            {
                // 翻开牌堆顶一张牌进行判定
                var drawn = DrawCard();
                OnCardEvent?.Invoke(drawn, "判定");
                if (drawn.IsRed)
                    State.TieJiBlocked = true;   // 目标本次不能使用闪
            }
        }

        void ApplySelectTargetCard(int slot, byte arg)
        {
            if (!State.WaitingForGuoHe)          return;
            if (slot != State.GuoHeActorSlot)    return;

            int    targetSlot = State.GuoHeTargetSlot;
            var    target     = State.Players[targetSlot];
            Card   discarded  = Card.None;

            if (arg < 0x10)                      // 手牌
            {
                int idx = arg & 0x0F;
                if (idx >= target.Hand.Count) return;
                discarded = target.Hand[idx];
                target.Hand.RemoveAt(idx);
            }
            else if (arg < 0x20)                 // 装备槽
            {
                var equipSlot = (EquipSlot)(arg & 0x0F);
                discarded = target.GetEquip(equipSlot);
                if (discarded.IsNone) return;
                target.SetEquip(equipSlot, Card.None);
            }
            else                                 // 判定区
            {
                int idx = arg & 0x0F;
                if (idx >= target.Judgment.Count) return;
                discarded = target.Judgment[idx];
                target.Judgment.RemoveAt(idx);
            }

            OnCardEvent?.Invoke(discarded, "弃牌");
            State.WaitingForGuoHe = false;
            State.GuoHeActorSlot  = -1;
            State.GuoHeTargetSlot = -1;
        }

        void ApplyPlayCard(int slot, int cardIdx)
        {
            var hand = State.Players[slot].Hand;
            if (cardIdx >= hand.Count) return;

            var playedCard   = hand[cardIdx];
            var ctx          = new UseContext(State, slot);
            var def          = CardRegistry.Get(playedCard.Type);

            // 武圣：玩家已主动发动技能，消费激活状态，尝试将红色牌当杀使用
            bool wuShengUsed = State.WuShengActive;
            State.WuShengActive = false;
            if ((def == null || !def.CanUse(ctx)) && playedCard.IsRed && wuShengUsed)
            {
                var killDef = CardRegistry.Get(CardType.Kill);
                if (killDef != null && killDef.CanUse(ctx))
                    def = killDef;
            }

            if (def == null || !def.CanUse(ctx)) return;

            hand.RemoveAt(cardIdx);
            OnCardEvent?.Invoke(playedCard, "使用");

            // ── 使用牌时机 ─────────────────────────────────────────────────────
            HeroRegistry.Get(State.Players[slot].Hero).OnUseCard(State, slot, playedCard);

            int targetSlot = def.TargetRule == TargetType.Opponent ? 1 - slot : -1;

            // ── 指定目标 / 成为目标时机 ────────────────────────────────────────
            if (targetSlot >= 0)
            {
                HeroRegistry.Get(State.Players[slot].Hero)
                    .OnDesignateTarget(State, slot, targetSlot, playedCard);
                HeroRegistry.Get(State.Players[targetSlot].Hero)
                    .OnBeTarget(State, targetSlot, slot, playedCard);
            }

            var result = def.Apply(State, slot, targetSlot, playedCard);

            // 随机弃掉目标一张手牌（保留 DiscardOneFromTarget 路径备用）
            if (result.DiscardOneFromTarget && targetSlot >= 0)
            {
                var targetHand = State.Players[targetSlot].Hand;
                if (targetHand.Count > 0)
                {
                    int discardIdx = (int)(NextRng() % (uint)targetHand.Count);
                    var discarded  = targetHand[discardIdx];
                    targetHand.RemoveAt(discardIdx);
                    OnCardEvent?.Invoke(discarded, "弃牌");
                }
            }

            // 过河拆桥：等待使用者从目标手牌/装备/判定区选一张牌弃置
            if (result.NeedsTargetCardSelect && targetSlot >= 0)
            {
                var target   = State.Players[targetSlot];
                bool hasAny  = target.Hand.Count > 0
                            || System.Array.Exists(target.Equips, e => !e.IsNone)
                            || target.Judgment.Count > 0;
                if (hasAny)
                {
                    State.WaitingForGuoHe = true;
                    State.GuoHeActorSlot  = slot;
                    State.GuoHeTargetSlot = targetSlot;
                }
            }

            // 直接伤害（当前卡牌未使用，为未来扩展保留）
            if (result.Damage > 0 && targetSlot >= 0)
            {
                State.Players[targetSlot].HP -= result.Damage;
                if (State.Players[targetSlot].HP <= 0)
                {
                    State.IsGameOver = true;
                    State.WinnerSlot = slot;
                    return;
                }
            }

            // 立即结束回合（为未来"结束你的回合"类卡牌保留）
            if (result.EndTurn)
                State.SubPhase = SubPhase.After;
        }

        void ApplyPass(int slot)
        {
            State.WuShengActive = false;   // 任何 Pass 均取消武圣激活状态
            if (State.WaitingForResponse)
            {
                // 受伤：不出闪，承受「杀」的伤害
                int defender = 1 - State.PendingAttackSlot;
                if (slot != defender) return;

                State.Players[defender].HP--;
                if (State.Players[defender].HP <= 0)
                {
                    State.IsGameOver = true;
                    State.WinnerSlot = State.PendingAttackSlot;
                    return;
                }
                State.WaitingForResponse = false;
                State.PendingAttackSlot  = -1;
                State.TieJiUsed          = false;
                State.TieJiBlocked       = false;
                // Play.During 继续（攻击方可继续出牌）
            }
            else if (State.TurnPhase == TurnPhase.Play
                  && State.SubPhase  == SubPhase.During
                  && slot            == State.ActiveSlot)
            {
                // 主动结束出牌阶段
                State.SubPhase = SubPhase.After;
            }
            else if (State.TurnPhase == TurnPhase.Discard
                  && State.SubPhase  == SubPhase.During
                  && slot            == State.ActiveSlot)
            {
                // 弃牌阶段强制结束（Demo 允许跳过弃牌）
                State.SubPhase = SubPhase.After;
            }
        }

        void ApplyDiscardCard(int slot, int cardIdx)
        {
            if (State.TurnPhase != TurnPhase.Discard) return;
            if (State.SubPhase  != SubPhase.During)   return;
            if (slot            != State.ActiveSlot)  return;

            var hand = State.Players[slot].Hand;
            if (cardIdx >= hand.Count) return;
            var discardedCard = hand[cardIdx];
            hand.RemoveAt(cardIdx);
            OnCardEvent?.Invoke(discardedCard, "弃牌");
            // AdvanceAutoPhases 会在 Tick 末尾检查是否仍超限
        }

        // ── Phase advancement ─────────────────────────────────────────────────

        /// <summary>
        /// 持续推进，直到遇到交互阶段或 WaitingForResponse。
        /// 选武将阶段（InHeroSelect）期间不推进。
        /// </summary>
        void AdvanceAutoPhases()
        {
            if (State.InHeroSelect) return;
            const int MaxSteps = 64;
            for (int i = 0; i < MaxSteps && !State.IsGameOver && !State.WaitingForResponse; i++)
            {
                if (IsInteractivePhase()) break;
                AdvanceSubPhase();
            }
        }

        bool IsInteractivePhase()
        {
            if (State.TurnPhase == TurnPhase.Play && State.SubPhase == SubPhase.During)
                return !State.SkipPlayPhase;  // 乐不思蜀判定失败时自动跳过出牌阶段

            if (State.TurnPhase == TurnPhase.Discard && State.SubPhase == SubPhase.During)
                return State.Players[State.ActiveSlot].Hand.Count
                     > State.HandLimit(State.ActiveSlot);

            return false;
        }

        /// <summary>
        /// Before → During → After → 下一阶段 Before；
        /// TurnEnd.After → 换手，回到 TurnStart.Before。
        /// 每次推进后调用 OnEnterSubPhase 触发确定性副作用。
        /// </summary>
        void AdvanceSubPhase()
        {
            if (State.SubPhase < SubPhase.After)
            {
                State.SubPhase = (SubPhase)((int)State.SubPhase + 1);
            }
            else if (State.TurnPhase < TurnPhase.TurnEnd)
            {
                State.TurnPhase = (TurnPhase)((int)State.TurnPhase + 1);
                State.SubPhase  = SubPhase.Before;
            }
            else
            {
                // TurnEnd.After → 换手，新回合 TurnStart.Before
                State.ActiveSlot        = 1 - State.ActiveSlot;
                State.HasPlayedKill     = false;
                State.PendingAttackSlot = -1;
                State.SkipPlayPhase     = false;
                State.TieJiUsed         = false;
                State.TieJiBlocked      = false;
                State.WaitingForGuoHe   = false;
                State.GuoHeActorSlot    = -1;
                State.GuoHeTargetSlot   = -1;
                State.TurnPhase         = TurnPhase.TurnStart;
                State.SubPhase          = SubPhase.Before;
            }

            OnEnterSubPhase();
        }

        /// <summary>进入子阶段时的确定性副作用（摸牌、判定等）。</summary>
        void OnEnterSubPhase()
        {
            if (State.TurnPhase == TurnPhase.Judgment && State.SubPhase == SubPhase.During)
            {
                int slot         = State.ActiveSlot;
                var judgeZone    = State.Players[slot].Judgment;
                // 逆序遍历，安全删除
                for (int i = judgeZone.Count - 1; i >= 0; i--)
                {
                    var judgeCard = judgeZone[i];
                    judgeZone.RemoveAt(i);
                    if (judgeCard.Type == CardType.LeBuSiShu)
                    {
                        // 翻开牌堆顶一张牌进行判定（消耗进牌堆）
                        var drawn = DrawCard();
                        OnCardEvent?.Invoke(drawn, "判定");
                        // 结果非红桃 → 跳过本回合出牌阶段
                        if (drawn.Suit != Suit.Heart)
                            State.SkipPlayPhase = true;
                    }
                }
            }

            if (State.TurnPhase == TurnPhase.Draw && State.SubPhase == SubPhase.During)
            {
                int slot  = State.ActiveSlot;
                var hand  = State.Players[slot].Hand;
                int bonus = HeroRegistry.Get(State.Players[slot].Hero).GetDrawBonus(State, slot);
                int draw  = Math.Min(CardGameState.DrawPerTurn + bonus, State.DeckRemaining);
                for (int i = 0; i < draw; i++)
                    hand.Add(DrawCard());
            }
        }

        Card DrawCard()
        {
            var card = State.Deck[State.DeckTop % CardGameState.DeckSize];
            State.DeckTop = Math.Min(State.DeckTop + 1, CardGameState.DeckSize);
            return card;
        }

        uint NextRng()
        {
            _rngState ^= _rngState << 13;
            _rngState ^= _rngState >> 17;
            _rngState ^= _rngState << 5;
            return _rngState;
        }

        // ── Snapshot ───────────────────────────────────────────────────────────

        public byte[] Serialize()
        {
            var buf = new List<byte>(512);

            buf.Add((byte)State.TurnPhase);
            buf.Add((byte)State.SubPhase);
            buf.Add((byte)(State.WaitingForResponse ? 1 : 0));
            buf.Add((byte)(State.PendingAttackSlot + 1));  // +1: -1→0
            buf.Add((byte)(State.WinnerSlot + 1));
            buf.Add((byte)(State.HasPlayedKill ? 1 : 0));
            buf.Add((byte)(State.IsGameOver ? 1 : 0));
            buf.Add((byte)(State.InHeroSelect    ? 1 : 0));
            buf.Add((byte)(State.HeroConfirmed[0] ? 1 : 0));
            buf.Add((byte)(State.HeroConfirmed[1] ? 1 : 0));
            buf.Add((byte)(State.WuShengActive    ? 1 : 0));
            buf.Add((byte)(State.SkipPlayPhase    ? 1 : 0));
            buf.Add((byte)(State.TieJiUsed        ? 1 : 0));
            buf.Add((byte)(State.TieJiBlocked     ? 1 : 0));
            buf.Add((byte)(State.WaitingForGuoHe  ? 1 : 0));
            buf.Add((byte)(State.GuoHeActorSlot  + 1));  // +1: -1→0
            buf.Add((byte)(State.GuoHeTargetSlot + 1));  // +1: -1→0
            buf.Add((byte)State.ActiveSlot);
            WriteU16(buf, (ushort)State.DeckTop);
            WriteS32(buf, State.FrameNumber);
            WriteU32(buf, _rngState);

            for (int i = 0; i < CardGameState.DeckSize; i++)
            {
                buf.Add((byte)State.Deck[i].Type);
                buf.Add((byte)State.Deck[i].Suit);
                buf.Add(State.Deck[i].Rank);
            }

            buf.Add((byte)_slotCount);
            for (int i = 0; i < _slotCount; i++)
                WriteS32(buf, _pidToSlot[i]);

            for (int s = 0; s < 2; s++)
            {
                buf.Add((byte)State.Players[s].Hero);
                buf.Add((byte)State.Players[s].HP);
                buf.Add((byte)State.Players[s].Hand.Count);
                foreach (var c in State.Players[s].Hand)
                    { buf.Add((byte)c.Type); buf.Add((byte)c.Suit); buf.Add(c.Rank); }
                // 装备区（固定 4 × 3 字节：武器 + 防具 + 进攻马 + 防御马）
                for (int e = 0; e < 4; e++)
                    { buf.Add((byte)State.Players[s].Equips[e].Type);
                      buf.Add((byte)State.Players[s].Equips[e].Suit);
                      buf.Add(State.Players[s].Equips[e].Rank); }
                // 判定区
                buf.Add((byte)State.Players[s].Judgment.Count);
                foreach (var c in State.Players[s].Judgment)
                    { buf.Add((byte)c.Type); buf.Add((byte)c.Suit); buf.Add(c.Rank); }
            }

            return buf.ToArray();
        }

        public void Deserialize(byte[] d)
        {
            int o = 0;
            State.TurnPhase           = (TurnPhase)d[o++];
            State.SubPhase            = (SubPhase)d[o++];
            State.WaitingForResponse  = d[o++] == 1;
            State.PendingAttackSlot   = d[o++] - 1;
            State.WinnerSlot          = d[o++] - 1;
            State.HasPlayedKill       = d[o++] == 1;
            State.IsGameOver          = d[o++] == 1;
            State.InHeroSelect        = d[o++] == 1;
            State.HeroConfirmed[0]    = d[o++] == 1;
            State.HeroConfirmed[1]    = d[o++] == 1;
            State.WuShengActive       = d[o++] == 1;
            State.SkipPlayPhase       = d[o++] == 1;
            State.TieJiUsed           = d[o++] == 1;
            State.TieJiBlocked        = d[o++] == 1;
            State.WaitingForGuoHe     = d[o++] == 1;
            State.GuoHeActorSlot      = d[o++] - 1;
            State.GuoHeTargetSlot     = d[o++] - 1;
            State.ActiveSlot          = d[o++];
            State.DeckTop             = d[o] | (d[o + 1] << 8); o += 2;
            State.FrameNumber         = ReadS32(d, ref o);
            _rngState                 = ReadU32(d, ref o);

            for (int i = 0; i < CardGameState.DeckSize; i++)
            {
                var t = (CardType)d[o++]; var su = (Suit)d[o++]; var r = d[o++];
                State.Deck[i] = new Card(t, su, r);
            }

            _slotCount = d[o++];
            for (int i = 0; i < _slotCount; i++)
                _pidToSlot[i] = ReadS32(d, ref o);

            for (int s = 0; s < 2; s++)
            {
                State.Players[s].Hero = (HeroType)d[o++];
                State.Players[s].HP   = d[o++];
                int count = d[o++];
                State.Players[s].Hand.Clear();
                for (int i = 0; i < count; i++)
                {
                    var t = (CardType)d[o++]; var su = (Suit)d[o++]; var r = d[o++];
                    State.Players[s].Hand.Add(new Card(t, su, r));
                }
                // 装备区（固定 2 × 3 字节：武器 + 防具）
                for (int e = 0; e < 4; e++)
                {
                    var t = (CardType)d[o++]; var su = (Suit)d[o++]; var r = d[o++];
                    State.Players[s].Equips[e] = new Card(t, su, r);
                }
                // 判定区
                int jCount = d[o++];
                State.Players[s].Judgment.Clear();
                for (int i = 0; i < jCount; i++)
                {
                    var t = (CardType)d[o++]; var su = (Suit)d[o++]; var r = d[o++];
                    State.Players[s].Judgment.Add(new Card(t, su, r));
                }
            }
        }

        static void WriteU16(List<byte> b, ushort v) { b.Add((byte)(v & 0xFF)); b.Add((byte)(v >> 8)); }
        static void WriteS32(List<byte> b, int v)    { b.Add((byte)(v & 0xFF)); b.Add((byte)((v >> 8) & 0xFF)); b.Add((byte)((v >> 16) & 0xFF)); b.Add((byte)((v >> 24) & 0xFF)); }
        static void WriteU32(List<byte> b, uint v)   => WriteS32(b, (int)v);
        static int  ReadS32(byte[] d, ref int o)     { int v = d[o] | (d[o+1]<<8) | (d[o+2]<<16) | (d[o+3]<<24); o += 4; return v; }
        static uint ReadU32(byte[] d, ref int o)     => (uint)ReadS32(d, ref o);
    }
}
