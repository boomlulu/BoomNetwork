// BoomNetwork VampireSurvivors Demo — Input Encoding (Fixed-Point)
//
// 4-byte input: [sbyte dirX] [sbyte dirZ] [byte ability] [byte reserved]
// Decode returns FInt values.

namespace BoomNetwork.Samples.VampireSurvivors
{
    public static class VSInput
    {
        public const int InputSize = 4;
        static readonly FInt _inv127 = FInt.One / FInt.FromInt(127);

        public static void Encode(byte[] buf, float dirX, float dirZ, byte abilityMask = 0)
        {
            int ix = (int)(dirX * 127f);
            int iz = (int)(dirZ * 127f);
            if (ix < -128) ix = -128; else if (ix > 127) ix = 127;
            if (iz < -128) iz = -128; else if (iz > 127) iz = 127;
            buf[0] = (byte)(sbyte)ix;
            buf[1] = (byte)(sbyte)iz;
            buf[2] = abilityMask;
            buf[3] = 0;
        }

        public static void Decode(byte[] buf, int offset,
            out FInt dirX, out FInt dirZ, out byte abilityMask)
        {
            // sbyte / 127 in fixed-point
            int rawX = (sbyte)buf[offset];
            int rawZ = (sbyte)buf[offset + 1];
            dirX = FInt.FromInt(rawX) * _inv127;
            dirZ = FInt.FromInt(rawZ) * _inv127;
            abilityMask = buf[offset + 2];
        }
    }
}
