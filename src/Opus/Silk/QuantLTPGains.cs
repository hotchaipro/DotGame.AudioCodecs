/***********************************************************************
Copyright (c) 2006-2011, Skype Limited. All rights reserved.
Ported to C# from libopus 1.6.1 (silk/quant_LTP_gains.c) by Hot Chai Games LLC
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
using static DotGame.AudioCodecs.Opus.Silk.Lin2log;
using static DotGame.AudioCodecs.Opus.Silk.SigProcFixEnc;
using static DotGame.AudioCodecs.Opus.Silk.TablesLTPEnc;
using static DotGame.AudioCodecs.Opus.Silk.TuningParameters;
using static DotGame.AudioCodecs.Opus.Silk.VQWMatEC;
using static DotGame.AudioCodecs.Opus.Silk.Define;
using static DotGame.AudioCodecs.Opus.Silk.Log2lin;
using static DotGame.AudioCodecs.Opus.Silk.SigProcFix;
using static DotGame.AudioCodecs.Opus.Silk.TablesLTP;

namespace DotGame.AudioCodecs.Opus.Silk;

[SkipLocalsInit]
internal static class QuantLTPGains
{
    internal static void silk_quant_LTP_gains(
        Span<short> B_Q14,              /* O    Quantized LTP gains             */
        Span<sbyte> cbk_index,          /* O    Codebook Index                  */
        ref sbyte periodicity_index,    /* O    Periodicity Index               */
        ref int sum_log_gain_Q7,        /* I/O  Cumulative max prediction gain  */
        out int pred_gain_dB_Q7,        /* O    LTP prediction gain             */
        ReadOnlySpan<int> XX_Q17,       /* I    Correlation matrix in Q18       */
        ReadOnlySpan<int> xX_Q17,       /* I    Correlation vector in Q18       */
        int subfr_len,                  /* I    Number of samples per subframe  */
        int nb_subfr)                   /* I    Number of subframes             */
    {
        int j, k, cbk_size;
        Span<sbyte> temp_idx = stackalloc sbyte[MAX_NB_SUBFR];
        ReadOnlySpan<byte> cl_ptr_Q5;
        ReadOnlySpan<sbyte> cbk_ptr_Q7;
        ReadOnlySpan<byte> cbk_gain_ptr_Q7;
        ReadOnlySpan<int> XX_Q17_ptr, xX_Q17_ptr;
        int res_nrg_Q15_subfr, res_nrg_Q15, rate_dist_Q7_subfr, rate_dist_Q7, min_rate_dist_Q7;
        int sum_log_gain_tmp_Q7, best_sum_log_gain_Q7, max_gain_Q7;
        /* PORT NOTE (see EncAPI.cs): C leaves gain_Q7 uninitialized; silk_VQ_WMat_EC only sets it when a
           codebook vector qualifies. Here it starts at 0. */
        int gain_Q7 = 0;

        /***************************************************/
        /* iterate over different codebooks with different */
        /* rates/distortions, and choose best */
        /***************************************************/
        min_rate_dist_Q7 = silk_int32_MAX;
        best_sum_log_gain_Q7 = 0;
        res_nrg_Q15 = 0;
        for (k = 0; k < 3; k++)
        {
            /* Safety margin for pitch gain control, to take into account factors
               such as state rescaling/rewhitening. */
            int gain_safety = SILK_FIX_CONST(0.4, 7);

            cl_ptr_Q5 = silk_LTP_gain_BITS_Q5_ptrs[k];
            cbk_ptr_Q7 = silk_LTP_vq_ptrs_Q7[k];
            cbk_gain_ptr_Q7 = silk_LTP_vq_gain_ptrs_Q7[k];
            cbk_size = silk_LTP_vq_sizes[k];

            /* Set up pointers to first subframe */
            XX_Q17_ptr = XX_Q17;
            xX_Q17_ptr = xX_Q17;

            res_nrg_Q15 = 0;
            rate_dist_Q7 = 0;
            sum_log_gain_tmp_Q7 = sum_log_gain_Q7;
            for (j = 0; j < nb_subfr; j++)
            {
                max_gain_Q7 = silk_log2lin((SILK_FIX_CONST(MAX_SUM_LOG_GAIN_DB / 6.0, 7) - sum_log_gain_tmp_Q7)
                                            + SILK_FIX_CONST(7, 7)) - gain_safety;
                silk_VQ_WMat_EC(
                    out temp_idx[j],            /* O    index of best codebook vector                           */
                    out res_nrg_Q15_subfr,      /* O    residual energy                                         */
                    out rate_dist_Q7_subfr,     /* O    best weighted quantization error + mu * rate            */
                    ref gain_Q7,                /* O    sum of absolute LTP coefficients                        */
                    XX_Q17_ptr,                 /* I    correlation matrix                                      */
                    xX_Q17_ptr,                 /* I    correlation vector                                      */
                    cbk_ptr_Q7,                 /* I    codebook                                                */
                    cbk_gain_ptr_Q7,            /* I    codebook effective gains                                */
                    cl_ptr_Q5,                  /* I    code length for each codebook vector                    */
                    subfr_len,                  /* I    number of samples per subframe                          */
                    max_gain_Q7,                /* I    maximum sum of absolute LTP coefficients                */
                    cbk_size);                  /* I    number of vectors in codebook                           */

                res_nrg_Q15 = silk_ADD_POS_SAT32(res_nrg_Q15, res_nrg_Q15_subfr);
                rate_dist_Q7 = silk_ADD_POS_SAT32(rate_dist_Q7, rate_dist_Q7_subfr);
                sum_log_gain_tmp_Q7 = silk_max(0, sum_log_gain_tmp_Q7
                                    + silk_lin2log(gain_safety + gain_Q7) - SILK_FIX_CONST(7, 7));

                XX_Q17_ptr = XX_Q17_ptr[(LTP_ORDER * LTP_ORDER)..];
                xX_Q17_ptr = xX_Q17_ptr[LTP_ORDER..];
            }

            if (rate_dist_Q7 <= min_rate_dist_Q7)
            {
                min_rate_dist_Q7 = rate_dist_Q7;
                periodicity_index = (sbyte)k;
                temp_idx[..nb_subfr].CopyTo(cbk_index);
                best_sum_log_gain_Q7 = sum_log_gain_tmp_Q7;
            }
        }

        cbk_ptr_Q7 = silk_LTP_vq_ptrs_Q7[periodicity_index];
        for (j = 0; j < nb_subfr; j++)
        {
            for (k = 0; k < LTP_ORDER; k++)
            {
                B_Q14[(j * LTP_ORDER) + k] = (short)silk_LSHIFT(cbk_ptr_Q7[(cbk_index[j] * LTP_ORDER) + k], 7);
            }
        }

        if (nb_subfr == 2)
        {
            res_nrg_Q15 = silk_RSHIFT32(res_nrg_Q15, 1);
        }
        else
        {
            res_nrg_Q15 = silk_RSHIFT32(res_nrg_Q15, 2);
        }

        sum_log_gain_Q7 = best_sum_log_gain_Q7;
        pred_gain_dB_Q7 = silk_SMULBB(-3, silk_lin2log(res_nrg_Q15) - (15 << 7));
    }
}
