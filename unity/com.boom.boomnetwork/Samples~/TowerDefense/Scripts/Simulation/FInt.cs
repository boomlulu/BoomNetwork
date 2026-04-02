// BoomNetwork TowerDefense Demo — Deterministic Fixed-Point Number
//
// 22.10 fixed-point: 22-bit integer + 10-bit fraction.
// Copied from VampireSurvivors Demo with namespace change.
// NO floating-point at runtime. NO Math.Sin/Cos dependency.

using System;
using System.Runtime.CompilerServices;

namespace BoomNetwork.Samples.TowerDefense
{
    public struct FInt : IEquatable<FInt>, IComparable<FInt>
    {
        public const int SHIFT = 10;
        public const int SCALE = 1 << SHIFT; // 1024

        public int Raw;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FInt(int raw) { Raw = raw; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FInt FromInt(int v) => new FInt(v << SHIFT);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FInt FromFloat(float v) => new FInt((int)(v * SCALE));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float ToFloat() => Raw * (1f / SCALE);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ToInt() => Raw >> SHIFT;

        public static readonly FInt Zero = new FInt(0);
        public static readonly FInt One = new FInt(SCALE);
        public static readonly FInt Half = new FInt(SCALE / 2);
        public static readonly FInt MinValue = new FInt(int.MinValue);
        public static readonly FInt MaxValue = new FInt(int.MaxValue);
        public static readonly FInt Epsilon = new FInt(1);

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

        /// <summary>Integer square root in fixed-point.</summary>
        public static FInt Sqrt(FInt v)
        {
            if (v.Raw <= 0) return Zero;
            ulong val = (ulong)v.Raw << SHIFT;
            ulong result = 0;
            ulong bit = 1UL << 30;
            while (bit > val) bit >>= 2;
            while (bit != 0)
            {
                ulong t = result + bit;
                result >>= 1;
                if (val >= t) { val -= t; result += bit; }
                bit >>= 2;
            }
            return new FInt((int)result);
        }

        /// <summary>1 / sqrt(v). Deterministic.</summary>
        public static FInt InvSqrt(FInt v)
        {
            if (v.Raw <= 0) return Zero;
            ulong val = (ulong)v.Raw << SHIFT;
            ulong result = 0;
            ulong bit = 1UL << 30;
            while (bit > val) bit >>= 2;
            while (bit != 0)
            {
                ulong t = result + bit;
                result >>= 1;
                if (val >= t) { val -= t; result += bit; }
                bit >>= 2;
            }
            if (result == 0) return MaxValue;
            return new FInt((int)(((long)SCALE * SCALE) / (long)result));
        }

        /// <summary>Distance squared between two 2D points.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FInt DistanceSqr(FInt ax, FInt az, FInt bx, FInt bz)
        {
            long dx = ax.Raw - bx.Raw;
            long dz = az.Raw - bz.Raw;
            return new FInt((int)((dx * dx + dz * dz) >> SHIFT));
        }

        public override string ToString() => ToFloat().ToString("F3");
    }
}
