/***********************************************************************
Copyright (c) 2006-2011, Skype Limited. All rights reserved.
Ported to C# from libopus 1.6.1 (silk/fixed/LTP_analysis_filter_FIX.c) by Hot Chai Games LLC
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

using System;
using System.Runtime.CompilerServices;
using static DotGame.AudioCodecs.Opus.Silk.Define;
using static DotGame.AudioCodecs.Opus.Silk.SigProcFix;

namespace DotGame.AudioCodecs.Opus.Silk;

[SkipLocalsInit]
internal static class LTPAnalysisFilterFIX
{
    /* C's x is read at negative offsets (x_ptr - pitchL[ k ] - 2), so it is given as the buffer it points
       into (xbuf) and its position there (xoff): C's x[ k ] is xbuf[ xoff + k ]. */
    internal static void silk_LTP_analysis_filter_FIX(
        Span<short> LTP_res,            /* O    LTP residual signal of length MAX_NB_SUBFR * ( pre_length + subfr_length )  */
        ReadOnlySpan<short> xbuf,       /* I    Buffer of the input signal, with at least max( pitchL ) samples before xoff */
        int xoff,                       /* I    Position of C's x in xbuf                                                   */
        ReadOnlySpan<short> LTPCoef_Q14,/* I    LTP_ORDER LTP coefficients for each MAX_NB_SUBFR subframe                   */
        ReadOnlySpan<int> pitchL,       /* I    Pitch lag, one for each subframe                                            */
        ReadOnlySpan<int> invGains_Q16, /* I    Inverse quantization gains, one for each subframe                           */
        int subfr_length,               /* I    Length of each subframe                                                     */
        int nb_subfr,                   /* I    Number of subframes                                                         */
        int pre_length)                 /* I    Length of the preceding samples starting at &x[0] for each subframe         */
    {
        int x_ptr, x_lag_ptr;           /* C: pointers into x; here positions in xbuf */
        Span<short> Btmp_Q14 = stackalloc short[LTP_ORDER];
        int LTP_res_ptr;                /* C: pointer into LTP_res; here a position */
        int k, i;
        int LTP_est;

        x_ptr = xoff;
        LTP_res_ptr = 0;
        for (k = 0; k < nb_subfr; k++)
        {
            x_lag_ptr = x_ptr - pitchL[k];

            Btmp_Q14[0] = LTPCoef_Q14[k * LTP_ORDER];
            Btmp_Q14[1] = LTPCoef_Q14[(k * LTP_ORDER) + 1];
            Btmp_Q14[2] = LTPCoef_Q14[(k * LTP_ORDER) + 2];
            Btmp_Q14[3] = LTPCoef_Q14[(k * LTP_ORDER) + 3];
            Btmp_Q14[4] = LTPCoef_Q14[(k * LTP_ORDER) + 4];

            /* LTP analysis FIR filter */
            for (i = 0; i < subfr_length + pre_length; i++)
            {
                LTP_res[LTP_res_ptr + i] = xbuf[x_ptr + i];

                /* Long-term prediction */
                LTP_est = silk_SMULBB(xbuf[x_lag_ptr + (LTP_ORDER / 2)], Btmp_Q14[0]);
                LTP_est = silk_SMLABB_ovflw(LTP_est, xbuf[x_lag_ptr + 1], Btmp_Q14[1]);
                LTP_est = silk_SMLABB_ovflw(LTP_est, xbuf[x_lag_ptr], Btmp_Q14[2]);
                LTP_est = silk_SMLABB_ovflw(LTP_est, xbuf[x_lag_ptr - 1], Btmp_Q14[3]);
                LTP_est = silk_SMLABB_ovflw(LTP_est, xbuf[x_lag_ptr - 2], Btmp_Q14[4]);

                LTP_est = silk_RSHIFT_ROUND(LTP_est, 14); /* round and -> Q0*/

                /* Subtract long-term prediction */
                LTP_res[LTP_res_ptr + i] = (short)silk_SAT16(xbuf[x_ptr + i] - LTP_est);

                /* Scale residual */
                LTP_res[LTP_res_ptr + i] = (short)silk_SMULWB(invGains_Q16[k], LTP_res[LTP_res_ptr + i]);

                x_lag_ptr++;
            }

            /* Update pointers */
            LTP_res_ptr += subfr_length + pre_length;
            x_ptr += subfr_length;
        }
    }
}
