using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace BoomNetwork.Core.FrameSync
{
    /// <summary>
    /// 帧同步协议 Cmd 定义 — 三层分级
    ///
    /// Core (0-15):     高频核心命令，包头 3B
    /// Extended (uint16): 框架扩展命令，包头 5B
    /// Game (uint32):   游戏自定义命令，服务器透传，包头 7B
    /// </summary>
    public static class FrameSyncCmd
    {
        // === Core Cmd (0-15) — 高频，最小包头 ===
        public const byte SessionBind     = 1;
        public const byte SessionBindRsp  = 2;
        public const byte RequestStart    = 3;  // 客户端 → 服务器：请求开始
        public const byte StartFrameSync  = 4;  // 服务器 → 客户端：帧同步开始
        public const byte StopFrameSync   = 5;  // 双向：帧同步结束
        public const byte FrameInput      = 6;
        public const byte PushFrames      = 7;
        public const byte Heartbeat       = 8;
        public const byte HeartbeatRsp    = 9;
        public const byte Reconnect       = 10;
        public const byte ReconnectRsp    = 11;
        public const byte ServerShutdown      = 12; // 服务器 → 客户端：服务器即将关闭
        public const byte RateLimitWarning   = 13; // 服务器 → 客户端：消息速率接近上限，请降速
    }

    /// <summary>
    /// Extended Cmd (uint16) — 框架扩展命令
    /// </summary>
    public static class FrameSyncExtCmd
    {
        // 房间管理
        public const ushort GetRooms         = 1;
        public const ushort GetRoomsRsp      = 2;
        public const ushort CreateRoom       = 3;
        public const ushort CreateRoomRsp    = 4;
        public const ushort JoinRoom         = 5;
        public const ushort JoinRoomRsp      = 6;
        public const ushort LeaveRoom        = 7;
        public const ushort LeaveRoomRsp     = 8;
        public const ushort MatchRoom        = 9;  // 匹配房间
        public const ushort MatchRoomRsp     = 10; // 匹配结果

        // 服务器推送
        public const ushort PlayerJoined     = 20;
        public const ushort PlayerLeft       = 21;
        public const ushort PlayerOffline    = 22; // 玩家临时掉线
        public const ushort PlayerOnline     = 23; // 玩家恢复在线
        public const ushort RoomSnapshot     = 24; // 房间快照

        // 快照
        public const ushort UploadSnapshot    = 30;
        public const ushort UploadSnapshotRsp = 31;

        // 实体权威同步
        public const ushort SendEntityState   = 40; // 管理者发送实体状态
        public const ushort PushEntityState   = 41; // 广播实体状态（带 senderPid）

        // 权威转移（双向，Data[0] 区分 request=0 / result=1）
        public const ushort AuthorityTransfer = 42;

        // 轻量状态同步（帧同步未运行时的通信通道）
        public const ushort SendStateMsg    = 50; // C→S 状态消息（服务器转发）
        public const ushort PushStateMsg    = 51; // S→C 转发状态消息
        public const ushort SetData         = 52; // C→S 设置 KV 数据
        public const ushort PushData        = 53; // S→C 增量广播 KV 变更
        public const ushort RequestDataSync = 54; // C→S 请求全量 KV 同步
        public const ushort PushDataSync    = 55; // S→C 全量 KV 快照
    }

    /// <summary>
    /// 帧同步初始化数据（StartFrameSync 携带）
    /// Wire: [FrameRate:4][FrameInterval:4][StartTime:8][SnapshotInterval:4][QuickReconnectMaxMs:4]
    /// </summary>
    public struct FrameSyncInitData
    {
        public const int Size = 24;
        public const int LegacySize = 16;

        public int FrameRate;               // 帧率（如 20）
        public int FrameInterval;           // 帧间隔 ms（如 50）
        public long StartTime;              // 服务器开始时间戳 ms
        public int SnapshotInterval;        // 快照间隔（帧数），服务器下发
        public int QuickReconnectMaxMs;     // 快速重连最长重试时间（ms），服务器下发

        public void WriteTo(Span<byte> buf)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buf, FrameRate);
            BinaryPrimitives.WriteInt32LittleEndian(buf.Slice(4), FrameInterval);
            BinaryPrimitives.WriteInt64LittleEndian(buf.Slice(8), StartTime);
            BinaryPrimitives.WriteInt32LittleEndian(buf.Slice(16), SnapshotInterval);
            BinaryPrimitives.WriteInt32LittleEndian(buf.Slice(20), QuickReconnectMaxMs);
        }

        public static FrameSyncInitData ReadFrom(ReadOnlySpan<byte> buf)
        {
            var data = new FrameSyncInitData
            {
                FrameRate = BinaryPrimitives.ReadInt32LittleEndian(buf),
                FrameInterval = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(4)),
                StartTime = BinaryPrimitives.ReadInt64LittleEndian(buf.Slice(8)),
            };
            if (buf.Length >= Size)
            {
                data.SnapshotInterval = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(16));
                data.QuickReconnectMaxMs = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(20));
            }
            return data;
        }
    }

    /// <summary>
    /// 重连结果码（ReconnectRsp 第一个字节）
    /// </summary>
    public static class ReconnectResult
    {
        public const byte Fail = 0;             // 通用失败
        public const byte Success = 1;          // 成功
        public const byte BufferStale = 2;      // 帧缓冲区过期，客户端应降级到快照重连
    }

    /// <summary>
    /// 单个帧数据（PushFrames 中的一帧）
    /// Wire: [FrameNumber:4][InputCount:2][Inputs...]
    /// 每个 Input: [PlayerId:4][DataLen:2][Data:N]
    /// </summary>
    public struct FrameData
    {
        public uint FrameNumber;
        public PlayerInput[] Inputs;

        public struct PlayerInput
        {
            public int PlayerId;
            public byte[] Data;
            public int DataLength;

            public ReadOnlySpan<byte> DataSpan => Data.AsSpan(0, DataLength);
        }
    }

    /// <summary>
    /// 帧数据序列化工具
    /// </summary>
    public static class FrameDataCodec
    {
        /// <summary>
        /// 编码 FrameData 到 buffer
        /// </summary>
        public static int Encode(in FrameData frame, Span<byte> buf)
        {
            int offset = 0;

            BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(offset), frame.FrameNumber);
            offset += 4;

            ushort inputCount = (ushort)(frame.Inputs?.Length ?? 0);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.Slice(offset), inputCount);
            offset += 2;

            for (int i = 0; i < inputCount; i++)
            {
                ref readonly var input = ref frame.Inputs[i];
                BinaryPrimitives.WriteInt32LittleEndian(buf.Slice(offset), input.PlayerId);
                offset += 4;

                ushort dataLen = (ushort)input.DataLength;
                BinaryPrimitives.WriteUInt16LittleEndian(buf.Slice(offset), dataLen);
                offset += 2;

                if (dataLen > 0)
                {
                    input.DataSpan.CopyTo(buf.Slice(offset));
                    offset += dataLen;
                }
            }

            return offset;
        }

        /// <summary>
        /// 解码 FrameData
        /// </summary>
        public static FrameData Decode(ReadOnlySpan<byte> buf)
        {
            int offset = 0;

            var frame = new FrameData();
            frame.FrameNumber = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(offset));
            offset += 4;

            ushort inputCount = BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(offset));
            offset += 2;

            frame.Inputs = new FrameData.PlayerInput[inputCount];
            for (int i = 0; i < inputCount; i++)
            {
                frame.Inputs[i].PlayerId = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(offset));
                offset += 4;

                ushort dataLen = BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(offset));
                offset += 2;

                if (dataLen > 0)
                {
                    frame.Inputs[i].Data = buf.Slice(offset, dataLen).ToArray();
                    frame.Inputs[i].DataLength = dataLen;
                    offset += dataLen;
                }
                else
                {
                    frame.Inputs[i].Data = Array.Empty<byte>();
                    frame.Inputs[i].DataLength = 0;
                }
            }

            return frame;
        }
    }

    /// <summary>
    /// 房间信息
    /// </summary>
    public struct RoomInfo
    {
        public int RoomId;
        public int PlayerCount;
        public int MaxPlayers;
        public bool Running;     // 帧同步是否已开始
        public string MatchKey;  // 匹配 key
    }

    /// <summary>
    /// 房间协议编解码
    /// </summary>
    public static class RoomCodec
    {
        // === GetRoomsRsp ===
        // Wire: [RoomCount:2] + N × [RoomId:4][PlayerCount:2][MaxPlayers:2][Running:1][MatchKeyLen:2][MatchKey:N]

        public static RoomInfo[] DecodeRoomList(ReadOnlySpan<byte> buf)
        {
            if (buf.Length < 2) return Array.Empty<RoomInfo>();
            int offset = 0;
            ushort count = BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(offset));
            offset += 2;

            var rooms = new RoomInfo[count];
            for (int i = 0; i < count; i++)
            {
                rooms[i].RoomId = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(offset));
                offset += 4;
                rooms[i].PlayerCount = BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(offset));
                offset += 2;
                rooms[i].MaxPlayers = BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(offset));
                offset += 2;
                rooms[i].Running = buf[offset] != 0;
                offset += 1;
                // MatchKey（向后兼容：老服务器不发此字段）
                if (offset + 2 <= buf.Length)
                {
                    int keyLen = BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(offset));
                    offset += 2;
                    if (keyLen > 0 && offset + keyLen <= buf.Length)
                    {
                        rooms[i].MatchKey = System.Text.Encoding.UTF8.GetString(buf.Slice(offset, keyLen));
                        offset += keyLen;
                    }
                    else
                    {
                        rooms[i].MatchKey = "";
                    }
                }
                else
                {
                    rooms[i].MatchKey = "";
                }
            }
            return rooms;
        }

        // === CreateRoom ===
        // Wire: [MaxPlayers:2]

        public static byte[] EncodeCreateRoom(int maxPlayers)
        {
            var buf = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(buf, (ushort)maxPlayers);
            return buf;
        }

        // === MatchRoom ===
        // Wire: [MaxPlayers:2][MatchKeyLen:2][MatchKey:N]

        public static byte[] EncodeMatchRoom(int maxPlayers, string? matchKey = null)
        {
            var keyBytes = string.IsNullOrEmpty(matchKey)
                ? Array.Empty<byte>()
                : System.Text.Encoding.UTF8.GetBytes(matchKey);
            var buf = new byte[2 + 2 + keyBytes.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(buf, (ushort)maxPlayers);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2), (ushort)keyBytes.Length);
            if (keyBytes.Length > 0)
                keyBytes.CopyTo(buf.AsSpan(4));
            return buf;
        }

        // === CreateRoomRsp ===
        // Wire: [RoomId:4]

        public static int DecodeCreateRoomRsp(ReadOnlySpan<byte> buf)
        {
            return BinaryPrimitives.ReadInt32LittleEndian(buf);
        }

        // === JoinRoom ===
        // Wire: [RoomId:4]

        public static byte[] EncodeJoinRoom(int roomId)
        {
            var buf = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(buf, roomId);
            return buf;
        }

        // === JoinRoomRsp ===
        // Wire: [PlayerId:4][RoomId:4][PlayerCount:2][PlayerIds:4×N]

        public static (int playerId, int roomId, int[] existingPlayers) DecodeJoinRoomRsp(ReadOnlySpan<byte> buf)
        {
            int playerId = BinaryPrimitives.ReadInt32LittleEndian(buf);
            int roomId = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(4));

            int[] existingPlayers = Array.Empty<int>();
            if (buf.Length >= 10)
            {
                int count = BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(8));
                existingPlayers = new int[count];
                int offset = 10;
                for (int i = 0; i < count && offset + 4 <= buf.Length; i++)
                {
                    existingPlayers[i] = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(offset));
                    offset += 4;
                }
            }

            return (playerId, roomId, existingPlayers);
        }

        // === PlayerJoined / PlayerLeft (服务器推送) ===
        // Wire: [PlayerId:4]

        public static int DecodePlayerId(ReadOnlySpan<byte> buf)
        {
            return BinaryPrimitives.ReadInt32LittleEndian(buf);
        }
    }

    /// <summary>
    /// 快照编解码
    /// </summary>
    public static class SnapshotCodec
    {
        // === UploadSnapshot ===
        // Wire: [FrameNumber:4][SnapshotData:N]

        public static byte[] EncodeUploadSnapshot(uint frameNumber, byte[] snapshotData)
        {
            var buf = new byte[4 + snapshotData.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(buf, frameNumber);
            Buffer.BlockCopy(snapshotData, 0, buf, 4, snapshotData.Length);
            return buf;
        }

        // === ReconnectRsp ===
        // Wire: [Result:1][RoomId:4][ServerFrame:4][SnapshotFrame:4][SnapshotData:N]
        // Result: 0=失败, 1=成功, 2=缓冲区过期(需降级到快照重连)

        public static (byte result, int roomId, uint serverFrame, uint snapshotFrame, byte[]? snapshotData)
            DecodeReconnectRsp(ReadOnlySpan<byte> buf)
        {
            if (buf.Length < 1) return (ReconnectResult.Fail, 0, 0, 0, null);

            byte result = buf[0];
            if (buf.Length < 13)
                return (result, 0, 0, 0, null);

            int roomId = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(1));
            uint serverFrame = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(5));
            uint snapshotFrame = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(9));

            byte[]? snapshotData = null;
            if (buf.Length > 13 && snapshotFrame > 0)
            {
                snapshotData = buf.Slice(13).ToArray();
            }

            return (result, roomId, serverFrame, snapshotFrame, snapshotData);
        }
    }

    // ===================== 实体权威同步 =====================

    /// <summary>
    /// 实体同步接口 — 游戏层为每个需要同步的实体实现
    ///
    /// 框架只关心字节，不关心内容。
    /// </summary>
    public interface IEntitySync
    {
        /// <summary>实体唯一标识</summary>
        int EntityId { get; }

        /// <summary>状态字节大小（固定大小或最大大小）</summary>
        int StateSize { get; }

        /// <summary>管理者调用：序列化当前状态到 buffer，返回写入字节数</summary>
        int WriteState(byte[] buffer, int offset);

        /// <summary>远端调用：收到管理者的权威状态</summary>
        /// <remarks>
        /// 游戏层决定如何使用此状态（惯性追踪 / 直接应用 / 忽略）。
        /// 框架只负责送达。
        /// </remarks>
        void OnRemoteState(byte[] authorityState, int offset, int length, int senderPlayerId);
    }

    /// <summary>
    /// 实体状态编解码
    ///
    /// C→S (ExtCmd 40): [entityCount:1B] + N × [entityId:4B][stateLen:2B][stateData]
    /// S→C (ExtCmd 41): [senderPid:4B] + [entityCount:1B] + N × [entityId:4B][stateLen:2B][stateData]
    /// </summary>
    public static class EntityStateCodec
    {
        /// <summary>编码管理者实体状态（客户端发送用）</summary>
        public static int Encode(byte[] buf, int offset, IList<IEntitySync> entities, int count)
        {
            int start = offset;
            buf[offset++] = (byte)count;
            for (int i = 0; i < count; i++)
            {
                var e = entities[i];
                BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(buf, offset, 4), e.EntityId);
                offset += 4;
                int stateStart = offset + 2; // reserve 2B for stateLen
                int stateLen = e.WriteState(buf, stateStart);
                BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(buf, offset, 2), (ushort)stateLen);
                offset = stateStart + stateLen;
            }
            return offset - start;
        }

        /// <summary>解码推送的实体状态（客户端接收用）</summary>
        /// <summary>
        /// 解码 PushEntityState (ExtCmd 41)
        /// onEntity(senderPid, entityId, data, offset, length)
        /// </summary>
        public static void Decode(ReadOnlySpan<byte> data, Action<int, int, byte[], int, int> onEntity)
        {
            if (data.Length < 5) return;
            int senderPid = BinaryPrimitives.ReadInt32LittleEndian(data);
            int count = data[4];
            int offset = 5;

            // 需要复制到 byte[] 因为 ReadOnlySpan 不能跨回调边界
            var buf = data.ToArray();

            for (int i = 0; i < count && offset < buf.Length; i++)
            {
                if (offset + 6 > buf.Length) break;
                int entityId = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(buf, offset, 4));
                offset += 4;
                int stateLen = BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(buf, offset, 2));
                offset += 2;
                if (offset + stateLen > buf.Length) break;
                onEntity(senderPid, entityId, buf, offset, stateLen);
                offset += stateLen;
            }
        }
    }

    /// <summary>
    /// 权威转移编解码
    ///
    /// C→S ExtCmd 42 (subCmd=0 request): [entityId:4][release:1]
    ///   release=0 → 请求获取权威, release=1 → 主动释放权威
    ///
    /// S→C ExtCmd 42 (subCmd=1 result): [entityId:4][newOwnerPlayerId:4]
    ///   newOwnerPlayerId=0 → unclaimed
    /// </summary>
    public static class AuthorityTransferCodec
    {
        public const int RequestSize = 5;
        public const int ResultSize  = 8;

        public static byte[] EncodeRequest(int entityId, bool release)
        {
            var buf = new byte[RequestSize];
            BinaryPrimitives.WriteInt32LittleEndian(buf, entityId);
            buf[4] = release ? (byte)1 : (byte)0;
            return buf;
        }

        public static (int entityId, bool release) DecodeRequest(ReadOnlySpan<byte> buf)
        {
            int entityId = BinaryPrimitives.ReadInt32LittleEndian(buf);
            bool release = buf[4] != 0;
            return (entityId, release);
        }

        public static byte[] EncodeResult(int entityId, int newOwnerPlayerId)
        {
            var buf = new byte[ResultSize];
            BinaryPrimitives.WriteInt32LittleEndian(buf, entityId);
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4), newOwnerPlayerId);
            return buf;
        }

        public static (int entityId, int newOwnerPlayerId) DecodeResult(ReadOnlySpan<byte> buf)
        {
            int entityId = BinaryPrimitives.ReadInt32LittleEndian(buf);
            int newOwnerPlayerId = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(4));
            return (entityId, newOwnerPlayerId);
        }
    }

    // ===================== 轻量状态同步 =====================

    /// <summary>
    /// KV 数据条目
    /// </summary>
    public struct DataEntry
    {
        public int PlayerId;
        public int Key;
        public byte[] Value;
    }

    /// <summary>
    /// 轻量状态同步编解码
    ///
    /// StateMessage: 服务器纯转发的事件消息
    /// DataMessage:  服务器存储 KV 并增量广播
    /// </summary>
    public static class StateSyncCodec
    {
        // === SendStateMsg (C→S) ===
        // Wire: [Data:N] (原始 payload)

        public static byte[] EncodeStateMsg(ReadOnlySpan<byte> data)
        {
            return data.ToArray();
        }

        // === PushStateMsg (S→C) ===
        // Wire: [PlayerId:4][Data:N]

        public static (int playerId, byte[] data) DecodePushStateMsg(ReadOnlySpan<byte> buf)
        {
            int playerId = BinaryPrimitives.ReadInt32LittleEndian(buf);
            byte[] data = buf.Slice(4).ToArray();
            return (playerId, data);
        }

        // === SetData (C→S) ===
        // Wire: [Key:4][ValueLen:2][Value:N]

        public static byte[] EncodeSetData(int key, ReadOnlySpan<byte> value)
        {
            var buf = new byte[6 + value.Length];
            BinaryPrimitives.WriteInt32LittleEndian(buf, key);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4), (ushort)value.Length);
            if (value.Length > 0)
                value.CopyTo(buf.AsSpan(6));
            return buf;
        }

        public static byte[] EncodeDeleteData(int key)
        {
            var buf = new byte[6];
            BinaryPrimitives.WriteInt32LittleEndian(buf, key);
            // ValueLen = 0 means delete
            return buf;
        }

        // === PushData (S→C) — 增量 ===
        // Wire: [Version:4][PlayerId:4][Key:4][ValueLen:2][Value:N]

        public static (uint version, int playerId, int key, byte[] value) DecodePushData(ReadOnlySpan<byte> buf)
        {
            uint version = BinaryPrimitives.ReadUInt32LittleEndian(buf);
            int playerId = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(4));
            int key = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(8));
            ushort valueLen = BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(12));
            byte[] value = valueLen > 0 ? buf.Slice(14, valueLen).ToArray() : Array.Empty<byte>();
            return (version, playerId, key, value);
        }

        // === PushDataSync (S→C) — 全量快照 ===
        // Wire: [Version:4][EntryCount:2] + N × [PlayerId:4][Key:4][ValueLen:2][Value:N]

        public static (uint version, DataEntry[] entries) DecodePushDataSync(ReadOnlySpan<byte> buf)
        {
            uint version = BinaryPrimitives.ReadUInt32LittleEndian(buf);
            ushort count = BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(4));
            var entries = new DataEntry[count];
            int offset = 6;
            for (int i = 0; i < count; i++)
            {
                entries[i].PlayerId = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(offset));
                offset += 4;
                entries[i].Key = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(offset));
                offset += 4;
                ushort valueLen = BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(offset));
                offset += 2;
                entries[i].Value = valueLen > 0 ? buf.Slice(offset, valueLen).ToArray() : Array.Empty<byte>();
                offset += valueLen;
            }
            return (version, entries);
        }
    }
}
