/***********************************************************************
Copyright (c) 2006-2011, Skype Limited. All rights reserved.
Ported to C# from libopus 1.6.1 (silk/enc_API.c) by Hot Chai Games LLC
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

// PORT NOTE: Pointers that C reads or writes at negative offsets (x_ptr[ -k ], x - la_shape,
//   r_ptr - lag, pulses[ i - decisionDelay ], stereo_LR_to_MS's x1[ -2 ]) are passed as the buffer
//   they point into plus the position there (the decoder's celt_fir convention), or as the span
//   that starts at the earliest sample read; each such function says which in a comment.
// PORT NOTE: What C allocates on the stack per frame as structs (silk_encode_frame_FIX's
//   silk_encoder_control_FIX sEncCtrl and sNSQ_copy[ 2 ], silk_LBRR_encode_FIX's sNSQ_LBRR,
//   silk_setup_resamplers's temp_resampler_state, silk_Encode's save_LP) is owned by the encoder
//   state and reused, so encoding allocates nothing. C leaves sEncCtrl uninitialized each frame; here
//   it keeps the previous frame's values. As far as I can tell every field C reads is written first in
//   the same frame, so this can't change output; noted in case a difference shows up there.
// PORT NOTE: silk_quant_LTP_gains passes &gain_Q7 to silk_VQ_WMat_EC, which sets it only when a codebook
//   vector has sum1_Q15 >= 0; C's gain_Q7 is otherwise uninitialized on the first subframe. Here it
//   starts at 0 (and keeps its last value afterwards, as in C).
// PORT NOTE: silk_resampler_init is ported again in ResamplerEnc.cs with its encoder branch
//   (delay_matrix_enc), since the decoder's port of it asserts on forEnc. The resampling itself is the
//   decoder's silk_resampler. silk_resampler_down2, silk_resampler_down2_3 and silk_Resampler_2_3_COEFS_LQ
//   (used only by the pitch analysis) are new files.
// PORT NOTE: The opus_res input is converted with celt/arch.h's RES2INT16 (SAT16(PSHR32(a, 8)) with
//   RES24), the decoder's Arch.RES2INT16, exactly as enc_API.c does: for the stereo-to-mono downmix the
//   two opus_res samples are added (as int) before RES2INT16, as in C.
// PORT NOTE: psNLSF_CB, pitch_lag_low_bits_iCDF and pitch_contour_iCDF are NULL after C's memset; here
//   they're the NB/MB codebook and empty arrays (silk_setup_fs sets them before any use), following the
//   decoder's silk_decoder_state.
// PORT NOTE: silk_LP_state is copied by value in C (save_LP = sLP); here it's a class with CopyFrom, and
//   save_LP is a second instance owned by silk_encoder.
// PORT NOTE: SILK_FIX_CONST is a method (SigProcFixEnc): for a float argument C multiplies in float, but by
//   a power of two, so the result is the same as the double computation on the float's value.
// PORT NOTE: The arch parameters are dropped (generic C versions throughout). silk_Get_Encoder_Size isn't
//   ported (the C# state is a class).
// PORT NOTE: silk_VAD_GetSA_Q8 initializes sumSquared before the subframe loop only to satisfy C#'s
//   definite assignment; the loop always runs, so the value is never used.

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using DotGame.AudioCodecs.Opus.Celt;
using DotGame.AudioCodecs.Opus.Common;
using static DotGame.AudioCodecs.Opus.Silk.CheckControlInput;
using static DotGame.AudioCodecs.Opus.Silk.ControlCodec;
using static DotGame.AudioCodecs.Opus.Silk.ControlSNR;
using static DotGame.AudioCodecs.Opus.Silk.EncodeFrameFIX;
using static DotGame.AudioCodecs.Opus.Silk.EncodeIndices;
using static DotGame.AudioCodecs.Opus.Silk.EncodePulses;
using static DotGame.AudioCodecs.Opus.Silk.ErrorsEnc;
using static DotGame.AudioCodecs.Opus.Silk.HPVariableCutoff;
using static DotGame.AudioCodecs.Opus.Silk.InitEncoder;
using static DotGame.AudioCodecs.Opus.Silk.SigProcFixEnc;
using static DotGame.AudioCodecs.Opus.Silk.StereoEncodePred;
using static DotGame.AudioCodecs.Opus.Silk.StereoLRToMS;
using static DotGame.AudioCodecs.Opus.Silk.TuningParameters;
using static DotGame.AudioCodecs.Opus.Silk.Define;
using static DotGame.AudioCodecs.Opus.Silk.Errors;
using static DotGame.AudioCodecs.Opus.Silk.SigProcFix;
using static DotGame.AudioCodecs.Opus.Silk.TablesOther;

namespace DotGame.AudioCodecs.Opus.Silk;

[SkipLocalsInit]
internal static class EncAPI
{
    /****************************************/
    /* Encoder functions                    */
    /****************************************/

    /*************************/
    /* Init or Reset encoder */
    /*************************/
    internal static int silk_InitEncoder(  /* O    Returns error code                              */
        silk_encoder encState,              /* I/O  State                                           */
        int channels,                       /* I    Number of channels                              */
        silk_EncControlStruct encStatus)    /* O    Encoder Status                                  */
    {
        silk_encoder psEnc;
        int n, ret = SILK_NO_ERROR;

        psEnc = encState;

        /* Reset encoder. Skip second encoder state for mono. */
        psEnc.Clear(channels);
        for (n = 0; n < channels; n++)
        {
            if ((ret += silk_init_encoder(psEnc.state_Fxx[n])) != 0)
            {
                Debug.Assert(false);
            }
        }

        psEnc.nChannelsAPI = 1;
        psEnc.nChannelsInternal = 1;

        /* Read control structure */
        if ((ret += silk_QueryEncoder(encState, encStatus)) != 0)
        {
            Debug.Assert(false);
        }

        return ret;
    }

    /***************************************/
    /* Read control structure from encoder */
    /***************************************/
    private static int silk_QueryEncoder(  /* O    Returns error code                              */
        silk_encoder encState,              /* I    State                                           */
        silk_EncControlStruct encStatus)    /* O    Encoder Status                                  */
    {
        int ret = SILK_NO_ERROR;
        silk_encoder_state_FIX[] state_Fxx;
        silk_encoder psEnc = encState;

        state_Fxx = psEnc.state_Fxx;

        encStatus.nChannelsAPI = psEnc.nChannelsAPI;
        encStatus.nChannelsInternal = psEnc.nChannelsInternal;
        encStatus.API_sampleRate = state_Fxx[0].sCmn.API_fs_Hz;
        encStatus.maxInternalSampleRate = state_Fxx[0].sCmn.maxInternal_fs_Hz;
        encStatus.minInternalSampleRate = state_Fxx[0].sCmn.minInternal_fs_Hz;
        encStatus.desiredInternalSampleRate = state_Fxx[0].sCmn.desiredInternal_fs_Hz;
        encStatus.payloadSize_ms = state_Fxx[0].sCmn.PacketSize_ms;
        encStatus.bitRate = state_Fxx[0].sCmn.TargetRate_bps;
        encStatus.packetLossPercentage = state_Fxx[0].sCmn.PacketLoss_perc;
        encStatus.complexity = state_Fxx[0].sCmn.Complexity;
        encStatus.useInBandFEC = state_Fxx[0].sCmn.useInBandFEC;
        encStatus.useDTX = state_Fxx[0].sCmn.useDTX;
        encStatus.useCBR = state_Fxx[0].sCmn.useCBR;
        encStatus.internalSampleRate = silk_SMULBB(state_Fxx[0].sCmn.fs_kHz, 1000);
        encStatus.allowBandwidthSwitch = state_Fxx[0].sCmn.allow_bandwidth_switch;
        encStatus.inWBmodeWithoutVariableLP = state_Fxx[0].sCmn.fs_kHz == 16 && state_Fxx[0].sCmn.sLP.mode == 0 ? 1 : 0;

        return ret;
    }

    /**************************/
    /* Encode frame with Silk */
    /**************************/
    /* Note: if prefillFlag is set, the input must contain 10 ms of audio, irrespective of what                     */
    /* encControl->payloadSize_ms is set to                                                                         */
    /* samplesIn is opus_res (int with RES24). haveEnc false is C's psRangeEnc == NULL (prefill). */
    internal static int silk_Encode(        /* O    Returns error code                              */
        silk_encoder encState,              /* I/O  State                                           */
        silk_EncControlStruct encControl,   /* I    Control status                                  */
        ReadOnlySpan<int> samplesIn,        /* I    Speech sample input vector                      */
        int nSamplesIn,                     /* I    Number of samples in input vector               */
        ref ec_enc psRangeEnc,              /* I/O  Compressor data structure                       */
        bool haveEnc,                       /* I    C's psRangeEnc != NULL                          */
        ref int nBytesOut,                  /* I/O  Number of bytes in payload (input: Max bytes)   */
        int prefillFlag,                    /* I    Flag to indicate prefilling buffers no coding   */
        int activity)                       /* I    Decision of Opus voice activity detector        */
    {
        int n, i, nBits, flags, tmp_payloadSize_ms = 0, tmp_complexity = 0, ret = 0;
        int nSamplesToBuffer, nSamplesToBufferMax, nBlocksOf10ms;
        int nSamplesFromInput = 0, nSamplesFromInputMax;
        int speech_act_thr_for_switch_Q8;
        int TargetRate_bps, channelRate_bps, LBRR_symbol, sum;
        Span<int> MStargetRates_bps = stackalloc int[2];
        silk_encoder psEnc = encState;
        scoped Span<short> buf;
        int transition, curr_block, tot_blocks;

        Debug.Assert(encControl.nChannelsAPI >= encControl.nChannelsInternal && encControl.nChannelsAPI >= psEnc.nChannelsInternal);
        Debug.Assert(haveEnc || prefillFlag != 0);
        MStargetRates_bps.Clear();
        if (encControl.reducedDependency != 0)
        {
            for (n = 0; n < encControl.nChannelsAPI; n++)
            {
                psEnc.state_Fxx[n].sCmn.first_frame_after_reset = 1;
            }
        }
        for (n = 0; n < encControl.nChannelsAPI; n++)
        {
            psEnc.state_Fxx[n].sCmn.nFramesEncoded = 0;
        }
        /* Check values in encoder control structure */
        if ((ret = check_control_input(encControl)) != 0)
        {
            Debug.Assert(false);
            return ret;
        }

        encControl.switchReady = 0;

        if (encControl.nChannelsInternal > psEnc.nChannelsInternal)
        {
            /* Mono -> Stereo transition: init state of second channel and stereo state */
            ret += silk_init_encoder(psEnc.state_Fxx[1]);
            Array.Clear(psEnc.sStereo.pred_prev_Q13);
            Array.Clear(psEnc.sStereo.sSide);
            psEnc.sStereo.mid_side_amp_Q0[0] = 0;
            psEnc.sStereo.mid_side_amp_Q0[1] = 1;
            psEnc.sStereo.mid_side_amp_Q0[2] = 0;
            psEnc.sStereo.mid_side_amp_Q0[3] = 1;
            psEnc.sStereo.width_prev_Q14 = 0;
            psEnc.sStereo.smth_width_Q14 = (short)SILK_FIX_CONST(1, 14);
            if (psEnc.nChannelsAPI == 2)
            {
                psEnc.state_Fxx[1].sCmn.resampler_state.CopyFrom(psEnc.state_Fxx[0].sCmn.resampler_state);
                psEnc.state_Fxx[0].sCmn.In_HP_State.AsSpan().CopyTo(psEnc.state_Fxx[1].sCmn.In_HP_State);
            }
        }

        transition = (encControl.payloadSize_ms != psEnc.state_Fxx[0].sCmn.PacketSize_ms) || (psEnc.nChannelsInternal != encControl.nChannelsInternal) ? 1 : 0;

        psEnc.nChannelsAPI = encControl.nChannelsAPI;
        psEnc.nChannelsInternal = encControl.nChannelsInternal;

        nBlocksOf10ms = silk_DIV32(100 * nSamplesIn, encControl.API_sampleRate);
        tot_blocks = (nBlocksOf10ms > 1) ? nBlocksOf10ms >> 1 : 1;
        curr_block = 0;
        if (prefillFlag != 0)
        {
            silk_LP_state save_LP = psEnc.save_LP;
            /* Only accept input length of 10 ms */
            if (nBlocksOf10ms != 1)
            {
                Debug.Assert(false);
                return SILK_ENC_INPUT_INVALID_NO_OF_SAMPLES;
            }
            if (prefillFlag == 2)
            {
                save_LP.CopyFrom(psEnc.state_Fxx[0].sCmn.sLP);
                /* Save the sampling rate so the bandwidth switching code can keep handling transitions. */
                save_LP.saved_fs_kHz = psEnc.state_Fxx[0].sCmn.fs_kHz;
            }
            /* Reset Encoder */
            for (n = 0; n < encControl.nChannelsInternal; n++)
            {
                ret = silk_init_encoder(psEnc.state_Fxx[n]);
                /* Restore the variable LP state. */
                if (prefillFlag == 2)
                {
                    psEnc.state_Fxx[n].sCmn.sLP.CopyFrom(save_LP);
                }
                Debug.Assert(ret == 0);
            }
            tmp_payloadSize_ms = encControl.payloadSize_ms;
            encControl.payloadSize_ms = 10;
            tmp_complexity = encControl.complexity;
            encControl.complexity = 0;
            for (n = 0; n < encControl.nChannelsInternal; n++)
            {
                psEnc.state_Fxx[n].sCmn.controlled_since_last_payload = 0;
                psEnc.state_Fxx[n].sCmn.prefillFlag = 1;
            }
        }
        else
        {
            /* Only accept input lengths that are a multiple of 10 ms */
            if (nBlocksOf10ms * encControl.API_sampleRate != 100 * nSamplesIn || nSamplesIn < 0)
            {
                Debug.Assert(false);
                return SILK_ENC_INPUT_INVALID_NO_OF_SAMPLES;
            }
            /* Make sure no more than one packet can be produced */
            if (1000 * nSamplesIn > encControl.payloadSize_ms * encControl.API_sampleRate)
            {
                Debug.Assert(false);
                return SILK_ENC_INPUT_INVALID_NO_OF_SAMPLES;
            }
        }

        for (n = 0; n < encControl.nChannelsInternal; n++)
        {
            /* Force the side channel to the same rate as the mid */
            int force_fs_kHz = (n == 1) ? psEnc.state_Fxx[0].sCmn.fs_kHz : 0;
            if ((ret = silk_control_encoder(psEnc.state_Fxx[n], encControl, psEnc.allowBandwidthSwitch, n, force_fs_kHz)) != 0)
            {
                Debug.Assert(false);
                return ret;
            }
            if (psEnc.state_Fxx[n].sCmn.first_frame_after_reset != 0 || transition != 0)
            {
                for (i = 0; i < psEnc.state_Fxx[0].sCmn.nFramesPerPacket; i++)
                {
                    psEnc.state_Fxx[n].sCmn.LBRR_flags[i] = 0;
                }
            }
            psEnc.state_Fxx[n].sCmn.inDTX = psEnc.state_Fxx[n].sCmn.useDTX;
        }
        Debug.Assert(encControl.nChannelsInternal == 1 || psEnc.state_Fxx[0].sCmn.fs_kHz == psEnc.state_Fxx[1].sCmn.fs_kHz);

        /* Input buffering/resampling and encoding */
        nSamplesToBufferMax =
            10 * nBlocksOf10ms * psEnc.state_Fxx[0].sCmn.fs_kHz;
        nSamplesFromInputMax =
            silk_DIV32_16(nSamplesToBufferMax *
                               psEnc.state_Fxx[0].sCmn.API_fs_Hz,
                           psEnc.state_Fxx[0].sCmn.fs_kHz * 1000);
        buf = stackalloc short[nSamplesFromInputMax];
        /* C: opus_uint8 iCDF[ 2 ], declared in the loop below; hoisted (CA2014) and set where C declares it */
        Span<byte> iCDF = stackalloc byte[2];
        while (true)
        {
            int curr_nBitsUsedLBRR = 0;
            nSamplesToBuffer = psEnc.state_Fxx[0].sCmn.frame_length - psEnc.state_Fxx[0].sCmn.inputBufIx;
            nSamplesToBuffer = silk_min(nSamplesToBuffer, nSamplesToBufferMax);
            nSamplesFromInput = silk_DIV32_16(nSamplesToBuffer * psEnc.state_Fxx[0].sCmn.API_fs_Hz, psEnc.state_Fxx[0].sCmn.fs_kHz * 1000);
            /* Resample and write to buffer */
            if (encControl.nChannelsAPI == 2 && encControl.nChannelsInternal == 2)
            {
                int id = psEnc.state_Fxx[0].sCmn.nFramesEncoded;
                for (n = 0; n < nSamplesFromInput; n++)
                {
                    buf[n] = Arch.RES2INT16(samplesIn[2 * n]);
                }
                /* Making sure to start both resamplers from the same state when switching from mono to stereo */
                if (psEnc.nPrevChannelsInternal == 1 && id == 0)
                {
                    psEnc.state_Fxx[1].sCmn.resampler_state.CopyFrom(psEnc.state_Fxx[0].sCmn.resampler_state);
                }

                ret += Resampler.silk_resampler(psEnc.state_Fxx[0].sCmn.resampler_state,
                    psEnc.state_Fxx[0].sCmn.inputBuf.AsSpan(psEnc.state_Fxx[0].sCmn.inputBufIx + 2), buf, nSamplesFromInput);
                psEnc.state_Fxx[0].sCmn.inputBufIx += nSamplesToBuffer;

                nSamplesToBuffer = psEnc.state_Fxx[1].sCmn.frame_length - psEnc.state_Fxx[1].sCmn.inputBufIx;
                nSamplesToBuffer = silk_min(nSamplesToBuffer, 10 * nBlocksOf10ms * psEnc.state_Fxx[1].sCmn.fs_kHz);
                for (n = 0; n < nSamplesFromInput; n++)
                {
                    buf[n] = Arch.RES2INT16(samplesIn[(2 * n) + 1]);
                }
                ret += Resampler.silk_resampler(psEnc.state_Fxx[1].sCmn.resampler_state,
                    psEnc.state_Fxx[1].sCmn.inputBuf.AsSpan(psEnc.state_Fxx[1].sCmn.inputBufIx + 2), buf, nSamplesFromInput);

                psEnc.state_Fxx[1].sCmn.inputBufIx += nSamplesToBuffer;
            }
            else if (encControl.nChannelsAPI == 2 && encControl.nChannelsInternal == 1)
            {
                /* Combine left and right channels before resampling */
                for (n = 0; n < nSamplesFromInput; n++)
                {
                    sum = Arch.RES2INT16(samplesIn[2 * n] + samplesIn[(2 * n) + 1]);
                    buf[n] = (short)silk_RSHIFT_ROUND(sum, 1);
                }
                ret += Resampler.silk_resampler(psEnc.state_Fxx[0].sCmn.resampler_state,
                    psEnc.state_Fxx[0].sCmn.inputBuf.AsSpan(psEnc.state_Fxx[0].sCmn.inputBufIx + 2), buf, nSamplesFromInput);
                /* On the first mono frame, average the results for the two resampler states  */
                if (psEnc.nPrevChannelsInternal == 2 && psEnc.state_Fxx[0].sCmn.nFramesEncoded == 0)
                {
                    ret += Resampler.silk_resampler(psEnc.state_Fxx[1].sCmn.resampler_state,
                        psEnc.state_Fxx[1].sCmn.inputBuf.AsSpan(psEnc.state_Fxx[1].sCmn.inputBufIx + 2), buf, nSamplesFromInput);
                    for (n = 0; n < psEnc.state_Fxx[0].sCmn.frame_length; n++)
                    {
                        psEnc.state_Fxx[0].sCmn.inputBuf[psEnc.state_Fxx[0].sCmn.inputBufIx + n + 2] =
                              (short)silk_RSHIFT(psEnc.state_Fxx[0].sCmn.inputBuf[psEnc.state_Fxx[0].sCmn.inputBufIx + n + 2]
                                        + psEnc.state_Fxx[1].sCmn.inputBuf[psEnc.state_Fxx[1].sCmn.inputBufIx + n + 2], 1);
                    }
                }
                psEnc.state_Fxx[0].sCmn.inputBufIx += nSamplesToBuffer;
            }
            else
            {
                Debug.Assert(encControl.nChannelsAPI == 1 && encControl.nChannelsInternal == 1);
                for (n = 0; n < nSamplesFromInput; n++)
                {
                    buf[n] = Arch.RES2INT16(samplesIn[n]);
                }
                ret += Resampler.silk_resampler(psEnc.state_Fxx[0].sCmn.resampler_state,
                    psEnc.state_Fxx[0].sCmn.inputBuf.AsSpan(psEnc.state_Fxx[0].sCmn.inputBufIx + 2), buf, nSamplesFromInput);
                psEnc.state_Fxx[0].sCmn.inputBufIx += nSamplesToBuffer;
            }

            samplesIn = samplesIn[(nSamplesFromInput * encControl.nChannelsAPI)..];
            nSamplesIn -= nSamplesFromInput;

            /* Default */
            psEnc.allowBandwidthSwitch = 0;

            /* Silk encoder */
            if (psEnc.state_Fxx[0].sCmn.inputBufIx >= psEnc.state_Fxx[0].sCmn.frame_length)
            {
                /* Enough data in input buffer, so encode */
                Debug.Assert(psEnc.state_Fxx[0].sCmn.inputBufIx == psEnc.state_Fxx[0].sCmn.frame_length);
                Debug.Assert(encControl.nChannelsInternal == 1 || psEnc.state_Fxx[1].sCmn.inputBufIx == psEnc.state_Fxx[1].sCmn.frame_length);

                /* Deal with LBRR data */
                if (psEnc.state_Fxx[0].sCmn.nFramesEncoded == 0 && prefillFlag == 0)
                {
                    /* Create space at start of payload for VAD and FEC flags */
                    iCDF[0] = 0;
                    iCDF[1] = 0;
                    iCDF[0] = (byte)(256 - silk_RSHIFT(256, (psEnc.state_Fxx[0].sCmn.nFramesPerPacket + 1) * encControl.nChannelsInternal));
                    psRangeEnc.ec_enc_icdf(0, iCDF, 8);
                    curr_nBitsUsedLBRR = psRangeEnc.ec_tell();

                    /* Encode any LBRR data from previous packet */
                    /* Encode LBRR flags */
                    for (n = 0; n < encControl.nChannelsInternal; n++)
                    {
                        LBRR_symbol = 0;
                        for (i = 0; i < psEnc.state_Fxx[n].sCmn.nFramesPerPacket; i++)
                        {
                            LBRR_symbol |= silk_LSHIFT(psEnc.state_Fxx[n].sCmn.LBRR_flags[i], i);
                        }
                        psEnc.state_Fxx[n].sCmn.LBRR_flag = (sbyte)(LBRR_symbol > 0 ? 1 : 0);
                        if (LBRR_symbol != 0 && psEnc.state_Fxx[n].sCmn.nFramesPerPacket > 1)
                        {
                            psRangeEnc.ec_enc_icdf(LBRR_symbol - 1, silk_LBRR_flags_iCDF_ptr[psEnc.state_Fxx[n].sCmn.nFramesPerPacket - 2], 8);
                        }
                    }

                    /* Code LBRR indices and excitation signals */
                    for (i = 0; i < psEnc.state_Fxx[0].sCmn.nFramesPerPacket; i++)
                    {
                        for (n = 0; n < encControl.nChannelsInternal; n++)
                        {
                            if (psEnc.state_Fxx[n].sCmn.LBRR_flags[i] != 0)
                            {
                                int condCoding;

                                if (encControl.nChannelsInternal == 2 && n == 0)
                                {
                                    silk_stereo_encode_pred(ref psRangeEnc, psEnc.sStereo.predIx_row(i));
                                    /* For LBRR data there's no need to code the mid-only flag if the side-channel LBRR flag is set */
                                    if (psEnc.state_Fxx[1].sCmn.LBRR_flags[i] == 0)
                                    {
                                        silk_stereo_encode_mid_only(ref psRangeEnc, psEnc.sStereo.mid_only_flags[i]);
                                    }
                                }
                                /* Use conditional coding if previous frame available */
                                if (i > 0 && psEnc.state_Fxx[n].sCmn.LBRR_flags[i - 1] != 0)
                                {
                                    condCoding = CODE_CONDITIONALLY;
                                }
                                else
                                {
                                    condCoding = CODE_INDEPENDENTLY;
                                }
                                silk_encode_indices(psEnc.state_Fxx[n].sCmn, ref psRangeEnc, i, 1, condCoding);
                                silk_encode_pulses(ref psRangeEnc, psEnc.state_Fxx[n].sCmn.indices_LBRR[i].signalType, psEnc.state_Fxx[n].sCmn.indices_LBRR[i].quantOffsetType,
                                    psEnc.state_Fxx[n].sCmn.pulses_LBRR_row(i), psEnc.state_Fxx[n].sCmn.frame_length);
                            }
                        }
                    }

                    /* Reset LBRR flags */
                    for (n = 0; n < encControl.nChannelsInternal; n++)
                    {
                        Array.Clear(psEnc.state_Fxx[n].sCmn.LBRR_flags);
                    }
                    curr_nBitsUsedLBRR = psRangeEnc.ec_tell() - curr_nBitsUsedLBRR;
                }

                silk_HP_variable_cutoff(psEnc.state_Fxx);

                /* Total target bits for packet */
                nBits = silk_DIV32_16(silk_MUL(encControl.bitRate, encControl.payloadSize_ms), 1000);
                /* Subtract bits used for LBRR */
                if (prefillFlag == 0)
                {
                    /* psEnc->nBitsUsedLBRR is an exponential moving average of the LBRR usage,
                       except that for the first LBRR frame it does no averaging and for the first
                       frame after after LBRR, it goes back to zero immediately. */
                    if (curr_nBitsUsedLBRR < 10)
                    {
                        psEnc.nBitsUsedLBRR = 0;
                    }
                    else if (psEnc.nBitsUsedLBRR < 10)
                    {
                        psEnc.nBitsUsedLBRR = curr_nBitsUsedLBRR;
                    }
                    else
                    {
                        psEnc.nBitsUsedLBRR = (psEnc.nBitsUsedLBRR + curr_nBitsUsedLBRR) / 2;
                    }
                    nBits -= psEnc.nBitsUsedLBRR;
                }
                /* Divide by number of uncoded frames left in packet */
                nBits = silk_DIV32_16(nBits, psEnc.state_Fxx[0].sCmn.nFramesPerPacket);
                /* Convert to bits/second */
                if (encControl.payloadSize_ms == 10)
                {
                    TargetRate_bps = silk_SMULBB(nBits, 100);
                }
                else
                {
                    TargetRate_bps = silk_SMULBB(nBits, 50);
                }
                /* Subtract fraction of bits in excess of target in previous frames and packets */
                TargetRate_bps -= silk_DIV32_16(silk_MUL(psEnc.nBitsExceeded, 1000), BITRESERVOIR_DECAY_TIME_MS);
                if (prefillFlag == 0 && psEnc.state_Fxx[0].sCmn.nFramesEncoded > 0)
                {
                    /* Compare actual vs target bits so far in this packet */
                    int bitsBalance = psRangeEnc.ec_tell() - psEnc.nBitsUsedLBRR - (nBits * psEnc.state_Fxx[0].sCmn.nFramesEncoded);
                    TargetRate_bps -= silk_DIV32_16(silk_MUL(bitsBalance, 1000), BITRESERVOIR_DECAY_TIME_MS);
                }
                /* Never exceed input bitrate */
                TargetRate_bps = silk_LIMIT(TargetRate_bps, encControl.bitRate, 5000);

                /* Convert Left/Right to Mid/Side */
                if (encControl.nChannelsInternal == 2)
                {
                    /* C passes &inputBuf[ 2 ] of both channels; the port takes the spans from C's x1[ -2 ] */
                    silk_stereo_LR_to_MS(psEnc.sStereo, psEnc.state_Fxx[0].sCmn.inputBuf, psEnc.state_Fxx[1].sCmn.inputBuf,
                        psEnc.sStereo.predIx_row(psEnc.state_Fxx[0].sCmn.nFramesEncoded), ref psEnc.sStereo.mid_only_flags[psEnc.state_Fxx[0].sCmn.nFramesEncoded],
                        MStargetRates_bps, TargetRate_bps, psEnc.state_Fxx[0].sCmn.speech_activity_Q8, encControl.toMono,
                        psEnc.state_Fxx[0].sCmn.fs_kHz, psEnc.state_Fxx[0].sCmn.frame_length);
                    if (psEnc.sStereo.mid_only_flags[psEnc.state_Fxx[0].sCmn.nFramesEncoded] == 0)
                    {
                        /* Reset side channel encoder memory for first frame with side coding */
                        if (psEnc.prev_decode_only_middle == 1)
                        {
                            psEnc.state_Fxx[1].sShape.Clear();
                            psEnc.state_Fxx[1].sCmn.sNSQ.Clear();
                            Array.Clear(psEnc.state_Fxx[1].sCmn.prev_NLSFq_Q15);
                            Array.Clear(psEnc.state_Fxx[1].sCmn.sLP.In_LP_State);
                            psEnc.state_Fxx[1].sCmn.prevLag = 100;
                            psEnc.state_Fxx[1].sCmn.sNSQ.lagPrev = 100;
                            psEnc.state_Fxx[1].sShape.LastGainIndex = 10;
                            psEnc.state_Fxx[1].sCmn.prevSignalType = TYPE_NO_VOICE_ACTIVITY;
                            psEnc.state_Fxx[1].sCmn.sNSQ.prev_gain_Q16 = 65536;
                            psEnc.state_Fxx[1].sCmn.first_frame_after_reset = 1;
                        }
                        silk_encode_do_VAD_FIX(psEnc.state_Fxx[1], activity);
                    }
                    else
                    {
                        psEnc.state_Fxx[1].sCmn.VAD_flags[psEnc.state_Fxx[0].sCmn.nFramesEncoded] = 0;
                    }
                    if (prefillFlag == 0)
                    {
                        silk_stereo_encode_pred(ref psRangeEnc, psEnc.sStereo.predIx_row(psEnc.state_Fxx[0].sCmn.nFramesEncoded));
                        if (psEnc.state_Fxx[1].sCmn.VAD_flags[psEnc.state_Fxx[0].sCmn.nFramesEncoded] == 0)
                        {
                            silk_stereo_encode_mid_only(ref psRangeEnc, psEnc.sStereo.mid_only_flags[psEnc.state_Fxx[0].sCmn.nFramesEncoded]);
                        }
                    }
                }
                else
                {
                    /* Buffering */
                    psEnc.sStereo.sMid.AsSpan(0, 2).CopyTo(psEnc.state_Fxx[0].sCmn.inputBuf);
                    psEnc.state_Fxx[0].sCmn.inputBuf.AsSpan(psEnc.state_Fxx[0].sCmn.frame_length, 2).CopyTo(psEnc.sStereo.sMid);
                }
                silk_encode_do_VAD_FIX(psEnc.state_Fxx[0], activity);

                /* Encode */
                for (n = 0; n < encControl.nChannelsInternal; n++)
                {
                    int maxBits, useCBR;

                    /* Handling rate constraints */
                    maxBits = encControl.maxBits;
                    if (tot_blocks == 2 && curr_block == 0)
                    {
                        maxBits = maxBits * 3 / 5;
                    }
                    else if (tot_blocks == 3)
                    {
                        if (curr_block == 0)
                        {
                            maxBits = maxBits * 2 / 5;
                        }
                        else if (curr_block == 1)
                        {
                            maxBits = maxBits * 3 / 4;
                        }
                    }
                    useCBR = encControl.useCBR != 0 && curr_block == tot_blocks - 1 ? 1 : 0;

                    if (encControl.nChannelsInternal == 1)
                    {
                        channelRate_bps = TargetRate_bps;
                    }
                    else
                    {
                        channelRate_bps = MStargetRates_bps[n];
                        if (n == 0 && MStargetRates_bps[1] > 0)
                        {
                            useCBR = 0;
                            /* Give mid up to 1/2 of the max bits for that frame */
                            maxBits -= encControl.maxBits / (tot_blocks * 2);
                        }
                    }

                    if (channelRate_bps > 0)
                    {
                        int condCoding;

                        silk_control_SNR(psEnc.state_Fxx[n].sCmn, channelRate_bps);

                        /* Use independent coding if no previous frame available */
                        if (psEnc.state_Fxx[0].sCmn.nFramesEncoded - n <= 0)
                        {
                            condCoding = CODE_INDEPENDENTLY;
                        }
                        else if (n > 0 && psEnc.prev_decode_only_middle != 0)
                        {
                            /* If we skipped a side frame in this packet, we don't
                               need LTP scaling; the LTP state is well-defined. */
                            condCoding = CODE_INDEPENDENTLY_NO_LTP_SCALING;
                        }
                        else
                        {
                            condCoding = CODE_CONDITIONALLY;
                        }
                        if ((ret = silk_encode_frame_FIX(psEnc.state_Fxx[n], ref nBytesOut, ref psRangeEnc, haveEnc, condCoding, maxBits, useCBR)) != 0)
                        {
                            Debug.Assert(false);
                        }
                    }
                    psEnc.state_Fxx[n].sCmn.controlled_since_last_payload = 0;
                    psEnc.state_Fxx[n].sCmn.inputBufIx = 0;
                    psEnc.state_Fxx[n].sCmn.nFramesEncoded++;
                }
                psEnc.prev_decode_only_middle = psEnc.sStereo.mid_only_flags[psEnc.state_Fxx[0].sCmn.nFramesEncoded - 1];

                /* Insert VAD and FEC flags at beginning of bitstream */
                if (nBytesOut > 0 && psEnc.state_Fxx[0].sCmn.nFramesEncoded == psEnc.state_Fxx[0].sCmn.nFramesPerPacket)
                {
                    flags = 0;
                    for (n = 0; n < encControl.nChannelsInternal; n++)
                    {
                        for (i = 0; i < psEnc.state_Fxx[n].sCmn.nFramesPerPacket; i++)
                        {
                            flags = silk_LSHIFT(flags, 1);
                            int vad_flag = psEnc.state_Fxx[n].sCmn.VAD_flags[i];  /* opus_int8 promoted to int, as in C */
                            flags |= vad_flag;
                        }
                        flags = silk_LSHIFT(flags, 1);
                        int lbrr_flag = psEnc.state_Fxx[n].sCmn.LBRR_flag;  /* opus_int8 promoted to int, as in C */
                        flags |= lbrr_flag;
                    }
                    if (prefillFlag == 0)
                    {
                        psRangeEnc.ec_enc_patch_initial_bits((uint)flags, (uint)((psEnc.state_Fxx[0].sCmn.nFramesPerPacket + 1) * encControl.nChannelsInternal));
                    }

                    /* Return zero bytes if all channels DTXed */
                    if (psEnc.state_Fxx[0].sCmn.inDTX != 0 && (encControl.nChannelsInternal == 1 || psEnc.state_Fxx[1].sCmn.inDTX != 0))
                    {
                        nBytesOut = 0;
                    }

                    psEnc.nBitsExceeded += nBytesOut * 8;
                    psEnc.nBitsExceeded -= silk_DIV32_16(silk_MUL(encControl.bitRate, encControl.payloadSize_ms), 1000);
                    psEnc.nBitsExceeded = silk_LIMIT(psEnc.nBitsExceeded, 0, 10000);

                    /* Update flag indicating if bandwidth switching is allowed */
                    speech_act_thr_for_switch_Q8 = silk_SMLAWB(SILK_FIX_CONST(SPEECH_ACTIVITY_DTX_THRES, 8),
                        SILK_FIX_CONST((1 - SPEECH_ACTIVITY_DTX_THRES) / MAX_BANDWIDTH_SWITCH_DELAY_MS, 16 + 8), psEnc.timeSinceSwitchAllowed_ms);
                    if (psEnc.state_Fxx[0].sCmn.speech_activity_Q8 < speech_act_thr_for_switch_Q8)
                    {
                        psEnc.allowBandwidthSwitch = 1;
                        psEnc.timeSinceSwitchAllowed_ms = 0;
                    }
                    else
                    {
                        psEnc.allowBandwidthSwitch = 0;
                        psEnc.timeSinceSwitchAllowed_ms += encControl.payloadSize_ms;
                    }
                }

                if (nSamplesIn == 0)
                {
                    break;
                }
            }
            else
            {
                break;
            }
            curr_block++;
        }

        psEnc.nPrevChannelsInternal = encControl.nChannelsInternal;

        encControl.allowBandwidthSwitch = psEnc.allowBandwidthSwitch;
        encControl.inWBmodeWithoutVariableLP = psEnc.state_Fxx[0].sCmn.fs_kHz == 16 && psEnc.state_Fxx[0].sCmn.sLP.mode == 0 ? 1 : 0;
        encControl.internalSampleRate = silk_SMULBB(psEnc.state_Fxx[0].sCmn.fs_kHz, 1000);
        encControl.stereoWidth_Q14 = encControl.toMono != 0 ? 0 : psEnc.sStereo.smth_width_Q14;
        if (prefillFlag != 0)
        {
            encControl.payloadSize_ms = tmp_payloadSize_ms;
            encControl.complexity = tmp_complexity;
            for (n = 0; n < encControl.nChannelsInternal; n++)
            {
                psEnc.state_Fxx[n].sCmn.controlled_since_last_payload = 0;
                psEnc.state_Fxx[n].sCmn.prefillFlag = 0;
            }
        }

        encControl.signalType = psEnc.state_Fxx[0].sCmn.indices.signalType;
        encControl.offset = silk_Quantization_Offsets_Q10
                             [((psEnc.state_Fxx[0].sCmn.indices.signalType >> 1) * 2)
                             + psEnc.state_Fxx[0].sCmn.indices.quantOffsetType];
        return ret;
    }
}
