/***********************************************************************
Copyright (c) 2006-2011, Skype Limited. All rights reserved.
Ported to C# from libopus 1.6.1 (silk/fixed/burg_modified_FIX.c) by Hot Chai Games LLC
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
using DotGame.AudioCodecs.Opus.Celt;
using static DotGame.AudioCodecs.Opus.Silk.SigProcFixEnc;
using static DotGame.AudioCodecs.Opus.Silk.TuningParameters;
using static DotGame.AudioCodecs.Opus.Silk.VectorOpsFIX;
using static DotGame.AudioCodecs.Opus.Silk.SigProcFix;

namespace DotGame.AudioCodecs.Opus.Silk;

internal static class BurgModifiedFIX
{
    private const int MAX_FRAME_SIZE = 384;             /* subfr_length * nb_subfr = ( 0.005 * 16000 + 16 ) * 4 = 384 */

    private const int QA = 25;
    private const int N_BITS_HEAD_ROOM = 3;
    private const int MIN_RSHIFTS = -16;
    private const int MAX_RSHIFTS = 32 - QA;

    /* Compute reflection coefficients from input signal */
    internal static void silk_burg_modified(
        out int res_nrg,                /* O    Residual energy                                             */
        out int res_nrg_Q,              /* O    Residual energy Q value                                     */
        Span<int> A_Q16,                /* O    Prediction coefficients (length order)                      */
        ReadOnlySpan<short> x,          /* I    Input signal, length: nb_subfr * ( D + subfr_length )       */
        int minInvGain_Q30,             /* I    Inverse of max prediction gain                              */
        int subfr_length,               /* I    Input signal subframe length (incl. D preceding samples)    */
        int nb_subfr,                   /* I    Number of subframes stacked in x                            */
        int D)                          /* I    Order                                                       */
    {
        int k, n, s, lz, rshifts, reached_max_gain;
        int C0, num, nrg, rc_Q31, invGain_Q30, Atmp_QA, Atmp1, tmp1, tmp2, x1, x2;
        ReadOnlySpan<short> x_ptr;
        Span<int> C_first_row = stackalloc int[SILK_MAX_ORDER_LPC];
        Span<int> C_last_row = stackalloc int[SILK_MAX_ORDER_LPC];
        Span<int> Af_QA = stackalloc int[SILK_MAX_ORDER_LPC];
        Span<int> CAf = stackalloc int[SILK_MAX_ORDER_LPC + 1];
        Span<int> CAb = stackalloc int[SILK_MAX_ORDER_LPC + 1];
        Span<int> xcorr = stackalloc int[SILK_MAX_ORDER_LPC];
        long C0_64;

        Debug.Assert(subfr_length * nb_subfr <= MAX_FRAME_SIZE);

        /* Compute autocorrelations, added over subframes */
        C0_64 = silk_inner_prod16(x, x, subfr_length * nb_subfr);
        lz = silk_CLZ64(C0_64);
        rshifts = 32 + 1 + N_BITS_HEAD_ROOM - lz;
        if (rshifts > MAX_RSHIFTS)
        {
            rshifts = MAX_RSHIFTS;
        }
        if (rshifts < MIN_RSHIFTS)
        {
            rshifts = MIN_RSHIFTS;
        }

        if (rshifts > 0)
        {
            C0 = (int)silk_RSHIFT64(C0_64, rshifts);
        }
        else
        {
            C0 = silk_LSHIFT32((int)C0_64, -rshifts);
        }

        CAb[0] = CAf[0] = C0 + silk_SMMUL(SILK_FIX_CONST(FIND_LPC_COND_FAC, 32), C0) + 1;                                /* Q(-rshifts) */
        C_first_row.Clear();
        if (rshifts > 0)
        {
            for (s = 0; s < nb_subfr; s++)
            {
                x_ptr = x[(s * subfr_length)..];
                for (n = 1; n < D + 1; n++)
                {
                    C_first_row[n - 1] += (int)silk_RSHIFT64(
                        silk_inner_prod16(x_ptr, x_ptr[n..], subfr_length - n), rshifts);
                }
            }
        }
        else
        {
            for (s = 0; s < nb_subfr; s++)
            {
                int i;
                int d;
                x_ptr = x[(s * subfr_length)..];
                Pitch.celt_pitch_xcorr(x_ptr, x_ptr[1..], xcorr, subfr_length - D, D);
                for (n = 1; n < D + 1; n++)
                {
                    for (i = n + subfr_length - D, d = 0; i < subfr_length; i++)
                    {
                        d = Arch.MAC16_16(d, x_ptr[i], x_ptr[i - n]);
                    }
                    xcorr[n - 1] += d;
                }
                for (n = 1; n < D + 1; n++)
                {
                    C_first_row[n - 1] += silk_LSHIFT32(xcorr[n - 1], -rshifts);
                }
            }
        }
        C_first_row.CopyTo(C_last_row);

        /* Initialize */
        CAb[0] = CAf[0] = C0 + silk_SMMUL(SILK_FIX_CONST(FIND_LPC_COND_FAC, 32), C0) + 1;                                /* Q(-rshifts) */

        invGain_Q30 = 1 << 30;
        reached_max_gain = 0;
        for (n = 0; n < D; n++)
        {
            /* Update first row of correlation matrix (without first element) */
            /* Update last row of correlation matrix (without last element, stored in reversed order) */
            /* Update C * Af */
            /* Update C * flipud(Af) (stored in reversed order) */
            if (rshifts > -2)
            {
                for (s = 0; s < nb_subfr; s++)
                {
                    x_ptr = x[(s * subfr_length)..];
                    x1 = -silk_LSHIFT32(x_ptr[n], 16 - rshifts);                       /* Q(16-rshifts) */
                    x2 = -silk_LSHIFT32(x_ptr[subfr_length - n - 1], 16 - rshifts);    /* Q(16-rshifts) */
                    tmp1 = silk_LSHIFT32(x_ptr[n], QA - 16);                           /* Q(QA-16) */
                    tmp2 = silk_LSHIFT32(x_ptr[subfr_length - n - 1], QA - 16);        /* Q(QA-16) */
                    for (k = 0; k < n; k++)
                    {
                        C_first_row[k] = silk_SMLAWB(C_first_row[k], x1, x_ptr[n - k - 1]); /* Q( -rshifts ) */
                        C_last_row[k] = silk_SMLAWB(C_last_row[k], x2, x_ptr[subfr_length - n + k]); /* Q( -rshifts ) */
                        Atmp_QA = Af_QA[k];
                        tmp1 = silk_SMLAWB(tmp1, Atmp_QA, x_ptr[n - k - 1]);                 /* Q(QA-16) */
                        tmp2 = silk_SMLAWB(tmp2, Atmp_QA, x_ptr[subfr_length - n + k]);      /* Q(QA-16) */
                    }
                    tmp1 = silk_LSHIFT32(-tmp1, 32 - QA - rshifts);                    /* Q(16-rshifts) */
                    tmp2 = silk_LSHIFT32(-tmp2, 32 - QA - rshifts);                    /* Q(16-rshifts) */
                    for (k = 0; k <= n; k++)
                    {
                        CAf[k] = silk_SMLAWB(CAf[k], tmp1, x_ptr[n - k]);                     /* Q( -rshift ) */
                        CAb[k] = silk_SMLAWB(CAb[k], tmp2, x_ptr[subfr_length - n + k - 1]);  /* Q( -rshift ) */
                    }
                }
            }
            else
            {
                for (s = 0; s < nb_subfr; s++)
                {
                    x_ptr = x[(s * subfr_length)..];
                    x1 = -silk_LSHIFT32(x_ptr[n], -rshifts);                           /* Q( -rshifts ) */
                    x2 = -silk_LSHIFT32(x_ptr[subfr_length - n - 1], -rshifts);        /* Q( -rshifts ) */
                    tmp1 = silk_LSHIFT32(x_ptr[n], 17);                                /* Q17 */
                    tmp2 = silk_LSHIFT32(x_ptr[subfr_length - n - 1], 17);             /* Q17 */
                    for (k = 0; k < n; k++)
                    {
                        C_first_row[k] = silk_MLA(C_first_row[k], x1, x_ptr[n - k - 1]); /* Q( -rshifts ) */
                        C_last_row[k] = silk_MLA(C_last_row[k], x2, x_ptr[subfr_length - n + k]); /* Q( -rshifts ) */
                        Atmp1 = silk_RSHIFT_ROUND(Af_QA[k], QA - 17);                    /* Q17 */
                        /* We sometimes get overflows in the multiplications (even beyond +/- 2^32),
                           but they cancel each other and the real result seems to always fit in a 32-bit
                           signed integer. This was determined experimentally, not theoretically (unfortunately). */
                        tmp1 = silk_MLA_ovflw(tmp1, x_ptr[n - k - 1], Atmp1);              /* Q17 */
                        tmp2 = silk_MLA_ovflw(tmp2, x_ptr[subfr_length - n + k], Atmp1);   /* Q17 */
                    }
                    tmp1 = -tmp1;                                                      /* Q17 */
                    tmp2 = -tmp2;                                                      /* Q17 */
                    for (k = 0; k <= n; k++)
                    {
                        CAf[k] = silk_SMLAWW(CAf[k], tmp1,
                            silk_LSHIFT32(x_ptr[n - k], -rshifts - 1));                    /* Q( -rshift ) */
                        CAb[k] = silk_SMLAWW(CAb[k], tmp2,
                            silk_LSHIFT32(x_ptr[subfr_length - n + k - 1], -rshifts - 1)); /* Q( -rshift ) */
                    }
                }
            }

            /* Calculate nominator and denominator for the next order reflection (parcor) coefficient */
            tmp1 = C_first_row[n];                                             /* Q( -rshifts ) */
            tmp2 = C_last_row[n];                                              /* Q( -rshifts ) */
            num = 0;                                                           /* Q( -rshifts ) */
            nrg = silk_ADD32(CAb[0], CAf[0]);                                  /* Q( 1-rshifts ) */
            for (k = 0; k < n; k++)
            {
                Atmp_QA = Af_QA[k];
                lz = silk_CLZ32(silk_abs(Atmp_QA)) - 1;
                lz = silk_min(32 - QA, lz);
                Atmp1 = silk_LSHIFT32(Atmp_QA, lz);                            /* Q( QA + lz ) */

                tmp1 = silk_ADD_LSHIFT32(tmp1, silk_SMMUL(C_last_row[n - k - 1], Atmp1), 32 - QA - lz);  /* Q( -rshifts ) */
                tmp2 = silk_ADD_LSHIFT32(tmp2, silk_SMMUL(C_first_row[n - k - 1], Atmp1), 32 - QA - lz); /* Q( -rshifts ) */
                num = silk_ADD_LSHIFT32(num, silk_SMMUL(CAb[n - k], Atmp1), 32 - QA - lz);              /* Q( -rshifts ) */
                nrg = silk_ADD_LSHIFT32(nrg, silk_SMMUL(silk_ADD32(CAb[k + 1], CAf[k + 1]),
                                                                    Atmp1), 32 - QA - lz);             /* Q( 1-rshifts ) */
            }
            CAf[n + 1] = tmp1;                                                 /* Q( -rshifts ) */
            CAb[n + 1] = tmp2;                                                 /* Q( -rshifts ) */
            num = silk_ADD32(num, tmp2);                                       /* Q( -rshifts ) */
            num = silk_LSHIFT32(-num, 1);                                      /* Q( 1-rshifts ) */

            /* Calculate the next order reflection (parcor) coefficient */
            if (silk_abs(num) < nrg)
            {
                rc_Q31 = silk_DIV32_varQ(num, nrg, 31);
            }
            else
            {
                rc_Q31 = (num > 0) ? silk_int32_MAX : silk_int32_MIN;
            }

            /* Update inverse prediction gain */
            tmp1 = (1 << 30) - silk_SMMUL(rc_Q31, rc_Q31);
            tmp1 = silk_LSHIFT(silk_SMMUL(invGain_Q30, tmp1), 2);
            if (tmp1 <= minInvGain_Q30)
            {
                /* Max prediction gain exceeded; set reflection coefficient such that max prediction gain is exactly hit */
                tmp2 = (1 << 30) - silk_DIV32_varQ(minInvGain_Q30, invGain_Q30, 30);            /* Q30 */
                rc_Q31 = silk_SQRT_APPROX(tmp2);                                  /* Q15 */
                if (rc_Q31 > 0)
                {
                    /* Newton-Raphson iteration */
                    rc_Q31 = silk_RSHIFT32(rc_Q31 + silk_DIV32(tmp2, rc_Q31), 1);    /* Q15 */
                    rc_Q31 = silk_LSHIFT32(rc_Q31, 16);                              /* Q31 */
                    if (num < 0)
                    {
                        /* Ensure adjusted reflection coefficients has the original sign */
                        rc_Q31 = -rc_Q31;
                    }
                }
                invGain_Q30 = minInvGain_Q30;
                reached_max_gain = 1;
            }
            else
            {
                invGain_Q30 = tmp1;
            }

            /* Update the AR coefficients */
            for (k = 0; k < (n + 1) >> 1; k++)
            {
                tmp1 = Af_QA[k];                                                  /* QA */
                tmp2 = Af_QA[n - k - 1];                                          /* QA */
                Af_QA[k] = silk_ADD_LSHIFT32(tmp1, silk_SMMUL(tmp2, rc_Q31), 1);       /* QA */
                Af_QA[n - k - 1] = silk_ADD_LSHIFT32(tmp2, silk_SMMUL(tmp1, rc_Q31), 1); /* QA */
            }
            Af_QA[n] = silk_RSHIFT32(rc_Q31, 31 - QA);                            /* QA */

            if (reached_max_gain != 0)
            {
                /* Reached max prediction gain; set remaining coefficients to zero and exit loop */
                for (k = n + 1; k < D; k++)
                {
                    Af_QA[k] = 0;
                }
                break;
            }

            /* Update C * Af and C * Ab */
            for (k = 0; k <= n + 1; k++)
            {
                tmp1 = CAf[k];                                                    /* Q( -rshifts ) */
                tmp2 = CAb[n - k + 1];                                            /* Q( -rshifts ) */
                CAf[k] = silk_ADD_LSHIFT32(tmp1, silk_SMMUL(tmp2, rc_Q31), 1);         /* Q( -rshifts ) */
                CAb[n - k + 1] = silk_ADD_LSHIFT32(tmp2, silk_SMMUL(tmp1, rc_Q31), 1); /* Q( -rshifts ) */
            }
        }

        if (reached_max_gain != 0)
        {
            for (k = 0; k < D; k++)
            {
                /* Scale coefficients */
                A_Q16[k] = -silk_RSHIFT_ROUND(Af_QA[k], QA - 16);
            }
            /* Subtract energy of preceding samples from C0 */
            if (rshifts > 0)
            {
                for (s = 0; s < nb_subfr; s++)
                {
                    x_ptr = x[(s * subfr_length)..];
                    C0 -= (int)silk_RSHIFT64(silk_inner_prod16(x_ptr, x_ptr, D), rshifts);
                }
            }
            else
            {
                for (s = 0; s < nb_subfr; s++)
                {
                    x_ptr = x[(s * subfr_length)..];
                    C0 -= silk_LSHIFT32(silk_inner_prod_aligned(x_ptr, x_ptr, D), -rshifts);
                }
            }
            /* Approximate residual energy */
            res_nrg = silk_LSHIFT(silk_SMMUL(invGain_Q30, C0), 2);
            res_nrg_Q = -rshifts;
        }
        else
        {
            /* Return residual energy */
            nrg = CAf[0];                                                         /* Q( -rshifts ) */
            tmp1 = 1 << 16;                                                       /* Q16 */
            for (k = 0; k < D; k++)
            {
                Atmp1 = silk_RSHIFT_ROUND(Af_QA[k], QA - 16);                     /* Q16 */
                nrg = silk_SMLAWW(nrg, CAf[k + 1], Atmp1);                        /* Q( -rshifts ) */
                tmp1 = silk_SMLAWW(tmp1, Atmp1, Atmp1);                           /* Q16 */
                A_Q16[k] = -Atmp1;
            }
            res_nrg = silk_SMLAWW(nrg, silk_SMMUL(SILK_FIX_CONST(FIND_LPC_COND_FAC, 32), C0), -tmp1);/* Q( -rshifts ) */
            res_nrg_Q = -rshifts;
        }
    }
}
