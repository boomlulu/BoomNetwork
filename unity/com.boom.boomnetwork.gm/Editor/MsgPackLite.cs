// MsgPackLite — 轻量级 MessagePack 编解码器
// 只覆盖 GM WebSocket 协议需要的类型子集，零外部依赖
// 支持: nil, bool, int (正/负), float64, string, bin, map, array

using System;
using System.Collections.Generic;
using System.Text;

namespace BoomNetwork.GM.Editor
{
    public static class MsgPackLite
    {
        // ===================== 编码 =====================

        public static byte[] Encode(object obj)
        {
            var buf = new List<byte>(128);
            WriteValue(buf, obj);
            return buf.ToArray();
        }

        public static byte[] EncodeMap(Dictionary<string, object> map)
        {
            return Encode(map);
        }

        static void WriteValue(List<byte> buf, object val)
        {
            if (val == null) { buf.Add(0xc0); return; }

            switch (val)
            {
                case bool b:
                    buf.Add(b ? (byte)0xc3 : (byte)0xc2);
                    break;
                case int i:
                    WriteInt(buf, i);
                    break;
                case long l:
                    WriteInt64(buf, l);
                    break;
                case uint u:
                    WriteUInt(buf, u);
                    break;
                case float f:
                    WriteFloat32(buf, f);
                    break;
                case double d:
                    WriteFloat64(buf, d);
                    break;
                case string s:
                    WriteString(buf, s);
                    break;
                case byte[] bin:
                    WriteBin(buf, bin);
                    break;
                case Dictionary<string, object> map:
                    WriteMap(buf, map);
                    break;
                case List<object> arr:
                    WriteArray(buf, arr);
                    break;
                case object[] arr:
                    WriteObjArray(buf, arr);
                    break;
                default:
                    // fallback: 尝试 toString
                    WriteString(buf, val.ToString());
                    break;
            }
        }

        static void WriteInt(List<byte> buf, int v)
        {
            if (v >= 0 && v <= 127) { buf.Add((byte)v); }
            else if (v >= -32 && v < 0) { buf.Add((byte)(0xe0 | (v & 0x1f))); }
            else if (v >= sbyte.MinValue && v <= sbyte.MaxValue) { buf.Add(0xd0); buf.Add((byte)(sbyte)v); }
            else if (v >= short.MinValue && v <= short.MaxValue) { buf.Add(0xd1); WriteBE16(buf, (ushort)(short)v); }
            else { buf.Add(0xd2); WriteBE32(buf, (uint)v); }
        }

        static void WriteInt64(List<byte> buf, long v)
        {
            if (v >= int.MinValue && v <= int.MaxValue) { WriteInt(buf, (int)v); return; }
            buf.Add(0xd3);
            WriteBE64(buf, (ulong)v);
        }

        static void WriteUInt(List<byte> buf, uint v)
        {
            if (v <= 127) { buf.Add((byte)v); }
            else if (v <= 255) { buf.Add(0xcc); buf.Add((byte)v); }
            else if (v <= 65535) { buf.Add(0xcd); WriteBE16(buf, (ushort)v); }
            else { buf.Add(0xce); WriteBE32(buf, v); }
        }

        static void WriteFloat32(List<byte> buf, float v)
        {
            buf.Add(0xca);
            var bytes = BitConverter.GetBytes(v);
            if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
            buf.AddRange(bytes);
        }

        static void WriteFloat64(List<byte> buf, double v)
        {
            buf.Add(0xcb);
            var bytes = BitConverter.GetBytes(v);
            if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
            buf.AddRange(bytes);
        }

        static void WriteString(List<byte> buf, string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            int len = bytes.Length;
            if (len <= 31) { buf.Add((byte)(0xa0 | len)); }
            else if (len <= 255) { buf.Add(0xd9); buf.Add((byte)len); }
            else if (len <= 65535) { buf.Add(0xda); WriteBE16(buf, (ushort)len); }
            else { buf.Add(0xdb); WriteBE32(buf, (uint)len); }
            buf.AddRange(bytes);
        }

        static void WriteBin(List<byte> buf, byte[] data)
        {
            int len = data.Length;
            if (len <= 255) { buf.Add(0xc4); buf.Add((byte)len); }
            else if (len <= 65535) { buf.Add(0xc5); WriteBE16(buf, (ushort)len); }
            else { buf.Add(0xc6); WriteBE32(buf, (uint)len); }
            buf.AddRange(data);
        }

        static void WriteMap(List<byte> buf, Dictionary<string, object> map)
        {
            int n = map.Count;
            if (n <= 15) { buf.Add((byte)(0x80 | n)); }
            else if (n <= 65535) { buf.Add(0xde); WriteBE16(buf, (ushort)n); }
            else { buf.Add(0xdf); WriteBE32(buf, (uint)n); }
            foreach (var kv in map)
            {
                WriteString(buf, kv.Key);
                WriteValue(buf, kv.Value);
            }
        }

        static void WriteArray(List<byte> buf, List<object> arr)
        {
            int n = arr.Count;
            if (n <= 15) { buf.Add((byte)(0x90 | n)); }
            else if (n <= 65535) { buf.Add(0xdc); WriteBE16(buf, (ushort)n); }
            else { buf.Add(0xdd); WriteBE32(buf, (uint)n); }
            foreach (var item in arr) WriteValue(buf, item);
        }

        static void WriteObjArray(List<byte> buf, object[] arr)
        {
            int n = arr.Length;
            if (n <= 15) { buf.Add((byte)(0x90 | n)); }
            else if (n <= 65535) { buf.Add(0xdc); WriteBE16(buf, (ushort)n); }
            else { buf.Add(0xdd); WriteBE32(buf, (uint)n); }
            foreach (var item in arr) WriteValue(buf, item);
        }

        static void WriteBE16(List<byte> buf, ushort v) { buf.Add((byte)(v >> 8)); buf.Add((byte)v); }
        static void WriteBE32(List<byte> buf, uint v) { buf.Add((byte)(v >> 24)); buf.Add((byte)(v >> 16)); buf.Add((byte)(v >> 8)); buf.Add((byte)v); }
        static void WriteBE64(List<byte> buf, ulong v) { WriteBE32(buf, (uint)(v >> 32)); WriteBE32(buf, (uint)v); }

        // ===================== 解码 =====================

        public static object Decode(byte[] data)
        {
            int offset = 0;
            return ReadValue(data, ref offset);
        }

        public static Dictionary<string, object> DecodeMap(byte[] data)
        {
            return Decode(data) as Dictionary<string, object>;
        }

        public static Dictionary<string, object> DecodeMap(byte[] data, int startOffset, int length)
        {
            int offset = 0;
            var slice = new byte[length];
            Buffer.BlockCopy(data, startOffset, slice, 0, length);
            return ReadValue(slice, ref offset) as Dictionary<string, object>;
        }

        static object ReadValue(byte[] data, ref int offset)
        {
            if (offset >= data.Length) return null;
            byte b = data[offset++];

            // positive fixint 0x00-0x7f
            if (b <= 0x7f) return (int)b;
            // fixmap 0x80-0x8f
            if (b >= 0x80 && b <= 0x8f) return ReadMapN(data, ref offset, b & 0x0f);
            // fixarray 0x90-0x9f
            if (b >= 0x90 && b <= 0x9f) return ReadArrayN(data, ref offset, b & 0x0f);
            // fixstr 0xa0-0xbf
            if (b >= 0xa0 && b <= 0xbf) return ReadStrN(data, ref offset, b & 0x1f);
            // negative fixint 0xe0-0xff
            if (b >= 0xe0) return (int)(sbyte)b;

            switch (b)
            {
                case 0xc0: return null;           // nil
                case 0xc2: return false;           // false
                case 0xc3: return true;            // true
                // bin
                case 0xc4: { int n = data[offset++]; return ReadBytes(data, ref offset, n); }
                case 0xc5: { int n = ReadBE16(data, ref offset); return ReadBytes(data, ref offset, n); }
                case 0xc6: { int n = (int)ReadBE32(data, ref offset); return ReadBytes(data, ref offset, n); }
                // float
                case 0xca: return ReadFloat32(data, ref offset);
                case 0xcb: return ReadFloat64(data, ref offset);
                // uint
                case 0xcc: return (int)data[offset++];
                case 0xcd: return (int)ReadBE16(data, ref offset);
                case 0xce: return (long)ReadBE32(data, ref offset);
                case 0xcf: return (long)ReadBE64(data, ref offset);
                // int
                case 0xd0: return (int)(sbyte)data[offset++];
                case 0xd1: return (int)(short)ReadBE16(data, ref offset);
                case 0xd2: return (int)ReadBE32S(data, ref offset);
                case 0xd3: return ReadBE64S(data, ref offset);
                // str
                case 0xd9: { int n = data[offset++]; return ReadStrN(data, ref offset, n); }
                case 0xda: { int n = ReadBE16(data, ref offset); return ReadStrN(data, ref offset, n); }
                case 0xdb: { int n = (int)ReadBE32(data, ref offset); return ReadStrN(data, ref offset, n); }
                // array
                case 0xdc: { int n = ReadBE16(data, ref offset); return ReadArrayN(data, ref offset, n); }
                case 0xdd: { int n = (int)ReadBE32(data, ref offset); return ReadArrayN(data, ref offset, n); }
                // map
                case 0xde: { int n = ReadBE16(data, ref offset); return ReadMapN(data, ref offset, n); }
                case 0xdf: { int n = (int)ReadBE32(data, ref offset); return ReadMapN(data, ref offset, n); }

                default: return null; // 跳过不认识的类型
            }
        }

        static Dictionary<string, object> ReadMapN(byte[] data, ref int offset, int count)
        {
            var map = new Dictionary<string, object>(count);
            for (int i = 0; i < count; i++)
            {
                var key = ReadValue(data, ref offset);
                var val = ReadValue(data, ref offset);
                if (key is string sk) map[sk] = val;
            }
            return map;
        }

        static List<object> ReadArrayN(byte[] data, ref int offset, int count)
        {
            var arr = new List<object>(count);
            for (int i = 0; i < count; i++)
                arr.Add(ReadValue(data, ref offset));
            return arr;
        }

        static string ReadStrN(byte[] data, ref int offset, int len)
        {
            var s = Encoding.UTF8.GetString(data, offset, len);
            offset += len;
            return s;
        }

        static byte[] ReadBytes(byte[] data, ref int offset, int len)
        {
            var result = new byte[len];
            Buffer.BlockCopy(data, offset, result, 0, len);
            offset += len;
            return result;
        }

        static int ReadBE16(byte[] data, ref int offset)
        {
            int v = (data[offset] << 8) | data[offset + 1];
            offset += 2;
            return v;
        }

        static uint ReadBE32(byte[] data, ref int offset)
        {
            uint v = ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) |
                     ((uint)data[offset + 2] << 8) | data[offset + 3];
            offset += 4;
            return v;
        }

        static int ReadBE32S(byte[] data, ref int offset)
        {
            return (int)ReadBE32(data, ref offset);
        }

        static ulong ReadBE64(byte[] data, ref int offset)
        {
            ulong hi = ReadBE32(data, ref offset);
            ulong lo = ReadBE32(data, ref offset);
            return (hi << 32) | lo;
        }

        static long ReadBE64S(byte[] data, ref int offset)
        {
            return (long)ReadBE64(data, ref offset);
        }

        static float ReadFloat32(byte[] data, ref int offset)
        {
            var bytes = new byte[4];
            Buffer.BlockCopy(data, offset, bytes, 0, 4);
            if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
            offset += 4;
            return BitConverter.ToSingle(bytes, 0);
        }

        static double ReadFloat64(byte[] data, ref int offset)
        {
            var bytes = new byte[8];
            Buffer.BlockCopy(data, offset, bytes, 0, 8);
            if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
            offset += 8;
            return BitConverter.ToDouble(bytes, 0);
        }

        // ===================== 辅助：从 map 取值 =====================

        public static string GetString(Dictionary<string, object> map, string key, string def = "")
        {
            if (map != null && map.TryGetValue(key, out var v) && v is string s) return s;
            return def;
        }

        public static int GetInt(Dictionary<string, object> map, string key, int def = 0)
        {
            if (map == null || !map.TryGetValue(key, out var v)) return def;
            if (v is int i) return i;
            if (v is long l) return (int)l;
            return def;
        }

        public static long GetLong(Dictionary<string, object> map, string key, long def = 0)
        {
            if (map == null || !map.TryGetValue(key, out var v)) return def;
            if (v is long l) return l;
            if (v is int i) return i;
            return def;
        }

        public static bool GetBool(Dictionary<string, object> map, string key, bool def = false)
        {
            if (map != null && map.TryGetValue(key, out var v) && v is bool b) return b;
            return def;
        }

        public static double GetDouble(Dictionary<string, object> map, string key, double def = 0)
        {
            if (map == null || !map.TryGetValue(key, out var v)) return def;
            if (v is double d) return d;
            if (v is float f) return f;
            if (v is int i) return i;
            if (v is long l) return l;
            return def;
        }

        public static byte[] GetBytes(Dictionary<string, object> map, string key)
        {
            if (map != null && map.TryGetValue(key, out var v) && v is byte[] b) return b;
            return null;
        }

        public static List<object> GetArray(Dictionary<string, object> map, string key)
        {
            if (map != null && map.TryGetValue(key, out var v) && v is List<object> arr) return arr;
            return null;
        }

        public static Dictionary<string, object> GetMap(Dictionary<string, object> map, string key)
        {
            if (map != null && map.TryGetValue(key, out var v) && v is Dictionary<string, object> m) return m;
            return null;
        }
    }
}
