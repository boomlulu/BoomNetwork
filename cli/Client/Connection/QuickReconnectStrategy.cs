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
    /// 流程: 重建 TCP → 发送 Reconnect(playerId) → 服务器确认 → 重发未确认消息
    /// 适用: 短时间断线（< 几秒），服务器还保留着玩家状态
    /// </summary>
    public class QuickReconnectStrategy : IReconnectStrategy
    {
        public string Name => "QuickReconnect";

        public float TimeoutMs { get; set; } = 5000;

        private bool _cancelled;

        public void Attempt(NetworkSession session, string host, int port,
            ReconnectContext context, Action onSuccess, Action<string> onFail)
        {
            _cancelled = false;

            // 轻量重置：保留已发送缓冲区
            session.LightReset();

            // 监听连接成功
            void onConnected()
            {
                session.OnConnected -= onConnected;
                if (_cancelled) return;

                // 发送重连请求
                var data = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(data, context.PlayerId);

                session.SendAsync(FrameSyncCmd.Reconnect, data, TimeoutMs,
                    onResponse: msg =>
                    {
                        if (_cancelled) return;

                        // 服务器返回当前帧号
                        if (msg.DataLength >= 4)
                        {
                            context.ServerFrameNumber = BinaryPrimitives.ReadUInt32LittleEndian(msg.DataSpan);
                        }
                        context.IsSnapshotRestore = false;

                        // 重发未确认的消息
                        int resent = session.ResendUnacked();

                        onSuccess();
                    },
                    onTimeout: err =>
                    {
                        if (_cancelled) return;
                        onFail($"QuickReconnect bind timeout: {err}");
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
