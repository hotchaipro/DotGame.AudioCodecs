/***********************************************************************
Copyright (c) 2006-2011, Skype Limited. All rights reserved.
Ported to C# from libopus 1.6.1 (silk/process_NLSFs.c) by Hot Chai Games LLC
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
using static DotGame.AudioCodecs.Opus.Silk.Interpolate;
using static DotGame.AudioCodecs.Opus.Silk.NLSFEncode;
using static DotGame.AudioCodecs.Opus.Silk.NLSFVQWeightsLaroia;
using static DotGame.AudioCodecs.Opus.Silk.SigProcFixEnc;
using static DotGame.AudioCodecs.Opus.Silk.Define;
using static DotGame.AudioCodecs.Opus.Silk.NLSF2A;
using static DotGame.AudioCodecs.Opus.Silk.SigProcFix;

namespace DotGame.AudioCodecs.Opus.Silk;

[SkipLocalsInit]
internal static class ProcessNLSFs
{
    /* Limit, stabilize, convert and quantize NLSFs */
    internal static void silk_process_NLSFs(
        silk_encoder_state psEncC,              /* I/O  Encoder state                               */
        Span<short> PredCoef_Q12,               /* O    Prediction coefficients; C: opus_int16 [ 2 ][ MAX_LPC_ORDER ], flattened */
        Span<short> pNLSF_Q15,                  /* I/O  Normalized LSFs (quant out) (0 - (2^15-1))  */
        ReadOnlySpan<short> prev_NLSFq_Q15)     /* I    Previous Normalized LSFs (0 - (2^15-1))     */
    {
        int i, doInterpolate;
        int NLSF_mu_Q20;
        short i_sqr_Q15;
        Span<short> pNLSF0_temp_Q15 = stackalloc short[MAX_LPC_ORDER];
        Span<short> pNLSFW_QW = stackalloc short[MAX_LPC_ORDER];
        Span<short> pNLSFW0_temp_QW = stackalloc short[MAX_LPC_ORDER];

        Debug.Assert(psEncC.speech_activity_Q8 >= 0);
        Debug.Assert(psEncC.speech_activity_Q8 <= SILK_FIX_CONST(1.0, 8));
        Debug.Assert(psEncC.useInterpolatedNLSFs == 1 || psEncC.indices.NLSFInterpCoef_Q2 == (1 << 2));

        /***********************/
        /* Calculate mu values */
        /***********************/
        /* NLSF_mu  = 0.003 - 0.0015 * psEnc->speech_activity; */
        NLSF_mu_Q20 = silk_SMLAWB(SILK_FIX_CONST(0.003, 20), SILK_FIX_CONST(-0.001, 28), psEncC.speech_activity_Q8);
        if (psEncC.nb_subfr == 2)
        {
            /* Multiply by 1.5 for 10 ms packets */
            NLSF_mu_Q20 = silk_ADD_RSHIFT(NLSF_mu_Q20, NLSF_mu_Q20, 1);
        }

        Debug.Assert(NLSF_mu_Q20 > 0);
        Debug.Assert(NLSF_mu_Q20 <= SILK_FIX_CONST(0.005, 20));

        /* Calculate NLSF weights */
        silk_NLSF_VQ_weights_laroia(pNLSFW_QW, pNLSF_Q15, psEncC.predictLPCOrder);

        /* Update NLSF weights for interpolated NLSFs */
        doInterpolate = (psEncC.useInterpolatedNLSFs == 1) && (psEncC.indices.NLSFInterpCoef_Q2 < 4) ? 1 : 0;
        if (doInterpolate != 0)
        {
            /* Calculate the interpolated NLSF vector for the first half */
            silk_interpolate(pNLSF0_temp_Q15, prev_NLSFq_Q15, pNLSF_Q15,
                psEncC.indices.NLSFInterpCoef_Q2, psEncC.predictLPCOrder);

            /* Calculate first half NLSF weights for the interpolated NLSFs */
            silk_NLSF_VQ_weights_laroia(pNLSFW0_temp_QW, pNLSF0_temp_Q15, psEncC.predictLPCOrder);

            /* Update NLSF weights with contribution from first half */
            i_sqr_Q15 = (short)silk_LSHIFT(silk_SMULBB(psEncC.indices.NLSFInterpCoef_Q2, psEncC.indices.NLSFInterpCoef_Q2), 11);
            for (i = 0; i < psEncC.predictLPCOrder; i++)
            {
                pNLSFW_QW[i] = (short)silk_ADD16(silk_RSHIFT(pNLSFW_QW[i], 1), silk_RSHIFT(
                      silk_SMULBB(pNLSFW0_temp_QW[i], i_sqr_Q15), 16));
                Debug.Assert(pNLSFW_QW[i] >= 1);
            }
        }

        silk_NLSF_encode(psEncC.indices.NLSFIndices, pNLSF_Q15, psEncC.psNLSF_CB, pNLSFW_QW,
            NLSF_mu_Q20, psEncC.NLSF_MSVQ_Survivors, psEncC.indices.signalType);

        /* Convert quantized NLSFs back to LPC coefficients */
        silk_NLSF2A(PredCoef_Q12.Slice(MAX_LPC_ORDER, MAX_LPC_ORDER), pNLSF_Q15, psEncC.predictLPCOrder);

        if (doInterpolate != 0)
        {
            /* Calculate the interpolated, quantized LSF vector for the first half */
            silk_interpolate(pNLSF0_temp_Q15, prev_NLSFq_Q15, pNLSF_Q15,
                psEncC.indices.NLSFInterpCoef_Q2, psEncC.predictLPCOrder);

            /* Convert back to LPC coefficients */
            silk_NLSF2A(PredCoef_Q12.Slice(0, MAX_LPC_ORDER), pNLSF0_temp_Q15, psEncC.predictLPCOrder);
        }
        else
        {
            /* Copy LPC coefficients for first half from second half */
            Debug.Assert(psEncC.predictLPCOrder <= MAX_LPC_ORDER);
            PredCoef_Q12.Slice(MAX_LPC_ORDER, psEncC.predictLPCOrder).CopyTo(PredCoef_Q12);
        }
    }
}
