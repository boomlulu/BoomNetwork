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
        public event Action<string>? OnLog;

        public float TimeoutMs { get; set; } = 10000;

        private bool _cancelled;

        public void Attempt(NetworkSession session, string host, int port,
            ReconnectState state, Action<ReconnectOutcome> onSuccess, Action<NetworkError> onFail)
        {
            _cancelled = false;

            OnLog?.Invoke($"[SnapshotReconnect] Attempt host={host}:{port} playerId={state.PlayerId}");

            // 全量重置：清空所有缓冲区和状态
            session.FullReset();

            void onConnected()
            {
                session.OnConnected -= onConnected;
                if (_cancelled) return;

                // 发送重连请求（只携带 playerId，lastFrame=0 告知服务器走快照路径）
                var data = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(data, state.PlayerId);

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

                        var hasSnapshot = snapshotData != null && snapshotData.Length > 0;
                        onSuccess(new ReconnectOutcome(serverFrame, isSnapshotRestore: hasSnapshot,
                            snapshotFrame: snapshotFrame,
                            snapshotData:  hasSnapshot ? snapshotData : null));
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
