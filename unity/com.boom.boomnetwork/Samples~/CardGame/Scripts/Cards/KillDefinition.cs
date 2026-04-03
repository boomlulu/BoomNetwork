namespace BoomNetwork.Samples.CardGame
{
    /// <summary>
    /// 杀：出牌阶段指定一名对手，对手须以「闪」响应，否则受 1 点伤害。
    /// 每回合限用一次（装备诸葛连弩时无限制）。
    /// </summary>
    public sealed class KillDefinition : CardDefinition
    {
        public override CardType   CardType    => CardType.Kill;
        public override string     Name        => "杀";
        public override string     Description =>
            "出牌阶段，指定一名其他角色为目标。目标须以「闪」响应，否则受到 1 点伤害。每回合限用一次。";
        public override UseTiming  Timing      => UseTiming.ActiveTurn;
        public override TargetType TargetRule  => TargetType.Opponent;

        public override bool CanUse(UseContext ctx)
            => ctx.TurnPhase          == TurnPhase.Play
            && ctx.SubPhase           == SubPhase.During
            && !ctx.WaitingForResponse
            && ctx.ActorSlot          == ctx.ActiveSlot
            && (!ctx.HasPlayedKill || ctx.ActorHasZhuGeLianNu)
            && ctx.DistanceToOpponent <= 1;

        public override ApplyResult Apply(CardGameState state, int actorSlot, int targetSlot, Card playedCard)
        {
            // 设置等待响应标志；伤害由 Pass（不出闪）时结算
            state.HasPlayedKill      = true;
            state.PendingAttackSlot  = actorSlot;
            state.WaitingForResponse = true;
            return ApplyResult.None;   // Play.During 暂停，等待目标响应
        }
    }
}
