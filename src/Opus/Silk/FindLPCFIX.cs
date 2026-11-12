/***********************************************************************
Copyright (c) 2006-2011, Skype Limited. All rights reserved.
Ported to C# from libopus 1.6.1 (silk/fixed/find_LPC_FIX.c) by Hot Chai Games LLC
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
using static DotGame.AudioCodecs.Opus.Silk.A2NLSF;
using static DotGame.AudioCodecs.Opus.Silk.BurgModifiedFIX;
using static DotGame.AudioCodecs.Opus.Silk.Interpolate;
using static DotGame.AudioCodecs.Opus.Silk.Define;
using static DotGame.AudioCodecs.Opus.Silk.LPCAnalysisFilter;
using static DotGame.AudioCodecs.Opus.Silk.NLSF2A;
using static DotGame.AudioCodecs.Opus.Silk.SigProcFix;
using static DotGame.AudioCodecs.Opus.Silk.SumSqrShift;

namespace DotGame.AudioCodecs.Opus.Silk;

[SkipLocalsInit]
internal static class FindLPCFIX
{
    /* Finds LPC vector from correlations, and converts to NLSF */
    internal static void silk_find_LPC_FIX(
        silk_encoder_state psEncC,      /* I/O  Encoder state                                                               */
        Span<short> NLSF_Q15,           /* O    NLSFs                                                                       */
        ReadOnlySpan<short> x,          /* I    Input signal                                                                */
        int minInvGain_Q30)             /* I    Inverse of max prediction gain                                              */
    {
        int k, subfr_length;
        Span<int> a_Q16 = stackalloc int[MAX_LPC_ORDER];
        int isInterpLower, shift;
        int res_nrg0, res_nrg1;
        int rshift0, rshift1;

        /* Used only for LSF interpolation */
        Span<int> a_tmp_Q16 = stackalloc int[MAX_LPC_ORDER];
        int res_nrg_interp, res_nrg, res_tmp_nrg;
        int res_nrg_interp_Q, res_nrg_Q, res_tmp_nrg_Q;
        Span<short> a_tmp_Q12 = stackalloc short[MAX_LPC_ORDER];
        Span<short> NLSF0_Q15 = stackalloc short[MAX_LPC_ORDER];

        subfr_length = psEncC.subfr_length + psEncC.predictLPCOrder;

        /* Default: no interpolation */
        psEncC.indices.NLSFInterpCoef_Q2 = 4;

        /* Burg AR analysis for the full frame */
        silk_burg_modified(out res_nrg, out res_nrg_Q, a_Q16, x, minInvGain_Q30, subfr_length, psEncC.nb_subfr, psEncC.predictLPCOrder);

        if (psEncC.useInterpolatedNLSFs != 0 && psEncC.first_frame_after_reset == 0 && psEncC.nb_subfr == MAX_NB_SUBFR)
        {
            scoped Span<short> LPC_res;

            /* Optimal solution for last 10 ms */
            silk_burg_modified(out res_tmp_nrg, out res_tmp_nrg_Q, a_tmp_Q16, x[(2 * subfr_length)..], minInvGain_Q30, subfr_length, 2, psEncC.predictLPCOrder);

            /* subtract residual energy here, as that's easier than adding it to the    */
            /* residual energy of the first 10 ms in each iteration of the search below */
            shift = res_tmp_nrg_Q - res_nrg_Q;
            if (shift >= 0)
            {
                if (shift < 32)
                {
                    res_nrg = res_nrg - silk_RSHIFT(res_tmp_nrg, shift);
                }
            }
            else
            {
                Debug.Assert(shift > -32);
                res_nrg = silk_RSHIFT(res_nrg, -shift) - res_tmp_nrg;
                res_nrg_Q = res_tmp_nrg_Q;
            }

            /* Convert to NLSFs */
            silk_A2NLSF(NLSF_Q15, a_tmp_Q16, psEncC.predictLPCOrder);

            LPC_res = stackalloc short[2 * subfr_length];

            /* Search over interpolation indices to find the one with lowest residual energy */
            for (k = 3; k >= 0; k--)
            {
                /* Interpolate NLSFs for first half */
                silk_interpolate(NLSF0_Q15, psEncC.prev_NLSFq_Q15, NLSF_Q15, k, psEncC.predictLPCOrder);

                /* Convert to LPC for residual energy evaluation */
                silk_NLSF2A(a_tmp_Q12, NLSF0_Q15, psEncC.predictLPCOrder);

                /* Calculate residual energy with NLSF interpolation */
                silk_LPC_analysis_filter(LPC_res, x, a_tmp_Q12, 2 * subfr_length, psEncC.predictLPCOrder);

                silk_sum_sqr_shift(out res_nrg0, out rshift0, LPC_res[psEncC.predictLPCOrder..], subfr_length - psEncC.predictLPCOrder);
                silk_sum_sqr_shift(out res_nrg1, out rshift1, LPC_res[(psEncC.predictLPCOrder + subfr_length)..], subfr_length - psEncC.predictLPCOrder);

                /* Add subframe energies from first half frame */
                shift = rshift0 - rshift1;
                if (shift >= 0)
                {
                    res_nrg1 = silk_RSHIFT(res_nrg1, shift);
                    res_nrg_interp_Q = -rshift0;
                }
                else
                {
                    res_nrg0 = silk_RSHIFT(res_nrg0, -shift);
                    res_nrg_interp_Q = -rshift1;
                }
                res_nrg_interp = silk_ADD32(res_nrg0, res_nrg1);

                /* Compare with first half energy without NLSF interpolation, or best interpolated value so far */
                shift = res_nrg_interp_Q - res_nrg_Q;
                if (shift >= 0)
                {
                    if (silk_RSHIFT(res_nrg_interp, shift) < res_nrg)
                    {
                        isInterpLower = silk_TRUE;
                    }
                    else
                    {
                        isInterpLower = silk_FALSE;
                    }
                }
                else
                {
                    if (-shift < 32)
                    {
                        if (res_nrg_interp < silk_RSHIFT(res_nrg, -shift))
                        {
                            isInterpLower = silk_TRUE;
                        }
                        else
                        {
                            isInterpLower = silk_FALSE;
                        }
                    }
                    else
                    {
                        isInterpLower = silk_FALSE;
                    }
                }

                /* Determine whether current interpolated NLSFs are best so far */
                if (isInterpLower == silk_TRUE)
                {
                    /* Interpolation has lower residual energy */
                    res_nrg = res_nrg_interp;
                    res_nrg_Q = res_nrg_interp_Q;
                    psEncC.indices.NLSFInterpCoef_Q2 = (sbyte)k;
                }
            }
        }

        if (psEncC.indices.NLSFInterpCoef_Q2 == 4)
        {
            /* NLSF interpolation is currently inactive, calculate NLSFs from full frame AR coefficients */
            silk_A2NLSF(NLSF_Q15, a_Q16, psEncC.predictLPCOrder);
        }

        Debug.Assert(psEncC.indices.NLSFInterpCoef_Q2 == 4 || (psEncC.useInterpolatedNLSFs != 0 && psEncC.first_frame_after_reset == 0 && psEncC.nb_subfr == MAX_NB_SUBFR));
    }
}
