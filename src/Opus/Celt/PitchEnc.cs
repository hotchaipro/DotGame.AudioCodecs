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
using static DotGame.AudioCodecs.Opus.Common.ec_dec;

namespace DotGame.AudioCodecs.Opus.Celt;

/// <summary>
/// The encoder's parts of celt/pitch.c and celt/pitch.h that the decoder's <see cref="Pitch"/>
/// doesn't have: the pitch-doubling check of the pre-filter (remove_doubling) and what it uses.
/// pitch_downsample and pitch_search are the decoder's.
/// </summary>
[SkipLocalsInit]
internal static class PitchEnc
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void dual_inner_prod(ReadOnlySpan<short> x, ReadOnlySpan<short> y01, ReadOnlySpan<short> y02,
          int N, out int xy1, out int xy2)
    {
        /* Two vectorised inner products: the same integer sums as the C's loop */
        xy1 = Simd.InnerProduct16(x, y01, N);
        xy2 = Simd.InnerProduct16(x, y02, N);
    }

    private static short compute_pitch_gain(int xy, int xx, int yy)
    {
        int x2y2;
        int sx, sy, shift;
        int g;
        short den;
        if (xy == 0 || xx == 0 || yy == 0)
        {
            return 0;
        }
        sx = celt_ilog2(xx) - 14;
        sy = celt_ilog2(yy) - 14;
        shift = sx + sy;
        x2y2 = SHR32(MULT16_16(VSHR32(xx, sx), VSHR32(yy, sy)), 14);
        if ((shift & 1) != 0)
        {
            if (x2y2 < 32768)
            {
                x2y2 <<= 1;
                shift--;
            }
            else
            {
                x2y2 >>= 1;
                shift++;
            }
        }
        den = celt_rsqrt_norm(x2y2);
        g = MULT16_32_Q15(den, xy);
        g = VSHR32(g, (shift >> 1) - 1);
        return EXTRACT16(MAX32(-Q15ONE, MIN32(g, Q15ONE)));
    }

    private static readonly int[] second_check = [0, 0, 3, 2, 3, 2, 5, 2, 3, 2, 3, 2, 5, 2, 3, 2];

    /* C's x is advanced by maxperiod/2 and then read at negative offsets (x[-i]), so it is given
       as the buffer it points into on entry (xbuf, C's x before the advance): C's x[k] after the
       advance is xbuf[xoff+k]. */
    internal static short remove_doubling(ReadOnlySpan<short> xbuf, int maxperiod, int minperiod,
          int N, ref int T0_, int prev_period, short prev_gain)
    {
        int k, i, T, T0;
        short g, g0;
        short pg;
        int xy, xx, yy, xy2;
        Span<int> xcorr = stackalloc int[3];
        int best_xy, best_yy;
        int offset;
        int minperiod0;
        int xoff;

        minperiod0 = minperiod;
        maxperiod /= 2;
        minperiod /= 2;
        T0_ /= 2;
        prev_period /= 2;
        N /= 2;
        xoff = maxperiod;
        if (T0_ >= maxperiod)
        {
            T0_ = maxperiod - 1;
        }

        T = T0 = T0_;
        Span<int> yy_lookup = stackalloc int[maxperiod + 1];
        dual_inner_prod(xbuf.Slice(xoff), xbuf.Slice(xoff), xbuf.Slice(xoff - T0), N, out xx, out xy);
        yy_lookup[0] = xx;
        yy = xx;
        for (i = 1; i <= maxperiod; i++)
        {
            yy = yy + MULT16_16(xbuf[xoff - i], xbuf[xoff - i]) - MULT16_16(xbuf[xoff + N - i], xbuf[xoff + N - i]);
            yy_lookup[i] = MAX32(0, yy);
        }
        yy = yy_lookup[T0];
        best_xy = xy;
        best_yy = yy;
        g = g0 = compute_pitch_gain(xy, xx, yy);
        /* Look for any pitch at T/k */
        for (k = 2; k <= 15; k++)
        {
            int T1, T1b;
            short g1;
            short cont = 0;
            short thresh;
            T1 = (int)celt_udiv((uint)((2 * T0) + k), (uint)(2 * k));
            if (T1 < minperiod)
            {
                break;
            }
            /* Look for another strong correlation at T1b */
            if (k == 2)
            {
                if (T1 + T0 > maxperiod)
                {
                    T1b = T0;
                }
                else
                {
                    T1b = T0 + T1;
                }
            }
            else
            {
                T1b = (int)celt_udiv((uint)((2 * second_check[k] * T0) + k), (uint)(2 * k));
            }
            dual_inner_prod(xbuf.Slice(xoff), xbuf.Slice(xoff - T1), xbuf.Slice(xoff - T1b), N, out xy, out xy2);
            xy = HALF32(xy + xy2);
            yy = HALF32(yy_lookup[T1] + yy_lookup[T1b]);
            g1 = compute_pitch_gain(xy, xx, yy);
            if (ABS32(T1 - prev_period) <= 1)
            {
                cont = prev_gain;
            }
            else if (ABS32(T1 - prev_period) <= 2 && 5 * k * k < T0)
            {
                cont = (short)HALF16(prev_gain);
            }
            else
            {
                cont = 0;
            }
            thresh = (short)MAX16(9830 /* QCONST16(.3f,15) */, MULT16_16_Q15(22938 /* QCONST16(.7f,15) */, g0) - cont);
            /* Bias against very high pitch (very short period) to avoid false-positives
               due to short-term correlation */
            if (T1 < 3 * minperiod)
            {
                thresh = (short)MAX16(13107 /* QCONST16(.4f,15) */, MULT16_16_Q15(27853 /* QCONST16(.85f,15) */, g0) - cont);
            }
            else if (T1 < 2 * minperiod)
            {
                thresh = (short)MAX16(16384 /* QCONST16(.5f,15) */, MULT16_16_Q15(29491 /* QCONST16(.9f,15) */, g0) - cont);
            }
            if (g1 > thresh)
            {
                best_xy = xy;
                best_yy = yy;
                T = T1;
                g = g1;
            }
        }
        best_xy = MAX32(0, best_xy);
        if (best_yy <= best_xy)
        {
            pg = Q15ONE;
        }
        else
        {
            pg = (short)SHR32(frac_div32(best_xy, best_yy + 1), 16);
        }

        for (k = 0; k < 3; k++)
        {
            xcorr[k] = Pitch.celt_inner_prod(xbuf.Slice(xoff), xbuf.Slice(xoff - (T + k - 1)), N);
        }
        if ((xcorr[2] - xcorr[0]) > MULT16_32_Q15(22938 /* QCONST16(.7f,15) */, xcorr[1] - xcorr[0]))
        {
            offset = 1;
        }
        else if ((xcorr[0] - xcorr[2]) > MULT16_32_Q15(22938 /* QCONST16(.7f,15) */, xcorr[1] - xcorr[2]))
        {
            offset = -1;
        }
        else
        {
            offset = 0;
        }
        if (pg > g)
        {
            pg = g;
        }
        T0_ = (2 * T) + offset;

        if (T0_ < minperiod0)
        {
            T0_ = minperiod0;
        }
        return pg;
    }
}
