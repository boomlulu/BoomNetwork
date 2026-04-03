namespace BoomNetwork.Samples.CardGame
{
    /// <summary>
    /// 装备牌基类。出牌阶段打出，直接进入装备区对应栏位（无目标，无游戏效果）。
    /// 同类型装备覆盖旧装备（旧装备移除，本 Demo 不计入弃牌堆）。
    /// </summary>
    public abstract class EquipmentDefinition : CardDefinition
    {
        /// <summary>对应的装备栏位</summary>
        public abstract EquipSlot Slot { get; }

        public override UseTiming  Timing     => UseTiming.ActiveTurn;
        public override TargetType TargetRule => TargetType.None;

        public override bool CanUse(UseContext ctx)
            => ctx.TurnPhase == TurnPhase.Play
            && ctx.SubPhase  == SubPhase.During
            && !ctx.WaitingForResponse
            && ctx.ActorSlot == ctx.ActiveSlot;

        public override ApplyResult Apply(CardGameState state, int actorSlot, int targetSlot, Card playedCard)
        {
            state.Players[actorSlot].SetEquip(Slot, playedCard);
            return ApplyResult.None;
        }
    }
}
