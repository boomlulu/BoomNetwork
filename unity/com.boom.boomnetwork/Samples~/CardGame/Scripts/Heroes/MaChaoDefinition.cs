namespace BoomNetwork.Samples.CardGame
{
    /// <summary>
    /// 马超·铁骑：当你使用「杀」指定目标后、目标响应前，
    /// 你可以触发铁骑判定：翻开牌堆顶一张牌，若结果为红色，
    /// 目标不能使用「闪」响应此「杀」。每次杀只能触发一次。
    /// 判定由 CardGameSimulation.ApplyUseAttackSkill 执行。
    /// </summary>
    public sealed class MaChaoDefinition : HeroDefinition
    {
        public override HeroType    HeroType    => HeroType.MaChao;
        public override string      Name        => "马超";
        public override string      Title       => "锦马超";
        public override string      SkillName   => "铁骑";
        public override string      SkillDesc   =>
            "你使用「杀」指定目标后，可进行判定：结果为红色，目标不能使用「闪」响应此「杀」。";
        public override SkillTiming SkillTiming => SkillTiming.Passive;

        public override bool CanActivateOnAttack(CardGameState state, int slot)
            => state.WaitingForResponse
            && state.PendingAttackSlot == slot
            && !state.TieJiUsed;

        public override bool AttackSkillNeedsJudge => true;
    }
}
