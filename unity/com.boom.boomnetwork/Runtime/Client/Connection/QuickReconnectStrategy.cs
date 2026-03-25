using System;
using System.Buffers.Binary;
using BoomNetwork.Core;
using BoomNetwork.Core.FrameSync;
using BoomNetwork.Client.Session;

namespace BoomNetwork.Client.Connection
{
    /// <summary>
    /// 快速重连策略
    ///
    /// 流程: 重建 TCP → 发送 Reconnect(playerId, lastFrame) → 服务器重发缺失帧 → 重发未确认消息
    /// 适用: 短时间断线（< 几秒），服务器帧缓冲区还有数据
    /// </summary>
    public class QuickReconnectStrategy : IReconnectStrategy
    {
        public string Name => "QuickReconnect";

        public float TimeoutMs { get; set; } = 5000;

        private bool _cancelled;

        public void Attempt(NetworkSession session, string host, int port,
            ReconnectContext context, Action onSuccess, Action<NetworkError> onFail)
        {
            _cancelled = false;

            session.LightReset();

            void onConnected()
            {
                session.OnConnected -= onConnected;
                if (_cancelled) return;

                // 发送 [playerId:4][lastFrame:4]
                var data = new byte[8];
                BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0), context.PlayerId);
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), context.LastFrameNumber);

                session.SendAsync(FrameSyncCmd.Reconnect, data, TimeoutMs,
                    onResponse: msg =>
                    {
                        if (_cancelled) return;

                        var (result, roomId, serverFrame, snapshotFrame, snapshotData) =
                            SnapshotCodec.DecodeReconnectRsp(msg.DataSpan);

                        if (result == ReconnectResult.BufferStale)
                        {
                            // 帧缓冲区过期 → 快速重连失败，由 Composite 降级到快照重连
                            onFail(new NetworkError(ErrorCode.ReconnectFailed, "QuickReconnect: buffer stale, need snapshot"));
                            return;
                        }
                        if (result != ReconnectResult.Success)
                        {
                            onFail(new NetworkError(ErrorCode.ReconnectFailed, "QuickReconnect: server rejected"));
                            return;
                        }

                        context.ServerFrameNumber = serverFrame;
                        context.IsSnapshotRestore = false;

                        // 重连成功：清空已发送缓冲区
                        // 注意：不能调用 ResendUnacked()！
                        // sent buffer 中的 SessionBind/CreateRoom/JoinRoom 等消息
                        // 重发会导致 server 创建重复 player/room（致命 bug）
                        // 真正需要重发的 FrameInput/EntityState 使用 Send（无 Seq），不在 buffer 中
                        session.ClearSentBuffer();

                        onSuccess();
                    },
                    onTimeout: err =>
                    {
                        if (_cancelled) return;
                        onFail(new NetworkError(ErrorCode.ReconnectFailed, $"QuickReconnect: {err.Message}"));
                    });
            }

            session.OnConnected += onConnected;
            session.Reconnect();
        }

        public void Cancel()
        {
            _cancelled = true;
        }
    }
}
