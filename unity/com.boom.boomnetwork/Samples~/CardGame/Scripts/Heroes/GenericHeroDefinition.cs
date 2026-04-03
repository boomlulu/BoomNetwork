namespace BoomNetwork.Samples.CardGame
{
    public sealed class GenericHeroDefinition : HeroDefinition
    {
        public override HeroType HeroType  => HeroType.None;
        public override string   Name      => "无名将";
        public override string   Title     => "";
        public override string   SkillName => "";
        public override string   SkillDesc => "";
    }
}
