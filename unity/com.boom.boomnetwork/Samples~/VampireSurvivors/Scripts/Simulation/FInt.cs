// BoomNetwork VampireSurvivors Demo — Deterministic Fixed-Point Number
//
// 22.10 fixed-point: 22-bit integer + 10-bit fraction.
// All arithmetic is integer-only — bit-level identical across all platforms.
// Sin/Cos use a hardcoded lookup table. Sqrt uses binary integer algorithm.
// NO floating-point at runtime. NO Math.Sin/Cos dependency.
//
// Range: ±2,097,151.999  Precision: ~0.001 (1/1024)

using System;
using System.Runtime.CompilerServices;

namespace BoomNetwork.Samples.VampireSurvivors
{
    public struct FInt : IEquatable<FInt>, IComparable<FInt>
    {
        public const int SHIFT = 10;
        public const int SCALE = 1 << SHIFT; // 1024

        public int Raw;

        // ==================== Construction ====================

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FInt(int raw) { Raw = raw; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FInt FromInt(int v) => new FInt(v << SHIFT);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FInt FromFloat(float v) => new FInt((int)(v * SCALE));

        // ==================== Conversion (Rendering only!) ====================

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float ToFloat() => Raw * (1f / SCALE);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ToInt() => Raw >> SHIFT;

        // ==================== Constants ====================

        public static readonly FInt Zero = new FInt(0);
        public static readonly FInt One = new FInt(SCALE);
        public static readonly FInt Half = new FInt(SCALE / 2);
        public static readonly FInt MinValue = new FInt(int.MinValue);
        public static readonly FInt MaxValue = new FInt(int.MaxValue);
        public static readonly FInt Epsilon = new FInt(1);
        public static readonly FInt Pi = new FInt(3217);       // π × 1024 ≈ 3216.99
        public static readonly FInt TwoPi = new FInt(6434);
        public static readonly FInt Deg2Rad = new FInt(18);    // (π/180) × 1024 ≈ 17.87

        // ==================== Operators ====================

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FInt operator +(FInt a, FInt b) => new FInt(a.Raw + b.Raw);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FInt operator -(FInt a, FInt b) => new FInt(a.Raw - b.Raw);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FInt operator -(FInt a) => new FInt(-a.Raw);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FInt operator *(FInt a, FInt b) => new FInt((int)((long)a.Raw * b.Raw >> SHIFT));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FInt operator /(FInt a, FInt b)
        {
            if (b.Raw == 0) return MaxValue;
            return new FInt((int)(((long)a.Raw << SHIFT) / b.Raw));
        }

        // FInt × int (no shift needed for the int side)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FInt operator *(FInt a, int b) => new FInt((int)((long)a.Raw * b));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FInt operator *(int a, FInt b) => new FInt((int)((long)a * b.Raw));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FInt operator /(FInt a, int b)
        {
            if (b == 0) return MaxValue;
            return new FInt(a.Raw / b);
        }

        // ==================== Comparison ====================

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator >(FInt a, FInt b) => a.Raw > b.Raw;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator <(FInt a, FInt b) => a.Raw < b.Raw;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator >=(FInt a, FInt b) => a.Raw >= b.Raw;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator <=(FInt a, FInt b) => a.Raw <= b.Raw;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator ==(FInt a, FInt b) => a.Raw == b.Raw;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator !=(FInt a, FInt b) => a.Raw != b.Raw;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(FInt other) => Raw == other.Raw;
        public override bool Equals(object obj) => obj is FInt f && Raw == f.Raw;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override int GetHashCode() => Raw;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int CompareTo(FInt other) => Raw.CompareTo(other.Raw);

        // ==================== Math ====================

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FInt Abs(FInt v) => new FInt(v.Raw < 0 ? -v.Raw : v.Raw);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FInt Min(FInt a, FInt b) => a.Raw < b.Raw ? a : b;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FInt Max(FInt a, FInt b) => a.Raw > b.Raw ? a : b;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FInt Clamp(FInt v, FInt min, FInt max)
        {
            if (v.Raw < min.Raw) return min;
            if (v.Raw > max.Raw) return max;
            return v;
        }

        /// <summary>Linear interpolation. t is in [0,1] fixed-point.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FInt Lerp(FInt a, FInt b, FInt t)
        {
            return new FInt(a.Raw + (int)(((long)(b.Raw - a.Raw) * t.Raw) >> SHIFT));
        }

        /// <summary>Move value towards target by at most maxDelta per call.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FInt MoveTowards(FInt current, FInt target, FInt maxDelta)
        {
            int diff = target.Raw - current.Raw;
            if (diff > maxDelta.Raw) return new FInt(current.Raw + maxDelta.Raw);
            if (diff < -maxDelta.Raw) return new FInt(current.Raw - maxDelta.Raw);
            return target;
        }

        /// <summary>Integer square root in fixed-point. Binary algorithm — pure bitwise, no division.</summary>
        public static FInt Sqrt(FInt v)
        {
            if (v.Raw <= 0) return Zero;

            // Binary square root: result.Raw = isqrt(v.Raw << SHIFT)
            ulong val = (ulong)v.Raw << SHIFT;
            ulong result = 0;
            ulong bit = 1UL << 30; // start from highest even bit

            while (bit > val) bit >>= 2;
            while (bit != 0)
            {
                ulong t = result + bit;
                result >>= 1;
                if (val >= t)
                {
                    val -= t;
                    result += bit;
                }
                bit >>= 2;
            }
            return new FInt((int)result);
        }

        /// <summary>1 / sqrt(v). Avoids separate Sqrt + division. Deterministic.</summary>
        public static FInt InvSqrt(FInt v)
        {
            if (v.Raw <= 0) return Zero;

            // Binary square root then invert
            ulong val = (ulong)v.Raw << SHIFT;
            ulong result = 0;
            ulong bit = 1UL << 30;

            while (bit > val) bit >>= 2;
            while (bit != 0)
            {
                ulong t = result + bit;
                result >>= 1;
                if (val >= t)
                {
                    val -= t;
                    result += bit;
                }
                bit >>= 2;
            }
            if (result == 0) return MaxValue;
            return new FInt((int)(((long)SCALE * SCALE) / (long)result));
        }

        /// <summary>Distance squared between two 2D points. Use for comparison to avoid Sqrt.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FInt DistanceSqr(FInt ax, FInt az, FInt bx, FInt bz)
        {
            long dx = ax.Raw - bx.Raw;
            long dz = az.Raw - bz.Raw;
            return new FInt((int)((dx * dx + dz * dz) >> SHIFT));
        }

        /// <summary>Magnitude squared of a 2D vector. Use for comparison to avoid Sqrt.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FInt LengthSqr(FInt x, FInt z)
        {
            return new FInt((int)(((long)x.Raw * x.Raw + (long)z.Raw * z.Raw) >> SHIFT));
        }

        // ==================== Trigonometry (Hardcoded Lookup Table) ====================

        // 1024 entries covering [0°, 360°). Hardcoded to guarantee bit-identical
        // values across Mono / IL2CPP / .NET — no Math.Sin dependency at runtime.
        // Resolution: 360/1024 ≈ 0.35° per step. Values are sin(i * 2π / 1024) * 1024.
        const int TABLE_SIZE = 1024;
        const int TABLE_MASK = TABLE_SIZE - 1;

        // @formatter:off
        static readonly int[] SinTable = {
                0,     6,    13,    19,    25,    31,    38,    44,    50,    57,    63,    69,    75,    82,    88,    94,
              100,   107,   113,   119,   125,   132,   138,   144,   150,   156,   163,   169,   175,   181,   187,   194,
              200,   206,   212,   218,   224,   230,   237,   243,   249,   255,   261,   267,   273,   279,   285,   291,
              297,   303,   309,   315,   321,   327,   333,   339,   345,   351,   357,   363,   369,   374,   380,   386,
              392,   398,   403,   409,   415,   421,   426,   432,   438,   443,   449,   455,   460,   466,   472,   477,
              483,   488,   494,   499,   505,   510,   516,   521,   526,   532,   537,   543,   548,   553,   558,   564,
              569,   574,   579,   584,   590,   595,   600,   605,   610,   615,   620,   625,   630,   635,   640,   645,
              650,   654,   659,   664,   669,   674,   678,   683,   688,   692,   697,   702,   706,   711,   715,   720,
              724,   729,   733,   737,   742,   746,   750,   755,   759,   763,   767,   771,   775,   779,   784,   788,
              792,   796,   799,   803,   807,   811,   815,   819,   822,   826,   830,   834,   837,   841,   844,   848,
              851,   855,   858,   862,   865,   868,   872,   875,   878,   882,   885,   888,   891,   894,   897,   900,
              903,   906,   909,   912,   915,   917,   920,   923,   926,   928,   931,   934,   936,   939,   941,   944,
              946,   948,   951,   953,   955,   958,   960,   962,   964,   966,   968,   970,   972,   974,   976,   978,
              980,   982,   983,   985,   987,   989,   990,   992,   993,   995,   996,   998,   999,  1000,  1002,  1003,
             1004,  1006,  1007,  1008,  1009,  1010,  1011,  1012,  1013,  1014,  1015,  1016,  1016,  1017,  1018,  1018,
             1019,  1020,  1020,  1021,  1021,  1022,  1022,  1022,  1023,  1023,  1023,  1024,  1024,  1024,  1024,  1024,
             1024,  1024,  1024,  1024,  1024,  1024,  1023,  1023,  1023,  1022,  1022,  1022,  1021,  1021,  1020,  1020,
             1019,  1018,  1018,  1017,  1016,  1016,  1015,  1014,  1013,  1012,  1011,  1010,  1009,  1008,  1007,  1006,
             1004,  1003,  1002,  1000,   999,   998,   996,   995,   993,   992,   990,   989,   987,   985,   983,   982,
              980,   978,   976,   974,   972,   970,   968,   966,   964,   962,   960,   958,   955,   953,   951,   948,
              946,   944,   941,   939,   936,   934,   931,   928,   926,   923,   920,   917,   915,   912,   909,   906,
              903,   900,   897,   894,   891,   888,   885,   882,   878,   875,   872,   868,   865,   862,   858,   855,
              851,   848,   844,   841,   837,   834,   830,   826,   822,   819,   815,   811,   807,   803,   799,   796,
              792,   788,   784,   779,   775,   771,   767,   763,   759,   755,   750,   746,   742,   737,   733,   729,
              724,   720,   715,   711,   706,   702,   697,   692,   688,   683,   678,   674,   669,   664,   659,   654,
              650,   645,   640,   635,   630,   625,   620,   615,   610,   605,   600,   595,   590,   584,   579,   574,
              569,   564,   558,   553,   548,   543,   537,   532,   526,   521,   516,   510,   505,   499,   494,   488,
              483,   477,   472,   466,   460,   455,   449,   443,   438,   432,   426,   421,   415,   409,   403,   398,
              392,   386,   380,   374,   369,   363,   357,   351,   345,   339,   333,   327,   321,   315,   309,   303,
              297,   291,   285,   279,   273,   267,   261,   255,   249,   243,   237,   230,   224,   218,   212,   206,
              200,   194,   187,   181,   175,   169,   163,   156,   150,   144,   138,   132,   125,   119,   113,   107,
              100,    94,    88,    82,    75,    69,    63,    57,    50,    44,    38,    31,    25,    19,    13,     6,
                0,    -6,   -13,   -19,   -25,   -31,   -38,   -44,   -50,   -57,   -63,   -69,   -75,   -82,   -88,   -94,
             -100,  -107,  -113,  -119,  -125,  -132,  -138,  -144,  -150,  -156,  -163,  -169,  -175,  -181,  -187,  -194,
             -200,  -206,  -212,  -218,  -224,  -230,  -237,  -243,  -249,  -255,  -261,  -267,  -273,  -279,  -285,  -291,
             -297,  -303,  -309,  -315,  -321,  -327,  -333,  -339,  -345,  -351,  -357,  -363,  -369,  -374,  -380,  -386,
             -392,  -398,  -403,  -409,  -415,  -421,  -426,  -432,  -438,  -443,  -449,  -455,  -460,  -466,  -472,  -477,
             -483,  -488,  -494,  -499,  -505,  -510,  -516,  -521,  -526,  -532,  -537,  -543,  -548,  -553,  -558,  -564,
             -569,  -574,  -579,  -584,  -590,  -595,  -600,  -605,  -610,  -615,  -620,  -625,  -630,  -635,  -640,  -645,
             -650,  -654,  -659,  -664,  -669,  -674,  -678,  -683,  -688,  -692,  -697,  -702,  -706,  -711,  -715,  -720,
             -724,  -729,  -733,  -737,  -742,  -746,  -750,  -755,  -759,  -763,  -767,  -771,  -775,  -779,  -784,  -788,
             -792,  -796,  -799,  -803,  -807,  -811,  -815,  -819,  -822,  -826,  -830,  -834,  -837,  -841,  -844,  -848,
             -851,  -855,  -858,  -862,  -865,  -868,  -872,  -875,  -878,  -882,  -885,  -888,  -891,  -894,  -897,  -900,
             -903,  -906,  -909,  -912,  -915,  -917,  -920,  -923,  -926,  -928,  -931,  -934,  -936,  -939,  -941,  -944,
             -946,  -948,  -951,  -953,  -955,  -958,  -960,  -962,  -964,  -966,  -968,  -970,  -972,  -974,  -976,  -978,
             -980,  -982,  -983,  -985,  -987,  -989,  -990,  -992,  -993,  -995,  -996,  -998,  -999, -1000, -1002, -1003,
            -1004, -1006, -1007, -1008, -1009, -1010, -1011, -1012, -1013, -1014, -1015, -1016, -1016, -1017, -1018, -1018,
            -1019, -1020, -1020, -1021, -1021, -1022, -1022, -1022, -1023, -1023, -1023, -1024, -1024, -1024, -1024, -1024,
            -1024, -1024, -1024, -1024, -1024, -1024, -1023, -1023, -1023, -1022, -1022, -1022, -1021, -1021, -1020, -1020,
            -1019, -1018, -1018, -1017, -1016, -1016, -1015, -1014, -1013, -1012, -1011, -1010, -1009, -1008, -1007, -1006,
            -1004, -1003, -1002, -1000,  -999,  -998,  -996,  -995,  -993,  -992,  -990,  -989,  -987,  -985,  -983,  -982,
             -980,  -978,  -976,  -974,  -972,  -970,  -968,  -966,  -964,  -962,  -960,  -958,  -955,  -953,  -951,  -948,
             -946,  -944,  -941,  -939,  -936,  -934,  -931,  -928,  -926,  -923,  -920,  -917,  -915,  -912,  -909,  -906,
             -903,  -900,  -897,  -894,  -891,  -888,  -885,  -882,  -878,  -875,  -872,  -868,  -865,  -862,  -858,  -855,
             -851,  -848,  -844,  -841,  -837,  -834,  -830,  -826,  -822,  -819,  -815,  -811,  -807,  -803,  -799,  -796,
             -792,  -788,  -784,  -779,  -775,  -771,  -767,  -763,  -759,  -755,  -750,  -746,  -742,  -737,  -733,  -729,
             -724,  -720,  -715,  -711,  -706,  -702,  -697,  -692,  -688,  -683,  -678,  -674,  -669,  -664,  -659,  -654,
             -650,  -645,  -640,  -635,  -630,  -625,  -620,  -615,  -610,  -605,  -600,  -595,  -590,  -584,  -579,  -574,
             -569,  -564,  -558,  -553,  -548,  -543,  -537,  -532,  -526,  -521,  -516,  -510,  -505,  -499,  -494,  -488,
             -483,  -477,  -472,  -466,  -460,  -455,  -449,  -443,  -438,  -432,  -426,  -421,  -415,  -409,  -403,  -398,
             -392,  -386,  -380,  -374,  -369,  -363,  -357,  -351,  -345,  -339,  -333,  -327,  -321,  -315,  -309,  -303,
             -297,  -291,  -285,  -279,  -273,  -267,  -261,  -255,  -249,  -243,  -237,  -230,  -224,  -218,  -212,  -206,
             -200,  -194,  -187,  -181,  -175,  -169,  -163,  -156,  -150,  -144,  -138,  -132,  -125,  -119,  -113,  -107,
             -100,   -94,   -88,   -82,   -75,   -69,   -63,   -57,   -50,   -44,   -38,   -31,   -25,   -19,   -13,    -6,
        };
        // @formatter:on

        /// <summary>Sin of angle in degrees. Deterministic lookup.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FInt SinDeg(FInt degrees)
        {
            // idx = deg * TABLE_SIZE / (360 * SCALE)
            long idx = ((long)degrees.Raw * TABLE_SIZE) / (360 * SCALE);
            return new FInt(SinTable[(int)(idx & TABLE_MASK)]);
        }

        /// <summary>Cos of angle in degrees. Deterministic lookup.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FInt CosDeg(FInt degrees)
        {
            // cos(x) = sin(x + 90)
            long idx = ((long)(degrees.Raw + 90 * SCALE) * TABLE_SIZE) / (360 * SCALE);
            return new FInt(SinTable[(int)(idx & TABLE_MASK)]);
        }

        /// <summary>Sin of angle in radians (FInt). Deterministic lookup.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FInt Sin(FInt radians)
        {
            // idx = rad * TABLE_SIZE / (2π), 2π in fixed-point = 6434
            long idx = ((long)radians.Raw * TABLE_SIZE) / 6434;
            return new FInt(SinTable[(int)(idx & TABLE_MASK)]);
        }

        /// <summary>Cos of angle in radians (FInt). Deterministic lookup.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FInt Cos(FInt radians)
        {
            long idx = ((long)radians.Raw * TABLE_SIZE) / 6434 + TABLE_SIZE / 4; // +90°
            return new FInt(SinTable[(int)(idx & TABLE_MASK)]);
        }

        // ==================== Debug ====================

        public override string ToString() => ToFloat().ToString("F3");
    }
}
