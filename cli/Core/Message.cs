using System;

namespace BoomNetwork.Core
{
    /// <summary>
    /// 网络消息结构
    /// 对应线格式: [BodyLen:4][Version:1][Cmd:4][ClientSeq:4][ServerSeq:4][Data:N]
    /// 所有多字节字段使用 little-endian
    /// </summary>
    public struct Message
    {
        public const int HeaderSize = 4;         // BodyLen 字段长度
        public const int BodyHeaderSize = 13;    // Version(1) + Cmd(4) + ClientSeq(4) + ServerSeq(4)

        public byte Version;
        public uint Cmd;
        public int ClientSeq;
        public int ServerSeq;
        public byte[] Data;

        /// <summary>
        /// Data 的实际有效长度。
        /// 当 Data 来自 ArrayPool 时，Data.Length 可能大于实际数据长度。
        /// 普通场景下 DataLength == Data.Length。
        /// </summary>
        public int DataLength;

        public int TotalSize => HeaderSize + BodyHeaderSize + DataLength;

        /// <summary>
        /// 获取有效数据的 Span
        /// </summary>
        public ReadOnlySpan<byte> DataSpan => Data != null ? Data.AsSpan(0, DataLength) : ReadOnlySpan<byte>.Empty;

        public override string ToString()
        {
            return $"[Msg Cmd={Cmd} CS={ClientSeq} SS={ServerSeq} DataLen={DataLength}]";
        }
    }
}
