using System;

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
    /// 网络消息结构
    ///
    /// 新线格式（动态包头）:
    ///   [FlagsCmd: 1B][BodyLen: 2B/4B][Seq: 0B/4B][Data: NB]
    ///
    ///   FlagsCmd: bit 0 = LenSize (0=2B, 1=4B)
    ///             bit 1 = HasSeq  (0=无, 1=4B)
    ///             bit 2-7 = Cmd   (0-63)
    ///
    /// 包头大小: 3 / 5 / 7 / 9 bytes（取决于 Flags）
    /// </summary>
    public struct Message
    {
        /// <summary>
        /// 最小包头: FlagsCmd(1) + BodyLen(2) = 3 bytes
        /// </summary>
        public const int MinHeaderSize = 3;

        public byte Cmd;         // 0-63
        public int Seq;          // 请求/响应匹配序号
        public bool HasSeq;      // 是否携带 Seq
        public byte[] Data;
        public int DataLength;

        /// <summary>
        /// 计算此消息的包头大小
        /// </summary>
        public int HeaderSize
        {
            get
            {
                int size = 1; // FlagsCmd
                size += NeedLargeLen ? 4 : 2; // BodyLen
                if (HasSeq) size += 4; // Seq
                return size;
            }
        }

        /// <summary>
        /// 是否需要 4 字节 BodyLen（Data > 65535 - maxBodyHeaderSize）
        /// </summary>
        public bool NeedLargeLen => DataLength > 65530;

        /// <summary>
        /// 总大小 = 包头 + Data
        /// </summary>
        public int TotalSize => HeaderSize + DataLength;

        /// <summary>
        /// 有效数据 Span
        /// </summary>
        public ReadOnlySpan<byte> DataSpan => Data != null ? Data.AsSpan(0, DataLength) : ReadOnlySpan<byte>.Empty;

        public override string ToString()
        {
            return $"[Msg Cmd={Cmd} Seq={Seq} DataLen={DataLength}]";
        }
    }
}
