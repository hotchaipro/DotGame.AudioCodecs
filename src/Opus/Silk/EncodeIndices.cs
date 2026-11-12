/***********************************************************************
Copyright (c) 2006-2011, Skype Limited. All rights reserved.
Ported to C# from libopus 1.6.1 (silk/encode_indices.c) by Hot Chai Games LLC
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
using DotGame.AudioCodecs.Opus.Common;
using static DotGame.AudioCodecs.Opus.Silk.Define;
using static DotGame.AudioCodecs.Opus.Silk.NLSFUnpack;
using static DotGame.AudioCodecs.Opus.Silk.SigProcFix;
using static DotGame.AudioCodecs.Opus.Silk.TablesGain;
using static DotGame.AudioCodecs.Opus.Silk.TablesLTP;
using static DotGame.AudioCodecs.Opus.Silk.TablesOther;
using static DotGame.AudioCodecs.Opus.Silk.TablesPitchLag;

namespace DotGame.AudioCodecs.Opus.Silk;

[SkipLocalsInit]
internal static class EncodeIndices
{
    private const int GAIN_ICDF_ROW = N_LEVELS_QGAIN / 8;

    /* Encode side-information parameters to payload */
    internal static void silk_encode_indices(
        silk_encoder_state psEncC,      /* I/O  Encoder state                               */
        ref ec_enc psRangeEnc,          /* I/O  Compressor data structure                   */
        int FrameIndex,                 /* I    Frame number                                */
        int encode_LBRR,                /* I    Flag indicating LBRR data is being encoded  */
        int condCoding)                 /* I    The type of conditional coding to use       */
    {
        int i, k, typeOffset;
        int encode_absolute_lagIndex, delta_lagIndex;
        Span<short> ec_ix = stackalloc short[MAX_LPC_ORDER];
        Span<byte> pred_Q8 = stackalloc byte[MAX_LPC_ORDER];
        SideInfoIndices psIndices;

        if (encode_LBRR != 0)
        {
            psIndices = psEncC.indices_LBRR[FrameIndex];
        }
        else
        {
            psIndices = psEncC.indices;
        }

        /*******************************************/
        /* Encode signal type and quantizer offset */
        /*******************************************/
        typeOffset = (2 * psIndices.signalType) + psIndices.quantOffsetType;
        Debug.Assert(typeOffset >= 0 && typeOffset < 6);
        Debug.Assert(encode_LBRR == 0 || typeOffset >= 2);
        if (encode_LBRR != 0 || typeOffset >= 2)
        {
            psRangeEnc.ec_enc_icdf(typeOffset - 2, silk_type_offset_VAD_iCDF, 8);
        }
        else
        {
            psRangeEnc.ec_enc_icdf(typeOffset, silk_type_offset_no_VAD_iCDF, 8);
        }

        /****************/
        /* Encode gains */
        /****************/
        /* first subframe */
        if (condCoding == CODE_CONDITIONALLY)
        {
            /* conditional coding */
            Debug.Assert(psIndices.GainsIndices[0] >= 0 && psIndices.GainsIndices[0] < MAX_DELTA_GAIN_QUANT - MIN_DELTA_GAIN_QUANT + 1);
            psRangeEnc.ec_enc_icdf(psIndices.GainsIndices[0], silk_delta_gain_iCDF, 8);
        }
        else
        {
            /* independent coding, in two stages: MSB bits followed by 3 LSBs */
            Debug.Assert(psIndices.GainsIndices[0] >= 0 && psIndices.GainsIndices[0] < N_LEVELS_QGAIN);
            psRangeEnc.ec_enc_icdf(silk_RSHIFT(psIndices.GainsIndices[0], 3), silk_gain_iCDF.AsSpan(psIndices.signalType * GAIN_ICDF_ROW, GAIN_ICDF_ROW), 8);
            psRangeEnc.ec_enc_icdf(psIndices.GainsIndices[0] & 7, silk_uniform8_iCDF, 8);
        }

        /* remaining subframes */
        for (i = 1; i < psEncC.nb_subfr; i++)
        {
            Debug.Assert(psIndices.GainsIndices[i] >= 0 && psIndices.GainsIndices[i] < MAX_DELTA_GAIN_QUANT - MIN_DELTA_GAIN_QUANT + 1);
            psRangeEnc.ec_enc_icdf(psIndices.GainsIndices[i], silk_delta_gain_iCDF, 8);
        }

        /****************/
        /* Encode NLSFs */
        /****************/
        psRangeEnc.ec_enc_icdf(psIndices.NLSFIndices[0], psEncC.psNLSF_CB.CB1_iCDF.AsSpan((psIndices.signalType >> 1) * psEncC.psNLSF_CB.nVectors), 8);
        silk_NLSF_unpack(ec_ix, pred_Q8, psEncC.psNLSF_CB, psIndices.NLSFIndices[0]);
        Debug.Assert(psEncC.psNLSF_CB.order == psEncC.predictLPCOrder);
        for (i = 0; i < psEncC.psNLSF_CB.order; i++)
        {
            if (psIndices.NLSFIndices[i + 1] >= NLSF_QUANT_MAX_AMPLITUDE)
            {
                psRangeEnc.ec_enc_icdf(2 * NLSF_QUANT_MAX_AMPLITUDE, psEncC.psNLSF_CB.ec_iCDF.AsSpan(ec_ix[i]), 8);
                psRangeEnc.ec_enc_icdf(psIndices.NLSFIndices[i + 1] - NLSF_QUANT_MAX_AMPLITUDE, silk_NLSF_EXT_iCDF, 8);
            }
            else if (psIndices.NLSFIndices[i + 1] <= -NLSF_QUANT_MAX_AMPLITUDE)
            {
                psRangeEnc.ec_enc_icdf(0, psEncC.psNLSF_CB.ec_iCDF.AsSpan(ec_ix[i]), 8);
                psRangeEnc.ec_enc_icdf(-psIndices.NLSFIndices[i + 1] - NLSF_QUANT_MAX_AMPLITUDE, silk_NLSF_EXT_iCDF, 8);
            }
            else
            {
                psRangeEnc.ec_enc_icdf(psIndices.NLSFIndices[i + 1] + NLSF_QUANT_MAX_AMPLITUDE, psEncC.psNLSF_CB.ec_iCDF.AsSpan(ec_ix[i]), 8);
            }
        }

        /* Encode NLSF interpolation factor */
        if (psEncC.nb_subfr == MAX_NB_SUBFR)
        {
            Debug.Assert(psIndices.NLSFInterpCoef_Q2 >= 0 && psIndices.NLSFInterpCoef_Q2 < 5);
            psRangeEnc.ec_enc_icdf(psIndices.NLSFInterpCoef_Q2, silk_NLSF_interpolation_factor_iCDF, 8);
        }

        if (psIndices.signalType == TYPE_VOICED)
        {
            /*********************/
            /* Encode pitch lags */
            /*********************/
            /* lag index */
            encode_absolute_lagIndex = 1;
            if (condCoding == CODE_CONDITIONALLY && psEncC.ec_prevSignalType == TYPE_VOICED)
            {
                /* Delta Encoding */
                delta_lagIndex = psIndices.lagIndex - psEncC.ec_prevLagIndex;
                if (delta_lagIndex < -8 || delta_lagIndex > 11)
                {
                    delta_lagIndex = 0;
                }
                else
                {
                    delta_lagIndex = delta_lagIndex + 9;
                    encode_absolute_lagIndex = 0; /* Only use delta */
                }
                Debug.Assert(delta_lagIndex >= 0 && delta_lagIndex < 21);
                psRangeEnc.ec_enc_icdf(delta_lagIndex, silk_pitch_delta_iCDF, 8);
            }
            if (encode_absolute_lagIndex != 0)
            {
                /* Absolute encoding */
                int pitch_high_bits, pitch_low_bits;
                pitch_high_bits = silk_DIV32_16(psIndices.lagIndex, silk_RSHIFT(psEncC.fs_kHz, 1));
                pitch_low_bits = psIndices.lagIndex - silk_SMULBB(pitch_high_bits, silk_RSHIFT(psEncC.fs_kHz, 1));
                Debug.Assert(pitch_low_bits < psEncC.fs_kHz / 2);
                Debug.Assert(pitch_high_bits < 32);
                psRangeEnc.ec_enc_icdf(pitch_high_bits, silk_pitch_lag_iCDF, 8);
                psRangeEnc.ec_enc_icdf(pitch_low_bits, psEncC.pitch_lag_low_bits_iCDF, 8);
            }
            psEncC.ec_prevLagIndex = psIndices.lagIndex;

            /* Contour index */
            Debug.Assert(psIndices.contourIndex >= 0);
            Debug.Assert((psIndices.contourIndex < 34 && psEncC.fs_kHz > 8 && psEncC.nb_subfr == 4) ||
                        (psIndices.contourIndex < 11 && psEncC.fs_kHz == 8 && psEncC.nb_subfr == 4) ||
                        (psIndices.contourIndex < 12 && psEncC.fs_kHz > 8 && psEncC.nb_subfr == 2) ||
                        (psIndices.contourIndex < 3 && psEncC.fs_kHz == 8 && psEncC.nb_subfr == 2));
            psRangeEnc.ec_enc_icdf(psIndices.contourIndex, psEncC.pitch_contour_iCDF, 8);

            /********************/
            /* Encode LTP gains */
            /********************/
            /* PERIndex value */
            Debug.Assert(psIndices.PERIndex >= 0 && psIndices.PERIndex < 3);
            psRangeEnc.ec_enc_icdf(psIndices.PERIndex, silk_LTP_per_index_iCDF, 8);

            /* Codebook Indices */
            for (k = 0; k < psEncC.nb_subfr; k++)
            {
                Debug.Assert(psIndices.LTPIndex[k] >= 0 && psIndices.LTPIndex[k] < (8 << psIndices.PERIndex));
                psRangeEnc.ec_enc_icdf(psIndices.LTPIndex[k], silk_LTP_gain_iCDF_ptrs[psIndices.PERIndex], 8);
            }

            /**********************/
            /* Encode LTP scaling */
            /**********************/
            if (condCoding == CODE_INDEPENDENTLY)
            {
                Debug.Assert(psIndices.LTP_scaleIndex >= 0 && psIndices.LTP_scaleIndex < 3);
                psRangeEnc.ec_enc_icdf(psIndices.LTP_scaleIndex, silk_LTPscale_iCDF, 8);
            }
            Debug.Assert(condCoding == 0 || psIndices.LTP_scaleIndex == 0);
        }

        psEncC.ec_prevSignalType = psIndices.signalType;

        /***************/
        /* Encode seed */
        /***************/
        Debug.Assert(psIndices.Seed >= 0 && psIndices.Seed < 4);
        psRangeEnc.ec_enc_icdf(psIndices.Seed, silk_uniform4_iCDF, 8);
    }
}
