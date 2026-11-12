/* Copyright (c) 2009-2010 Xiph.Org Foundation
   Written by Jean-Marc Valin
   Ported to C# from libopus 1.6.1 (celt/celt_lpc.c) by Hot Chai Games LLC */
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
using DotGame.AudioCodecs.Opus.Common;
using static DotGame.AudioCodecs.Opus.Celt.Arch;
using static DotGame.AudioCodecs.Opus.Celt.MathOps;

namespace DotGame.AudioCodecs.Opus.Celt;

/// <summary>
/// celt/celt_lpc.c: LPC analysis and filtering for the pitch-based packet-loss concealment.
/// </summary>
[SkipLocalsInit]
internal static class CeltLpc
{
    internal const int CELT_LPC_ORDER = 24;

    internal static void _celt_lpc(
          Span<short> _lpc, /* out: [0...p-1] LPC coefficients      */
          ReadOnlySpan<int> ac,  /* in:  [0...p] autocorrelation values  */
          int p
    )
    {
        int i, j;
        int r;
        int error = ac[0];
        Span<int> lpc = stackalloc int[CELT_LPC_ORDER];

        lpc.Slice(0, p).Clear();
        if (ac[0] != 0)
        {
            for (i = 0; i < p; i++)
            {
                /* Sum up this iteration's reflection coefficient */
                int rr = 0;
                long acc = 0;
                for (j = 0; j < i; j++)
                {
                    acc += (long)lpc[j] * (long)ac[i - j];
                }
                rr = (int)(acc >> 31);
                rr += SHR32(ac[i + 1], 6);
                r = -frac_div32(SHL32(rr, 6), error);
                /*  Update LPC coefficients and total error */
                lpc[i] = SHR32(r, 6);
                for (j = 0; j < (i + 1) >> 1; j++)
                {
                    int tmp1, tmp2;
                    tmp1 = lpc[j];
                    tmp2 = lpc[i - 1 - j];
                    lpc[j] = tmp1 + MULT32_32_Q31(r, tmp2);
                    lpc[i - 1 - j] = tmp2 + MULT32_32_Q31(r, tmp1);
                }

                error = error - MULT32_32_Q31(MULT32_32_Q31(r, r), error);
                /* Bail out once we get 30 dB gain */
                if (error <= SHR32(ac[0], 10))
                {
                    break;
                }
            }
        }
        {
            /* Convert the int32 lpcs to int16 and ensure there are no wrap-arounds.
               This reuses the logic in silk_LPC_fit() and silk_bwexpander_32(). Any bug
               fixes should also be applied there. */
            int iter, idx = 0;
            int maxabs, absval, chirp_Q16, chirp_minus_one_Q16;

            for (iter = 0; iter < 10; iter++)
            {
                maxabs = 0;
                for (i = 0; i < p; i++)
                {
                    absval = ABS32(lpc[i]);
                    if (absval > maxabs)
                    {
                        maxabs = absval;
                        idx = i;
                    }
                }
                maxabs = PSHR32(maxabs, 13);  /* Q25->Q12 */

                if (maxabs > 32767)
                {
                    maxabs = MIN32(maxabs, 163838);
                    chirp_Q16 = 65470 /* QCONST32(0.999, 16) */ - DIV32(SHL32(maxabs - 32767, 14),
                                                            SHR32(MULT32_32_32(maxabs, idx + 1), 2));
                    chirp_minus_one_Q16 = chirp_Q16 - 65536;

                    /* Apply bandwidth expansion. */
                    for (i = 0; i < p - 1; i++)
                    {
                        lpc[i] = MULT32_32_Q16(chirp_Q16, lpc[i]);
                        chirp_Q16 += PSHR32(MULT32_32_32(chirp_Q16, chirp_minus_one_Q16), 16);
                    }
                    lpc[p - 1] = MULT32_32_Q16(chirp_Q16, lpc[p - 1]);
                }
                else
                {
                    break;
                }
            }

            if (iter == 10)
            {
                /* If the coeffs still do not fit into the 16 bit range after 10 iterations,
                   fall back to the A(z)=1 filter. */
                lpc.Slice(0, p).Clear();
                _lpc[0] = 4096;  /* Q12 */
            }
            else
            {
                for (i = 0; i < p; i++)
                {
                    _lpc[i] = EXTRACT16(PSHR32(lpc[i], 13));  /* Q25->Q12 */
                }
            }
        }
    }

    /* C's x is read at negative offsets (x[i+j-ord]), so it is given as the buffer it points into
       (xbuf) and its position there (xoff): C's x[k] is xbuf[xoff+k]. */
    internal static void celt_fir(
             ReadOnlySpan<short> xbuf,
             int xoff,
             ReadOnlySpan<short> num,
             Span<short> y,
             int N,
             int ord)
    {
        int i, j;
        Span<short> rnum = stackalloc short[ord];
        Span<int> sum = stackalloc int[4];
        for (i = 0; i < ord; i++)
        {
            rnum[i] = num[ord - i - 1];
        }
        for (i = 0; i < N - 3; i += 4)
        {
            sum[0] = SHL32(EXTEND32(xbuf[xoff + i]), SIG_SHIFT);
            sum[1] = SHL32(EXTEND32(xbuf[xoff + i + 1]), SIG_SHIFT);
            sum[2] = SHL32(EXTEND32(xbuf[xoff + i + 2]), SIG_SHIFT);
            sum[3] = SHL32(EXTEND32(xbuf[xoff + i + 3]), SIG_SHIFT);
            Pitch.xcorr_kernel(rnum, xbuf.Slice(xoff + i - ord), sum, ord);
            y[i] = SROUND16(sum[0], SIG_SHIFT);
            y[i + 1] = SROUND16(sum[1], SIG_SHIFT);
            y[i + 2] = SROUND16(sum[2], SIG_SHIFT);
            y[i + 3] = SROUND16(sum[3], SIG_SHIFT);
        }
        for (; i < N; i++)
        {
            int s = SHL32(EXTEND32(xbuf[xoff + i]), SIG_SHIFT);
            for (j = 0; j < ord; j++)
            {
                s = MAC16_16(s, rnum[j], xbuf[xoff + i + j - ord]);
            }
            y[i] = SROUND16(s, SIG_SHIFT);
        }
    }

    /* _x and _y may be the same memory (in-place filtering). */
    internal static void celt_iir(ReadOnlySpan<int> _x,
             ReadOnlySpan<short> den,
             Span<int> _y,
             int N,
             int ord,
             Span<short> mem)
    {
        int i, j;
        Span<short> rden = stackalloc short[ord];
        Span<short> y = stackalloc short[N + ord];
        Span<int> sum = stackalloc int[4];

        for (i = 0; i < ord; i++)
        {
            rden[i] = den[ord - i - 1];
        }
        for (i = 0; i < ord; i++)
        {
            y[i] = (short)(-mem[ord - i - 1]);
        }
        for (; i < N + ord; i++)
        {
            y[i] = 0;
        }
        for (i = 0; i < N - 3; i += 4)
        {
            /* Unroll by 4 as if it were an FIR filter */
            sum[0] = _x[i];
            sum[1] = _x[i + 1];
            sum[2] = _x[i + 2];
            sum[3] = _x[i + 3];
            Pitch.xcorr_kernel(rden, y.Slice(i), sum, ord);

            /* Patch up the result to compensate for the fact that this is an IIR */
            y[i + ord] = (short)(-SROUND16(sum[0], SIG_SHIFT));
            _y[i] = sum[0];
            sum[1] = MAC16_16(sum[1], y[i + ord], den[0]);
            y[i + ord + 1] = (short)(-SROUND16(sum[1], SIG_SHIFT));
            _y[i + 1] = sum[1];
            sum[2] = MAC16_16(sum[2], y[i + ord + 1], den[0]);
            sum[2] = MAC16_16(sum[2], y[i + ord], den[1]);
            y[i + ord + 2] = (short)(-SROUND16(sum[2], SIG_SHIFT));
            _y[i + 2] = sum[2];

            sum[3] = MAC16_16(sum[3], y[i + ord + 2], den[0]);
            sum[3] = MAC16_16(sum[3], y[i + ord + 1], den[1]);
            sum[3] = MAC16_16(sum[3], y[i + ord], den[2]);
            y[i + ord + 3] = (short)(-SROUND16(sum[3], SIG_SHIFT));
            _y[i + 3] = sum[3];
        }
        for (; i < N; i++)
        {
            int s = _x[i];
            for (j = 0; j < ord; j++)
            {
                s -= MULT16_16(rden[j], y[i + j]);
            }
            y[i + ord] = SROUND16(s, SIG_SHIFT);
            _y[i] = s;
        }
        for (i = 0; i < ord; i++)
        {
            mem[i] = (short)_y[N - i - 1];
        }
    }

    internal static int _celt_autocorr(
                       ReadOnlySpan<short> x,   /*  in: [0...n-1] samples x   */
                       Span<int> ac,  /* out: [0...lag-1] ac values */
                       ReadOnlySpan<short> window,
                       int overlap,
                       int lag,
                       int n
                      )
    {
        int d;
        int i, k;
        int fastN = n - lag;
        int shift;
        scoped ReadOnlySpan<short> xptr;
        Span<short> xx = stackalloc short[n];
        if (overlap == 0)
        {
            xptr = x;
        }
        else
        {
            for (i = 0; i < n; i++)
            {
                xx[i] = x[i];
            }
            for (i = 0; i < overlap; i++)
            {
                short w = COEF2VAL16(window[i]);
                xx[i] = (short)MULT16_16_Q15(x[i], w);
                xx[n - i - 1] = (short)MULT16_16_Q15(x[n - i - 1], w);
            }
            xptr = xx;
        }
        shift = 0;
        {
            int ac0;
            int ac0_shift = celt_ilog2(n + (n >> 4));
            ac0 = 1 + (n << 7);
            if ((n & 1) != 0)
            {
                ac0 += SHR32(MULT16_16(xptr[0], xptr[0]), ac0_shift);
            }
            for (i = n & 1; i < n; i += 2)
            {
                ac0 += SHR32(MULT16_16(xptr[i], xptr[i]), ac0_shift);
                ac0 += SHR32(MULT16_16(xptr[i + 1], xptr[i + 1]), ac0_shift);
            }
            /* Consider the effect of rounding-to-nearest when scaling the signal. */
            ac0 += SHR32(ac0, 7);

            shift = celt_ilog2(ac0) - 30 + ac0_shift + 1;
            shift = shift / 2;
            if (shift > 0)
            {
                for (i = 0; i < n; i++)
                {
                    xx[i] = (short)PSHR32(xptr[i], shift);
                }
                xptr = xx;
            }
            else
            {
                shift = 0;
            }
        }
        Pitch.celt_pitch_xcorr(xptr, xptr, ac, fastN, lag + 1);
        for (k = 0; k <= lag; k++)
        {
            for (i = k + fastN, d = 0; i < n; i++)
            {
                d = MAC16_16(d, xptr[i], xptr[i - k]);
            }
            ac[k] += d;
        }
        shift = 2 * shift;
        if (shift <= 0)
        {
            ac[0] += SHL32(1, -shift);
        }
        if (ac[0] < 268435456)
        {
            int shift2 = 29 - ec_dec.EC_ILOG((uint)ac[0]);
            for (i = 0; i <= lag; i++)
            {
                ac[i] = SHL32(ac[i], shift2);
            }
            shift -= shift2;
        }
        else if (ac[0] >= 536870912)
        {
            int shift2 = 1;
            if (ac[0] >= 1073741824)
            {
                shift2++;
            }
            for (i = 0; i <= lag; i++)
            {
                ac[i] = SHR32(ac[i], shift2);
            }
            shift += shift2;
        }

        return shift;
    }
}
