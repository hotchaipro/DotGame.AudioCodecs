/* Copyright (c) 2010-2011 Xiph.Org Foundation, Skype Limited
   Written by Jean-Marc Valin and Koen Vos
   Ported to C# from libopus 1.6.1 (src/opus_encoder.c, and celt/float_cast.h's conversions) by Hot Chai Games LLC */
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

// PORT NOTE: float2int is C's lrintf on x86-64 and vcvtns_s32_f32 on aarch64 (the Mac reference):
// both round to nearest even; the port uses (int)MathF.Round(x), which also saturates out of range
// and gives 0 for NaN as vcvtns does (.NET 9+ conversions saturate). Every caller clamps first
// except the analysis's float2int(d2_angle), whose argument is a small phase difference.
// PORT NOTE: the ctl requests are methods: setters throw ArgumentOutOfRangeException (or
// ArgumentException) where C returns OPUS_BAD_ARG, as the decoder's port does; getters return the
// value. OPUS_SET_LFE and OPUS_SET_ENERGY_MASK return nothing (C returns the CELT ctl's result,
// always OPUS_OK). opus_encoder_init's errors are thrown by the constructor.
// PORT NOTE: AnalysisInfo analysis_info in opus_encode_native is zeroed (default), where C only
// sets valid = 0 and leaves the rest uninitialized; it reaches CELT_SET_ANALYSIS unchanged when
// the analysis doesn't run (complexity < 10 or Fs < 16 kHz). CELT should only read it when valid.
// PORT NOTE: the analysis's input (C's const void *analysis_pcm) is the caller's samples as bytes
// (MemoryMarshal.AsBytes), and downmix_func is a delegate over them; downmix_float and downmix_int24
// are static readonly delegates, so encoding allocates nothing.
// PORT NOTE: C's per-call ALLOCs that are large (opus_encode_float/opus_encode24's opus_res
// conversion, opus_encode_frame_native's pcm_buf, the multiframe tmp_data and OpusRepacketizer)
// are buffers the encoder makes in its constructor. tmp_data is 1276*6+256 bytes rather than C's
// max_len_sum (which grows with the caller's buffer): the frames only ever use about
// max_data_bytes (at most 1276*6) of it. max_len_sum itself is computed as C computes it.
// PORT NOTE: bits_to_bitrate and bitrate_to_bits (celt.h's static inlines) are private here, as
// are opus_encoder.c's own constants from silk/define.h and silk/tuning_parameters.h
// (NB_SPEECH_FRAMES_BEFORE_DTX, MAX_CONSECUTIVE_DTX, DTX_ACTIVITY_THRESHOLD, VAD_NO_DECISION,
// VARIABLE_HP_MIN_CUTOFF_HZ, VARIABLE_HP_SMTH_COEF2), so they can't clash with the SILK port's.
// silk_lin2log (silk/lin2log.c) is the SILK part's Lin2log.silk_lin2log.
// PORT NOTE: opus_encode (int16 input), opus_encoder_get_size, opus_encoder_create/destroy (the
// constructor and the GC), the DRED, QEXT, FUZZING, OSCE-training and float-build branches, and
// OPUS_SET_DNN_BLOB (USE_WEIGHTS_FILE) aren't ported. downmix_int is, for completeness.
// PORT NOTE: the arch fields and parameters are dropped (no RTCD).

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using DotGame.AudioCodecs.Opus.Celt;
using DotGame.AudioCodecs.Opus.Common;
using DotGame.AudioCodecs.Opus.Silk;
using static DotGame.AudioCodecs.Opus.Celt.Arch;
using static DotGame.AudioCodecs.Opus.OpusDefines;
using static DotGame.AudioCodecs.Opus.Silk.SigProcFix;

namespace DotGame.AudioCodecs.Opus;

/// <summary>
/// opus_private.h's downmix_func: mixes the caller's samples (C's const void *, as bytes here)
/// down to one channel of celt_sig for the analysis.
/// </summary>
internal delegate void downmix_func(ReadOnlySpan<byte> _x, Span<int> y, int subframe, int offset, int c1, int c2, int C);

/// <summary>opus_encoder.c's StereoWidthState.</summary>
internal struct StereoWidthState
{
    internal int XX, XY, YY;
    internal short smoothed_width;
    internal short max_follower;
}

/// <summary>
/// An Opus encoder: libopus's OpusEncoderState (fixed point, 24-bit internal resolution, with the float
/// tonality analysis), which codes each frame with SILK, CELT or both. It allocates nothing once
/// made.
/// </summary>
internal sealed class OpusEncoderState
{
    /* opus_defines.h */
    internal const int OPUS_AUTO = -1000;
    internal const int OPUS_BITRATE_MAX = -1;
    internal const int OPUS_APPLICATION_VOIP = 2048;
    internal const int OPUS_APPLICATION_AUDIO = 2049;
    internal const int OPUS_APPLICATION_RESTRICTED_LOWDELAY = 2051;
    internal const int OPUS_APPLICATION_RESTRICTED_SILK = 2052;
    internal const int OPUS_APPLICATION_RESTRICTED_CELT = 2053;
    internal const int OPUS_SIGNAL_VOICE = 3001;
    internal const int OPUS_SIGNAL_MUSIC = 3002;
    internal const int OPUS_FRAMESIZE_ARG = 5000;
    internal const int OPUS_FRAMESIZE_2_5_MS = 5001;
    internal const int OPUS_FRAMESIZE_5_MS = 5002;
    internal const int OPUS_FRAMESIZE_10_MS = 5003;
    internal const int OPUS_FRAMESIZE_20_MS = 5004;
    internal const int OPUS_FRAMESIZE_40_MS = 5005;
    internal const int OPUS_FRAMESIZE_60_MS = 5006;
    internal const int OPUS_FRAMESIZE_80_MS = 5007;
    internal const int OPUS_FRAMESIZE_100_MS = 5008;
    internal const int OPUS_FRAMESIZE_120_MS = 5009;

    private const int MAX_ENCODER_BUFFER = 480;

    private const float PSEUDO_SNR_THRESHOLD = 316.23f;    /* 10^(25/10) */

    /* silk/define.h */
    private const int NB_SPEECH_FRAMES_BEFORE_DTX = 10;     /* eq 200 ms */
    private const int MAX_CONSECUTIVE_DTX = 20;             /* eq 400 ms */
    private const float DTX_ACTIVITY_THRESHOLD = 0.1f;
    private const int VAD_NO_DECISION = -1;

    /* silk/tuning_parameters.h */
    private const float VARIABLE_HP_SMTH_COEF2 = 0.015f;
    private const int VARIABLE_HP_MIN_CUTOFF_HZ = 60;

    // the multiframe tmp_data (see the PORT NOTE)
    private const int MAX_TMP_DATA = (1276 * 6) + 256;

    private readonly OpusCustomEncoder? celt_enc;
    private readonly silk_encoder? silk_enc;
    private readonly silk_EncControlStruct silk_mode = new silk_EncControlStruct();
    private int application;
    private readonly int channels;
    private readonly int delay_compensation;
    private int force_channels;
    private int signal_type;
    private int user_bandwidth;
    private int max_bandwidth;
    private int user_forced_mode;
    private int voice_ratio;
    private readonly int Fs;
    private int use_vbr;
    private int vbr_constraint;
    private int variable_duration;
    private int bitrate_bps;
    private int user_bitrate_bps;
    private int lsb_depth;
    private readonly int encoder_buffer;
    private int lfe;
    private int use_dtx;                 /* general DTX for both SILK and CELT */
    private int fec_config;
    private readonly TonalityAnalysisState analysis = new TonalityAnalysisState();

    /* Everything from here on is cleared by OPUS_RESET_STATE (OPUS_ENCODER_RESET_START) */
    private int stream_channels;
    private short hybrid_stereo_width_Q14;
    private int variable_HP_smth2_Q15;
    private short prev_HB_gain;
    private readonly int[] hp_mem = new int[4];
    private int mode;
    private int prev_mode;
    private int prev_channels;
    private int prev_framesize;
    private int bandwidth;
    /* Bandwidth determined automatically from the rate (before any other adjustment) */
    private int auto_bandwidth;
    private int silk_bw_switch;
    /* Sampling rate (at the API level) */
    private int first;
    private int[]? energy_masking;
    private StereoWidthState width_mem;
    private int detected_bandwidth;
    private int nb_no_activity_ms_Q1;
    private int peak_signal_energy;
    private int nonfinal_frame; /* current frame is not the final in a packet */
    private uint rangeFinal;
    private readonly int[] delay_buffer = new int[MAX_ENCODER_BUFFER * 2];

    // Scratch C allocates per call (see the PORT NOTE)
    private readonly int[] in_buf;           // opus_encode_float's in[]
    private readonly int[] pcm_buf_mem;      // opus_encode_frame_native's pcm_buf[]
    private readonly byte[] tmp_data = new byte[MAX_TMP_DATA];
    private readonly OpusRepacketizer rp = new OpusRepacketizer();
    private readonly OpusRepacketizer pad_rp = new OpusRepacketizer();
    private readonly silk_EncControlStruct dummy = new silk_EncControlStruct();
    // opus_encode_frame_native's tmp_prefill[] and dummy[2]: arrays rather than stackalloc,
    // because celt_encode_with_ec's ref ec_enc could (to the compiler) capture a stack span
    private readonly int[] tmp_prefill_mem;
    private readonly byte[] dummy2 = new byte[2];

    internal static readonly downmix_func downmix_float_func = downmix_float;
    internal static readonly downmix_func downmix_int_func = downmix_int;
    private static readonly downmix_func downmix_int24_func = downmix_int24;

    /* Transition tables for the voice and music. First column is the
       middle (memoriless) threshold. The second column is the hysteresis
       (difference with the middle) */
    private static readonly int[] mono_voice_bandwidth_thresholds =
    [
        9000, 700, /* NB<->MB */
        9000, 700, /* MB<->WB */
        13500, 1000, /* WB<->SWB */
        14000, 2000, /* SWB<->FB */
    ];

    private static readonly int[] mono_music_bandwidth_thresholds =
    [
        9000, 700, /* NB<->MB */
        9000, 700, /* MB<->WB */
        11000, 1000, /* WB<->SWB */
        12000, 2000, /* SWB<->FB */
    ];

    private static readonly int[] stereo_voice_bandwidth_thresholds =
    [
        9000, 700, /* NB<->MB */
        9000, 700, /* MB<->WB */
        13500, 1000, /* WB<->SWB */
        14000, 2000, /* SWB<->FB */
    ];

    private static readonly int[] stereo_music_bandwidth_thresholds =
    [
        9000, 700, /* NB<->MB */
        9000, 700, /* MB<->WB */
        11000, 1000, /* WB<->SWB */
        12000, 2000, /* SWB<->FB */
    ];

    /* Threshold bit-rates for switching between mono and stereo */
    private const int stereo_voice_threshold = 19000;
    private const int stereo_music_threshold = 17000;

    /* Threshold bit-rate for switching between SILK/hybrid and CELT-only */
    private static readonly int[] mode_thresholds =
    [
        /* voice */ /* music */
        64000, 10000, /* mono */
        44000, 10000, /* stereo */
    ];

    private static readonly int[] fec_thresholds =
    [
        12000, 1000, /* NB */
        14000, 1000, /* MB */
        16000, 1000, /* WB */
        20000, 1000, /* SWB */
        22000, 1000, /* FB */
    ];

    private static readonly int[] rate_table =
    [
        /*  |total| |-------- SILK------------|
                    |-- No FEC -| |--- FEC ---|
                     10ms   20ms   10ms   20ms */
        0, 0, 0, 0, 0,
        12000, 10000, 10000, 11000, 11000,
        16000, 13500, 13500, 15000, 15000,
        20000, 16000, 16000, 18000, 18000,
        24000, 18000, 18000, 21000, 21000,
        32000, 22000, 22000, 28000, 28000,
        64000, 38000, 38000, 50000, 50000,
    ];

    /// <summary>
    /// opus_encoder_create/opus_encoder_init: an encoder at <paramref name="Fs"/> (8, 12, 16, 24 or
    /// 48 kHz) for one or two channels and an OPUS_APPLICATION_* mode.
    /// </summary>
    /// <exception cref="ArgumentException">C's OPUS_BAD_ARG.</exception>
    /// <exception cref="InvalidOperationException">C's OPUS_INTERNAL_ERROR.</exception>
    internal OpusEncoderState(int Fs, int channels, int application)
    {
        int ret = 0;

        if ((Fs != 48000 && Fs != 24000 && Fs != 16000 && Fs != 12000 && Fs != 8000)
            || (channels != 1 && channels != 2)
            || (application != OPUS_APPLICATION_VOIP && application != OPUS_APPLICATION_AUDIO
            && application != OPUS_APPLICATION_RESTRICTED_LOWDELAY
            && application != OPUS_APPLICATION_RESTRICTED_SILK
            && application != OPUS_APPLICATION_RESTRICTED_CELT))
        {
            throw new ArgumentException("Opus encodes at 8, 12, 16, 24 or 48 kHz, one or two channels, for a known application.");
        }

        this.stream_channels = this.channels = channels;

        this.Fs = Fs;

        if (application != OPUS_APPLICATION_RESTRICTED_CELT)
        {
            this.silk_enc = new silk_encoder();
            ret = EncAPI.silk_InitEncoder(this.silk_enc, this.channels, this.silk_mode);
        }
        if (ret != 0)
        {
            throw new InvalidOperationException("The SILK encoder didn't initialise.");
        }

        /* default SILK parameters */
        this.silk_mode.nChannelsAPI = channels;
        this.silk_mode.nChannelsInternal = channels;
        this.silk_mode.API_sampleRate = this.Fs;
        this.silk_mode.maxInternalSampleRate = 16000;
        this.silk_mode.minInternalSampleRate = 8000;
        this.silk_mode.desiredInternalSampleRate = 16000;
        this.silk_mode.payloadSize_ms = 20;
        this.silk_mode.bitRate = 25000;
        this.silk_mode.packetLossPercentage = 0;
        this.silk_mode.complexity = 9;
        this.silk_mode.useInBandFEC = 0;
        this.silk_mode.useDRED = 0;
        this.silk_mode.useDTX = 0;
        this.silk_mode.useCBR = 0;
        this.silk_mode.reducedDependency = 0;

        /* Create CELT encoder */
        /* Initialize CELT encoder */
        if (application != OPUS_APPLICATION_RESTRICTED_SILK)
        {
            this.celt_enc = new OpusCustomEncoder(Fs, channels);
            this.celt_enc.SetSignalling(0);
            this.celt_enc.SetComplexity(this.silk_mode.complexity);
        }

        this.use_vbr = 1;
        /* Makes constrained VBR the default (safer for real-time use) */
        this.vbr_constraint = 1;
        this.user_bitrate_bps = OPUS_AUTO;
        this.bitrate_bps = 3000 + (Fs * channels);
        this.application = application;
        this.signal_type = OPUS_AUTO;
        this.user_bandwidth = OPUS_AUTO;
        this.max_bandwidth = OPUS_BANDWIDTH_FULLBAND;
        this.force_channels = OPUS_AUTO;
        this.user_forced_mode = OPUS_AUTO;
        this.voice_ratio = -1;
        if (application != OPUS_APPLICATION_RESTRICTED_CELT && application != OPUS_APPLICATION_RESTRICTED_SILK)
        {
            this.encoder_buffer = this.Fs / 100;
        }
        else
        {
            this.encoder_buffer = 0;
        }
        this.lsb_depth = 24;
        this.variable_duration = OPUS_FRAMESIZE_ARG;

        /* Delay compensation of 4 ms (2.5 ms for SILK's extra look-ahead
           + 1.5 ms for SILK resamplers and stereo prediction) */
        this.delay_compensation = this.Fs / 250;

        this.hybrid_stereo_width_Q14 = 1 << 14;
        this.prev_HB_gain = Q15ONE;
        this.variable_HP_smth2_Q15 = silk_LSHIFT(Lin2log.silk_lin2log(VARIABLE_HP_MIN_CUTOFF_HZ), 8);
        this.first = 1;
        this.mode = MODE_HYBRID;
        this.bandwidth = OPUS_BANDWIDTH_FULLBAND;

        Analysis.tonality_analysis_init(this.analysis, this.Fs);
        this.analysis.application = this.application;

        // 120 ms of each channel; and opus_encode_frame_native's delay compensation plus 60 ms
        this.in_buf = new int[3 * Fs / 25 * channels];
        this.pcm_buf_mem = new int[((Fs / 250) + (3 * Fs / 50)) * channels];
        this.tmp_prefill_mem = new int[channels * Fs / 400];
    }

    // celt.h
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int bits_to_bitrate(int bits, int Fs, int frame_size)
    {
        return bits * (6 * Fs / frame_size) / 6;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int bitrate_to_bits(int bitrate, int Fs, int frame_size)
    {
        return bitrate * 6 / (6 * Fs / frame_size);
    }

    // celt/float_cast.h

    /// <summary>float2int: lrintf (round to nearest even); see the PORT NOTE.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int float2int(float x) => (int)MathF.Round(x);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int FLOAT2INT24(float x)
    {
        x = x * (32768.0f * 256.0f); /* CELT_SIG_SCALE*256.f */
        x = x > -16777216 ? x : -16777216; /* MAX32(x, -16777216) */
        x = x < 16777216 ? x : 16777216;   /* MIN32(x, 16777216) */
        return float2int(x);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int FLOAT2SIG(float x)
    {
        x = x * (32768 << SIG_SHIFT);
        x = x > -(65536 << SIG_SHIFT) ? x : -(65536 << SIG_SHIFT); /* MAX32(x, -(65536<<SIG_SHIFT)) */
        x = x < (65536 << SIG_SHIFT) ? x : (65536 << SIG_SHIFT);   /* MIN32(x, 65536<<SIG_SHIFT) */
        return float2int(x);
    }

    /* FLOAT2RES with RES24 */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int FLOAT2RES(float a) => FLOAT2INT24(a);

    // opus_encoder_ctl's requests

    /// <summary>OPUS_SET_APPLICATION.</summary>
    internal void SetApplication(int value)
    {
        if (this.application == OPUS_APPLICATION_RESTRICTED_SILK || this.application == OPUS_APPLICATION_RESTRICTED_CELT)
        {
            throw new ArgumentException("A restricted encoder's application can't change.", nameof(value));
        }
        if ((value != OPUS_APPLICATION_VOIP && value != OPUS_APPLICATION_AUDIO
             && value != OPUS_APPLICATION_RESTRICTED_LOWDELAY)
            || (this.first == 0 && this.application != value))
        {
            throw new ArgumentException("The application is unknown, or can't change after the first frame.", nameof(value));
        }
        this.application = value;
        this.analysis.application = value;
    }

    /// <summary>OPUS_GET_APPLICATION.</summary>
    internal int GetApplication() => this.application;

    /// <summary>OPUS_SET_BITRATE: bits per second, OPUS_AUTO or OPUS_BITRATE_MAX.</summary>
    internal void SetBitrate(int value)
    {
        if (value != OPUS_AUTO && value != OPUS_BITRATE_MAX)
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            else if (value <= 500)
            {
                value = 500;
            }
            else if (value > 750000 * this.channels)
            {
                value = 750000 * this.channels;
            }
        }
        this.user_bitrate_bps = value;
    }

    /// <summary>OPUS_GET_BITRATE.</summary>
    internal int GetBitrate() => this.user_bitrate_to_bitrate(this.prev_framesize, 1276);

    /// <summary>OPUS_SET_FORCE_CHANNELS: 1, 2 (no more than the encoder's) or OPUS_AUTO.</summary>
    internal void SetForceChannels(int value)
    {
        if ((value < 1 || value > this.channels) && value != OPUS_AUTO)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        this.force_channels = value;
    }

    /// <summary>OPUS_GET_FORCE_CHANNELS.</summary>
    internal int GetForceChannels() => this.force_channels;

    /// <summary>OPUS_SET_MAX_BANDWIDTH.</summary>
    internal void SetMaxBandwidth(int value)
    {
        if (value < OPUS_BANDWIDTH_NARROWBAND || value > OPUS_BANDWIDTH_FULLBAND)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        this.max_bandwidth = value;
        if (this.max_bandwidth == OPUS_BANDWIDTH_NARROWBAND)
        {
            this.silk_mode.maxInternalSampleRate = 8000;
        }
        else if (this.max_bandwidth == OPUS_BANDWIDTH_MEDIUMBAND)
        {
            this.silk_mode.maxInternalSampleRate = 12000;
        }
        else
        {
            this.silk_mode.maxInternalSampleRate = 16000;
        }
    }

    /// <summary>OPUS_GET_MAX_BANDWIDTH.</summary>
    internal int GetMaxBandwidth() => this.max_bandwidth;

    /// <summary>OPUS_SET_BANDWIDTH: a bandwidth or OPUS_AUTO.</summary>
    internal void SetBandwidth(int value)
    {
        if ((value < OPUS_BANDWIDTH_NARROWBAND || value > OPUS_BANDWIDTH_FULLBAND) && value != OPUS_AUTO)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        this.user_bandwidth = value;
        if (this.user_bandwidth == OPUS_BANDWIDTH_NARROWBAND)
        {
            this.silk_mode.maxInternalSampleRate = 8000;
        }
        else if (this.user_bandwidth == OPUS_BANDWIDTH_MEDIUMBAND)
        {
            this.silk_mode.maxInternalSampleRate = 12000;
        }
        else
        {
            this.silk_mode.maxInternalSampleRate = 16000;
        }
    }

    /// <summary>OPUS_GET_BANDWIDTH.</summary>
    internal int GetBandwidth() => this.bandwidth;

    /// <summary>OPUS_SET_DTX: 0 or 1.</summary>
    internal void SetDTX(int value)
    {
        if (value < 0 || value > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        this.use_dtx = value;
    }

    /// <summary>OPUS_GET_DTX.</summary>
    internal int GetDTX() => this.use_dtx;

    /// <summary>OPUS_SET_COMPLEXITY: 0 to 10.</summary>
    internal void SetComplexity(int value)
    {
        if (value < 0 || value > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        this.silk_mode.complexity = value;
        if (this.application != OPUS_APPLICATION_RESTRICTED_SILK)
        {
            this.celt_enc!.SetComplexity(value);
        }
    }

    /// <summary>OPUS_GET_COMPLEXITY.</summary>
    internal int GetComplexity() => this.silk_mode.complexity;

    /// <summary>OPUS_SET_INBAND_FEC: 0, 1 or 2.</summary>
    internal void SetInbandFEC(int value)
    {
        if (value < 0 || value > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        this.fec_config = value;
        this.silk_mode.useInBandFEC = value != 0 ? 1 : 0;
    }

    /// <summary>OPUS_GET_INBAND_FEC.</summary>
    internal int GetInbandFEC() => this.fec_config;

    /// <summary>OPUS_SET_PACKET_LOSS_PERC: 0 to 100.</summary>
    internal void SetPacketLossPerc(int value)
    {
        if (value < 0 || value > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        this.silk_mode.packetLossPercentage = value;
        if (this.application != OPUS_APPLICATION_RESTRICTED_SILK)
        {
            this.celt_enc!.SetPacketLossPerc(value);
        }
    }

    /// <summary>OPUS_GET_PACKET_LOSS_PERC.</summary>
    internal int GetPacketLossPerc() => this.silk_mode.packetLossPercentage;

    /// <summary>OPUS_SET_VBR: 0 or 1.</summary>
    internal void SetVBR(int value)
    {
        if (value < 0 || value > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        this.use_vbr = value;
        this.silk_mode.useCBR = 1 - value;
    }

    /// <summary>OPUS_GET_VBR.</summary>
    internal int GetVBR() => this.use_vbr;

    /// <summary>OPUS_SET_VOICE_RATIO: -1 to 100.</summary>
    internal void SetVoiceRatio(int value)
    {
        if (value < -1 || value > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        this.voice_ratio = value;
    }

    /// <summary>OPUS_GET_VOICE_RATIO.</summary>
    internal int GetVoiceRatio() => this.voice_ratio;

    /// <summary>OPUS_SET_VBR_CONSTRAINT: 0 or 1.</summary>
    internal void SetVBRConstraint(int value)
    {
        if (value < 0 || value > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        this.vbr_constraint = value;
    }

    /// <summary>OPUS_GET_VBR_CONSTRAINT.</summary>
    internal int GetVBRConstraint() => this.vbr_constraint;

    /// <summary>OPUS_SET_SIGNAL: OPUS_AUTO, OPUS_SIGNAL_VOICE or OPUS_SIGNAL_MUSIC.</summary>
    internal void SetSignal(int value)
    {
        if (value != OPUS_AUTO && value != OPUS_SIGNAL_VOICE && value != OPUS_SIGNAL_MUSIC)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        this.signal_type = value;
    }

    /// <summary>OPUS_GET_SIGNAL.</summary>
    internal int GetSignal() => this.signal_type;

    /// <summary>OPUS_GET_LOOKAHEAD.</summary>
    internal int GetLookahead()
    {
        int value = this.Fs / 400;
        if (this.application != OPUS_APPLICATION_RESTRICTED_LOWDELAY && this.application != OPUS_APPLICATION_RESTRICTED_CELT)
        {
            value += this.delay_compensation;
        }
        return value;
    }

    /// <summary>OPUS_GET_SAMPLE_RATE.</summary>
    internal int GetSampleRate() => this.Fs;

    /// <summary>OPUS_GET_FINAL_RANGE: the range coder's final state, for testing.</summary>
    internal uint GetFinalRange() => this.rangeFinal;

    /// <summary>OPUS_SET_LSB_DEPTH: 8 to 24.</summary>
    internal void SetLsbDepth(int value)
    {
        if (value < 8 || value > 24)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        this.lsb_depth = value;
    }

    /// <summary>OPUS_GET_LSB_DEPTH.</summary>
    internal int GetLsbDepth() => this.lsb_depth;

    /// <summary>OPUS_SET_EXPERT_FRAME_DURATION: an OPUS_FRAMESIZE_* value.</summary>
    internal void SetExpertFrameDuration(int value)
    {
        if (value != OPUS_FRAMESIZE_ARG && value != OPUS_FRAMESIZE_2_5_MS &&
            value != OPUS_FRAMESIZE_5_MS && value != OPUS_FRAMESIZE_10_MS &&
            value != OPUS_FRAMESIZE_20_MS && value != OPUS_FRAMESIZE_40_MS &&
            value != OPUS_FRAMESIZE_60_MS && value != OPUS_FRAMESIZE_80_MS &&
            value != OPUS_FRAMESIZE_100_MS && value != OPUS_FRAMESIZE_120_MS)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        this.variable_duration = value;
    }

    /// <summary>OPUS_GET_EXPERT_FRAME_DURATION.</summary>
    internal int GetExpertFrameDuration() => this.variable_duration;

    /// <summary>OPUS_SET_PREDICTION_DISABLED: 0 or 1.</summary>
    internal void SetPredictionDisabled(int value)
    {
        if (value > 1 || value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        this.silk_mode.reducedDependency = value;
    }

    /// <summary>OPUS_GET_PREDICTION_DISABLED.</summary>
    internal int GetPredictionDisabled() => this.silk_mode.reducedDependency;

    /// <summary>OPUS_SET_PHASE_INVERSION_DISABLED: 0 or 1.</summary>
    internal void SetPhaseInversionDisabled(int value)
    {
        if (value < 0 || value > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        if (this.application != OPUS_APPLICATION_RESTRICTED_SILK)
        {
            this.celt_enc!.SetPhaseInversionDisabled(value);
        }
    }

    /// <summary>OPUS_GET_PHASE_INVERSION_DISABLED.</summary>
    internal int GetPhaseInversionDisabled()
    {
        if (this.application != OPUS_APPLICATION_RESTRICTED_SILK)
        {
            return this.celt_enc!.GetPhaseInversionDisabled();
        }
        else
        {
            return 0;
        }
    }

    /// <summary>OPUS_RESET_STATE: as a new encoder (keeping the settings).</summary>
    internal void ResetState()
    {
        Analysis.tonality_analysis_reset(this.analysis);

        /* OPUS_CLEAR from OPUS_ENCODER_RESET_START */
        this.stream_channels = 0;
        this.hybrid_stereo_width_Q14 = 0;
        this.variable_HP_smth2_Q15 = 0;
        this.prev_HB_gain = 0;
        Array.Clear(this.hp_mem);
        this.mode = 0;
        this.prev_mode = 0;
        this.prev_channels = 0;
        this.prev_framesize = 0;
        this.bandwidth = 0;
        this.auto_bandwidth = 0;
        this.silk_bw_switch = 0;
        this.first = 0;
        this.energy_masking = null;
        this.width_mem = default;
        this.detected_bandwidth = 0;
        this.nb_no_activity_ms_Q1 = 0;
        this.peak_signal_energy = 0;
        this.nonfinal_frame = 0;
        this.rangeFinal = 0;
        Array.Clear(this.delay_buffer);

        if (this.application != OPUS_APPLICATION_RESTRICTED_SILK)
        {
            this.celt_enc!.ResetState();
        }
        if (this.application != OPUS_APPLICATION_RESTRICTED_CELT)
        {
            EncAPI.silk_InitEncoder(this.silk_enc!, this.channels, this.dummy);
        }
        this.stream_channels = this.channels;
        this.hybrid_stereo_width_Q14 = 1 << 14;
        this.prev_HB_gain = Q15ONE;
        this.first = 1;
        this.mode = MODE_HYBRID;
        this.bandwidth = OPUS_BANDWIDTH_FULLBAND;
        this.variable_HP_smth2_Q15 = silk_LSHIFT(Lin2log.silk_lin2log(VARIABLE_HP_MIN_CUTOFF_HZ), 8);
    }

    /// <summary>OPUS_SET_FORCE_MODE: MODE_SILK_ONLY, MODE_HYBRID, MODE_CELT_ONLY or OPUS_AUTO.</summary>
    internal void SetForceMode(int value)
    {
        if ((value < MODE_SILK_ONLY || value > MODE_CELT_ONLY) && value != OPUS_AUTO)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        this.user_forced_mode = value;
    }

    /// <summary>OPUS_SET_LFE.</summary>
    internal void SetLFE(int value)
    {
        this.lfe = value;
        if (this.application != OPUS_APPLICATION_RESTRICTED_SILK)
        {
            this.celt_enc!.SetLFE(value);
        }
    }

    /// <summary>OPUS_SET_ENERGY_MASK: C keeps the pointer (null = NULL).</summary>
    internal void SetEnergyMask(int[]? value)
    {
        this.energy_masking = value;
        if (this.application != OPUS_APPLICATION_RESTRICTED_SILK)
        {
            this.celt_enc!.SetEnergyMask(value);
        }
    }

    /// <summary>OPUS_GET_IN_DTX.</summary>
    internal int GetInDTX()
    {
        int value;
        if (this.silk_mode.useDTX != 0 && (this.prev_mode == MODE_SILK_ONLY || this.prev_mode == MODE_HYBRID))
        {
            /* DTX determined by Silk. */
            silk_encoder silk_enc = this.silk_enc!;
            value = silk_enc.state_Fxx[0].sCmn.noSpeechCounter >= NB_SPEECH_FRAMES_BEFORE_DTX ? 1 : 0;
            /* Stereo: check second channel unless only the middle channel was encoded. */
            if (value == 1 && this.silk_mode.nChannelsInternal == 2 && silk_enc.prev_decode_only_middle == 0)
            {
                value = silk_enc.state_Fxx[1].sCmn.noSpeechCounter >= NB_SPEECH_FRAMES_BEFORE_DTX ? 1 : 0;
            }
        }
        else if (this.use_dtx != 0)
        {
            /* DTX determined by Opus. */
            value = this.nb_no_activity_ms_Q1 >= NB_SPEECH_FRAMES_BEFORE_DTX * 20 * 2 ? 1 : 0;
        }
        else
        {
            value = 0;
        }
        return value;
    }

    /// <summary>CELT_GET_MODE.</summary>
    internal OpusCustomMode GetMode() => this.celt_enc!.GetMode();

    private static byte gen_toc(int mode, int framerate, int bandwidth, int channels)
    {
        int period;
        int toc;
        period = 0;
        while (framerate < 400)
        {
            framerate <<= 1;
            period++;
        }
        if (mode == MODE_SILK_ONLY)
        {
            toc = (bandwidth - OPUS_BANDWIDTH_NARROWBAND) << 5;
            toc |= (period - 2) << 3;
        }
        else if (mode == MODE_CELT_ONLY)
        {
            int tmp = bandwidth - OPUS_BANDWIDTH_MEDIUMBAND;
            if (tmp < 0)
            {
                tmp = 0;
            }
            toc = 0x80;
            toc |= tmp << 5;
            toc |= period << 3;
        }
        else /* Hybrid */
        {
            toc = 0x60;
            toc |= (bandwidth - OPUS_BANDWIDTH_SUPERWIDEBAND) << 4;
            toc |= (period - 2) << 3;
        }
        toc |= (channels == 2 ? 1 : 0) << 2;
        return (byte)toc;
    }

    /* Second order ARMA filter, alternative implementation */
    internal static void silk_biquad_res(
        ReadOnlySpan<int> @in,          /* I     input signal                                               */
        ReadOnlySpan<int> B_Q28,        /* I     MA coefficients [3]                                        */
        ReadOnlySpan<int> A_Q28,        /* I     AR coefficients [2]                                        */
        Span<int> S,                    /* I/O   State vector [2]                                           */
        Span<int> @out,                 /* O     output signal                                              */
        int len,                        /* I     signal length (must be even)                               */
        int stride)
    {
        /* DIRECT FORM II TRANSPOSED (uses 2 element state vector) */
        int k;
        int inval, A0_U_Q28, A0_L_Q28, A1_U_Q28, A1_L_Q28, out32_Q14;

        /* Negate A_Q28 values and split in two parts */
        A0_L_Q28 = (-A_Q28[0]) & 0x00003FFF;        /* lower part */
        A0_U_Q28 = silk_RSHIFT(-A_Q28[0], 14);      /* upper part */
        A1_L_Q28 = (-A_Q28[1]) & 0x00003FFF;        /* lower part */
        A1_U_Q28 = silk_RSHIFT(-A_Q28[1], 14);      /* upper part */

        for (k = 0; k < len; k++)
        {
            /* S[ 0 ], S[ 1 ]: Q12 */
            inval = RES2INT16(@in[k * stride]);
            out32_Q14 = silk_LSHIFT(silk_SMLAWB(S[0], B_Q28[0], inval), 2);

            S[0] = S[1] + silk_RSHIFT_ROUND(silk_SMULWB(out32_Q14, A0_L_Q28), 14);
            S[0] = silk_SMLAWB(S[0], out32_Q14, A0_U_Q28);
            S[0] = silk_SMLAWB(S[0], B_Q28[1], inval);

            S[1] = silk_RSHIFT_ROUND(silk_SMULWB(out32_Q14, A1_L_Q28), 14);
            S[1] = silk_SMLAWB(S[1], out32_Q14, A1_U_Q28);
            S[1] = silk_SMLAWB(S[1], B_Q28[2], inval);

            /* Scale back to Q0 and saturate */
            @out[k * stride] = INT16TORES(silk_SAT16(silk_RSHIFT(out32_Q14 + (1 << 14) - 1, 14)));
        }
    }

    private static void hp_cutoff(ReadOnlySpan<int> @in, int cutoff_Hz, Span<int> @out, Span<int> hp_mem, int len, int channels, int Fs)
    {
        Span<int> B_Q28 = stackalloc int[3];
        Span<int> A_Q28 = stackalloc int[2];
        int Fc_Q19, r_Q28, r_Q22;

        Fc_Q19 = silk_DIV32_16(silk_SMULBB((int)((1.5 * 3.14159 / 1000 * (1L << 19)) + 0.5) /* SILK_FIX_CONST( 1.5 * 3.14159 / 1000, 19 ) */, cutoff_Hz), Fs / 1000);

        r_Q28 = (1 << 28) /* SILK_FIX_CONST( 1.0, 28 ) */ - silk_MUL((int)((0.92 * (1L << 9)) + 0.5) /* SILK_FIX_CONST( 0.92, 9 ) */, Fc_Q19);

        /* b = r * [ 1; -2; 1 ]; */
        /* a = [ 1; -2 * r * ( 1 - 0.5 * Fc^2 ); r^2 ]; */
        B_Q28[0] = r_Q28;
        B_Q28[1] = silk_LSHIFT(-r_Q28, 1);
        B_Q28[2] = r_Q28;

        /* -r * ( 2 - Fc * Fc ); */
        r_Q22 = silk_RSHIFT(r_Q28, 6);
        A_Q28[0] = silk_SMULWW(r_Q22, silk_SMULWW(Fc_Q19, Fc_Q19) - (2 << 22) /* SILK_FIX_CONST( 2.0, 22 ) */);
        A_Q28[1] = silk_SMULWW(r_Q22, r_Q22);

        silk_biquad_res(@in, B_Q28, A_Q28, hp_mem, @out, len, channels);
        if (channels == 2)
        {
            silk_biquad_res(@in.Slice(1), B_Q28, A_Q28, hp_mem.Slice(2), @out.Slice(1), len, channels);
        }
    }

    private static void dc_reject(ReadOnlySpan<int> @in, int cutoff_Hz, Span<int> @out, Span<int> hp_mem, int len, int channels, int Fs)
    {
        int c, i;
        int shift;

        /* Approximates -round(log2(6.3*cutoff_Hz/Fs)) */
        shift = MathOps.celt_ilog2(Fs / (cutoff_Hz * 4));
        if (channels == 2)
        {
            /* Both channels in one loop: each is a chain of dependent steps, so two run side by
               side. The same steps as below, channel by channel. */
            int limit = (1 << 16 << RES_SHIFT) - 1;
            int mem0 = hp_mem[0];
            int mem2 = hp_mem[2];
            ReadOnlySpan<int> input = @in.Slice(0, 2 * len);
            Span<int> output = @out.Slice(0, 2 * len);
            for (i = 0; i < len; i++)
            {
                int x0 = SHL32(SATURATE(input[2 * i], limit), 14 - RES_SHIFT);
                int x1 = SHL32(SATURATE(input[(2 * i) + 1], limit), 14 - RES_SHIFT);
                int y0 = x0 - mem0;
                int y1 = x1 - mem2;
                mem0 = mem0 + PSHR32(x0 - mem0, shift);
                mem2 = mem2 + PSHR32(x1 - mem2, shift);
                output[2 * i] = PSHR32(y0, 14 - RES_SHIFT);
                output[(2 * i) + 1] = PSHR32(y1, 14 - RES_SHIFT);
            }

            hp_mem[0] = mem0;
            hp_mem[2] = mem2;
            return;
        }

        for (c = 0; c < channels; c++)
        {
            for (i = 0; i < len; i++)
            {
                int x, y;
                /* Saturate at +6 dBFS to avoid any wrap-around. */
                x = SATURATE(@in[(channels * i) + c], (1 << 16 << RES_SHIFT) - 1);
                x = SHL32(x, 14 - RES_SHIFT);
                y = x - hp_mem[2 * c];
                hp_mem[2 * c] = hp_mem[2 * c] + PSHR32(x - hp_mem[2 * c], shift);
                /* Don't saturate if we have the headroom to avoid it. */
                @out[(channels * i) + c] = PSHR32(y, 14 - RES_SHIFT);
            }
        }
    }

    private static void stereo_fade(ReadOnlySpan<int> @in, Span<int> @out, short g1, short g2,
            int overlap48, int frame_size, int channels, ReadOnlySpan<short> window, int Fs)
    {
        int i;
        int overlap;
        int inc;
        inc = IMAX(1, 48000 / Fs);
        overlap = overlap48 / inc;
        g1 = (short)(Q15ONE - g1);
        g2 = (short)(Q15ONE - g2);
        for (i = 0; i < overlap; i++)
        {
            int diff;
            short g, w;
            w = COEF2VAL16(window[i * inc]);
            w = (short)MULT16_16_Q15(w, w);
            g = (short)SHR32(MAC16_16(MULT16_16(w, g2),
                  Q15ONE - w, g1), 15);
            diff = HALF32(@in[i * channels] - @in[(i * channels) + 1]);
            diff = MULT16_RES_Q15(g, diff);
            @out[i * channels] = @out[i * channels] - diff;
            @out[(i * channels) + 1] = @out[(i * channels) + 1] + diff;
        }
        for (; i < frame_size; i++)
        {
            int diff;
            diff = HALF32(@in[i * channels] - @in[(i * channels) + 1]);
            diff = MULT16_RES_Q15(g2, diff);
            @out[i * channels] = @out[i * channels] - diff;
            @out[(i * channels) + 1] = @out[(i * channels) + 1] + diff;
        }
    }

    private static void gain_fade(ReadOnlySpan<int> @in, Span<int> @out, short g1, short g2,
            int overlap48, int frame_size, int channels, ReadOnlySpan<short> window, int Fs)
    {
        int i;
        int inc;
        int overlap;
        int c;
        inc = IMAX(1, 48000 / Fs);
        overlap = overlap48 / inc;
        if (channels == 1)
        {
            for (i = 0; i < overlap; i++)
            {
                short g, w;
                w = COEF2VAL16(window[i * inc]);
                w = (short)MULT16_16_Q15(w, w);
                g = (short)SHR32(MAC16_16(MULT16_16(w, g2),
                      Q15ONE - w, g1), 15);
                @out[i] = MULT16_RES_Q15(g, @in[i]);
            }
        }
        else
        {
            for (i = 0; i < overlap; i++)
            {
                short g, w;
                w = COEF2VAL16(window[i * inc]);
                w = (short)MULT16_16_Q15(w, w);
                g = (short)SHR32(MAC16_16(MULT16_16(w, g2),
                      Q15ONE - w, g1), 15);
                @out[i * 2] = MULT16_RES_Q15(g, @in[i * 2]);
                @out[(i * 2) + 1] = MULT16_RES_Q15(g, @in[(i * 2) + 1]);
            }
        }
        c = 0;
        do
        {
            for (i = overlap; i < frame_size; i++)
            {
                @out[(i * channels) + c] = MULT16_RES_Q15(g2, @in[(i * channels) + c]);
            }
        }
        while (++c < channels);
    }

    private int user_bitrate_to_bitrate(int frame_size, int max_data_bytes)
    {
        int max_bitrate, user_bitrate;
        if (frame_size == 0)
        {
            frame_size = this.Fs / 400;
        }
        max_bitrate = bits_to_bitrate(max_data_bytes * 8, this.Fs, frame_size);
        if (this.user_bitrate_bps == OPUS_AUTO)
        {
            user_bitrate = (60 * this.Fs / frame_size) + (this.Fs * this.channels);
        }
        else if (this.user_bitrate_bps == OPUS_BITRATE_MAX)
        {
            user_bitrate = 1500000;
        }
        else
        {
            user_bitrate = this.user_bitrate_bps;
        }
        return IMIN(user_bitrate, max_bitrate);
    }

    internal static void downmix_float(ReadOnlySpan<byte> _x, Span<int> y, int subframe, int offset, int c1, int c2, int C)
    {
        ReadOnlySpan<float> x;
        int j;

        x = MemoryMarshal.Cast<byte, float>(_x);
        for (j = 0; j < subframe; j++)
        {
            y[j] = FLOAT2SIG(x[((j + offset) * C) + c1]);
        }
        if (c2 > -1)
        {
            for (j = 0; j < subframe; j++)
            {
                y[j] += FLOAT2SIG(x[((j + offset) * C) + c2]);
            }
        }
        else if (c2 == -2)
        {
            int c;
            for (c = 1; c < C; c++)
            {
                for (j = 0; j < subframe; j++)
                {
                    y[j] += FLOAT2SIG(x[((j + offset) * C) + c]);
                }
            }
        }
    }

    internal static void downmix_int(ReadOnlySpan<byte> _x, Span<int> y, int subframe, int offset, int c1, int c2, int C)
    {
        ReadOnlySpan<short> x;
        int j;

        x = MemoryMarshal.Cast<byte, short>(_x);
        for (j = 0; j < subframe; j++)
        {
            y[j] = INT16TOSIG(x[((j + offset) * C) + c1]);
        }
        if (c2 > -1)
        {
            for (j = 0; j < subframe; j++)
            {
                y[j] += INT16TOSIG(x[((j + offset) * C) + c2]);
            }
        }
        else if (c2 == -2)
        {
            int c;
            for (c = 1; c < C; c++)
            {
                for (j = 0; j < subframe; j++)
                {
                    y[j] += INT16TOSIG(x[((j + offset) * C) + c]);
                }
            }
        }
    }

    internal static void downmix_int24(ReadOnlySpan<byte> _x, Span<int> y, int subframe, int offset, int c1, int c2, int C)
    {
        ReadOnlySpan<int> x;
        int j;

        x = MemoryMarshal.Cast<byte, int>(_x);
        for (j = 0; j < subframe; j++)
        {
            y[j] = INT24TOSIG(x[((j + offset) * C) + c1]);
        }
        if (c2 > -1)
        {
            for (j = 0; j < subframe; j++)
            {
                y[j] += INT24TOSIG(x[((j + offset) * C) + c2]);
            }
        }
        else if (c2 == -2)
        {
            int c;
            for (c = 1; c < C; c++)
            {
                for (j = 0; j < subframe; j++)
                {
                    y[j] += INT24TOSIG(x[((j + offset) * C) + c]);
                }
            }
        }
    }

    internal static int frame_size_select(int application, int frame_size, int variable_duration, int Fs)
    {
        int new_size;
        if (frame_size < Fs / 400)
        {
            return -1;
        }
        if (variable_duration == OPUS_FRAMESIZE_ARG)
        {
            new_size = frame_size;
        }
        else if (variable_duration >= OPUS_FRAMESIZE_2_5_MS && variable_duration <= OPUS_FRAMESIZE_120_MS)
        {
            if (variable_duration <= OPUS_FRAMESIZE_40_MS)
            {
                new_size = (Fs / 400) << (variable_duration - OPUS_FRAMESIZE_2_5_MS);
            }
            else
            {
                new_size = (variable_duration - OPUS_FRAMESIZE_2_5_MS - 2) * Fs / 50;
            }
        }
        else
        {
            return -1;
        }
        if (new_size > frame_size)
        {
            return -1;
        }
        if (400 * new_size != Fs && 200 * new_size != Fs && 100 * new_size != Fs &&
             50 * new_size != Fs && 25 * new_size != Fs && 50 * new_size != 3 * Fs &&
             50 * new_size != 4 * Fs && 50 * new_size != 5 * Fs && 50 * new_size != 6 * Fs)
        {
            return -1;
        }
        if (application == OPUS_APPLICATION_RESTRICTED_SILK && new_size < Fs / 100)
        {
            return -1;
        }
        return new_size;
    }

    internal static short compute_stereo_width(ReadOnlySpan<int> pcm, int frame_size, int Fs, ref StereoWidthState mem)
    {
        int xx, xy, yy;
        short sqrt_xx, sqrt_yy;
        short qrrt_xx, qrrt_yy;
        int frame_rate;
        int i;
        short short_alpha;
        int shift = MathOps.celt_ilog2(frame_size) - 2;
        frame_rate = Fs / frame_size;
        short_alpha = (short)(MULT16_16(25, Q15ONE) / IMAX(50, frame_rate));
        xx = xy = yy = 0;
        /* Unroll by 4. The frame size is always a multiple of 4 *except* for
           2.5 ms frames at 12 kHz. Since this setting is very rare (and very
           stupid), we just discard the last two samples. */
        for (i = 0; i < frame_size - 3; i += 4)
        {
            int pxx = 0;
            int pxy = 0;
            int pyy = 0;
            short x, y;
            x = RES2VAL16(pcm[2 * i]);
            y = RES2VAL16(pcm[(2 * i) + 1]);
            pxx = SHR32(MULT16_16(x, x), 2);
            pxy = SHR32(MULT16_16(x, y), 2);
            pyy = SHR32(MULT16_16(y, y), 2);
            x = RES2VAL16(pcm[(2 * i) + 2]);
            y = RES2VAL16(pcm[(2 * i) + 3]);
            pxx += SHR32(MULT16_16(x, x), 2);
            pxy += SHR32(MULT16_16(x, y), 2);
            pyy += SHR32(MULT16_16(y, y), 2);
            x = RES2VAL16(pcm[(2 * i) + 4]);
            y = RES2VAL16(pcm[(2 * i) + 5]);
            pxx += SHR32(MULT16_16(x, x), 2);
            pxy += SHR32(MULT16_16(x, y), 2);
            pyy += SHR32(MULT16_16(y, y), 2);
            x = RES2VAL16(pcm[(2 * i) + 6]);
            y = RES2VAL16(pcm[(2 * i) + 7]);
            pxx += SHR32(MULT16_16(x, x), 2);
            pxy += SHR32(MULT16_16(x, y), 2);
            pyy += SHR32(MULT16_16(y, y), 2);

            xx += SHR32(pxx, shift);
            xy += SHR32(pxy, shift);
            yy += SHR32(pyy, shift);
        }
        mem.XX += MULT16_32_Q15(short_alpha, xx - mem.XX);
        /*mem->XY += MULT16_32_Q15(short_alpha, xy-mem->XY);*/
        /* Rewritten to avoid overflows on abrupt sign change. */
        mem.XY = MULT16_32_Q15(Q15ONE - short_alpha, mem.XY) + MULT16_32_Q15(short_alpha, xy);
        mem.YY += MULT16_32_Q15(short_alpha, yy - mem.YY);
        mem.XX = MAX32(0, mem.XX);
        mem.XY = MAX32(0, mem.XY);
        mem.YY = MAX32(0, mem.YY);
        if (MAX32(mem.XX, mem.YY) > 210 /* QCONST16(8e-4f, 18) */)
        {
            short corr;
            short ldiff;
            short width;
            sqrt_xx = (short)MathOps.celt_sqrt(mem.XX);
            sqrt_yy = (short)MathOps.celt_sqrt(mem.YY);
            qrrt_xx = (short)MathOps.celt_sqrt(sqrt_xx);
            qrrt_yy = (short)MathOps.celt_sqrt(sqrt_yy);
            /* Inter-channel correlation */
            mem.XY = MIN32(mem.XY, sqrt_xx * sqrt_yy);
            corr = (short)SHR32(MathOps.frac_div32(mem.XY, EPSILON + MULT16_16(sqrt_xx, sqrt_yy)), 16);
            /* Approximate loudness difference */
            ldiff = (short)(MULT16_16(Q15ONE, ABS16(qrrt_xx - qrrt_yy)) / (EPSILON + qrrt_xx + qrrt_yy));
            width = (short)MULT16_16_Q15(MIN16(Q15ONE, MathOps.celt_sqrt(1073741824 /* QCONST32(1.f,30) */ - MULT16_16(corr, corr))), ldiff);
            /* Smoothing over one second */
            mem.smoothed_width += (short)((width - mem.smoothed_width) / frame_rate);
            /* Peak follower */
            mem.max_follower = (short)MAX16(mem.max_follower - (655 /* QCONST16(.02f,15) */ / frame_rate), mem.smoothed_width);
        }
        return EXTRACT16(MIN32(Q15ONE, MULT16_16(20, mem.max_follower)));
    }

    private static int decide_fec(int useInBandFEC, int PacketLoss_perc, int last_fec, int mode, ref int bandwidth, int rate)
    {
        int orig_bandwidth;
        if (useInBandFEC == 0 || PacketLoss_perc == 0 || mode == MODE_CELT_ONLY)
        {
            return 0;
        }
        orig_bandwidth = bandwidth;
        for (; ; )
        {
            int hysteresis;
            int LBRR_rate_thres_bps;
            /* Compute threshold for using FEC at the current bandwidth setting */
            LBRR_rate_thres_bps = fec_thresholds[2 * (bandwidth - OPUS_BANDWIDTH_NARROWBAND)];
            hysteresis = fec_thresholds[(2 * (bandwidth - OPUS_BANDWIDTH_NARROWBAND)) + 1];
            if (last_fec == 1)
            {
                LBRR_rate_thres_bps -= hysteresis;
            }
            if (last_fec == 0)
            {
                LBRR_rate_thres_bps += hysteresis;
            }
            LBRR_rate_thres_bps = silk_SMULWB(silk_MUL(LBRR_rate_thres_bps,
                  125 - silk_min(PacketLoss_perc, 25)), (int)((0.01 * (1L << 16)) + 0.5) /* SILK_FIX_CONST( 0.01, 16 ) */);
            /* If loss <= 5%, we look at whether we have enough rate to enable FEC.
               If loss > 5%, we decrease the bandwidth until we can enable FEC. */
            if (rate > LBRR_rate_thres_bps)
            {
                return 1;
            }
            else if (PacketLoss_perc <= 5)
            {
                return 0;
            }
            else if (bandwidth > OPUS_BANDWIDTH_NARROWBAND)
            {
                bandwidth--;
            }
            else
            {
                break;
            }
        }
        /* Couldn't find any bandwidth to enable FEC, keep original bandwidth. */
        bandwidth = orig_bandwidth;
        return 0;
    }

    private static int compute_silk_rate_for_hybrid(int rate, int bandwidth, int frame20ms, int vbr, int fec, int channels)
    {
        int entry;
        int i;
        int N;
        int silk_rate;
        /* Do the allocation per-channel. */
        rate /= channels;
        entry = 1 + frame20ms + (2 * fec);
        N = rate_table.Length / 5;
        for (i = 1; i < N; i++)
        {
            if (rate_table[i * 5] > rate)
            {
                break;
            }
        }
        if (i == N)
        {
            silk_rate = rate_table[((i - 1) * 5) + entry];
            /* For now, just give 50% of the extra bits to SILK. */
            silk_rate += (rate - rate_table[(i - 1) * 5]) / 2;
        }
        else
        {
            int lo, hi, x0, x1;
            lo = rate_table[((i - 1) * 5) + entry];
            hi = rate_table[(i * 5) + entry];
            x0 = rate_table[(i - 1) * 5];
            x1 = rate_table[i * 5];
            silk_rate = ((lo * (x1 - rate)) + (hi * (rate - x0))) / (x1 - x0);
        }
        if (vbr == 0)
        {
            /* Tiny boost to SILK for CBR. We should probably tune this better. */
            silk_rate += 100;
        }
        if (bandwidth == OPUS_BANDWIDTH_SUPERWIDEBAND)
        {
            silk_rate += 300;
        }
        silk_rate *= channels;
        /* Small adjustment for stereo (calibrated for 32 kb/s, haven't tried other bitrates). */
        if (channels == 2 && rate >= 12000)
        {
            silk_rate -= 1000;
        }
        return silk_rate;
    }

    /* Returns the equivalent bitrate corresponding to 20 ms frames,
       complexity 10 VBR operation. */
    private static int compute_equiv_rate(int bitrate, int channels,
          int frame_rate, int vbr, int mode, int complexity, int loss)
    {
        int equiv;
        equiv = bitrate;
        /* Take into account overhead from smaller frames. */
        if (frame_rate > 50)
        {
            equiv -= ((40 * channels) + 20) * (frame_rate - 50);
        }
        /* CBR is about a 8% penalty for both SILK and CELT. */
        if (vbr == 0)
        {
            equiv -= equiv / 12;
        }
        /* Complexity makes about 10% difference (from 0 to 10) in general. */
        equiv = equiv * (90 + complexity) / 100;
        if (mode == MODE_SILK_ONLY || mode == MODE_HYBRID)
        {
            /* SILK complexity 0-1 uses the non-delayed-decision NSQ, which
               costs about 20%. */
            if (complexity < 2)
            {
                equiv = equiv * 4 / 5;
            }
            equiv -= equiv * loss / ((6 * loss) + 10);
        }
        else if (mode == MODE_CELT_ONLY)
        {
            /* CELT complexity 0-4 doesn't have the pitch filter, which costs
               about 10%. */
            if (complexity < 5)
            {
                equiv = equiv * 9 / 10;
            }
        }
        else
        {
            /* Mode not known yet */
            /* Half the SILK loss*/
            equiv -= equiv * loss / ((12 * loss) + 20);
        }
        return equiv;
    }

    internal static int is_digital_silence(ReadOnlySpan<int> pcm, int frame_size, int channels, int lsb_depth)
    {
        int silence = 0;
        int sample_max = 0;
        sample_max = MathOps.celt_maxabs_res(pcm, frame_size * channels);

        silence = sample_max == 0 ? 1 : 0;
        _ = lsb_depth;

        return silence;
    }

    private static int compute_frame_energy(ReadOnlySpan<int> pcm, int frame_size, int channels)
    {
        int i;
        int sample_max;
        int max_shift;
        int shift;
        int energy = 0;
        int len = frame_size * channels;
        /* Max amplitude in the signal */
        sample_max = RES2INT16(MathOps.celt_maxabs_res(pcm, len));

        /* Compute the right shift required in the MAC to avoid an overflow */
        max_shift = MathOps.celt_ilog2(len);
        shift = IMAX(0, (MathOps.celt_ilog2(1 + sample_max) << 1) + max_shift - 28);

        /* Compute the energy */
        /* Four at a time: RES2INT16 rounds and saturates to 16 bits, and the squares and their
           sum are integers, the same in any order */
        i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            var acc = Vector128<int>.Zero;
            var round = Vector128.Create(1 << (RES_SHIFT - 1));
            var low = Vector128.Create(-32768);
            var high = Vector128.Create(32767);
            for (; i + 4 <= len; i += 4)
            {
                var v = Vector128.Min(Vector128.Max((Vector128.Create(pcm.Slice(i, 4)) + round) >> RES_SHIFT, low), high);
                acc += (v * v) >> shift;
            }

            energy = Vector128.Sum(acc);
        }

        for (; i < len; i++)
        {
            energy += SHR32(MULT16_16(RES2INT16(pcm[i]), RES2INT16(pcm[i])), shift);
        }

        /* Normalize energy by the frame size and left-shift back to the original position */
        energy /= len;
        energy = SHL32(energy, shift);

        return energy;
    }

    /* Decides if DTX should be turned on (=1) or off (=0) */
    private static int decide_dtx_mode(int activity,           /* indicates if this frame contains speech/music */
                               ref int nb_no_activity_ms_Q1,    /* number of consecutive milliseconds with no activity, in Q1 */
                               int frame_size_ms_Q1)            /* number of milliseconds in this update, in Q1 */
    {
        if (activity == 0)
        {
            /* The number of consecutive DTX frames should be within the allowed bounds.
               Note that the allowed bound is defined in the SILK headers and assumes 20 ms
               frames. As this function can be called with any frame length, a conversion to
               milliseconds is done before the comparisons. */
            nb_no_activity_ms_Q1 += frame_size_ms_Q1;
            if (nb_no_activity_ms_Q1 > NB_SPEECH_FRAMES_BEFORE_DTX * 20 * 2)
            {
                if (nb_no_activity_ms_Q1 <= (NB_SPEECH_FRAMES_BEFORE_DTX + MAX_CONSECUTIVE_DTX) * 20 * 2)
                {
                    /* Valid frame for DTX! */
                    return 1;
                }
                else
                {
                    nb_no_activity_ms_Q1 = NB_SPEECH_FRAMES_BEFORE_DTX * 20 * 2;
                }
            }
        }
        else
        {
            nb_no_activity_ms_Q1 = 0;
        }

        return 0;
    }

    private static int compute_redundancy_bytes(int max_data_bytes, int bitrate_bps, int frame_rate, int channels)
    {
        int redundancy_bytes_cap;
        int redundancy_bytes;
        int redundancy_rate;
        int base_bits;
        int available_bits;
        base_bits = (40 * channels) + 20;

        /* Equivalent rate for 5 ms frames. */
        redundancy_rate = bitrate_bps + (base_bits * (200 - frame_rate));
        /* For VBR, further increase the bitrate if we can afford it. It's pretty short
           and we'll avoid artefacts. */
        redundancy_rate = 3 * redundancy_rate / 2;
        redundancy_bytes = redundancy_rate / 1600;

        /* Compute the max rate we can use given CBR or VBR with cap. */
        available_bits = (max_data_bytes * 8) - (2 * base_bits);
        redundancy_bytes_cap = ((available_bits * 240 / (240 + (48000 / frame_rate))) + base_bits) / 8;
        redundancy_bytes = IMIN(redundancy_bytes, redundancy_bytes_cap);
        /* It we can't get enough bits for redundancy to be worth it, rely on the decoder PLC. */
        if (redundancy_bytes > 4 + (8 * channels))
        {
            redundancy_bytes = IMIN(257, redundancy_bytes);
        }
        else
        {
            redundancy_bytes = 0;
        }
        return redundancy_bytes;
    }

    // analysis_pcm is C's const void * (the caller's samples, as bytes)
    internal int opus_encode_native(ReadOnlySpan<int> pcm, int frame_size,
                    Span<byte> data, int out_data_bytes, int lsb_depth,
                    ReadOnlySpan<byte> analysis_pcm, int analysis_size, int c1, int c2,
                    int analysis_channels, downmix_func downmix, int float_api)
    {
        int i;
        int ret = 0;
        int prefill = 0;
        int redundancy = 0;
        int celt_to_silk = 0;
        int to_celt = 0;
        int voice_est; /* Probability of voice in Q7 */
        int equiv_rate;
        int frame_rate;
        int max_rate; /* Max bitrate we're allowed to use */
        int curr_bandwidth;
        int max_data_bytes; /* Max number of bytes we're allowed to use */
        int cbr_bytes = -1;
        short stereo_width;
        OpusCustomMode? celt_mode = null;
        int packet_size_cap = 1276;
        AnalysisInfo analysis_info = default;
        int analysis_read_pos_bak = -1;
        int analysis_read_subframe_bak = -1;
        int is_silence = 0;

        /* Just avoid insane packet sizes here, but the real bounds are applied later on. */
        max_data_bytes = IMIN(packet_size_cap * 6, out_data_bytes);

        this.rangeFinal = 0;
        if (frame_size <= 0 || max_data_bytes <= 0)
        {
            return OPUS_BAD_ARG;
        }

        /* Cannot encode 100 ms in 1 byte */
        if (max_data_bytes == 1 && this.Fs == (frame_size * 10))
        {
            return OPUS_BUFFER_TOO_SMALL;
        }

        lsb_depth = IMIN(lsb_depth, this.lsb_depth);

        if (this.application != OPUS_APPLICATION_RESTRICTED_SILK)
        {
            celt_mode = this.celt_enc!.GetMode();
        }
        is_silence = is_digital_silence(pcm, frame_size, this.channels, lsb_depth);
        analysis_info.valid = 0;
        if (this.silk_mode.complexity >= 10 && this.Fs >= 16000 && this.Fs <= 48000 && this.application != OPUS_APPLICATION_RESTRICTED_SILK)
        {
            analysis_read_pos_bak = this.analysis.read_pos;
            analysis_read_subframe_bak = this.analysis.read_subframe;
            Analysis.run_analysis(this.analysis, celt_mode!, analysis_pcm, true, analysis_size, frame_size,
                  c1, c2, analysis_channels, this.Fs,
                  lsb_depth, downmix, ref analysis_info);
        }
        else if (this.analysis.initialized != 0)
        {
            Analysis.tonality_analysis_reset(this.analysis);
        }

        /* Reset voice_ratio if this frame is not silent or if analysis is disabled.
         * Otherwise, preserve voice_ratio from the last non-silent frame */
        if (is_silence == 0)
        {
            this.voice_ratio = -1;
        }
        this.detected_bandwidth = 0;
        if (analysis_info.valid != 0)
        {
            int analysis_bandwidth;
            if (this.signal_type == OPUS_AUTO)
            {
                float prob;
                if (this.prev_mode == 0)
                {
                    prob = analysis_info.music_prob;
                }
                else if (this.prev_mode == MODE_CELT_ONLY)
                {
                    prob = analysis_info.music_prob_max;
                }
                else
                {
                    prob = analysis_info.music_prob_min;
                }
                this.voice_ratio = (int)Math.Floor(.5 + (100 * (1 - prob)));
            }

            analysis_bandwidth = analysis_info.bandwidth;
            if (analysis_bandwidth <= 12)
            {
                this.detected_bandwidth = OPUS_BANDWIDTH_NARROWBAND;
            }
            else if (analysis_bandwidth <= 14)
            {
                this.detected_bandwidth = OPUS_BANDWIDTH_MEDIUMBAND;
            }
            else if (analysis_bandwidth <= 16)
            {
                this.detected_bandwidth = OPUS_BANDWIDTH_WIDEBAND;
            }
            else if (analysis_bandwidth <= 18)
            {
                this.detected_bandwidth = OPUS_BANDWIDTH_SUPERWIDEBAND;
            }
            else
            {
                this.detected_bandwidth = OPUS_BANDWIDTH_FULLBAND;
            }
        }

        /* Track the peak signal energy */
        if (analysis_info.valid == 0 || analysis_info.activity_probability > DTX_ACTIVITY_THRESHOLD)
        {
            if (is_silence == 0)
            {
                this.peak_signal_energy = MAX32(MULT16_32_Q15(32735 /* QCONST16(0.999f, 15) */, this.peak_signal_energy),
                      compute_frame_energy(pcm, frame_size, this.channels));
            }
        }
        if (this.channels == 2 && this.force_channels != 1)
        {
            stereo_width = compute_stereo_width(pcm, frame_size, this.Fs, ref this.width_mem);
        }
        else
        {
            stereo_width = 0;
        }
        this.bitrate_bps = this.user_bitrate_to_bitrate(frame_size, max_data_bytes);

        frame_rate = this.Fs / frame_size;
        if (this.use_vbr == 0)
        {
            cbr_bytes = IMIN((bitrate_to_bits(this.bitrate_bps, this.Fs, frame_size) + 4) / 8, max_data_bytes);
            this.bitrate_bps = bits_to_bitrate(cbr_bytes * 8, this.Fs, frame_size);
            /* Make sure we provide at least one byte to avoid failing. */
            max_data_bytes = IMAX(1, cbr_bytes);
        }
        if (max_data_bytes < 3 || this.bitrate_bps < 3 * frame_rate * 8
           || (frame_rate < 50 && (max_data_bytes * frame_rate < 300 || this.bitrate_bps < 2400)))
        {
            /*If the space is too low to do something useful, emit 'PLC' frames.*/
            int tocmode = this.mode;
            int bw = this.bandwidth == 0 ? OPUS_BANDWIDTH_NARROWBAND : this.bandwidth;
            int packet_code = 0;
            int num_multiframes = 0;

            if (tocmode == 0)
            {
                tocmode = MODE_SILK_ONLY;
            }
            if (frame_rate > 100)
            {
                tocmode = MODE_CELT_ONLY;
            }
            /* 40 ms -> 2 x 20 ms if in CELT_ONLY or HYBRID mode */
            if (frame_rate == 25 && tocmode != MODE_SILK_ONLY)
            {
                frame_rate = 50;
                packet_code = 1;
            }

            /* >= 60 ms frames */
            if (frame_rate <= 16)
            {
                /* 1 x 60 ms, 2 x 40 ms, 2 x 60 ms */
                if (out_data_bytes == 1 || (tocmode == MODE_SILK_ONLY && frame_rate != 10))
                {
                    tocmode = MODE_SILK_ONLY;

                    packet_code = frame_rate <= 12 ? 1 : 0;
                    frame_rate = frame_rate == 12 ? 25 : 16;
                }
                else
                {
                    num_multiframes = 50 / frame_rate;
                    frame_rate = 50;
                    packet_code = 3;
                }
            }

            if (tocmode == MODE_SILK_ONLY && bw > OPUS_BANDWIDTH_WIDEBAND)
            {
                bw = OPUS_BANDWIDTH_WIDEBAND;
            }
            else if (tocmode == MODE_CELT_ONLY && bw == OPUS_BANDWIDTH_MEDIUMBAND)
            {
                bw = OPUS_BANDWIDTH_NARROWBAND;
            }
            else if (tocmode == MODE_HYBRID && bw <= OPUS_BANDWIDTH_SUPERWIDEBAND)
            {
                bw = OPUS_BANDWIDTH_SUPERWIDEBAND;
            }

            data[0] = gen_toc(tocmode, frame_rate, bw, this.stream_channels);
            data[0] |= (byte)packet_code;

            ret = packet_code <= 1 ? 1 : 2;

            max_data_bytes = IMAX(max_data_bytes, ret);

            if (packet_code == 3)
            {
                data[1] = (byte)num_multiframes;
            }

            if (this.use_vbr == 0)
            {
                ret = Repacketizer.opus_packet_pad(data, ret, max_data_bytes, this.pad_rp);
                if (ret == OPUS_OK)
                {
                    ret = max_data_bytes;
                }
                else
                {
                    ret = OPUS_INTERNAL_ERROR;
                }
            }
            return ret;
        }
        max_rate = bits_to_bitrate(max_data_bytes * 8, this.Fs, frame_size);

        /* Equivalent 20-ms rate for mode/channel/bandwidth decisions */
        equiv_rate = compute_equiv_rate(this.bitrate_bps, this.channels, this.Fs / frame_size,
              this.use_vbr, 0, this.silk_mode.complexity, this.silk_mode.packetLossPercentage);

        if (this.signal_type == OPUS_SIGNAL_VOICE)
        {
            voice_est = 127;
        }
        else if (this.signal_type == OPUS_SIGNAL_MUSIC)
        {
            voice_est = 0;
        }
        else if (this.voice_ratio >= 0)
        {
            voice_est = (this.voice_ratio * 327) >> 8;
            /* For AUDIO, never be more than 90% confident of having speech */
            if (this.application == OPUS_APPLICATION_AUDIO)
            {
                voice_est = IMIN(voice_est, 115);
            }
        }
        else if (this.application == OPUS_APPLICATION_VOIP)
        {
            voice_est = 115;
        }
        else
        {
            voice_est = 48;
        }

        if (this.force_channels != OPUS_AUTO && this.channels == 2)
        {
            this.stream_channels = this.force_channels;
        }
        else
        {
            /* Rate-dependent mono-stereo decision */
            if (this.channels == 2)
            {
                int stereo_threshold;
                stereo_threshold = stereo_music_threshold + ((voice_est * voice_est * (stereo_voice_threshold - stereo_music_threshold)) >> 14);
                if (this.stream_channels == 2)
                {
                    stereo_threshold -= 1000;
                }
                else
                {
                    stereo_threshold += 1000;
                }
                this.stream_channels = (equiv_rate > stereo_threshold) ? 2 : 1;
            }
            else
            {
                this.stream_channels = this.channels;
            }
        }
        /* Update equivalent rate for channels decision. */
        equiv_rate = compute_equiv_rate(this.bitrate_bps, this.stream_channels, this.Fs / frame_size,
              this.use_vbr, 0, this.silk_mode.complexity, this.silk_mode.packetLossPercentage);

        /* Allow SILK DTX if DTX is enabled but the generalized DTX cannot be used,
           e.g. because of the complexity setting or sample rate. */
        this.silk_mode.useDTX = (this.use_dtx != 0 && !(analysis_info.valid != 0 || is_silence != 0)) ? 1 : 0;

        /* Mode selection depending on application and signal type */
        if (this.application == OPUS_APPLICATION_RESTRICTED_SILK)
        {
            this.mode = MODE_SILK_ONLY;
        }
        else if (this.application == OPUS_APPLICATION_RESTRICTED_LOWDELAY || this.application == OPUS_APPLICATION_RESTRICTED_CELT)
        {
            this.mode = MODE_CELT_ONLY;
        }
        else if (this.user_forced_mode == OPUS_AUTO)
        {
            int mode_voice, mode_music;
            int threshold;

            /* Interpolate based on stereo width */
            mode_voice = MULT16_32_Q15(Q15ONE - stereo_width, mode_thresholds[0])
                  + MULT16_32_Q15(stereo_width, mode_thresholds[2]);
            mode_music = MULT16_32_Q15(Q15ONE - stereo_width, mode_thresholds[3])
                  + MULT16_32_Q15(stereo_width, mode_thresholds[3]);
            /* Interpolate based on speech/music probability */
            threshold = mode_music + ((voice_est * voice_est * (mode_voice - mode_music)) >> 14);
            /* Bias towards SILK for VoIP because of some useful features */
            if (this.application == OPUS_APPLICATION_VOIP)
            {
                threshold += 8000;
            }

            /* Hysteresis */
            if (this.prev_mode == MODE_CELT_ONLY)
            {
                threshold -= 4000;
            }
            else if (this.prev_mode > 0)
            {
                threshold += 4000;
            }

            this.mode = (equiv_rate >= threshold) ? MODE_CELT_ONLY : MODE_SILK_ONLY;

            /* When FEC is enabled and there's enough packet loss, use SILK.
               Unless the FEC is set to 2, in which case we don't switch to SILK if we're confident we have music. */
            if (this.silk_mode.useInBandFEC != 0 && this.silk_mode.packetLossPercentage > (128 - voice_est) >> 4 && (this.fec_config != 2 || voice_est > 25))
            {
                this.mode = MODE_SILK_ONLY;
            }
            /* When encoding voice and DTX is enabled but the generalized DTX cannot be used,
               use SILK in order to make use of its DTX. */
            if (this.silk_mode.useDTX != 0 && voice_est > 100)
            {
                this.mode = MODE_SILK_ONLY;
            }

            /* If max_data_bytes represents less than 6 kb/s, switch to CELT-only mode */
            if (max_data_bytes < bitrate_to_bits(frame_rate > 50 ? 9000 : 6000, this.Fs, frame_size) / 8)
            {
                this.mode = MODE_CELT_ONLY;
            }
        }
        else
        {
            this.mode = this.user_forced_mode;
        }

        /* Override the chosen mode to make sure we meet the requested frame size */
        if (this.mode != MODE_CELT_ONLY && frame_size < this.Fs / 100)
        {
            System.Diagnostics.Debug.Assert(this.application != OPUS_APPLICATION_RESTRICTED_SILK);
            this.mode = MODE_CELT_ONLY;
        }
        if (this.lfe != 0 && this.application != OPUS_APPLICATION_RESTRICTED_SILK)
        {
            this.mode = MODE_CELT_ONLY;
        }

        if (this.prev_mode > 0 &&
            ((this.mode != MODE_CELT_ONLY && this.prev_mode == MODE_CELT_ONLY) ||
            (this.mode == MODE_CELT_ONLY && this.prev_mode != MODE_CELT_ONLY)))
        {
            redundancy = 1;
            celt_to_silk = (this.mode != MODE_CELT_ONLY) ? 1 : 0;
            if (celt_to_silk == 0)
            {
                /* Switch to SILK/hybrid if frame size is 10 ms or more*/
                if (frame_size >= this.Fs / 100)
                {
                    this.mode = this.prev_mode;
                    to_celt = 1;
                }
                else
                {
                    redundancy = 0;
                }
            }
        }

        /* When encoding multiframes, we can ask for a switch to CELT only in the last frame. This switch
         * is processed above as the requested mode shouldn't interrupt stereo->mono transition. */
        if (this.stream_channels == 1 && this.prev_channels == 2 && this.silk_mode.toMono == 0
              && this.mode != MODE_CELT_ONLY && this.prev_mode != MODE_CELT_ONLY)
        {
            /* Delay stereo->mono transition by two frames so that SILK can do a smooth downmix */
            this.silk_mode.toMono = 1;
            this.stream_channels = 2;
        }
        else
        {
            this.silk_mode.toMono = 0;
        }

        /* Update equivalent rate with mode decision. */
        equiv_rate = compute_equiv_rate(this.bitrate_bps, this.stream_channels, this.Fs / frame_size,
              this.use_vbr, this.mode, this.silk_mode.complexity, this.silk_mode.packetLossPercentage);

        if (this.mode != MODE_CELT_ONLY && this.prev_mode == MODE_CELT_ONLY)
        {
            EncAPI.silk_InitEncoder(this.silk_enc!, this.channels, this.dummy);
            prefill = 1;
        }

        /* Automatic (rate-dependent) bandwidth selection */
        if (this.mode == MODE_CELT_ONLY || this.first != 0 || this.silk_mode.allowBandwidthSwitch != 0)
        {
            ReadOnlySpan<int> voice_bandwidth_thresholds, music_bandwidth_thresholds;
            Span<int> bandwidth_thresholds = stackalloc int[8];
            int bandwidth = OPUS_BANDWIDTH_FULLBAND;

            if (this.channels == 2 && this.force_channels != 1)
            {
                voice_bandwidth_thresholds = stereo_voice_bandwidth_thresholds;
                music_bandwidth_thresholds = stereo_music_bandwidth_thresholds;
            }
            else
            {
                voice_bandwidth_thresholds = mono_voice_bandwidth_thresholds;
                music_bandwidth_thresholds = mono_music_bandwidth_thresholds;
            }
            /* Interpolate bandwidth thresholds depending on voice estimation */
            for (i = 0; i < 8; i++)
            {
                bandwidth_thresholds[i] = music_bandwidth_thresholds[i]
                         + ((voice_est * voice_est * (voice_bandwidth_thresholds[i] - music_bandwidth_thresholds[i])) >> 14);
            }
            do
            {
                int threshold, hysteresis;
                threshold = bandwidth_thresholds[2 * (bandwidth - OPUS_BANDWIDTH_MEDIUMBAND)];
                hysteresis = bandwidth_thresholds[(2 * (bandwidth - OPUS_BANDWIDTH_MEDIUMBAND)) + 1];
                if (this.first == 0)
                {
                    if (this.auto_bandwidth >= bandwidth)
                    {
                        threshold -= hysteresis;
                    }
                    else
                    {
                        threshold += hysteresis;
                    }
                }
                if (equiv_rate >= threshold)
                {
                    break;
                }
            }
            while (--bandwidth > OPUS_BANDWIDTH_NARROWBAND);
            /* We don't use mediumband anymore, except when explicitly requested or during
               mode transitions. */
            if (bandwidth == OPUS_BANDWIDTH_MEDIUMBAND)
            {
                bandwidth = OPUS_BANDWIDTH_WIDEBAND;
            }
            this.bandwidth = this.auto_bandwidth = bandwidth;
            /* Prevents any transition to SWB/FB until the SILK layer has fully
               switched to WB mode and turned the variable LP filter off */
            if (this.first == 0 && this.mode != MODE_CELT_ONLY && this.silk_mode.inWBmodeWithoutVariableLP == 0 && this.bandwidth > OPUS_BANDWIDTH_WIDEBAND)
            {
                this.bandwidth = OPUS_BANDWIDTH_WIDEBAND;
            }
        }

        if (this.bandwidth > this.max_bandwidth)
        {
            this.bandwidth = this.max_bandwidth;
        }

        if (this.user_bandwidth != OPUS_AUTO)
        {
            this.bandwidth = this.user_bandwidth;
        }

        /* This prevents us from using hybrid at unsafe CBR/max rates */
        if (this.mode != MODE_CELT_ONLY && max_rate < 15000)
        {
            this.bandwidth = IMIN(this.bandwidth, OPUS_BANDWIDTH_WIDEBAND);
        }

        /* Prevents Opus from wasting bits on frequencies that are above
           the Nyquist rate of the input signal */
        if (this.Fs <= 24000 && this.bandwidth > OPUS_BANDWIDTH_SUPERWIDEBAND)
        {
            this.bandwidth = OPUS_BANDWIDTH_SUPERWIDEBAND;
        }
        if (this.Fs <= 16000 && this.bandwidth > OPUS_BANDWIDTH_WIDEBAND)
        {
            this.bandwidth = OPUS_BANDWIDTH_WIDEBAND;
        }
        if (this.Fs <= 12000 && this.bandwidth > OPUS_BANDWIDTH_MEDIUMBAND)
        {
            this.bandwidth = OPUS_BANDWIDTH_MEDIUMBAND;
        }
        if (this.Fs <= 8000 && this.bandwidth > OPUS_BANDWIDTH_NARROWBAND)
        {
            this.bandwidth = OPUS_BANDWIDTH_NARROWBAND;
        }
        /* Use detected bandwidth to reduce the encoded bandwidth. */
        if (this.detected_bandwidth != 0 && this.user_bandwidth == OPUS_AUTO)
        {
            int min_detected_bandwidth;
            /* Makes bandwidth detection more conservative just in case the detector
               gets it wrong when we could have coded a high bandwidth transparently.
               When operating in SILK/hybrid mode, we don't go below wideband to avoid
               more complicated switches that require redundancy. */
            if (equiv_rate <= 18000 * this.stream_channels && this.mode == MODE_CELT_ONLY)
            {
                min_detected_bandwidth = OPUS_BANDWIDTH_NARROWBAND;
            }
            else if (equiv_rate <= 24000 * this.stream_channels && this.mode == MODE_CELT_ONLY)
            {
                min_detected_bandwidth = OPUS_BANDWIDTH_MEDIUMBAND;
            }
            else if (equiv_rate <= 30000 * this.stream_channels)
            {
                min_detected_bandwidth = OPUS_BANDWIDTH_WIDEBAND;
            }
            else if (equiv_rate <= 44000 * this.stream_channels)
            {
                min_detected_bandwidth = OPUS_BANDWIDTH_SUPERWIDEBAND;
            }
            else
            {
                min_detected_bandwidth = OPUS_BANDWIDTH_FULLBAND;
            }

            this.detected_bandwidth = IMAX(this.detected_bandwidth, min_detected_bandwidth);
            this.bandwidth = IMIN(this.bandwidth, this.detected_bandwidth);
        }
        this.silk_mode.LBRR_coded = decide_fec(this.silk_mode.useInBandFEC, this.silk_mode.packetLossPercentage,
              this.silk_mode.LBRR_coded, this.mode, ref this.bandwidth, equiv_rate);
        if (this.application != OPUS_APPLICATION_RESTRICTED_SILK)
        {
            this.celt_enc!.SetLsbDepth(lsb_depth);
        }

        /* CELT mode doesn't support mediumband, use wideband instead */
        if (this.mode == MODE_CELT_ONLY && this.bandwidth == OPUS_BANDWIDTH_MEDIUMBAND)
        {
            this.bandwidth = OPUS_BANDWIDTH_WIDEBAND;
        }
        if (this.lfe != 0)
        {
            this.bandwidth = OPUS_BANDWIDTH_NARROWBAND;
        }

        curr_bandwidth = this.bandwidth;

        if (this.application == OPUS_APPLICATION_RESTRICTED_SILK && curr_bandwidth > OPUS_BANDWIDTH_WIDEBAND)
        {
            this.bandwidth = curr_bandwidth = OPUS_BANDWIDTH_WIDEBAND;
        }
        /* Chooses the appropriate mode for speech
           *NEVER* switch to/from CELT-only mode here as this will invalidate some assumptions */
        if (this.mode == MODE_SILK_ONLY && curr_bandwidth > OPUS_BANDWIDTH_WIDEBAND)
        {
            this.mode = MODE_HYBRID;
        }
        if (this.mode == MODE_HYBRID && curr_bandwidth <= OPUS_BANDWIDTH_WIDEBAND)
        {
            this.mode = MODE_SILK_ONLY;
        }

        /* Can't support higher than >60 ms frames, and >20 ms when in Hybrid or CELT-only modes */
        if ((frame_size > this.Fs / 50 && (this.mode != MODE_SILK_ONLY)) || frame_size > 3 * this.Fs / 50)
        {
            int enc_frame_size;
            int nb_frames;
            int max_header_bytes;
            int repacketize_len;
            int max_len_sum;
            int tot_size = 0;
            int curr_data;
            int tmp_len;
            int dtx_count = 0;
            int bak_to_mono;

            if (this.mode == MODE_SILK_ONLY)
            {
                if (frame_size == 2 * this.Fs / 25)  /* 80 ms -> 2x 40 ms */
                {
                    enc_frame_size = this.Fs / 25;
                }
                else if (frame_size == 3 * this.Fs / 25)  /* 120 ms -> 2x 60 ms */
                {
                    enc_frame_size = 3 * this.Fs / 50;
                }
                else                            /* 100 ms -> 5x 20 ms */
                {
                    enc_frame_size = this.Fs / 50;
                }
            }
            else
            {
                enc_frame_size = this.Fs / 50;
            }

            nb_frames = frame_size / enc_frame_size;

            if (analysis_read_pos_bak != -1)
            {
                /* Reset analysis position to the beginning of the first frame so we
                   can use it one frame at a time. */
                this.analysis.read_pos = analysis_read_pos_bak;
                this.analysis.read_subframe = analysis_read_subframe_bak;
            }

            /* Worst cases:
             * 2 frames: Code 2 with different compressed sizes
             * >2 frames: Code 3 VBR */
            max_header_bytes = nb_frames == 2 ? 3 : (2 + ((nb_frames - 1) * 2));

            if (this.use_vbr != 0 || this.user_bitrate_bps == OPUS_BITRATE_MAX)
            {
                repacketize_len = out_data_bytes;
            }
            else
            {
                System.Diagnostics.Debug.Assert(cbr_bytes >= 0);
                repacketize_len = IMIN(cbr_bytes, out_data_bytes);
            }
            max_len_sum = nb_frames + repacketize_len - max_header_bytes;

            /* tmp_data: this.tmp_data (see the PORT NOTE) */
            curr_data = 0;
            Repacketizer.opus_repacketizer_init(this.rp);

            bak_to_mono = this.silk_mode.toMono;
            if (bak_to_mono != 0)
            {
                this.force_channels = 1;
            }
            else
            {
                this.prev_channels = this.stream_channels;
            }

            for (i = 0; i < nb_frames; i++)
            {
                int first_frame;
                int frame_to_celt;
                int frame_redundancy;
                int curr_max;
                /* Attempt DRED encoding until we have a non-DTX frame. In case of DTX refresh,
                   that allows for DRED not to be in the first frame. */
                first_frame = (i == 0) || (i == dtx_count) ? 1 : 0;
                this.silk_mode.toMono = 0;
                this.nonfinal_frame = i < (nb_frames - 1) ? 1 : 0;

                /* When switching from SILK/Hybrid to CELT, only ask for a switch at the last frame */
                frame_to_celt = (to_celt != 0 && i == nb_frames - 1) ? 1 : 0;
                frame_redundancy = (redundancy != 0 && (frame_to_celt != 0 || (to_celt == 0 && i == 0))) ? 1 : 0;

                curr_max = IMIN(bitrate_to_bits(this.bitrate_bps, this.Fs, enc_frame_size) / 8, max_len_sum / nb_frames);
                curr_max = IMIN(max_len_sum - tot_size, curr_max);
                if (analysis_read_pos_bak != -1)
                {
                    /* Get analysis for current frame. */
                    Analysis.tonality_get_info(this.analysis, ref analysis_info, enc_frame_size);
                }
                is_silence = is_digital_silence(pcm.Slice(i * (this.channels * enc_frame_size)), enc_frame_size, this.channels, lsb_depth);

                System.Diagnostics.Debug.Assert(curr_data + curr_max <= MAX_TMP_DATA);
                tmp_len = this.opus_encode_frame_native(pcm.Slice(i * (this.channels * enc_frame_size)), enc_frame_size, this.tmp_data.AsSpan(curr_data), curr_max, float_api, first_frame,
                          in analysis_info,
                          is_silence, frame_redundancy, celt_to_silk, prefill,
                          equiv_rate, frame_to_celt);
                if (tmp_len < 0)
                {
                    return OPUS_INTERNAL_ERROR;
                }
                else if (tmp_len == 1)
                {
                    dtx_count++;
                }
                ret = Repacketizer.opus_repacketizer_cat(this.rp, this.tmp_data.AsMemory(curr_data, tmp_len), tmp_len);

                if (ret < 0)
                {
                    return OPUS_INTERNAL_ERROR;
                }
                tot_size += tmp_len;
                curr_data += tmp_len;
            }
            ret = Repacketizer.opus_repacketizer_out_range_impl(this.rp, 0, nb_frames, data, repacketize_len, 0, (this.use_vbr == 0 && (dtx_count != nb_frames)) ? 1 : 0, ReadOnlySpan<opus_extension_data>.Empty, 0);
            if (ret < 0)
            {
                ret = OPUS_INTERNAL_ERROR;
            }
            this.silk_mode.toMono = bak_to_mono;
            return ret;
        }
        else
        {
            ret = this.opus_encode_frame_native(pcm, frame_size, data, max_data_bytes, float_api, 1,
                    in analysis_info,
                    is_silence, redundancy, celt_to_silk, prefill,
                    equiv_rate, to_celt);
            return ret;
        }
    }

    private int opus_encode_frame_native(ReadOnlySpan<int> pcm, int frame_size,
                    Span<byte> data, int orig_max_data_bytes,
                    int float_api, int first_frame,
                    in AnalysisInfo analysis_info,
                    int is_silence, int redundancy, int celt_to_silk, int prefill,
                    int equiv_rate, int to_celt)
    {
        OpusCustomEncoder? celt_enc = null;
        OpusCustomMode? celt_mode = null;
        int i;
        int ret = 0;
        int max_data_bytes;
        int nBytes = 0;
        ec_enc enc = default;
        ec_enc no_enc = default; // for C's NULL ec_enc
        int bits_target;
        int start_band = 0;
        int redundancy_bytes = 0; /* Number of bytes to use for redundancy frame */
        int nb_compr_bytes;
        uint redundant_rng = 0;
        int cutoff_Hz;
        int hp_freq_smth1;
        short HB_gain;
        int apply_padding;
        int frame_rate;
        int curr_bandwidth;
        int delay_compensation;
        int total_buffer;
        int activity = VAD_NO_DECISION;
        Span<byte> dummy2 = this.dummy2;

        max_data_bytes = IMIN(orig_max_data_bytes, 1276);
        this.rangeFinal = 0;
        silk_encoder? silk_enc = this.silk_enc;
        if (this.application != OPUS_APPLICATION_RESTRICTED_SILK)
        {
            celt_enc = this.celt_enc!;
            celt_mode = celt_enc.GetMode();
        }
        curr_bandwidth = this.bandwidth;
        if (this.application == OPUS_APPLICATION_RESTRICTED_LOWDELAY || this.application == OPUS_APPLICATION_RESTRICTED_CELT || this.application == OPUS_APPLICATION_RESTRICTED_SILK)
        {
            delay_compensation = 0;
        }
        else
        {
            delay_compensation = this.delay_compensation;
        }
        total_buffer = delay_compensation;

        frame_rate = this.Fs / frame_size;

        if (is_silence != 0)
        {
            activity = is_silence == 0 ? 1 : 0;
        }
        else if (analysis_info.valid != 0)
        {
            activity = analysis_info.activity_probability >= DTX_ACTIVITY_THRESHOLD ? 1 : 0;
            if (activity == 0)
            {
                /* Mark as active if this noise frame is sufficiently loud */
                int noise_energy = compute_frame_energy(pcm, frame_size, this.channels);
                activity = this.peak_signal_energy < (PSEUDO_SNR_THRESHOLD * noise_energy) ? 1 : 0;
            }
        }
        else if (this.mode == MODE_CELT_ONLY)
        {
            int noise_energy = compute_frame_energy(pcm, frame_size, this.channels);
            /* Boosting peak energy a bit because we didn't just average the active frames. */
            activity = this.peak_signal_energy < (316 /* QCONST16(PSEUDO_SNR_THRESHOLD, 0) */ * (long)HALF32(noise_energy)) ? 1 : 0;
        }

        /* For the first frame at a new SILK bandwidth */
        if (this.silk_bw_switch != 0)
        {
            redundancy = 1;
            celt_to_silk = 1;
            this.silk_bw_switch = 0;
            /* Do a prefill without resetting the sampling rate control. */
            prefill = 2;
        }

        /* If we decided to go with CELT, make sure redundancy is off, no matter what
           we decided earlier. */
        if (this.mode == MODE_CELT_ONLY)
        {
            redundancy = 0;
        }

        if (redundancy != 0)
        {
            redundancy_bytes = compute_redundancy_bytes(max_data_bytes, this.bitrate_bps, frame_rate, this.stream_channels);
            if (redundancy_bytes == 0)
            {
                redundancy = 0;
            }
        }
        if (this.application == OPUS_APPLICATION_RESTRICTED_SILK)
        {
            redundancy = 0;
            redundancy_bytes = 0;
        }

        bits_target = IMIN(8 * (max_data_bytes - redundancy_bytes), bitrate_to_bits(this.bitrate_bps, this.Fs, frame_size)) - 8;

        // C's data += 1: data1 is the packet after the ToC byte, data[-1] is data[0]
        Span<byte> data1 = data.Slice(1);

        enc.ec_enc_init(data1, (uint)(orig_max_data_bytes - 1));

        Span<int> pcm_buf = this.pcm_buf_mem.AsSpan(0, (total_buffer + frame_size) * this.channels);
        this.delay_buffer.AsSpan((this.encoder_buffer - total_buffer) * this.channels, total_buffer * this.channels).CopyTo(pcm_buf);

        if (this.mode == MODE_CELT_ONLY)
        {
            hp_freq_smth1 = silk_LSHIFT(Lin2log.silk_lin2log(VARIABLE_HP_MIN_CUTOFF_HZ), 8);
        }
        else
        {
            hp_freq_smth1 = silk_enc!.state_Fxx[0].sCmn.variable_HP_smth1_Q15;
        }

        this.variable_HP_smth2_Q15 = silk_SMLAWB(this.variable_HP_smth2_Q15,
              hp_freq_smth1 - this.variable_HP_smth2_Q15, (int)((VARIABLE_HP_SMTH_COEF2 * (1L << 16)) + 0.5) /* SILK_FIX_CONST( VARIABLE_HP_SMTH_COEF2, 16 ) */);

        /* convert from log scale to Hertz */
        cutoff_Hz = Log2lin.silk_log2lin(silk_RSHIFT(this.variable_HP_smth2_Q15, 8));

        if (this.application == OPUS_APPLICATION_VOIP)
        {
            hp_cutoff(pcm, cutoff_Hz, pcm_buf.Slice(total_buffer * this.channels), this.hp_mem, frame_size, this.channels, this.Fs);
        }
        else
        {
            dc_reject(pcm, 3, pcm_buf.Slice(total_buffer * this.channels), this.hp_mem, frame_size, this.channels, this.Fs);
        }
        _ = float_api;

        /* SILK processing */
        HB_gain = Q15ONE;
        if (this.mode != MODE_CELT_ONLY)
        {
            int total_bitRate, celt_rate;
            ReadOnlySpan<int> pcm_silk;

            /* Distribute bits between SILK and CELT */
            total_bitRate = bits_to_bitrate(bits_target, this.Fs, frame_size);
            if (this.mode == MODE_HYBRID)
            {
                /* Base rate for SILK */
                this.silk_mode.bitRate = compute_silk_rate_for_hybrid(total_bitRate,
                      curr_bandwidth, this.Fs == 50 * frame_size ? 1 : 0, this.use_vbr, this.silk_mode.LBRR_coded,
                      this.stream_channels);
                if (this.energy_masking == null)
                {
                    /* Increasingly attenuate high band when it gets allocated fewer bits */
                    celt_rate = total_bitRate - this.silk_mode.bitRate;
                    HB_gain = (short)(Q15ONE - SHR32(MathOps.celt_exp2((short)(-celt_rate * 1 /* QCONST16(1.f/1024, 10) */)), 1));
                }
            }
            else
            {
                /* SILK gets all bits */
                this.silk_mode.bitRate = total_bitRate;
            }

            /* Surround masking for SILK */
            if (this.energy_masking != null && this.use_vbr != 0 && this.lfe == 0)
            {
                int mask_sum = 0;
                int masking_depth;
                int rate_offset;
                int c;
                int end = 17;
                short srate = 16000;
                if (this.bandwidth == OPUS_BANDWIDTH_NARROWBAND)
                {
                    end = 13;
                    srate = 8000;
                }
                else if (this.bandwidth == OPUS_BANDWIDTH_MEDIUMBAND)
                {
                    end = 15;
                    srate = 12000;
                }
                for (c = 0; c < this.channels; c++)
                {
                    for (i = 0; i < end; i++)
                    {
                        int mask;
                        mask = MAXG(MING(this.energy_masking[(21 * c) + i],
                               8388608 /* GCONST(.5f) */), -33554432 /* -GCONST(2.0f) */);
                        if (mask > 0)
                        {
                            mask = HALF32(mask);
                        }
                        mask_sum += mask;
                    }
                }
                /* Conservative rate reduction, we cut the masking in half */
                masking_depth = mask_sum / end * this.channels;
                masking_depth += 3355443 /* GCONST(.2f) */;
                rate_offset = PSHR32(MULT16_16(srate, SHR32(masking_depth, DB_SHIFT - 10)), 10);
                rate_offset = MAX32(rate_offset, -2 * this.silk_mode.bitRate / 3);
                /* Split the rate change between the SILK and CELT part for hybrid. */
                if (this.bandwidth == OPUS_BANDWIDTH_SUPERWIDEBAND || this.bandwidth == OPUS_BANDWIDTH_FULLBAND)
                {
                    this.silk_mode.bitRate += 3 * rate_offset / 5;
                }
                else
                {
                    this.silk_mode.bitRate += rate_offset;
                }
            }

            this.silk_mode.payloadSize_ms = 1000 * frame_size / this.Fs;
            this.silk_mode.nChannelsAPI = this.channels;
            this.silk_mode.nChannelsInternal = this.stream_channels;
            if (curr_bandwidth == OPUS_BANDWIDTH_NARROWBAND)
            {
                this.silk_mode.desiredInternalSampleRate = 8000;
            }
            else if (curr_bandwidth == OPUS_BANDWIDTH_MEDIUMBAND)
            {
                this.silk_mode.desiredInternalSampleRate = 12000;
            }
            else
            {
                System.Diagnostics.Debug.Assert(this.mode == MODE_HYBRID || curr_bandwidth == OPUS_BANDWIDTH_WIDEBAND);
                this.silk_mode.desiredInternalSampleRate = 16000;
            }
            if (this.mode == MODE_HYBRID)
            {
                /* Don't allow bandwidth reduction at lowest bitrates in hybrid mode */
                this.silk_mode.minInternalSampleRate = 16000;
            }
            else
            {
                this.silk_mode.minInternalSampleRate = 8000;
            }

            this.silk_mode.maxInternalSampleRate = 16000;
            if (this.mode == MODE_SILK_ONLY)
            {
                int effective_max_rate = bits_to_bitrate(max_data_bytes * 8, this.Fs, frame_size);
                if (frame_rate > 50)
                {
                    effective_max_rate = effective_max_rate * 2 / 3;
                }
                if (effective_max_rate < 8000)
                {
                    this.silk_mode.maxInternalSampleRate = 12000;
                    this.silk_mode.desiredInternalSampleRate = IMIN(12000, this.silk_mode.desiredInternalSampleRate);
                }
                if (effective_max_rate < 7000)
                {
                    this.silk_mode.maxInternalSampleRate = 8000;
                    this.silk_mode.desiredInternalSampleRate = IMIN(8000, this.silk_mode.desiredInternalSampleRate);
                }
            }

            this.silk_mode.useCBR = this.use_vbr == 0 ? 1 : 0;

            /* Call SILK encoder for the low band */

            /* Max bits for SILK, counting ToC, redundancy bytes, and optionally redundancy. */
            this.silk_mode.maxBits = (max_data_bytes - 1) * 8;
            if (redundancy != 0 && redundancy_bytes >= 2)
            {
                /* Counting 1 bit for redundancy position and 20 bits for flag+size (only for hybrid). */
                this.silk_mode.maxBits -= (redundancy_bytes * 8) + 1;
                if (this.mode == MODE_HYBRID)
                {
                    this.silk_mode.maxBits -= 20;
                }
            }
            if (this.silk_mode.useCBR != 0)
            {
                /* When we're in CBR mode, but we have non-SILK data to encode, switch SILK to VBR with cap to
                   save on complexity. Any variations will be absorbed by CELT and/or DRED and we can still
                   produce a constant bitrate without wasting bits. */
                if (this.mode == MODE_HYBRID)
                {
                    /* Allow SILK to steal up to 25% of the remaining bits */
                    short other_bits = (short)IMAX(0, this.silk_mode.maxBits - (this.silk_mode.bitRate * frame_size / this.Fs));
                    this.silk_mode.maxBits = IMAX(0, this.silk_mode.maxBits - (other_bits * 3 / 4));
                    this.silk_mode.useCBR = 0;
                }
            }
            else
            {
                /* Constrained VBR. */
                if (this.mode == MODE_HYBRID)
                {
                    /* Compute SILK bitrate corresponding to the max total bits available */
                    int maxBitRate = compute_silk_rate_for_hybrid(this.silk_mode.maxBits * this.Fs / frame_size,
                          curr_bandwidth, this.Fs == 50 * frame_size ? 1 : 0, this.use_vbr, this.silk_mode.LBRR_coded,
                          this.stream_channels);
                    this.silk_mode.maxBits = bitrate_to_bits(maxBitRate, this.Fs, frame_size);
                }
            }

            if (prefill != 0 && this.application != OPUS_APPLICATION_RESTRICTED_SILK)
            {
                int zero = 0;
                int prefill_offset;
                /* Use a smooth onset for the SILK prefill to avoid the encoder trying to encode
                   a discontinuity. The exact location is what we need to avoid leaving any "gap"
                   in the audio when mixing with the redundant CELT frame. Here we can afford to
                   overwrite st->delay_buffer because the only thing that uses it before it gets
                   rewritten is tmp_prefill[] and even then only the part after the ramp really
                   gets used (rather than sent to the encoder and discarded) */
                prefill_offset = this.channels * (this.encoder_buffer - this.delay_compensation - (this.Fs / 400));
                gain_fade(this.delay_buffer.AsSpan(prefill_offset), this.delay_buffer.AsSpan(prefill_offset),
                      0, Q15ONE, celt_mode!.overlap, this.Fs / 400, this.channels, celt_mode.window, this.Fs);
                this.delay_buffer.AsSpan(0, prefill_offset).Clear();
                pcm_silk = this.delay_buffer;
                EncAPI.silk_Encode(silk_enc!, this.silk_mode, pcm_silk, this.encoder_buffer, ref no_enc, false, ref zero, prefill, activity);
                /* Prevent a second switch in the real encode call. */
                this.silk_mode.opusCanSwitch = 0;
            }

            pcm_silk = pcm_buf.Slice(total_buffer * this.channels);
            ret = EncAPI.silk_Encode(silk_enc!, this.silk_mode, pcm_silk, frame_size, ref enc, true, ref nBytes, 0, activity);
            if (ret != 0)
            {
                /* Handle error */
                return OPUS_INTERNAL_ERROR;
            }

            /* Extract SILK internal bandwidth for signaling in first byte */
            if (this.mode == MODE_SILK_ONLY)
            {
                if (this.silk_mode.internalSampleRate == 8000)
                {
                    curr_bandwidth = OPUS_BANDWIDTH_NARROWBAND;
                }
                else if (this.silk_mode.internalSampleRate == 12000)
                {
                    curr_bandwidth = OPUS_BANDWIDTH_MEDIUMBAND;
                }
                else if (this.silk_mode.internalSampleRate == 16000)
                {
                    curr_bandwidth = OPUS_BANDWIDTH_WIDEBAND;
                }
            }
            else
            {
                System.Diagnostics.Debug.Assert(this.silk_mode.internalSampleRate == 16000);
            }

            this.silk_mode.opusCanSwitch = (this.silk_mode.switchReady != 0 && this.nonfinal_frame == 0) ? 1 : 0;

            if (activity == VAD_NO_DECISION)
            {
                activity = (this.silk_mode.signalType != Define.TYPE_NO_VOICE_ACTIVITY) ? 1 : 0;
            }
            if (nBytes == 0)
            {
                this.rangeFinal = 0;
                data[0] = gen_toc(this.mode, this.Fs / frame_size, curr_bandwidth, this.stream_channels);
                return 1;
            }

            /* FIXME: How do we allocate the redundancy for CBR? */
            if (this.silk_mode.opusCanSwitch != 0)
            {
                if (this.application != OPUS_APPLICATION_RESTRICTED_SILK)
                {
                    redundancy_bytes = compute_redundancy_bytes(max_data_bytes, this.bitrate_bps, frame_rate, this.stream_channels);
                    redundancy = (redundancy_bytes != 0) ? 1 : 0;
                }
                celt_to_silk = 0;
                this.silk_bw_switch = 1;
            }
        }

        /* CELT processing */
        if (this.application != OPUS_APPLICATION_RESTRICTED_SILK)
        {
            int endband = 21;

            switch (curr_bandwidth)
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
            }
            celt_enc!.SetEndBand(endband);
            celt_enc.SetChannels(this.stream_channels);
            celt_enc.SetBitrate(OPUS_BITRATE_MAX);
        }
        if (this.mode != MODE_SILK_ONLY)
        {
            int celt_pred = 2;
            /* We may still decide to disable prediction later */
            if (this.silk_mode.reducedDependency != 0)
            {
                celt_pred = 0;
            }
            celt_enc!.SetPrediction(celt_pred);
        }

        Span<int> tmp_prefill = this.tmp_prefill_mem.AsSpan(0, this.channels * this.Fs / 400);
        if (this.mode != MODE_SILK_ONLY && this.mode != this.prev_mode && this.prev_mode > 0
              && this.application != OPUS_APPLICATION_RESTRICTED_CELT)
        {
            this.delay_buffer.AsSpan((this.encoder_buffer - total_buffer - (this.Fs / 400)) * this.channels, this.channels * this.Fs / 400).CopyTo(tmp_prefill);
        }

        if (this.channels * (this.encoder_buffer - (frame_size + total_buffer)) > 0)
        {
            this.delay_buffer.AsSpan(this.channels * frame_size, this.channels * (this.encoder_buffer - frame_size - total_buffer)).CopyTo(this.delay_buffer);
            pcm_buf.Slice(0, (frame_size + total_buffer) * this.channels).CopyTo(
                  this.delay_buffer.AsSpan(this.channels * (this.encoder_buffer - frame_size - total_buffer)));
        }
        else
        {
            pcm_buf.Slice((frame_size + total_buffer - this.encoder_buffer) * this.channels, this.encoder_buffer * this.channels).CopyTo(this.delay_buffer);
        }
        /* gain_fade() and stereo_fade() need to be after the buffer copying
           because we don't want any of this to affect the SILK part */
        if ((this.prev_HB_gain < Q15ONE || HB_gain < Q15ONE) && celt_mode != null)
        {
            gain_fade(pcm_buf, pcm_buf,
                  this.prev_HB_gain, HB_gain, celt_mode.overlap, frame_size, this.channels, celt_mode.window, this.Fs);
        }
        this.prev_HB_gain = HB_gain;
        if (this.mode != MODE_HYBRID || this.stream_channels == 1)
        {
            if (equiv_rate > 32000)
            {
                this.silk_mode.stereoWidth_Q14 = 16384;
            }
            else if (equiv_rate < 16000)
            {
                this.silk_mode.stereoWidth_Q14 = 0;
            }
            else
            {
                this.silk_mode.stereoWidth_Q14 = 16384 - (2048 * (32000 - equiv_rate) / (equiv_rate - 14000));
            }
        }
        if (this.energy_masking == null && this.channels == 2)
        {
            /* Apply stereo width reduction (at low bitrates) */
            if (this.hybrid_stereo_width_Q14 < (1 << 14) || this.silk_mode.stereoWidth_Q14 < (1 << 14))
            {
                short g1, g2;
                g1 = this.hybrid_stereo_width_Q14;
                g2 = (short)this.silk_mode.stereoWidth_Q14;
                g1 = g1 == 16384 ? (short)Q15ONE : SHL16(g1, 1);
                g2 = g2 == 16384 ? (short)Q15ONE : SHL16(g2, 1);
                if (celt_mode != null)
                {
                    stereo_fade(pcm_buf, pcm_buf, g1, g2, celt_mode.overlap,
                         frame_size, this.channels, celt_mode.window, this.Fs);
                }
                this.hybrid_stereo_width_Q14 = (short)this.silk_mode.stereoWidth_Q14;
            }
        }

        if (this.mode != MODE_CELT_ONLY && enc.ec_tell() + 17 + (20 * (this.mode == MODE_HYBRID ? 1 : 0)) <= 8 * (max_data_bytes - 1))
        {
            /* For SILK mode, the redundancy is inferred from the length */
            if (this.mode == MODE_HYBRID)
            {
                enc.ec_enc_bit_logp(redundancy, 12);
            }
            if (redundancy != 0)
            {
                int max_redundancy;
                enc.ec_enc_bit_logp(celt_to_silk, 1);
                if (this.mode == MODE_HYBRID)
                {
                    /* Reserve the 8 bits needed for the redundancy length,
                       and at least a few bits for CELT if possible */
                    max_redundancy = (max_data_bytes - 1) - ((enc.ec_tell() + 8 + 3 + 7) >> 3);
                }
                else
                {
                    max_redundancy = (max_data_bytes - 1) - ((enc.ec_tell() + 7) >> 3);
                }
                /* Target the same bit-rate for redundancy as for the rest,
                   up to a max of 257 bytes */
                redundancy_bytes = IMIN(max_redundancy, redundancy_bytes);
                redundancy_bytes = IMIN(257, IMAX(2, redundancy_bytes));
                if (this.mode == MODE_HYBRID)
                {
                    enc.ec_enc_uint((uint)(redundancy_bytes - 2), 256);
                }
            }
        }
        else
        {
            redundancy = 0;
        }

        if (redundancy == 0)
        {
            this.silk_bw_switch = 0;
            redundancy_bytes = 0;
        }
        if (this.mode != MODE_CELT_ONLY)
        {
            start_band = 17;
        }

        if (this.mode == MODE_SILK_ONLY)
        {
            ret = (enc.ec_tell() + 7) >> 3;
            enc.ec_enc_done();
            nb_compr_bytes = ret;
        }
        else
        {
            nb_compr_bytes = (max_data_bytes - 1) - redundancy_bytes;
            enc.ec_enc_shrink((uint)nb_compr_bytes);
        }

        if (redundancy != 0 || this.mode != MODE_SILK_ONLY)
        {
            celt_enc!.SetAnalysis(in analysis_info);
        }
        if (this.mode == MODE_HYBRID)
        {
            SILKInfo info;
            info.signalType = this.silk_mode.signalType;
            info.offset = this.silk_mode.offset;
            celt_enc!.SetSilkInfo(in info);
        }

        /* 5 ms redundant frame for CELT->SILK */
        if (redundancy != 0 && celt_to_silk != 0)
        {
            int err;
            celt_enc!.SetStartBand(0);
            celt_enc.SetVBR(0);
            celt_enc.SetBitrate(OPUS_BITRATE_MAX);
            err = CeltEncoder.celt_encode_with_ec(celt_enc, pcm_buf, this.Fs / 200, data1.Slice(nb_compr_bytes), redundancy_bytes, ref no_enc, false);
            if (err < 0)
            {
                return OPUS_INTERNAL_ERROR;
            }
            redundant_rng = celt_enc.GetFinalRange();
            celt_enc.ResetState();
        }

        if (this.application != OPUS_APPLICATION_RESTRICTED_SILK)
        {
            celt_enc!.SetStartBand(start_band);
        }

        data[0] = 0;
        if (this.mode != MODE_SILK_ONLY)
        {
            celt_enc!.SetVBR(this.use_vbr);
            if (this.mode == MODE_HYBRID)
            {
                if (this.use_vbr != 0)
                {
                    celt_enc.SetBitrate(this.bitrate_bps - this.silk_mode.bitRate);
                    celt_enc.SetVBRConstraint(0);
                }
            }
            else
            {
                if (this.use_vbr != 0)
                {
                    celt_enc.SetVBR(1);
                    celt_enc.SetVBRConstraint(this.vbr_constraint);
                    celt_enc.SetBitrate(this.bitrate_bps);
                }
            }
            if (this.mode != this.prev_mode && this.prev_mode > 0 && this.application != OPUS_APPLICATION_RESTRICTED_CELT)
            {
                celt_enc.ResetState();

                /* Prefilling */
                CeltEncoder.celt_encode_with_ec(celt_enc, tmp_prefill, this.Fs / 400, dummy2, 2, ref no_enc, false);
                celt_enc.SetPrediction(0);
            }
            /* If false, we already busted the budget and we'll end up with a "PLC frame" */
            if (enc.ec_tell() <= 8 * nb_compr_bytes)
            {
                ret = CeltEncoder.celt_encode_with_ec(celt_enc, pcm_buf, frame_size, Span<byte>.Empty, nb_compr_bytes, ref enc, true);
                if (ret < 0)
                {
                    return OPUS_INTERNAL_ERROR;
                }
                /* Put CELT->SILK redundancy data in the right place. */
                if (redundancy != 0 && celt_to_silk != 0 && this.mode == MODE_HYBRID && nb_compr_bytes != ret)
                {
                    data1.Slice(nb_compr_bytes, redundancy_bytes).CopyTo(data1.Slice(ret));
                    nb_compr_bytes = ret + redundancy_bytes;
                }
            }
            this.rangeFinal = celt_enc.GetFinalRange();
        }
        else
        {
            this.rangeFinal = enc.rng;
        }

        /* 5 ms redundant frame for SILK->CELT */
        if (redundancy != 0 && celt_to_silk == 0)
        {
            int err;
            int N2, N4;
            N2 = this.Fs / 200;
            N4 = this.Fs / 400;

            celt_enc!.ResetState();
            celt_enc.SetStartBand(0);
            celt_enc.SetPrediction(0);
            celt_enc.SetVBR(0);
            celt_enc.SetBitrate(OPUS_BITRATE_MAX);

            if (this.mode == MODE_HYBRID)
            {
                /* Shrink packet to what the encoder actually used. */
                nb_compr_bytes = ret;
                enc.ec_enc_shrink((uint)nb_compr_bytes);
            }
            /* NOTE: We could speed this up slightly (at the expense of code size) by just adding a function that prefills the buffer */
            CeltEncoder.celt_encode_with_ec(celt_enc, pcm_buf.Slice(this.channels * (frame_size - N2 - N4)), N4, dummy2, 2, ref no_enc, false);

            err = CeltEncoder.celt_encode_with_ec(celt_enc, pcm_buf.Slice(this.channels * (frame_size - N2)), N2, data1.Slice(nb_compr_bytes), redundancy_bytes, ref no_enc, false);
            if (err < 0)
            {
                return OPUS_INTERNAL_ERROR;
            }
            redundant_rng = celt_enc.GetFinalRange();
        }

        /* Signalling the mode in the first byte */
        data[0] |= gen_toc(this.mode, this.Fs / frame_size, curr_bandwidth, this.stream_channels);

        this.rangeFinal ^= redundant_rng;

        if (to_celt != 0)
        {
            this.prev_mode = MODE_CELT_ONLY;
        }
        else
        {
            this.prev_mode = this.mode;
        }
        this.prev_channels = this.stream_channels;
        this.prev_framesize = frame_size;

        this.first = 0;

        /* DTX decision */
        if (this.use_dtx != 0 && this.silk_mode.useDTX == 0)
        {
            if (decide_dtx_mode(activity, ref this.nb_no_activity_ms_Q1, 2 * 1000 * frame_size / this.Fs) != 0)
            {
                this.rangeFinal = 0;
                data[0] = gen_toc(this.mode, this.Fs / frame_size, curr_bandwidth, this.stream_channels);
                return 1;
            }
        }
        else
        {
            this.nb_no_activity_ms_Q1 = 0;
        }

        /* In the unlikely case that the SILK encoder busted its target, tell
           the decoder to call the PLC */
        if (enc.ec_tell() > (max_data_bytes - 1) * 8)
        {
            if (max_data_bytes < 2)
            {
                return OPUS_BUFFER_TOO_SMALL;
            }
            data[1] = 0;
            ret = 1;
            this.rangeFinal = 0;
        }
        else if (this.mode == MODE_SILK_ONLY && redundancy == 0)
        {
            /*When in LPC only mode it's perfectly
              reasonable to strip off trailing zero bytes as
              the required range decoder behavior is to
              fill these in. This can't be done when the MDCT
              modes are used because the decoder needs to know
              the actual length for allocation purposes.*/
            while (ret > 2 && data[ret] == 0)
            {
                ret--;
            }
        }
        /* Count ToC and redundancy */
        ret += 1 + redundancy_bytes;
        apply_padding = this.use_vbr == 0 ? 1 : 0;
        _ = first_frame; /* Avoids a warning about first_frame being unused. */
        if (apply_padding != 0)
        {
            if (Repacketizer.opus_packet_pad(data, ret, orig_max_data_bytes, this.pad_rp) != OPUS_OK)
            {
                return OPUS_INTERNAL_ERROR;
            }
            ret = orig_max_data_bytes;
        }
        return ret;
    }

    /// <summary>
    /// opus_encode_float: encodes a frame of interleaved float samples (frame_size of each channel:
    /// 2.5, 5, 10, 20, 40, 60, 80, 100 or 120 ms) into <paramref name="data"/>, and returns the
    /// packet's length in bytes, or a negative OPUS_* error code, as C does.
    /// </summary>
    internal int Encode(ReadOnlySpan<float> pcm, int frame_size, Span<byte> data)
    {
        return this.opus_encode_float(pcm, frame_size, data, data.Length);
    }

    /// <summary>
    /// opus_encode: as <see cref="Encode(ReadOnlySpan{float}, int, Span{byte})"/>, for interleaved
    /// 16-bit samples (an input depth of 16 bits).
    /// </summary>
    internal int Encode(ReadOnlySpan<short> pcm, int frame_size, Span<byte> data)
    {
        return this.opus_encode(pcm, frame_size, data, data.Length);
    }

    private int opus_encode(ReadOnlySpan<short> pcm, int analysis_frame_size,
          Span<byte> data, int max_data_bytes)
    {
        int i, ret;
        int frame_size;

        frame_size = frame_size_select(this.application, analysis_frame_size, this.variable_duration, this.Fs);
        if (frame_size <= 0)
        {
            return OPUS_BAD_ARG;
        }
        Span<int> @in = this.in_buf.AsSpan(0, frame_size * this.channels);

        for (i = 0; i < frame_size * this.channels; i++)
        {
            @in[i] = INT16TORES(pcm[i]);
        }
        ret = this.opus_encode_native(@in, frame_size, data, max_data_bytes, 16,
                                 MemoryMarshal.AsBytes(pcm), analysis_frame_size, 0, -2, this.channels, downmix_int_func, 1);
        return ret;
    }
    /// <summary>
    /// opus_encode24: as <see cref="Encode(ReadOnlySpan{float}, int, Span{byte})"/>, for interleaved 24-bit samples in ints.
    /// </summary>
    internal int Encode24(ReadOnlySpan<int> pcm, int frame_size, Span<byte> data)
    {
        return this.opus_encode24(pcm, frame_size, data, data.Length);
    }

    private int opus_encode24(ReadOnlySpan<int> pcm, int analysis_frame_size,
                    Span<byte> data, int max_data_bytes)
    {
        int frame_size;
        frame_size = frame_size_select(this.application, analysis_frame_size, this.variable_duration, this.Fs);
        return this.opus_encode_native(pcm, frame_size, data, max_data_bytes, MAX_ENCODING_DEPTH,
                                  MemoryMarshal.AsBytes(pcm), analysis_frame_size, 0, -2, this.channels, downmix_int24_func, 0);
    }

    private int opus_encode_float(ReadOnlySpan<float> pcm, int analysis_frame_size,
          Span<byte> data, int max_data_bytes)
    {
        int i, ret;
        int frame_size;

        frame_size = frame_size_select(this.application, analysis_frame_size, this.variable_duration, this.Fs);
        if (frame_size <= 0)
        {
            return OPUS_BAD_ARG;
        }
        Span<int> @in = this.in_buf.AsSpan(0, frame_size * this.channels);

        for (i = 0; i < frame_size * this.channels; i++)
        {
            @in[i] = FLOAT2RES(pcm[i]);
        }
        ret = this.opus_encode_native(@in, frame_size, data, max_data_bytes, MAX_ENCODING_DEPTH,
                                 MemoryMarshal.AsBytes(pcm), analysis_frame_size, 0, -2, this.channels, downmix_float_func, 1);
        return ret;
    }
}
