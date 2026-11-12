/***********************************************************************
Copyright (c) 2006-2011, Skype Limited. All rights reserved.
Ported to C# from libopus 1.6.1 (silk/decode_indices.c) by Hot Chai Games LLC
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
internal static class DecodeIndices
{
    private const int GAIN_ICDF_ROW = N_LEVELS_QGAIN / 8;

    /* Decode side-information parameters from payload */
    internal static void silk_decode_indices(
        silk_decoder_state psDec,       /* I/O  State                                       */
        ref ec_dec psRangeDec,          /* I/O  Compressor data structure                   */
        int FrameIndex,                 /* I    Frame number                                */
        int decode_LBRR,                /* I    Flag indicating LBRR data is being decoded  */
        int condCoding)                 /* I    The type of conditional coding to use       */
    {
        int i, k, Ix;
        int decode_absolute_lagIndex, delta_lagIndex;
        Span<short> ec_ix = stackalloc short[MAX_LPC_ORDER];
        Span<byte> pred_Q8 = stackalloc byte[MAX_LPC_ORDER];
        silk_NLSF_CB_struct psNLSF_CB = psDec.psNLSF_CB;

        /*******************************************/
        /* Decode signal type and quantizer offset */
        /*******************************************/
        if (decode_LBRR != 0 || psDec.VAD_flags[FrameIndex] != 0)
        {
            Ix = psRangeDec.ec_dec_icdf(silk_type_offset_VAD_iCDF, 8) + 2;
        }
        else
        {
            Ix = psRangeDec.ec_dec_icdf(silk_type_offset_no_VAD_iCDF, 8);
        }
        psDec.indices.signalType = (sbyte)silk_RSHIFT(Ix, 1);
        psDec.indices.quantOffsetType = (sbyte)(Ix & 1);

        /****************/
        /* Decode gains */
        /****************/
        /* First subframe */
        if (condCoding == CODE_CONDITIONALLY)
        {
            /* Conditional coding */
            psDec.indices.GainsIndices[0] = (sbyte)psRangeDec.ec_dec_icdf(silk_delta_gain_iCDF, 8);
        }
        else
        {
            /* Independent coding, in two stages: MSB bits followed by 3 LSBs */
            psDec.indices.GainsIndices[0] = (sbyte)silk_LSHIFT(psRangeDec.ec_dec_icdf(silk_gain_iCDF.AsSpan(psDec.indices.signalType * GAIN_ICDF_ROW, GAIN_ICDF_ROW), 8), 3);
            psDec.indices.GainsIndices[0] += (sbyte)psRangeDec.ec_dec_icdf(silk_uniform8_iCDF, 8);
        }

        /* Remaining subframes */
        for (i = 1; i < psDec.nb_subfr; i++)
        {
            psDec.indices.GainsIndices[i] = (sbyte)psRangeDec.ec_dec_icdf(silk_delta_gain_iCDF, 8);
        }

        /**********************/
        /* Decode LSF Indices */
        /**********************/
        psDec.indices.NLSFIndices[0] = (sbyte)psRangeDec.ec_dec_icdf(psNLSF_CB.CB1_iCDF.AsSpan((psDec.indices.signalType >> 1) * psNLSF_CB.nVectors), 8);
        silk_NLSF_unpack(ec_ix, pred_Q8, psNLSF_CB, psDec.indices.NLSFIndices[0]);
        Debug.Assert(psNLSF_CB.order == psDec.LPC_order);
        for (i = 0; i < psNLSF_CB.order; i++)
        {
            Ix = psRangeDec.ec_dec_icdf(psNLSF_CB.ec_iCDF.AsSpan(ec_ix[i]), 8);
            if (Ix == 0)
            {
                Ix -= psRangeDec.ec_dec_icdf(silk_NLSF_EXT_iCDF, 8);
            }
            else if (Ix == 2 * NLSF_QUANT_MAX_AMPLITUDE)
            {
                Ix += psRangeDec.ec_dec_icdf(silk_NLSF_EXT_iCDF, 8);
            }
            psDec.indices.NLSFIndices[i + 1] = (sbyte)(Ix - NLSF_QUANT_MAX_AMPLITUDE);
        }

        /* Decode LSF interpolation factor */
        if (psDec.nb_subfr == MAX_NB_SUBFR)
        {
            psDec.indices.NLSFInterpCoef_Q2 = (sbyte)psRangeDec.ec_dec_icdf(silk_NLSF_interpolation_factor_iCDF, 8);
        }
        else
        {
            psDec.indices.NLSFInterpCoef_Q2 = 4;
        }

        if (psDec.indices.signalType == TYPE_VOICED)
        {
            /*********************/
            /* Decode pitch lags */
            /*********************/
            /* Get lag index */
            decode_absolute_lagIndex = 1;
            if (condCoding == CODE_CONDITIONALLY && psDec.ec_prevSignalType == TYPE_VOICED)
            {
                /* Decode Delta index */
                delta_lagIndex = (short)psRangeDec.ec_dec_icdf(silk_pitch_delta_iCDF, 8);
                if (delta_lagIndex > 0)
                {
                    delta_lagIndex = delta_lagIndex - 9;
                    psDec.indices.lagIndex = (short)(psDec.ec_prevLagIndex + delta_lagIndex);
                    decode_absolute_lagIndex = 0;
                }
            }
            if (decode_absolute_lagIndex != 0)
            {
                /* Absolute decoding */
                psDec.indices.lagIndex = (short)((short)psRangeDec.ec_dec_icdf(silk_pitch_lag_iCDF, 8) * silk_RSHIFT(psDec.fs_kHz, 1));
                psDec.indices.lagIndex += (short)psRangeDec.ec_dec_icdf(psDec.pitch_lag_low_bits_iCDF, 8);
            }
            psDec.ec_prevLagIndex = psDec.indices.lagIndex;

            /* Get contour index */
            psDec.indices.contourIndex = (sbyte)psRangeDec.ec_dec_icdf(psDec.pitch_contour_iCDF, 8);

            /********************/
            /* Decode LTP gains */
            /********************/
            /* Decode PERIndex value */
            psDec.indices.PERIndex = (sbyte)psRangeDec.ec_dec_icdf(silk_LTP_per_index_iCDF, 8);

            for (k = 0; k < psDec.nb_subfr; k++)
            {
                psDec.indices.LTPIndex[k] = (sbyte)psRangeDec.ec_dec_icdf(silk_LTP_gain_iCDF_ptrs[psDec.indices.PERIndex], 8);
            }

            /**********************/
            /* Decode LTP scaling */
            /**********************/
            if (condCoding == CODE_INDEPENDENTLY)
            {
                psDec.indices.LTP_scaleIndex = (sbyte)psRangeDec.ec_dec_icdf(silk_LTPscale_iCDF, 8);
            }
            else
            {
                psDec.indices.LTP_scaleIndex = 0;
            }
        }
        psDec.ec_prevSignalType = psDec.indices.signalType;

        /***************/
        /* Decode seed */
        /***************/
        psDec.indices.Seed = (sbyte)psRangeDec.ec_dec_icdf(silk_uniform4_iCDF, 8);
    }
}
