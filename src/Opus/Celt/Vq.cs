/* Copyright (c) 2007-2008 CSIRO
   Copyright (c) 2007-2009 Xiph.Org Foundation
   Written by Jean-Marc Valin
   Ported to C# from libopus 1.6.1 (celt/vq.c) by Hot Chai Games LLC */
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
using System.Runtime.Intrinsics;
using DotGame.AudioCodecs.Opus.Common;
using static DotGame.AudioCodecs.Opus.Celt.Arch;
using static DotGame.AudioCodecs.Opus.Celt.MathOps;

namespace DotGame.AudioCodecs.Opus.Celt;

/// <summary>
/// The decoder side of celt/vq.c: the algebraic pulse decoder (alg_unquant), spreading
/// rotations, and renormalisation.
/// </summary>
[SkipLocalsInit]
internal static class Vq
{
    internal static void norm_scaleup(Span<int> X, int N, int shift)
    {
        int i;
        if (shift <= 0)
        {
            return;
        }
        i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            Vector128<int> round = Vector128.Create((1 << shift) >> 1);
            for (; i + 4 <= N; i += 4)
            {
                Vector128<int> v = Vector128.Create((ReadOnlySpan<int>)X.Slice(i, 4));
                (v << shift).CopyTo(X.Slice(i, 4));
            }
        }
        for (; i < N; i++)
        {
            X[i] = SHL32(X[i], shift);
        }
    }

    internal static void norm_scaledown(Span<int> X, int N, int shift)
    {
        int i;
        if (shift <= 0)
        {
            return;
        }
        i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            Vector128<int> round = Vector128.Create((1 << shift) >> 1);
            for (; i + 4 <= N; i += 4)
            {
                Vector128<int> v = Vector128.Create((ReadOnlySpan<int>)X.Slice(i, 4));
                ((v + round) >> shift).CopyTo(X.Slice(i, 4));
            }
        }
        for (; i < N; i++)
        {
            X[i] = PSHR32(X[i], shift);
        }
    }

    internal static int celt_inner_prod_norm(ReadOnlySpan<int> x, ReadOnlySpan<int> y, int len)
    {
        int i;
        int sum = 0;
        for (i = 0; i < len; i++)
        {
            sum += x[i] * y[i];
        }
        return sum;
    }

    internal static int celt_inner_prod_norm_shift(ReadOnlySpan<int> x, ReadOnlySpan<int> y, int len)
    {
        long sum = Simd.InnerProduct64(x, y, len);
        return (int)(sum >> (2 * (NORM_SHIFT - 14)));
    }

    private static void exp_rotation1(Span<int> X, int len, int stride, short c, short s)
    {
        int i;
        short ms;
        int Xptr;
        Xptr = 0;
        ms = (short)NEG16(s);
        norm_scaledown(X, len, NORM_SHIFT - 14);
        if (stride == 1 && len >= 3)
        {
            /* The forward pass: X[i+1] is the next step's x1 */
            int x1f = X[0];
            for (i = 0; i < len - 1; i++)
            {
                int x2 = X[i + 1];
                int next = EXTRACT16(PSHR32(MAC16_16(MULT16_16(c, x2), s, x1f), 15));
                X[i] = EXTRACT16(PSHR32(MAC16_16(MULT16_16(c, x1f), ms, x2), 15));
                x1f = next;
            }
            X[len - 1] = x1f;
            /* The backward pass, from len-3 down: X[i] is the next step's x2 */
            int x2b = X[len - 2];
            for (i = len - 3; i >= 0; i--)
            {
                int x1 = X[i];
                X[i + 1] = EXTRACT16(PSHR32(MAC16_16(MULT16_16(c, x2b), s, x1), 15));
                x2b = EXTRACT16(PSHR32(MAC16_16(MULT16_16(c, x1), ms, x2b), 15));
            }
            X[0] = x2b;
            norm_scaleup(X, len, NORM_SHIFT - 14);
            return;
        }
        for (i = 0; i < len - stride; i++)
        {
            int x1, x2;
            x1 = X[Xptr];
            x2 = X[Xptr + stride];
            X[Xptr + stride] = EXTRACT16(PSHR32(MAC16_16(MULT16_16(c, x2), s, x1), 15));
            X[Xptr++] = EXTRACT16(PSHR32(MAC16_16(MULT16_16(c, x1), ms, x2), 15));
        }
        Xptr = len - (2 * stride) - 1;
        for (i = len - (2 * stride) - 1; i >= 0; i--)
        {
            int x1, x2;
            x1 = X[Xptr];
            x2 = X[Xptr + stride];
            X[Xptr + stride] = EXTRACT16(PSHR32(MAC16_16(MULT16_16(c, x2), s, x1), 15));
            X[Xptr--] = EXTRACT16(PSHR32(MAC16_16(MULT16_16(c, x1), ms, x2), 15));
        }
        norm_scaleup(X, len, NORM_SHIFT - 14);
    }

    private static readonly int[] SPREAD_FACTOR = [15, 10, 5];

    internal static void exp_rotation(Span<int> X, int len, int dir, int stride, int K, int spread)
    {
        int i;
        short c, s;
        short gain, theta;
        int stride2 = 0;
        int factor;

        if (2 * K >= len || spread == Bands.SPREAD_NONE)
        {
            return;
        }
        factor = SPREAD_FACTOR[spread - 1];

        gain = (short)celt_div(MULT16_16(Q15_ONE, len), len + (factor * K));
        theta = (short)HALF16(MULT16_16_Q15(gain, gain));

        c = celt_cos_norm(EXTEND32(theta));
        s = celt_cos_norm(EXTEND32(SUB16(Q15ONE, theta))); /*  sin(theta) */

        if (len >= 8 * stride)
        {
            stride2 = 1;
            /* This is just a simple (equivalent) way of computing sqrt(len/stride) with rounding.
               It's basically incrementing long as (stride2+0.5)^2 < len/stride. */
            while ((((stride2 * stride2) + stride2) * stride) + (stride >> 2) < len)
            {
                stride2++;
            }
        }
        /*NOTE: As a minor optimization, we could be passing around log2(B), not B, for both this and for
           extract_collapse_mask().*/
        len = (int)ec_dec.celt_udiv((uint)len, (uint)stride);
        for (i = 0; i < stride; i++)
        {
            if (dir < 0)
            {
                if (stride2 != 0)
                {
                    exp_rotation1(X.Slice(i * len), len, stride2, s, c);
                }
                exp_rotation1(X.Slice(i * len), len, 1, c, s);
            }
            else
            {
                exp_rotation1(X.Slice(i * len), len, 1, c, (short)-s);
                if (stride2 != 0)
                {
                    exp_rotation1(X.Slice(i * len), len, stride2, s, (short)-c);
                }
            }
        }
    }

    /* Normalizes the decoded integer pvq codeword to unit norm. */
    private static void normalise_residual(ReadOnlySpan<int> iy, Span<int> X,
          int N, int Ryy, int gain, int shift)
    {
        int i;
        int k;
        int t;
        int g;

        k = celt_ilog2(Ryy) >> 1;
        t = VSHR32(Ryy, (2 * (k - 7)) - 15);
        g = MULT32_32_Q31(celt_rsqrt_norm32(t), gain);
        i = 0;
        _ = shift;
        if (Vector128.IsHardwareAccelerated)
        {
            Vector128<int> vg = Vector128.Create(g);
            for (; i + 4 <= N; i += 4)
            {
                /* MULT16_32_Q15's (opus_val16) cast of iy */
                Vector128<int> v = ((Vector128.Create(iy.Slice(i, 4)) << 16) >> 16);
                Simd.VSHR32(Simd.MULT16_32_Q15(v, vg), k + 15 - NORM_SHIFT).CopyTo(X.Slice(i, 4));
            }
        }
        for (; i < N; i++)
        {
            X[i] = VSHR32(MULT16_32_Q15(iy[i], g), k + 15 - NORM_SHIFT);
        }
    }

    private static uint extract_collapse_mask(ReadOnlySpan<int> iy, int N, int B)
    {
        uint collapse_mask;
        int N0;
        int i;
        if (B <= 1)
        {
            return 1;
        }
        /*NOTE: As a minor optimization, we could be passing around log2(B), not B, for both this and for
           exp_rotation().*/
        N0 = (int)ec_dec.celt_udiv((uint)N, (uint)B);
        collapse_mask = 0;
        i = 0;
        do
        {
            int j;
            uint tmp = 0;
            j = 0;
            do
            {
                tmp |= (uint)iy[(i * N0) + j];
            }
            while (++j < N0);
            collapse_mask |= (uint)((tmp != 0 ? 1 : 0) << i);
        }
        while (++i < B);
        return collapse_mask;
    }

    /* Algebraic pulse decoder
     * @param X Decoded normalised spectrum (returned)
     * @param N Number of samples to decode
     * @param K Number of pulses to use
     * @param dec Entropy decoder state
     * @ret A mask indicating which blocks in the band received pulses
     */
    internal static uint alg_unquant(scoped Span<int> X, int N, int K, int spread, int B,
          ref ec_dec dec, int gain)
    {
        int Ryy;
        uint collapse_mask;
        Span<int> iy = stackalloc int[N];
        int yy_shift = 0;

        Ryy = Cwrs.decode_pulses(iy, N, K, ref dec);
        normalise_residual(iy, X, N, Ryy, gain, yy_shift);
        exp_rotation(X, N, -1, B, K, spread);
        collapse_mask = extract_collapse_mask(iy, N, B);
        return collapse_mask;
    }

    internal static void renormalise_vector(Span<int> X, int N, int gain)
    {
        int i;
        int k;
        int E;
        short g;
        int t;
        int xptr;
        norm_scaledown(X, N, NORM_SHIFT - 14);
        E = EPSILON + celt_inner_prod_norm(X, X, N);
        k = celt_ilog2(E) >> 1;
        t = VSHR32(E, 2 * (k - 7));
        g = (short)MULT32_32_Q31(celt_rsqrt_norm(t), gain);

        xptr = 0;
        for (i = 0; i < N; i++)
        {
            X[xptr] = EXTRACT16(PSHR32(MULT16_16(g, X[xptr]), k + 15 - 14));
            xptr++;
        }
        norm_scaleup(X, N, NORM_SHIFT - 14);
        /*return celt_sqrt(E);*/
    }
}
