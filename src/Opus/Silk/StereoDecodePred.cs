/***********************************************************************
Copyright (c) 2006-2011, Skype Limited. All rights reserved.
Ported to C# from libopus 1.6.1 (silk/stereo_decode_pred.c) by Hot Chai Games LLC
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
using DotGame.AudioCodecs.Opus.Common;
using static DotGame.AudioCodecs.Opus.Silk.Define;
using static DotGame.AudioCodecs.Opus.Silk.SigProcFix;
using static DotGame.AudioCodecs.Opus.Silk.TablesOther;

namespace DotGame.AudioCodecs.Opus.Silk;

[SkipLocalsInit]
internal static class StereoDecodePred
{
    /* C: SILK_FIX_CONST( 0.5 / STEREO_QUANT_SUB_STEPS, 16 ) */
    private const int HALF_STEP_Q16 = (int)((0.5 / STEREO_QUANT_SUB_STEPS * (1L << 16)) + 0.5);

    /* Decode mid/side predictors */
    internal static void silk_stereo_decode_pred(
        ref ec_dec psRangeDec,          /* I/O  Compressor data structure                   */
        scoped Span<int> pred_Q13)      /* O    Predictors                                  */
    {
        int n;
        Span<int> ix = stackalloc int[2 * 3];    /* C: opus_int ix[ 2 ][ 3 ], ix[ n ][ m ] is ix[ 3 * n + m ] */
        int low_Q13, step_Q13;

        /* Entropy decoding */
        n = psRangeDec.ec_dec_icdf(silk_stereo_pred_joint_iCDF, 8);
        ix[(3 * 0) + 2] = silk_DIV32_16(n, 5);
        ix[(3 * 1) + 2] = n - (5 * ix[(3 * 0) + 2]);
        for (n = 0; n < 2; n++)
        {
            ix[(3 * n) + 0] = psRangeDec.ec_dec_icdf(silk_uniform3_iCDF, 8);
            ix[(3 * n) + 1] = psRangeDec.ec_dec_icdf(silk_uniform5_iCDF, 8);
        }

        /* Dequantize */
        for (n = 0; n < 2; n++)
        {
            ix[(3 * n) + 0] += 3 * ix[(3 * n) + 2];
            low_Q13 = silk_stereo_pred_quant_Q13[ix[(3 * n) + 0]];
            step_Q13 = silk_SMULWB(silk_stereo_pred_quant_Q13[ix[(3 * n) + 0] + 1] - low_Q13,
                HALF_STEP_Q16);
            pred_Q13[n] = silk_SMLABB(low_Q13, step_Q13, (2 * ix[(3 * n) + 1]) + 1);
        }

        /* Subtract second from first predictor (helps when actually applying these) */
        pred_Q13[0] -= pred_Q13[1];
    }

    /* Decode mid-only flag */
    internal static void silk_stereo_decode_mid_only(
        ref ec_dec psRangeDec,          /* I/O  Compressor data structure                   */
        out int decode_only_mid)        /* O    Flag that only mid channel has been coded   */
    {
        /* Decode flag that only mid channel is coded */
        decode_only_mid = psRangeDec.ec_dec_icdf(silk_stereo_only_code_mid_iCDF, 8);
    }
}
