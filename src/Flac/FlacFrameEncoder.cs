// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;

namespace DotGame.AudioCodecs.Flac;

/// <summary>
/// Encodes FLAC frames (RFC 9639), our own encoder, not libFLAC: each block's channels are tried as a
/// constant, verbatim, a fixed predictor and linear prediction (a Tukey window, Levinson-Durbin,
/// coefficients of libFLAC's precision), and the smallest is kept; residuals are Rice coded in
/// partitions, each with its own parameter, chosen from the partitions' sums. <see cref="Settings"/> says
/// how hard it tries (<see cref="FlacEncoderOptions.CompressionLevel"/>).
/// </summary>
/// <remarks>
/// As libFLAC chooses without its exhaustive search, the fixed order whose residuals add up least and
/// the LPC order the prediction errors say is best are tried; with it, every order. Stereo is coded as left and right,
/// left and side, side and right, or mid and side: all four are tried, or the one a quick estimate
/// favors, or left and right alone. The window comes from <see cref="DeterministicMath"/>, and the
/// coefficients from IEEE arithmetic in a fixed order, so the same samples encode to the same bytes on
/// every machine.
/// </remarks>
internal static class FlacFrameEncoder
{
    /// <summary>How a frame's stereo channels are chosen.</summary>
    internal enum StereoMode
    {
        /// <summary>Left and right, always.</summary>
        Independent,

        /// <summary>The assignment a quick estimate favors, coded alone.</summary>
        Adaptive,

        /// <summary>Every assignment coded, the smallest kept.</summary>
        Exhaustive,
    }

    /// <summary>How hard the encoder tries, and the samples' width.</summary>
    /// <remarks>
    /// <see cref="Subdivisions"/> is flac's apodization: 1 is <c>tukey(0.5)</c>; 2 and 3 are
    /// <c>subdivide_tukey(2)</c> and <c>(3)</c>, which add windows over each half or third of the block
    /// (and, from thirds, windows with each third left out), so a block whose signal changes partway is
    /// predicted from the part that predicts it best.
    /// </remarks>
    internal readonly record struct Settings(int BlockSize, int MaxLpcOrder, int MaxPartitionOrder, StereoMode Stereo, bool Exhaustive, int Bits, int Subdivisions)
    {
        /// <summary>
        /// The LPC coefficients' precision, as libFLAC chooses it from the samples' width and the block
        /// size: 12 bits for 16-bit samples in blocks of 4096, which keeps the prediction in 32 bits.
        /// </summary>
        public int Precision => this.Bits < 16 ? Math.Max(5, 2 + (this.Bits / 2))
            : this.Bits == 16 ? (this.BlockSize <= 192 ? 7 : this.BlockSize <= 384 ? 8 : this.BlockSize <= 576 ? 9
                : this.BlockSize <= 1152 ? 10 : this.BlockSize <= 2304 ? 11 : this.BlockSize <= 4608 ? 12 : 13)
            : this.BlockSize <= 384 ? 13 : this.BlockSize <= 1152 ? 14 : 15;
    }

    /// <summary>The LPC windows for blocks of <paramref name="n"/> samples, with <paramref name="subdivisions"/> as <see cref="Settings.Subdivisions"/>.</summary>
    internal static double[][] Windows(int n, int subdivisions)
    {
        double[] whole = TukeyWindow(n);
        var windows = new System.Collections.Generic.List<double[]> { whole };
        for (int parts = 2; parts <= subdivisions; parts++)
        {
            for (int part = 0; part < parts; part++)
            {
                int start = (int)((long)n * part / parts);
                int end = (int)((long)n * (part + 1) / parts);
                double[] tukey = TukeyWindow(end - start);
                var partial = new double[n];
                tukey.CopyTo(partial, start);
                windows.Add(partial);
                if (parts >= 3)
                {
                    var punchout = new double[n];
                    for (int i = 0; i < n; i++)
                    {
                        punchout[i] = whole[i] * (1 - partial[i]);
                    }
                    windows.Add(punchout);
                }
            }
        }
        return [.. windows];
    }

    /// <summary>
    /// Writes one frame of the first <paramref name="n"/> samples of each of <paramref name="channels"/>'s
    /// first <paramref name="count"/> arrays, numbered <paramref name="frameNumber"/>.
    /// </summary>
    internal static void WriteFrame(BitWriter output, int[][] channels, int count, int sampleRate, int n, long frameNumber, double[][] windows, Scratch scratch, in Settings settings)
    {
        int frameStart = output.ByteLength;
        output.Write(0x3FFE, 14);
        output.Write(0, 2);
        int blockCode = BlockSizeCode(n);
        output.Write((uint)blockCode, 4);
        var (rateCode, rateBits, rateValue) = SampleRateCode(sampleRate);
        output.Write((uint)rateCode, 4);

        int bits = settings.Bits;
        int assignment;
        BitWriter[] writers = scratch.Writers;
        BitWriter first;
        BitWriter? second = null;
        if (count != 2)
        {
            assignment = count - 1;
            for (int c = 0; c < count; c++)
            {
                Subframe(writers[c], channels[c], n, bits, windows, scratch, settings);
            }
            first = writers[0];
        }
        else
        {
            int[] left = channels[0];
            int[] right = channels[1];
            var side = scratch.Channel(0);
            var mid = scratch.Channel(1);
            for (int i = 0; i < n; i++)
            {
                side[i] = left[i] - right[i];
                mid[i] = (left[i] + right[i]) >> 1;
            }

            if (settings.Stereo == StereoMode.Exhaustive)
            {
                BitWriter l = writers[0];
                BitWriter r = writers[1];
                BitWriter s = writers[2];
                BitWriter m = writers[3];
                Subframe(l, left, n, bits, windows, scratch, settings);
                Subframe(r, right, n, bits, windows, scratch, settings);
                Subframe(s, side, n, bits + 1, windows, scratch, settings);
                Subframe(m, mid, n, bits, windows, scratch, settings);
                long independent = l.BitLength + r.BitLength;
                long leftSide = l.BitLength + s.BitLength;
                long sideRight = s.BitLength + r.BitLength;
                long midSide = m.BitLength + s.BitLength;
                long best = Math.Min(Math.Min(independent, leftSide), Math.Min(sideRight, midSide));
                (assignment, first, second) =
                    best == independent ? (1, l, r) :
                    best == leftSide ? (8, l, s) :
                    best == sideRight ? (9, s, r) :
                    (10, m, s);
            }
            else
            {
                assignment = settings.Stereo == StereoMode.Independent ? 1 : EstimateAssignment(left, right, side, mid, n);
                (int[] a, int aBits, int[] b, int bBits) = assignment switch
                {
                    1 => (left, bits, right, bits),
                    8 => (left, bits, side, bits + 1),
                    9 => (side, bits + 1, right, bits),
                    _ => (mid, bits, side, bits + 1),
                };
                first = writers[0];
                second = writers[1];
                Subframe(first, a, n, aBits, windows, scratch, settings);
                Subframe(second, b, n, bBits, windows, scratch, settings);
            }
        }

        output.Write((uint)assignment, 4);
        output.Write((uint)SampleSizeCode(bits), 3);
        output.Write(0, 1);
        WriteUtf8(output, frameNumber);
        if (blockCode == 6)
        {
            output.Write((uint)(n - 1), 8);
        }
        else if (blockCode == 7)
        {
            output.Write((uint)(n - 1), 16);
        }
        if (rateBits > 0)
        {
            output.Write((uint)rateValue, rateBits);
        }
        output.Write(output.Crc8(frameStart), 8);

        output.Append(first);
        if (second is not null)
        {
            output.Append(second);
        }
        for (int c = 1; c < count; c++)
        {
            if (count != 2)
            {
                output.Append(writers[c]);
            }
        }
        output.AlignToByte();
        output.Write(output.Crc16(frameStart), 16);
    }

    // The frame header's block size: a code for the usual sizes, as flac uses, otherwise the size
    // after the header
    private static int BlockSizeCode(int n)
    {
        {
            switch (n)
            {
                case 192: return 1;
                case 576: return 2;
                case 1152: return 3;
                case 2304: return 4;
                case 4608: return 5;
                case 256: return 8;
                case 512: return 9;
                case 1024: return 10;
                case 2048: return 11;
                case 4096: return 12;
                case 8192: return 13;
                case 16384: return 14;
                case 32768: return 15;
            }
        }
        return n <= 256 ? 6 : 7;
    }
    // The frame header's sample size: a code for the usual widths, or 0 for STREAMINFO's
    private static int SampleSizeCode(int bits) => bits switch
    {
        8 => 1,
        12 => 2,
        16 => 4,
        20 => 5,
        24 => 6,
        _ => 0,
    };

    // The stereo assignment with the smallest second differences: a stand-in for coding all four
    private static int EstimateAssignment(int[] left, int[] right, int[] side, int[] mid, int n)
    {
        long l = SecondDifferences(left, n);
        long r = SecondDifferences(right, n);
        long s = SecondDifferences(side, n);
        long m = SecondDifferences(mid, n);
        long independent = l + r;
        long leftSide = l + s;
        long sideRight = s + r;
        long midSide = m + s;
        long best = Math.Min(Math.Min(independent, leftSide), Math.Min(sideRight, midSide));
        return best == independent ? 1 : best == leftSide ? 8 : best == sideRight ? 9 : 10;
    }

    private static long SecondDifferences(int[] x, int n)
    {
        long sum = 0;
        for (int i = 2; i < n; i++)
        {
            sum += Math.Abs((long)x[i] - (2L * x[i - 1]) + x[i - 2]);
        }
        return sum;
    }
    // The frame header's sample rate: one of the codes, or a value after the header
    private static (int Code, int Bits, int Value) SampleRateCode(int rate) => rate switch
    {
        88200 => (1, 0, 0),
        176400 => (2, 0, 0),
        192000 => (3, 0, 0),
        8000 => (4, 0, 0),
        16000 => (5, 0, 0),
        22050 => (6, 0, 0),
        24000 => (7, 0, 0),
        32000 => (8, 0, 0),
        44100 => (9, 0, 0),
        48000 => (10, 0, 0),
        96000 => (11, 0, 0),
        _ when (rate % 1000 == 0) && (rate / 1000 < 256) => (12, 8, rate / 1000),
        _ when rate < 65536 => (13, 16, rate),
        _ when (rate % 10 == 0) && (rate / 10 < 65536) => (14, 16, rate / 10),
        _ => (0, 0, 0),
    };

    private static void WriteUtf8(BitWriter output, long value)
    {
        if (value < 0x80)
        {
            output.Write((uint)value, 8);
            return;
        }

        int bytes = value < 0x800 ? 2 : value < 0x10000 ? 3 : value < 0x200000 ? 4 : value < 0x4000000 ? 5 : 6;
        int shift = (bytes - 1) * 6;
        output.Write((uint)((0xFF00 >> bytes) & 0xFF) | (uint)(value >> shift), 8);
        for (shift -= 6; shift >= 0; shift -= 6)
        {
            output.Write(0x80u | (uint)((value >> shift) & 0x3F), 8);
        }
    }

    // The smallest coding of one channel's block
    private static void Subframe(BitWriter subframe, int[] x, int n, int bits, double[][] windows, Scratch scratch, in Settings settings)
    {
        subframe.Reset();
        bool constant = true;
        for (int i = 1; i < n; i++)
        {
            if (x[i] != x[0])
            {
                constant = false;
                break;
            }
        }
        if (constant)
        {
            subframe.Write(0, 8);
            subframe.WriteSigned(x[0], bits);
            return;
        }

        // Wasted bits: low bits zero in every sample are shifted out and noted in the header
        int or = 0;
        for (int i = 0; i < n; i++)
        {
            or |= x[i];
        }
        int wasted = System.Numerics.BitOperations.TrailingZeroCount(or);
        if (wasted > 0)
        {
            int[] shifted = scratch.Shifted;
            for (int i = 0; i < n; i++)
            {
                shifted[i] = x[i] >> wasted;
            }
            x = shifted;
            bits -= wasted;
        }

        int[] residual = scratch.Residual;
        long bestBits = 8 + wasted + ((long)n * bits);
        int bestKind = 0;
        int bestOrder = 0;
        int bestShift = 0;
        int bestPrecision = 0;

        // The fixed predictors: the one whose residuals' magnitudes add up least, as libFLAC chooses,
        // or every one with the exhaustive search
        int firstFixed = 0;
        int lastFixed = Math.Min(4, n - 1);
        if (!settings.Exhaustive && (n > 4))
        {
            firstFixed = lastFixed = BestFixedOrder(x, n, bits);
        }
        for (int order = firstFixed; order <= lastFixed; order++)
        {
            if (!FixedResidual(x, n, order, bits, residual, scratch))
            {
                continue;
            }
            RiceChoice rice = Rice.Choose(residual, n, order, settings.MaxPartitionOrder, scratch);
            long total = 8 + wasted + ((long)order * bits) + rice.Bits;
            if (total < bestBits)
            {
                (bestBits, bestKind, bestOrder) = (total, 1, order);
                scratch.KeepRice();
            }
        }

        int maxLpcOrder = settings.MaxLpcOrder;
        if ((maxLpcOrder > 0) && (n > 2 * maxLpcOrder))
        {
            // For each window, the order the prediction errors say is best, as libFLAC chooses without
            // its exhaustive search, or every order with it; only they are tried exactly
            foreach (double[] window in windows)
            {
                int found = Lpc.Compute(x, n, maxLpcOrder, window, scratch);
                if (found == 0)
                {
                    continue;
                }
                int first = settings.Exhaustive ? 1 : Lpc.BestOrder(found, scratch.Errors, n, bits + settings.Precision);
                int last = settings.Exhaustive ? found : first;
                for (int order = first; order <= last; order++)
                {
                    // libFLAC's limit, so a 17-bit side channel's prediction stays in 32 bits
                    int precision = settings.Precision;
                    if (bits <= 17)
                    {
                        precision = Math.Min(precision, 32 - bits - System.Numerics.BitOperations.Log2((uint)order));
                    }
                    Span<int> coefficients = scratch.Coefficients.AsSpan(0, order);
                    Lpc.Quantize(scratch.Predictors[order - 1], precision, coefficients, out int shift);
                    if (!LpcResidual(x, n, coefficients, shift, bits, residual, scratch))
                    {
                        continue;
                    }
                    RiceChoice rice = Rice.Choose(residual, n, order, settings.MaxPartitionOrder, scratch);
                    long total = 8 + wasted + ((long)order * bits) + 4 + 5 + ((long)order * precision) + rice.Bits;
                    if (total < bestBits)
                    {
                        (bestBits, bestKind, bestOrder, bestShift, bestPrecision) = (total, 2, order, shift, precision);
                        scratch.KeepRice();
                        coefficients.CopyTo(scratch.BestCoefficients);
                    }
                }
            }
        }

        if (bestKind == 0)
        {
            subframe.Write(0, 1);
            subframe.Write(1, 6);
            WriteWasted(subframe, wasted);
            for (int i = 0; i < n; i++)
            {
                subframe.WriteSigned(x[i], bits);
            }

            return;
        }

        subframe.Write(0, 1);
        subframe.Write(bestKind == 1 ? (uint)(8 | bestOrder) : (uint)(32 | (bestOrder - 1)), 6);
        WriteWasted(subframe, wasted);
        for (int i = 0; i < bestOrder; i++)
        {
            subframe.WriteSigned(x[i], bits);
        }

        if (bestKind == 1)
        {
            FixedResidual(x, n, bestOrder, bits, residual, scratch);
        }
        else
        {
            subframe.Write((uint)(bestPrecision - 1), 4);
            subframe.WriteSigned(bestShift, 5);
            for (int i = 0; i < bestOrder; i++)
            {
                subframe.WriteSigned(scratch.BestCoefficients[i], bestPrecision);
            }
            LpcResidual(x, n, scratch.BestCoefficients.AsSpan(0, bestOrder), bestShift, bits, residual, scratch);
        }

        Rice.Write(subframe, residual, n, bestOrder, scratch.BestRice);
    }

    // The subframe header's wasted-bits flag, and their count less one in unary
    private static void WriteWasted(BitWriter subframe, int wasted)
    {
        if (wasted == 0)
        {
            subframe.Write(0, 1);
            return;
        }
        subframe.Write(1, 1);
        subframe.WriteUnary((ulong)(wasted - 1));
    }

    // The fixed order whose residuals' magnitudes add up least over the samples from the fifth, with
    // libFLAC's preference for the lower order on a tie: one pass for all five, four samples at a time
    // in 32 bits where they can't overflow (samples to 27 bits, so 2^4 times that is under 2^31)
    private static int BestFixedOrder(int[] x, int n, int bits)
    {
        ulong e0 = 0;
        ulong e1 = 0;
        ulong e2 = 0;
        ulong e3 = 0;
        ulong e4 = 0;
        int i = 4;
        if (Vector128.IsHardwareAccelerated && (bits <= 27))
        {
            ref int s = ref MemoryMarshal.GetArrayDataReference(x);

            // Each lane gains under 2^(bits + 3) a step, so the 32-bit sums are moved out before they could wrap
            int steps = 1 << Math.Max(0, 28 - (bits + 3));
            while (i + 4 <= n)
            {
                var a0 = Vector128<uint>.Zero;
                var a1 = Vector128<uint>.Zero;
                var a2 = Vector128<uint>.Zero;
                var a3 = Vector128<uint>.Zero;
                var a4 = Vector128<uint>.Zero;
                for (int step = 0; (step < steps) && (i + 4 <= n); step++, i += 4)
                {
                    var x0 = Vector128.LoadUnsafe(ref s, (nuint)i);
                    var x1 = Vector128.LoadUnsafe(ref s, (nuint)(i - 1));
                    var x2 = Vector128.LoadUnsafe(ref s, (nuint)(i - 2));
                    var x3 = Vector128.LoadUnsafe(ref s, (nuint)(i - 3));
                    var x4 = Vector128.LoadUnsafe(ref s, (nuint)(i - 4));
                    var d0 = x0 - x1;
                    var d1 = x1 - x2;
                    var d2 = x2 - x3;
                    var d3 = x3 - x4;
                    var f0 = d0 - d1;
                    var f1 = d1 - d2;
                    var f2 = d2 - d3;
                    var g0 = f0 - f1;
                    var g1 = f1 - f2;
                    a0 += Vector128.Abs(x0).AsUInt32();
                    a1 += Vector128.Abs(d0).AsUInt32();
                    a2 += Vector128.Abs(f0).AsUInt32();
                    a3 += Vector128.Abs(g0).AsUInt32();
                    a4 += Vector128.Abs(g0 - g1).AsUInt32();
                }
                e0 += Sum(a0);
                e1 += Sum(a1);
                e2 += Sum(a2);
                e3 += Sum(a3);
                e4 += Sum(a4);
            }
        }

        for (; i < n; i++)
        {
            long d0 = (long)x[i] - x[i - 1];
            long d1 = (long)x[i - 1] - x[i - 2];
            long d2 = (long)x[i - 2] - x[i - 3];
            long d3 = (long)x[i - 3] - x[i - 4];
            long f0 = d0 - d1;
            long f1 = d1 - d2;
            long g0 = f0 - f1;
            long g1 = f1 - (d2 - d3);
            e0 += (ulong)Math.Abs((long)x[i]);
            e1 += (ulong)Math.Abs(d0);
            e2 += (ulong)Math.Abs(f0);
            e3 += (ulong)Math.Abs(g0);
            e4 += (ulong)Math.Abs(g0 - g1);
        }

        return e0 < Math.Min(Math.Min(e1, e2), Math.Min(e3, e4)) ? 0
            : e1 < Math.Min(e2, Math.Min(e3, e4)) ? 1
            : e2 < Math.Min(e3, e4) ? 2
            : e3 < e4 ? 3
            : 4;
    }

    private static ulong Sum(Vector128<uint> v) => (ulong)v.GetElement(0) + v.GetElement(1) + v.GetElement(2) + v.GetElement(3);

    // A fixed predictor's residuals, four at a time in 32 bits where they can't overflow; wider
    // samples in 64 bits, false if a residual doesn't fit the 32 bits FLAC allows
    private static bool FixedResidual(int[] x, int n, int order, int bits, int[] r, Scratch scratch)
    {
        if (bits > 27)
        {
            for (int i = order; i < n; i++)
            {
                long e = order switch
                {
                    0 => x[i],
                    1 => (long)x[i] - x[i - 1],
                    2 => (long)x[i] - (2L * x[i - 1]) + x[i - 2],
                    3 => (long)x[i] - (3L * x[i - 1]) + (3L * x[i - 2]) - x[i - 3],
                    _ => (long)x[i] - (4L * x[i - 1]) + (6L * x[i - 2]) - (4L * x[i - 3]) + x[i - 4],
                };
                if (e != (int)e)
                {
                    return false;
                }
                r[i] = (int)e;
            }
            return true;
        }

        ref int s = ref MemoryMarshal.GetArrayDataReference(x);
        ref int e0 = ref MemoryMarshal.GetArrayDataReference(r);
        int k = order;
        if (Vector128.IsHardwareAccelerated)
        {
            switch (order)
            {
                case 0:
                    for (; k + 4 <= n; k += 4)
                    {
                        Vector128.LoadUnsafe(ref s, (nuint)k).StoreUnsafe(ref e0, (nuint)k);
                    }
                    break;
                case 1:
                    for (; k + 4 <= n; k += 4)
                    {
                        (Vector128.LoadUnsafe(ref s, (nuint)k) - Vector128.LoadUnsafe(ref s, (nuint)(k - 1))).StoreUnsafe(ref e0, (nuint)k);
                    }
                    break;
                case 2:
                    for (; k + 4 <= n; k += 4)
                    {
                        var x1 = Vector128.LoadUnsafe(ref s, (nuint)(k - 1));
                        (Vector128.LoadUnsafe(ref s, (nuint)k) - x1 - x1 + Vector128.LoadUnsafe(ref s, (nuint)(k - 2))).StoreUnsafe(ref e0, (nuint)k);
                    }
                    break;
                case 3:
                    for (; k + 4 <= n; k += 4)
                    {
                        var d = Vector128.LoadUnsafe(ref s, (nuint)(k - 1)) - Vector128.LoadUnsafe(ref s, (nuint)(k - 2));
                        (Vector128.LoadUnsafe(ref s, (nuint)k) - Vector128.LoadUnsafe(ref s, (nuint)(k - 3)) - d - d - d).StoreUnsafe(ref e0, (nuint)k);
                    }
                    break;
                default:
                    for (; k + 4 <= n; k += 4)
                    {
                        var x1 = Vector128.LoadUnsafe(ref s, (nuint)(k - 1));
                        var x2 = Vector128.LoadUnsafe(ref s, (nuint)(k - 2));
                        var x3 = Vector128.LoadUnsafe(ref s, (nuint)(k - 3));
                        var x13 = x1 + x3;
                        var six = (x2 << 2) + x2 + x2;
                        (Vector128.LoadUnsafe(ref s, (nuint)k) + Vector128.LoadUnsafe(ref s, (nuint)(k - 4)) - (x13 << 2) + six).StoreUnsafe(ref e0, (nuint)k);
                    }
                    break;
            }
        }

        for (; k < n; k++)
        {
            r[k] = order switch
            {
                0 => x[k],
                1 => x[k] - x[k - 1],
                2 => x[k] - (2 * x[k - 1]) + x[k - 2],
                3 => x[k] - (3 * x[k - 1]) + (3 * x[k - 2]) - x[k - 3],
                _ => x[k] - (4 * x[k - 1]) + (6 * x[k - 2]) - (4 * x[k - 3]) + x[k - 4],
            };
        }
        return true;
    }

    // The 32-bit LPC residuals from the first sample with 4, 8 or 12 before it, as an order 4, 8 or 12
    // predictor whose coefficients past the order are zero (adding nothing), each coefficient in a
    // register; returns where the scalar loop takes over
    private static int Narrow(int[] x, int n, ReadOnlySpan<int> q, int shift, int[] r)
    {
        int order = q.Length;
        int width = order <= 4 ? 4 : order <= 8 ? 8 : 12;
        Span<int> padded = stackalloc int[12];
        padded.Clear();
        q.CopyTo(padded);
        ref int s = ref MemoryMarshal.GetArrayDataReference(x);
        ref int e = ref MemoryMarshal.GetArrayDataReference(r);

        // The samples before there need the scalar loop
        for (int k = order; k < Math.Min(width, n); k++)
        {
            int sum = 0;
            for (int j = 0; j < order; j++)
            {
                sum += q[j] * x[k - j - 1];
            }
            r[k] = x[k] - (sum >> shift);
        }

        int i = width;
        var c0 = Vector128.Create(padded[0]);
        var c1 = Vector128.Create(padded[1]);
        var c2 = Vector128.Create(padded[2]);
        var c3 = Vector128.Create(padded[3]);
        if (width == 4)
        {
            for (; i + 8 <= n; i += 8)
            {
                ref int at = ref Unsafe.Add(ref s, i);
                var low = (c0 * Vector128.LoadUnsafe(ref Unsafe.Subtract(ref at, 1))) + (c1 * Vector128.LoadUnsafe(ref Unsafe.Subtract(ref at, 2)))
                    + (c2 * Vector128.LoadUnsafe(ref Unsafe.Subtract(ref at, 3))) + (c3 * Vector128.LoadUnsafe(ref Unsafe.Subtract(ref at, 4)));
                var high = (c0 * Vector128.LoadUnsafe(ref at, 3)) + (c1 * Vector128.LoadUnsafe(ref at, 2))
                    + (c2 * Vector128.LoadUnsafe(ref at, 1)) + (c3 * Vector128.LoadUnsafe(ref at));
                (Vector128.LoadUnsafe(ref at) - (low >> shift)).StoreUnsafe(ref e, (nuint)i);
                (Vector128.LoadUnsafe(ref at, 4) - (high >> shift)).StoreUnsafe(ref e, (nuint)(i + 4));
            }
            return i;
        }

        var c4 = Vector128.Create(padded[4]);
        var c5 = Vector128.Create(padded[5]);
        var c6 = Vector128.Create(padded[6]);
        var c7 = Vector128.Create(padded[7]);
        if (width == 8)
        {
            for (; i + 8 <= n; i += 8)
            {
                ref int at = ref Unsafe.Add(ref s, i);
                ref int back = ref Unsafe.Subtract(ref at, 8);
                var low = (c0 * Vector128.LoadUnsafe(ref back, 7)) + (c1 * Vector128.LoadUnsafe(ref back, 6))
                    + (c2 * Vector128.LoadUnsafe(ref back, 5)) + (c3 * Vector128.LoadUnsafe(ref back, 4))
                    + (c4 * Vector128.LoadUnsafe(ref back, 3)) + (c5 * Vector128.LoadUnsafe(ref back, 2))
                    + (c6 * Vector128.LoadUnsafe(ref back, 1)) + (c7 * Vector128.LoadUnsafe(ref back));
                var high = (c0 * Vector128.LoadUnsafe(ref back, 11)) + (c1 * Vector128.LoadUnsafe(ref back, 10))
                    + (c2 * Vector128.LoadUnsafe(ref back, 9)) + (c3 * Vector128.LoadUnsafe(ref back, 8))
                    + (c4 * Vector128.LoadUnsafe(ref back, 7)) + (c5 * Vector128.LoadUnsafe(ref back, 6))
                    + (c6 * Vector128.LoadUnsafe(ref back, 5)) + (c7 * Vector128.LoadUnsafe(ref back, 4));
                (Vector128.LoadUnsafe(ref at) - (low >> shift)).StoreUnsafe(ref e, (nuint)i);
                (Vector128.LoadUnsafe(ref at, 4) - (high >> shift)).StoreUnsafe(ref e, (nuint)(i + 4));
            }
            return i;
        }

        var c8 = Vector128.Create(padded[8]);
        var c9 = Vector128.Create(padded[9]);
        var c10 = Vector128.Create(padded[10]);
        var c11 = Vector128.Create(padded[11]);
        for (; i + 4 <= n; i += 4)
        {
            ref int back = ref Unsafe.Add(ref s, i - 12);
            var sum = (c0 * Vector128.LoadUnsafe(ref back, 11)) + (c1 * Vector128.LoadUnsafe(ref back, 10))
                + (c2 * Vector128.LoadUnsafe(ref back, 9)) + (c3 * Vector128.LoadUnsafe(ref back, 8))
                + (c4 * Vector128.LoadUnsafe(ref back, 7)) + (c5 * Vector128.LoadUnsafe(ref back, 6))
                + (c6 * Vector128.LoadUnsafe(ref back, 5)) + (c7 * Vector128.LoadUnsafe(ref back, 4))
                + (c8 * Vector128.LoadUnsafe(ref back, 3)) + (c9 * Vector128.LoadUnsafe(ref back, 2))
                + (c10 * Vector128.LoadUnsafe(ref back, 1)) + (c11 * Vector128.LoadUnsafe(ref back));
            (Vector128.LoadUnsafe(ref back, 12) - (sum >> shift)).StoreUnsafe(ref e, (nuint)i);
        }
        return i;
    }

    // An LPC predictor's residuals. Where the prediction before the shift can't reach 2^31 (the
    // coefficients' magnitudes, times the largest sample, under it) and the shift halves it, every sum
    // and residual fits 32 bits: eight samples a step, in two vectors. Otherwise in 64 bits (NEON's
    // widening multiply-adds, or one at a time), false if a residual doesn't fit 32 bits.
    private static bool LpcResidual(int[] x, int n, ReadOnlySpan<int> q, int shift, int bits, int[] r, Scratch scratch)
    {
        int order = q.Length;
        long magnitude = 0;
        foreach (int c in q)
        {
            magnitude += Math.Abs(c);
        }
        ref int s = ref MemoryMarshal.GetArrayDataReference(x);
        ref int e = ref MemoryMarshal.GetArrayDataReference(r);
        int i = order;
        if ((shift > 0) && (bits < 32) && ((magnitude << (bits - 1)) < (1L << 31)))
        {
            if (Vector128.IsHardwareAccelerated && (order <= 12))
            {
                i = Narrow(x, n, q, shift, r);
            }
            else if (Vector128.IsHardwareAccelerated)
            {
                Span<Vector128<int>> c = stackalloc Vector128<int>[order];
                for (int j = 0; j < order; j++)
                {
                    c[j] = Vector128.Create(q[j]);
                }
                for (; i + 8 <= n; i += 8)
                {
                    var low = Vector128<int>.Zero;
                    var high = Vector128<int>.Zero;
                    for (int j = 0; j < order; j++)
                    {
                        // x[i - j - 1] to x[i - j + 6], each times q[j]
                        low += c[j] * Vector128.LoadUnsafe(ref s, (nuint)(i - j - 1));
                        high += c[j] * Vector128.LoadUnsafe(ref s, (nuint)(i - j + 3));
                    }
                    (Vector128.LoadUnsafe(ref s, (nuint)i) - (low >> shift)).StoreUnsafe(ref e, (nuint)i);
                    (Vector128.LoadUnsafe(ref s, (nuint)(i + 4)) - (high >> shift)).StoreUnsafe(ref e, (nuint)(i + 4));
                }
            }
            for (; i < n; i++)
            {
                int sum = 0;
                for (int j = 0; j < order; j++)
                {
                    sum += q[j] * x[i - j - 1];
                }
                r[i] = x[i] - (sum >> shift);
            }
            return true;
        }

        long[] wide = scratch.Wide;
        ref long w = ref MemoryMarshal.GetArrayDataReference(wide);
        if (AdvSimd.Arm64.IsSupported)
        {
            for (; i + 4 <= n; i += 4)
            {
                var low = Vector128<long>.Zero;
                var high = Vector128<long>.Zero;
                for (int j = 0; j < order; j++)
                {
                    // x[i - j - 1] to x[i - j + 2], each times q[j]
                    var v = Vector128.LoadUnsafe(ref s, (nuint)(i - j - 1));
                    low = AdvSimd.MultiplyWideningLowerAndAdd(low, v.GetLower(), Vector64.Create(q[j]));
                    high = AdvSimd.MultiplyWideningUpperAndAdd(high, v, Vector128.Create(q[j]));
                }

                var (currentLow, currentHigh) = Vector128.Widen(Vector128.LoadUnsafe(ref s, (nuint)i));
                (currentLow - AdvSimd.ShiftArithmetic(low, Vector128.Create((long)-shift))).StoreUnsafe(ref w, (nuint)i);
                (currentHigh - AdvSimd.ShiftArithmetic(high, Vector128.Create((long)-shift))).StoreUnsafe(ref w, (nuint)(i + 2));
            }
        }
        for (; i < n; i++)
        {
            long sum = 0;
            for (int j = 0; j < order; j++)
            {
                sum += (long)q[j] * x[i - j - 1];
            }
            wide[i] = x[i] - (sum >> shift);
        }
        for (int k = order; k < n; k++)
        {
            long v = wide[k];
            if (v != (int)v)
            {
                return false;
            }
            r[k] = (int)v;
        }
        return true;
    }

    // Tukey(0.5): flat in the middle half, cosine tapers at the ends
    private static double[] TukeyWindow(int n)
    {
        var w = new double[n];
        for (int i = 0; i < n; i++)
        {
            double t = n > 1 ? (double)i / (n - 1) : 0.5;
            w[i] = t < 0.25 ? 0.5 * (1 - DeterministicMath.Cos(4 * Math.PI * t))
                : t > 0.75 ? 0.5 * (1 - DeterministicMath.Cos(4 * Math.PI * (1 - t)))
                : 1;
        }

        return w;
    }

    /// <summary>A frame's working buffers.</summary>
    /// <remarks>
    /// Everything a frame needs, made once, so encoding a frame allocates nothing: a writer for each
    /// subframe kept for the frame, the Rice choices (the one being tried and the best), each partition
    /// order's sums, the predictors, their autocorrelation and the coefficients.
    /// </remarks>
    internal sealed class Scratch
    {
        private readonly int[][] _channels;

        public Scratch(int size, int maxLpcOrder, int maxPartitionOrder, int channels)
        {
            this._channels = [new int[size], new int[size]];
            this.Residual = new int[size];
            this.Wide = new long[size];
            this.Shifted = new int[size];
            this.Windowed = new double[size + maxLpcOrder + 16];
            this.Errors = new double[Math.Max(1, maxLpcOrder)];
            this.Autocorrelation = new double[maxLpcOrder + 1];
            this.A = new double[maxLpcOrder + 1];
            this.Previous = new double[maxLpcOrder + 1];
            this.Predictors = new double[maxLpcOrder][];
            for (int i = 0; i < maxLpcOrder; i++)
            {
                this.Predictors[i] = new double[i + 1];
            }
            this.Coefficients = new int[32];
            this.BestCoefficients = new int[32];
            this.CurrentRice = new RiceChoice(maxPartitionOrder);
            this.BestRice = new RiceChoice(maxPartitionOrder);
            this.Levels = new ulong[maxPartitionOrder + 1][];
            for (int p = 0; p <= maxPartitionOrder; p++)
            {
                this.Levels[p] = new ulong[1 << p];
            }
            this.Writers = new BitWriter[Math.Max(4, channels)];
            for (int i = 0; i < this.Writers.Length; i++)
            {
                this.Writers[i] = new BitWriter(64 + (size * 4));
            }
        }

        public int[] Residual { get; }

        public long[] Wide { get; }

        public int[] Shifted { get; }

        public double[] Windowed { get; }

        public double[] Errors { get; }

        public double[] Autocorrelation { get; }

        public double[] A { get; }

        public double[] Previous { get; }

        public double[][] Predictors { get; }

        public int[] Coefficients { get; }

        public int[] BestCoefficients { get; }

        public RiceChoice CurrentRice { get; private set; }

        public RiceChoice BestRice { get; private set; }

        public ulong[][] Levels { get; }

        public BitWriter[] Writers { get; }

        public int[] Channel(int index) => this._channels[index];

        /// <summary>Keeps the Rice choice just made as the best: the two swap.</summary>
        public void KeepRice() => (this.CurrentRice, this.BestRice) = (this.BestRice, this.CurrentRice);
    }

    internal sealed class RiceChoice(int maxPartitionOrder)
    {
        public long Bits;
        public int PartitionOrder;
        public int[] Parameters = new int[1 << maxPartitionOrder];
        public bool Wide;
    }

    private static class Rice
    {
        // The partition order and parameters, from each partition's sum of folded residuals: sums are
        // made once for the smallest partitions and added up for the larger ones, and a parameter's
        // cost is estimated as count * (k + 1) + (sum >> k), as libFLAC does, so the samples are read
        // once whatever the number of orders tried
        public static RiceChoice Choose(int[] r, int n, int order, int maxPartitionOrder, Scratch scratch)
        {
            int maxOrder = 0;
            while ((maxOrder < maxPartitionOrder) && ((n % (1 << (maxOrder + 1))) == 0) && ((n >> (maxOrder + 1)) > order))
            {
                maxOrder++;
            }

            int finest = 1 << maxOrder;
            int finestLength = n >> maxOrder;
            ulong[][] levels = scratch.Levels;
            ulong[] sums = levels[maxOrder];
            for (int part = 0; part < finest; part++)
            {
                int s = part == 0 ? order : part * finestLength;
                int e = (part + 1) * finestLength;
                sums[part] = FoldedSum(r.AsSpan(s, e - s));
            }

            // Each order's sums, the finest first, kept so the best order's parameters can be found
            // again at the end (the coarser sums overwrite the finer as each order is tried)
            for (int p = maxOrder - 1; p >= 0; p--)
            {
                ulong[] finer = levels[p + 1];
                ulong[] coarser = levels[p];
                for (int part = 0; part < (1 << p); part++)
                {
                    coarser[part] = finer[2 * part] + finer[(2 * part) + 1];
                }
            }

            long bestBits = long.MaxValue;
            int bestOrder = 0;
            bool bestWide = false;
            for (int p = maxOrder; p >= 0; p--)
            {
                long bits = 2 + 4;
                bool wide = false;
                ulong[] orderSums = levels[p];
                int length = n >> p;
                for (int part = 0; part < (1 << p); part++)
                {
                    long count = (part == 0) ? length - order : length;
                    int k = Parameter(orderSums[part], count, out long partBits);
                    wide |= k >= 15;
                    bits += partBits;
                }

                bits += (1L << p) * (wide ? 5 : 4);
                if (bits < bestBits)
                {
                    (bestBits, bestOrder, bestWide) = (bits, p, wide);
                }
            }

            RiceChoice choice = scratch.CurrentRice;
            int[] parameters = choice.Parameters;
            int bestLength = n >> bestOrder;
            for (int part = 0; part < (1 << bestOrder); part++)
            {
                parameters[part] = Parameter(levels[bestOrder][part], (part == 0) ? bestLength - order : bestLength, out _);
            }

            (choice.Bits, choice.PartitionOrder, choice.Wide) = (bestBits, bestOrder, bestWide);
            return choice;
        }

        // A partition's parameter and its bits: the cheapest of the three around the estimate, the
        // largest e to 30 with 2^(e+1) <= sum / count (rounded down), which is count << (e + 1) <=
        // sum, found from the two numbers' logarithms without a division
        private static int Parameter(ulong sum, long count, out long bits)
        {
            int estimate = 0;
            if ((count > 0) && (sum >= (ulong)count * 2))
            {
                estimate = Math.Clamp(System.Numerics.BitOperations.Log2(sum) - System.Numerics.BitOperations.Log2((ulong)count) - 1, 0, 30);
                while ((estimate > 0) && (((ulong)count << (estimate + 1)) > sum))
                {
                    estimate--;
                }
                while ((estimate < 30) && (((ulong)count << (estimate + 2)) <= sum))
                {
                    estimate++;
                }
            }

            long bestPart = long.MaxValue;
            int bestK = 0;
            for (int k = Math.Max(0, estimate - 1); k <= Math.Min(30, estimate + 1); k++)
            {
                long b = (count * (k + 1)) + (long)(sum >> k);
                if (b < bestPart)
                {
                    bestPart = b;
                    bestK = k;
                }
            }

            bits = bestPart;
            return bestK;
        }
        public static void Write(BitWriter output, int[] r, int n, int order, RiceChoice choice)
        {
            output.Write(choice.Wide ? 1u : 0u, 2);
            output.Write((uint)choice.PartitionOrder, 4);
            int parts = 1 << choice.PartitionOrder;
            int length = n >> choice.PartitionOrder;
            for (int part = 0; part < parts; part++)
            {
                int k = choice.Parameters[part];
                output.Write((uint)k, choice.Wide ? 5 : 4);
                int s = part == 0 ? order : part * length;
                output.WriteRice(r.AsSpan(s, ((part + 1) * length) - s), k);
            }
        }

        // The sum of the folded values (zigzag: 0, -1, 1, -2, ... to 0, 1, 2, 3, ...), four at a time,
        // widened to 64 bits as they're added
        private static ulong FoldedSum(ReadOnlySpan<int> values)
        {
            ref int v = ref MemoryMarshal.GetReference(values);
            int i = 0;
            ulong sum = 0;
            if (Vector128.IsHardwareAccelerated && (values.Length >= 8))
            {
                var low = Vector128<ulong>.Zero;
                var high = Vector128<ulong>.Zero;
                for (; i + 4 <= values.Length; i += 4)
                {
                    var x = Vector128.LoadUnsafe(ref v, (nuint)i);
                    var folded = ((x << 1) ^ (x >> 31)).AsUInt32();
                    var (a, b) = Vector128.Widen(folded);
                    low += a;
                    high += b;
                }

                var both = low + high;
                sum = both.GetElement(0) + both.GetElement(1);
            }

            for (; i < values.Length; i++)
            {
                int x = Unsafe.Add(ref v, i);
                sum += (uint)((x << 1) ^ (x >> 31));
            }

            return sum;
        }
    }

    private static class Lpc
    {
        // The predictors of orders 1 to maxOrder, from the windowed block's autocorrelation, into the
        // scratch's, with their errors; returns how many there are, fewer where the recursion stops (a
        // block that's perfectly predicted)
        public static int Compute(int[] x, int n, int maxOrder, double[] window, Scratch scratch)
        {
            double[] windowed = scratch.Windowed;
            for (int i = 0; i < n; i++)
            {
                windowed[i] = x[i] * window[i];
            }
            Array.Clear(windowed, n, windowed.Length - n);

            double[] autocorrelation = Autocorrelate(windowed, n, maxOrder, scratch);
            if (autocorrelation[0] == 0)
            {
                return 0;
            }

            double[] a = scratch.A;
            double[] previous = scratch.Previous;
            Array.Clear(a);
            double[] errors = scratch.Errors;
            int found = 0;
            double error = autocorrelation[0];
            for (int i = 1; i <= maxOrder; i++)
            {
                double k = -autocorrelation[i];
                for (int j = 1; j < i; j++)
                {
                    k -= a[j] * autocorrelation[i - j];
                }
                k /= error;
                Array.Copy(a, previous, a.Length);
                a[i] = k;
                for (int j = 1; j < i; j++)
                {
                    a[j] = previous[j] + (k * previous[i - j]);
                }
                error *= 1 - (k * k);
                if (error <= 0)
                {
                    break;
                }

                double[] coefficients = scratch.Predictors[i - 1];
                for (int j = 0; j < i; j++)
                {
                    coefficients[j] = -a[j + 1];
                }
                errors[i - 1] = error;
                found = i;
            }

            return found;
        }

        // Each lag's sum of w[i] * w[i + lag], as two sums, of the even i and of the odd, in order,
        // added at the end (so a sum's adds don't each wait on the last). Vectors hold two lags and
        // registers four vectors, so the lags go in passes of up to eight; the scalar loops add the
        // same products in the same order, so every double is the same. w is zero past the block, so
        // the lags run past its end, and an odd block has a zero to end the odd sums.
        private static double[] Autocorrelate(double[] w, int n, int maxOrder, Scratch scratch)
        {
            double[] autocorrelation = scratch.Autocorrelation;
            int m = (n + 1) & ~1;
            if (Vector128.IsHardwareAccelerated)
            {
                int lags = maxOrder + 1;
                for (int lag = 0; lag < lags;)
                {
                    int left = lags - lag;
                    int pairs = left > 8 ? 4 : left > 4 ? 5 : left > 2 ? 2 : 1;
                    Pass(w, m, lag, pairs, autocorrelation, lags);
                    lag += 2 * pairs;
                }

                return autocorrelation;
            }

            for (int lag = 0; lag <= maxOrder; lag++)
            {
                double even = 0;
                double odd = 0;
                for (int i = 0; i < m; i += 2)
                {
                    even += w[i] * w[i + lag];
                    odd += w[i + 1] * w[i + 1 + lag];
                }
                autocorrelation[lag] = even + odd;
            }

            return autocorrelation;
        }

        // Lags first to first + 2 × pairs - 1 (pairs 1, 2, 4 or 5), each sum in a register
        private static void Pass(double[] w, int m, int first, int pairs, double[] autocorrelation, int lags)
        {
            ref double s = ref MemoryMarshal.GetArrayDataReference(w);
            var e0 = Vector128<double>.Zero;
            var e1 = Vector128<double>.Zero;
            var e2 = Vector128<double>.Zero;
            var e3 = Vector128<double>.Zero;
            var o0 = Vector128<double>.Zero;
            var o1 = Vector128<double>.Zero;
            var o2 = Vector128<double>.Zero;
            var o3 = Vector128<double>.Zero;
            var e4 = Vector128<double>.Zero;
            var o4 = Vector128<double>.Zero;
            if (pairs == 5)
            {
                for (int i = 0; i < m; i += 2)
                {
                    var even = Vector128.Create(Unsafe.Add(ref s, i));
                    var odd = Vector128.Create(Unsafe.Add(ref s, i + 1));
                    ref double at = ref Unsafe.Add(ref s, i + first);
                    e0 += even * Vector128.LoadUnsafe(ref at);
                    o0 += odd * Vector128.LoadUnsafe(ref at, 1);
                    e1 += even * Vector128.LoadUnsafe(ref at, 2);
                    o1 += odd * Vector128.LoadUnsafe(ref at, 3);
                    e2 += even * Vector128.LoadUnsafe(ref at, 4);
                    o2 += odd * Vector128.LoadUnsafe(ref at, 5);
                    e3 += even * Vector128.LoadUnsafe(ref at, 6);
                    o3 += odd * Vector128.LoadUnsafe(ref at, 7);
                    e4 += even * Vector128.LoadUnsafe(ref at, 8);
                    o4 += odd * Vector128.LoadUnsafe(ref at, 9);
                }
            }
            else if (pairs == 4)
            {
                for (int i = 0; i < m; i += 2)
                {
                    var even = Vector128.Create(Unsafe.Add(ref s, i));
                    var odd = Vector128.Create(Unsafe.Add(ref s, i + 1));
                    ref double at = ref Unsafe.Add(ref s, i + first);
                    e0 += even * Vector128.LoadUnsafe(ref at);
                    o0 += odd * Vector128.LoadUnsafe(ref at, 1);
                    e1 += even * Vector128.LoadUnsafe(ref at, 2);
                    o1 += odd * Vector128.LoadUnsafe(ref at, 3);
                    e2 += even * Vector128.LoadUnsafe(ref at, 4);
                    o2 += odd * Vector128.LoadUnsafe(ref at, 5);
                    e3 += even * Vector128.LoadUnsafe(ref at, 6);
                    o3 += odd * Vector128.LoadUnsafe(ref at, 7);
                }
            }
            else if (pairs == 2)
            {
                for (int i = 0; i < m; i += 2)
                {
                    var even = Vector128.Create(Unsafe.Add(ref s, i));
                    var odd = Vector128.Create(Unsafe.Add(ref s, i + 1));
                    ref double at = ref Unsafe.Add(ref s, i + first);
                    e0 += even * Vector128.LoadUnsafe(ref at);
                    o0 += odd * Vector128.LoadUnsafe(ref at, 1);
                    e1 += even * Vector128.LoadUnsafe(ref at, 2);
                    o1 += odd * Vector128.LoadUnsafe(ref at, 3);
                }
            }
            else
            {
                for (int i = 0; i < m; i += 2)
                {
                    ref double at = ref Unsafe.Add(ref s, i + first);
                    e0 += Vector128.Create(Unsafe.Add(ref s, i)) * Vector128.LoadUnsafe(ref at);
                    o0 += Vector128.Create(Unsafe.Add(ref s, i + 1)) * Vector128.LoadUnsafe(ref at, 1);
                }
            }

            Store(e0 + o0, first, autocorrelation, lags);
            Store(e1 + o1, first + 2, autocorrelation, lags);
            Store(e2 + o2, first + 4, autocorrelation, lags);
            Store(e3 + o3, first + 6, autocorrelation, lags);
            Store(e4 + o4, first + 8, autocorrelation, lags);
        }

        private static void Store(Vector128<double> sums, int lag, double[] autocorrelation, int lags)
        {
            if (lag < lags)
            {
                autocorrelation[lag] = sums.GetElement(0);
            }
            if (lag + 1 < lags)
            {
                autocorrelation[lag + 1] = sums.GetElement(1);
            }
        }

        // The order with the fewest expected bits, as libFLAC's FLAC__lpc_compute_best_order: the
        // residual's, from the prediction error (0.5 log2(error / 2n) bits a sample), and each order's
        // warm-up sample and coefficient (overhead bits)
        public static int BestOrder(int found, double[] errors, int n, int overhead)
        {
            int best = 1;
            double bestBits = double.MaxValue;
            for (int order = 1; order <= found; order++)
            {
                double scaled = errors[order - 1] * 0.5 / n;
                double perSample = scaled > 1 ? 0.5 * DeterministicMath.Log2(scaled) : 0;
                double expected = ((n - order) * perSample) + (order * overhead);
                if (expected < bestBits)
                {
                    (best, bestBits) = (order, expected);
                }
            }

            return best;
        }

        // Quantized with the error carried from one coefficient to the next, as libFLAC does
        public static void Quantize(ReadOnlySpan<double> c, int precision, Span<int> q, out int shift)
        {
            double max = 0;
            foreach (double v in c)
            {
                max = Math.Max(max, Math.Abs(v));
            }

            int log2 = max > 0 ? Math.ILogB(max) + 1 : 0;
            shift = Math.Clamp(precision - 1 - log2, 0, 15);
            int qmax = (1 << (precision - 1)) - 1;
            int qmin = -(1 << (precision - 1));
            double error = 0;
            for (int i = 0; i < c.Length; i++)
            {
                error += c[i] * (1 << shift);
                int v = Math.Clamp((int)Math.Round(error, MidpointRounding.ToEven), qmin, qmax);
                q[i] = v;
                error -= v;
            }
        }
    }

    // MSB-first bits, 64 at a time
    /// <summary>MSB-first bits, 64 at a time.</summary>
    internal sealed class BitWriter(int capacity)
    {
        private byte[] _bytes = new byte[Math.Max(16, capacity)];
        private int _length;
        private ulong _pending;
        private int _pendingBits;

        public long BitLength => ((long)this._length * 8) + this._pendingBits;

        public int ByteLength
        {
            get
            {
                this.Flush();
                return this._pendingBits == 0 ? this._length : throw new InvalidOperationException("Not on a byte boundary.");
            }
        }

        public void Write(int value, int bits) => this.Write((uint)value, bits);

        public void Write(uint value, int bits)
        {
            if (bits == 0)
            {
                return;
            }

            ulong masked = value & (bits == 32 ? uint.MaxValue : ((1u << bits) - 1));
            if (this._pendingBits + bits <= 64)
            {
                this._pending = (this._pending << bits) | masked;
                this._pendingBits += bits;
            }
            else
            {
                int head = 64 - this._pendingBits;
                this._pending = (this._pending << head) | (masked >> (bits - head));
                this._pendingBits = 64;
                this.Flush();
                this._pending = masked & ((1UL << (bits - head)) - 1);
                this._pendingBits = bits - head;
            }
            if (this._pendingBits >= 56)
            {
                this.Flush();
            }
        }

        public void WriteSigned(long value, int bits) => this.Write((uint)((ulong)value & (bits == 32 ? uint.MaxValue : ((1UL << bits) - 1))), bits);

        /// <summary>
        /// Rice codes of <paramref name="values"/> with parameter <paramref name="k"/> (each value folded,
        /// its high part in unary, then its low k bits): codes of up to 32 bits gathered in a local
        /// register and stored 32 bits at a time, longer ones through <see cref="WriteUnary"/>.
        /// </summary>
        public void WriteRice(ReadOnlySpan<int> values, int k)
        {
            this.Flush();
            this.Reserve(values.Length);
            ulong pending = this._pending;
            int count = this._pendingBits;
            int length = this._length;
            ref byte bytes = ref MemoryMarshal.GetArrayDataReference(this._bytes);
            uint low = (1u << k) - 1;
            uint stop = 1u << k;
            for (int i = 0; i < values.Length; i++)
            {
                int v = values[i];
                uint u = (uint)((v << 1) ^ (v >> 31));
                uint zeros = u >> k;
                if (zeros + 1 + (uint)k > 32)
                {
                    (this._pending, this._pendingBits, this._length) = (pending, count, length);
                    this.WriteUnary(zeros);
                    if (k > 0)
                    {
                        this.Write(u & low, k);
                    }
                    this.Flush();
                    this.Reserve(values.Length - i);
                    (pending, count, length) = (this._pending, this._pendingBits, this._length);
                    bytes = ref MemoryMarshal.GetArrayDataReference(this._bytes);
                    continue;
                }

                // The top 32 bits are stored whether or not they're whole; only whole ones are kept
                int bits = (int)zeros + 1 + k;
                pending = (pending << bits) | stop | (u & low);
                count += bits;
                int full = count >> 5;
                count -= full << 5;
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref bytes, length), BinaryPrimitives.ReverseEndianness((uint)(pending >> count)));
                length += full << 2;
            }
            this._pending = count == 0 ? 0 : pending & ((1UL << count) - 1);
            this._pendingBits = count;
            this._length = length;
        }

        // Room for that many codes of up to 32 bits, and the pending bits
        private void Reserve(int codes)
        {
            int needed = this._length + (4 * codes) + 16;
            if (needed > this._bytes.Length)
            {
                Array.Resize(ref this._bytes, Math.Max(needed, this._bytes.Length * 2));
            }
        }

        public void WriteUnary(ulong zeros)
        {
            while (zeros >= 32)
            {
                this.Write(0, 32);
                zeros -= 32;
            }

            this.Write(1, (int)zeros + 1);
        }

        public void AlignToByte()
        {
            int extra = this._pendingBits & 7;
            if (extra != 0)
            {
                this.Write(0, 8 - extra);
            }
            this.Flush();
        }

        public void Append(BitWriter other)
        {
            other.Flush();
            int i = 0;
            for (; i + 4 <= other._length; i += 4)
            {
                this.Write(BinaryPrimitives.ReadUInt32BigEndian(other._bytes.AsSpan(i)), 32);
            }
            for (; i < other._length; i++)
            {
                this.Write(other._bytes[i], 8);
            }
            if (other._pendingBits > 0)
            {
                this.Write((uint)other._pending, other._pendingBits);
            }
        }

        public byte Crc8(int from)
        {
            this.Flush();
            int crc = 0;
            for (int i = from; i < this._length; i++)
            {
                crc = Crc8Table[crc ^ this._bytes[i]];
            }

            return (byte)crc;
        }

        public ushort Crc16(int from)
        {
            this.Flush();
            return FlacFrame.Crc16(this._bytes.AsSpan(from, this._length - from));
        }

        private static readonly byte[] Crc8Table = CreateCrc8Table();

        // CRC-8, polynomial 0x07, of each byte
        private static byte[] CreateCrc8Table()
        {
            var table = new byte[256];
            for (int b = 0; b < 256; b++)
            {
                int crc = b;
                for (int k = 0; k < 8; k++)
                {
                    crc = ((crc & 0x80) != 0) ? ((crc << 1) ^ 0x07) & 0xFF : (crc << 1) & 0xFF;
                }
                table[b] = (byte)crc;
            }

            return table;
        }

        /// <summary>The bytes written, which must end on a byte boundary; valid until the next write.</summary>
        public ReadOnlySpan<byte> Bytes
        {
            get
            {
                this.Flush();
                return this._pendingBits == 0 ? this._bytes.AsSpan(0, this._length) : throw new InvalidOperationException("Not on a byte boundary.");
            }
        }

        /// <summary>Empties the writer, keeping its buffer.</summary>
        public void Reset()
        {
            this._length = 0;
            this._pending = 0;
            this._pendingBits = 0;
        }
        public byte[] ToArray()
        {
            this.Flush();
            if (this._pendingBits != 0)
            {
                throw new InvalidOperationException("Not on a byte boundary.");
            }

            return this._bytes.AsSpan(0, this._length).ToArray();
        }

        // Moves the whole bytes of the pending bits out
        private void Flush()
        {
            if (this._pendingBits >= 32)
            {
                if (this._length + 4 > this._bytes.Length)
                {
                    Array.Resize(ref this._bytes, this._bytes.Length * 2);
                }
                this._pendingBits -= 32;
                BinaryPrimitives.WriteUInt32BigEndian(this._bytes.AsSpan(this._length), (uint)(this._pending >> this._pendingBits));
                this._length += 4;
            }
            while (this._pendingBits >= 8)
            {
                if (this._length == this._bytes.Length)
                {
                    Array.Resize(ref this._bytes, this._bytes.Length * 2);
                }
                this._pendingBits -= 8;
                this._bytes[this._length++] = (byte)(this._pending >> this._pendingBits);
            }
            this._pending &= this._pendingBits == 0 ? 0 : ((1UL << this._pendingBits) - 1);
        }
    }
}
