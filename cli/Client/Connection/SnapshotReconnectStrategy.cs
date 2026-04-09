using System;
using System.Buffers.Binary;
using BoomNetwork.Core;
using BoomNetwork.Core.FrameSync;
using BoomNetwork.Client.Session;

namespace BoomNetwork.Client.Connection
{
    /// <summary>
    /// 快照重连策略（超时重连）
    ///
    /// 流程: 重建 TCP → 全量重置 → 发送 Reconnect(playerId) → 请求快照 → 加载恢复
    /// 适用: 长时间断线，服务器消息缓冲区已清空
    /// </summary>
    public class SnapshotReconnectStrategy : IReconnectStrategy
    {
        public string Name => "SnapshotReconnect";

        public float TimeoutMs { get; set; } = 10000;

        private bool _cancelled;

        public void Attempt(NetworkSession session, string host, int port,
            ReconnectContext context, Action onSuccess, Action<NetworkError> onFail)
        {
            _cancelled = false;

            // 全量重置：清空所有缓冲区和状态
            session.FullReset();

            void onConnected()
            {
                session.OnConnected -= onConnected;
                if (_cancelled) return;

                // 发送重连请求（和快速重连相同的协议，服务器根据上下文决定是否返回快照）
                var data = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(data, context.PlayerId);

                session.SendAsync(FrameSyncCmd.Reconnect, data, TimeoutMs,
                    onResponse: msg =>
                    {
                        if (_cancelled) return;

                        var (result, roomId, serverFrame, snapshotFrame, _, snapshotData) =
                            SnapshotCodec.DecodeReconnectRsp(msg.DataSpan);

                        if (result != ReconnectResult.Success)
                        {
                            onFail(new NetworkError(ErrorCode.ReconnectFailed, "SnapshotReconnect: server rejected"));
                            return;
                        }

                        context.ServerFrameNumber = serverFrame;
                        context.SnapshotFrame = snapshotFrame;

                        if (snapshotData != null && snapshotData.Length > 0)
                        {
                            context.SnapshotData = snapshotData;
                            context.IsSnapshotRestore = true;
                        }
                        else
                        {
                            context.IsSnapshotRestore = false;
                        }

                        onSuccess();
                    },
                    onTimeout: err =>
                    {
                        if (_cancelled) return;
                        onFail(new NetworkError(ErrorCode.ReconnectFailed, $"SnapshotReconnect: {err.Message}"));
                    });
            }

            session.OnConnected += onConnected;
            session.Connect(host, port);
        }

        public void Cancel()
        {
            _cancelled = true;
        }
    }
}
