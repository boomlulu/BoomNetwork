namespace BoomNetwork.Samples.CardGame
{
    /// <summary>
    /// 绝影马：防御马。装备后其他角色计算与你的距离 +1。
    /// 效果体现在 UseContext.DistanceToOpponent 计算时叠加。
    /// </summary>
    public sealed class JueYingDefinition : EquipmentDefinition
    {
        public override CardType  CardType    => CardType.JueYing;
        public override string    Name        => "绝影";
        public override string    Description => "防御马：其他角色计算与你的距离 +1。";
        public override EquipSlot Slot        => EquipSlot.DefHorse;
    }
}
