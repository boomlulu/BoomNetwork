namespace BoomNetwork.Samples.CardGame
{
    /// <summary>
    /// 周瑜·英姿：摸牌阶段多摸一张牌。
    /// </summary>
    public sealed class ZhouYuDefinition : HeroDefinition
    {
        public override HeroType HeroType  => HeroType.ZhouYu;
        public override string   Name      => "周瑜";
        public override string   Title     => "都督";
        public override string   SkillName => "英姿";
        public override string   SkillDesc => "摸牌阶段，多摸一张牌。";

        public override int GetDrawBonus(CardGameState state, int slot) => 1;
    }
}
