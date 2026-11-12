/***********************************************************************
Copyright (c) 2006-2011, Skype Limited. All rights reserved.
Ported to C# from libopus 1.6.1 (silk/fixed/find_LTP_FIX.c) by Hot Chai Games LLC
Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions
are met:
- Redistributions of source code must retain the above copyright notice,
this list of conditions and the following disclaimer.
- Redistributions in binary form must reproduce the above copyright
notice, this list of conditions and the following disclaimer in the
documentation and/or other materials provided with the distribution.
- Neither the name of Internet Society, IETF or IETF Trust, nor the
names of specific contributors, may be used to endorse or promote
products derived from this software without specific prior written
permission.
THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE
LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
POSSIBILITY OF SUCH DAMAGE.
***********************************************************************/

using static DotGame.AudioCodecs.Opus.Silk.CorrMatrixFIX;
using static DotGame.AudioCodecs.Opus.Silk.SigProcFixEnc;
using static DotGame.AudioCodecs.Opus.Silk.TuningParameters;
using static DotGame.AudioCodecs.Opus.Silk.Define;
using static DotGame.AudioCodecs.Opus.Silk.SigProcFix;
using static DotGame.AudioCodecs.Opus.Silk.SumSqrShift;

using System;

namespace DotGame.AudioCodecs.Opus.Silk;

internal static class FindLTPFIX
{
    /* C's r_ptr is read at negative offsets (r_ptr - ( lag[ k ] + LTP_ORDER / 2 )), so it is given as the
       buffer it points into (r_buf) and its position there (r_off): C's r_ptr[ k ] is r_buf[ r_off + k ]. */
    internal static void silk_find_LTP_FIX(
        Span<int> XXLTP_Q17,            /* O    Correlation matrix                                               */
        Span<int> xXLTP_Q17,            /* O    Correlation vector                                                          */
        ReadOnlySpan<short> r_buf,      /* I    Buffer of the residual signal after LPC                                     */
        int r_off,                      /* I    Position of C's r_ptr in r_buf                                              */
        ReadOnlySpan<int> lag,          /* I    LTP lags                                                                    */
        int subfr_length,               /* I    Subframe length                                                             */
        int nb_subfr)                   /* I    Number of subframes                                                         */
    {
        int i, k, extra_shifts;
        int xx_shifts, xX_shifts, XX_shifts;
        int lag_ptr;                    /* C: pointer into the residual; here a position in r_buf */
        int r_ptr = r_off;              /* C: pointer into the residual; here a position in r_buf */
        Span<int> XXLTP_Q17_ptr, xXLTP_Q17_ptr;
        int xx, nrg, temp;

        xXLTP_Q17_ptr = xXLTP_Q17;
        XXLTP_Q17_ptr = XXLTP_Q17;
        for (k = 0; k < nb_subfr; k++)
        {
            lag_ptr = r_ptr - (lag[k] + (LTP_ORDER / 2));

            silk_sum_sqr_shift(out xx, out xx_shifts, r_buf[r_ptr..], subfr_length + LTP_ORDER);                  /* xx in Q( -xx_shifts ) */
            silk_corrMatrix_FIX(r_buf[lag_ptr..], subfr_length, LTP_ORDER, XXLTP_Q17_ptr, out nrg, out XX_shifts); /* XXLTP_Q17_ptr and nrg in Q( -XX_shifts ) */
            extra_shifts = xx_shifts - XX_shifts;
            if (extra_shifts > 0)
            {
                /* Shift XX */
                xX_shifts = xx_shifts;
                for (i = 0; i < LTP_ORDER * LTP_ORDER; i++)
                {
                    XXLTP_Q17_ptr[i] = silk_RSHIFT32(XXLTP_Q17_ptr[i], extra_shifts);              /* Q( -xX_shifts ) */
                }
                nrg = silk_RSHIFT32(nrg, extra_shifts);                                            /* Q( -xX_shifts ) */
            }
            else if (extra_shifts < 0)
            {
                /* Shift xx */
                xX_shifts = XX_shifts;
                xx = silk_RSHIFT32(xx, -extra_shifts);                                             /* Q( -xX_shifts ) */
            }
            else
            {
                xX_shifts = xx_shifts;
            }
            silk_corrVector_FIX(r_buf[lag_ptr..], r_buf[r_ptr..], subfr_length, LTP_ORDER, xXLTP_Q17_ptr, xX_shifts);  /* xXLTP_Q17_ptr in Q( -xX_shifts ) */

            /* At this point all correlations are in Q(-xX_shifts) */
            temp = silk_SMLAWB(1, nrg, SILK_FIX_CONST(LTP_CORR_INV_MAX, 16));
            temp = silk_max(temp, xx);
            /* C: the #else branch (the #if 0 one divides with silk_DIV32_varQ) */
            for (i = 0; i < LTP_ORDER * LTP_ORDER; i++)
            {
                XXLTP_Q17_ptr[i] = (int)(silk_LSHIFT64(XXLTP_Q17_ptr[i], 17) / temp);
            }
            for (i = 0; i < LTP_ORDER; i++)
            {
                xXLTP_Q17_ptr[i] = (int)(silk_LSHIFT64(xXLTP_Q17_ptr[i], 17) / temp);
            }
            r_ptr += subfr_length;
            if (k < nb_subfr - 1)
            {
                /* C moves the pointers past the end after the last subframe; they aren't read after */
                XXLTP_Q17_ptr = XXLTP_Q17_ptr[(LTP_ORDER * LTP_ORDER)..];
                xXLTP_Q17_ptr = xXLTP_Q17_ptr[LTP_ORDER..];
            }
        }
    }
}
