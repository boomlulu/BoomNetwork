using System;
using BoomNetwork.Core;
using BoomNetwork.Client.Session;

namespace BoomNetwork.Client.Connection
{
    /// <summary>
    /// 重连策略接口
    /// </summary>
    public interface IReconnectStrategy
    {
        /// <summary>
        /// 策略名称（日志用）
        /// </summary>
        string Name { get; }

        /// <summary>
        /// 尝试重连。
        ///
        /// <paramref name="state"/> 由 ConnectionManager 持有并持续维护：
        ///   - <see cref="ReconnectState.LastFrameNumber"/> 在重连期间每收到一帧就被 CM 同步更新。
        ///   - 策略每次发送 ReconnectReq 时应直接读取 <paramref name="state"/>，不要缓存本地副本。
        ///
        /// 成功时通过 <paramref name="onSuccess"/>(outcome) 传回不可变的 <see cref="ReconnectOutcome"/>；
        /// 失败时调用 <paramref name="onFail"/>。
        /// </summary>
        void Attempt(NetworkSession session, string host, int port,
            ReconnectState state,
            Action<ReconnectOutcome> onSuccess,
            Action<NetworkError> onFail);

        /// <summary>
        /// 取消正在进行的重连
        /// </summary>
        void Cancel();
    }

    /// <summary>
    /// 重连活状态 — 由 ConnectionManager 持有并跨 Attempt 维护。
    ///
    /// <see cref="LastFrameNumber"/> 在重连期间随收帧持续更新，
    /// 策略每次调用 Attempt 时读取到的都是当前最新帧号，杜绝跨 Attempt 帧号过期。
    /// </summary>
    public class ReconnectState
    {
        /// <summary>玩家 ID（断线时捕获，整个重连过程不变）</summary>
        public int PlayerId { get; set; }

        /// <summary>
        /// 客户端当前帧号（由 CM 每帧同步，非断线时刻快照）。
        /// 策略用此值构造 ReconnectReq.lastFrame，告知服务器从哪一帧开始补帧。
        /// </summary>
        public uint LastFrameNumber { get; set; }
    }

    /// <summary>
    /// 重连结果 — 策略成功时通过 onSuccess(outcome) 回传，纯输出，不可变。
    /// </summary>
    public readonly struct ReconnectOutcome
    {
        /// <summary>重连成功时服务器的当前帧号</summary>
        public uint ServerFrameNumber { get; }

        /// <summary>快照对应的帧号（快照重连时有效）</summary>
        public uint SnapshotFrame { get; }

        /// <summary>服务器返回的快照数据（快照重连时有效，否则 null）</summary>
        public byte[] SnapshotData { get; }

        /// <summary>true = 需要加载快照恢复状态；false = 快速重连，直接补帧</summary>
        public bool IsSnapshotRestore { get; }

        public ReconnectOutcome(uint serverFrameNumber, bool isSnapshotRestore,
            uint snapshotFrame = 0, byte[] snapshotData = null)
        {
            ServerFrameNumber = serverFrameNumber;
            IsSnapshotRestore = isSnapshotRestore;
            SnapshotFrame     = snapshotFrame;
            SnapshotData      = snapshotData;
        }
    }
}
