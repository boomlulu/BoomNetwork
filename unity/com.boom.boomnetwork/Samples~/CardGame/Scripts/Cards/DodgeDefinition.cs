namespace BoomNetwork.Samples.CardGame
{
    /// <summary>
    /// 闪：响应阶段，当自己是「杀」的目标时打出，抵消伤害并结束该轮响应。
    /// 铁骑判定成功时不能使用。
    /// </summary>
    public sealed class DodgeDefinition : CardDefinition
    {
        public override CardType   CardType    => CardType.Dodge;
        public override string     Name        => "闪";
        public override string     Description =>
            "响应阶段，当自己成为「杀」的目标时使用，完全抵消该「杀」的伤害效果，回合随即结束。";
        public override UseTiming  Timing      => UseTiming.Response;
        public override TargetType TargetRule  => TargetType.None;

        public override bool CanUse(UseContext ctx)
            => ctx.WaitingForResponse
            && ctx.PendingAttackSlot >= 0
            && ctx.ActorSlot         != ctx.PendingAttackSlot  // 只有被攻击方可用
            && !ctx.TieJiBlocked;                              // 铁骑封锁时不能出闪

        public override ApplyResult Apply(CardGameState state, int actorSlot, int targetSlot, Card playedCard)
        {
            // 闪成功：清除等待响应标志，攻击方 Play.During 继续
            state.WaitingForResponse = false;
            state.PendingAttackSlot  = -1;
            state.TieJiUsed          = false;
            state.TieJiBlocked       = false;
            return ApplyResult.None;
        }
    }
}
