/***********************************************************************
Copyright (c) 2006-2011, Skype Limited. All rights reserved.
Ported to C# from libopus 1.6.1 (silk/stereo_encode_pred.c) by Hot Chai Games LLC
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
using DotGame.AudioCodecs.Opus.Common;
using static DotGame.AudioCodecs.Opus.Silk.Define;
using static DotGame.AudioCodecs.Opus.Silk.TablesOther;

namespace DotGame.AudioCodecs.Opus.Silk;

internal static class StereoEncodePred
{
    /* Entropy code the mid/side quantization indices */
    internal static void silk_stereo_encode_pred(
        ref ec_enc psRangeEnc,          /* I/O  Compressor data structure                   */
        scoped ReadOnlySpan<sbyte> ix)         /* I    Quantization indices; C: opus_int8 ix[ 2 ][ 3 ], flattened */
    {
        int n;

        /* Entropy coding */
        n = (5 * ix[(0 * 3) + 2]) + ix[(1 * 3) + 2];
        Debug.Assert(n < 25);
        psRangeEnc.ec_enc_icdf(n, silk_stereo_pred_joint_iCDF, 8);
        for (n = 0; n < 2; n++)
        {
            Debug.Assert(ix[(n * 3) + 0] < 3);
            Debug.Assert(ix[(n * 3) + 1] < STEREO_QUANT_SUB_STEPS);
            psRangeEnc.ec_enc_icdf(ix[(n * 3) + 0], silk_uniform3_iCDF, 8);
            psRangeEnc.ec_enc_icdf(ix[(n * 3) + 1], silk_uniform5_iCDF, 8);
        }
    }

    /* Entropy code the mid-only flag */
    internal static void silk_stereo_encode_mid_only(
        ref ec_enc psRangeEnc,          /* I/O  Compressor data structure                   */
        sbyte mid_only_flag)
    {
        /* Encode flag that only mid channel is coded */
        psRangeEnc.ec_enc_icdf(mid_only_flag, silk_stereo_only_code_mid_iCDF, 8);
    }
}
