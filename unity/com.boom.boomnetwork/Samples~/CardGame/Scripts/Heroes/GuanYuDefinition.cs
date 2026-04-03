namespace BoomNetwork.Samples.CardGame
{
    /// <summary>
    /// 关羽·武圣：你能将一张红色牌当「杀」使用或打出。
    /// 实际合法性校验和效果代理由 CardGameSimulation 处理。
    /// </summary>
    public sealed class GuanYuDefinition : HeroDefinition
    {
        public override HeroType    HeroType    => HeroType.GuanYu;
        public override string      Name        => "关羽";
        public override string      Title       => "武圣";
        public override string      SkillName   => "武圣";
        public override string      SkillDesc   => "出牌阶段，将一张红色牌当「杀」使用或打出。";
        public override SkillTiming SkillTiming => SkillTiming.ActiveTurn;

        /// <summary>手牌中有红色牌，且技能尚未激活。</summary>
        public override bool CanActivate(CardGameState state, int slot)
        {
            if (state.WuShengActive) return false;
            foreach (var c in state.Players[slot].Hand)
                if (c.IsRed) return true;
            return false;
        }

        public override void Activate(CardGameState state, int slot)
            => state.WuShengActive = true;

        public override bool IsPendingActivation(CardGameState state, int slot)
            => state.WuShengActive;
    }
}
