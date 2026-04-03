namespace BoomNetwork.Samples.CardGame
{
    /// <summary>
    /// 过河拆桥：出牌阶段，指定一名其他角色，从其手牌/装备区/判定区中选择一张牌弃置。
    /// Apply 返回 SelectFromTarget，由 Simulation 设置等待状态，等待使用者指定目标牌。
    /// </summary>
    public sealed class GuoHeChaiQiaoDefinition : CardDefinition
    {
        public override CardType   CardType    => CardType.GuoHeChaiQiao;
        public override string     Name        => "过河拆桥";
        public override string     Description =>
            "出牌阶段，指定一名其他角色，从其手牌、装备区或判定区中选择一张牌弃置。";
        public override UseTiming  Timing      => UseTiming.ActiveTurn;
        public override TargetType TargetRule  => TargetType.Opponent;

        public override bool CanUse(UseContext ctx)
            => ctx.TurnPhase == TurnPhase.Play
            && ctx.SubPhase  == SubPhase.During
            && !ctx.WaitingForResponse
            && ctx.ActorSlot == ctx.ActiveSlot;

        public override ApplyResult Apply(CardGameState state, int actorSlot, int targetSlot, Card playedCard)
            => ApplyResult.SelectFromTarget;
    }
}
