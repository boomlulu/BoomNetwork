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
        /// 尝试重连
        /// </summary>
        /// <param name="session">网络会话</param>
        /// <param name="host">服务器地址</param>
        /// <param name="port">服务器端口</param>
        /// <param name="context">重连上下文（携带 playerId、帧号等信息）</param>
        /// <param name="onSuccess">成功回调</param>
        /// <param name="onFail">失败回调（reason）</param>
        void Attempt(NetworkSession session, string host, int port,
            ReconnectContext context, Action onSuccess, Action<NetworkError> onFail);

        /// <summary>
        /// 取消正在进行的重连
        /// </summary>
        void Cancel();
    }

    /// <summary>
    /// 重连上下文 — 在策略间传递状态
    /// </summary>
    public class ReconnectContext
    {
        public int PlayerId { get; set; }
        public uint LastFrameNumber { get; set; }

        /// <summary>
        /// 重连成功后服务器返回的帧号（由策略填写）
        /// </summary>
        public uint ServerFrameNumber { get; set; }

        /// <summary>
        /// 重连成功后服务器返回的快照数据（超时重连时由策略填写）
        /// </summary>
        public byte[]? SnapshotData { get; set; }

        /// <summary>
        /// 是否是快照恢复（超时重连）
        /// </summary>
        public bool IsSnapshotRestore { get; set; }
    }
}
