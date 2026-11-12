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

using static DotGame.AudioCodecs.Opus.Silk.DefineEnc;
using static DotGame.AudioCodecs.Opus.Silk.Define;

using System;

namespace DotGame.AudioCodecs.Opus.Silk;

/************************************/
/* Noise shaping quantization state */
/************************************/
internal sealed class silk_nsq_state
{
    internal readonly short[] xq = new short[2 * MAX_FRAME_LENGTH]; /* Buffer for quantized output signal                             */
    internal readonly int[] sLTP_shp_Q14 = new int[2 * MAX_FRAME_LENGTH];
    internal readonly int[] sLPC_Q14 = new int[MAX_SUB_FRAME_LENGTH + NSQ_LPC_BUF_LENGTH];
    internal readonly int[] sAR2_Q14 = new int[MAX_SHAPE_LPC_ORDER];
    internal int sLF_AR_shp_Q14;
    internal int sDiff_shp_Q14;
    internal int lagPrev;
    internal int sLTP_buf_idx;
    internal int sLTP_shp_buf_idx;
    internal int rand_seed;
    internal int prev_gain_Q16;
    internal int rewhite_flag;

    /// <summary>silk_memset( NSQ, 0, sizeof( silk_nsq_state ) ).</summary>
    internal void Clear()
    {
        Array.Clear(this.xq);
        Array.Clear(this.sLTP_shp_Q14);
        Array.Clear(this.sLPC_Q14);
        Array.Clear(this.sAR2_Q14);
        this.sLF_AR_shp_Q14 = 0;
        this.sDiff_shp_Q14 = 0;
        this.lagPrev = 0;
        this.sLTP_buf_idx = 0;
        this.sLTP_shp_buf_idx = 0;
        this.rand_seed = 0;
        this.prev_gain_Q16 = 0;
        this.rewhite_flag = 0;
    }

    /// <summary>silk_memcpy( this, src, sizeof( silk_nsq_state ) ).</summary>
    internal void CopyFrom(silk_nsq_state src)
    {
        src.xq.AsSpan().CopyTo(this.xq);
        src.sLTP_shp_Q14.AsSpan().CopyTo(this.sLTP_shp_Q14);
        src.sLPC_Q14.AsSpan().CopyTo(this.sLPC_Q14);
        src.sAR2_Q14.AsSpan().CopyTo(this.sAR2_Q14);
        this.sLF_AR_shp_Q14 = src.sLF_AR_shp_Q14;
        this.sDiff_shp_Q14 = src.sDiff_shp_Q14;
        this.lagPrev = src.lagPrev;
        this.sLTP_buf_idx = src.sLTP_buf_idx;
        this.sLTP_shp_buf_idx = src.sLTP_shp_buf_idx;
        this.rand_seed = src.rand_seed;
        this.prev_gain_Q16 = src.prev_gain_Q16;
        this.rewhite_flag = src.rewhite_flag;
    }
}

/********************************/
/* VAD state                    */
/********************************/
internal sealed class silk_VAD_state
{
    internal readonly int[] AnaState = new int[2];                  /* Analysis filterbank state: 0-8 kHz                                   */
    internal readonly int[] AnaState1 = new int[2];                 /* Analysis filterbank state: 0-4 kHz                                   */
    internal readonly int[] AnaState2 = new int[2];                 /* Analysis filterbank state: 0-2 kHz                                   */
    internal readonly int[] XnrgSubfr = new int[VAD_N_BANDS];       /* Subframe energies                                                    */
    internal readonly int[] NrgRatioSmth_Q8 = new int[VAD_N_BANDS]; /* Smoothed energy level in each band                                   */
    internal short HPstate;                                         /* State of differentiator in the lowest band                           */
    internal readonly int[] NL = new int[VAD_N_BANDS];              /* Noise energy level in each band                                      */
    internal readonly int[] inv_NL = new int[VAD_N_BANDS];          /* Inverse noise energy level in each band                              */
    internal readonly int[] NoiseLevelBias = new int[VAD_N_BANDS];  /* Noise level estimator bias/offset                                    */
    internal int counter;                                           /* Frame counter used in the initial phase                              */

    /// <summary>silk_memset( psSilk_VAD, 0, sizeof( silk_VAD_state ) ).</summary>
    internal void Clear()
    {
        Array.Clear(this.AnaState);
        Array.Clear(this.AnaState1);
        Array.Clear(this.AnaState2);
        Array.Clear(this.XnrgSubfr);
        Array.Clear(this.NrgRatioSmth_Q8);
        this.HPstate = 0;
        Array.Clear(this.NL);
        Array.Clear(this.inv_NL);
        Array.Clear(this.NoiseLevelBias);
        this.counter = 0;
    }
}

/* Variable cut-off low-pass filter state */
internal sealed class silk_LP_state
{
    internal readonly int[] In_LP_State = new int[2];  /* Low pass filter state */
    internal int transition_frame_no;                   /* Counter which is mapped to a cut-off frequency */
    internal int mode;                                  /* Operating mode, <0: switch down, >0: switch up; 0: do nothing           */
    internal int saved_fs_kHz;                          /* If non-zero, holds the last sampling rate before a bandwidth switching reset. */

    internal void Clear()
    {
        Array.Clear(this.In_LP_State);
        this.transition_frame_no = 0;
        this.mode = 0;
        this.saved_fs_kHz = 0;
    }

    /// <summary>C's struct assignment <c>*this = *src</c>.</summary>
    internal void CopyFrom(silk_LP_state src)
    {
        src.In_LP_State.AsSpan().CopyTo(this.In_LP_State);
        this.transition_frame_no = src.transition_frame_no;
        this.mode = src.mode;
        this.saved_fs_kHz = src.saved_fs_kHz;
    }
}

internal sealed class stereo_enc_state
{
    internal readonly short[] pred_prev_Q13 = new short[2];
    internal readonly short[] sMid = new short[2];
    internal readonly short[] sSide = new short[2];
    internal readonly int[] mid_side_amp_Q0 = new int[4];
    internal short smth_width_Q14;
    internal short width_prev_Q14;
    internal short silent_side_len;
    /* C: opus_int8 predIx[ MAX_FRAMES_PER_PACKET ][ 2 ][ 3 ], flattened */
    internal readonly sbyte[] predIx = new sbyte[MAX_FRAMES_PER_PACKET * 2 * 3];
    internal readonly sbyte[] mid_only_flags = new sbyte[MAX_FRAMES_PER_PACKET];

    /// <summary>C's psEnc->sStereo.predIx[ n ] (an opus_int8 [ 2 ][ 3 ], flattened).</summary>
    internal Span<sbyte> predIx_row(int n) => this.predIx.AsSpan(n * 2 * 3, 2 * 3);

    internal void Clear()
    {
        Array.Clear(this.pred_prev_Q13);
        Array.Clear(this.sMid);
        Array.Clear(this.sSide);
        Array.Clear(this.mid_side_amp_Q0);
        this.smth_width_Q14 = 0;
        this.width_prev_Q14 = 0;
        this.silent_side_len = 0;
        Array.Clear(this.predIx);
        Array.Clear(this.mid_only_flags);
    }
}

/// <summary>Not in C: struct copies of the decoder's SideInfoIndices, which the encoder makes.</summary>
internal static class SideInfoIndicesEnc
{
    /// <summary>C's struct assignment <c>*dst = *src</c> (or silk_memcpy of a SideInfoIndices).</summary>
    internal static void CopyTo(this SideInfoIndices src, SideInfoIndices dst)
    {
        src.GainsIndices.AsSpan().CopyTo(dst.GainsIndices);
        src.LTPIndex.AsSpan().CopyTo(dst.LTPIndex);
        src.NLSFIndices.AsSpan().CopyTo(dst.NLSFIndices);
        dst.lagIndex = src.lagIndex;
        dst.contourIndex = src.contourIndex;
        dst.signalType = src.signalType;
        dst.quantOffsetType = src.quantOffsetType;
        dst.NLSFInterpCoef_Q2 = src.NLSFInterpCoef_Q2;
        dst.PERIndex = src.PERIndex;
        dst.LTP_scaleIndex = src.LTP_scaleIndex;
        dst.Seed = src.Seed;
    }
}

/********************************/
/* Encoder state                */
/********************************/
internal sealed class silk_encoder_state
{
    internal readonly int[] In_HP_State = new int[2];                  /* High pass filter state                                           */
    internal int variable_HP_smth1_Q15;                                 /* State of first smoother                                          */
    internal int variable_HP_smth2_Q15;                                 /* State of second smoother                                         */
    internal readonly silk_LP_state sLP = new();                        /* Low pass filter state                                            */
    internal readonly silk_VAD_state sVAD = new();                      /* Voice activity detector state                                    */
    internal readonly silk_nsq_state sNSQ = new();                      /* Noise Shape Quantizer State                                      */
    internal readonly short[] prev_NLSFq_Q15 = new short[MAX_LPC_ORDER]; /* Previously quantized NLSF vector                                 */
    internal int speech_activity_Q8;                                    /* Speech activity                                                  */
    internal int allow_bandwidth_switch;                                /* Flag indicating that switching of internal bandwidth is allowed  */
    internal sbyte LBRRprevLastGainIndex;
    internal sbyte prevSignalType;
    internal int prevLag;
    internal int pitch_LPC_win_length;
    internal int max_pitch_lag;                                         /* Highest possible pitch lag (samples)                             */
    internal int API_fs_Hz;                                             /* API sampling frequency (Hz)                                      */
    internal int prev_API_fs_Hz;                                        /* Previous API sampling frequency (Hz)                             */
    internal int maxInternal_fs_Hz;                                     /* Maximum internal sampling frequency (Hz)                         */
    internal int minInternal_fs_Hz;                                     /* Minimum internal sampling frequency (Hz)                         */
    internal int desiredInternal_fs_Hz;                                 /* Soft request for internal sampling frequency (Hz)                */
    internal int fs_kHz;                                                /* Internal sampling frequency (kHz)                                */
    internal int nb_subfr;                                              /* Number of 5 ms subframes in a frame                              */
    internal int frame_length;                                          /* Frame length (samples)                                           */
    internal int subfr_length;                                          /* Subframe length (samples)                                        */
    internal int ltp_mem_length;                                        /* Length of LTP memory                                             */
    internal int la_pitch;                                              /* Look-ahead for pitch analysis (samples)                          */
    internal int la_shape;                                              /* Look-ahead for noise shape analysis (samples)                    */
    internal int shapeWinLength;                                        /* Window length for noise shape analysis (samples)                 */
    internal int TargetRate_bps;                                        /* Target bitrate (bps)                                             */
    internal int PacketSize_ms;                                         /* Number of milliseconds to put in each packet                     */
    internal int PacketLoss_perc;                                       /* Packet loss rate measured by farend                              */
    internal int frameCounter;
    internal int Complexity;                                            /* Complexity setting                                               */
    internal int nStatesDelayedDecision;                                /* Number of states in delayed decision quantization                */
    internal int useInterpolatedNLSFs;                                  /* Flag for using NLSF interpolation                                */
    internal int shapingLPCOrder;                                       /* Filter order for noise shaping filters                           */
    internal int predictLPCOrder;                                       /* Filter order for prediction filters                              */
    internal int pitchEstimationComplexity;                             /* Complexity level for pitch estimator                             */
    internal int pitchEstimationLPCOrder;                               /* Whitening filter order for pitch estimator                       */
    internal int pitchEstimationThreshold_Q16;                          /* Threshold for pitch estimator                                    */
    internal int sum_log_gain_Q7;                                       /* Cumulative max prediction gain                                   */
    internal int NLSF_MSVQ_Survivors;                                   /* Number of survivors in NLSF MSVQ                                 */
    internal int first_frame_after_reset;                               /* Flag for deactivating NLSF interpolation, pitch prediction       */
    internal int controlled_since_last_payload;                         /* Flag for ensuring codec_control only runs once per packet        */
    internal int warping_Q16;                                           /* Warping parameter for warped noise shaping                       */
    internal int useCBR;                                                /* Flag to enable constant bitrate                                  */
    internal int prefillFlag;                                           /* Flag to indicate that only buffers are prefilled, no coding      */

    /* C: const opus_uint8 *, NULL (empty here) until silk_setup_fs sets them */
    internal byte[] pitch_lag_low_bits_iCDF = [];                       /* Pointer to iCDF table for low bits of pitch lag index            */
    internal byte[] pitch_contour_iCDF = [];                            /* Pointer to iCDF table for pitch contour index                    */

    /* C: const silk_NLSF_CB_struct *, NULL after a reset. It is only read after silk_setup_fs has set it
       (a reset leaves fs_kHz at 0, so the next silk_setup_fs always does), so a reset points it at the
       NB/MB codebook instead of null. */
    internal silk_NLSF_CB_struct psNLSF_CB = TablesNLSFCBNBMB.silk_NLSF_CB_NB_MB;  /* Pointer to NLSF codebook */
    internal readonly int[] input_quality_bands_Q15 = new int[VAD_N_BANDS];
    internal int input_tilt_Q15;
    internal int SNR_dB_Q7;                                             /* Quality setting                                                  */

    internal readonly sbyte[] VAD_flags = new sbyte[MAX_FRAMES_PER_PACKET];
    internal sbyte LBRR_flag;
    internal readonly int[] LBRR_flags = new int[MAX_FRAMES_PER_PACKET];

    internal readonly SideInfoIndices indices = new();
    internal readonly sbyte[] pulses = new sbyte[MAX_FRAME_LENGTH];

    /* Input/output buffering */
    internal readonly short[] inputBuf = new short[MAX_FRAME_LENGTH + 2];  /* Buffer containing input signal                                   */
    internal int inputBufIx;
    internal int nFramesPerPacket;
    internal int nFramesEncoded;                                        /* Number of frames analyzed in current packet                      */

    internal int nChannelsAPI;
    internal int nChannelsInternal;
    internal int channelNb;

    /* Parameters For LTP scaling Control */
    internal int frames_since_onset;

    /* Specifically for entropy coding */
    internal int ec_prevSignalType;
    internal short ec_prevLagIndex;

    internal readonly silk_resampler_state_struct resampler_state = new();

    /* DTX */
    internal int useDTX;                                                /* Flag to enable DTX                                               */
    internal int inDTX;                                                 /* Flag to signal DTX period                                        */
    internal int noSpeechCounter;                                       /* Counts concecutive nonactive frames, used by DTX                 */

    /* Inband Low Bitrate Redundancy (LBRR) data */
    internal int useInBandFEC;                                          /* Saves the API setting for query                                  */
    internal int LBRR_enabled;                                          /* Depends on useInBandFRC, bitrate and packet loss rate            */
    internal int LBRR_GainIncreases;                                    /* Gains increment for coding LBRR frames                           */
    internal readonly SideInfoIndices[] indices_LBRR = [new(), new(), new()];
    /* C: opus_int8 pulses_LBRR[ MAX_FRAMES_PER_PACKET ][ MAX_FRAME_LENGTH ], flattened */
    internal readonly sbyte[] pulses_LBRR = new sbyte[MAX_FRAMES_PER_PACKET * MAX_FRAME_LENGTH];

    /// <summary>C's psEncC->pulses_LBRR[ i ].</summary>
    internal Span<sbyte> pulses_LBRR_row(int i) => this.pulses_LBRR.AsSpan(i * MAX_FRAME_LENGTH, MAX_FRAME_LENGTH);

    /// <summary>silk_memset( psEncC, 0, sizeof( silk_encoder_state ) ).</summary>
    internal void Clear()
    {
        Array.Clear(this.In_HP_State);
        this.variable_HP_smth1_Q15 = 0;
        this.variable_HP_smth2_Q15 = 0;
        this.sLP.Clear();
        this.sVAD.Clear();
        this.sNSQ.Clear();
        Array.Clear(this.prev_NLSFq_Q15);
        this.speech_activity_Q8 = 0;
        this.allow_bandwidth_switch = 0;
        this.LBRRprevLastGainIndex = 0;
        this.prevSignalType = 0;
        this.prevLag = 0;
        this.pitch_LPC_win_length = 0;
        this.max_pitch_lag = 0;
        this.API_fs_Hz = 0;
        this.prev_API_fs_Hz = 0;
        this.maxInternal_fs_Hz = 0;
        this.minInternal_fs_Hz = 0;
        this.desiredInternal_fs_Hz = 0;
        this.fs_kHz = 0;
        this.nb_subfr = 0;
        this.frame_length = 0;
        this.subfr_length = 0;
        this.ltp_mem_length = 0;
        this.la_pitch = 0;
        this.la_shape = 0;
        this.shapeWinLength = 0;
        this.TargetRate_bps = 0;
        this.PacketSize_ms = 0;
        this.PacketLoss_perc = 0;
        this.frameCounter = 0;
        this.Complexity = 0;
        this.nStatesDelayedDecision = 0;
        this.useInterpolatedNLSFs = 0;
        this.shapingLPCOrder = 0;
        this.predictLPCOrder = 0;
        this.pitchEstimationComplexity = 0;
        this.pitchEstimationLPCOrder = 0;
        this.pitchEstimationThreshold_Q16 = 0;
        this.sum_log_gain_Q7 = 0;
        this.NLSF_MSVQ_Survivors = 0;
        this.first_frame_after_reset = 0;
        this.controlled_since_last_payload = 0;
        this.warping_Q16 = 0;
        this.useCBR = 0;
        this.prefillFlag = 0;
        this.pitch_lag_low_bits_iCDF = [];
        this.pitch_contour_iCDF = [];
        this.psNLSF_CB = TablesNLSFCBNBMB.silk_NLSF_CB_NB_MB;
        Array.Clear(this.input_quality_bands_Q15);
        this.input_tilt_Q15 = 0;
        this.SNR_dB_Q7 = 0;
        Array.Clear(this.VAD_flags);
        this.LBRR_flag = 0;
        Array.Clear(this.LBRR_flags);
        this.indices.Clear();
        Array.Clear(this.pulses);
        Array.Clear(this.inputBuf);
        this.inputBufIx = 0;
        this.nFramesPerPacket = 0;
        this.nFramesEncoded = 0;
        this.nChannelsAPI = 0;
        this.nChannelsInternal = 0;
        this.channelNb = 0;
        this.frames_since_onset = 0;
        this.ec_prevSignalType = 0;
        this.ec_prevLagIndex = 0;
        this.resampler_state.Clear();
        this.useDTX = 0;
        this.inDTX = 0;
        this.noSpeechCounter = 0;
        this.useInBandFEC = 0;
        this.LBRR_enabled = 0;
        this.LBRR_GainIncreases = 0;
        for (int i = 0; i < MAX_FRAMES_PER_PACKET; i++)
        {
            this.indices_LBRR[i].Clear();
        }
        Array.Clear(this.pulses_LBRR);
    }
}
