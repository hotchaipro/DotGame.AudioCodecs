/* Copyright (c) 2007-2008 CSIRO
   Copyright (c) 2007-2010 Xiph.Org Foundation
   Copyright (c) 2008 Gregory Maxwell
   Written by Jean-Marc Valin and Gregory Maxwell
   Ported to C# from libopus 1.6.1 (celt/celt.c, celt/celt.h) by Hot Chai Games LLC */
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
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using static DotGame.AudioCodecs.Opus.Celt.Arch;

namespace DotGame.AudioCodecs.Opus.Celt;

/// <summary>
/// celt/celt.c and the decoder's constants from celt/celt.h: the comb (pitch post-) filter, the
/// band caps, the TF table and the resampling factor.
/// </summary>
internal static class Celt
{
    internal const int COMBFILTER_MAXPERIOD = 1024;
    internal const int COMBFILTER_MINPERIOD = 15;

    internal static readonly byte[] trim_icdf = [126, 124, 119, 109, 87, 41, 19, 9, 4, 2, 0];
    /* Probs: NONE: 21.875%, LIGHT: 6.25%, NORMAL: 65.625%, AGGRESSIVE: 6.25% */
    internal static readonly byte[] spread_icdf = [25, 23, 2, 0];

    internal static readonly byte[] tapset_icdf = [2, 1, 0];

    internal static int resampling_factor(int rate)
    {
        int ret;
        switch (rate)
        {
            case 48000:
                ret = 1;
                break;
            case 24000:
                ret = 2;
                break;
            case 16000:
                ret = 3;
                break;
            case 12000:
                ret = 4;
                break;
            case 8000:
                ret = 6;
                break;
            default:
                ret = 0;
                break;
        }
        return ret;
    }

    /* C's x pointer may be read at negative offsets (x[-T]), so x is given as the buffer it points
       into (xbuf) and its position there (xoff): C's x[k] is xbuf[xoff+k]. */
    private static void comb_filter_const(Span<int> y, ReadOnlySpan<int> xbuf, int xoff, int T, int N,
        short g10, short g11, short g12)
    {
        int x0, x1, x2, x3, x4;
        int i;
        i = 0;
        if (Vector128.IsHardwareAccelerated && T >= 8)
        {
            Vector128<int> v10 = Vector128.Create((int)g10);
            Vector128<int> v11 = Vector128.Create((int)g11);
            Vector128<int> v12 = Vector128.Create((int)g12);
            Vector128<int> one = Vector128.Create(1);
            Vector128<int> sat = Vector128.Create(SIG_SAT);
            Vector128<int> nsat = Vector128.Create(-SIG_SAT);
            for (; i + 4 <= N; i += 4)
            {
                int b = xoff + i - T;
                Vector128<int> xm2 = Vector128.Create((ReadOnlySpan<int>)xbuf.Slice(b - 2, 4));
                Vector128<int> xm1 = Vector128.Create((ReadOnlySpan<int>)xbuf.Slice(b - 1, 4));
                Vector128<int> xc = Vector128.Create((ReadOnlySpan<int>)xbuf.Slice(b, 4));
                Vector128<int> xp1 = Vector128.Create((ReadOnlySpan<int>)xbuf.Slice(b + 1, 4));
                Vector128<int> xp2 = Vector128.Create((ReadOnlySpan<int>)xbuf.Slice(b + 2, 4));
                Vector128<int> xi = Vector128.Create((ReadOnlySpan<int>)xbuf.Slice(xoff + i, 4));
                Vector128<int> yv = xi
                    + Simd.MULT16_32_Q15(v10, xc)
                    + Simd.MULT16_32_Q15(v11, xp1 + xm1)
                    + Simd.MULT16_32_Q15(v12, xp2 + xm2);
                /* A bit of bias seems to help here. */
                Vector128.Clamp(yv - one, nsat, sat).CopyTo(y.Slice(i, 4));
            }
        }
        x4 = xbuf[xoff + i - T - 2];
        x3 = xbuf[xoff + i - T - 1];
        x2 = xbuf[xoff + i - T];
        x1 = xbuf[xoff + i - T + 1];
        for (; i < N; i++)
        {
            x0 = xbuf[xoff + i - T + 2];
            y[i] = xbuf[xoff + i]
                     + MULT_COEF_32(g10, x2)
                     + MULT_COEF_32(g11, ADD32(x1, x3))
                     + MULT_COEF_32(g12, ADD32(x0, x4));
            /* A bit of bias seems to help here. */
            y[i] = SUB32(y[i], 1);
            y[i] = SATURATE(y[i], SIG_SAT);
            x4 = x3;
            x3 = x2;
            x2 = x1;
            x1 = x0;
        }
    }

    private static readonly short[] gains =
    [
        10048, 7112, 4248, /* QCONST16(0.3066406250f, 15), QCONST16(0.2170410156f, 15), QCONST16(0.1296386719f, 15) */
        15200, 8784, 0,    /* QCONST16(0.4638671875f, 15), QCONST16(0.2680664062f, 15), QCONST16(0.f, 15) */
        26208, 3280, 0,    /* QCONST16(0.7998046875f, 15), QCONST16(0.1000976562f, 15), QCONST16(0.f, 15) */
    ];

    /* As in comb_filter_const, C's x[k] is xbuf[xoff+k]. y may be the same memory as C's x
       (in-place filtering). */
    internal static void comb_filter(Span<int> y, Span<int> xbuf, int xoff, int T0, int T1, int N,
        short g0, short g1, int tapset0, int tapset1, ReadOnlySpan<short> window, int overlap)
    {
        int i;
        /* printf ("%d %d %f %f\n", T0, T1, g0, g1); */
        short g00, g01, g02, g10, g11, g12;
        int x0, x1, x2, x3, x4;
        bool inPlace = Unsafe.AreSame(ref MemoryMarshal.GetReference(y), ref xbuf[xoff]);
        if (g0 == 0 && g1 == 0)
        {
            /* OPT: Happens to work without the OPUS_MOVE(), but only because the current encoder already copies x to y */
            if (!inPlace)
            {
                xbuf.Slice(xoff, N).CopyTo(y);
            }
            return;
        }
        /* When the gain is zero, T0 and/or T1 is set to zero. We need
           to have then be at least 2 to avoid processing garbage data. */
        T0 = IMAX(T0, COMBFILTER_MINPERIOD);
        T1 = IMAX(T1, COMBFILTER_MINPERIOD);
        g00 = (short)MULT_COEF_TAPS(g0, gains[(tapset0 * 3) + 0]);
        g01 = (short)MULT_COEF_TAPS(g0, gains[(tapset0 * 3) + 1]);
        g02 = (short)MULT_COEF_TAPS(g0, gains[(tapset0 * 3) + 2]);
        g10 = (short)MULT_COEF_TAPS(g1, gains[(tapset1 * 3) + 0]);
        g11 = (short)MULT_COEF_TAPS(g1, gains[(tapset1 * 3) + 1]);
        g12 = (short)MULT_COEF_TAPS(g1, gains[(tapset1 * 3) + 2]);
        x1 = xbuf[xoff - T1 + 1];
        x2 = xbuf[xoff - T1];
        x3 = xbuf[xoff - T1 - 1];
        x4 = xbuf[xoff - T1 - 2];
        /* If the filter didn't change, we don't need the overlap */
        if (g0 == g1 && T0 == T1 && tapset0 == tapset1)
        {
            overlap = 0;
        }
        i = 0;
        if (Vector128.IsHardwareAccelerated && T0 >= 8 && T1 >= 8)
        {
            Vector128<int> one = Vector128.Create((int)COEF_ONE);
            Vector128<int> v00 = Vector128.Create((int)g00), v01 = Vector128.Create((int)g01), v02 = Vector128.Create((int)g02);
            Vector128<int> v10 = Vector128.Create((int)g10), v11 = Vector128.Create((int)g11), v12 = Vector128.Create((int)g12);
            Vector128<int> three = Vector128.Create(3);
            Vector128<int> sat = Vector128.Create(SIG_SAT);
            Vector128<int> nsat = Vector128.Create(-SIG_SAT);
            for (; i + 4 <= overlap; i += 4)
            {
                Vector128<int> w = Simd.LoadWiden(window.Slice(i));
                Vector128<int> f = Simd.ToInt16(Simd.MULT16_16_Q15(w, w));
                Vector128<int> nf = one - f;
                int b0 = xoff + i - T0;
                int b1 = xoff + i - T1;
                Vector128<int> yv = Vector128.Create((ReadOnlySpan<int>)xbuf.Slice(xoff + i, 4))
                    + Simd.MULT16_32_Q15(Simd.MULT16_16_Q15(nf, v00), Vector128.Create((ReadOnlySpan<int>)xbuf.Slice(b0, 4)))
                    + Simd.MULT16_32_Q15(Simd.MULT16_16_Q15(nf, v01), Vector128.Create((ReadOnlySpan<int>)xbuf.Slice(b0 + 1, 4)) + Vector128.Create((ReadOnlySpan<int>)xbuf.Slice(b0 - 1, 4)))
                    + Simd.MULT16_32_Q15(Simd.MULT16_16_Q15(nf, v02), Vector128.Create((ReadOnlySpan<int>)xbuf.Slice(b0 + 2, 4)) + Vector128.Create((ReadOnlySpan<int>)xbuf.Slice(b0 - 2, 4)))
                    + Simd.MULT16_32_Q15(Simd.MULT16_16_Q15(f, v10), Vector128.Create((ReadOnlySpan<int>)xbuf.Slice(b1, 4)))
                    + Simd.MULT16_32_Q15(Simd.MULT16_16_Q15(f, v11), Vector128.Create((ReadOnlySpan<int>)xbuf.Slice(b1 + 1, 4)) + Vector128.Create((ReadOnlySpan<int>)xbuf.Slice(b1 - 1, 4)))
                    + Simd.MULT16_32_Q15(Simd.MULT16_16_Q15(f, v12), Vector128.Create((ReadOnlySpan<int>)xbuf.Slice(b1 + 2, 4)) + Vector128.Create((ReadOnlySpan<int>)xbuf.Slice(b1 - 2, 4)));
                /* A bit of bias seems to help here. */
                Vector128.Clamp(yv - three, nsat, sat).CopyTo(y.Slice(i, 4));
            }
            x1 = xbuf[xoff + i - T1 + 1];
            x2 = xbuf[xoff + i - T1];
            x3 = xbuf[xoff + i - T1 - 1];
            x4 = xbuf[xoff + i - T1 - 2];
        }
        for (; i < overlap; i++)
        {
            short f;
            x0 = xbuf[xoff + i - T1 + 2];
            f = (short)MULT_COEF(window[i], window[i]);
            y[i] = xbuf[xoff + i]
                     + MULT_COEF_32(MULT_COEF(COEF_ONE - f, g00), xbuf[xoff + i - T0])
                     + MULT_COEF_32(MULT_COEF(COEF_ONE - f, g01), ADD32(xbuf[xoff + i - T0 + 1], xbuf[xoff + i - T0 - 1]))
                     + MULT_COEF_32(MULT_COEF(COEF_ONE - f, g02), ADD32(xbuf[xoff + i - T0 + 2], xbuf[xoff + i - T0 - 2]))
                     + MULT_COEF_32(MULT_COEF(f, g10), x2)
                     + MULT_COEF_32(MULT_COEF(f, g11), ADD32(x1, x3))
                     + MULT_COEF_32(MULT_COEF(f, g12), ADD32(x0, x4));
            /* A bit of bias seems to help here. */
            y[i] = SUB32(y[i], 3);
            y[i] = SATURATE(y[i], SIG_SAT);
            x4 = x3;
            x3 = x2;
            x2 = x1;
            x1 = x0;
        }
        if (g1 == 0)
        {
            /* OPT: Happens to work without the OPUS_MOVE(), but only because the current encoder already copies x to y */
            if (!inPlace)
            {
                xbuf.Slice(xoff + overlap, N - overlap).CopyTo(y.Slice(overlap));
            }
            return;
        }

        /* Compute the part with the constant filter. */
        comb_filter_const(y.Slice(i), xbuf, xoff + i, T1, N - i, g10, g11, g12);
    }

    /* TF change table. Positive values mean better frequency resolution (longer
       effective window), whereas negative values mean better time resolution
       (shorter effective window). The second index is computed as:
       4*isTransient + 2*tf_select + per_band_flag */
    internal static readonly sbyte[] tf_select_table =
    [
        /*isTransient=0     isTransient=1 */
        0, -1, 0, -1,    0, -1, 0, -1, /* 2.5 ms */
        0, -1, 0, -2,    1, 0, 1, -1, /* 5 ms */
        0, -2, 0, -3,    2, 0, 1, -1, /* 10 ms */
        0, -2, 0, -3,    3, 0, 1, -1, /* 20 ms */
    ];

    internal static void init_caps(OpusCustomMode m, Span<int> cap, int LM, int C)
    {
        int i;
        for (i = 0; i < m.nbEBands; i++)
        {
            int N;
            N = (m.eBands[i + 1] - m.eBands[i]) << LM;
            cap[i] = ((m.cache.caps[(m.nbEBands * ((2 * LM) + C - 1)) + i] + 64) * C * N) >> 2;
        }
    }
}
