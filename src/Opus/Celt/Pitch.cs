/* Copyright (c) 2007-2008 CSIRO
   Copyright (c) 2007-2009 Xiph.Org Foundation
   Written by Jean-Marc Valin
   Ported to C# from libopus 1.6.1 (celt/pitch.c, celt/pitch.h) by Hot Chai Games LLC */
/*
   @file pitch.c
   @brief Pitch analysis
 */

/*
   Redistribution and use in source and binary forms, with or without
   modification, are permitted provided that the following conditions
   are met:

   - Redistributions of source code must retain the above copyright
   notice, this list of conditions and the following disclaimer.

   - Redistributions in binary form must reproduce the above copyright
   notice, this list of conditions and the following disclaimer in the
   documentation and/or other materials provided with the distribution.

   THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
   ``AS IS'' AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
   LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
   A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER
   OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL,
   EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO,
   PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR
   PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF
   LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
   NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
   SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
*/

using System;
using System.Runtime.CompilerServices;
using static DotGame.AudioCodecs.Opus.Celt.Arch;
using static DotGame.AudioCodecs.Opus.Celt.MathOps;

namespace DotGame.AudioCodecs.Opus.Celt;

/// <summary>
/// celt/pitch.c and celt/pitch.h: the pitch search the packet-loss concealment uses, and the
/// correlation kernels.
/// </summary>
[SkipLocalsInit]
internal static class Pitch
{
    /* OPT: This is the kernel you really want to optimize. It gets used a lot
       by the prefilter and by the PLC. */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void xcorr_kernel(ReadOnlySpan<short> x, ReadOnlySpan<short> y, Span<int> sum, int len)
    {
        Span<int> products = stackalloc int[4];
        Simd.CrossCorrelate4(x, y, len, products);
        sum[0] += products[0];
        sum[1] += products[1];
        sum[2] += products[2];
        sum[3] += products[3];
    }

    /*We make sure a C version is always available for cases where the overhead of
      vectorization and passing around an arch flag aren't worth it.*/
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int celt_inner_prod(ReadOnlySpan<short> x, ReadOnlySpan<short> y, int N)
    {
        return Simd.InnerProduct16(x, y, N);
    }

    private static void find_best_pitch(ReadOnlySpan<int> xcorr, ReadOnlySpan<short> y, int len,
                                int max_pitch, Span<int> best_pitch, int yshift, int maxcorr)
    {
        int i, j;
        int Syy = 1;
        Span<short> best_num = stackalloc short[2];
        Span<int> best_den = stackalloc int[2];
        int xshift;

        xshift = celt_ilog2(maxcorr) - 14;

        best_num[0] = -1;
        best_num[1] = -1;
        best_den[0] = 0;
        best_den[1] = 0;
        best_pitch[0] = 0;
        best_pitch[1] = 1;
        for (j = 0; j < len; j++)
        {
            Syy = ADD32(Syy, SHR32(MULT16_16(y[j], y[j]), yshift));
        }
        for (i = 0; i < max_pitch; i++)
        {
            if (xcorr[i] > 0)
            {
                short num;
                int xcorr16;
                xcorr16 = EXTRACT16(VSHR32(xcorr[i], xshift));
                num = (short)MULT16_16_Q15(xcorr16, xcorr16);
                if (MULT16_32_Q15(num, best_den[1]) > MULT16_32_Q15(best_num[1], Syy))
                {
                    if (MULT16_32_Q15(num, best_den[0]) > MULT16_32_Q15(best_num[0], Syy))
                    {
                        best_num[1] = best_num[0];
                        best_den[1] = best_den[0];
                        best_pitch[1] = best_pitch[0];
                        best_num[0] = num;
                        best_den[0] = Syy;
                        best_pitch[0] = i;
                    }
                    else
                    {
                        best_num[1] = num;
                        best_den[1] = Syy;
                        best_pitch[1] = i;
                    }
                }
            }
            Syy += SHR32(MULT16_16(y[i + len], y[i + len]), yshift) - SHR32(MULT16_16(y[i], y[i]), yshift);
            Syy = MAX32(1, Syy);
        }
    }

    private static void celt_fir5(Span<short> x,
             ReadOnlySpan<short> num,
             int N)
    {
        int i;
        short num0, num1, num2, num3, num4;
        int mem0, mem1, mem2, mem3, mem4;
        num0 = num[0];
        num1 = num[1];
        num2 = num[2];
        num3 = num[3];
        num4 = num[4];
        mem0 = 0;
        mem1 = 0;
        mem2 = 0;
        mem3 = 0;
        mem4 = 0;
        for (i = 0; i < N; i++)
        {
            int sum = SHL32(EXTEND32(x[i]), SIG_SHIFT);
            sum = MAC16_16(sum, num0, mem0);
            sum = MAC16_16(sum, num1, mem1);
            sum = MAC16_16(sum, num2, mem2);
            sum = MAC16_16(sum, num3, mem3);
            sum = MAC16_16(sum, num4, mem4);
            mem4 = mem3;
            mem3 = mem2;
            mem2 = mem1;
            mem1 = mem0;
            mem0 = x[i];
            x[i] = ROUND16(sum, SIG_SHIFT);
        }
    }

    internal static void pitch_downsample(celt_sig_ptrs x, Span<short> x_lp,
          int len, int C, int factor)
    {
        int i;
        Span<int> ac = stackalloc int[5];
        short tmp = Q15ONE;
        Span<short> lpc = stackalloc short[4];
        Span<short> lpc2 = stackalloc short[5];
        short c1 = 26214; /* QCONST16(.8f,15) */
        int offset;
        int shift;
        int maxabs;
        offset = factor / 2;
        maxabs = celt_maxabs32(x[0], len * factor);
        if (C == 2)
        {
            int maxabs_1 = celt_maxabs32(x[1], len * factor);
            maxabs = MAX32(maxabs, maxabs_1);
        }
        if (maxabs < 1)
        {
            maxabs = 1;
        }
        shift = celt_ilog2(maxabs) - 10;
        if (shift < 0)
        {
            shift = 0;
        }
        if (C == 2)
        {
            shift++;
        }
        ReadOnlySpan<int> x0 = x[0];
        for (i = 1; i < len; i++)
        {
            x_lp[i] = (short)(SHR32(x0[((factor * i) - offset)], shift + 2) + SHR32(x0[((factor * i) + offset)], shift + 2) + SHR32(x0[factor * i], shift + 1));
        }
        x_lp[0] = (short)(SHR32(x0[offset], shift + 2) + SHR32(x0[0], shift + 1));
        if (C == 2)
        {
            ReadOnlySpan<int> x1 = x[1];
            for (i = 1; i < len; i++)
            {
                x_lp[i] += (short)(SHR32(x1[((factor * i) - offset)], shift + 2) + SHR32(x1[((factor * i) + offset)], shift + 2) + SHR32(x1[factor * i], shift + 1));
            }
            x_lp[0] += (short)(SHR32(x1[offset], shift + 2) + SHR32(x1[0], shift + 1));
        }
        CeltLpc._celt_autocorr(x_lp, ac, default, 0,
                       4, len);

        /* Noise floor -40 dB */
        ac[0] += SHR32(ac[0], 13);
        /* Lag windowing */
        for (i = 1; i <= 4; i++)
        {
            /*ac[i] *= exp(-.5*(2*M_PI*.002*i)*(2*M_PI*.002*i));*/
            ac[i] -= MULT16_32_Q15(2 * i * i, ac[i]);
        }

        CeltLpc._celt_lpc(lpc, ac, 4);
        for (i = 0; i < 4; i++)
        {
            tmp = (short)MULT16_16_Q15(29491 /* QCONST16(.9f,15) */, tmp);
            lpc[i] = (short)MULT16_16_Q15(lpc[i], tmp);
        }
        /* Add a zero */
        lpc2[0] = (short)(lpc[0] + 3277 /* QCONST16(.8f,SIG_SHIFT) */);
        lpc2[1] = (short)(lpc[1] + MULT16_16_Q15(c1, lpc[0]));
        lpc2[2] = (short)(lpc[2] + MULT16_16_Q15(c1, lpc[1]));
        lpc2[3] = (short)(lpc[3] + MULT16_16_Q15(c1, lpc[2]));
        lpc2[4] = (short)MULT16_16_Q15(c1, lpc[3]);
        celt_fir5(x_lp, lpc2, len);
    }

    /* Pure C implementation. */
    internal static int celt_pitch_xcorr(ReadOnlySpan<short> _x, ReadOnlySpan<short> _y,
          Span<int> xcorr, int len, int max_pitch)
    {
        /* Unrolled version of the pitch correlation -- runs faster on x86 and ARM */
        int i;
        /*The EDSP version requires that max_pitch is at least 1, and that _x is
           32-bit aligned.
          Since it's hard to put asserts in assembly, put them here.*/
        int maxcorr = 1;
        Span<int> sum = stackalloc int[4];
        for (i = 0; i < max_pitch - 3; i += 4)
        {
            Simd.CrossCorrelate4(_x, _y.Slice(i), len, sum);
            xcorr[i] = sum[0];
            xcorr[i + 1] = sum[1];
            xcorr[i + 2] = sum[2];
            xcorr[i + 3] = sum[3];
            sum[0] = MAX32(sum[0], sum[1]);
            sum[2] = MAX32(sum[2], sum[3]);
            sum[0] = MAX32(sum[0], sum[2]);
            maxcorr = MAX32(maxcorr, sum[0]);
        }
        /* In case max_pitch isn't a multiple of 4, do non-unrolled version. */
        for (; i < max_pitch; i++)
        {
            int s;
            s = celt_inner_prod(_x, _y.Slice(i), len);
            xcorr[i] = s;
            maxcorr = MAX32(maxcorr, s);
        }
        return maxcorr;
    }

    internal static void pitch_search(ReadOnlySpan<short> x_lp, ReadOnlySpan<short> y,
                      int len, int max_pitch, out int pitch)
    {
        int i, j;
        int lag;
        Span<int> best_pitch = stackalloc int[2];
        int maxcorr;
        int xmax, ymax;
        int shift = 0;
        int offset;

        lag = len + max_pitch;

        Span<short> x_lp4 = stackalloc short[len >> 2];
        Span<short> y_lp4 = stackalloc short[lag >> 2];
        Span<int> xcorr = stackalloc int[max_pitch >> 1];

        /* Downsample by 2 again */
        for (j = 0; j < len >> 2; j++)
        {
            x_lp4[j] = x_lp[2 * j];
        }
        for (j = 0; j < lag >> 2; j++)
        {
            y_lp4[j] = y[2 * j];
        }

        xmax = celt_maxabs16(x_lp4, len >> 2);
        ymax = celt_maxabs16(y_lp4, lag >> 2);
        shift = celt_ilog2(MAX32(1, MAX32(xmax, ymax))) - 14 + (celt_ilog2(len) / 2);
        if (shift > 0)
        {
            for (j = 0; j < len >> 2; j++)
            {
                x_lp4[j] = (short)SHR16(x_lp4[j], shift);
            }
            for (j = 0; j < lag >> 2; j++)
            {
                y_lp4[j] = (short)SHR16(y_lp4[j], shift);
            }
            /* Use double the shift for a MAC */
            shift *= 2;
        }
        else
        {
            shift = 0;
        }

        /* Coarse search with 4x decimation */

        maxcorr =
        celt_pitch_xcorr(x_lp4, y_lp4, xcorr, len >> 2, max_pitch >> 2);

        find_best_pitch(xcorr, y_lp4, len >> 2, max_pitch >> 2, best_pitch, 0, maxcorr);

        /* Finer search with 2x decimation */
        maxcorr = 1;
        for (i = 0; i < max_pitch >> 1; i++)
        {
            int sum;
            xcorr[i] = 0;
            if (ABS32(i - (2 * best_pitch[0])) > 2 && ABS32(i - (2 * best_pitch[1])) > 2)
            {
                continue;
            }
            sum = 0;
            for (j = 0; j < len >> 1; j++)
            {
                sum += SHR32(MULT16_16(x_lp[j], y[i + j]), shift);
            }
            xcorr[i] = MAX32(-1, sum);
            maxcorr = MAX32(maxcorr, sum);
        }
        find_best_pitch(xcorr, y, len >> 1, max_pitch >> 1, best_pitch, shift + 1, maxcorr);

        /* Refine by pseudo-interpolation */
        if (best_pitch[0] > 0 && best_pitch[0] < (max_pitch >> 1) - 1)
        {
            int a, b, c;
            a = xcorr[best_pitch[0] - 1];
            b = xcorr[best_pitch[0]];
            c = xcorr[best_pitch[0] + 1];
            if ((c - a) > MULT16_32_Q15(22938 /* QCONST16(.7f,15) */, b - a))
            {
                offset = 1;
            }
            else if ((a - c) > MULT16_32_Q15(22938 /* QCONST16(.7f,15) */, b - c))
            {
                offset = -1;
            }
            else
            {
                offset = 0;
            }
        }
        else
        {
            offset = 0;
        }
        pitch = (2 * best_pitch[0]) - offset;
    }
}
