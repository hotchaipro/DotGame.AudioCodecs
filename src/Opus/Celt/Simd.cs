// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace DotGame.AudioCodecs.Opus.Celt;

/// <summary>
/// Fixed-point operations on four 32-bit lanes, each exactly the scalar macro in every lane, so a
/// vectorised loop gives the same samples as the C. Complex numbers are kept interleaved, (r, i)
/// in two lanes, so a vector holds two of them.
/// </summary>
/// <remarks>
/// Each has a NEON form (CoreCLR on arm64), an SSE form where it helps (x86), and a portable one
/// for the rest: Mono (Android) accelerates <see cref="Vector128"/> but not the Arm intrinsics, and
/// its LLVM back end turns the portable widening forms into NEON's widening multiplies. Compiled
/// with <c>OPUS_PORTABLE_VECTORS</c>, every platform takes the portable forms, so the checks can
/// cover them where the intrinsics exist; a constant, so no build pays for a test at run time.
/// </remarks>
internal static class Simd
{
#if OPUS_PORTABLE_VECTORS
    private const bool Portable = true;
#else
    private const bool Portable = false;
#endif

    private static bool Neon => AdvSimd.Arm64.IsSupported && !Portable;

    private static bool Sse4 => Sse41.IsSupported && !Portable;

    /// <summary>
    /// MULT16_32_Q15 in each lane: <paramref name="a"/> holds 16-bit values (sign-extended),
    /// <paramref name="b"/> 32-bit ones; the product's bits 15 to 46, as C's 64-bit form gives.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<int> MULT16_32_Q15(Vector128<int> a, Vector128<int> b)
    {
        if (Neon)
        {
            // The widened products, shifted right by 15 and narrowed to their low 32 bits
            Vector128<long> lo = AdvSimd.MultiplyWideningLower(a.GetLower(), b.GetLower());
            Vector128<long> hi = AdvSimd.MultiplyWideningUpper(a, b);
            return AdvSimd.ShiftRightLogicalNarrowingUpper(AdvSimd.ShiftRightLogicalNarrowingLower(lo, 15), hi, 15);
        }
        if (!Sse4)
        {
            return WideningProductShifted(a, b, 15);
        }
        // fixed_generic.h's 32-bit form, which equals the 64-bit one in two's complement:
        // (a*(b>>16))<<1 + (a*(b&0xffff))>>15, where a*(b&0xffff) fits 32 bits
        Vector128<int> high = (b >> 16);
        Vector128<int> low = b & Vector128.Create(0xFFFF);
        return ((a * high) << 1) + ((a * low) >> 15);
    }

    /// <summary>MULT16_32_Q16 in each lane: as <see cref="MULT16_32_Q15"/>, the product's bits 16 to 47.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<int> MULT16_32_Q16(Vector128<int> a, Vector128<int> b)
    {
        if (Neon)
        {
            Vector128<long> lo = AdvSimd.MultiplyWideningLower(a.GetLower(), b.GetLower());
            Vector128<long> hi = AdvSimd.MultiplyWideningUpper(a, b);
            return AdvSimd.ShiftRightLogicalNarrowingUpper(AdvSimd.ShiftRightLogicalNarrowingLower(lo, 16), hi, 16);
        }
        if (!Sse4)
        {
            return WideningProductShifted(a, b, 16);
        }
        // a*(b>>16) + (a*(b&0xffff))>>16, which equals the 64-bit form: the low part's product fits 32 bits
        return (a * (b >> 16)) + ((a * (b & Vector128.Create(0xFFFF))) >> 16);
    }

    /// <summary>Whether <see cref="MULT32_32_Q31"/> is vectorised here; otherwise callers keep their scalar loops.</summary>
    internal static bool HasMULT32_32_Q31 => Vector128.IsHardwareAccelerated;

    /// <summary>MULT32_32_Q31 in each lane: the 64-bit product's bits 31 to 62.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<int> MULT32_32_Q31(Vector128<int> a, Vector128<int> b)
    {
        if (Neon)
        {
            Vector128<long> lo = AdvSimd.MultiplyWideningLower(a.GetLower(), b.GetLower());
            Vector128<long> hi = AdvSimd.MultiplyWideningUpper(a, b);
            return AdvSimd.ShiftRightLogicalNarrowingUpper(AdvSimd.ShiftRightLogicalNarrowingLower(lo, 31), hi, 31);
        }
        if (!Sse4)
        {
            return WideningProductShifted(a, b, 31);
        }
        // SSE4.1: the even lanes' 64-bit products, then the odd lanes' (moved down), shifted and
        // their low 32 bits put back in place
        Vector128<long> even = Sse41.Multiply(a, b);
        Vector128<long> odd = Sse41.Multiply(Sse2.ShiftRightLogical(a.AsInt64(), 32).AsInt32(), Sse2.ShiftRightLogical(b.AsInt64(), 32).AsInt32());
        Vector128<int> e = Sse2.ShiftRightLogical(even, 31).AsInt32();
        Vector128<int> o = Sse2.ShiftLeftLogical(Sse2.ShiftRightLogical(odd, 31), 32).AsInt32();
        return Sse41.Blend(e.AsInt16(), o.AsInt16(), 0b1100_1100).AsInt32();
    }

    // The portable form of a widening product's bits shift to shift + 31: sign-extended to 64 bits,
    // multiplied, shifted and narrowed to the low 32 bits (LLVM makes it SMULL and SHRN)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> WideningProductShifted(Vector128<int> a, Vector128<int> b, int shift)
    {
        Vector128<long> lo = Vector128.WidenLower(a) * Vector128.WidenLower(b);
        Vector128<long> hi = Vector128.WidenUpper(a) * Vector128.WidenUpper(b);
        return Vector128.Narrow((lo >>> shift), (hi >>> shift));
    }

    /// <summary>
    /// The sum of x[i] * (long)y[i] over <paramref name="len"/> values (any order: the 64-bit sum is
    /// exact), the products widened in vectors.
    /// </summary>
    internal static long InnerProduct64(ReadOnlySpan<int> x, ReadOnlySpan<int> y, int len)
    {
        int i = 0;
        long sum = 0;
        if (Neon)
        {
            Vector128<long> acc = Vector128<long>.Zero;
            for (; i + 4 <= len; i += 4)
            {
                Vector128<int> a = Vector128.Create(x.Slice(i, 4));
                Vector128<int> b = Vector128.Create(y.Slice(i, 4));
                acc = AdvSimd.MultiplyWideningLowerAndAdd(acc, a.GetLower(), b.GetLower());
                acc = AdvSimd.MultiplyWideningUpperAndAdd(acc, a, b);
            }
            sum = acc.GetElement(0) + acc.GetElement(1);
        }
        else if (Sse4)
        {
            Vector128<long> acc = Vector128<long>.Zero;
            for (; i + 4 <= len; i += 4)
            {
                Vector128<int> a = Vector128.Create(x.Slice(i, 4));
                Vector128<int> b = Vector128.Create(y.Slice(i, 4));
                acc += Sse41.Multiply(a, b);
                acc += Sse41.Multiply(Sse2.ShiftRightLogical(a.AsInt64(), 32).AsInt32(), Sse2.ShiftRightLogical(b.AsInt64(), 32).AsInt32());
            }
            sum = acc.GetElement(0) + acc.GetElement(1);
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            Vector128<long> acc = Vector128<long>.Zero;
            for (; i + 4 <= len; i += 4)
            {
                Vector128<int> a = Vector128.Create(x.Slice(i, 4));
                Vector128<int> b = Vector128.Create(y.Slice(i, 4));
                acc += Vector128.WidenLower(a) * Vector128.WidenLower(b);
                acc += Vector128.WidenUpper(a) * Vector128.WidenUpper(b);
            }
            sum = acc.GetElement(0) + acc.GetElement(1);
        }
        for (; i < len; i++)
        {
            sum += x[i] * (long)y[i];
        }
        return sum;
    }

    /// <summary>(short) in each lane: the low 16 bits, sign-extended.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<int> ToInt16(Vector128<int> a) => ((a << 16) >> 16);

    /// <summary>MULT16_16_Q15 in each lane: both operands cast to 16 bits, multiplied, shifted right by 15.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<int> MULT16_16_Q15(Vector128<int> a, Vector128<int> b) =>
        ((ToInt16(a) * ToInt16(b)) >> 15);

    /// <summary>VSHR32 by one count in each lane: right when it's positive, left when it's negative.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<int> VSHR32(Vector128<int> a, int shift) =>
        shift > 0 ? (a >> shift) : (a << -shift);

    /// <summary>Four 16-bit values, sign-extended to 32 bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<int> LoadWiden(ReadOnlySpan<short> s) =>
        Vector128.WidenLower(Vector64.Create(s.Slice(0, 4)).ToVector128());

    /// <summary>Four 32-bit values, the last first.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<int> Reverse(Vector128<int> v) => Vector128.Shuffle(v, Vector128.Create(3, 2, 1, 0));

    /// <summary>The even and odd elements of eight: (a0, a2, b0, b2) and (a1, a3, b1, b3).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Unzip(Vector128<int> a, Vector128<int> b, out Vector128<int> even, out Vector128<int> odd)
    {
        if (Neon)
        {
            even = AdvSimd.Arm64.UnzipEven(a, b);
            odd = AdvSimd.Arm64.UnzipOdd(a, b);
        }
        else if (Sse.IsSupported && !Portable)
        {
            even = Sse.Shuffle(a.AsSingle(), b.AsSingle(), 0b10_00_10_00).AsInt32();
            odd = Sse.Shuffle(a.AsSingle(), b.AsSingle(), 0b11_01_11_01).AsInt32();
        }
        else
        {
            // Each vector's even and odd pairs side by side, then their halves joined (UZP1 and UZP2)
            Vector128<int> sa = Vector128.Shuffle(a, Vector128.Create(0, 2, 1, 3));
            Vector128<int> sb = Vector128.Shuffle(b, Vector128.Create(0, 2, 1, 3));
            even = Vector128.Create(sa.GetLower(), sb.GetLower());
            odd = Vector128.Create(sa.GetUpper(), sb.GetUpper());
        }
    }

    /// <summary>Interleaves four even and four odd elements: (e0, o0, e1, o1) and (e2, o2, e3, o3).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Zip(Vector128<int> even, Vector128<int> odd, out Vector128<int> lo, out Vector128<int> hi)
    {
        if (Neon)
        {
            lo = AdvSimd.Arm64.ZipLow(even, odd);
            hi = AdvSimd.Arm64.ZipHigh(even, odd);
        }
        else if (Sse2.IsSupported && !Portable)
        {
            lo = Sse2.UnpackLow(even, odd);
            hi = Sse2.UnpackHigh(even, odd);
        }
        else
        {
            // The halves joined, then each one's pairs interleaved (ZIP1 and ZIP2)
            lo = Vector128.Shuffle(Vector128.Create(even.GetLower(), odd.GetLower()), Vector128.Create(0, 2, 1, 3));
            hi = Vector128.Shuffle(Vector128.Create(even.GetUpper(), odd.GetUpper()), Vector128.Create(0, 2, 1, 3));
        }
    }

    /// <summary>(r, i) to (i, r) in each complex.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<int> Swap(Vector128<int> v) => Vector128.Shuffle(v, Vector128.Create(1, 0, 3, 2));

    /// <summary>(r, i) to (r, -i) in each complex.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<int> NegI(Vector128<int> v) => v * Vector128.Create(1, -1, 1, -1);

    /// <summary>(r, i) to (-r, i) in each complex.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<int> NegR(Vector128<int> v) => v * Vector128.Create(-1, 1, -1, 1);

    /// <summary>
    /// C_MUL of two complexes by their twiddles: (a.r*w.r - a.i*w.i, a.r*w.i + a.i*w.r), each
    /// product S_MUL'd (MULT16_32_Q15 by the 16-bit twiddle) before it's added, as the C does.
    /// <paramref name="wr"/> is (w.r, w.r, w'.r, w'.r) and <paramref name="wi"/> (w.i, w.i, w'.i, w'.i).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<int> C_MUL(Vector128<int> a, Vector128<int> wr, Vector128<int> wi) =>
        MULT16_32_Q15(wr, a) + NegR(MULT16_32_Q15(wi, Swap(a)));

    /// <summary>
    /// The sum of <c>x[j] * y[j]</c> for <c>j</c> below <paramref name="len"/>, in 32 bits
    /// wrapping as C's MAC16_16 does: integer sums are the same in any order, so this is the C's
    /// loop exactly.
    /// </summary>
    internal static int InnerProduct16(ReadOnlySpan<short> x, ReadOnlySpan<short> y, int len)
    {
        if (((uint)len > (uint)x.Length) || ((uint)len > (uint)y.Length))
        {
            throw new ArgumentOutOfRangeException(nameof(len));
        }

        ref short xs = ref MemoryMarshal.GetReference(x);
        ref short ys = ref MemoryMarshal.GetReference(y);
        int j = 0;
        int sum = 0;
        if (Vector128.IsHardwareAccelerated && (len >= 8))
        {
            var acc = Vector128<int>.Zero;
            for (; j + 8 <= len; j += 8)
            {
                acc = MultiplyAddWidening(acc, Vector128.LoadUnsafe(ref xs, (nuint)j), Vector128.LoadUnsafe(ref ys, (nuint)j));
            }

            sum = Vector128.Sum(acc);
        }

        for (; j < len; j++)
        {
            sum += Unsafe.Add(ref xs, j) * Unsafe.Add(ref ys, j);
        }

        return sum;
    }

    /// <summary>
    /// The pitch correlation's kernel for four lags at once: <c>sums[k]</c> is the sum of
    /// <c>x[j] * y[j + k]</c> for <c>j</c> below <paramref name="len"/> and <c>k</c> from 0 to 3, in
    /// 32 bits wrapping (the C's xcorr_kernel, which adds the same products).
    /// </summary>
    internal static void CrossCorrelate4(ReadOnlySpan<short> x, ReadOnlySpan<short> y, int len, Span<int> sums)
    {
        if (((uint)len > (uint)x.Length) || ((uint)(len + 3) > (uint)y.Length) || (sums.Length < 4))
        {
            throw new ArgumentOutOfRangeException(nameof(len));
        }

        ref short xs = ref MemoryMarshal.GetReference(x);
        ref short ys = ref MemoryMarshal.GetReference(y);
        int j = 0;
        int s0 = 0, s1 = 0, s2 = 0, s3 = 0;
        if (Vector128.IsHardwareAccelerated && (len >= 8))
        {
            var a0 = Vector128<int>.Zero;
            var a1 = Vector128<int>.Zero;
            var a2 = Vector128<int>.Zero;
            var a3 = Vector128<int>.Zero;
            for (; j + 8 <= len; j += 8)
            {
                var xv = Vector128.LoadUnsafe(ref xs, (nuint)j);
                a0 = MultiplyAddWidening(a0, xv, Vector128.LoadUnsafe(ref ys, (nuint)j));
                a1 = MultiplyAddWidening(a1, xv, Vector128.LoadUnsafe(ref ys, (nuint)(j + 1)));
                a2 = MultiplyAddWidening(a2, xv, Vector128.LoadUnsafe(ref ys, (nuint)(j + 2)));
                a3 = MultiplyAddWidening(a3, xv, Vector128.LoadUnsafe(ref ys, (nuint)(j + 3)));
            }

            s0 = Vector128.Sum(a0);
            s1 = Vector128.Sum(a1);
            s2 = Vector128.Sum(a2);
            s3 = Vector128.Sum(a3);
        }

        for (; j < len; j++)
        {
            int xj = Unsafe.Add(ref xs, j);
            s0 += xj * Unsafe.Add(ref ys, j);
            s1 += xj * Unsafe.Add(ref ys, j + 1);
            s2 += xj * Unsafe.Add(ref ys, j + 2);
            s3 += xj * Unsafe.Add(ref ys, j + 3);
        }

        sums[0] = s0;
        sums[1] = s1;
        sums[2] = s2;
        sums[3] = s3;
    }

    // acc plus the products of x's and y's 16-bit lanes, pairwise: NEON's widening multiply-adds,
    // SSE2's multiply-add of adjacent pairs (whose one overflow, two products of -32768, wraps as
    // the 32-bit sum does), or widened products
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> MultiplyAddWidening(Vector128<int> acc, Vector128<short> x, Vector128<short> y)
    {
        if (Neon)
        {
            acc = AdvSimd.MultiplyWideningLowerAndAdd(acc, x.GetLower(), y.GetLower());
            return AdvSimd.MultiplyWideningUpperAndAdd(acc, x, y);
        }

        if (Sse2.IsSupported && !Portable)
        {
            return acc + Sse2.MultiplyAddAdjacent(x, y);
        }

        var (xLow, xHigh) = Vector128.Widen(x);
        var (yLow, yHigh) = Vector128.Widen(y);
        return acc + (xLow * yLow) + (xHigh * yHigh);
    }

    /// <summary>
    /// celt_maxabs32: the larger of the largest value and the negated smallest (each at least 0),
    /// over <paramref name="len"/> values, as the C's loop gives it.
    /// </summary>
    internal static int MaxAbs32(ReadOnlySpan<int> x, int len)
    {
        x = x.Slice(0, len);
        ref int xs = ref MemoryMarshal.GetReference(x);
        int i = 0;
        int maxval = 0;
        int minval = 0;
        if (Vector128.IsHardwareAccelerated && (len >= 4))
        {
            var max = Vector128<int>.Zero;
            var min = Vector128<int>.Zero;
            for (; i + 4 <= len; i += 4)
            {
                var v = Vector128.LoadUnsafe(ref xs, (nuint)i);
                max = Vector128.Max(max, v);
                min = Vector128.Min(min, v);
            }

            for (int lane = 0; lane < 4; lane++)
            {
                maxval = Math.Max(maxval, max.GetElement(lane));
                minval = Math.Min(minval, min.GetElement(lane));
            }
        }

        for (; i < len; i++)
        {
            int v = Unsafe.Add(ref xs, i);
            maxval = Math.Max(maxval, v);
            minval = Math.Min(minval, v);
        }

        return Math.Max(maxval, -minval);
    }

    /// <summary>celt_maxabs16: <see cref="MaxAbs32"/> for 16-bit values.</summary>
    internal static int MaxAbs16(ReadOnlySpan<short> x, int len)
    {
        x = x.Slice(0, len);
        ref short xs = ref MemoryMarshal.GetReference(x);
        int i = 0;
        int maxval = 0;
        int minval = 0;
        if (Vector128.IsHardwareAccelerated && (len >= 8))
        {
            var max = Vector128<short>.Zero;
            var min = Vector128<short>.Zero;
            for (; i + 8 <= len; i += 8)
            {
                var v = Vector128.LoadUnsafe(ref xs, (nuint)i);
                max = Vector128.Max(max, v);
                min = Vector128.Min(min, v);
            }

            for (int lane = 0; lane < 8; lane++)
            {
                maxval = Math.Max(maxval, max.GetElement(lane));
                minval = Math.Min(minval, min.GetElement(lane));
            }
        }

        for (; i < len; i++)
        {
            int v = Unsafe.Add(ref xs, i);
            maxval = Math.Max(maxval, v);
            minval = Math.Min(minval, v);
        }

        return Math.Max(maxval, -minval);
    }

    /// <summary>
    /// The sum of MULT32_32_Q31(v, v) for each value shifted left by <paramref name="shift"/> (in
    /// 32 bits wrapping, as the C's SHL32 and ADD32 do): the C's band energy loop, in any order.
    /// </summary>
    internal static int SumSquaresQ31(ReadOnlySpan<int> x, int shift)
    {
        ref int xs = ref MemoryMarshal.GetReference(x);
        int len = x.Length;
        int i = 0;
        int sum = 0;
        if (HasMULT32_32_Q31 && (len >= 4))
        {
            var acc = Vector128<int>.Zero;
            for (; i + 4 <= len; i += 4)
            {
                var v = Vector128.LoadUnsafe(ref xs, (nuint)i) << shift;
                acc += MULT32_32_Q31(v, v);
            }

            sum = Vector128.Sum(acc);
        }

        for (; i < len; i++)
        {
            int v = Unsafe.Add(ref xs, i) << shift;
            sum += (int)(((long)v * v) >> 31);
        }

        return sum;
    }
}
