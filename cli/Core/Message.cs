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

        public int TotalSize => HeaderSize + BodyHeaderSize + (Data?.Length ?? 0);

        public override string ToString()
        {
            return $"[Msg Cmd={Cmd} CS={ClientSeq} SS={ServerSeq} DataLen={Data?.Length ?? 0}]";
        }
    }
}
