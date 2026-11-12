/***********************************************************************
Copyright (c) 2006-2011, Skype Limited. All rights reserved.
Ported to C# from libopus 1.6.1 (silk/gain_quant.c) by Hot Chai Games LLC
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
using static DotGame.AudioCodecs.Opus.Silk.Define;
using static DotGame.AudioCodecs.Opus.Silk.Log2lin;
using static DotGame.AudioCodecs.Opus.Silk.SigProcFix;

namespace DotGame.AudioCodecs.Opus.Silk;

internal static class GainQuant
{
    private const int OFFSET = ((MIN_QGAIN_DB * 128) / 6) + (16 * 128);
    private const int INV_SCALE_Q16 = (65536 * (((MAX_QGAIN_DB - MIN_QGAIN_DB) * 128) / 6)) / (N_LEVELS_QGAIN - 1);

    /* Gains scalar dequantization, uniform on log scale */
    internal static void silk_gains_dequant(
        Span<int> gain_Q16,             /* O    quantized gains                             */
        ReadOnlySpan<sbyte> ind,        /* I    gain indices                                */
        ref sbyte prev_ind,             /* I/O  last index in previous frame                */
        int conditional,                /* I    first gain is delta coded if 1              */
        int nb_subfr)                   /* I    number of subframes                          */
    {
        int k, ind_tmp, double_step_size_threshold;

        for (k = 0; k < nb_subfr; k++)
        {
            if (k == 0 && conditional == 0)
            {
                /* Gain index is not allowed to go down more than 16 steps (~21.8 dB) */
                prev_ind = (sbyte)silk_max_int(ind[k], prev_ind - 16);
            }
            else
            {
                /* Delta index */
                ind_tmp = ind[k] + MIN_DELTA_GAIN_QUANT;

                /* Accumulate deltas */
                double_step_size_threshold = (2 * MAX_DELTA_GAIN_QUANT) - N_LEVELS_QGAIN + prev_ind;
                if (ind_tmp > double_step_size_threshold)
                {
                    prev_ind = (sbyte)(prev_ind + (silk_LSHIFT(ind_tmp, 1) - double_step_size_threshold));
                }
                else
                {
                    prev_ind = (sbyte)(prev_ind + ind_tmp);
                }
            }
            prev_ind = (sbyte)silk_LIMIT_int(prev_ind, 0, N_LEVELS_QGAIN - 1);

            /* Scale and convert to linear scale */
            gain_Q16[k] = silk_log2lin(silk_min_32(silk_SMULWB(INV_SCALE_Q16, prev_ind) + OFFSET, 3967)); /* 3967 = 31 in Q7 */
        }
    }
}
