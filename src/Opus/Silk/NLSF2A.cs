/***********************************************************************
Copyright (c) 2006-2011, Skype Limited. All rights reserved.
Ported to C# from libopus 1.6.1 (silk/NLSF2A.c) by Hot Chai Games LLC
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
using System.Diagnostics;
using System.Runtime.CompilerServices;
using static DotGame.AudioCodecs.Opus.Silk.Bwexpander32;
using static DotGame.AudioCodecs.Opus.Silk.Define;
using static DotGame.AudioCodecs.Opus.Silk.LPCFit;
using static DotGame.AudioCodecs.Opus.Silk.LPCInvPredGain;
using static DotGame.AudioCodecs.Opus.Silk.SigProcFix;
using static DotGame.AudioCodecs.Opus.Silk.TableLSFCos;

namespace DotGame.AudioCodecs.Opus.Silk;

/* conversion between prediction filter coefficients and LSFs   */
/* order should be even                                         */
/* a piecewise linear approximation maps LSF <-> cos(LSF)       */
/* therefore the result is not accurate LSFs, but the two       */
/* functions are accurate inverses of each other                */
[SkipLocalsInit]
internal static class NLSF2A
{
    private const int QA = 16;

    /* This ordering was found to maximize quality. It improves numerical accuracy of
       silk_NLSF2A_find_poly() compared to "standard" ordering. */
    private static readonly byte[] ordering16 =
    [
        0, 15, 8, 7, 4, 11, 12, 3, 2, 13, 10, 5, 6, 9, 14, 1,
    ];

    private static readonly byte[] ordering10 =
    [
        0, 9, 6, 3, 4, 5, 8, 1, 2, 7,
    ];

    /* helper function for NLSF2A(..) */
    private static void silk_NLSF2A_find_poly(
        Span<int> @out,                 /* O    intermediate polynomial, QA [dd+1]        */
        ReadOnlySpan<int> cLSF,         /* I    vector of interleaved 2*cos(LSFs), QA [d] */
        int dd)                         /* I    polynomial order (= 1/2 * filter order)   */
    {
        int k, n;
        int ftmp;

        @out[0] = silk_LSHIFT(1, QA);
        @out[1] = -cLSF[0];
        for (k = 1; k < dd; k++)
        {
            ftmp = cLSF[2 * k];            /* QA*/
            @out[k + 1] = silk_LSHIFT(@out[k - 1], 1) - (int)silk_RSHIFT_ROUND64(silk_SMULL(ftmp, @out[k]), QA);
            for (n = k; n > 1; n--)
            {
                @out[n] += @out[n - 2] - (int)silk_RSHIFT_ROUND64(silk_SMULL(ftmp, @out[n - 1]), QA);
            }
            @out[1] -= ftmp;
        }
    }

    /* compute whitening filter coefficients from normalized line spectral frequencies */
    internal static void silk_NLSF2A(
        Span<short> a_Q12,              /* O    monic whitening filter coefficients in Q12,  [ d ]          */
        ReadOnlySpan<short> NLSF,       /* I    normalized line spectral frequencies in Q15, [ d ]          */
        int d)                          /* I    filter order (should be even)                               */
    {
        byte[] ordering;
        int k, i, dd;
        Span<int> cos_LSF_QA = stackalloc int[SILK_MAX_ORDER_LPC];
        Span<int> P = stackalloc int[(SILK_MAX_ORDER_LPC / 2) + 1];
        Span<int> Q = stackalloc int[(SILK_MAX_ORDER_LPC / 2) + 1];
        int Ptmp, Qtmp, f_int, f_frac, cos_val, delta;
        Span<int> a32_QA1 = stackalloc int[SILK_MAX_ORDER_LPC];

        Debug.Assert(LSF_COS_TAB_SZ_FIX == 128);
        Debug.Assert(d == 10 || d == 16);

        /* convert LSFs to 2*cos(LSF), using piecewise linear curve from table */
        ordering = d == 16 ? ordering16 : ordering10;
        for (k = 0; k < d; k++)
        {
            Debug.Assert(NLSF[k] >= 0);

            /* f_int on a scale 0-127 (rounded down) */
            f_int = silk_RSHIFT(NLSF[k], 15 - 7);

            /* f_frac, range: 0..255 */
            f_frac = NLSF[k] - silk_LSHIFT(f_int, 15 - 7);

            Debug.Assert(f_int >= 0);
            Debug.Assert(f_int < LSF_COS_TAB_SZ_FIX);

            /* Read start and end value from table */
            cos_val = silk_LSFCosTab_FIX_Q12[f_int];                /* Q12 */
            delta = silk_LSFCosTab_FIX_Q12[f_int + 1] - cos_val;    /* Q12, with a range of 0..200 */

            /* Linear interpolation */
            cos_LSF_QA[ordering[k]] = silk_RSHIFT_ROUND(silk_LSHIFT(cos_val, 8) + silk_MUL(delta, f_frac), 20 - QA); /* QA */
        }

        dd = silk_RSHIFT(d, 1);

        /* generate even and odd polynomials using convolution */
        silk_NLSF2A_find_poly(P, cos_LSF_QA, dd);
        silk_NLSF2A_find_poly(Q, cos_LSF_QA[1..], dd);

        /* convert even and odd polynomials to opus_int32 Q12 filter coefs */
        for (k = 0; k < dd; k++)
        {
            Ptmp = P[k + 1] + P[k];
            Qtmp = Q[k + 1] - Q[k];

            /* the Ptmp and Qtmp values at this stage need to fit in int32 */
            a32_QA1[k] = -Qtmp - Ptmp;        /* QA+1 */
            a32_QA1[d - k - 1] = Qtmp - Ptmp;   /* QA+1 */
        }

        /* Convert int32 coefficients to Q12 int16 coefs */
        silk_LPC_fit(a_Q12, a32_QA1, 12, QA + 1, d);

        for (i = 0; silk_LPC_inverse_pred_gain(a_Q12, d) == 0 && i < MAX_LPC_STABILIZE_ITERATIONS; i++)
        {
            /* Prediction coefficients are (too close to) unstable; apply bandwidth expansion   */
            /* on the unscaled coefficients, convert to Q12 and measure again                   */
            silk_bwexpander_32(a32_QA1, d, 65536 - silk_LSHIFT(2, i));
            for (k = 0; k < d; k++)
            {
                a_Q12[k] = (short)silk_RSHIFT_ROUND(a32_QA1[k], QA + 1 - 12);            /* QA+1 -> Q12 */
            }
        }
    }
}
