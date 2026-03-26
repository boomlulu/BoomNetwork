using System;
using System.Runtime.CompilerServices;

namespace BoomNetwork.Core
{
    /// <summary>
    /// 包头标志位
    /// </summary>
    [Flags]
    public enum MessageFlags : byte
    {
        None     = 0,
        LenSize4 = 1 << 0,  // BodyLen 用 4 字节（否则 2 字节）
        HasSeq   = 1 << 1,  // 有 Seq 字段（4 字节）
    }

    /// <summary>
    /// 命令层级类型
    /// </summary>
    public enum CmdType : byte
    {
        Core     = 0, // 高频核心命令, Cmd 在 FlagsCmd 高 4 位 (0-15)
        Extended = 1, // 框架扩展命令, ExtCmd uint16 在 body 内
        Game     = 2, // 游戏自定义命令, GameCmd uint32 在 body 内
    }

    /// <summary>
    /// 网络消息结构
    ///
    /// 线格式（动态包头 + 三层 Cmd）:
    ///   [FlagsCmd: 1B][BodyLen: 2B/4B][Seq: 0B/4B][ExtCmd/GameCmd: 0/2/4B][Data: NB]
    ///
    ///   FlagsCmd: bit 0   = LenSize  (0=2B, 1=4B)
    ///             bit 1   = HasSeq   (0=无, 1=4B)
    ///             bit 2-3 = CmdType  (00=Core, 01=Extended, 10=Game)
    ///             bit 4-7 = CoreCmd  (0-15, 仅 CmdType=Core)
    ///
    /// Core:     帧同步/心跳/连接 — 最高频，包头 3B
    /// Extended: 房间/实体管理 — 中频，包头 5B
    /// Game:     游戏自定义 — 服务器透传，包头 7B
    /// </summary>
    public struct Message
    {
        /// <summary>
        /// 最小包头: FlagsCmd(1) + BodyLen(2) = 3 bytes
        /// </summary>
        public const int MinHeaderSize = 3;

        public CmdType MsgType;     // Core / Extended / Game
        public byte Cmd;            // Core Cmd (0-15)
        public ushort ExtCmd;       // Extended Cmd (0-65535)
        public uint GameCmd;        // Game Cmd (0-4294967295)
        public int Seq;
        public bool HasSeq;
        public byte[] Data;
        public int DataLength;

        /// <summary>ExtCmd/GameCmd 在 body 内占的额外字节</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetCmdExtraSize() => MsgType switch
        {
            CmdType.Extended => 2,
            CmdType.Game => 4,
            _ => 0,
        };

        /// <summary>包头大小（供外部调用，内部热路径应直接用 CalcSizes）</summary>
        public int HeaderSize
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                int extra = GetCmdExtraSize();
                int payload = DataLength + extra;
                int size = 1; // FlagsCmd
                size += payload > 65530 ? 4 : 2; // BodyLen
                if (HasSeq) size += 4;
                size += extra;
                return size;
            }
        }

        public bool NeedLargeLen => (DataLength + GetCmdExtraSize()) > 65530;

        /// <summary>总大小 = 包头 + Data</summary>
        public int TotalSize => HeaderSize + DataLength;

        /// <summary>有效数据 Span</summary>
        public ReadOnlySpan<byte> DataSpan => Data != null ? Data.AsSpan(0, DataLength) : ReadOnlySpan<byte>.Empty;

        public override string ToString() => MsgType switch
        {
            CmdType.Extended => $"[ExtMsg ExtCmd={ExtCmd} Seq={Seq} DataLen={DataLength}]",
            CmdType.Game => $"[GameMsg GameCmd={GameCmd} DataLen={DataLength}]",
            _ => $"[Msg Cmd={Cmd} Seq={Seq} DataLen={DataLength}]",
        };
    }
}
