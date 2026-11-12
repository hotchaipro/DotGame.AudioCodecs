/***********************************************************************
Copyright (c) 2006-2011, Skype Limited. All rights reserved.
Ported to C# from libopus 1.6.1 (silk/structs.h) by Hot Chai Games LLC
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

namespace DotGame.AudioCodecs.Opus.Silk;

/* Structure containing NLSF codebook */
internal sealed class silk_NLSF_CB_struct
{
    internal readonly short nVectors;
    internal readonly short order;
    internal readonly short quantStepSize_Q16;
    internal readonly short invQuantStepSize_Q6;
    internal readonly byte[] CB1_NLSF_Q8;
    internal readonly short[] CB1_Wght_Q9;
    internal readonly byte[] CB1_iCDF;
    internal readonly byte[] pred_Q8;
    internal readonly byte[] ec_sel;
    internal readonly byte[] ec_iCDF;
    internal readonly byte[] ec_Rates_Q5;
    internal readonly short[] deltaMin_Q15;

    internal silk_NLSF_CB_struct(short nVectors, short order, short quantStepSize_Q16, short invQuantStepSize_Q6,
        byte[] CB1_NLSF_Q8, short[] CB1_Wght_Q9, byte[] CB1_iCDF, byte[] pred_Q8, byte[] ec_sel, byte[] ec_iCDF,
        byte[] ec_Rates_Q5, short[] deltaMin_Q15)
    {
        this.nVectors = nVectors;
        this.order = order;
        this.quantStepSize_Q16 = quantStepSize_Q16;
        this.invQuantStepSize_Q6 = invQuantStepSize_Q6;
        this.CB1_NLSF_Q8 = CB1_NLSF_Q8;
        this.CB1_Wght_Q9 = CB1_Wght_Q9;
        this.CB1_iCDF = CB1_iCDF;
        this.pred_Q8 = pred_Q8;
        this.ec_sel = ec_sel;
        this.ec_iCDF = ec_iCDF;
        this.ec_Rates_Q5 = ec_Rates_Q5;
        this.deltaMin_Q15 = deltaMin_Q15;
    }
}

internal sealed class stereo_dec_state
{
    internal readonly short[] pred_prev_Q13 = new short[2];
    internal readonly short[] sMid = new short[2];
    internal readonly short[] sSide = new short[2];

    /// <summary>silk_memset( state, 0, sizeof( stereo_dec_state ) ).</summary>
    internal void Clear()
    {
        Array.Clear(this.pred_prev_Q13);
        Array.Clear(this.sMid);
        Array.Clear(this.sSide);
    }
}

internal sealed class SideInfoIndices
{
    internal readonly sbyte[] GainsIndices = new sbyte[MAX_NB_SUBFR];
    internal readonly sbyte[] LTPIndex = new sbyte[MAX_NB_SUBFR];
    internal readonly sbyte[] NLSFIndices = new sbyte[MAX_LPC_ORDER + 1];
    internal short lagIndex;
    internal sbyte contourIndex;
    internal sbyte signalType;
    internal sbyte quantOffsetType;
    internal sbyte NLSFInterpCoef_Q2;
    internal sbyte PERIndex;
    internal sbyte LTP_scaleIndex;
    internal sbyte Seed;

    internal void Clear()
    {
        Array.Clear(this.GainsIndices);
        Array.Clear(this.LTPIndex);
        Array.Clear(this.NLSFIndices);
        this.lagIndex = 0;
        this.contourIndex = 0;
        this.signalType = 0;
        this.quantOffsetType = 0;
        this.NLSFInterpCoef_Q2 = 0;
        this.PERIndex = 0;
        this.LTP_scaleIndex = 0;
        this.Seed = 0;
    }
}

/* Struct for Packet Loss Concealment */
internal sealed class silk_PLC_struct
{
    internal int pitchL_Q8;                                         /* Pitch lag to use for voiced concealment                          */
    internal readonly short[] LTPCoef_Q14 = new short[LTP_ORDER];   /* LTP coefficients to use for voiced concealment                   */
    internal readonly short[] prevLPC_Q12 = new short[MAX_LPC_ORDER];
    internal int last_frame_lost;                                   /* Was previous frame lost                                          */
    internal int rand_seed;                                         /* Seed for unvoiced signal generation                              */
    internal short randScale_Q14;                                   /* Scaling of unvoiced random signal                                */
    internal int conc_energy;
    internal int conc_energy_shift;
    internal short prevLTP_scale_Q14;
    internal readonly int[] prevGain_Q16 = new int[2];
    internal int fs_kHz;
    internal int nb_subfr;
    internal int subfr_length;
    internal int enable_deep_plc;

    internal void Clear()
    {
        this.pitchL_Q8 = 0;
        Array.Clear(this.LTPCoef_Q14);
        Array.Clear(this.prevLPC_Q12);
        this.last_frame_lost = 0;
        this.rand_seed = 0;
        this.randScale_Q14 = 0;
        this.conc_energy = 0;
        this.conc_energy_shift = 0;
        this.prevLTP_scale_Q14 = 0;
        Array.Clear(this.prevGain_Q16);
        this.fs_kHz = 0;
        this.nb_subfr = 0;
        this.subfr_length = 0;
        this.enable_deep_plc = 0;
    }
}

/* Struct for CNG */
internal sealed class silk_CNG_struct
{
    internal readonly int[] CNG_exc_buf_Q14 = new int[MAX_FRAME_LENGTH];
    internal readonly short[] CNG_smth_NLSF_Q15 = new short[MAX_LPC_ORDER];
    internal readonly int[] CNG_synth_state = new int[MAX_LPC_ORDER];
    internal int CNG_smth_Gain_Q16;
    internal int rand_seed;
    internal int fs_kHz;

    internal void Clear()
    {
        Array.Clear(this.CNG_exc_buf_Q14);
        Array.Clear(this.CNG_smth_NLSF_Q15);
        Array.Clear(this.CNG_synth_state);
        this.CNG_smth_Gain_Q16 = 0;
        this.rand_seed = 0;
        this.fs_kHz = 0;
    }
}

/********************************/
/* Decoder state                */
/********************************/
internal sealed class silk_decoder_state
{
    /* SILK_DECODER_STATE_RESET_START is prev_gain_Q16: with no OSCE, the whole struct */
    internal int prev_gain_Q16;
    internal readonly int[] exc_Q14 = new int[MAX_FRAME_LENGTH];
    internal readonly int[] sLPC_Q14_buf = new int[MAX_LPC_ORDER];
    internal readonly short[] outBuf = new short[MAX_FRAME_LENGTH + (2 * MAX_SUB_FRAME_LENGTH)];  /* Buffer for output signal */
    internal int lagPrev;                                           /* Previous Lag                                                     */
    internal sbyte LastGainIndex;                                   /* Previous gain index                                              */
    internal int fs_kHz;                                            /* Sampling frequency in kHz                                        */
    internal int fs_API_hz;                                         /* API sample frequency (Hz)                                        */
    internal int nb_subfr;                                          /* Number of 5 ms subframes in a frame                              */
    internal int frame_length;                                      /* Frame length (samples)                                           */
    internal int subfr_length;                                      /* Subframe length (samples)                                        */
    internal int ltp_mem_length;                                    /* Length of LTP memory                                             */
    internal int LPC_order;                                         /* LPC order                                                        */
    internal readonly short[] prevNLSF_Q15 = new short[MAX_LPC_ORDER]; /* Used to interpolate LSFs                                      */
    internal int first_frame_after_reset;                           /* Flag for deactivating NLSF interpolation                         */

    /* C: const opus_uint8 *, NULL (empty here) until silk_decoder_set_fs sets them */
    internal byte[] pitch_lag_low_bits_iCDF = [];                   /* Pointer to iCDF table for low bits of pitch lag index            */
    internal byte[] pitch_contour_iCDF = [];                        /* Pointer to iCDF table for pitch contour index                    */

    /* For buffering payload in case of more frames per packet */
    internal int nFramesDecoded;
    internal int nFramesPerPacket;

    /* Specifically for entropy coding */
    internal int ec_prevSignalType;
    internal short ec_prevLagIndex;

    internal readonly int[] VAD_flags = new int[MAX_FRAMES_PER_PACKET];
    internal int LBRR_flag;
    internal readonly int[] LBRR_flags = new int[MAX_FRAMES_PER_PACKET];

    internal readonly silk_resampler_state_struct resampler_state = new();

    /* C: const silk_NLSF_CB_struct *, NULL after a reset. It is only read after silk_decoder_set_fs has
       set it (a reset leaves fs_kHz at 0, so the next silk_decoder_set_fs always does), so a reset
       points it at the NB/MB codebook instead of null. */
    internal silk_NLSF_CB_struct psNLSF_CB = TablesNLSFCBNBMB.silk_NLSF_CB_NB_MB;  /* Pointer to NLSF codebook */

    /* Quantization indices */
    internal readonly SideInfoIndices indices = new();

    /* CNG state */
    internal readonly silk_CNG_struct sCNG = new();

    /* Stuff used for PLC */
    internal int lossCnt;
    internal int prevSignalType;

    internal readonly silk_PLC_struct sPLC = new();

    /* Not in C: the silk_decoder_control that silk_decode_frame allocates on its stack for each frame */
    internal readonly silk_decoder_control sDecCtrl = new();

    /// <summary>
    /// silk_memset( &amp;psDec->SILK_DECODER_STATE_RESET_START, 0, ... ): with no OSCE, the whole state.
    /// </summary>
    internal void Clear()
    {
        this.prev_gain_Q16 = 0;
        Array.Clear(this.exc_Q14);
        Array.Clear(this.sLPC_Q14_buf);
        Array.Clear(this.outBuf);
        this.lagPrev = 0;
        this.LastGainIndex = 0;
        this.fs_kHz = 0;
        this.fs_API_hz = 0;
        this.nb_subfr = 0;
        this.frame_length = 0;
        this.subfr_length = 0;
        this.ltp_mem_length = 0;
        this.LPC_order = 0;
        Array.Clear(this.prevNLSF_Q15);
        this.first_frame_after_reset = 0;
        this.pitch_lag_low_bits_iCDF = [];
        this.pitch_contour_iCDF = [];
        this.nFramesDecoded = 0;
        this.nFramesPerPacket = 0;
        this.ec_prevSignalType = 0;
        this.ec_prevLagIndex = 0;
        Array.Clear(this.VAD_flags);
        this.LBRR_flag = 0;
        Array.Clear(this.LBRR_flags);
        this.resampler_state.Clear();
        this.psNLSF_CB = TablesNLSFCBNBMB.silk_NLSF_CB_NB_MB;
        this.indices.Clear();
        this.sCNG.Clear();
        this.lossCnt = 0;
        this.prevSignalType = 0;
        this.sPLC.Clear();
    }
}

/************************/
/* Decoder control      */
/************************/
internal sealed class silk_decoder_control
{
    /* Prediction and coding parameters */
    internal readonly int[] pitchL = new int[MAX_NB_SUBFR];
    internal readonly int[] Gains_Q16 = new int[MAX_NB_SUBFR];

    /* Holds interpolated and final coefficients. C: opus_int16 PredCoef_Q12[ 2 ][ MAX_LPC_ORDER ], flattened */
    internal readonly short[] PredCoef_Q12 = new short[2 * MAX_LPC_ORDER];
    internal readonly short[] LTPCoef_Q14 = new short[LTP_ORDER * MAX_NB_SUBFR];
    internal int LTP_scale_Q14;

    /// <summary>C's psDecCtrl->PredCoef_Q12[ k ].</summary>
    internal Span<short> PredCoef_Q12_row(int k) => this.PredCoef_Q12.AsSpan(k * MAX_LPC_ORDER, MAX_LPC_ORDER);
}
