/***********************************************************************
Copyright (c) 2006-2011, Skype Limited. All rights reserved.
Ported to C# from libopus 1.6.1 (silk/stereo_quant_pred.c) by Hot Chai Games LLC
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

using static DotGame.AudioCodecs.Opus.Silk.SigProcFixEnc;
using static DotGame.AudioCodecs.Opus.Silk.Define;
using static DotGame.AudioCodecs.Opus.Silk.SigProcFix;
using static DotGame.AudioCodecs.Opus.Silk.TablesOther;

using System;

namespace DotGame.AudioCodecs.Opus.Silk;

internal static class StereoQuantPred
{
    /* Quantize mid/side predictors */
    internal static void silk_stereo_quant_pred(
        Span<int> pred_Q13,             /* I/O  Predictors (out: quantized)                 */
        Span<sbyte> ix)                 /* O    Quantization indices; C: opus_int8 ix[ 2 ][ 3 ], flattened */
    {
        int i, j, n;
        int low_Q13, step_Q13, lvl_Q13, err_min_Q13, err_Q13, quant_pred_Q13 = 0;

        /* Quantize */
        for (n = 0; n < 2; n++)
        {
            /* Brute-force search over quantization levels */
            err_min_Q13 = silk_int32_MAX;
            for (i = 0; i < STEREO_QUANT_TAB_SIZE - 1; i++)
            {
                low_Q13 = silk_stereo_pred_quant_Q13[i];
                step_Q13 = silk_SMULWB(silk_stereo_pred_quant_Q13[i + 1] - low_Q13,
                    SILK_FIX_CONST(0.5 / STEREO_QUANT_SUB_STEPS, 16));
                for (j = 0; j < STEREO_QUANT_SUB_STEPS; j++)
                {
                    lvl_Q13 = silk_SMLABB(low_Q13, step_Q13, (2 * j) + 1);
                    err_Q13 = silk_abs(pred_Q13[n] - lvl_Q13);
                    if (err_Q13 < err_min_Q13)
                    {
                        err_min_Q13 = err_Q13;
                        quant_pred_Q13 = lvl_Q13;
                        ix[(n * 3) + 0] = (sbyte)i;
                        ix[(n * 3) + 1] = (sbyte)j;
                    }
                    else
                    {
                        /* Error increasing, so we're past the optimum */
                        goto done;
                    }
                }
            }
        done:
            ix[(n * 3) + 2] = (sbyte)silk_DIV32_16(ix[(n * 3) + 0], 3);
            ix[(n * 3) + 0] -= (sbyte)(ix[(n * 3) + 2] * 3);
            pred_Q13[n] = quant_pred_Q13;
        }

        /* Subtract second from first predictor (helps when actually applying these) */
        pred_Q13[0] -= pred_Q13[1];
    }
}
