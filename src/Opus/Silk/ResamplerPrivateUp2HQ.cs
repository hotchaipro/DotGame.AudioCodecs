/***********************************************************************
Copyright (c) 2006-2011, Skype Limited. All rights reserved.
Ported to C# from libopus 1.6.1 (silk/resampler_private_up2_HQ.c) by Hot Chai Games LLC
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
using static DotGame.AudioCodecs.Opus.Silk.ResamplerRom;
using static DotGame.AudioCodecs.Opus.Silk.SigProcFix;

namespace DotGame.AudioCodecs.Opus.Silk;

internal static class ResamplerPrivateUp2HQ
{
    /* Upsample by a factor 2, high quality */
    /* Uses 2nd order allpass filters for the 2x upsampling, followed by a      */
    /* notch filter just above Nyquist.                                         */
    internal static void silk_resampler_private_up2_HQ(
        Span<int> S,                    /* I/O  Resampler state [ 6 ]       */
        Span<short> @out,               /* O    Output signal [ 2 * len ]   */
        ReadOnlySpan<short> @in,        /* I    Input signal [ len ]        */
        int len)                        /* I    Number of input samples     */
    {
        int k;
        int in32, out32_1, out32_2, Y, X;

        Debug.Assert(silk_resampler_up2_hq_0[0] > 0);
        Debug.Assert(silk_resampler_up2_hq_0[1] > 0);
        Debug.Assert(silk_resampler_up2_hq_0[2] < 0);
        Debug.Assert(silk_resampler_up2_hq_1[0] > 0);
        Debug.Assert(silk_resampler_up2_hq_1[1] > 0);
        Debug.Assert(silk_resampler_up2_hq_1[2] < 0);

        /* Internal variables and state are in Q10 format */
        for (k = 0; k < len; k++)
        {
            /* Convert to Q10 */
            in32 = silk_LSHIFT(@in[k], 10);

            /* First all-pass section for even output sample */
            Y = silk_SUB32(in32, S[0]);
            X = silk_SMULWB(Y, silk_resampler_up2_hq_0[0]);
            out32_1 = silk_ADD32(S[0], X);
            S[0] = silk_ADD32(in32, X);

            /* Second all-pass section for even output sample */
            Y = silk_SUB32(out32_1, S[1]);
            X = silk_SMULWB(Y, silk_resampler_up2_hq_0[1]);
            out32_2 = silk_ADD32(S[1], X);
            S[1] = silk_ADD32(out32_1, X);

            /* Third all-pass section for even output sample */
            Y = silk_SUB32(out32_2, S[2]);
            X = silk_SMLAWB(Y, Y, silk_resampler_up2_hq_0[2]);
            out32_1 = silk_ADD32(S[2], X);
            S[2] = silk_ADD32(out32_2, X);

            /* Apply gain in Q15, convert back to int16 and store to output */
            @out[2 * k] = (short)silk_SAT16(silk_RSHIFT_ROUND(out32_1, 10));

            /* First all-pass section for odd output sample */
            Y = silk_SUB32(in32, S[3]);
            X = silk_SMULWB(Y, silk_resampler_up2_hq_1[0]);
            out32_1 = silk_ADD32(S[3], X);
            S[3] = silk_ADD32(in32, X);

            /* Second all-pass section for odd output sample */
            Y = silk_SUB32(out32_1, S[4]);
            X = silk_SMULWB(Y, silk_resampler_up2_hq_1[1]);
            out32_2 = silk_ADD32(S[4], X);
            S[4] = silk_ADD32(out32_1, X);

            /* Third all-pass section for odd output sample */
            Y = silk_SUB32(out32_2, S[5]);
            X = silk_SMLAWB(Y, Y, silk_resampler_up2_hq_1[2]);
            out32_1 = silk_ADD32(S[5], X);
            S[5] = silk_ADD32(out32_2, X);

            /* Apply gain in Q15, convert back to int16 and store to output */
            @out[(2 * k) + 1] = (short)silk_SAT16(silk_RSHIFT_ROUND(out32_1, 10));
        }
    }

    internal static void silk_resampler_private_up2_HQ_wrapper(
        silk_resampler_state_struct S,  /* I/O  Resampler state (unused)    */
        Span<short> @out,               /* O    Output signal [ 2 * len ]   */
        ReadOnlySpan<short> @in,        /* I    Input signal [ len ]        */
        int len)                        /* I    Number of input samples     */
    {
        silk_resampler_private_up2_HQ(S.sIIR, @out, @in, len);
    }
}
