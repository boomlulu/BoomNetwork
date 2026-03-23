using System;
using System.Buffers.Binary;

namespace BoomNetwork.Core.FrameSync
{
    /// <summary>
    /// 帧同步协议 Cmd 定义（客户端服务器共用）
    /// </summary>
    public static class FrameSyncCmd
    {
        // 注意: FlagsCmd 只有 6 bit 给 Cmd，范围 0-63
        public const byte SessionBind       = 1;
        public const byte SessionBindRsp    = 2;

        public const byte RequestStart     = 3;  // 客户端 → 服务器：请求开始帧同步
        public const byte StartFrameSync  = 4;  // 服务器 → 客户端：帧同步开始（广播）
        public const byte StopFrameSync   = 21; // 服务器 → 客户端：帧同步结束

        public const byte FrameInput       = 5;
        public const byte PushFrames       = 6;

        public const byte Heartbeat        = 7;
        public const byte HeartbeatRsp     = 8;

        public const byte Reconnect        = 9;
        public const byte ReconnectRsp     = 10;

        // 房间管理
        public const byte GetRooms         = 11;
        public const byte GetRoomsRsp      = 12;
        public const byte CreateRoom       = 13;
        public const byte CreateRoomRsp    = 14;
        public const byte JoinRoom         = 15;
        public const byte JoinRoomRsp      = 16;
        public const byte LeaveRoom        = 17;
        public const byte LeaveRoomRsp     = 18;

        // 服务器推送
        public const byte PlayerJoined     = 19;
        public const byte PlayerLeft       = 20;
        public const byte PlayerOffline    = 24;  // 玩家临时掉线
        public const byte PlayerOnline     = 25;  // 玩家恢复在线（重连成功）
        public const byte RoomSnapshot     = 26;  // 服务器 → 客户端：房间快照（迟到者加入）

        // 快照
        public const byte UploadSnapshot    = 22;  // 客户端 → 服务器：上传快照
        public const byte UploadSnapshotRsp = 23;  // 服务器 → 客户端：上传确认
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
    }

    /// <summary>
    /// 房间协议编解码
    /// </summary>
    public static class RoomCodec
    {
        // === GetRoomsRsp ===
        // Wire: [RoomCount:2] + N × [RoomId:4][PlayerCount:2][MaxPlayers:2][Running:1]

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
    /// 游戏层实现此接口，库只管传输不关心快照内容
    /// </summary>
    public interface ISnapshotable
    {
        /// <summary>
        /// 序列化当前游戏状态（帧执行完毕后调用）
        /// </summary>
        byte[] TakeSnapshot();

        /// <summary>
        /// 从快照恢复游戏状态
        /// 加载后帧同步从 snapshotFrame+1 继续执行
        /// </summary>
        void LoadSnapshot(byte[] data);
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
}
