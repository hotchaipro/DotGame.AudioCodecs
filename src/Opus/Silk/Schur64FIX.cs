/***********************************************************************
Copyright (c) 2006-2011, Skype Limited. All rights reserved.
Ported to C# from libopus 1.6.1 (silk/fixed/schur64_FIX.c) by Hot Chai Games LLC
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
using static DotGame.AudioCodecs.Opus.Silk.SigProcFixEnc;
using static DotGame.AudioCodecs.Opus.Silk.SigProcFix;

namespace DotGame.AudioCodecs.Opus.Silk;

[SkipLocalsInit]
internal static class Schur64FIX
{
    /* Slower than schur(), but more accurate.                              */
    /* Uses SMULL(), available on armv4                                     */
    internal static int silk_schur64(   /* O    returns residual energy                                     */
        Span<int> rc_Q16,               /* O    Reflection coefficients [order] Q16                         */
        ReadOnlySpan<int> c,            /* I    Correlations [order+1]                                      */
        int order)                      /* I    Prediction order                                            */
    {
        int k, n;
        /* C: opus_int32 C[ SILK_MAX_ORDER_LPC + 1 ][ 2 ], flattened: C[ k ][ j ] is C[ 2 * k + j ] */
        Span<int> C = stackalloc int[(SILK_MAX_ORDER_LPC + 1) * 2];
        int Ctmp1_Q30, Ctmp2_Q30, rc_tmp_Q31;

        Debug.Assert(order >= 0 && order <= SILK_MAX_ORDER_LPC);

        /* Check for invalid input */
        if (c[0] <= 0)
        {
            rc_Q16[..order].Clear();
            return 0;
        }

        k = 0;
        do
        {
            C[(2 * k) + 0] = C[(2 * k) + 1] = c[k];
        } while (++k <= order);

        for (k = 0; k < order; k++)
        {
            /* Check that we won't be getting an unstable rc, otherwise stop here. */
            if (silk_abs_int32(C[(2 * (k + 1)) + 0]) >= C[(2 * 0) + 1])
            {
                if (C[(2 * (k + 1)) + 0] > 0)
                {
                    rc_Q16[k] = -SILK_FIX_CONST(.99f, 16);
                }
                else
                {
                    rc_Q16[k] = SILK_FIX_CONST(.99f, 16);
                }
                k++;
                break;
            }

            /* Get reflection coefficient: divide two Q30 values and get result in Q31 */
            rc_tmp_Q31 = silk_DIV32_varQ(-C[(2 * (k + 1)) + 0], C[(2 * 0) + 1], 31);

            /* Save the output */
            rc_Q16[k] = silk_RSHIFT_ROUND(rc_tmp_Q31, 15);

            /* Update correlations */
            for (n = 0; n < order - k; n++)
            {
                Ctmp1_Q30 = C[(2 * (n + k + 1)) + 0];
                Ctmp2_Q30 = C[(2 * n) + 1];

                /* Multiply and add the highest int32 */
                C[(2 * (n + k + 1)) + 0] = Ctmp1_Q30 + silk_SMMUL(silk_LSHIFT(Ctmp2_Q30, 1), rc_tmp_Q31);
                C[(2 * n) + 1] = Ctmp2_Q30 + silk_SMMUL(silk_LSHIFT(Ctmp1_Q30, 1), rc_tmp_Q31);
            }
        }

        for (; k < order; k++)
        {
            rc_Q16[k] = 0;
        }

        return silk_max_32(1, C[(2 * 0) + 1]);
    }
}
