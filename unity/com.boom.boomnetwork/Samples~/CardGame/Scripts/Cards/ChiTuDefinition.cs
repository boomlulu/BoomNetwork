namespace BoomNetwork.Samples.CardGame
{
    /// <summary>
    /// 赤兔马：进攻马。装备后你与其他角色计算距离 -1（最低为 1）。
    /// 效果体现在 UseContext.DistanceToOpponent 计算时扣减。
    /// </summary>
    public sealed class ChiTuDefinition : EquipmentDefinition
    {
        public override CardType  CardType    => CardType.ChiTu;
        public override string    Name        => "赤兔";
        public override string    Description => "进攻马：你与其他角色计算距离 -1。";
        public override EquipSlot Slot        => EquipSlot.OffHorse;
    }
}
