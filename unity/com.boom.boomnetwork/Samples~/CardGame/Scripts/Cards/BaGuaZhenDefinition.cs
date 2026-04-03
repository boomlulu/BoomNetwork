namespace BoomNetwork.Samples.CardGame
{
    /// <summary>
    /// 八卦阵：防具装备。当你需要使用「闪」时，可触发判定，
    /// 判定结果为红色（♥/♦）则视为使用了一张「闪」。
    /// 判定失败不消耗装备，玩家仍需出闪或承受伤害。
    /// 触发逻辑由 CardGameSimulation.ApplyActivateArmor 执行。
    /// </summary>
    public sealed class BaGuaZhenDefinition : EquipmentDefinition
    {
        public override CardType   CardType    => CardType.BaGuaZhen;
        public override string     Name        => "八卦阵";
        public override string     Description =>
            "防具。当你需要使用「闪」时，可触发判定：结果为红色（♥/♦）视为使用了一张「闪」。";
        public override EquipSlot  Slot        => EquipSlot.Armor;
    }
}
