/***********************************************************************
Copyright (c) 2006-2011, Skype Limited. All rights reserved.
Ported to C# from libopus 1.6.1 (silk/LPC_analysis_filter.c) by Hot Chai Games LLC
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
using static DotGame.AudioCodecs.Opus.Silk.SigProcFix;

namespace DotGame.AudioCodecs.Opus.Silk;

internal static class LPCAnalysisFilter
{
    /*******************************************/
    /* LPC analysis filter                     */
    /* NB! State is kept internally and the    */
    /* filter always starts with zero state    */
    /* first d output samples are set to zero  */
    /*******************************************/

    /* C: USE_CELT_FIR is 0, so this is the plain loop. */
    internal static void silk_LPC_analysis_filter(
        Span<short> @out,               /* O    Output signal                                               */
        ReadOnlySpan<short> @in,        /* I    Input signal                                                */
        ReadOnlySpan<short> B,          /* I    MA prediction coefficients, Q12 [order]                     */
        int len,                        /* I    Signal length                                               */
        int d)                          /* I    Filter order                                                */
    {
        int j;
        int ix;
        int out32_Q12, out32;
        ReadOnlySpan<short> in_ptr;

        Debug.Assert(d >= 6);
        Debug.Assert((d & 1) == 0);
        Debug.Assert(d <= len);

        for (ix = d; ix < len; ix++)
        {
            /* C: in_ptr = &in[ ix - 1 ], read from in_ptr[ -( d - 1 ) ] to in_ptr[ 1 ]. Here
               in_ptr = &in[ ix - d ], so C's in_ptr[ k ] is in_ptr[ d - 1 + k ]. */
            in_ptr = @in[(ix - d)..];

            out32_Q12 = silk_SMULBB(in_ptr[d - 1], B[0]);
            /* Allowing wrap around so that two wraps can cancel each other. The rare
               cases where the result wraps around can only be triggered by invalid streams*/
            out32_Q12 = silk_SMLABB_ovflw(out32_Q12, in_ptr[d - 1 - 1], B[1]);
            out32_Q12 = silk_SMLABB_ovflw(out32_Q12, in_ptr[d - 1 - 2], B[2]);
            out32_Q12 = silk_SMLABB_ovflw(out32_Q12, in_ptr[d - 1 - 3], B[3]);
            out32_Q12 = silk_SMLABB_ovflw(out32_Q12, in_ptr[d - 1 - 4], B[4]);
            out32_Q12 = silk_SMLABB_ovflw(out32_Q12, in_ptr[d - 1 - 5], B[5]);
            for (j = 6; j < d; j += 2)
            {
                out32_Q12 = silk_SMLABB_ovflw(out32_Q12, in_ptr[d - 1 - j], B[j]);
                out32_Q12 = silk_SMLABB_ovflw(out32_Q12, in_ptr[d - 1 - j - 1], B[j + 1]);
            }

            /* Subtract prediction */
            out32_Q12 = silk_SUB32_ovflw(silk_LSHIFT(in_ptr[d - 1 + 1], 12), out32_Q12);

            /* Scale to Q0 */
            out32 = silk_RSHIFT_ROUND(out32_Q12, 12);

            /* Saturate output */
            @out[ix] = (short)silk_SAT16(out32);
        }

        /* Set first d output samples to zero */
        @out[..d].Clear();
    }
}
