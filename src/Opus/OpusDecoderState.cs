/* Copyright (c) 2010 Xiph.Org Foundation, Skype Limited
   Written by Jean-Marc Valin and Koen Vos
   Ported to C# from libopus 1.6.1 (src/opus_decoder.c, src/opus.c) by Hot Chai Games LLC */
/*
   Redistribution and use in source and binary forms, with or without
   modification, are permitted provided that the following conditions
   are met:

   - Redistributions of source code must retain the above copyright
   notice, this list of conditions and the following disclaimer.

   - Redistributions in binary form must reproduce the above copyright
   notice, this list of conditions and the following disclaimer in the
   documentation and/or other materials provided with the distribution.

   THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
   ``AS IS'' AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
   LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
   A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER
   OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL,
   EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO,
   PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR
   PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF
   LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
   NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
   SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
*/

using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using DotGame.AudioCodecs.Opus.Celt;
using DotGame.AudioCodecs.Opus.Common;
using DotGame.AudioCodecs.Opus.Silk;
using static DotGame.AudioCodecs.Opus.OpusDefines;

namespace DotGame.AudioCodecs.Opus;

/// <summary>
/// An Opus decoder: libopus's OpusDecoderState (fixed point, 24-bit internal resolution), which
/// decodes each packet with SILK, CELT or both. It allocates nothing once made.
/// </summary>
[SkipLocalsInit]
internal sealed class OpusDecoderState
{
    private readonly OpusCustomDecoder celt_dec;
    private readonly silk_decoder silk_dec;
    private readonly int channels;
    /* Sampling rate (at the API level) */
    private readonly int Fs;
    private readonly silk_DecControlStruct DecControl = new silk_DecControlStruct();
    private int decode_gain;
    private int complexity;
    private int ignore_extensions;

    /* Everything beyond this point gets cleared on a reset */
    private int stream_channels;
    private int bandwidth;
    private int mode;
    private int prev_mode;
    private int frame_size;
    private int prev_redundancy;
    private int last_packet_duration;
    private uint rangeFinal;

    // opus_decode's opus_res buffer: 120 ms of each channel
    private readonly int[] _out;

    /// <summary>opus_decoder_init.</summary>
    internal OpusDecoderState(int Fs, int channels)
    {
        if ((Fs != 48000 && Fs != 24000 && Fs != 16000 && Fs != 12000 && Fs != 8000)
            || (channels != 1 && channels != 2))
        {
            throw new ArgumentException("Opus decodes at 8, 12, 16, 24 or 48 kHz, to one or two channels.");
        }

        this.silk_dec = new silk_decoder();
        this.stream_channels = this.channels = channels;
        this.complexity = 0;

        this.Fs = Fs;
        this.DecControl.API_sampleRate = this.Fs;
        this.DecControl.nChannelsAPI = this.channels;

        /* Reset decoder */
        if (DecApi.silk_InitDecoder(this.silk_dec) != 0)
        {
            throw new InvalidOperationException("The SILK decoder didn't initialise.");
        }

        /* Initialize CELT decoder */
        this.celt_dec = new OpusCustomDecoder(Fs, channels);
        this.celt_dec.SetSignalling(0);

        this.prev_mode = 0;
        this.frame_size = Fs / 400;
        this._out = new int[(Fs / 25 * 3) * channels];
    }

    /// <summary>
    /// Decodes a packet (empty to conceal a lost one) to 16-bit interleaved samples, at most
    /// <paramref name="frame_size"/> of each channel, and returns how many of each it decoded.
    /// </summary>
    /// <exception cref="InvalidDataException">The packet is damaged.</exception>
    internal int Decode(ReadOnlySpan<byte> data, Span<short> pcm, int frame_size, bool decode_fec = false)
    {
        int ret = this.opus_decode(data, data.Length, pcm, frame_size, decode_fec ? 1 : 0);
        if (ret < 0)
        {
            throw new InvalidDataException("The Opus packet can't be decoded: " + opus_strerror(ret) + ".");
        }
        return ret;
    }

    /// <summary>
    /// opus_decode24 (the RES24 build's): decodes a packet (empty to conceal a lost one) to the
    /// decoder's own 24-bit samples, interleaved, at most <paramref name="frame_size"/> of each channel,
    /// and returns how many of each it decoded. opus_decode's 16 bits are RES2INT16 of them, and
    /// opus_decode_float's floats RES2FLOAT.
    /// </summary>
    /// <exception cref="InvalidDataException">The packet is damaged.</exception>
    internal int DecodeRes(ReadOnlySpan<byte> data, Span<int> pcm, int frame_size, bool decode_fec = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frame_size);
        int ret = this.opus_decode_native(data, data.Length, pcm, frame_size, decode_fec ? 1 : 0, 0, out _);
        if (ret < 0)
        {
            throw new InvalidDataException("The Opus packet can't be decoded: " + opus_strerror(ret) + ".");
        }
        return ret;
    }

    /// <summary>RES2INT16 of a frame, eight samples at a time where the hardware has vectors.</summary>
    internal static void ResToInt16(ReadOnlySpan<int> input, Span<short> output) => res2int16(input, output);

    /// <summary>RES2FLOAT of a frame: (1/32768/256) × each sample.</summary>
    internal static void ResToFloat(ReadOnlySpan<int> input, Span<float> output)
    {
        for (int i = 0; i < input.Length; i++)
        {
            output[i] = (1f / 32768f / 256f) * input[i];
        }
    }
    /// <summary>OPUS_RESET_STATE: as a new decoder, for a seek.</summary>
    internal void ResetState()
    {
        this.stream_channels = 0;
        this.bandwidth = 0;
        this.mode = 0;
        this.prev_mode = 0;
        this.frame_size = 0;
        this.prev_redundancy = 0;
        this.last_packet_duration = 0;
        this.rangeFinal = 0;

        this.celt_dec.ResetState();
        DecApi.silk_ResetDecoder(this.silk_dec);
        this.stream_channels = this.channels;
        this.frame_size = this.Fs / 400;
    }

    /// <summary>OPUS_GET_FINAL_RANGE: the range coder's final state, for testing.</summary>
    internal uint GetFinalRange() => this.rangeFinal;

    /// <summary>OPUS_SET_GAIN: Q8 dB, -32768 to 32767.</summary>
    internal void SetGain(int value)
    {
        if (value < -32768 || value > 32767)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        this.decode_gain = value;
    }

    /// <summary>OPUS_SET_COMPLEXITY: 0 to 10.</summary>
    internal void SetComplexity(int value)
    {
        if (value < 0 || value > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        this.complexity = value;
        this.celt_dec.SetComplexity(value);
    }

    /// <summary>OPUS_GET_LAST_PACKET_DURATION.</summary>
    internal int GetLastPacketDuration() => this.last_packet_duration;

    /// <summary>OPUS_GET_PITCH.</summary>
    internal int GetPitch() => this.prev_mode == MODE_CELT_ONLY ? this.celt_dec.GetPitch() : this.DecControl.prevPitchLag;

    /// <summary>OPUS_SET_IGNORE_EXTENSIONS.</summary>
    internal void SetIgnoreExtensions(bool value) => this.ignore_extensions = value ? 1 : 0;

    private static void smooth_fade(ReadOnlySpan<int> in1, ReadOnlySpan<int> in2,
        Span<int> output, int overlap, int channels, ReadOnlySpan<short> window, int Fs)
    {
        int i, c;
        int inc = 48000 / Fs;
        for (c = 0; c < channels; c++)
        {
            for (i = 0; i < overlap; i++)
            {
                short w = (short)Arch.MULT16_16_Q15(window[i * inc], window[i * inc]);
                output[(i * channels) + c] = Arch.ADD32(Arch.MULT16_32_Q15(w, in2[(i * channels) + c]),
                    Arch.MULT16_32_Q15((short)(Arch.Q15ONE - w), in1[(i * channels) + c]));
            }
        }
    }

    private static int opus_packet_get_mode(ReadOnlySpan<byte> data)
    {
        int mode;
        if ((data[0] & 0x80) != 0)
        {
            mode = MODE_CELT_ONLY;
        }
        else if ((data[0] & 0x60) == 0x60)
        {
            mode = MODE_HYBRID;
        }
        else
        {
            mode = MODE_SILK_ONLY;
        }
        return mode;
    }

    private int opus_decode_frame(ReadOnlySpan<byte> data, int len, Span<int> pcm, int frame_size, int decode_fec)
    {
        int i, silk_ret = 0, celt_ret = 0;
        ec_dec dec = default;
        int silk_frame_size;
        int pcm_transition_silk_size;
        int pcm_transition_celt_size;
        scoped Span<int> pcm_transition = default;
        int redundant_audio_size;

        int audiosize;
        int mode;
        int bandwidth;
        int transition = 0;
        int start_band;
        int redundancy = 0;
        int redundancy_bytes = 0;
        int celt_to_silk = 0;
        int c;
        int F2_5, F5, F10, F20;
        ReadOnlySpan<short> window;
        uint redundant_rng = 0;
        int celt_accum;

        F20 = this.Fs / 50;
        F10 = F20 >> 1;
        F5 = F10 >> 1;
        F2_5 = F5 >> 1;
        if (frame_size < F2_5)
        {
            return OPUS_BUFFER_TOO_SMALL;
        }
        /* Limit frame_size to avoid excessive stack allocations. */
        frame_size = Math.Min(frame_size, this.Fs / 25 * 3);
        /* Payloads of 1 (2 including ToC) or 0 trigger the PLC/DTX */
        if (len <= 1)
        {
            data = default;
            /* In that case, don't conceal more than what the ToC says */
            frame_size = Math.Min(frame_size, this.frame_size);
        }
        bool haveData = !data.IsEmpty;
        if (haveData)
        {
            audiosize = this.frame_size;
            mode = this.mode;
            bandwidth = this.bandwidth;
            dec.ec_dec_init(data, (uint)len);
        }
        else
        {
            audiosize = frame_size;
            /* Run PLC using last used mode (CELT if we ended with CELT redundancy) */
            mode = this.prev_redundancy != 0 ? MODE_CELT_ONLY : this.prev_mode;
            bandwidth = 0;

            if (mode == 0)
            {
                /* If we haven't got any packet yet, all we can do is return zeros */
                pcm.Slice(0, audiosize * this.channels).Clear();
                return audiosize;
            }

            /* Avoids trying to run the PLC on sizes other than 2.5 (CELT), 5 (CELT),
               10, or 20 (e.g. 12.5 or 30 ms). */
            if (audiosize > F20)
            {
                do
                {
                    int ret = this.opus_decode_frame(default, 0, pcm, Math.Min(audiosize, F20), 0);
                    if (ret < 0)
                    {
                        return ret;
                    }
                    pcm = pcm.Slice(ret * this.channels);
                    audiosize -= ret;
                }
                while (audiosize > 0);
                return frame_size;
            }
            else if (audiosize < F20)
            {
                if (audiosize > F10)
                {
                    audiosize = F10;
                }
                else if (mode != MODE_SILK_ONLY && audiosize > F5 && audiosize < F10)
                {
                    audiosize = F5;
                }
            }
        }

        /* In fixed-point, we can tell CELT to do the accumulation on top of the
           SILK PCM buffer. This saves some stack space. */
        celt_accum = mode != MODE_CELT_ONLY ? 1 : 0;

        pcm_transition_silk_size = 0;
        pcm_transition_celt_size = 0;
        if (haveData && this.prev_mode > 0 && (
            (mode == MODE_CELT_ONLY && this.prev_mode != MODE_CELT_ONLY && this.prev_redundancy == 0)
            || (mode != MODE_CELT_ONLY && this.prev_mode == MODE_CELT_ONLY)))
        {
            transition = 1;
            /* Decide where to allocate the stack memory for pcm_transition */
            if (mode == MODE_CELT_ONLY)
            {
                pcm_transition_celt_size = F5 * this.channels;
            }
            else
            {
                pcm_transition_silk_size = F5 * this.channels;
            }
        }
        Span<int> pcm_transition_celt = stackalloc int[pcm_transition_celt_size];
        if (transition != 0 && mode == MODE_CELT_ONLY)
        {
            pcm_transition = pcm_transition_celt;
            this.opus_decode_frame(default, 0, pcm_transition, Math.Min(F5, audiosize), 0);
        }
        if (audiosize > frame_size)
        {
            /*fprintf(stderr, "PCM buffer too small: %d vs %d (mode = %d)\n", audiosize, frame_size, mode);*/
            return OPUS_BAD_ARG;
        }
        else
        {
            frame_size = audiosize;
        }

        /* SILK processing */
        if (mode != MODE_CELT_ONLY)
        {
            int lost_flag, decoded_samples;
            scoped Span<int> pcm_ptr;
            bool pcm_too_small;
            int pcm_silk_size = 0;
            pcm_too_small = frame_size < F10;
            if (pcm_too_small)
            {
                pcm_silk_size = F10 * this.channels;
            }
            Span<int> pcm_silk = stackalloc int[pcm_silk_size];
            if (pcm_too_small)
            {
                pcm_ptr = pcm_silk;
            }
            else
            {
                pcm_ptr = pcm;
            }

            if (this.prev_mode == MODE_CELT_ONLY)
            {
                DecApi.silk_ResetDecoder(this.silk_dec);
            }

            /* The SILK PLC cannot produce frames of less than 10 ms */
            this.DecControl.payloadSize_ms = Math.Max(10, 1000 * audiosize / this.Fs);

            if (haveData)
            {
                this.DecControl.nChannelsInternal = this.stream_channels;
                if (mode == MODE_SILK_ONLY)
                {
                    if (bandwidth == OPUS_BANDWIDTH_NARROWBAND)
                    {
                        this.DecControl.internalSampleRate = 8000;
                    }
                    else if (bandwidth == OPUS_BANDWIDTH_MEDIUMBAND)
                    {
                        this.DecControl.internalSampleRate = 12000;
                    }
                    else if (bandwidth == OPUS_BANDWIDTH_WIDEBAND)
                    {
                        this.DecControl.internalSampleRate = 16000;
                    }
                    else
                    {
                        this.DecControl.internalSampleRate = 16000;
                    }
                }
                else
                {
                    /* Hybrid mode */
                    this.DecControl.internalSampleRate = 16000;
                }
            }
            this.DecControl.enable_deep_plc = this.complexity >= 5 ? 1 : 0;

            lost_flag = !haveData ? 1 : 2 * (decode_fec != 0 ? 1 : 0);
            decoded_samples = 0;
            do
            {
                /* Call SILK decoder */
                int first_frame = decoded_samples == 0 ? 1 : 0;
                silk_ret = DecApi.silk_Decode(this.silk_dec, this.DecControl,
                    lost_flag, first_frame, ref dec, pcm_ptr, out silk_frame_size);
                if (silk_ret != 0)
                {
                    if (lost_flag != 0)
                    {
                        /* PLC failure should not be fatal */
                        silk_frame_size = frame_size;
                        pcm_ptr.Slice(0, frame_size * this.channels).Clear();
                    }
                    else
                    {
                        return OPUS_INTERNAL_ERROR;
                    }
                }
                pcm_ptr = pcm_ptr.Slice(silk_frame_size * this.channels);
                decoded_samples += silk_frame_size;
            }
            while (decoded_samples < frame_size);
            if (pcm_too_small)
            {
                pcm_silk.Slice(0, frame_size * this.channels).CopyTo(pcm);
            }
        }

        start_band = 0;
        if (decode_fec == 0 && mode != MODE_CELT_ONLY && haveData
            && dec.ec_tell() + 17 + (20 * (mode == MODE_HYBRID ? 1 : 0)) <= 8 * len)
        {
            /* Check if we have a redundant 0-8 kHz band */
            if (mode == MODE_HYBRID)
            {
                redundancy = dec.ec_dec_bit_logp(12);
            }
            else
            {
                redundancy = 1;
            }
            if (redundancy != 0)
            {
                celt_to_silk = dec.ec_dec_bit_logp(1);
                /* redundancy_bytes will be at least two, in the non-hybrid
                   case due to the ec_tell() check above */
                redundancy_bytes = mode == MODE_HYBRID
                    ? (int)dec.ec_dec_uint(256) + 2
                    : len - ((dec.ec_tell() + 7) >> 3);
                len -= redundancy_bytes;
                /* This is a sanity check. It should never happen for a valid
                   packet, so the exact behaviour is not normative. */
                if (len * 8 < dec.ec_tell())
                {
                    len = 0;
                    redundancy_bytes = 0;
                    redundancy = 0;
                }
                /* Shrink decoder because of raw bits */
                dec.storage -= (uint)redundancy_bytes;
            }
        }
        if (mode != MODE_CELT_ONLY)
        {
            start_band = 17;
        }

        if (redundancy != 0)
        {
            transition = 0;
            pcm_transition_silk_size = 0;
        }

        Span<int> pcm_transition_silk = stackalloc int[pcm_transition_silk_size];

        if (transition != 0 && mode != MODE_CELT_ONLY)
        {
            pcm_transition = pcm_transition_silk;
            this.opus_decode_frame(default, 0, pcm_transition, Math.Min(F5, audiosize), 0);
        }

        if (bandwidth != 0)
        {
            int endband = 21;

            switch (bandwidth)
            {
                case OPUS_BANDWIDTH_NARROWBAND:
                    endband = 13;
                    break;
                case OPUS_BANDWIDTH_MEDIUMBAND:
                case OPUS_BANDWIDTH_WIDEBAND:
                    endband = 17;
                    break;
                case OPUS_BANDWIDTH_SUPERWIDEBAND:
                    endband = 19;
                    break;
                case OPUS_BANDWIDTH_FULLBAND:
                    endband = 21;
                    break;
                default:
                    break;
            }
            this.celt_dec.SetEndBand(endband);
        }
        this.celt_dec.SetChannels(this.stream_channels);

        /* Only allocation memory for redundancy if/when needed */
        redundant_audio_size = redundancy != 0 ? F5 * this.channels : 0;
        Span<int> redundant_audio = stackalloc int[redundant_audio_size];

        /* 5 ms redundant frame for CELT->SILK*/
        if (redundancy != 0 && celt_to_silk != 0)
        {
            /* If the previous frame did not use CELT (the first redundancy frame in
               a transition from SILK may have been lost) then the CELT decoder is
               stale at this point and the redundancy audio is not useful, however
               the final range is still needed (for testing), so the redundancy is
               always decoded but the decoded audio may not be used */
            this.celt_dec.SetStartBand(0);
            ec_dec none = default;
            CeltDecoder.celt_decode_with_ec(this.celt_dec, data.Slice(len), redundancy_bytes,
                redundant_audio, F5, ref none, false, 0);
            redundant_rng = this.celt_dec.GetFinalRange();
        }

        /* MUST be after PLC */
        this.celt_dec.SetStartBand(start_band);

        if (mode != MODE_SILK_ONLY)
        {
            int celt_frame_size = Math.Min(F20, frame_size);
            /* Make sure to discard any previous CELT state */
            if (mode != this.prev_mode && this.prev_mode > 0 && this.prev_redundancy == 0)
            {
                this.celt_dec.ResetState();
            }
            /* Decode CELT */
            celt_ret = CeltDecoder.celt_decode_with_ec(this.celt_dec, decode_fec != 0 ? default : data,
                len, pcm, celt_frame_size, ref dec, true, celt_accum);
            this.rangeFinal = this.celt_dec.GetFinalRange();
        }
        else
        {
            ReadOnlySpan<byte> silence = [0xFF, 0xFF];
            if (celt_accum == 0)
            {
                pcm.Slice(0, frame_size * this.channels).Clear();
            }
            /* For hybrid -> SILK transitions, we let the CELT MDCT
               do a fade-out by decoding a silence frame */
            if (this.prev_mode == MODE_HYBRID && !(redundancy != 0 && celt_to_silk != 0 && this.prev_redundancy != 0))
            {
                this.celt_dec.SetStartBand(0);
                ec_dec none = default;
                CeltDecoder.celt_decode_with_ec(this.celt_dec, silence, 2, pcm, F2_5, ref none, false, celt_accum);
            }
            this.rangeFinal = dec.rng;
        }

        window = this.celt_dec.GetMode().window;

        /* 5 ms redundant frame for SILK->CELT */
        if (redundancy != 0 && celt_to_silk == 0)
        {
            this.celt_dec.ResetState();
            this.celt_dec.SetStartBand(0);

            ec_dec none = default;
            CeltDecoder.celt_decode_with_ec(this.celt_dec, data.Slice(len), redundancy_bytes, redundant_audio, F5, ref none, false, 0);
            redundant_rng = this.celt_dec.GetFinalRange();
            smooth_fade(pcm.Slice(this.channels * (frame_size - F2_5)), redundant_audio.Slice(this.channels * F2_5),
                pcm.Slice(this.channels * (frame_size - F2_5)), F2_5, this.channels, window, this.Fs);
        }
        /* 5ms redundant frame for CELT->SILK; ignore if the previous frame did not
           use CELT (the first redundancy frame in a transition from SILK may have
           been lost) */
        if (redundancy != 0 && celt_to_silk != 0 && (this.prev_mode != MODE_SILK_ONLY || this.prev_redundancy != 0))
        {
            for (c = 0; c < this.channels; c++)
            {
                for (i = 0; i < F2_5; i++)
                {
                    pcm[(this.channels * i) + c] = redundant_audio[(this.channels * i) + c];
                }
            }
            smooth_fade(redundant_audio.Slice(this.channels * F2_5), pcm.Slice(this.channels * F2_5),
                pcm.Slice(this.channels * F2_5), F2_5, this.channels, window, this.Fs);
        }
        if (transition != 0)
        {
            if (audiosize >= F5)
            {
                for (i = 0; i < this.channels * F2_5; i++)
                {
                    pcm[i] = pcm_transition[i];
                }
                smooth_fade(pcm_transition.Slice(this.channels * F2_5), pcm.Slice(this.channels * F2_5),
                    pcm.Slice(this.channels * F2_5), F2_5,
                    this.channels, window, this.Fs);
            }
            else
            {
                /* Not enough time to do a clean transition, but we do it anyway
                   This will not preserve amplitude perfectly and may introduce
                   a bit of temporal aliasing, but it shouldn't be too bad and
                   that's pretty much the best we can do. In any case, generating this
                   transition it pretty silly in the first place */
                smooth_fade(pcm_transition, pcm,
                    pcm, F2_5,
                    this.channels, window, this.Fs);
            }
        }

        if (this.decode_gain != 0)
        {
            int gain;
            // QCONST16(6.48814081e-4f, 25)
            gain = MathOps.celt_exp2((short)Arch.MULT16_16_P15((short)21771, (short)this.decode_gain));
            for (i = 0; i < frame_size * this.channels; i++)
            {
                int x;
                x = Arch.MULT32_32_Q16(pcm[i], gain);
                pcm[i] = Arch.SATURATE(x, 32767);
            }
        }

        if (len <= 1)
        {
            this.rangeFinal = 0;
        }
        else
        {
            this.rangeFinal ^= redundant_rng;
        }

        this.prev_mode = mode;
        this.prev_redundancy = redundancy != 0 && celt_to_silk == 0 ? 1 : 0;

        return celt_ret < 0 ? celt_ret : audiosize;
    }

    internal int opus_decode_native(ReadOnlySpan<byte> data, int len, Span<int> pcm, int frame_size, int decode_fec,
        int self_delimited, out int packet_offset)
    {
        int i, nb_samples;
        int count, offset;
        int packet_frame_size, packet_bandwidth, packet_mode, packet_stream_channels;
        /* 48 x 2.5 ms = 120 ms */
        Span<short> size = stackalloc short[48];
        packet_offset = 0;
        if (decode_fec < 0 || decode_fec > 1)
        {
            return OPUS_BAD_ARG;
        }
        /* For FEC/PLC, frame_size has to be to have a multiple of 2.5 ms */
        if ((decode_fec != 0 || len == 0 || data.IsEmpty) && frame_size % (this.Fs / 400) != 0)
        {
            return OPUS_BAD_ARG;
        }
        if (len == 0 || data.IsEmpty)
        {
            int pcm_count = 0;
            do
            {
                int ret;
                ret = this.opus_decode_frame(default, 0, pcm.Slice(pcm_count * this.channels), frame_size - pcm_count, 0);
                if (ret < 0)
                {
                    return ret;
                }
                pcm_count += ret;
            }
            while (pcm_count < frame_size);
            this.last_packet_duration = pcm_count;
            return pcm_count;
        }
        else if (len < 0)
        {
            return OPUS_BAD_ARG;
        }

        packet_mode = opus_packet_get_mode(data);
        packet_bandwidth = opus_packet_get_bandwidth(data);
        packet_frame_size = opus_packet_get_samples_per_frame(data, this.Fs);
        packet_stream_channels = opus_packet_get_nb_channels(data);

        // The padding (with any extensions) isn't used without QEXT or DRED
        count = opus_packet_parse_impl(data, len, self_delimited, out _, size, out offset, out packet_offset, out _, out _);
        if (count < 0)
        {
            return count;
        }

        data = data.Slice(offset);

        if (decode_fec != 0)
        {
            int duration_copy;
            int ret;
            /* If no FEC can be present, run the PLC (recursive call) */
            if (frame_size < packet_frame_size || packet_mode == MODE_CELT_ONLY || this.mode == MODE_CELT_ONLY)
            {
                return this.opus_decode_native(default, 0, pcm, frame_size, 0, 0, out _);
            }
            /* Otherwise, run the PLC on everything except the size for which we might have FEC */
            duration_copy = this.last_packet_duration;
            if (frame_size - packet_frame_size != 0)
            {
                ret = this.opus_decode_native(default, 0, pcm, frame_size - packet_frame_size, 0, 0, out _);
                if (ret < 0)
                {
                    this.last_packet_duration = duration_copy;
                    return ret;
                }
            }
            /* Complete with FEC */
            this.mode = packet_mode;
            this.bandwidth = packet_bandwidth;
            this.frame_size = packet_frame_size;
            this.stream_channels = packet_stream_channels;
            ret = this.opus_decode_frame(data, size[0], pcm.Slice(this.channels * (frame_size - packet_frame_size)),
                packet_frame_size, 1);
            if (ret < 0)
            {
                return ret;
            }
            else
            {
                this.last_packet_duration = frame_size;
                return frame_size;
            }
        }

        if (count * packet_frame_size > frame_size)
        {
            return OPUS_BUFFER_TOO_SMALL;
        }

        /* Update the state as the last step to avoid updating it on an invalid packet */
        this.mode = packet_mode;
        this.bandwidth = packet_bandwidth;
        this.frame_size = packet_frame_size;
        this.stream_channels = packet_stream_channels;

        nb_samples = 0;
        for (i = 0; i < count; i++)
        {
            int ret;
            ret = this.opus_decode_frame(data, size[i], pcm.Slice(nb_samples * this.channels), frame_size - nb_samples, 0);
            if (ret < 0)
            {
                return ret;
            }
            data = data.Slice(size[i]);
            nb_samples += ret;
        }
        this.last_packet_duration = nb_samples;
        return nb_samples;
    }

    /// <summary>opus_decode, to 16 bits: the RES24 build's, through opus_res.</summary>
    internal int opus_decode(ReadOnlySpan<byte> data, int len, Span<short> pcm, int frame_size, int decode_fec)
    {
        int ret;
        int nb_samples;

        if (frame_size <= 0)
        {
            return OPUS_BAD_ARG;
        }
        if (!data.IsEmpty && len > 0 && decode_fec == 0)
        {
            nb_samples = opus_packet_get_nb_samples(data, len, this.Fs);
            if (nb_samples > 0)
            {
                frame_size = Math.Min(frame_size, nb_samples);
            }
            else
            {
                return OPUS_INVALID_PACKET;
            }
        }
        Span<int> output = this._out.AsSpan(0, Math.Min(frame_size, this.Fs / 25 * 3) * this.channels);

        ret = this.opus_decode_native(data, len, output, Math.Min(frame_size, this.Fs / 25 * 3), decode_fec, 0, out _);
        if (ret > 0)
        {
            res2int16(output.Slice(0, ret * this.channels), pcm);
        }
        return ret;
    }

    // RES2INT16 over a frame: SAT16(PSHR32(x, RES_SHIFT)), eight samples at a time where the
    // hardware has vectors
    private static void res2int16(ReadOnlySpan<int> input, Span<short> output)
    {
        int i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            Vector128<int> round = Vector128.Create(1 << (Arch.RES_SHIFT - 1));
            Vector128<int> min = Vector128.Create((int)short.MinValue);
            Vector128<int> max = Vector128.Create((int)short.MaxValue);
            for (; i + 8 <= input.Length; i += 8)
            {
                Vector128<int> lo = ((Vector128.Create(input.Slice(i, 4)) + round) >> Arch.RES_SHIFT);
                Vector128<int> hi = ((Vector128.Create(input.Slice(i + 4, 4)) + round) >> Arch.RES_SHIFT);
                Vector128.Narrow(Vector128.Clamp(lo, min, max), Vector128.Clamp(hi, min, max)).CopyTo(output.Slice(i));
            }
        }
        for (; i < input.Length; i++)
        {
            output[i] = (short)Arch.RES2INT16(input[i]);
        }
    }

    // src/opus.c and src/opus_decoder.c's packet functions

    internal static int opus_packet_get_bandwidth(ReadOnlySpan<byte> data)
    {
        int bandwidth;
        if ((data[0] & 0x80) != 0)
        {
            bandwidth = OPUS_BANDWIDTH_MEDIUMBAND + ((data[0] >> 5) & 0x3);
            if (bandwidth == OPUS_BANDWIDTH_MEDIUMBAND)
            {
                bandwidth = OPUS_BANDWIDTH_NARROWBAND;
            }
        }
        else if ((data[0] & 0x60) == 0x60)
        {
            bandwidth = (data[0] & 0x10) != 0 ? OPUS_BANDWIDTH_FULLBAND : OPUS_BANDWIDTH_SUPERWIDEBAND;
        }
        else
        {
            bandwidth = OPUS_BANDWIDTH_NARROWBAND + ((data[0] >> 5) & 0x3);
        }
        return bandwidth;
    }

    internal static int opus_packet_get_nb_channels(ReadOnlySpan<byte> data) => (data[0] & 0x4) != 0 ? 2 : 1;

    internal static int opus_packet_get_nb_frames(ReadOnlySpan<byte> packet, int len)
    {
        int count;
        if (len < 1)
        {
            return OPUS_BAD_ARG;
        }
        count = packet[0] & 0x3;
        if (count == 0)
        {
            return 1;
        }
        else if (count != 3)
        {
            return 2;
        }
        else if (len < 2)
        {
            return OPUS_INVALID_PACKET;
        }
        else
        {
            return packet[1] & 0x3F;
        }
    }

    internal static int opus_packet_get_nb_samples(ReadOnlySpan<byte> packet, int len, int Fs)
    {
        int samples;
        int count = opus_packet_get_nb_frames(packet, len);

        if (count < 0)
        {
            return count;
        }

        samples = count * opus_packet_get_samples_per_frame(packet, Fs);
        /* Can't have more than 120 ms */
        if (samples * 25 > Fs * 3)
        {
            return OPUS_INVALID_PACKET;
        }
        else
        {
            return samples;
        }
    }

    internal static int opus_packet_get_samples_per_frame(ReadOnlySpan<byte> data, int Fs)
    {
        int audiosize;
        if ((data[0] & 0x80) != 0)
        {
            audiosize = (data[0] >> 3) & 0x3;
            audiosize = (Fs << audiosize) / 400;
        }
        else if ((data[0] & 0x60) == 0x60)
        {
            audiosize = (data[0] & 0x08) != 0 ? Fs / 50 : Fs / 100;
        }
        else
        {
            audiosize = (data[0] >> 3) & 0x3;
            if (audiosize == 3)
            {
                audiosize = Fs * 60 / 1000;
            }
            else
            {
                audiosize = (Fs << audiosize) / 100;
            }
        }
        return audiosize;
    }

    private static int parse_size(ReadOnlySpan<byte> data, int len, out short size)
    {
        if (len < 1)
        {
            size = -1;
            return -1;
        }
        else if (data[0] < 252)
        {
            size = data[0];
            return 1;
        }
        else if (len < 2)
        {
            size = -1;
            return -1;
        }
        else
        {
            size = (short)((4 * data[1]) + data[0]);
            return 2;
        }
    }

    // opus_packet_parse_impl, without the frame pointers (the decoder walks the sizes)
    internal static int opus_packet_parse_impl(ReadOnlySpan<byte> data, int len,
        int self_delimited, out byte out_toc, Span<short> size,
        out int payload_offset, out int packet_offset, out int padding_offset, out int padding_len)
    {
        int i, bytes;
        int count;
        int cbr;
        byte ch, toc;
        int framesize;
        int last_size;
        int pad = 0;
        int data_ptr = 0;

        out_toc = 0;
        payload_offset = 0;
        packet_offset = 0;
        /* Make sure we return NULL/0 on error. */
        padding_offset = -1;
        padding_len = 0;

        if (len < 0)
        {
            return OPUS_BAD_ARG;
        }
        if (len == 0)
        {
            return OPUS_INVALID_PACKET;
        }

        framesize = opus_packet_get_samples_per_frame(data, 48000);

        cbr = 0;
        toc = data[data_ptr++];
        len--;
        last_size = len;
        switch (toc & 0x3)
        {
            /* One frame */
            case 0:
                count = 1;
                break;
            /* Two CBR frames */
            case 1:
                count = 2;
                cbr = 1;
                if (self_delimited == 0)
                {
                    if ((len & 0x1) != 0)
                    {
                        return OPUS_INVALID_PACKET;
                    }
                    last_size = len / 2;
                    /* If last_size doesn't fit in size[0], we'll catch it later */
                    size[0] = (short)last_size;
                }
                break;
            /* Two VBR frames */
            case 2:
                count = 2;
                bytes = parse_size(data.Slice(data_ptr), len, out size[0]);
                len -= bytes;
                if (size[0] < 0 || size[0] > len)
                {
                    return OPUS_INVALID_PACKET;
                }
                data_ptr += bytes;
                last_size = len - size[0];
                break;
            /* Multiple CBR/VBR frames (from 0 to 120 ms) */
            default: /*case 3:*/
                if (len < 1)
                {
                    return OPUS_INVALID_PACKET;
                }
                /* Number of frames encoded in bits 0 to 5 */
                ch = data[data_ptr++];
                count = ch & 0x3F;
                if (count <= 0 || framesize * count > 5760)
                {
                    return OPUS_INVALID_PACKET;
                }
                len--;
                /* Padding flag is bit 6 */
                if ((ch & 0x40) != 0)
                {
                    int p;
                    do
                    {
                        int tmp;
                        if (len <= 0)
                        {
                            return OPUS_INVALID_PACKET;
                        }
                        p = data[data_ptr++];
                        len--;
                        tmp = p == 255 ? 254 : p;
                        len -= tmp;
                        pad += tmp;
                    }
                    while (p == 255);
                }
                if (len < 0)
                {
                    return OPUS_INVALID_PACKET;
                }
                /* VBR flag is bit 7 */
                cbr = (ch & 0x80) == 0 ? 1 : 0;
                if (cbr == 0)
                {
                    /* VBR case */
                    last_size = len;
                    for (i = 0; i < count - 1; i++)
                    {
                        bytes = parse_size(data.Slice(data_ptr), len, out size[i]);
                        len -= bytes;
                        if (size[i] < 0 || size[i] > len)
                        {
                            return OPUS_INVALID_PACKET;
                        }
                        data_ptr += bytes;
                        last_size -= bytes + size[i];
                    }
                    if (last_size < 0)
                    {
                        return OPUS_INVALID_PACKET;
                    }
                }
                else if (self_delimited == 0)
                {
                    /* CBR case */
                    last_size = len / count;
                    if (last_size * count != len)
                    {
                        return OPUS_INVALID_PACKET;
                    }
                    for (i = 0; i < count - 1; i++)
                    {
                        size[i] = (short)last_size;
                    }
                }
                break;
        }
        /* Self-delimited framing has an extra size for the last frame. */
        if (self_delimited != 0)
        {
            bytes = parse_size(data.Slice(data_ptr), len, out size[count - 1]);
            len -= bytes;
            if (size[count - 1] < 0 || size[count - 1] > len)
            {
                return OPUS_INVALID_PACKET;
            }
            data_ptr += bytes;
            /* For CBR packets, apply the size to all the frames. */
            if (cbr != 0)
            {
                if (size[count - 1] * count > len)
                {
                    return OPUS_INVALID_PACKET;
                }
                for (i = 0; i < count - 1; i++)
                {
                    size[i] = size[count - 1];
                }
            }
            else if (bytes + size[count - 1] > last_size)
            {
                return OPUS_INVALID_PACKET;
            }
        }
        else
        {
            /* Because it's not encoded explicitly, it's possible the size of the
               last packet (or all the packets, for the CBR case) is larger than
               1275. Reject them here.*/
            if (last_size > 1275)
            {
                return OPUS_INVALID_PACKET;
            }
            size[count - 1] = (short)last_size;
        }

        payload_offset = data_ptr;

        for (i = 0; i < count; i++)
        {
            data_ptr += size[i];
        }

        padding_offset = data_ptr;
        padding_len = pad;
        packet_offset = pad + data_ptr;

        out_toc = toc;

        return count;
    }
}
