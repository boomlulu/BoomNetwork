namespace BoomNetwork.Core.Prediction
{
    /// <summary>
    /// 游戏层实现此接口，提供确定性模拟能力。
    ///
    /// 要求:
    ///   1. Simulate 必须确定性（相同输入 → 相同输出）
    ///   2. SaveState/LoadState 必须能完整保存/恢复游戏状态
    ///   3. StateHash 用于跨客户端一致性校验
    /// </summary>
    public interface ISimulation
    {
        /// <summary>
        /// 执行一帧游戏逻辑
        /// </summary>
        /// <param name="inputs">本帧所有玩家的输入</param>
        /// <param name="inputCount">有效输入数量（inputs 数组可能更大，只处理前 inputCount 个）</param>
        void Simulate(FrameInput[] inputs, int inputCount);

        /// <summary>
        /// 保存当前游戏状态快照
        /// </summary>
        byte[] SaveState();

        /// <summary>
        /// 从快照恢复游戏状态
        /// </summary>
        void LoadState(byte[] snapshot);

        /// <summary>
        /// 计算当前状态哈希（用于一致性校验，可选）
        /// 返回 0 表示不支持
        /// </summary>
        uint StateHash() => 0;
    }

    /// <summary>
    /// 单个玩家的帧输入
    /// </summary>
    public struct FrameInput
    {
        public int PlayerId;
        public byte[] Data;

        public FrameInput(int playerId, byte[] data)
        {
            PlayerId = playerId;
            Data = data;
        }
    }
}
