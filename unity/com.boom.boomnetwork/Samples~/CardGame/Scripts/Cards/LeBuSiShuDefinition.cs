namespace BoomNetwork.Samples.CardGame
{
    /// <summary>
    /// 乐不思蜀：出牌阶段，指定一名其他角色，将本牌置入其判定区。
    /// 该角色判定阶段开始时进行判定，结果非红桃（♥）则跳过其本回合出牌阶段。
    /// 判定由 CardGameSimulation.OnEnterSubPhase 在 Judgment.During 时执行。
    /// </summary>
    public sealed class LeBuSiShuDefinition : CardDefinition
    {
        public override CardType   CardType    => CardType.LeBuSiShu;
        public override string     Name        => "乐不思蜀";
        public override string     Description =>
            "出牌阶段，指定一名其他角色，将此牌置入其判定区。" +
            "其判定阶段开始时进行判定，结果非♥则跳过该角色本回合的出牌阶段。";
        public override UseTiming  Timing      => UseTiming.ActiveTurn;
        public override TargetType TargetRule  => TargetType.Opponent;

        public override bool CanUse(UseContext ctx)
            => ctx.TurnPhase == TurnPhase.Play
            && ctx.SubPhase  == SubPhase.During
            && !ctx.WaitingForResponse
            && ctx.ActorSlot == ctx.ActiveSlot;

        public override ApplyResult Apply(CardGameState state, int actorSlot, int targetSlot, Card playedCard)
        {
            state.Players[targetSlot].Judgment.Add(playedCard);
            return ApplyResult.None;
        }
    }
}
