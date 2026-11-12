/***********************************************************************
Copyright (c) 2006-2011, Skype Limited. All rights reserved.
Ported to C# from libopus 1.6.1 (silk/fixed/warped_autocorrelation_FIX.c) by Hot Chai Games LLC
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
using static DotGame.AudioCodecs.Opus.Silk.DefineEnc;
using static DotGame.AudioCodecs.Opus.Silk.SigProcFix;

namespace DotGame.AudioCodecs.Opus.Silk;

internal static class WarpedAutocorrelationFIX
{
    /* silk/fixed/main_FIX.h */
    internal const int QC = 10;
    internal const int QS = 13;

    /* Autocorrelations for a warped frequency axis */
    internal static void silk_warped_autocorrelation_FIX(
        Span<int> corr,                 /* O    Result [order + 1]                                                          */
        out int scale,                  /* O    Scaling of the correlation vector                                           */
        ReadOnlySpan<short> input,      /* I    Input data to correlate                                                     */
        int warping_Q16,                /* I    Warping coefficient                                                         */
        int length,                     /* I    Length of input                                                             */
        int order)                      /* I    Correlation order (even)                                                    */
    {
        int n, i, lsh;
        int tmp1_QS, tmp2_QS;
        Span<int> state_QS = stackalloc int[MAX_SHAPE_LPC_ORDER + 1];
        Span<long> corr_QC = stackalloc long[MAX_SHAPE_LPC_ORDER + 1];

        state_QS.Clear();
        corr_QC.Clear();

        /* Order must be even */
        Debug.Assert((order & 1) == 0);
        Debug.Assert((2 * QS) - QC >= 0);

        /* Loop over samples */
        for (n = 0; n < length; n++)
        {
            tmp1_QS = silk_LSHIFT32(input[n], QS);
            /* Loop over allpass sections */
            for (i = 0; i < order; i += 2)
            {
                /* Output of allpass section */
                tmp2_QS = silk_SMLAWB(state_QS[i], state_QS[i + 1] - tmp1_QS, warping_Q16);
                state_QS[i] = tmp1_QS;
                corr_QC[i] += silk_RSHIFT64(silk_SMULL(tmp1_QS, state_QS[0]), (2 * QS) - QC);
                /* Output of allpass section */
                tmp1_QS = silk_SMLAWB(state_QS[i + 1], state_QS[i + 2] - tmp2_QS, warping_Q16);
                state_QS[i + 1] = tmp2_QS;
                corr_QC[i + 1] += silk_RSHIFT64(silk_SMULL(tmp2_QS, state_QS[0]), (2 * QS) - QC);
            }
            state_QS[order] = tmp1_QS;
            corr_QC[order] += silk_RSHIFT64(silk_SMULL(tmp1_QS, state_QS[0]), (2 * QS) - QC);
        }

        lsh = silk_CLZ64(corr_QC[0]) - 35;
        lsh = silk_LIMIT(lsh, -12 - QC, 30 - QC);
        scale = -(QC + lsh);
        Debug.Assert(scale >= -30 && scale <= 12);
        if (lsh >= 0)
        {
            for (i = 0; i < order + 1; i++)
            {
                corr[i] = (int)silk_LSHIFT64(corr_QC[i], lsh);  /* C: silk_CHECK_FIT32 */
            }
        }
        else
        {
            for (i = 0; i < order + 1; i++)
            {
                corr[i] = (int)silk_RSHIFT64(corr_QC[i], -lsh);  /* C: silk_CHECK_FIT32 */
            }
        }
        Debug.Assert(corr_QC[0] >= 0); /* If breaking, decrease QC*/
    }
}
