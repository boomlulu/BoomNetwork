namespace BoomNetwork.Samples.CardGame
{
    /// <summary>
    /// 诸葛连弩：武器装备。装备后出牌阶段可无限次使用「杀」（解除每回合一次限制）。
    /// </summary>
    public sealed class ZhuGeLianNuDefinition : EquipmentDefinition
    {
        public override CardType   CardType    => CardType.ZhuGeLianNu;
        public override string     Name        => "连弩";
        public override string     Description => "武器。装备后出牌阶段使用「杀」无次数限制。";
        public override EquipSlot  Slot        => EquipSlot.Weapon;
    }
}
