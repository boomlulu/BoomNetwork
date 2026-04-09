using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using BoomNetwork.Core;
using BoomNetwork.Core.Codec;

namespace BoomNetwork.Core.FrameSync
{
    /// <summary>
    /// 协议安全限制常量（C3 修复）
    ///
    /// 所有解码方法对变长字段使用这些上限，防止恶意或截断数据造成 OOM / panic。
    /// </summary>
    internal static class ProtocolLimits
    {
        public const int MaxFrameInputs  = 256;    // 单帧最多 256 个玩家输入
        public const int MaxInputDataLen = 4096;   // 单条输入数据最大 4KB
        public const int MaxFrameEvents  = 32;     // 单帧最多 32 个帧内事件
        public const int MaxRooms        = 1000;   // 房间列表最多 1000 个
        public const int MaxRoomKeyLen   = 256;    // matchKey 最长 256 字节
        public const int MaxValueLen     = 65_536; // KV value 最大 64KB
        public const int MaxEntries      = 1000;   // KV 全量快照最多 1000 条
    }

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

        // 帧同步暂停/恢复
        public const ushort FrameSyncPaused  = 56; // S→C [Reason:1]
        public const ushort FrameSyncResumed = 57; // S→C (empty)

        // 游戏级暂停（客户端请求）
        public const ushort RequestGamePause  = 58; // C→S (empty)
        public const ushort RequestGameResume = 59; // C→S (empty)

        // 不同步检测
        public const ushort FrameHash         = 60; // C→S [FrameNumber:4][Hash:4]
        public const ushort FrameHashMismatch = 61; // S→C [FrameNumber:4][PlayerCount:1][PlayerId:4+Hash:4]...

        // 可靠通道（双向，快速重连不掉消息）
        public const ushort ReliableMsg = 200; // 包装任意消息，赋予可靠语义
        // Wire: [reliableSeq:4][innerCmdType:1][innerCmd:0/2/4][innerData:N]
        public const ushort ReliableAck = 201; // S→C 确认已处理的 C→S reliable seq
        // Wire: [ackSeq:4]
    }

    /// <summary>帧内事件类型（嵌入 FrameData，确保所有客户端在同一帧处理）</summary>
    public static class FrameEventType
    {
        public const byte PlayerJoined  = 1;
        public const byte PlayerLeft    = 2;
        public const byte PlayerOffline = 3;
        public const byte PlayerOnline  = 4;
        public const byte HostChanged   = 5;
    }

    /// <summary>帧同步暂停原因</summary>
    public enum FrameSyncPauseReason : byte
    {
        SnapshotStale = 1,  // 快照过期
        Desync        = 2,  // 帧 hash 不匹配（不同步）
        GamePause     = 3,  // 游戏逻辑暂停（客户端请求）
    }

    /// <summary>帧 hash 不匹配详情</summary>
    public struct FrameHashMismatch
    {
        public uint FrameNumber;
        public (int PlayerId, uint Hash)[] PlayerHashes;

        /// <summary>
        /// C3: 所有读取前验证长度，count×8 越界 → InvalidDataException。
        /// Wire: [FrameNumber:4][PlayerCount:1][PlayerId:4+Hash:4]×N
        /// </summary>
        public static FrameHashMismatch Decode(ReadOnlySpan<byte> buf)
        {
            if (buf.Length < 5)
                throw new InvalidDataException(
                    $"FrameHashMismatch too short: need 5, got {buf.Length}");

            var result = new FrameHashMismatch();
            result.FrameNumber = BinaryPrimitives.ReadUInt32LittleEndian(buf);
            int count = buf[4];

            int needed = 5 + count * 8;
            if (buf.Length < needed)
                throw new InvalidDataException(
                    $"FrameHashMismatch truncated: need {needed} for {count} players, got {buf.Length}");

            result.PlayerHashes = new (int, uint)[count];
            int offset = 5;
            for (int i = 0; i < count; i++)
            {
                int pid  = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(offset));
                uint hash = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(offset + 4));
                result.PlayerHashes[i] = (pid, hash);
                offset += 8;
            }
            return result;
        }
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

        /// <summary>
        /// C3: 入口检查至少 LegacySize(16) 字节。
        /// </summary>
        public static FrameSyncInitData ReadFrom(ReadOnlySpan<byte> buf)
        {
            if (buf.Length < LegacySize)
                throw new InvalidDataException(
                    $"FrameSyncInitData too short: need {LegacySize}, got {buf.Length}");

            var data = new FrameSyncInitData
            {
                FrameRate     = BinaryPrimitives.ReadInt32LittleEndian(buf),
                FrameInterval = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(4)),
                StartTime     = BinaryPrimitives.ReadInt64LittleEndian(buf.Slice(8)),
            };
            if (buf.Length >= Size)
            {
                data.SnapshotInterval    = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(16));
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
        public const byte S2CBufStale = 3;      // S→C reliable buffer 过期，客户端应降级到快照重连
    }

    /// <summary>帧内事件</summary>
    public struct FrameEvent
    {
        public byte EventType;
        public int PlayerId;
    }

    /// <summary>
    /// 单个帧数据（PushFrames 中的一帧）
    /// Wire: [FrameNumber:4][InputCount:2][Inputs...][EventCount:1][Events...]
    /// 每个 Input: [PlayerId:4][DataLen:2][Data:N]
    /// 每个 Event: [EventType:1][PlayerId:4]
    /// </summary>
    public struct FrameData
    {
        public uint FrameNumber;
        public PlayerInput[] Inputs;
        public FrameEvent[] Events;  // 帧内事件

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
                ref readonly var input = ref frame.Inputs![i];
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

            // Events
            byte eventCount = (byte)(frame.Events?.Length ?? 0);
            buf[offset] = eventCount;
            offset++;
            if (frame.Events != null)
            {
                for (int i = 0; i < frame.Events.Length; i++)
                {
                    buf[offset] = frame.Events[i].EventType;
                    offset++;
                    BinaryPrimitives.WriteInt32LittleEndian(buf.Slice(offset), frame.Events[i].PlayerId);
                    offset += 4;
                }
            }

            return offset;
        }

        /// <summary>
        /// 解码 FrameData。
        ///
        /// C3: 完整边界检查：
        ///   - 入口至少 6B（FrameNumber + InputCount）
        ///   - inputCount 上限 MaxFrameInputs=256（防 OOM）
        ///   - 每条 Input 读取前验证剩余长度
        ///   - dataLen 上限 MaxInputDataLen=4096（防单条 OOM）
        ///   - eventCount 上限 MaxFrameEvents=32
        ///   - 每条 Event 读取前验证剩余长度
        ///
        /// 任何违规均抛出 InvalidDataException（不发生越界访问）。
        /// </summary>
        public static FrameData Decode(ReadOnlySpan<byte> buf)
        {
            if (buf.Length < 6)
                throw new InvalidDataException(
                    $"FrameData too short: need 6, got {buf.Length}");

            int offset = 0;
            var frame = new FrameData();

            frame.FrameNumber = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(offset));
            offset += 4;

            int inputCount = BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(offset));
            offset += 2;

            if (inputCount > ProtocolLimits.MaxFrameInputs)
                throw new InvalidDataException(
                    $"FrameData inputCount {inputCount} exceeds limit {ProtocolLimits.MaxFrameInputs}");

            frame.Inputs = new FrameData.PlayerInput[inputCount];
            for (int i = 0; i < inputCount; i++)
            {
                if (offset + 6 > buf.Length)
                    throw new InvalidDataException(
                        $"FrameData truncated reading input[{i}] header at offset {offset}");

                frame.Inputs[i].PlayerId = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(offset));
                offset += 4;

                int dataLen = BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(offset));
                offset += 2;

                if (dataLen > ProtocolLimits.MaxInputDataLen)
                    throw new InvalidDataException(
                        $"FrameData input[{i}].dataLen {dataLen} exceeds limit {ProtocolLimits.MaxInputDataLen}");

                if (dataLen > 0)
                {
                    if (offset + dataLen > buf.Length)
                        throw new InvalidDataException(
                            $"FrameData truncated reading input[{i}].data: need {offset + dataLen}, got {buf.Length}");

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

            // Events（向后兼容：旧格式无此字段）
            if (offset < buf.Length)
            {
                if (offset + 1 > buf.Length)
                    throw new InvalidDataException("FrameData truncated reading eventCount");

                int eventCount = buf[offset];
                offset++;

                if (eventCount > ProtocolLimits.MaxFrameEvents)
                    throw new InvalidDataException(
                        $"FrameData eventCount {eventCount} exceeds limit {ProtocolLimits.MaxFrameEvents}");

                frame.Events = new FrameEvent[eventCount];
                for (int i = 0; i < eventCount; i++)
                {
                    if (offset + 5 > buf.Length)
                        throw new InvalidDataException(
                            $"FrameData truncated reading event[{i}] at offset {offset}");

                    frame.Events[i].EventType = buf[offset];
                    offset++;
                    frame.Events[i].PlayerId = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(offset));
                    offset += 4;
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

        /// <summary>
        /// C3: count 上限 MaxRooms，每房间固定 9B 字段读取前验证剩余长度。
        /// </summary>
        public static RoomInfo[] DecodeRoomList(ReadOnlySpan<byte> buf)
        {
            if (buf.Length < 2) return Array.Empty<RoomInfo>();

            int offset = 0;
            int count = BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(offset));
            offset += 2;

            if (count > ProtocolLimits.MaxRooms)
                throw new InvalidDataException(
                    $"DecodeRoomList count {count} exceeds limit {ProtocolLimits.MaxRooms}");

            var rooms = new RoomInfo[count];
            for (int i = 0; i < count; i++)
            {
                // 固定字段：RoomId(4)+PlayerCount(2)+MaxPlayers(2)+Running(1) = 9B
                if (offset + 9 > buf.Length)
                    throw new InvalidDataException(
                        $"DecodeRoomList truncated at room[{i}] fixed fields, offset={offset}");

                rooms[i].RoomId      = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(offset));
                offset += 4;
                rooms[i].PlayerCount = BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(offset));
                offset += 2;
                rooms[i].MaxPlayers  = BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(offset));
                offset += 2;
                rooms[i].Running     = buf[offset] != 0;
                offset += 1;

                // MatchKey（向后兼容：老服务器不发此字段）
                if (offset + 2 <= buf.Length)
                {
                    int keyLen = BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(offset));
                    offset += 2;

                    if (keyLen > ProtocolLimits.MaxRoomKeyLen)
                        throw new InvalidDataException(
                            $"DecodeRoomList room[{i}].keyLen {keyLen} exceeds limit {ProtocolLimits.MaxRoomKeyLen}");

                    if (keyLen > 0 && offset + keyLen <= buf.Length)
                    {
                        rooms[i].MatchKey = System.Text.Encoding.UTF8.GetString(buf.Slice(offset, keyLen));
                        offset += keyLen;
                    }
                    else
                    {
                        rooms[i].MatchKey = "";
                        if (keyLen > 0) offset += Math.Min(keyLen, buf.Length - offset);
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
        // Wire: [MaxPlayers:2][MatchKeyLen:2][MatchKey:N]（向后兼容：老服务器只读前2字节）
        public static byte[] EncodeCreateRoom(int maxPlayers, string? matchKey = null)
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

        /// <summary>C3: 入口检查 ≥4B。</summary>
        public static int DecodeCreateRoomRsp(ReadOnlySpan<byte> buf)
        {
            if (buf.Length < 4)
                throw new InvalidDataException(
                    $"DecodeCreateRoomRsp too short: need 4, got {buf.Length}");
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

        /// <summary>C3: 入口检查 ≥8B（PlayerId + RoomId）。</summary>
        public static (int playerId, int roomId, int[] existingPlayers) DecodeJoinRoomRsp(ReadOnlySpan<byte> buf)
        {
            if (buf.Length < 8)
                throw new InvalidDataException(
                    $"DecodeJoinRoomRsp too short: need 8, got {buf.Length}");

            int playerId = BinaryPrimitives.ReadInt32LittleEndian(buf);
            int roomId   = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(4));

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

        /// <summary>C3: 入口检查 ≥4B。</summary>
        public static int DecodePlayerId(ReadOnlySpan<byte> buf)
        {
            if (buf.Length < 4)
                throw new InvalidDataException(
                    $"DecodePlayerId too short: need 4, got {buf.Length}");
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
        // Wire: [Result:1][RoomId:4][ServerFrame:4][SnapshotFrame:4][ServerLastC2SSeq:4][SnapshotData:N]
        // Result: 0=失败, 1=成功, 2=帧缓冲区过期, 3=S→C reliable buffer 过期(均需降级到快照重连)

        public static (byte result, int roomId, uint serverFrame, uint snapshotFrame, uint serverLastC2SSeq, byte[]? snapshotData)
            DecodeReconnectRsp(ReadOnlySpan<byte> buf)
        {
            if (buf.Length < 1) return (ReconnectResult.Fail, 0, 0, 0, 0, null);

            byte result = buf[0];
            if (buf.Length < 17)
                return (result, 0, 0, 0, 0, null);

            int roomId             = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(1));
            uint serverFrame       = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(5));
            uint snapshotFrame     = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(9));
            uint serverLastC2SSeq  = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(13));

            byte[]? snapshotData = null;
            if (buf.Length > 17 && snapshotFrame > 0)
            {
                snapshotData = buf.Slice(17).ToArray();
            }

            return (result, roomId, serverFrame, snapshotFrame, serverLastC2SSeq, snapshotData);
        }

        // === Reliable Channel ===

        /// <summary>
        /// 编码 ExtCmdReliableMsg 消息体
        /// Wire: [reliableSeq:4][innerCmdType:1][innerCmd:0/2/4][innerData:N]
        /// </summary>
        public static byte[] EncodeReliableMsgData(uint seq, Message inner)
        {
            int cmdHeaderSize = inner.MsgType switch
            {
                CmdType.Core     => 2, // type(1) + cmd(1)
                CmdType.Extended => 3, // type(1) + extCmd(2)
                CmdType.Game     => 5, // type(1) + gameCmd(4)
                _                => 1,
            };
            int dataLen = inner.DataLength;
            var buf = new byte[4 + cmdHeaderSize + dataLen];
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0), seq);
            switch (inner.MsgType)
            {
                case CmdType.Core:
                    buf[4] = (byte)CmdType.Core;
                    buf[5] = inner.Cmd;
                    break;
                case CmdType.Extended:
                    buf[4] = (byte)CmdType.Extended;
                    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(5), inner.ExtCmd);
                    break;
                case CmdType.Game:
                    buf[4] = (byte)CmdType.Game;
                    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(5), inner.GameCmd);
                    break;
            }
            if (dataLen > 0)
                Buffer.BlockCopy(inner.Data, 0, buf, 4 + cmdHeaderSize, dataLen);
            return buf;
        }

        /// <summary>
        /// 从 ExtCmdReliableMsg 消息体中读取 reliableSeq（不解析 inner）
        /// </summary>
        public static bool TryDecodeReliableSeq(ReadOnlySpan<byte> data, out uint seq)
        {
            if (data.Length < 5) { seq = 0; return false; }
            seq = BinaryPrimitives.ReadUInt32LittleEndian(data);
            return true;
        }

        /// <summary>
        /// 从 ExtCmdReliableMsg 消息体中解包内层消息
        /// </summary>
        public static Message? DecodeReliableInner(ReadOnlySpan<byte> data)
        {
            if (data.Length < 5) return null; // seq(4) + type(1) minimum
            var inner = data.Slice(4);
            var innerType = (CmdType)inner[0];
            switch (innerType)
            {
                case CmdType.Core:
                    if (inner.Length < 2) return null;
                    var coreData = inner.Length > 2 ? inner.Slice(2).ToArray() : Array.Empty<byte>();
                    return new Message { MsgType = CmdType.Core, Cmd = inner[1], Data = coreData, DataLength = coreData.Length };
                case CmdType.Extended:
                    if (inner.Length < 3) return null;
                    var extCmd = BinaryPrimitives.ReadUInt16LittleEndian(inner.Slice(1));
                    var extData = inner.Length > 3 ? inner.Slice(3).ToArray() : Array.Empty<byte>();
                    return new Message { MsgType = CmdType.Extended, ExtCmd = extCmd, Data = extData, DataLength = extData.Length };
                case CmdType.Game:
                    if (inner.Length < 5) return null;
                    var gameCmd = BinaryPrimitives.ReadUInt32LittleEndian(inner.Slice(1));
                    var gameData = inner.Length > 5 ? inner.Slice(5).ToArray() : Array.Empty<byte>();
                    return new Message { MsgType = CmdType.Game, GameCmd = gameCmd, Data = gameData, DataLength = gameData.Length };
                default:
                    return null;
            }
        }

        /// <summary>
        /// 读取 ExtCmdReliableAck 消息体中的 ackSeq
        /// Wire: [ackSeq:4]
        /// </summary>
        public static bool TryDecodeReliableAck(ReadOnlySpan<byte> data, out uint ackSeq)
        {
            if (data.Length < 4) { ackSeq = 0; return false; }
            ackSeq = BinaryPrimitives.ReadUInt32LittleEndian(data);
            return true;
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

        /// <summary>
        /// 解码 PushEntityState (ExtCmd 41)
        /// onEntity(senderPid, entityId, data, offset, length)
        ///
        /// 已有边界检查：entry-level ≥5B，循环内每项 ≥6B + stateLen 验证。
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

        /// <summary>已有入口检查 ≥5B。</summary>
        public static (int entityId, bool release) DecodeRequest(ReadOnlySpan<byte> buf)
        {
            if (buf.Length < RequestSize)
                throw new InvalidDataException(
                    $"DecodeRequest too short: need {RequestSize}, got {buf.Length}");
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

        /// <summary>C3: 入口检查 ≥ResultSize(8)B。</summary>
        public static (int entityId, int newOwnerPlayerId) DecodeResult(ReadOnlySpan<byte> buf)
        {
            if (buf.Length < ResultSize)
                throw new InvalidDataException(
                    $"DecodeResult too short: need {ResultSize}, got {buf.Length}");
            int entityId       = BinaryPrimitives.ReadInt32LittleEndian(buf);
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

        /// <summary>C3: 入口检查 ≥4B。</summary>
        public static (int playerId, byte[] data) DecodePushStateMsg(ReadOnlySpan<byte> buf)
        {
            if (buf.Length < 4)
                throw new InvalidDataException(
                    $"DecodePushStateMsg too short: need 4, got {buf.Length}");
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

        /// <summary>
        /// C3: 入口检查 ≥14B（固定头），valueLen 读取后验证剩余长度。
        /// </summary>
        public static (uint version, int playerId, int key, byte[] value) DecodePushData(ReadOnlySpan<byte> buf)
        {
            if (buf.Length < 14)
                throw new InvalidDataException(
                    $"DecodePushData too short: need 14, got {buf.Length}");

            uint version  = BinaryPrimitives.ReadUInt32LittleEndian(buf);
            int playerId  = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(4));
            int key       = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(8));
            int valueLen  = BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(12));

            if (valueLen > ProtocolLimits.MaxValueLen)
                throw new InvalidDataException(
                    $"DecodePushData valueLen {valueLen} exceeds limit {ProtocolLimits.MaxValueLen}");

            if (valueLen > 0 && 14 + valueLen > buf.Length)
                throw new InvalidDataException(
                    $"DecodePushData truncated: need {14 + valueLen}, got {buf.Length}");

            byte[] value = valueLen > 0 ? buf.Slice(14, valueLen).ToArray() : Array.Empty<byte>();
            return (version, playerId, key, value);
        }

        // === PushDataSync (S→C) — 全量快照 ===
        // Wire: [Version:4][EntryCount:2] + N × [PlayerId:4][Key:4][ValueLen:2][Value:N]

        /// <summary>
        /// C3: 入口检查 ≥6B，count 上限 MaxEntries，每项固定 10B 读取前验证，valueLen 读后验证。
        /// </summary>
        public static (uint version, DataEntry[] entries) DecodePushDataSync(ReadOnlySpan<byte> buf)
        {
            if (buf.Length < 6)
                throw new InvalidDataException(
                    $"DecodePushDataSync too short: need 6, got {buf.Length}");

            uint version = BinaryPrimitives.ReadUInt32LittleEndian(buf);
            int count    = BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(4));

            if (count > ProtocolLimits.MaxEntries)
                throw new InvalidDataException(
                    $"DecodePushDataSync count {count} exceeds limit {ProtocolLimits.MaxEntries}");

            var entries = new DataEntry[count];
            int offset = 6;
            for (int i = 0; i < count; i++)
            {
                // 每项固定字段：PlayerId(4)+Key(4)+ValueLen(2) = 10B
                if (offset + 10 > buf.Length)
                    throw new InvalidDataException(
                        $"DecodePushDataSync truncated at entry[{i}] fixed fields, offset={offset}");

                entries[i].PlayerId = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(offset));
                offset += 4;
                entries[i].Key      = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(offset));
                offset += 4;
                int valueLen = BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(offset));
                offset += 2;

                if (valueLen > ProtocolLimits.MaxValueLen)
                    throw new InvalidDataException(
                        $"DecodePushDataSync entry[{i}].valueLen {valueLen} exceeds limit {ProtocolLimits.MaxValueLen}");

                if (valueLen > 0)
                {
                    if (offset + valueLen > buf.Length)
                        throw new InvalidDataException(
                            $"DecodePushDataSync truncated reading entry[{i}].value");
                    entries[i].Value = buf.Slice(offset, valueLen).ToArray();
                    offset += valueLen;
                }
                else
                {
                    entries[i].Value = Array.Empty<byte>();
                }
            }
            return (version, entries);
        }
    }
}
