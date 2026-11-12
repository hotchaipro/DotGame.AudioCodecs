/***********************************************************************
Copyright (c) 2006-2011, Skype Limited. All rights reserved.
Ported to C# from libopus 1.6.1 (silk/dec_API.c) by Hot Chai Games LLC
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
using static DotGame.AudioCodecs.Opus.Silk.Control;
using static DotGame.AudioCodecs.Opus.Silk.DecodeFrame;
using static DotGame.AudioCodecs.Opus.Silk.DecodeIndices;
using static DotGame.AudioCodecs.Opus.Silk.DecodePulses;
using static DotGame.AudioCodecs.Opus.Silk.DecoderSetFs;
using static DotGame.AudioCodecs.Opus.Silk.Define;
using static DotGame.AudioCodecs.Opus.Silk.Errors;
using static DotGame.AudioCodecs.Opus.Silk.InitDecoder;
using static DotGame.AudioCodecs.Opus.Silk.Resampler;
using static DotGame.AudioCodecs.Opus.Silk.SigProcFix;
using static DotGame.AudioCodecs.Opus.Silk.StereoDecodePred;
using static DotGame.AudioCodecs.Opus.Silk.StereoMSToLR;
using static DotGame.AudioCodecs.Opus.Silk.TablesOther;

namespace DotGame.AudioCodecs.Opus.Silk;

/************************/
/* Decoder Super Struct */
/************************/
[SkipLocalsInit]
internal sealed class silk_decoder
{
    internal readonly silk_decoder_state[] channel_state = [new(), new()];
    internal readonly stereo_dec_state sStereo = new();
    internal int nChannelsAPI;
    internal int nChannelsInternal;
    internal int prev_decode_only_middle;
}

[SkipLocalsInit]
internal static class DecApi
{
    /* celt/arch.h with ENABLE_RES24: INT16TORES(a) = SHL32( EXTEND32( a ), RES_SHIFT ), RES_SHIFT = 8 */
    private const int RES_SHIFT = 8;

    private static int INT16TORES(short a) => (int)((uint)(int)a << RES_SHIFT);

    private static readonly int[] mult_tab = [6, 4, 3];

    /* Reset decoder state */
    internal static int silk_ResetDecoder(  /* O    Returns error code                              */
        silk_decoder decState)              /* I/O  State                                           */
    {
        int n, ret = SILK_NO_ERROR;
        silk_decoder_state[] channel_state = decState.channel_state;

        for (n = 0; n < DECODER_NUM_CHANNELS; n++)
        {
            ret = silk_reset_decoder(channel_state[n]);
        }
        decState.sStereo.Clear();
        /* Not strictly needed, but it's cleaner that way */
        decState.prev_decode_only_middle = 0;

        return ret;
    }

    internal static int silk_InitDecoder(   /* O    Returns error code                              */
        silk_decoder decState)              /* I/O  State                                           */
    {
        int n, ret = SILK_NO_ERROR;
        silk_decoder_state[] channel_state = decState.channel_state;

        for (n = 0; n < DECODER_NUM_CHANNELS; n++)
        {
            ret = silk_init_decoder(channel_state[n]);
        }
        decState.sStereo.Clear();
        /* Not strictly needed, but it's cleaner that way */
        decState.prev_decode_only_middle = 0;

        return ret;
    }

    /* Decode a frame */
    internal static int silk_Decode(                /* O    Returns error code                              */
        silk_decoder decState,                      /* I/O  State                                           */
        silk_DecControlStruct decControl,           /* I/O  Control Structure                               */
        int lostFlag,                               /* I    0: no loss, 1 loss, 2 decode fec                */
        int newPacketFlag,                          /* I    Indicates first decoder call for this packet    */
        ref ec_dec psRangeDec,                      /* I/O  Compressor data structure                       */
        scoped Span<int> samplesOut,                     /* O    Decoded output speech vector                    */
        out int nSamplesOut)                        /* O    Number of samples decoded                       */
    {
        int i, n, decode_only_middle = 0, ret = SILK_NO_ERROR;
        int nSamplesOutDec = 0, LBRR_symbol;
        Span<int> MS_pred_Q13 = stackalloc int[2];
        scoped Span<short> resample_out_ptr;
        silk_decoder psDec = decState;
        silk_decoder_state[] channel_state = psDec.channel_state;
        int has_side;
        int stereo_to_mono;

        /* C: opus_int16 pulses[ MAX_FRAME_LENGTH ] in the LBRR loop below, hoisted out of the loop */
        Span<short> pulses = stackalloc short[MAX_FRAME_LENGTH];
        /* C: ALLOC( samplesOut1_tmp_storage1, nChannelsInternal * ( frame_length + 2 ), opus_int16 ), at most this size */
        Span<short> samplesOut1_tmp_storage1 = stackalloc short[2 * (MAX_FRAME_LENGTH + 2)];
        /* C: ALLOC( samplesOut2_tmp, *nSamplesOut, opus_int16 ), at most this size (20 ms at 48 kHz) */
        Span<short> samplesOut2_tmp = stackalloc short[MAX_FRAME_LENGTH_MS * MAX_API_FS_KHZ];

        nSamplesOut = 0;

        Debug.Assert(decControl.nChannelsInternal == 1 || decControl.nChannelsInternal == 2);

        /**********************************/
        /* Test if first frame in payload */
        /**********************************/
        if (newPacketFlag != 0)
        {
            for (n = 0; n < decControl.nChannelsInternal; n++)
            {
                channel_state[n].nFramesDecoded = 0;  /* Used to count frames in packet */
            }
        }

        /* If Mono -> Stereo transition in bitstream: init state of second channel */
        if (decControl.nChannelsInternal > psDec.nChannelsInternal)
        {
            ret += silk_init_decoder(channel_state[1]);
        }

        stereo_to_mono = (decControl.nChannelsInternal == 1 && psDec.nChannelsInternal == 2 &&
                         (decControl.internalSampleRate == 1000 * channel_state[0].fs_kHz)) ? 1 : 0;

        if (channel_state[0].nFramesDecoded == 0)
        {
            for (n = 0; n < decControl.nChannelsInternal; n++)
            {
                int fs_kHz_dec;
                if (decControl.payloadSize_ms == 0)
                {
                    /* Assuming packet loss, use 10 ms */
                    channel_state[n].nFramesPerPacket = 1;
                    channel_state[n].nb_subfr = 2;
                }
                else if (decControl.payloadSize_ms == 10)
                {
                    channel_state[n].nFramesPerPacket = 1;
                    channel_state[n].nb_subfr = 2;
                }
                else if (decControl.payloadSize_ms == 20)
                {
                    channel_state[n].nFramesPerPacket = 1;
                    channel_state[n].nb_subfr = 4;
                }
                else if (decControl.payloadSize_ms == 40)
                {
                    channel_state[n].nFramesPerPacket = 2;
                    channel_state[n].nb_subfr = 4;
                }
                else if (decControl.payloadSize_ms == 60)
                {
                    channel_state[n].nFramesPerPacket = 3;
                    channel_state[n].nb_subfr = 4;
                }
                else
                {
                    Debug.Assert(false);
                    return SILK_DEC_INVALID_FRAME_SIZE;
                }
                fs_kHz_dec = (decControl.internalSampleRate >> 10) + 1;
                if (fs_kHz_dec != 8 && fs_kHz_dec != 12 && fs_kHz_dec != 16)
                {
                    Debug.Assert(false);
                    return SILK_DEC_INVALID_SAMPLING_FREQUENCY;
                }
                ret += silk_decoder_set_fs(channel_state[n], fs_kHz_dec, decControl.API_sampleRate);
            }
        }

        if (decControl.nChannelsAPI == 2 && decControl.nChannelsInternal == 2 && (psDec.nChannelsAPI == 1 || psDec.nChannelsInternal == 1))
        {
            Array.Clear(psDec.sStereo.pred_prev_Q13);
            Array.Clear(psDec.sStereo.sSide);
            channel_state[1].resampler_state.CopyFrom(channel_state[0].resampler_state);
        }
        psDec.nChannelsAPI = decControl.nChannelsAPI;
        psDec.nChannelsInternal = decControl.nChannelsInternal;

        if (decControl.API_sampleRate > MAX_API_FS_KHZ * 1000 || decControl.API_sampleRate < 8000)
        {
            ret = SILK_DEC_INVALID_SAMPLING_FREQUENCY;
            return ret;
        }

        if (lostFlag != FLAG_PACKET_LOST && channel_state[0].nFramesDecoded == 0)
        {
            /* First decoder call for this payload */
            /* Decode VAD flags and LBRR flag */
            for (n = 0; n < decControl.nChannelsInternal; n++)
            {
                for (i = 0; i < channel_state[n].nFramesPerPacket; i++)
                {
                    channel_state[n].VAD_flags[i] = psRangeDec.ec_dec_bit_logp(1);
                }
                channel_state[n].LBRR_flag = psRangeDec.ec_dec_bit_logp(1);
            }
            /* Decode LBRR flags */
            for (n = 0; n < decControl.nChannelsInternal; n++)
            {
                Array.Clear(channel_state[n].LBRR_flags);
                if (channel_state[n].LBRR_flag != 0)
                {
                    if (channel_state[n].nFramesPerPacket == 1)
                    {
                        channel_state[n].LBRR_flags[0] = 1;
                    }
                    else
                    {
                        LBRR_symbol = psRangeDec.ec_dec_icdf(silk_LBRR_flags_iCDF_ptr[channel_state[n].nFramesPerPacket - 2], 8) + 1;
                        for (i = 0; i < channel_state[n].nFramesPerPacket; i++)
                        {
                            channel_state[n].LBRR_flags[i] = silk_RSHIFT(LBRR_symbol, i) & 1;
                        }
                    }
                }
            }

            if (lostFlag == FLAG_DECODE_NORMAL)
            {
                /* Regular decoding: skip all LBRR data */
                for (i = 0; i < channel_state[0].nFramesPerPacket; i++)
                {
                    for (n = 0; n < decControl.nChannelsInternal; n++)
                    {
                        if (channel_state[n].LBRR_flags[i] != 0)
                        {
                            int condCoding;

                            if (decControl.nChannelsInternal == 2 && n == 0)
                            {
                                silk_stereo_decode_pred(ref psRangeDec, MS_pred_Q13);
                                if (channel_state[1].LBRR_flags[i] == 0)
                                {
                                    silk_stereo_decode_mid_only(ref psRangeDec, out decode_only_middle);
                                }
                            }
                            /* Use conditional coding if previous frame available */
                            if (i > 0 && channel_state[n].LBRR_flags[i - 1] != 0)
                            {
                                condCoding = CODE_CONDITIONALLY;
                            }
                            else
                            {
                                condCoding = CODE_INDEPENDENTLY;
                            }
                            silk_decode_indices(channel_state[n], ref psRangeDec, i, 1, condCoding);
                            silk_decode_pulses(ref psRangeDec, pulses, channel_state[n].indices.signalType,
                                channel_state[n].indices.quantOffsetType, channel_state[n].frame_length);
                        }
                    }
                }
            }
        }

        /* Get MS predictor index */
        if (decControl.nChannelsInternal == 2)
        {
            if (lostFlag == FLAG_DECODE_NORMAL ||
                (lostFlag == FLAG_DECODE_LBRR && channel_state[0].LBRR_flags[channel_state[0].nFramesDecoded] == 1))
            {
                silk_stereo_decode_pred(ref psRangeDec, MS_pred_Q13);
                /* For LBRR data, decode mid-only flag only if side-channel's LBRR flag is false */
                if ((lostFlag == FLAG_DECODE_NORMAL && channel_state[1].VAD_flags[channel_state[0].nFramesDecoded] == 0) ||
                    (lostFlag == FLAG_DECODE_LBRR && channel_state[1].LBRR_flags[channel_state[0].nFramesDecoded] == 0))
                {
                    silk_stereo_decode_mid_only(ref psRangeDec, out decode_only_middle);
                }
                else
                {
                    decode_only_middle = 0;
                }
            }
            else
            {
                for (n = 0; n < 2; n++)
                {
                    MS_pred_Q13[n] = psDec.sStereo.pred_prev_Q13[n];
                }
            }
        }

        /* Reset side channel decoder prediction memory for first frame with side coding */
        if (decControl.nChannelsInternal == 2 && decode_only_middle == 0 && psDec.prev_decode_only_middle == 1)
        {
            Array.Clear(psDec.channel_state[1].outBuf);
            Array.Clear(psDec.channel_state[1].sLPC_Q14_buf);
            psDec.channel_state[1].lagPrev = 100;
            psDec.channel_state[1].LastGainIndex = 10;
            psDec.channel_state[1].prevSignalType = TYPE_NO_VOICE_ACTIVITY;
            psDec.channel_state[1].first_frame_after_reset = 1;
        }

        /* Check if the temp buffer fits into the output PCM buffer. If it fits,
           we can delay allocating the temp buffer until after the SILK peak stack
           usage. We need to use a < and not a <= because of the two extra samples. */
        /* C: samplesOut1_tmp[ 0 ] = samplesOut1_tmp_storage1;
              samplesOut1_tmp[ 1 ] = samplesOut1_tmp_storage1 + channel_state[ 0 ].frame_length + 2 */
        Span<short> samplesOut1_tmp_0 = samplesOut1_tmp_storage1[..(channel_state[0].frame_length + 2)];
        Span<short> samplesOut1_tmp_1 = samplesOut1_tmp_storage1.Slice(channel_state[0].frame_length + 2, channel_state[0].frame_length + 2);

        if (lostFlag == FLAG_DECODE_NORMAL)
        {
            has_side = decode_only_middle == 0 ? 1 : 0;
        }
        else
        {
            has_side = (psDec.prev_decode_only_middle == 0
                  || (decControl.nChannelsInternal == 2 && lostFlag == FLAG_DECODE_LBRR && channel_state[1].LBRR_flags[channel_state[1].nFramesDecoded] == 1)) ? 1 : 0;
        }
        channel_state[0].sPLC.enable_deep_plc = decControl.enable_deep_plc;
        /* Call decoder for one frame */
        for (n = 0; n < decControl.nChannelsInternal; n++)
        {
            Span<short> samplesOut1_tmp_n = n == 0 ? samplesOut1_tmp_0 : samplesOut1_tmp_1;
            if (n == 0 || has_side != 0)
            {
                int FrameIndex;
                int condCoding;

                FrameIndex = channel_state[0].nFramesDecoded - n;
                /* Use independent coding if no previous frame available */
                if (FrameIndex <= 0)
                {
                    condCoding = CODE_INDEPENDENTLY;
                }
                else if (lostFlag == FLAG_DECODE_LBRR)
                {
                    condCoding = channel_state[n].LBRR_flags[FrameIndex - 1] != 0 ? CODE_CONDITIONALLY : CODE_INDEPENDENTLY;
                }
                else if (n > 0 && psDec.prev_decode_only_middle != 0)
                {
                    /* If we skipped a side frame in this packet, we don't
                       need LTP scaling; the LTP state is well-defined. */
                    condCoding = CODE_INDEPENDENTLY_NO_LTP_SCALING;
                }
                else
                {
                    condCoding = CODE_CONDITIONALLY;
                }
                ret += silk_decode_frame(channel_state[n], ref psRangeDec, samplesOut1_tmp_n[2..], out nSamplesOutDec, lostFlag, condCoding);
            }
            else
            {
                samplesOut1_tmp_n.Slice(2, nSamplesOutDec).Clear();
            }
            channel_state[n].nFramesDecoded++;
        }

        if (decControl.nChannelsAPI == 2 && decControl.nChannelsInternal == 2)
        {
            /* Convert Mid/Side to Left/Right */
            silk_stereo_MS_to_LR(psDec.sStereo, samplesOut1_tmp_0, samplesOut1_tmp_1, MS_pred_Q13, channel_state[0].fs_kHz, nSamplesOutDec);
        }
        else
        {
            /* Buffering */
            psDec.sStereo.sMid.AsSpan(0, 2).CopyTo(samplesOut1_tmp_0);
            samplesOut1_tmp_0.Slice(nSamplesOutDec, 2).CopyTo(psDec.sStereo.sMid);
        }

        /* Number of output samples */
        nSamplesOut = silk_DIV32(nSamplesOutDec * decControl.API_sampleRate, silk_SMULBB(channel_state[0].fs_kHz, 1000));

        /* Set up pointers to temp buffers */
        resample_out_ptr = samplesOut2_tmp[..nSamplesOut];

        for (n = 0; n < silk_min(decControl.nChannelsAPI, decControl.nChannelsInternal); n++)
        {
            Span<short> samplesOut1_tmp_n = n == 0 ? samplesOut1_tmp_0 : samplesOut1_tmp_1;

            /* Resample decoded signal to API_sampleRate */
            ret += silk_resampler(channel_state[n].resampler_state, resample_out_ptr, samplesOut1_tmp_n[1..], nSamplesOutDec);

            /* Interleave if stereo output and stereo stream */
            if (decControl.nChannelsAPI == 2)
            {
                for (i = 0; i < nSamplesOut; i++)
                {
                    samplesOut[n + (2 * i)] = INT16TORES(resample_out_ptr[i]);
                }
            }
            else
            {
                for (i = 0; i < nSamplesOut; i++)
                {
                    samplesOut[i] = INT16TORES(resample_out_ptr[i]);
                }
            }
        }

        /* Create two channel output from mono stream */
        if (decControl.nChannelsAPI == 2 && decControl.nChannelsInternal == 1)
        {
            if (stereo_to_mono != 0)
            {
                /* Resample right channel for newly collapsed stereo just in case
                   we weren't doing collapsing when switching to mono */
                ret += silk_resampler(channel_state[1].resampler_state, resample_out_ptr, samplesOut1_tmp_0[1..], nSamplesOutDec);

                for (i = 0; i < nSamplesOut; i++)
                {
                    samplesOut[1 + (2 * i)] = INT16TORES(resample_out_ptr[i]);
                }
            }
            else
            {
                for (i = 0; i < nSamplesOut; i++)
                {
                    samplesOut[1 + (2 * i)] = samplesOut[0 + (2 * i)];
                }
            }
        }

        /* Export pitch lag, measured at 48 kHz sampling rate */
        if (channel_state[0].prevSignalType == TYPE_VOICED)
        {
            decControl.prevPitchLag = channel_state[0].lagPrev * mult_tab[(channel_state[0].fs_kHz - 8) >> 2];
        }
        else
        {
            decControl.prevPitchLag = 0;
        }

        if (lostFlag == FLAG_PACKET_LOST)
        {
            /* On packet loss, remove the gain clamping to prevent having the energy "bounce back"
               if we lose packets when the energy is going down */
            for (i = 0; i < psDec.nChannelsInternal; i++)
            {
                psDec.channel_state[i].LastGainIndex = 10;
            }
        }
        else
        {
            psDec.prev_decode_only_middle = decode_only_middle;
        }
        return ret;
    }
}
