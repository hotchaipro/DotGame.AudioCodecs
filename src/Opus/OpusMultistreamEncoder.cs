/* Copyright (c) 2011 Xiph.Org Foundation
   Written by Jean-Marc Valin
   Ported to C# from libopus 1.6.1 (src/opus_multistream_encoder.c, src/opus_multistream.c) by Hot Chai Games LLC */
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
using System.Runtime.InteropServices;
using DotGame.AudioCodecs.Opus.Celt;
using static DotGame.AudioCodecs.Opus.Celt.Arch;
using static DotGame.AudioCodecs.Opus.OpusDefines;

namespace DotGame.AudioCodecs.Opus;

/// <summary>
/// The surround multistream encoder (opus_multistream_surround_encoder_create with mapping family 1):
/// 1 to 8 channels in Vorbis order, coded as libopus's Vorbis layouts, with its surround masking
/// analysis and rate allocation, the LFE (6 channels and more) as an LFE stream.
/// </summary>
internal sealed class OpusMultistreamEncoder
{
    private const int MAX_OVERLAP = 120;
    private const int MS_FRAME_TMP = (6 * 1275) + 12;

    /* Index is nb_channel-1 */
    private static readonly (int Streams, int Coupled, byte[] Mapping)[] vorbis_mappings =
    [
        (1, 0, [0]),                      /* 1: mono */
        (1, 1, [0, 1]),                   /* 2: stereo */
        (2, 1, [0, 2, 1]),                /* 3: 1-d surround */
        (2, 2, [0, 1, 2, 3]),             /* 4: quadraphonic surround */
        (3, 2, [0, 4, 1, 2, 3]),          /* 5: 5-channel surround */
        (4, 2, [0, 4, 1, 2, 3, 5]),       /* 6: 5.1 surround */
        (4, 3, [0, 4, 1, 2, 3, 5, 6]),    /* 7: 6.1 surround */
        (5, 3, [0, 6, 1, 2, 3, 4, 5, 7]), /* 8: 7.1 surround */
    ];

    private static readonly int[] diff_table = BuildDiffTable();

    private readonly int nb_channels;
    private readonly int nb_streams;
    private readonly int nb_coupled_streams;
    private readonly byte[] mapping;
    private readonly int lfe_stream;
    private readonly int application;
    private readonly int Fs;
    private readonly OpusEncoderState[] encoders;
    private readonly int[] preemph_mem;
    private readonly int[] window_mem;
    private readonly int[] buf;
    private readonly int[] bandSMR;
    private readonly int[] bitrates;
    private readonly int[] bandLogE = new int[42];
    private readonly byte[] tmp_data = new byte[MS_FRAME_TMP];
    private readonly OpusRepacketizer rp = new();
    private readonly int[] surround_in;
    private readonly int[] surround_x;
    private readonly int[] surround_freq;
    private int bitrate_bps = OpusEncoderState.OPUS_AUTO;

    /// <summary>
    /// opus_multistream_surround_encoder_create(Fs, channels, 1, ...), then opusenc's settings through
    /// opus_multistream_encoder_ctl.
    /// </summary>
    internal OpusMultistreamEncoder(int Fs, int channels, OpusEncoderOptions options, int inputRate)
    {
        var layout = vorbis_mappings[channels - 1];
        this.nb_channels = channels;
        this.nb_streams = layout.Streams;
        this.nb_coupled_streams = layout.Coupled;
        this.mapping = layout.Mapping;
        this.lfe_stream = channels >= 6 ? layout.Streams - 1 : -1;
        this.Fs = Fs;
        this.application = options.Application switch
        {
            OpusApplication.Audio => OpusEncoderState.OPUS_APPLICATION_AUDIO,
            OpusApplication.Voip => OpusEncoderState.OPUS_APPLICATION_VOIP,
            OpusApplication.RestrictedLowDelay => OpusEncoderState.OPUS_APPLICATION_RESTRICTED_LOWDELAY,
            OpusApplication.RestrictedSilk => OpusEncoderState.OPUS_APPLICATION_RESTRICTED_SILK,
            _ => OpusEncoderState.OPUS_APPLICATION_RESTRICTED_CELT,
        };

        this.encoders = new OpusEncoderState[this.nb_streams];
        for (int i = 0; i < this.nb_streams; i++)
        {
            this.encoders[i] = new OpusEncoderState(Fs, i < this.nb_coupled_streams ? 2 : 1, this.application);
            if (i == this.lfe_stream)
            {
                this.encoders[i].SetLFE(1);
            }
        }
        bool surround = channels > 2;
        this.preemph_mem = new int[channels];
        this.window_mem = new int[channels * MAX_OVERLAP];
        int maxFrame = 3 * Fs / 25;
        this.buf = new int[2 * maxFrame];
        this.bandSMR = new int[21 * channels];
        this.bitrates = new int[this.nb_streams];
        int analysisFrame = maxFrame * resampling_factor_or_1(Fs);
        this.surround_in = surround ? new int[analysisFrame + MAX_OVERLAP] : [];
        this.surround_x = surround ? new int[maxFrame] : [];
        this.surround_freq = surround ? new int[analysisFrame] : [];

        // opusenc's settings, as opus_multistream_encoder_ctl applies them
        this.SetBitrate(options.BitrateFor(this.nb_streams, this.nb_coupled_streams, inputRate, channels));
        foreach (OpusEncoderState encoder in this.encoders)
        {
            OpusPacketEncoder.Configure(encoder, options, bitrate: OpusEncoderState.OPUS_AUTO);
        }
    }

    internal int Streams => this.nb_streams;

    internal int CoupledStreams => this.nb_coupled_streams;

    internal ReadOnlySpan<byte> Mapping => this.mapping;

    /// <summary>OPUS_GET_LOOKAHEAD: the first stream's.</summary>
    internal int GetLookahead() => this.encoders[0].GetLookahead();

    /// <summary>OPUS_GET_FINAL_RANGE: the streams' final ranges, XORed.</summary>
    internal uint GetFinalRange()
    {
        uint value = 0;
        foreach (OpusEncoderState encoder in this.encoders)
        {
            value ^= encoder.GetFinalRange();
        }
        return value;
    }

    /// <summary>OPUS_RESET_STATE.</summary>
    internal void ResetState()
    {
        foreach (OpusEncoderState encoder in this.encoders)
        {
            encoder.ResetState();
        }
        Array.Clear(this.preemph_mem);
        Array.Clear(this.window_mem);
    }

    /// <summary>opus_multistream_encode_float.</summary>
    internal int Encode(ReadOnlySpan<float> pcm, int frame_size, Span<byte> data) =>
        this.opus_multistream_encode_native(MemoryMarshal.AsBytes(pcm), isFloat: true, frame_size, data, data.Length,
            MAX_ENCODING_DEPTH, OpusEncoderState.downmix_float_func, 1);

    /// <summary>opus_multistream_encode.</summary>
    internal int Encode(ReadOnlySpan<short> pcm, int frame_size, Span<byte> data) =>
        this.opus_multistream_encode_native(MemoryMarshal.AsBytes(pcm), isFloat: false, frame_size, data, data.Length,
            16, OpusEncoderState.downmix_int_func, 0);

    // OPUS_SET_BITRATE on the multistream encoder: the total, which rate_allocation divides
    private void SetBitrate(int value)
    {
        if (value != OpusEncoderState.OPUS_AUTO && value != OpusEncoderState.OPUS_BITRATE_MAX)
        {
            value = Math.Min(750000 * this.nb_channels, Math.Max(500 * this.nb_channels, value));
        }
        this.bitrate_bps = value;
    }

    private static int resampling_factor_or_1(int rate) => Math.Max(1, Celt.Celt.resampling_factor(rate));

    private static int[] BuildDiffTable()
    {
        // GCONST of the first nine; the rest of C's 17 entries are zero
        double[] values = [0.5000000, 0.2924813, 0.1609640, 0.0849625, 0.0437314, 0.0221971, 0.0111839, 0.0056136, 0.0028123];
        var table = new int[17];
        for (int i = 0; i < values.Length; i++)
        {
            table[i] = GCONST(values[i]);
        }
        return table;
    }

    // GCONST: Q(DB_SHIFT), rounded as C's (celt_glog)(.5 + x*2^DB_SHIFT)
    private static int GCONST(double x) => (int)(.5 + (x * (1L << DB_SHIFT)));

    /* Computes a rough approximation of log2(2^a + 2^b); returned as C's opus_val16 */
    private static int logSum(int a, int b)
    {
        int max;
        int diff;
        int frac;
        int low;
        if (a > b)
        {
            max = a;
            diff = SUB32(a, b);
        }
        else
        {
            max = b;
            diff = SUB32(b, a);
        }
        if (!(diff < GCONST(8.0)))  /* inverted to catch NaNs */
        {
            return unchecked((short)max);
        }
        low = SHR32(diff, DB_SHIFT - 1);
        frac = VSHR32(diff - SHL32(low, DB_SHIFT - 1), DB_SHIFT - 16);
        return unchecked((short)(max + diff_table[low] + MULT16_32_Q15(frac, SUB32(diff_table[low + 1], diff_table[low]))));
    }

    private static void channel_pos(int channels, Span<int> pos)
    {
        /* Position in the mix: 0 don't mix, 1: left, 2: center, 3:right */
        if (channels == 4)
        {
            pos[0] = 1;
            pos[1] = 3;
            pos[2] = 1;
            pos[3] = 3;
        }
        else if (channels == 3 || channels == 5 || channels == 6)
        {
            pos[0] = 1;
            pos[1] = 2;
            pos[2] = 3;
            pos[3] = 1;
            pos[4] = 3;
            pos[5] = 0;
        }
        else if (channels == 7)
        {
            pos[0] = 1;
            pos[1] = 2;
            pos[2] = 3;
            pos[3] = 1;
            pos[4] = 3;
            pos[5] = 2;
            pos[6] = 0;
        }
        else if (channels == 8)
        {
            pos[0] = 1;
            pos[1] = 2;
            pos[2] = 3;
            pos[3] = 1;
            pos[4] = 3;
            pos[5] = 1;
            pos[6] = 3;
            pos[7] = 0;
        }
    }

    // opus_copy_channel_in_float or _short: one input channel to opus_res, at a stride
    private static void copy_channel_in(Span<int> dst, int dst_stride, ReadOnlySpan<byte> src, bool isFloat, int src_stride, int src_channel, int frame_size)
    {
        int i;
        if (isFloat)
        {
            ReadOnlySpan<float> float_src = MemoryMarshal.Cast<byte, float>(src);
            for (i = 0; i < frame_size; i++)
            {
                dst[i * dst_stride] = OpusEncoderState.FLOAT2RES(float_src[(i * src_stride) + src_channel]);
            }
        }
        else
        {
            ReadOnlySpan<short> short_src = MemoryMarshal.Cast<byte, short>(src);
            for (i = 0; i < frame_size; i++)
            {
                dst[i * dst_stride] = INT16TORES(short_src[(i * src_stride) + src_channel]);
            }
        }
    }

    private void surround_analysis(OpusCustomMode celt_mode, ReadOnlySpan<byte> pcm, bool isFloat, Span<int> bandLogE, int len, int overlap, int channels, int rate)
    {
        int c;
        int i;
        int LM;
        Span<int> pos = stackalloc int[8];
        int upsample;
        int frame_size;
        int freq_size;
        int channel_offset;
        Span<int> bandE = stackalloc int[21];
        Span<int> maskLogE = stackalloc int[3 * 21];
        Span<int> tmpE = stackalloc int[21];

        upsample = Celt.Celt.resampling_factor(rate);
        frame_size = len * upsample;

        /* LM = log2(frame_size / 120) */
        for (LM = 0; LM < celt_mode.maxLM; LM++)
        {
            if (celt_mode.shortMdctSize << LM == frame_size)
            {
                break;
            }
        }

        freq_size = celt_mode.shortMdctSize << LM;

        Span<int> @in = this.surround_in.AsSpan(0, frame_size + overlap);
        Span<int> x = this.surround_x.AsSpan(0, len);
        Span<int> freq = this.surround_freq.AsSpan(0, freq_size);

        channel_pos(channels, pos);

        maskLogE.Fill(-GCONST(28.0));

        for (c = 0; c < channels; c++)
        {
            int frame;
            int nb_frames = frame_size / freq_size;
            this.window_mem.AsSpan(c * overlap, overlap).CopyTo(@in);
            copy_channel_in(x, 1, pcm, isFloat, channels, c, len);
            CeltEncoder.celt_preemphasis(x, @in[overlap..], frame_size, 1, upsample, celt_mode.preemph, ref this.preemph_mem[c], 0);
            bandE.Clear();
            for (frame = 0; frame < nb_frames; frame++)
            {
                MdctEnc.clt_mdct_forward(celt_mode.mdct, @in[(freq_size * frame)..], freq, celt_mode.window,
                      overlap, celt_mode.maxLM - LM, 1);
                if (upsample != 1)
                {
                    int bound = freq_size / upsample;
                    for (i = 0; i < bound; i++)
                    {
                        freq[i] *= upsample;
                    }
                    for (; i < freq_size; i++)
                    {
                        freq[i] = 0;
                    }
                }

                BandsEnc.compute_band_energies(celt_mode, freq, tmpE, 21, 1, LM);
                /* If we have multiple frames, take the max energy. */
                for (i = 0; i < 21; i++)
                {
                    bandE[i] = MAX32(bandE[i], tmpE[i]);
                }
            }
            QuantBandsEnc.amp2Log2(celt_mode, 21, 21, bandE, bandLogE.Slice(21 * c, 21), 1);
            /* Apply spreading function with -6 dB/band going up and -12 dB/band going down. */
            for (i = 1; i < 21; i++)
            {
                bandLogE[(21 * c) + i] = MAXG(bandLogE[(21 * c) + i], bandLogE[(21 * c) + i - 1] - GCONST(1.0));
            }
            for (i = 19; i >= 0; i--)
            {
                bandLogE[(21 * c) + i] = MAXG(bandLogE[(21 * c) + i], bandLogE[(21 * c) + i + 1] - GCONST(2.0));
            }
            if (pos[c] == 1)
            {
                for (i = 0; i < 21; i++)
                {
                    maskLogE[i] = logSum(maskLogE[i], bandLogE[(21 * c) + i]);
                }
            }
            else if (pos[c] == 3)
            {
                for (i = 0; i < 21; i++)
                {
                    maskLogE[42 + i] = logSum(maskLogE[42 + i], bandLogE[(21 * c) + i]);
                }
            }
            else if (pos[c] == 2)
            {
                for (i = 0; i < 21; i++)
                {
                    maskLogE[i] = logSum(maskLogE[i], bandLogE[(21 * c) + i] - GCONST(.5));
                    maskLogE[42 + i] = logSum(maskLogE[42 + i], bandLogE[(21 * c) + i] - GCONST(.5));
                }
            }
            @in.Slice(frame_size, overlap).CopyTo(this.window_mem.AsSpan(c * overlap));
        }
        for (i = 0; i < 21; i++)
        {
            maskLogE[21 + i] = MIN32(maskLogE[i], maskLogE[42 + i]);
        }
        channel_offset = HALF16(MathOpsEnc.celt_log2((int)(.5 + (2.0 * (1 << 14))) / (channels - 1)));
        for (c = 0; c < 3; c++)
        {
            for (i = 0; i < 21; i++)
            {
                maskLogE[(21 * c) + i] += channel_offset;
            }
        }
        for (c = 0; c < channels; c++)
        {
            if (pos[c] != 0)
            {
                int mask = 21 * (pos[c] - 1);
                for (i = 0; i < 21; i++)
                {
                    bandLogE[(21 * c) + i] = bandLogE[(21 * c) + i] - maskLogE[mask + i];
                }
            }
            else
            {
                for (i = 0; i < 21; i++)
                {
                    bandLogE[(21 * c) + i] = 0;
                }
            }
        }
    }

    private void surround_rate_allocation(Span<int> rate, int frame_size, int Fs)
    {
        int i;
        int channel_rate;
        int stream_offset;
        int lfe_offset;
        int coupled_ratio; /* Q8 */
        int lfe_ratio;     /* Q8 */
        int nb_lfe;
        int nb_uncoupled;
        int nb_coupled;
        int nb_normal;
        int channel_offset;
        int bitrate;
        int total;

        nb_lfe = (this.lfe_stream != -1) ? 1 : 0;
        nb_coupled = this.nb_coupled_streams;
        nb_uncoupled = this.nb_streams - nb_coupled - nb_lfe;
        nb_normal = (2 * nb_coupled) + nb_uncoupled;

        /* Give each non-LFE channel enough bits per channel for coding band energy. */
        channel_offset = 40 * Math.Max(50, Fs / frame_size);

        if (this.bitrate_bps == OpusEncoderState.OPUS_AUTO)
        {
            bitrate = (nb_normal * (channel_offset + Fs + 10000)) + (8000 * nb_lfe);
        }
        else if (this.bitrate_bps == OpusEncoderState.OPUS_BITRATE_MAX)
        {
            bitrate = (nb_normal * 750000) + (nb_lfe * 128000);
        }
        else
        {
            bitrate = this.bitrate_bps;
        }

        /* Give LFE some basic stream_channel allocation but never exceed 1/20 of the
           total rate for the non-energy part to avoid problems at really low rate. */
        lfe_offset = Math.Min(bitrate / 20, 3000) + (15 * Math.Max(50, Fs / frame_size));

        /* We give each stream (coupled or uncoupled) a starting bitrate.
           This models the main saving of coupled channels over uncoupled. */
        stream_offset = (bitrate - (channel_offset * nb_normal) - (lfe_offset * nb_lfe)) / nb_normal / 2;
        stream_offset = Math.Max(0, Math.Min(20000, stream_offset));

        /* Coupled streams get twice the mono rate after the offset is allocated. */
        coupled_ratio = 512;
        /* Should depend on the bitrate, for now we assume LFE gets 1/8 the bits of mono */
        lfe_ratio = 32;

        total = (nb_uncoupled << 8)         /* mono */
              + (coupled_ratio * nb_coupled) /* stereo */
              + (nb_lfe * lfe_ratio);
        channel_rate = (int)(256 * (long)(bitrate - (lfe_offset * nb_lfe) - (stream_offset * (nb_coupled + nb_uncoupled)) - (channel_offset * nb_normal)) / total);

        for (i = 0; i < this.nb_streams; i++)
        {
            if (i < this.nb_coupled_streams)
            {
                rate[i] = (2 * channel_offset) + Math.Max(0, stream_offset + ((channel_rate * coupled_ratio) >> 8));
            }
            else if (i != this.lfe_stream)
            {
                rate[i] = channel_offset + Math.Max(0, stream_offset + channel_rate);
            }
            else
            {
                rate[i] = Math.Max(0, lfe_offset + ((channel_rate * lfe_ratio) >> 8));
            }
        }
    }

    private int rate_allocation(Span<int> rate, int frame_size)
    {
        int i;
        int rate_sum = 0;

        this.surround_rate_allocation(rate, frame_size, this.encoders[0].GetSampleRate());

        for (i = 0; i < this.nb_streams; i++)
        {
            rate[i] = Math.Max(rate[i], 500);
            rate_sum += rate[i];
        }
        return rate_sum;
    }

    private int get_left_channel(int stream_id, int prev)
    {
        for (int i = (prev < 0) ? 0 : prev + 1; i < this.nb_channels; i++)
        {
            if (this.mapping[i] == stream_id * 2)
            {
                return i;
            }
        }
        return -1;
    }

    private int get_right_channel(int stream_id, int prev)
    {
        for (int i = (prev < 0) ? 0 : prev + 1; i < this.nb_channels; i++)
        {
            if (this.mapping[i] == (stream_id * 2) + 1)
            {
                return i;
            }
        }
        return -1;
    }

    private int get_mono_channel(int stream_id, int prev)
    {
        for (int i = (prev < 0) ? 0 : prev + 1; i < this.nb_channels; i++)
        {
            if (this.mapping[i] == stream_id + this.nb_coupled_streams)
            {
                return i;
            }
        }
        return -1;
    }

    private int opus_multistream_encode_native(ReadOnlySpan<byte> pcm, bool isFloat, int analysis_frame_size,
        Span<byte> data, int max_data_bytes, int lsb_depth, downmix_func downmix, int float_api)
    {
        int Fs;
        int s;
        int tot_size;
        int vbr;
        OpusCustomMode? celt_mode = null;
        int frame_size;
        int rate_sum;
        int smallest_packet;
        bool surround = this.nb_channels > 2;

        Fs = this.encoders[0].GetSampleRate();
        vbr = this.encoders[0].GetVBR();
        if (this.application != OpusEncoderState.OPUS_APPLICATION_RESTRICTED_SILK)
        {
            celt_mode = this.encoders[0].GetMode();
        }

        frame_size = OpusEncoderState.frame_size_select(this.application, analysis_frame_size, OpusEncoderState.OPUS_FRAMESIZE_ARG, Fs);
        if (frame_size <= 0)
        {
            return OPUS_BAD_ARG;
        }

        /* Smallest packet the encoder can produce. */
        smallest_packet = (this.nb_streams * 2) - 1;
        /* 100 ms needs an extra byte per stream for the ToC. */
        if (Fs / frame_size == 10)
        {
            smallest_packet += this.nb_streams;
        }
        if (max_data_bytes < smallest_packet)
        {
            return OPUS_BUFFER_TOO_SMALL;
        }
        Span<int> buf = this.buf.AsSpan(0, 2 * frame_size);

        Span<int> bandSMR = this.bandSMR;
        if (surround && this.application != OpusEncoderState.OPUS_APPLICATION_RESTRICTED_SILK)
        {
            this.surround_analysis(celt_mode!, pcm, isFloat, bandSMR, frame_size, celt_mode!.overlap, this.nb_channels, Fs);
        }

        /* Compute bitrate allocation between streams (this could be a lot better) */
        rate_sum = this.rate_allocation(this.bitrates, frame_size);

        if (vbr == 0)
        {
            if (this.bitrate_bps == OpusEncoderState.OPUS_AUTO)
            {
                max_data_bytes = Math.Min(max_data_bytes, (CeltEnc.bitrate_to_bits(rate_sum, Fs, frame_size) + 4) / 8);
            }
            else if (this.bitrate_bps != OpusEncoderState.OPUS_BITRATE_MAX)
            {
                max_data_bytes = Math.Min(max_data_bytes, Math.Max(smallest_packet,
                                 (CeltEnc.bitrate_to_bits(this.bitrate_bps, Fs, frame_size) + 4) / 8));
            }
        }
        for (s = 0; s < this.nb_streams; s++)
        {
            OpusEncoderState enc = this.encoders[s];
            enc.SetBitrate(this.bitrates[s]);
            if (surround)
            {
                int equiv_rate;
                equiv_rate = this.bitrate_bps;
                if (frame_size * 50 < Fs)
                {
                    equiv_rate -= 60 * ((Fs / frame_size) - 50) * this.nb_channels;
                }
                if (equiv_rate > 10000 * this.nb_channels)
                {
                    enc.SetBandwidth(OPUS_BANDWIDTH_FULLBAND);
                }
                else if (equiv_rate > 7000 * this.nb_channels)
                {
                    enc.SetBandwidth(OPUS_BANDWIDTH_SUPERWIDEBAND);
                }
                else if (equiv_rate > 5000 * this.nb_channels)
                {
                    enc.SetBandwidth(OPUS_BANDWIDTH_WIDEBAND);
                }
                else
                {
                    enc.SetBandwidth(OPUS_BANDWIDTH_NARROWBAND);
                }
                if (s < this.nb_coupled_streams)
                {
                    /* To preserve the spatial image, force stereo CELT on coupled streams */
                    enc.SetForceMode(MODE_CELT_ONLY);
                    enc.SetForceChannels(2);
                }
            }
        }

        /* Counting ToC */
        tot_size = 0;
        for (s = 0; s < this.nb_streams; s++)
        {
            OpusEncoderState enc;
            int len;
            int curr_max;
            int c1, c2;
            int ret;

            Repacketizer.opus_repacketizer_init(this.rp);
            enc = this.encoders[s];
            if (s < this.nb_coupled_streams)
            {
                int i;
                int left, right;
                left = this.get_left_channel(s, -1);
                right = this.get_right_channel(s, -1);
                copy_channel_in(buf, 2, pcm, isFloat, this.nb_channels, left, frame_size);
                copy_channel_in(buf[1..], 2, pcm, isFloat, this.nb_channels, right, frame_size);
                if (surround && this.application != OpusEncoderState.OPUS_APPLICATION_RESTRICTED_SILK)
                {
                    for (i = 0; i < 21; i++)
                    {
                        this.bandLogE[i] = bandSMR[(21 * left) + i];
                        this.bandLogE[21 + i] = bandSMR[(21 * right) + i];
                    }
                }
                c1 = left;
                c2 = right;
            }
            else
            {
                int i;
                int chan = this.get_mono_channel(s, -1);
                copy_channel_in(buf, 1, pcm, isFloat, this.nb_channels, chan, frame_size);
                if (surround && this.application != OpusEncoderState.OPUS_APPLICATION_RESTRICTED_SILK)
                {
                    for (i = 0; i < 21; i++)
                    {
                        this.bandLogE[i] = bandSMR[(21 * chan) + i];
                    }
                }
                c1 = chan;
                c2 = -1;
            }
            if (surround && this.application != OpusEncoderState.OPUS_APPLICATION_RESTRICTED_SILK)
            {
                enc.SetEnergyMask(this.bandLogE);
            }
            /* number of bytes left (+Toc) */
            curr_max = max_data_bytes - tot_size;
            /* Reserve one byte for the last stream and two for the others */
            curr_max -= Math.Max(0, (2 * (this.nb_streams - s - 1)) - 1);
            /* For 100 ms, reserve an extra byte per stream for the ToC */
            if (Fs / frame_size == 10)
            {
                curr_max -= this.nb_streams - s - 1;
            }
            curr_max = Math.Min(curr_max, MS_FRAME_TMP);
            /* Repacketizer will add one or two bytes for self-delimited frames */
            if (s != this.nb_streams - 1)
            {
                curr_max -= curr_max > 253 ? 2 : 1;
            }
            if (vbr == 0 && s == this.nb_streams - 1)
            {
                enc.SetBitrate(CeltEnc.bits_to_bitrate(curr_max * 8, Fs, frame_size));
            }
            len = enc.opus_encode_native(buf, frame_size, this.tmp_data, curr_max, lsb_depth,
                  pcm, analysis_frame_size, c1, c2, this.nb_channels, downmix, float_api);
            if (len < 0)
            {
                return len;
            }
            /* We need to use the repacketizer to add the self-delimiting lengths
               while taking into account the fact that the encoder can now return
               more than one frame at a time (e.g. 60 ms CELT-only) */
            ret = Repacketizer.opus_repacketizer_cat(this.rp, this.tmp_data, len);
            /* If the opus_repacketizer_cat() fails, then something's seriously wrong
               with the encoder. */
            if (ret != OPUS_OK)
            {
                return OPUS_INTERNAL_ERROR;
            }
            len = Repacketizer.opus_repacketizer_out_range_impl(this.rp, 0, Repacketizer.opus_repacketizer_get_nb_frames(this.rp),
                  data, max_data_bytes - tot_size, s != this.nb_streams - 1 ? 1 : 0, vbr == 0 && s == this.nb_streams - 1 ? 1 : 0, default, 0);
            data = data[len..];
            tot_size += len;
        }
        return tot_size;
    }
}
