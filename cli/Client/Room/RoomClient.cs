using System;
using System.Buffers.Binary;
using BoomNetwork.Core;
using BoomNetwork.Core.FrameSync;
using BoomNetwork.Client.Session;

namespace BoomNetwork.Client.Room
{
    /// <summary>
    /// 房间管理客户端
    ///
    /// 职责: 房间的创建/加入/离开/列表查询
    /// 不管: 帧同步、连接管理
    /// </summary>
    public class RoomClient
    {
        private readonly NetworkSession _session;

        // === 事件 ===

        /// <summary>有玩家加入当前房间</summary>
        public event Action<int>? OnPlayerJoined;

        /// <summary>有玩家离开当前房间</summary>
        public event Action<int>? OnPlayerLeft;

        /// <summary>错误</summary>
        public event Action<NetworkError>? OnError;

        // === 状态 ===

        public int CurrentRoomId { get; private set; }
        public int MyPlayerId { get; private set; }

        public RoomClient(NetworkSession session)
        {
            _session = session;
            _session.OnMessage += HandleMessage;
        }

        /// <summary>
        /// 获取房间列表
        /// </summary>
        public void GetRooms(Action<RoomInfo[]> onResult)
        {
            _session.SendExtAsync(FrameSyncExtCmd.GetRooms, null, 5000,
                onResponse: msg =>
                {
                    var rooms = RoomCodec.DecodeRoomList(msg.DataSpan);
                    onResult?.Invoke(rooms);
                },
                onTimeout: err =>
                {
                    OnError?.Invoke(err);
                    onResult?.Invoke(Array.Empty<RoomInfo>());
                });
        }

        /// <summary>
        /// 创建房间
        /// </summary>
        public void CreateRoom(int maxPlayers, string? matchKey = null, Action<int>? onCreated = null)
        {
            var data = RoomCodec.EncodeCreateRoom(maxPlayers, matchKey);
            _session.SendExtAsync(FrameSyncExtCmd.CreateRoom, data, 5000,
                onResponse: msg =>
                {
                    int roomId = RoomCodec.DecodeCreateRoomRsp(msg.DataSpan);
                    onCreated?.Invoke(roomId);
                },
                onTimeout: err =>
                {
                    OnError?.Invoke(err);
                });
        }

        /// <summary>
        /// 加入房间
        /// </summary>
        /// <param name="onJoined">回调: (playerId, roomId, existingPlayerIds)</param>
        public void JoinRoom(int roomId, Action<int, int, int[]>? onJoined = null)
        {
            var data = RoomCodec.EncodeJoinRoom(roomId);
            _session.SendExtAsync(FrameSyncExtCmd.JoinRoom, data, 5000,
                onResponse: msg =>
                {
                    var (playerId, rspRoomId, existingPlayers) = RoomCodec.DecodeJoinRoomRsp(msg.DataSpan);
                    if (playerId == 0)
                    {
                        OnError?.Invoke(new NetworkError(JoinRoomErrorCode(rspRoomId), $"Join room {roomId} failed ({JoinRoomErrorName(rspRoomId)})"));
                        return;
                    }
                    MyPlayerId = playerId;
                    CurrentRoomId = rspRoomId;
                    onJoined?.Invoke(playerId, rspRoomId, existingPlayers);
                },
                onTimeout: err =>
                {
                    OnError?.Invoke(err);
                });
        }

        /// <summary>
        /// 匹配房间（有空位加入，否则创建新房间）
        /// </summary>
        /// <param name="matchKey">匹配 key，相同 key 才能匹配到一起（避免不同 demo 串房）</param>
        /// <param name="onJoined">回调: (playerId, roomId, existingPlayerIds)</param>
        public void MatchRoom(int maxPlayers, string? matchKey = null, Action<int, int, int[]>? onJoined = null)
        {
            var data = RoomCodec.EncodeMatchRoom(maxPlayers, matchKey);
            _session.SendExtAsync(FrameSyncExtCmd.MatchRoom, data, 5000,
                onResponse: msg =>
                {
                    var (playerId, rspRoomId, existingPlayers) = RoomCodec.DecodeJoinRoomRsp(msg.DataSpan);
                    if (playerId == 0)
                    {
                        OnError?.Invoke(new NetworkError(JoinRoomErrorCode(rspRoomId), $"MatchRoom failed ({JoinRoomErrorName(rspRoomId)})"));
                        return;
                    }
                    MyPlayerId = playerId;
                    CurrentRoomId = rspRoomId;
                    onJoined?.Invoke(playerId, rspRoomId, existingPlayers);
                },
                onTimeout: err =>
                {
                    OnError?.Invoke(err);
                });
        }

        /// <summary>
        /// 离开房间
        /// </summary>
        public void LeaveRoom(Action? onLeft = null)
        {
            _session.SendExtAsync(FrameSyncExtCmd.LeaveRoom, null, 5000,
                onResponse: _ =>
                {
                    CurrentRoomId = 0;
                    MyPlayerId = 0;
                    onLeft?.Invoke();
                },
                onTimeout: err =>
                {
                    OnError?.Invoke(err);
                });
        }

        // JoinRoom 服务端错误码 → 客户端 ErrorCode 映射
        private static ErrorCode JoinRoomErrorCode(int serverCode) => serverCode switch
        {
            1 => ErrorCode.RoomNotFound,
            2 => ErrorCode.RoomFull,
            3 => ErrorCode.RoomNotBound,
            _ => ErrorCode.JoinRoomFailed,
        };

        private static string JoinRoomErrorName(int serverCode) => serverCode switch
        {
            1 => "NotFound",
            2 => "Full",
            3 => "NotBound",
            4 => "BadData",
            _ => "Unknown",
        };

        private void HandleMessage(Message msg)
        {
            if (msg.MsgType != CmdType.Extended) return;

            switch (msg.ExtCmd)
            {
                case FrameSyncExtCmd.PlayerJoined:
                    if (msg.DataLength >= 4)
                        OnPlayerJoined?.Invoke(RoomCodec.DecodePlayerId(msg.DataSpan));
                    break;

                case FrameSyncExtCmd.PlayerLeft:
                    if (msg.DataLength >= 4)
                        OnPlayerLeft?.Invoke(RoomCodec.DecodePlayerId(msg.DataSpan));
                    break;
            }
        }
    }
}
