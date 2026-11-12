/* Copyright (c) 2007-2008 CSIRO
   Copyright (c) 2007-2010 Xiph.Org Foundation
   Copyright (c) 2008 Gregory Maxwell
   Written by Jean-Marc Valin and Gregory Maxwell
   Ported to C# from libopus 1.6.1 (celt/celt_encoder.c) by Hot Chai Games LLC */
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

/* PORT NOTE: celt_encoder_init() doesn't fail on an unsupported sampling rate in C:
   resampling_factor() asserts (compiled out) and returns 0, leaving upsample at 0. The
   constructor does the same (Debug.Assert only), so such an encoder divides by zero later. */
/* PORT NOTE: CELT_SET_INPUT_CLIPPING is only compiled with CUSTOM_MODES/ENABLE_OPUS_CUSTOM_API;
   SetInputClipping() is provided anyway (the interface asks for it) and sets clip as C would. */
/* PORT NOTE: RESYNTH is not defined in this configuration, so the encoder's resynthesis
   (syn_mem, prefilter_*_old, anti_collapse, celt_synthesis) isn't ported; preemph_memD is kept
   as a field (C has it, it's cleared on reset, nothing reads it). quant_all_bands() still
   resynthesises when theta RDO is on (stereo, complexity >= 8), as C does. */
/* PORT NOTE: celt_encode_with_ec() ALLOCs bandE[] on the stack in C; here it's a buffer the
   encoder owns (bandE_buf), because quant_all_bands() keeps it in its band context and C#'s
   ref-safety rules don't allow a stackalloc'd span there. The large ALLOCs (in, freq, X and
   run_prefilter's _pre) are owned buffers too; the rest are stackalloc. */
/* PORT NOTE: Where C stores into an opus_val16 the port truncates with (short), even where that
   looks unintended: patch_transient_decision()'s x1/x2 (celt_glog in an opus_val16),
   run_prefilter()'s thresh[] and transient_analysis()'s tf_max. */
/* PORT NOTE: The float expressions (analysis->tonality_slope, max_pitch_ratio, activity,
   tonality) are evaluated in float, with double where C's literal is a double (.3, 1.26, .79,
   .4); casts to int/short truncate as C's do. */
/* PORT NOTE: Not ported: opus_custom_encoder_create/destroy, opus_custom_encode,
   opus_custom_encode24, opus_custom_encode_float and toOpus() signalling (CUSTOM_MODES /
   ENABLE_OPUS_CUSTOM_API only), celt_encoder_get_size (no use in C#), and everything QEXT. */

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using DotGame.AudioCodecs.Opus.Common;
using static DotGame.AudioCodecs.Opus.Celt.Arch;
using static DotGame.AudioCodecs.Opus.Celt.MathOps;
using static DotGame.AudioCodecs.Opus.Common.ec_dec;

namespace DotGame.AudioCodecs.Opus.Celt;

/** Encoder state
 @brief Encoder state
 */
internal sealed class OpusCustomEncoder
{
    internal const int OPUS_BITRATE_MAX = -1;

    internal OpusCustomMode mode;     /*< Mode used by the encoder */
    internal int channels;
    internal int stream_channels;

    internal int force_intra;
    internal int clip;
    internal int disable_pf;
    internal int complexity;
    internal int upsample;
    internal int start, end;

    internal int bitrate;
    internal int vbr;
    internal int signalling;
    internal int constrained_vbr;      /* If zero, VBR can do whatever it likes with the rate */
    internal int loss_rate;
    internal int lsb_depth;
    internal int lfe;
    internal int disable_inv;

    /* Everything beyond this point gets cleared on a reset */
    internal uint rng;
    internal int spread_decision;
    internal int delayedIntra;
    internal int tonal_average;
    internal int lastCodedBands;
    internal int hf_average;
    internal int tapset_decision;

    internal int prefilter_period;
    internal short prefilter_gain;
    internal int prefilter_tapset;
    internal int consec_transient;
    internal AnalysisInfo analysis;
    internal SILKInfo silk_info;

    internal readonly int[] preemph_memE = new int[2];
    internal readonly int[] preemph_memD = new int[2];

    /* VBR-related parameters */
    internal int vbr_reservoir;
    internal int vbr_drift;
    internal int vbr_offset;
    internal int vbr_count;
    internal int overlap_max;
    internal short stereo_saving;
    internal int intensity;
    internal int[]? energy_mask;
    internal int spec_avg;

    /* C keeps these in one allocation after the struct; here they are separate arrays. */
    internal readonly int[] in_mem;        /* Size = channels*mode->overlap */
    internal readonly int[] prefilter_mem; /* Size = channels*COMBFILTER_MAXPERIOD */
    internal readonly int[] oldBandE;      /* Size = channels*mode->nbEBands */
    internal readonly int[] oldLogE;       /* Size = channels*mode->nbEBands */
    internal readonly int[] oldLogE2;      /* Size = channels*mode->nbEBands */
    internal readonly int[] energyError;   /* Size = channels*mode->nbEBands */

    /* The port's buffers for what celt_encode_with_ec() and run_prefilter() ALLOC per call in
       C, sized for the largest frame. */
    internal readonly int[] in_buf;    /* in[CC*(N+overlap)] */
    internal readonly int[] freq_buf;  /* freq[CC*N] */
    internal readonly int[] X_buf;     /* X[C*N] */
    internal readonly int[] bandE_buf; /* bandE[nbEBands*CC] */
    internal readonly int[] pre_buf;   /* run_prefilter's _pre[CC*(N+max_period)] */

    /* celt_encoder_init(st, sampling_rate, channels) */
    internal OpusCustomEncoder(int sampling_rate, int channels)
    {
        OpusCustomMode mode = Modes.opus_custom_mode_create(48000, 960, out _)
            ?? throw new InvalidOperationException("The static CELT mode is missing.");
        /* opus_custom_encoder_init_arch(st, mode, channels) */
        if (channels < 0 || channels > 2)
        {
            throw new ArgumentException("CELT supports 1 or 2 channels.", nameof(channels));
        }

        int maxN = mode.shortMdctSize * mode.nbShortMdcts;
        this.in_mem = new int[channels * mode.overlap];
        this.prefilter_mem = new int[channels * DotGame.AudioCodecs.Opus.Celt.Celt.COMBFILTER_MAXPERIOD];
        this.oldBandE = new int[channels * mode.nbEBands];
        this.oldLogE = new int[channels * mode.nbEBands];
        this.oldLogE2 = new int[channels * mode.nbEBands];
        this.energyError = new int[channels * mode.nbEBands];
        this.in_buf = new int[channels * (maxN + mode.overlap)];
        this.freq_buf = new int[channels * maxN];
        this.X_buf = new int[2 * maxN];
        this.bandE_buf = new int[2 * mode.nbEBands];
        this.pre_buf = new int[channels * (maxN + DotGame.AudioCodecs.Opus.Celt.Celt.COMBFILTER_MAXPERIOD)];

        this.mode = mode;
        this.stream_channels = this.channels = channels;

        this.upsample = 1;
        this.start = 0;
        this.end = this.mode.effEBands;
        this.signalling = 1;

        this.constrained_vbr = 1;
        this.clip = 1;

        this.bitrate = OPUS_BITRATE_MAX;
        this.vbr = 0;
        this.force_intra = 0;
        this.complexity = 5;
        this.lsb_depth = 24;

        this.ResetState();

        this.upsample = DotGame.AudioCodecs.Opus.Celt.Celt.resampling_factor(sampling_rate);
        Debug.Assert(this.upsample != 0, "Unsupported sampling rate.");
    }

    /* opus_custom_encoder_ctl requests. Where C returns OPUS_BAD_ARG, these throw. */

    /* OPUS_SET_COMPLEXITY */
    internal void SetComplexity(int value)
    {
        if (value < 0 || value > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        this.complexity = value;
    }

    /* CELT_SET_START_BAND */
    internal void SetStartBand(int value)
    {
        if (value < 0 || value >= this.mode.nbEBands)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        this.start = value;
    }

    /* CELT_SET_END_BAND */
    internal void SetEndBand(int value)
    {
        if (value < 1 || value > this.mode.nbEBands)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        this.end = value;
    }

    /* CELT_SET_PREDICTION */
    internal void SetPrediction(int value)
    {
        if (value < 0 || value > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        this.disable_pf = value <= 1 ? 1 : 0;
        this.force_intra = value == 0 ? 1 : 0;
    }

    /* OPUS_SET_PACKET_LOSS_PERC */
    internal void SetPacketLossPerc(int value)
    {
        if (value < 0 || value > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        this.loss_rate = value;
    }

    /* OPUS_SET_VBR_CONSTRAINT */
    internal void SetVBRConstraint(int value) => this.constrained_vbr = value;

    /* OPUS_SET_VBR */
    internal void SetVBR(int value) => this.vbr = value;

    /* OPUS_SET_BITRATE */
    internal void SetBitrate(int value)
    {
        if (value <= 500 && value != OPUS_BITRATE_MAX)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        value = IMIN(value, 750000 * this.channels);
        this.bitrate = value;
    }

    /* CELT_SET_CHANNELS */
    internal void SetChannels(int value)
    {
        if (value < 1 || value > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        this.stream_channels = value;
    }

    /* OPUS_SET_LSB_DEPTH */
    internal void SetLsbDepth(int value)
    {
        if (value < 8 || value > 24)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        this.lsb_depth = value;
    }

    /* OPUS_GET_LSB_DEPTH */
    internal int GetLsbDepth() => this.lsb_depth;

    /* OPUS_SET_PHASE_INVERSION_DISABLED */
    internal void SetPhaseInversionDisabled(int value)
    {
        if (value < 0 || value > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        this.disable_inv = value;
    }

    /* OPUS_GET_PHASE_INVERSION_DISABLED */
    internal int GetPhaseInversionDisabled() => this.disable_inv;

    /* OPUS_RESET_STATE: clears everything from rng on, as C's OPUS_CLEAR does. */
    internal void ResetState()
    {
        int i;
        this.rng = 0;
        this.spread_decision = 0;
        this.delayedIntra = 0;
        this.tonal_average = 0;
        this.lastCodedBands = 0;
        this.hf_average = 0;
        this.tapset_decision = 0;
        this.prefilter_period = 0;
        this.prefilter_gain = 0;
        this.prefilter_tapset = 0;
        this.consec_transient = 0;
        this.analysis = default;
        this.silk_info = default;
        Array.Clear(this.preemph_memE);
        Array.Clear(this.preemph_memD);
        this.vbr_reservoir = 0;
        this.vbr_drift = 0;
        this.vbr_offset = 0;
        this.vbr_count = 0;
        this.overlap_max = 0;
        this.stereo_saving = 0;
        this.intensity = 0;
        this.energy_mask = null;
        this.spec_avg = 0;
        Array.Clear(this.in_mem);
        Array.Clear(this.prefilter_mem);
        Array.Clear(this.oldBandE);
        Array.Clear(this.oldLogE);
        Array.Clear(this.oldLogE2);
        Array.Clear(this.energyError);
        for (i = 0; i < this.channels * this.mode.nbEBands; i++)
        {
            this.oldLogE[i] = this.oldLogE2[i] = -469762048; /* -GCONST(28.f) */
        }
        this.vbr_offset = 0;
        this.delayedIntra = 1;
        this.spread_decision = Bands.SPREAD_NORMAL;
        this.tonal_average = 256;
        this.hf_average = 0;
        this.tapset_decision = 0;
    }

    /* CELT_SET_INPUT_CLIPPING (CUSTOM_MODES only in C) */
    internal void SetInputClipping(int value) => this.clip = value;

    /* CELT_SET_SIGNALLING */
    internal void SetSignalling(int value) => this.signalling = value;

    /* CELT_SET_ANALYSIS */
    internal void SetAnalysis(in AnalysisInfo info) => this.analysis = info;

    /* CELT_SET_SILK_INFO */
    internal void SetSilkInfo(in SILKInfo info) => this.silk_info = info;

    /* CELT_GET_MODE */
    internal OpusCustomMode GetMode() => this.mode;

    /* OPUS_GET_FINAL_RANGE */
    internal uint GetFinalRange() => this.rng;

    /* OPUS_SET_LFE */
    internal void SetLFE(int value) => this.lfe = value;

    /* OPUS_SET_ENERGY_MASK: C keeps the pointer; null is NULL. */
    internal void SetEnergyMask(int[]? mask) => this.energy_mask = mask;
}

/// <summary>
/// celt/celt_encoder.c: CELT frame encoding — pre-emphasis, tone and transient detection, the
/// pitch pre-filter, the MDCTs, the energy, TF, spreading, trim and allocation decisions, VBR, and
/// quantisation.
/// </summary>
internal static class CeltEncoder
{
    private const int OPUS_BAD_ARG = -1;
    private const int OPUS_INTERNAL_ERROR = -3;

    private const int COMBFILTER_MAXPERIOD = DotGame.AudioCodecs.Opus.Celt.Celt.COMBFILTER_MAXPERIOD;
    private const int COMBFILTER_MINPERIOD = DotGame.AudioCodecs.Opus.Celt.Celt.COMBFILTER_MINPERIOD;

    /* Table of 6*64/x, trained on real data to minimize the average error */
    private static readonly byte[] inv_table =
    [
        255, 255, 156, 110, 86, 70, 59, 51, 45, 40, 37, 33, 31, 28, 26, 25,
        23, 22, 21, 20, 19, 18, 17, 16, 16, 15, 15, 14, 13, 13, 12, 12,
        12, 12, 11, 11, 11, 10, 10, 10, 9, 9, 9, 9, 9, 9, 8, 8,
        8, 8, 8, 7, 7, 7, 7, 7, 7, 6, 6, 6, 6, 6, 6, 6,
        6, 6, 6, 6, 6, 6, 6, 6, 6, 5, 5, 5, 5, 5, 5, 5,
        5, 5, 5, 5, 5, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4,
        4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 2,
    ];

    private static int transient_analysis(ReadOnlySpan<int> @in, int len, int C,
          out short tf_estimate, ref int tf_chan, int allow_weak_transients,
          out int weak_transient, short tone_freq, int toneishness)
    {
        int i;
        int mem0, mem1;
        int is_transient = 0;
        int mask_metric = 0;
        int c;
        short tf_max;
        int len2;
        /* Forward masking: 6.7 dB/ms. */
        int forward_shift = 4;
        int in_shift = IMAX(0, celt_ilog2(1 + celt_maxabs32(@in, C * len)) - 14);
        Span<short> tmp = stackalloc short[len];
        if (@in.Length < C * len)
        {
            throw new ArgumentException("The transient analysis's input is too short.");
        }
        ref int input = ref MemoryMarshal.GetReference(@in);
        ref short t = ref MemoryMarshal.GetReference(tmp);

        weak_transient = 0;
        /* For lower bitrates, let's be more conservative and have a forward masking
           decay of 3.3 dB/ms. This avoids having to code transients at very low
           bitrate (mostly for hybrid), which can result in unstable energy and/or
           partial collapse. */
        if (allow_weak_transients != 0)
        {
            forward_shift = 5;
        }
        len2 = len / 2;
        for (c = 0; c < C; c++)
        {
            int mean;
            int unmask = 0;
            int norm;
            short maxE;
            mem0 = 0;
            mem1 = 0;
            /* High-pass filter: (1 - 2*z^-1 + z^-2) / (1 - z^-1 + .5*z^-2) */
            for (i = 0; i < len; i++)
            {
                int x, y;
                x = SHR32(Unsafe.Add(ref input, i + (c * len)), in_shift);
                y = ADD32(mem0, x);
                mem0 = mem1 + y - SHL32(x, 1);
                mem1 = x - SHR32(y, 1);
                Unsafe.Add(ref t, i) = SROUND16(y, 2);
                /*printf("%f ", tmp[i]);*/
            }
            /*printf("\n");*/
            /* First few samples are bad because we don't propagate the memory */
            tmp.Slice(0, 12).Clear();

            /* Normalize tmp to max range */
            {
                int shift = 0;
                shift = 14 - celt_ilog2(MAX16(1, celt_maxabs16(tmp, len)));
                if (shift != 0)
                {
                    for (i = 0; i < len; i++)
                    {
                        tmp[i] = SHL16(tmp[i], shift);
                    }
                }
            }

            mean = 0;
            mem0 = 0;
            /* Grouping by two to reduce complexity */
            /* Forward pass to compute the post-echo threshold*/
            for (i = 0; i < len2; i++)
            {
                short a = Unsafe.Add(ref t, 2 * i);
                short b = Unsafe.Add(ref t, (2 * i) + 1);
                int x2 = PSHR32(MULT16_16(a, a) + MULT16_16(b, b), 4);
                mean += PSHR32(x2, 12);
                /* FIXME: Use PSHR16() instead */
                mem0 = mem0 + PSHR32(x2 - mem0, forward_shift);
                Unsafe.Add(ref t, i) = (short)PSHR32(mem0, 12);
            }

            mem0 = 0;
            maxE = 0;
            /* Backward pass to compute the pre-echo threshold */
            for (i = len2 - 1; i >= 0; i--)
            {
                /* Backward masking: 13.9 dB/ms. */
                /* FIXME: Use PSHR16() instead */
                mem0 = mem0 + PSHR32(SHL32(Unsafe.Add(ref t, i), 4) - mem0, 3);
                short v = (short)PSHR32(mem0, 4);
                Unsafe.Add(ref t, i) = v;
                maxE = (short)MAX16(maxE, v);
            }
            /*for (i=0;i<len2;i++)printf("%f ", tmp[i]/mean);printf("\n");*/

            /* Compute the ratio of the "frame energy" over the harmonic mean of the energy.
               This essentially corresponds to a bitrate-normalized temporal noise-to-mask
               ratio */

            /* As a compromise with the old transient detector, frame energy is the
               geometric mean of the energy and half the max */
            /* Costs two sqrt() to avoid overflows */
            mean = MULT16_16(celt_sqrt(mean), celt_sqrt(MULT16_16(maxE, len2 >> 1)));
            /* Inverse of the mean energy in Q15+6 */
            norm = SHL32(EXTEND32(len2), 6 + 14) / ADD32(EPSILON, SHR32(mean, 1));
            /* Compute harmonic mean discarding the unreliable boundaries
               The data is smooth, so we only take 1/4th of the samples */
            unmask = 0;
            for (i = 12; i < len2 - 5; i += 4)
            {
                int id;
                id = MAX32(0, MIN32(127, MULT16_32_Q15(tmp[i] + EPSILON, norm))); /* Do not round to nearest */
                unmask += inv_table[id];
            }
            /*printf("%d\n", unmask);*/
            /* Normalize, compensate for the 1/4th of the sample and the factor of 6 in the inverse table */
            unmask = 64 * unmask * 4 / (6 * (len2 - 17));
            if (unmask > mask_metric)
            {
                tf_chan = c;
                mask_metric = unmask;
            }
        }
        is_transient = mask_metric > 200 ? 1 : 0;
        /* Prevent the transient detector from confusing the partial cycle of a
           very low frequency tone with a transient. */
        if (toneishness > 526133504 /* QCONST32(.98f, 29) */ && tone_freq < 213 /* QCONST16(0.026f, 13) */)
        {
            is_transient = 0;
            mask_metric = 0;
        }
        /* For low bitrates, define "weak transients" that need to be
           handled differently to avoid partial collapse. */
        if (allow_weak_transients != 0 && is_transient != 0 && mask_metric < 600)
        {
            is_transient = 0;
            weak_transient = 1;
        }
        /* Arbitrary metric for VBR boost */
        tf_max = (short)MAX16(0, celt_sqrt(27 * mask_metric) - 42);
        /* *tf_estimate = 1 + MIN16(1, sqrt(MAX16(0, tf_max-30))/20); */
        tf_estimate = (short)celt_sqrt(MAX32(0, SHL32(MULT16_16(113 /* QCONST16(0.0069,14) */, MIN16(163, tf_max)), 14) - 37312528 /* QCONST32(0.139,28) */));
        /*printf("%d %f\n", tf_max, mask_metric);*/
        /*printf("%d %f %d\n", is_transient, (float)*tf_estimate, tf_max);*/
        return is_transient;
    }

    /* Looks for sudden increases of energy to decide whether we need to patch
       the transient decision */
    private static int patch_transient_decision(ReadOnlySpan<int> newE, ReadOnlySpan<int> oldE, int nbEBands,
          int start, int end, int C)
    {
        int i, c;
        int mean_diff = 0;
        Span<int> spread_old = stackalloc int[26];
        /* Apply an aggressive (-6 dB/Bark) spreading function to the old frame to
           avoid false detection caused by irrelevant bands */
        if (C == 1)
        {
            spread_old[start] = oldE[start];
            for (i = start + 1; i < end; i++)
            {
                spread_old[i] = MAXG(spread_old[i - 1] - 16777216 /* GCONST(1.0f) */, oldE[i]);
            }
        }
        else
        {
            spread_old[start] = MAXG(oldE[start], oldE[start + nbEBands]);
            for (i = start + 1; i < end; i++)
            {
                spread_old[i] = MAXG(spread_old[i - 1] - 16777216 /* GCONST(1.0f) */,
                                      MAXG(oldE[i], oldE[i + nbEBands]));
            }
        }
        for (i = end - 2; i >= start; i--)
        {
            spread_old[i] = MAXG(spread_old[i], spread_old[i + 1] - 16777216 /* GCONST(1.0f) */);
        }
        /* Compute mean increase */
        c = 0;
        do
        {
            for (i = IMAX(2, start); i < end - 1; i++)
            {
                short x1, x2;
                x1 = (short)MAXG(0, newE[i + (c * nbEBands)]);
                x2 = (short)MAXG(0, spread_old[i]);
                mean_diff = ADD32(mean_diff, MAXG(0, SUB32(x1, x2)));
            }
        }
        while (++c < C);
        mean_diff = DIV32(mean_diff, C * (end - 1 - IMAX(2, start)));
        /*printf("%f %f %d\n", mean_diff, max_diff, count);*/
        return mean_diff > 16777216 /* GCONST(1.f) */ ? 1 : 0;
    }

    /** Apply window and compute the MDCT for all sub-frames and
        all channels in a frame */
    private static void compute_mdcts(OpusCustomMode mode, int shortBlocks, ReadOnlySpan<int> @in,
          Span<int> @out, int C, int CC, int LM, int upsample)
    {
        int overlap = mode.overlap;
        int N;
        int B;
        int shift;
        int i, b, c;
        if (shortBlocks != 0)
        {
            B = shortBlocks;
            N = mode.shortMdctSize;
            shift = mode.maxLM;
        }
        else
        {
            B = 1;
            N = mode.shortMdctSize << LM;
            shift = mode.maxLM - LM;
        }
        c = 0;
        do
        {
            for (b = 0; b < B; b++)
            {
                /* Interleaving the sub-frames while doing the MDCTs */
                MdctEnc.clt_mdct_forward(mode.mdct, @in.Slice((c * ((B * N) + overlap)) + (b * N)),
                                 @out.Slice(b + (c * N * B)), mode.window, overlap, shift, B);
            }
        }
        while (++c < CC);
        if (CC == 2 && C == 1)
        {
            for (i = 0; i < B * N; i++)
            {
                @out[i] = ADD32(HALF32(@out[i]), HALF32(@out[(B * N) + i]));
            }
        }
        if (upsample != 1)
        {
            c = 0;
            do
            {
                int bound = B * N / upsample;
                for (i = 0; i < bound; i++)
                {
                    @out[(c * B * N) + i] *= upsample;
                }
                @out.Slice((c * B * N) + bound, (B * N) - bound).Clear();
            }
            while (++c < C);
        }
    }

    internal static void celt_preemphasis(ReadOnlySpan<int> pcmp, Span<int> inp,
          int N, int CC, int upsample, ReadOnlySpan<short> coef, ref int mem, int clip)
    {
        int i;
        short coef0;
        int m;
        int Nu;

        coef0 = coef[0];
        m = mem;

        /* Fast path for the normal 48kHz case and no clipping */
        if (coef[1] == 0 && upsample == 1 && clip == 0)
        {
            for (i = 0; i < N; i++)
            {
                int x;
                x = RES2SIG(pcmp[CC * i]);
                /* Apply pre-emphasis */
                inp[i] = x - m;
                m = MULT16_32_Q15(coef0, x);
            }
            mem = m;
            return;
        }

        Nu = N / upsample;
        if (upsample != 1)
        {
            inp.Slice(0, N).Clear();
        }
        for (i = 0; i < Nu; i++)
        {
            inp[i * upsample] = RES2SIG(pcmp[CC * i]);
        }

        if (clip != 0)
        {
            /* Clip input to avoid encoding non-portable files */
            for (i = 0; i < Nu; i++)
            {
                inp[i * upsample] = MAX32(-(65536 << SIG_SHIFT), MIN32(65536 << SIG_SHIFT, inp[i * upsample]));
            }
        }
        {
            for (i = 0; i < N; i++)
            {
                int x;
                x = inp[i];
                /* Apply pre-emphasis */
                inp[i] = x - m;
                m = MULT16_32_Q15(coef0, x);
            }
        }
        mem = m;
    }

    private static int l1_metric(ReadOnlySpan<int> tmp, int N, int LM, short bias)
    {
        int i;
        int L1;
        L1 = 0;
        tmp = tmp[..N];
        i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            // The same sum four lanes at a time (integer adds, so in any order)
            ref int x = ref MemoryMarshal.GetReference(tmp);
            var sum = Vector128<int>.Zero;
            for (; i + 4 <= N; i += 4)
            {
                sum += Vector128.Abs(Vector128.LoadUnsafe(ref x, (nuint)i) >> (NORM_SHIFT - 14));
            }
            L1 = Vector128.Sum(sum);
        }
        for (; i < N; i++)
        {
            L1 += EXTEND32(ABS16(SHR32(tmp[i], NORM_SHIFT - 14)));
        }
        /* When in doubt, prefer good freq resolution */
        L1 = MAC16_32_Q15(L1, LM * bias, L1);
        return L1;
    }

    private static int tf_analysis(OpusCustomMode m, int len, int isTransient,
          Span<int> tf_res, int lambda, ReadOnlySpan<int> X, int N0, int LM,
          short tf_estimate, int tf_chan, ReadOnlySpan<int> importance)
    {
        int i;
        int cost0;
        int cost1;
        int sel;
        Span<int> selcost = stackalloc int[2];
        int tf_select = 0;
        short bias;
        ReadOnlySpan<sbyte> tf_select_table = DotGame.AudioCodecs.Opus.Celt.Celt.tf_select_table.AsSpan(LM * 8, 8);

        bias = (short)MULT16_16_Q14(1311 /* QCONST16(.04f,15) */, MAX16(-4096 /* -QCONST16(.25f,14) */, 8192 /* QCONST16(.5f,14) */ - tf_estimate));
        /*printf("%f ", bias);*/

        Span<int> metric = stackalloc int[len];
        Span<int> tmp = stackalloc int[(m.eBands[len] - m.eBands[len - 1]) << LM];
        Span<int> tmp_1 = stackalloc int[(m.eBands[len] - m.eBands[len - 1]) << LM];
        Span<int> path0 = stackalloc int[len];
        Span<int> path1 = stackalloc int[len];

        for (i = 0; i < len; i++)
        {
            int k, N;
            int narrow;
            int L1, best_L1;
            int best_level = 0;
            N = (m.eBands[i + 1] - m.eBands[i]) << LM;
            /* band is too narrow to be split down to LM=-1 */
            narrow = (m.eBands[i + 1] - m.eBands[i]) == 1 ? 1 : 0;
            X.Slice((tf_chan * N0) + (m.eBands[i] << LM), N).CopyTo(tmp);
            /* Just add the right channel if we're in stereo */
            /*if (C==2)
               for (j=0;j<N;j++)
                  tmp[j] = ADD16(SHR16(tmp[j], 1),SHR16(X[N0+j+(m->eBands[i]<<LM)], 1));*/
            L1 = l1_metric(tmp, N, isTransient != 0 ? LM : 0, bias);
            best_L1 = L1;
            /* Check the -1 case for transients */
            if (isTransient != 0 && narrow == 0)
            {
                tmp.Slice(0, N).CopyTo(tmp_1);
                Bands.haar1(tmp_1, N >> LM, 1 << LM);
                L1 = l1_metric(tmp_1, N, LM + 1, bias);
                if (L1 < best_L1)
                {
                    best_L1 = L1;
                    best_level = -1;
                }
            }
            /*printf ("%f ", L1);*/
            for (k = 0; k < LM + ((isTransient != 0 || narrow != 0) ? 0 : 1); k++)
            {
                int B;

                if (isTransient != 0)
                {
                    B = (LM - k - 1);
                }
                else
                {
                    B = k + 1;
                }

                Bands.haar1(tmp, N >> k, 1 << k);

                L1 = l1_metric(tmp, N, B, bias);

                if (L1 < best_L1)
                {
                    best_L1 = L1;
                    best_level = k + 1;
                }
            }
            /*printf ("%d ", isTransient ? LM-best_level : best_level);*/
            /* metric is in Q1 to be able to select the mid-point (-0.5) for narrower bands */
            if (isTransient != 0)
            {
                metric[i] = 2 * best_level;
            }
            else
            {
                metric[i] = -2 * best_level;
            }
            /* For bands that can't be split to -1, set the metric to the half-way point to avoid
               biasing the decision */
            if (narrow != 0 && (metric[i] == 0 || metric[i] == -2 * LM))
            {
                metric[i] -= 1;
            }
            /*printf("%d ", metric[i]/2 + (!isTransient)*LM);*/
        }
        /*printf("\n");*/
        /* Search for the optimal tf resolution, including tf_select */
        tf_select = 0;
        for (sel = 0; sel < 2; sel++)
        {
            cost0 = importance[0] * ABS32(metric[0] - (2 * tf_select_table[(4 * isTransient) + (2 * sel) + 0]));
            cost1 = (importance[0] * ABS32(metric[0] - (2 * tf_select_table[(4 * isTransient) + (2 * sel) + 1]))) + (isTransient != 0 ? 0 : lambda);
            for (i = 1; i < len; i++)
            {
                int curr0, curr1;
                curr0 = IMIN(cost0, cost1 + lambda);
                curr1 = IMIN(cost0 + lambda, cost1);
                cost0 = curr0 + (importance[i] * ABS32(metric[i] - (2 * tf_select_table[(4 * isTransient) + (2 * sel) + 0])));
                cost1 = curr1 + (importance[i] * ABS32(metric[i] - (2 * tf_select_table[(4 * isTransient) + (2 * sel) + 1])));
            }
            cost0 = IMIN(cost0, cost1);
            selcost[sel] = cost0;
        }
        /* For now, we're conservative and only allow tf_select=1 for transients.
         * If tests confirm it's useful for non-transients, we could allow it. */
        if (selcost[1] < selcost[0] && isTransient != 0)
        {
            tf_select = 1;
        }
        cost0 = importance[0] * ABS32(metric[0] - (2 * tf_select_table[(4 * isTransient) + (2 * tf_select) + 0]));
        cost1 = (importance[0] * ABS32(metric[0] - (2 * tf_select_table[(4 * isTransient) + (2 * tf_select) + 1]))) + (isTransient != 0 ? 0 : lambda);
        /* Viterbi forward pass */
        for (i = 1; i < len; i++)
        {
            int curr0, curr1;
            int from0, from1;

            from0 = cost0;
            from1 = cost1 + lambda;
            if (from0 < from1)
            {
                curr0 = from0;
                path0[i] = 0;
            }
            else
            {
                curr0 = from1;
                path0[i] = 1;
            }

            from0 = cost0 + lambda;
            from1 = cost1;
            if (from0 < from1)
            {
                curr1 = from0;
                path1[i] = 0;
            }
            else
            {
                curr1 = from1;
                path1[i] = 1;
            }
            cost0 = curr0 + (importance[i] * ABS32(metric[i] - (2 * tf_select_table[(4 * isTransient) + (2 * tf_select) + 0])));
            cost1 = curr1 + (importance[i] * ABS32(metric[i] - (2 * tf_select_table[(4 * isTransient) + (2 * tf_select) + 1])));
        }
        tf_res[len - 1] = cost0 < cost1 ? 0 : 1;
        /* Viterbi backward pass to check the decisions */
        for (i = len - 2; i >= 0; i--)
        {
            if (tf_res[i + 1] == 1)
            {
                tf_res[i] = path1[i + 1];
            }
            else
            {
                tf_res[i] = path0[i + 1];
            }
        }
        /*printf("%d %f\n", *tf_sum, tf_estimate);*/
        return tf_select;
    }

    private static void tf_encode(int start, int end, int isTransient, scoped Span<int> tf_res, int LM, int tf_select, ref ec_enc enc)
    {
        int curr, i;
        int tf_select_rsv;
        int tf_changed;
        int logp;
        uint budget;
        uint tell;
        budget = enc.storage * 8;
        tell = (uint)enc.ec_tell();
        logp = isTransient != 0 ? 2 : 4;
        /* Reserve space to code the tf_select decision. */
        tf_select_rsv = LM > 0 && tell + (uint)logp + 1 <= budget ? 1 : 0;
        budget -= (uint)tf_select_rsv;
        curr = tf_changed = 0;
        for (i = start; i < end; i++)
        {
            if (tell + (uint)logp <= budget)
            {
                enc.ec_enc_bit_logp(tf_res[i] ^ curr, (uint)logp);
                tell = (uint)enc.ec_tell();
                curr = tf_res[i];
                tf_changed |= curr;
            }
            else
            {
                tf_res[i] = curr;
            }
            logp = isTransient != 0 ? 4 : 5;
        }
        /* Only code tf_select if it would actually make a difference. */
        if (tf_select_rsv != 0 &&
              DotGame.AudioCodecs.Opus.Celt.Celt.tf_select_table[(LM * 8) + (4 * isTransient) + 0 + tf_changed] !=
              DotGame.AudioCodecs.Opus.Celt.Celt.tf_select_table[(LM * 8) + (4 * isTransient) + 2 + tf_changed])
        {
            enc.ec_enc_bit_logp(tf_select, 1);
        }
        else
        {
            tf_select = 0;
        }
        for (i = start; i < end; i++)
        {
            tf_res[i] = DotGame.AudioCodecs.Opus.Celt.Celt.tf_select_table[(LM * 8) + (4 * isTransient) + (2 * tf_select) + tf_res[i]];
        }
        /*for(i=0;i<end;i++)printf("%d ", isTransient ? tf_res[i] : LM+tf_res[i]);printf("\n");*/
    }

    private static int alloc_trim_analysis(OpusCustomMode m, ReadOnlySpan<int> X,
          ReadOnlySpan<int> bandLogE, int end, int LM, int C, int N0,
          ref AnalysisInfo analysis, ref short stereo_saving, short tf_estimate,
          int intensity, int surround_trim, int equiv_rate)
    {
        int i;
        int diff = 0;
        int c;
        int trim_index;
        short trim = 1280; /* QCONST16(5.f, 8) */
        short logXC, logXC2;
        /* At low bitrate, reducing the trim seems to help. At higher bitrates, it's less
           clear what's best, so we're keeping it as it was before, at least for now. */
        if (equiv_rate < 64000)
        {
            trim = 1024; /* QCONST16(4.f, 8) */
        }
        else if (equiv_rate < 80000)
        {
            int frac = (equiv_rate - 64000) >> 10;
            trim = (short)(1024 /* QCONST16(4.f, 8) */ + (16 /* QCONST16(1.f/16.f, 8) */ * frac));
        }
        if (C == 2)
        {
            short sum = 0; /* Q10 */
            short minXC; /* Q10 */
            /* Compute inter-channel correlation for low frequencies */
            for (i = 0; i < 8; i++)
            {
                int partial;
                partial = Vq.celt_inner_prod_norm_shift(X.Slice(m.eBands[i] << LM), X.Slice(N0 + (m.eBands[i] << LM)),
                      (m.eBands[i + 1] - m.eBands[i]) << LM);
                sum = ADD16(sum, EXTRACT16(SHR32(partial, 18)));
            }
            sum = (short)MULT16_16_Q15(4096 /* QCONST16(1.f/8, 15) */, sum);
            sum = (short)MIN16(1024 /* QCONST16(1.f, 10) */, ABS16(sum));
            minXC = sum;
            for (i = 8; i < intensity; i++)
            {
                int partial;
                partial = Vq.celt_inner_prod_norm_shift(X.Slice(m.eBands[i] << LM), X.Slice(N0 + (m.eBands[i] << LM)),
                      (m.eBands[i + 1] - m.eBands[i]) << LM);
                minXC = (short)MIN16(minXC, ABS16(EXTRACT16(SHR32(partial, 18))));
            }
            minXC = (short)MIN16(1024 /* QCONST16(1.f, 10) */, ABS16(minXC));
            /*printf ("%f\n", sum);*/
            /* mid-side savings estimations based on the LF average*/
            logXC = MathOpsEnc.celt_log2(1049625 /* QCONST32(1.001f, 20) */ - MULT16_16(sum, sum));
            /* mid-side savings estimations based on min correlation */
            logXC2 = (short)MAX16(HALF16(logXC), MathOpsEnc.celt_log2(1049625 /* QCONST32(1.001f, 20) */ - MULT16_16(minXC, minXC)));
            /* Compensate for Q20 vs Q14 input and convert output to Q8 */
            logXC = (short)PSHR32(logXC - 6144 /* QCONST16(6.f, 10) */, 10 - 8);
            logXC2 = (short)PSHR32(logXC2 - 6144 /* QCONST16(6.f, 10) */, 10 - 8);

            trim = (short)(trim + MAX16(-1024 /* -QCONST16(4.f, 8) */, MULT16_16_Q15(24576 /* QCONST16(.75f,15) */, logXC)));
            stereo_saving = (short)MIN16(stereo_saving + 64 /* QCONST16(0.25f, 8) */, -HALF16(logXC2));
        }

        /* Estimate spectral tilt */
        c = 0;
        do
        {
            for (i = 0; i < end - 1; i++)
            {
                diff += SHR32(bandLogE[i + (c * m.nbEBands)], 5) * (int)(2 + (2 * i) - end);
            }
        }
        while (++c < C);
        diff /= C * (end - 1);
        /*printf("%f\n", diff);*/
        trim = (short)(trim - MAX32(-512 /* -QCONST16(2.f, 8) */, MIN32(512 /* QCONST16(2.f, 8) */, SHR32(diff + 524288 /* QCONST32(1.f, DB_SHIFT-5) */, DB_SHIFT - 13) / 6)));
        trim = (short)(trim - SHR16(surround_trim, DB_SHIFT - 8));
        trim = (short)(trim - (2 * SHR16(tf_estimate, 14 - 8)));
        if (analysis.valid != 0)
        {
            trim = (short)(trim - MAX16(-512 /* -QCONST16(2.f, 8) */, MIN16(512 /* QCONST16(2.f, 8) */,
                  (short)(512 /* QCONST16(2.f, 8) */ * (analysis.tonality_slope + .05f)))));
        }

        trim_index = PSHR32(trim, 8);
        trim_index = IMAX(0, IMIN(10, trim_index));
        /*printf("%d\n", trim_index);*/
        return trim_index;
    }

    private static int stereo_analysis(OpusCustomMode m, ReadOnlySpan<int> X,
          int LM, int N0)
    {
        int i;
        int thetas;
        int sumLR = EPSILON, sumMS = EPSILON;

        /* Use the L1 norm to model the entropy of the L/R signal vs the M/S signal */
        for (i = 0; i < 13; i++)
        {
            int j;
            for (j = m.eBands[i] << LM; j < m.eBands[i + 1] << LM; j++)
            {
                int L, R, M, S;
                /* We cast to 32-bit first because of the -32768 case */
                L = SHR32(X[j], NORM_SHIFT - 14);
                R = SHR32(X[N0 + j], NORM_SHIFT - 14);
                M = ADD32(L, R);
                S = SUB32(L, R);
                sumLR = ADD32(sumLR, ADD32(ABS32(L), ABS32(R)));
                sumMS = ADD32(sumMS, ADD32(ABS32(M), ABS32(S)));
            }
        }
        sumMS = MULT16_32_Q15(23170 /* QCONST16(0.707107f, 15) */, sumMS);
        thetas = 13;
        /* We don't need thetas for lower bands with LM<=1 */
        if (LM <= 1)
        {
            thetas -= 8;
        }
        return MULT16_32_Q15((m.eBands[13] << (LM + 1)) + thetas, sumMS)
              > MULT16_32_Q15(m.eBands[13] << (LM + 1), sumLR) ? 1 : 0;
    }

    private static int median_of_5(ReadOnlySpan<int> x)
    {
        int t0, t1, t2, t3, t4;
        int tmp;
        t2 = x[2];
        if (x[0] > x[1])
        {
            t0 = x[1];
            t1 = x[0];
        }
        else
        {
            t0 = x[0];
            t1 = x[1];
        }
        if (x[3] > x[4])
        {
            t3 = x[4];
            t4 = x[3];
        }
        else
        {
            t3 = x[3];
            t4 = x[4];
        }
        if (t0 > t3)
        {
            /* MSWAP(t0, t3); MSWAP(t1, t4); */
            tmp = t0;
            t0 = t3;
            t3 = tmp;
            tmp = t1;
            t1 = t4;
            t4 = tmp;
        }
        _ = t0;
        if (t2 > t1)
        {
            if (t1 < t3)
            {
                return MING(t2, t3);
            }
            else
            {
                return MING(t4, t1);
            }
        }
        else
        {
            if (t2 < t3)
            {
                return MING(t1, t3);
            }
            else
            {
                return MING(t2, t4);
            }
        }
    }

    private static int median_of_3(ReadOnlySpan<int> x)
    {
        int t0, t1, t2;
        if (x[0] > x[1])
        {
            t0 = x[1];
            t1 = x[0];
        }
        else
        {
            t0 = x[0];
            t1 = x[1];
        }
        t2 = x[2];
        if (t1 < t2)
        {
            return t1;
        }
        else if (t0 < t2)
        {
            return t2;
        }
        else
        {
            return t0;
        }
    }

    private static int dynalloc_analysis(ReadOnlySpan<int> bandLogE, ReadOnlySpan<int> bandLogE2, ReadOnlySpan<int> oldBandE,
          int nbEBands, int start, int end, int C, Span<int> offsets, int lsb_depth, ReadOnlySpan<short> logN,
          int isTransient, int vbr, int constrained_vbr, ReadOnlySpan<short> eBands, int LM,
          int effectiveBytes, out int tot_boost_, int lfe, ReadOnlySpan<int> surround_dynalloc,
          ref AnalysisInfo analysis, Span<int> importance, Span<int> spread_weight, short tone_freq, int toneishness)
    {
        int i, c;
        int tot_boost = 0;
        int maxDepth;
        Span<int> follower = stackalloc int[C * nbEBands];
        Span<int> noise_floor = stackalloc int[C * nbEBands];
        Span<int> bandLogE3 = stackalloc int[nbEBands];
        Span<int> mask = stackalloc int[nbEBands];
        Span<int> sig = stackalloc int[nbEBands];
        offsets.Slice(0, nbEBands).Clear();
        /* Dynamic allocation code */
        maxDepth = -535193184; /* -GCONST(31.9f) */
        for (i = 0; i < end; i++)
        {
            /* Noise floor must take into account eMeans, the depth, the width of the bands
               and the preemphasis filter (approx. square of bark band ID) */
            noise_floor[i] = (1048576 /* GCONST(0.0625f) */ * logN[i])
                  + 8388608 /* GCONST(.5f) */ + SHL32(9 - lsb_depth, DB_SHIFT) - SHL32(QuantBands.eMeans[i], DB_SHIFT - 4)
                  + (104019 /* GCONST(.0062f) */ * (i + 5) * (i + 5));
        }
        c = 0;
        do
        {
            for (i = 0; i < end; i++)
            {
                maxDepth = MAXG(maxDepth, bandLogE[(c * nbEBands) + i] - noise_floor[i]);
            }
        }
        while (++c < C);
        {
            /* Compute a really simple masking model to avoid taking into account completely masked
               bands when computing the spreading decision. */
            for (i = 0; i < end; i++)
            {
                mask[i] = bandLogE[i] - noise_floor[i];
            }
            if (C == 2)
            {
                for (i = 0; i < end; i++)
                {
                    mask[i] = MAXG(mask[i], bandLogE[nbEBands + i] - noise_floor[i]);
                }
            }
            mask.Slice(0, end).CopyTo(sig);
            for (i = 1; i < end; i++)
            {
                mask[i] = MAXG(mask[i], mask[i - 1] - 33554432 /* GCONST(2.f) */);
            }
            for (i = end - 2; i >= 0; i--)
            {
                mask[i] = MAXG(mask[i], mask[i + 1] - 50331648 /* GCONST(3.f) */);
            }
            for (i = 0; i < end; i++)
            {
                /* Compute SMR: Mask is never more than 72 dB below the peak and never below the noise floor.*/
                int smr = sig[i] - MAXG(MAXG(0, maxDepth - 201326592 /* GCONST(12.f) */), mask[i]);
                /* Clamp SMR to make sure we're not shifting by something negative or too large. */
                /* FIXME: Use PSHR16() instead */
                int shift = -PSHR32(MAXG(-83886080 /* -GCONST(5.f) */, MING(0, smr)), DB_SHIFT);
                spread_weight[i] = 32 >> shift;
            }
            /*for (i=0;i<end;i++)
               printf("%d ", spread_weight[i]);
            printf("\n");*/
        }
        /* Make sure that dynamic allocation can't make us bust the budget.
           We enable the feature starting at 24 kb/s for 20-ms frames
           and 96 kb/s for 2.5 ms frames.  */
        if (effectiveBytes >= (30 + (5 * LM)) && lfe == 0)
        {
            int last = 0;
            c = 0;
            do
            {
                int offset;
                int tmp;
                scoped Span<int> f;
                bandLogE2.Slice(c * nbEBands, end).CopyTo(bandLogE3);
                if (LM == 0)
                {
                    /* For 2.5 ms frames, the first 8 bands have just one bin, so the
                       energy is highly unreliable (high variance). For that reason,
                       we take the max with the previous energy so that at least 2 bins
                       are getting used. */
                    for (i = 0; i < IMIN(8, end); i++)
                    {
                        bandLogE3[i] = MAXG(bandLogE2[(c * nbEBands) + i], oldBandE[(c * nbEBands) + i]);
                    }
                }
                f = follower.Slice(c * nbEBands);
                f[0] = bandLogE3[0];
                for (i = 1; i < end; i++)
                {
                    /* The last band to be at least 3 dB higher than the previous one
                       is the last we'll consider. Otherwise, we run into problems on
                       bandlimited signals. */
                    if (bandLogE3[i] > bandLogE3[i - 1] + 8388608 /* GCONST(.5f) */)
                    {
                        last = i;
                    }
                    f[i] = MING(f[i - 1] + 25165824 /* GCONST(1.5f) */, bandLogE3[i]);
                }
                for (i = last - 1; i >= 0; i--)
                {
                    f[i] = MING(f[i], MING(f[i + 1] + 33554432 /* GCONST(2.f) */, bandLogE3[i]));
                }

                /* Combine with a median filter to avoid dynalloc triggering unnecessarily.
                   The "offset" value controls how conservative we are -- a higher offset
                   reduces the impact of the median filter and makes dynalloc use more bits. */
                offset = 16777216; /* GCONST(1.f) */
                for (i = 2; i < end - 2; i++)
                {
                    f[i] = MAXG(f[i], median_of_5(bandLogE3.Slice(i - 2)) - offset);
                }
                tmp = median_of_3(bandLogE3) - offset;
                f[0] = MAXG(f[0], tmp);
                f[1] = MAXG(f[1], tmp);
                tmp = median_of_3(bandLogE3.Slice(end - 3)) - offset;
                f[end - 2] = MAXG(f[end - 2], tmp);
                f[end - 1] = MAXG(f[end - 1], tmp);

                for (i = 0; i < end; i++)
                {
                    f[i] = MAXG(f[i], noise_floor[i]);
                }
            }
            while (++c < C);
            if (C == 2)
            {
                for (i = start; i < end; i++)
                {
                    /* Consider 24 dB "cross-talk" */
                    follower[nbEBands + i] = MAXG(follower[nbEBands + i], follower[i] - 67108864 /* GCONST(4.f) */);
                    follower[i] = MAXG(follower[i], follower[nbEBands + i] - 67108864 /* GCONST(4.f) */);
                    follower[i] = HALF32(MAXG(0, bandLogE[i] - follower[i]) + MAXG(0, bandLogE[nbEBands + i] - follower[nbEBands + i]));
                }
            }
            else
            {
                for (i = start; i < end; i++)
                {
                    follower[i] = MAXG(0, bandLogE[i] - follower[i]);
                }
            }
            for (i = start; i < end; i++)
            {
                follower[i] = MAXG(follower[i], surround_dynalloc[i]);
            }
            for (i = start; i < end; i++)
            {
                importance[i] = PSHR32(13 * celt_exp2_db(MING(follower[i], 67108864 /* GCONST(4.f) */)), 16);
            }
            /* For non-transient CBR/CVBR frames, halve the dynalloc contribution */
            if ((vbr == 0 || constrained_vbr != 0) && isTransient == 0)
            {
                for (i = start; i < end; i++)
                {
                    follower[i] = HALF32(follower[i]);
                }
            }
            for (i = start; i < end; i++)
            {
                if (i < 8)
                {
                    follower[i] *= 2;
                }
                if (i >= 12)
                {
                    follower[i] = HALF32(follower[i]);
                }
            }
            /* Compensate for Opus' under-allocation on tones. */
            if (toneishness > 526133504 /* QCONST32(.98f, 29) */)
            {
                int freq_bin = PSHR32((int)tone_freq * 19557 /* QCONST16(120/M_PI, 9) */, 13 + 9);
                for (i = start; i < end; i++)
                {
                    if (freq_bin >= eBands[i] && freq_bin <= eBands[i + 1])
                    {
                        follower[i] += 33554432; /* GCONST(2.f) */
                    }
                    if (freq_bin >= eBands[i] - 1 && freq_bin <= eBands[i + 1] + 1)
                    {
                        follower[i] += 16777216; /* GCONST(1.f) */
                    }
                    if (freq_bin >= eBands[i] - 2 && freq_bin <= eBands[i + 1] + 2)
                    {
                        follower[i] += 16777216; /* GCONST(1.f) */
                    }
                    if (freq_bin >= eBands[i] - 3 && freq_bin <= eBands[i + 1] + 3)
                    {
                        follower[i] += 8388608; /* GCONST(.5f) */
                    }
                }
                if (freq_bin >= eBands[end])
                {
                    follower[end - 1] += 33554432; /* GCONST(2.f) */
                    follower[end - 2] += 16777216; /* GCONST(1.f) */
                }
            }
            if (analysis.valid != 0)
            {
                for (i = start; i < IMIN(AnalysisInfo.LEAK_BANDS, end); i++)
                {
                    follower[i] = follower[i] + (262144 /* GCONST(1.f/64.f) */ * analysis.leak_boost[i]);
                }
            }
            for (i = start; i < end; i++)
            {
                int width;
                int boost;
                int boost_bits;

                follower[i] = MING(follower[i], 67108864 /* GCONST(4) */);

                follower[i] = SHR32(follower[i], 8);
                width = (C * (eBands[i + 1] - eBands[i])) << LM;
                if (width < 6)
                {
                    boost = (int)SHR32(follower[i], DB_SHIFT - 8);
                    boost_bits = (boost * width) << BITRES;
                }
                else if (width > 48)
                {
                    boost = (int)SHR32(follower[i] * 8, DB_SHIFT - 8);
                    boost_bits = ((boost * width) << BITRES) / 8;
                }
                else
                {
                    boost = (int)SHR32(follower[i] * width / 6, DB_SHIFT - 8);
                    boost_bits = (boost * 6) << BITRES;
                }
                /* For CBR and non-transient CVBR frames, limit dynalloc to 2/3 of the bits */
                if ((vbr == 0 || (constrained_vbr != 0 && isTransient == 0))
                      && (tot_boost + boost_bits) >> BITRES >> 3 > 2 * effectiveBytes / 3)
                {
                    int cap = ((2 * effectiveBytes / 3) << BITRES << 3);
                    offsets[i] = cap - tot_boost;
                    tot_boost = cap;
                    break;
                }
                else
                {
                    offsets[i] = boost;
                    tot_boost += boost_bits;
                }
            }
        }
        else
        {
            for (i = start; i < end; i++)
            {
                importance[i] = 13;
            }
        }
        tot_boost_ = tot_boost;
        return maxDepth;
    }

    internal static void normalize_tone_input(Span<short> x, int len)
    {
        int ac0 = len;
        int i;
        int shift;
        for (i = 0; i < len; i++)
        {
            ac0 = ADD32(ac0, SHR32(MULT16_16(x[i], x[i]), 10));
        }
        shift = 5 - ((28 - celt_ilog2(ac0)) / 2);
        if (shift > 0)
        {
            for (i = 0; i < len; i++)
            {
                x[i] = (short)PSHR32(x[i], shift);
            }
        }
    }

    internal static int acos_approx(int x)
    {
        short x14;
        int tmp;
        int flip = x < 0 ? 1 : 0;
        x = ABS32(x);
        x14 = (short)(x >> 15);
        tmp = ((762 * x14) >> 14) - 3308;
        tmp = ((tmp * x14) >> 14) + 25726;
        tmp = (tmp * celt_sqrt(IMAX(0, (1 << 30) - (x << 1)))) >> 16;
        if (flip != 0)
        {
            tmp = 25736 - tmp;
        }
        return tmp;
    }

    /* Compute the LPC coefficients using a least-squares fit for both forward and backward prediction. */
    private static int tone_lpc(ReadOnlySpan<short> x, int len, int delay, Span<int> lpc)
    {
        int i;
        int r00 = 0, r01 = 0, r11 = 0, r02 = 0, r12 = 0, r22 = 0;
        int edges;
        int num0, num1, den;
        Debug.Assert(len > 2 * delay);
        /* Compute correlations as if using the forward prediction covariance method. */
        r00 = Simd.InnerProduct16(x, x, len - (2 * delay));
        r01 = Simd.InnerProduct16(x, x.Slice(delay), len - (2 * delay));
        r02 = Simd.InnerProduct16(x, x.Slice(2 * delay), len - (2 * delay));
        edges = 0;
        for (i = 0; i < delay; i++)
        {
            edges += MULT16_16(x[len + i - (2 * delay)], x[len + i - (2 * delay)]) - MULT16_16(x[i], x[i]);
        }
        r11 = r00 + edges;
        edges = 0;
        for (i = 0; i < delay; i++)
        {
            edges += MULT16_16(x[len + i - delay], x[len + i - delay]) - MULT16_16(x[i + delay], x[i + delay]);
        }
        r22 = r11 + edges;
        edges = 0;
        for (i = 0; i < delay; i++)
        {
            edges += MULT16_16(x[len + i - (2 * delay)], x[len + i - delay]) - MULT16_16(x[i], x[i + delay]);
        }
        r12 = r01 + edges;
        /* Reverse and sum to get the backward contribution. */
        {
            int R00, R01, R11, R02, R12, R22;
            R00 = r00 + r22;
            R01 = r01 + r12;
            R11 = 2 * r11;
            R02 = 2 * r02;
            R12 = r12 + r01;
            R22 = r00 + r22;
            r00 = R00;
            r01 = R01;
            r11 = R11;
            r02 = R02;
            r12 = R12;
            r22 = R22;
        }
        _ = r22;
        /* Solve A*x=b, where A=[r00, r01; r01, r11] and b=[r02; r12]. */
        den = MULT32_32_Q31(r00, r11) - MULT32_32_Q31(r01, r01);
        if (den <= SHR32(MULT32_32_Q31(r00, r11), 10))
        {
            return 1;
        }
        num1 = MULT32_32_Q31(r02, r11) - MULT32_32_Q31(r01, r12);
        if (num1 >= den)
        {
            lpc[1] = 536870912; /* QCONST32(1.f, 29) */
        }
        else if (num1 <= -den)
        {
            lpc[1] = -536870912; /* -QCONST32(1.f, 29) */
        }
        else
        {
            lpc[1] = frac_div32_q29(num1, den);
        }
        num0 = MULT32_32_Q31(r00, r12) - MULT32_32_Q31(r02, r01);
        if (HALF32(num0) >= den)
        {
            lpc[0] = 1073741312; /* QCONST32(1.999999f, 29) */
        }
        else if (HALF32(num0) <= -den)
        {
            lpc[0] = -1073741312; /* -QCONST32(1.999999f, 29) */
        }
        else
        {
            lpc[0] = frac_div32_q29(num0, den);
        }
        /*printf("%f %f\n", lpc[0], lpc[1]);*/
        return 0;
    }

    /* Detects pure of nearly pure tones so we can prevent them from causing problems with the encoder. */
    private static short tone_detect(ReadOnlySpan<int> @in, int CC, int N, out int toneishness, int Fs)
    {
        int i;
        int delay = 1;
        int fail;
        Span<int> lpc = stackalloc int[2];
        short freq;
        Span<short> x = stackalloc short[N];
        lpc.Clear();
        /* Shift by SIG_SHIFT+2 (+3 for stereo) to account for HF gain of the preemphasis filter. */
        if (CC == 2)
        {
            for (i = 0; i < N; i++)
            {
                x[i] = (short)PSHR32(ADD32(SHR32(@in[i], 1), SHR32(@in[i + N], 1)), SIG_SHIFT + 2);
            }
        }
        else
        {
            for (i = 0; i < N; i++)
            {
                x[i] = (short)PSHR32(@in[i], SIG_SHIFT + 2);
            }
        }
        normalize_tone_input(x, N);
        fail = tone_lpc(x, N, delay, lpc);
        /* If our LPC filter resonates too close to DC, retry the analysis with down-sampling. */
        while (delay <= Fs / 3000 && (fail != 0 || (lpc[0] > 536870912 /* QCONST32(1.f, 29) */ && lpc[1] < 0)))
        {
            delay *= 2;
            fail = tone_lpc(x, N, delay, lpc);
        }
        /* Check that our filter has complex roots. */
        if (fail == 0 && MULT32_32_Q31(lpc[0], lpc[0]) + MULT32_32_Q31(2147483111 /* QCONST32(3.999999, 29) */, lpc[1]) < 0)
        {
            /* Squared radius of the poles. */
            toneishness = -lpc[1];
            freq = (short)((acos_approx(lpc[0] >> 1) + (delay / 2)) / delay);
        }
        else
        {
            freq = -1;
            toneishness = 0;
        }
        /*printf("%f %f %f %f\n", freq, lpc[0], lpc[1], *toneishness);*/
        return freq;
    }

    // The sum of ABS32(SHR32(x, 12)) over x, four lanes at a time (integer adds, so in any order)
    private static int AbsSum12(ReadOnlySpan<int> x)
    {
        int sum = 0;
        int i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            ref int v = ref MemoryMarshal.GetReference(x);
            var lanes = Vector128<int>.Zero;
            for (; i + 4 <= x.Length; i += 4)
            {
                lanes += Vector128.Abs(Vector128.LoadUnsafe(ref v, (nuint)i) >> 12);
            }
            sum = Vector128.Sum(lanes);
        }
        for (; i < x.Length; i++)
        {
            sum += ABS32(SHR32(x[i], 12));
        }
        return sum;
    }

    private static int run_prefilter(OpusCustomEncoder st, Span<int> @in, Span<int> prefilter_mem, int CC, int N,
          int prefilter_tapset, out int pitch, out short gain, out int qgain, int enabled, int complexity, short tf_estimate,
          int nbAvailableBytes, ref AnalysisInfo analysis, short tone_freq, int toneishness)
    {
        int c;
        OpusCustomMode mode;
        int pitch_index;
        short gain1;
        short pf_threshold;
        int pf_on;
        int qg;
        int overlap;
        int min_period, max_period;
        Span<int> before = stackalloc int[2];
        Span<int> after = stackalloc int[2];
        int cancel_pitch = 0;

        before.Clear();
        after.Clear();
        max_period = COMBFILTER_MAXPERIOD;
        min_period = COMBFILTER_MINPERIOD;
        mode = st.mode;
        overlap = mode.overlap;
        Span<int> _pre = st.pre_buf.AsSpan(0, CC * (N + max_period));

        Span<int> pre0 = _pre.Slice(0, N + max_period);
        Span<int> pre1 = CC == 2 ? _pre.Slice(N + max_period, N + max_period) : default;
        celt_sig_ptrs pre = new celt_sig_ptrs(pre0, pre1);

        c = 0;
        do
        {
            prefilter_mem.Slice(c * max_period, max_period).CopyTo(pre[c]);
            @in.Slice((c * (N + overlap)) + overlap, N).CopyTo(pre[c].Slice(max_period));
        }
        while (++c < CC);

        /* If we detect that the signal is dominated by a single tone, don't rely on the standard pitch
           estimator, as it can become unreliable. */
        if (enabled != 0 && toneishness > 531502208 /* QCONST32(.99f, 29) */)
        {
            int multiple = 1;
            /* Using aliased version of the postfilter above 24 kHz.
               First value is purposely slightly above pi to avoid triggering for Fs=48kHz. */
            if (tone_freq >= 25736 /* QCONST16(3.1416f, 13) */)
            {
                tone_freq = (short)(25736 /* QCONST16(3.141593f, 13) */ - tone_freq);
            }
            /* If the pitch is too high for our post-filter, apply pitch doubling until
               we can get something that fits (not ideal, but better than nothing). */
            while (tone_freq >= multiple * 3195 /* QCONST16(0.39f, 13) */)
            {
                multiple++;
            }
            if (tone_freq > 50 /* QCONST16(0.006148f, 13) */)
            {
                pitch_index = IMIN(((51472 * multiple) + (tone_freq / 2)) / tone_freq, COMBFILTER_MAXPERIOD - 2);
            }
            else
            {
                /* If the pitch is too low, using a very high pitch will actually give us an improvement
                   due to the DC component of the filter that will be close to our tone. Again, not ideal,
                   but if we only have a single tone, it's better than nothing. */
                pitch_index = COMBFILTER_MINPERIOD;
            }
            gain1 = 24576; /* QCONST16(.75f, 15) */
        }
        else if (enabled != 0 && complexity >= 5)
        {
            Span<short> pitch_buf = stackalloc short[(max_period + N) >> 1];

            Pitch.pitch_downsample(pre, pitch_buf, (max_period + N) >> 1, CC, 2);
            /* Don't search for the fir last 1.5 octave of the range because
               there's too many false-positives due to short-term correlation */
            Pitch.pitch_search(pitch_buf.Slice(max_period >> 1), pitch_buf, N,
                  max_period - (3 * min_period), out pitch_index);
            pitch_index = max_period - pitch_index;

            gain1 = PitchEnc.remove_doubling(pitch_buf, max_period, min_period,
                  N, ref pitch_index, st.prefilter_period, st.prefilter_gain);
            if (pitch_index > max_period - 2)
            {
                pitch_index = max_period - 2;
            }
            gain1 = (short)MULT16_16_Q15(22938 /* QCONST16(.7f,15) */, gain1);
            /*printf("%d %d %f %f\n", pitch_change, pitch_index, gain1, st->analysis.tonality);*/
            if (st.loss_rate > 2)
            {
                gain1 = (short)HALF32(gain1);
            }
            if (st.loss_rate > 4)
            {
                gain1 = (short)HALF32(gain1);
            }
            if (st.loss_rate > 8)
            {
                gain1 = 0;
            }
        }
        else
        {
            gain1 = 0;
            pitch_index = COMBFILTER_MINPERIOD;
        }
        if (analysis.valid != 0)
        {
            gain1 = (short)(gain1 * analysis.max_pitch_ratio);
        }
        /* Gain threshold for enabling the prefilter/postfilter */
        pf_threshold = 6554; /* QCONST16(.2f,15) */

        /* Adjusting the threshold based on rate and continuity */
        if (ABS32(pitch_index - st.prefilter_period) * 10 > pitch_index)
        {
            pf_threshold += 6554; /* QCONST16(.2f,15) */
            /* Completely disable the prefilter on strong transients without continuity. */
            if (tf_estimate > 16056 /* QCONST16(.98f, 14) */)
            {
                gain1 = 0;
            }
        }
        if (nbAvailableBytes < 25)
        {
            pf_threshold += 3277; /* QCONST16(.1f,15) */
        }
        if (nbAvailableBytes < 35)
        {
            pf_threshold += 3277; /* QCONST16(.1f,15) */
        }
        if (st.prefilter_gain > 13107 /* QCONST16(.4f,15) */)
        {
            pf_threshold -= 3277; /* QCONST16(.1f,15) */
        }
        if (st.prefilter_gain > 18022 /* QCONST16(.55f,15) */)
        {
            pf_threshold -= 3277; /* QCONST16(.1f,15) */
        }

        /* Hard threshold at 0.2 */
        pf_threshold = (short)MAX16(pf_threshold, 6554 /* QCONST16(.2f,15) */);
        if (gain1 < pf_threshold)
        {
            gain1 = 0;
            pf_on = 0;
            qg = 0;
        }
        else
        {
            /*This block is not gated by a total bits check only because
              of the nbAvailableBytes check above.*/
            if (ABS16(gain1 - st.prefilter_gain) < 3277 /* QCONST16(.1f,15) */)
            {
                gain1 = st.prefilter_gain;
            }

            qg = (((gain1 + 1536) >> 10) / 3) - 1;
            qg = IMAX(0, IMIN(7, qg));
            gain1 = (short)(3072 /* QCONST16(0.09375f,15) */ * (qg + 1));
            pf_on = 1;
        }
        /*printf("%d %f\n", pitch_index, gain1);*/

        c = 0;
        do
        {
            int offset = mode.shortMdctSize - overlap;
            st.prefilter_period = IMAX(st.prefilter_period, COMBFILTER_MINPERIOD);
            st.in_mem.AsSpan(c * overlap, overlap).CopyTo(@in.Slice(c * (N + overlap)));
            before[c] += AbsSum12(@in.Slice((c * (N + overlap)) + overlap, N));
            if (offset != 0)
            {
                DotGame.AudioCodecs.Opus.Celt.Celt.comb_filter(@in.Slice((c * (N + overlap)) + overlap), pre[c], max_period,
                      st.prefilter_period, st.prefilter_period, offset, (short)-st.prefilter_gain, (short)-st.prefilter_gain,
                      st.prefilter_tapset, st.prefilter_tapset, default, 0);
            }

            DotGame.AudioCodecs.Opus.Celt.Celt.comb_filter(@in.Slice((c * (N + overlap)) + overlap + offset), pre[c], max_period + offset,
                  st.prefilter_period, pitch_index, N - offset, (short)-st.prefilter_gain, (short)-gain1,
                  st.prefilter_tapset, prefilter_tapset, mode.window, overlap);
            after[c] += AbsSum12(@in.Slice((c * (N + overlap)) + overlap, N));
        }
        while (++c < CC);

        if (CC == 2)
        {
            Span<short> thresh = stackalloc short[2];
            thresh[0] = (short)(MULT16_32_Q15(MULT16_16_Q15(8192 /* QCONST16(.25f, 15) */, gain1), before[0]) + MULT16_32_Q15(328 /* QCONST16(.01f,15) */, before[1]));
            thresh[1] = (short)(MULT16_32_Q15(MULT16_16_Q15(8192 /* QCONST16(.25f, 15) */, gain1), before[1]) + MULT16_32_Q15(328 /* QCONST16(.01f,15) */, before[0]));
            /* Don't use the filter if one channel gets significantly worse. */
            if (after[0] - before[0] > thresh[0] || after[1] - before[1] > thresh[1])
            {
                cancel_pitch = 1;
            }
            /* Use the filter only if at least one channel gets significantly better. */
            if (before[0] - after[0] < thresh[0] && before[1] - after[1] < thresh[1])
            {
                cancel_pitch = 1;
            }
        }
        else
        {
            /* Check that the mono channel actually got better. */
            if (after[0] > before[0])
            {
                cancel_pitch = 1;
            }
        }
        /* If needed, revert to a gain of zero. */
        if (cancel_pitch != 0)
        {
            c = 0;
            do
            {
                int offset = mode.shortMdctSize - overlap;
                pre[c].Slice(max_period, N).CopyTo(@in.Slice((c * (N + overlap)) + overlap));
                DotGame.AudioCodecs.Opus.Celt.Celt.comb_filter(@in.Slice((c * (N + overlap)) + overlap + offset), pre[c], max_period + offset,
                      st.prefilter_period, pitch_index, overlap, (short)-st.prefilter_gain, 0,
                      st.prefilter_tapset, prefilter_tapset, mode.window, overlap);
            }
            while (++c < CC);
            gain1 = 0;
            pf_on = 0;
            qg = 0;
        }

        c = 0;
        do
        {
            @in.Slice((c * (N + overlap)) + N, overlap).CopyTo(st.in_mem.AsSpan(c * overlap));

            if (N > max_period)
            {
                pre[c].Slice(N, max_period).CopyTo(prefilter_mem.Slice(c * max_period));
            }
            else
            {
                prefilter_mem.Slice((c * max_period) + N, max_period - N).CopyTo(prefilter_mem.Slice(c * max_period));
                pre[c].Slice(max_period, N).CopyTo(prefilter_mem.Slice((c * max_period) + max_period - N));
            }
        }
        while (++c < CC);

        gain = gain1;
        pitch = pitch_index;
        qgain = qg;
        return pf_on;
    }

    private static int compute_vbr(OpusCustomMode mode, ref AnalysisInfo analysis, int base_target,
          int LM, int bitrate, int lastCodedBands, int C, int intensity,
          int constrained_vbr, short stereo_saving, int tot_boost,
          short tf_estimate, int pitch_change, int maxDepth,
          int lfe, int has_surround_mask, int surround_masking,
          int temporal_vbr)
    {
        /* The target rate in 8th bits per frame */
        int target;
        int coded_bins;
        int coded_bands;
        short tf_calibration;
        int nbEBands;
        short[] eBands;

        nbEBands = mode.nbEBands;
        eBands = mode.eBands;

        coded_bands = lastCodedBands != 0 ? lastCodedBands : nbEBands;
        coded_bins = eBands[coded_bands] << LM;
        if (C == 2)
        {
            coded_bins += eBands[IMIN(intensity, coded_bands)] << LM;
        }

        target = base_target;

        /*printf("%f %f %f %f %d %d ", st->analysis.activity, st->analysis.tonality, tf_estimate, st->stereo_saving, tot_boost, coded_bands);*/
        if (analysis.valid != 0 && analysis.activity < .4)
        {
            target -= (int)((coded_bins << BITRES) * (.4f - analysis.activity));
        }
        /* Stereo savings */
        if (C == 2)
        {
            int coded_stereo_bands;
            int coded_stereo_dof;
            short max_frac;
            coded_stereo_bands = IMIN(intensity, coded_bands);
            coded_stereo_dof = (eBands[coded_stereo_bands] << LM) - coded_stereo_bands;
            /* Maximum fraction of the bits we can save if the signal is mono. */
            max_frac = DIV32_16(MULT16_16(26214 /* QCONST16(0.8f, 15) */, coded_stereo_dof), coded_bins);
            stereo_saving = (short)MIN16(stereo_saving, 256 /* QCONST16(1.f, 8) */);
            /*printf("%d %d %d ", coded_stereo_dof, coded_bins, tot_boost);*/
            target -= (int)MIN32(MULT16_32_Q15(max_frac, target),
                            SHR32(MULT16_16(stereo_saving - 26 /* QCONST16(0.1f,8) */, (coded_stereo_dof << BITRES)), 8));
        }
        /* Boost the rate according to dynalloc (minus the dynalloc average for calibration). */
        target += tot_boost - (19 << LM);
        /* Apply transient boost, compensating for average boost. */
        tf_calibration = 721; /* QCONST16(0.044f,14) */
        target += (int)SHL32(MULT16_32_Q15(tf_estimate - tf_calibration, target), 1);

        /* Apply tonality boost */
        if (analysis.valid != 0 && lfe == 0)
        {
            int tonal_target;
            float tonal;

            /* Tonality boost (compensating for the average). */
            tonal = MathF.Max(0.0f, analysis.tonality - .15f) - 0.12f;
            tonal_target = target + (int)((coded_bins << BITRES) * 1.2f * tonal);
            if (pitch_change != 0)
            {
                tonal_target += (int)((coded_bins << BITRES) * .8f);
            }
            /*printf("%f %f ", analysis->tonality, tonal);*/
            target = tonal_target;
        }

        if (has_surround_mask != 0 && lfe == 0)
        {
            int surround_target = target + (int)SHR32(MULT16_16(SHR32(surround_masking, DB_SHIFT - 10), coded_bins << BITRES), 10);
            /*printf("%f %d %d %d %d %d %d ", surround_masking, coded_bins, st->end, st->intensity, surround_target, target, st->bitrate);*/
            target = IMAX(target / 4, surround_target);
        }

        {
            int floor_depth;
            int bins;
            bins = eBands[nbEBands - 2] << LM;
            /*floor_depth = SHR32(MULT16_16((C*bins<<BITRES),celt_log2(SHL32(MAX16(1,sample_max),13))), DB_SHIFT);*/
            floor_depth = (int)SHR32(MULT16_32_Q15(((C * bins) << BITRES), maxDepth), DB_SHIFT - 15);
            floor_depth = IMAX(floor_depth, target >> 2);
            target = IMIN(target, floor_depth);
            /*printf("%f %d\n", maxDepth, floor_depth);*/
        }

        /* Make VBR less aggressive for constrained VBR because we can't keep a higher bitrate
           for long. Needs tuning. */
        if ((has_surround_mask == 0 || lfe != 0) && constrained_vbr != 0)
        {
            target = base_target + (int)MULT16_32_Q15(21955 /* QCONST16(0.67f, 15) */, target - base_target);
        }

        if (has_surround_mask == 0 && tf_estimate < 3277 /* QCONST16(.2f, 14) */)
        {
            short amount;
            short tvbr_factor;
            amount = (short)MULT16_16_Q15(3329 /* QCONST16(.0000031f, 30) */, IMAX(0, IMIN(32000, 96000 - bitrate)));
            tvbr_factor = (short)SHR32(MULT16_16(SHR32(temporal_vbr, DB_SHIFT - 10), amount), 10);
            target += (int)MULT16_32_Q15(tvbr_factor, target);
        }

        /* Don't allow more than doubling the rate */
        target = IMIN(2 * base_target, target);

        return target;
    }

    private static readonly short[] intensity_thresholds =
    /* 0  1  2  3  4  5  6  7  8  9 10 11 12 13 14 15 16 17 18 19  20  off*/
      [1, 2, 3, 4, 5, 6, 7, 8, 16, 24, 36, 44, 50, 56, 62, 67, 72, 79, 88, 106, 134];

    private static readonly short[] intensity_histeresis =
      [1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 3, 3, 4, 5, 6, 8, 8];

    /* celt_encode_with_ec(). pcm empty stands for C's NULL, as does compressed (only used when
       haveEnc is false); haveEnc false is C's enc == NULL, when the frame is coded with a range
       encoder over compressed. */
    internal static int celt_encode_with_ec(OpusCustomEncoder st, ReadOnlySpan<int> pcm, int frame_size,
          Span<byte> compressed, int nbCompressedBytes, ref ec_enc enc, bool haveEnc)
    {
        int i, c, N;
        int bits;
        ec_enc _enc = default;
        Span<int> prefilter_mem;
        Span<int> oldBandE, oldLogE, oldLogE2, energyError;
        int shortBlocks = 0;
        int isTransient = 0;
        int CC = st.channels;
        int C = st.stream_channels;
        int LM, M;
        int tf_select;
        int nbFilledBytes, nbAvailableBytes;
        int min_allowed;
        int start;
        int end;
        int effEnd;
        int codedBands;
        int alloc_trim;
        int pitch_index = COMBFILTER_MINPERIOD;
        short gain1 = 0;
        int dual_stereo = 0;
        int effectiveBytes;
        int dynalloc_logp;
        int vbr_rate;
        int total_bits;
        int total_boost;
        int balance;
        int tell;
        int tell0_frac;
        int prefilter_tapset = 0;
        int pf_on;
        int anti_collapse_rsv;
        int anti_collapse_on = 0;
        int silence = 0;
        int tf_chan = 0;
        short tf_estimate;
        int pitch_change = 0;
        int tot_boost;
        int sample_max;
        int maxDepth;
        OpusCustomMode mode;
        int nbEBands;
        int overlap;
        short[] eBands;
        int secondMdct;
        int signalBandwidth;
        int transient_got_disabled = 0;
        int surround_masking = 0;
        int temporal_vbr = 0;
        int surround_trim = 0;
        int equiv_rate;
        int hybrid;
        int weak_transient = 0;
        int enable_tf_analysis;
        short tone_freq = -1;
        int toneishness = 0;
        int qext_bytes = 0;
        int packet_size_cap = 1275;

        mode = st.mode;
        nbEBands = mode.nbEBands;
        overlap = mode.overlap;
        eBands = mode.eBands;
        start = st.start;
        end = st.end;
        hybrid = start != 0 ? 1 : 0;
        tf_estimate = 0;
        if (nbCompressedBytes < 2 || pcm.IsEmpty)
        {
            return OPUS_BAD_ARG;
        }

        frame_size *= st.upsample;
        for (LM = 0; LM <= mode.maxLM; LM++)
        {
            if (mode.shortMdctSize << LM == frame_size)
            {
                break;
            }
        }
        if (LM > mode.maxLM)
        {
            return OPUS_BAD_ARG;
        }
        M = 1 << LM;
        N = M * mode.shortMdctSize;

        prefilter_mem = st.prefilter_mem;
        oldBandE = st.oldBandE;
        oldLogE = st.oldLogE;
        oldLogE2 = st.oldLogE2;
        energyError = st.energyError;

        if (!haveEnc)
        {
            tell0_frac = tell = 1;
            nbFilledBytes = 0;
        }
        else
        {
            tell0_frac = (int)enc.ec_tell_frac();
            tell = enc.ec_tell();
            nbFilledBytes = (tell + 4) >> 3;
        }

        Debug.Assert(st.signalling == 0);

        /* Can't produce more than 1275 output bytes for the main payload, plus any QEXT extra data. */
        nbCompressedBytes = IMIN(nbCompressedBytes, packet_size_cap);

        if (st.vbr != 0 && st.bitrate != OpusCustomEncoder.OPUS_BITRATE_MAX)
        {
            vbr_rate = CeltEnc.bitrate_to_bits(st.bitrate, mode.Fs, frame_size) << BITRES;
            effectiveBytes = vbr_rate >> (3 + BITRES);
        }
        else
        {
            int tmp;
            vbr_rate = 0;
            tmp = st.bitrate * frame_size;
            if (tell > 1)
            {
                tmp += tell * mode.Fs;
            }
            if (st.bitrate != OpusCustomEncoder.OPUS_BITRATE_MAX)
            {
                nbCompressedBytes = IMAX(2, IMIN(nbCompressedBytes,
                      ((tmp + (4 * mode.Fs)) / (8 * mode.Fs)) - (st.signalling != 0 ? 1 : 0)));
                if (haveEnc)
                {
                    enc.ec_enc_shrink((uint)nbCompressedBytes);
                }
            }
            effectiveBytes = nbCompressedBytes - nbFilledBytes;
        }
        nbAvailableBytes = nbCompressedBytes - nbFilledBytes;
        equiv_rate = (((int)nbCompressedBytes * 8 * 50) << (3 - LM)) - (((40 * C) + 20) * ((400 >> LM) - 50));
        if (st.bitrate != OpusCustomEncoder.OPUS_BITRATE_MAX)
        {
            equiv_rate = IMIN(equiv_rate, st.bitrate - (((40 * C) + 20) * ((400 >> LM) - 50)));
        }

        scoped ref ec_enc e = ref enc;
        if (!haveEnc)
        {
            _enc.ec_enc_init(compressed, (uint)nbCompressedBytes);
            e = ref _enc;
        }

        if (vbr_rate > 0)
        {
            /* Computes the max bit-rate allowed in VBR mode to avoid violating the
                target rate and buffering.
               We must do this up front so that bust-prevention logic triggers
                correctly if we don't have enough bits. */
            if (st.constrained_vbr != 0)
            {
                int vbr_bound;
                int max_allowed;
                /* We could use any multiple of vbr_rate as bound (depending on the
                    delay).
                   This is clamped to ensure we use at least two bytes if the encoder
                    was entirely empty, but to allow 0 in hybrid mode. */
                vbr_bound = vbr_rate;
                max_allowed = IMIN(IMAX(tell == 1 ? 2 : 0,
                      (vbr_rate + vbr_bound - st.vbr_reservoir) >> (BITRES + 3)),
                      nbAvailableBytes);
                if (max_allowed < nbAvailableBytes)
                {
                    nbCompressedBytes = nbFilledBytes + max_allowed;
                    nbAvailableBytes = max_allowed;
                    e.ec_enc_shrink((uint)nbCompressedBytes);
                }
            }
        }
        total_bits = nbCompressedBytes * 8;

        effEnd = end;
        if (effEnd > mode.effEBands)
        {
            effEnd = mode.effEBands;
        }

        Span<int> @in = st.in_buf.AsSpan(0, CC * (N + overlap));

        sample_max = MAX32(st.overlap_max, celt_maxabs_res(pcm, C * (N - overlap) / st.upsample));
        st.overlap_max = celt_maxabs_res(pcm.Slice(C * (N - overlap) / st.upsample), C * overlap / st.upsample);
        sample_max = MAX32(sample_max, st.overlap_max);
        silence = sample_max == 0 ? 1 : 0;
        if (tell == 1)
        {
            e.ec_enc_bit_logp(silence, 15);
        }
        else
        {
            silence = 0;
        }
        if (silence != 0)
        {
            /*In VBR mode there is no need to send more than the minimum. */
            if (vbr_rate > 0)
            {
                effectiveBytes = nbCompressedBytes = IMIN(nbCompressedBytes, nbFilledBytes + 2);
                total_bits = nbCompressedBytes * 8;
                nbAvailableBytes = 2;
                e.ec_enc_shrink((uint)nbCompressedBytes);
            }
            /* Pretend we've filled all the remaining bits with zeros
                  (that's what the initialiser did anyway) */
            tell = nbCompressedBytes * 8;
            e.nbits_total += tell - e.ec_tell();
        }
        c = 0;
        do
        {
            int need_clip = 0;
            need_clip = st.clip != 0 && sample_max > 65536 << RES_SHIFT ? 1 : 0;
            celt_preemphasis(pcm.Slice(c), @in.Slice((c * (N + overlap)) + overlap), N, CC, st.upsample,
                        mode.preemph, ref st.preemph_memE[c], need_clip);
            prefilter_mem.Slice(((1 + c) * COMBFILTER_MAXPERIOD) - overlap, overlap).CopyTo(@in.Slice(c * (N + overlap)));
        }
        while (++c < CC);

        tone_freq = tone_detect(@in, CC, N + overlap, out toneishness, mode.Fs);
        isTransient = 0;
        shortBlocks = 0;
        if (st.complexity >= 1 && st.lfe == 0)
        {
            /* Reduces the likelihood of energy instability on fricatives at low bitrate
               in hybrid mode. It seems like we still want to have real transients on vowels
               though (small SILK quantization offset value). */
            int allow_weak_transients = hybrid != 0 && effectiveBytes < 15 && st.silk_info.signalType != 2 ? 1 : 0;
            isTransient = transient_analysis(@in, N + overlap, CC,
                  out tf_estimate, ref tf_chan, allow_weak_transients, out weak_transient, tone_freq, toneishness);
        }
        toneishness = MIN32(toneishness, 536870912 /* QCONST32(1.f, 29) */ - SHL32(tf_estimate, 15));
        /* Find pitch period and gain */
        {
            int enabled;
            int qg;
            enabled = ((st.lfe != 0 && nbAvailableBytes > 3) || nbAvailableBytes > 12 * C) && hybrid == 0 && silence == 0 && tell + 16 <= total_bits && st.disable_pf == 0 ? 1 : 0;

            prefilter_tapset = st.tapset_decision;
            pf_on = run_prefilter(st, @in, prefilter_mem, CC, N, prefilter_tapset, out pitch_index, out gain1, out qg, enabled, st.complexity, tf_estimate, nbAvailableBytes, ref st.analysis, tone_freq, toneishness);
            if ((gain1 > 13107 /* QCONST16(.4f,15) */ || st.prefilter_gain > 13107 /* QCONST16(.4f,15) */) && (st.analysis.valid == 0 || st.analysis.tonality > .3)
                  && (pitch_index > 1.26 * st.prefilter_period || pitch_index < .79 * st.prefilter_period))
            {
                pitch_change = 1;
            }
            if (pf_on == 0)
            {
                if (hybrid == 0 && tell + 16 <= total_bits)
                {
                    e.ec_enc_bit_logp(0, 1);
                }
            }
            else
            {
                /*This block is not gated by a total bits check only because
                  of the nbAvailableBytes check above.*/
                int octave;
                e.ec_enc_bit_logp(1, 1);
                pitch_index += 1;
                octave = EC_ILOG((uint)pitch_index) - 5;
                e.ec_enc_uint((uint)octave, 6);
                e.ec_enc_bits((uint)(pitch_index - (16 << octave)), (uint)(4 + octave));
                pitch_index -= 1;
                e.ec_enc_bits((uint)qg, 3);
                e.ec_enc_icdf(prefilter_tapset, DotGame.AudioCodecs.Opus.Celt.Celt.tapset_icdf, 2);
            }
        }
        if (LM > 0 && e.ec_tell() + 3 <= total_bits)
        {
            if (isTransient != 0)
            {
                shortBlocks = M;
            }
        }
        else
        {
            isTransient = 0;
            transient_got_disabled = 1;
        }

        Span<int> freq = st.freq_buf.AsSpan(0, CC * N); /*< Interleaved signal MDCTs */
        Span<int> bandE = st.bandE_buf.AsSpan(0, nbEBands * CC);
        Span<int> bandLogE = stackalloc int[nbEBands * CC];

        secondMdct = shortBlocks != 0 && st.complexity >= 8 ? 1 : 0;
        Span<int> bandLogE2 = stackalloc int[C * nbEBands];
        if (secondMdct != 0)
        {
            compute_mdcts(mode, 0, @in, freq, C, CC, LM, st.upsample);
            BandsEnc.compute_band_energies(mode, freq, bandE, effEnd, C, LM);
            QuantBandsEnc.amp2Log2(mode, effEnd, end, bandE, bandLogE2, C);
            for (c = 0; c < C; c++)
            {
                for (i = 0; i < end; i++)
                {
                    bandLogE2[(nbEBands * c) + i] += HALF32(SHL32(LM, DB_SHIFT));
                }
            }
        }

        compute_mdcts(mode, shortBlocks, @in, freq, C, CC, LM, st.upsample);
        /* This should catch any NaN in the CELT input. Since we're not supposed to see any (they're filtered
           at the Opus layer), just abort. */
        if (CC == 2 && C == 1)
        {
            tf_chan = 0;
        }
        BandsEnc.compute_band_energies(mode, freq, bandE, effEnd, C, LM);

        if (st.lfe != 0)
        {
            for (i = 2; i < end; i++)
            {
                bandE[i] = IMIN(bandE[i], MULT16_32_Q15(3 /* QCONST16(1e-4f,15) */, bandE[0]));
                bandE[i] = MAX32(bandE[i], EPSILON);
            }
        }
        QuantBandsEnc.amp2Log2(mode, effEnd, end, bandE, bandLogE, C);

        Span<int> surround_dynalloc = stackalloc int[C * nbEBands];
        surround_dynalloc.Slice(0, end).Clear();
        /* This computes how much masking takes place between surround channels */
        if (hybrid == 0 && st.energy_mask != null && st.lfe == 0)
        {
            int[] energy_mask = st.energy_mask;
            int mask_end;
            int midband;
            int count_dynalloc;
            int mask_avg = 0;
            int diff = 0;
            int count = 0;
            mask_end = IMAX(2, st.lastCodedBands);
            for (c = 0; c < C; c++)
            {
                for (i = 0; i < mask_end; i++)
                {
                    int mask;
                    short mask16;
                    mask = MAXG(MING(energy_mask[(nbEBands * c) + i],
                           4194304 /* GCONST(.25f) */), -33554432 /* -GCONST(2.0f) */);
                    if (mask > 0)
                    {
                        mask = HALF32(mask);
                    }
                    mask16 = (short)SHR32(mask, DB_SHIFT - 10);
                    mask_avg += MULT16_16(mask16, eBands[i + 1] - eBands[i]);
                    count += eBands[i + 1] - eBands[i];
                    diff += MULT16_16(mask16, 1 + (2 * i) - mask_end);
                }
            }
            Debug.Assert(count > 0);
            mask_avg = SHL32(DIV32_16(mask_avg, count), DB_SHIFT - 10);
            mask_avg += 3355443; /* GCONST(.2f) */
            diff = SHL32(diff * 6 / (C * (mask_end - 1) * (mask_end + 1) * mask_end), DB_SHIFT - 10);
            /* Again, being conservative */
            diff = HALF32(diff);
            diff = MAX32(MIN32(diff, 520094 /* GCONST(.031f) */), -520094 /* -GCONST(.031f) */);
            /* Find the band that's in the middle of the coded spectrum */
            for (midband = 0; eBands[midband + 1] < eBands[mask_end] / 2; midband++)
            {
            }
            count_dynalloc = 0;
            for (i = 0; i < mask_end; i++)
            {
                int lin;
                int unmask;
                lin = mask_avg + (diff * (i - midband));
                if (C == 2)
                {
                    unmask = MAXG(energy_mask[i], energy_mask[nbEBands + i]);
                }
                else
                {
                    unmask = energy_mask[i];
                }
                unmask = MING(unmask, 0 /* GCONST(.0f) */);
                unmask -= lin;
                if (unmask > 4194304 /* GCONST(.25f) */)
                {
                    surround_dynalloc[i] = unmask - 4194304; /* GCONST(.25f) */
                    count_dynalloc++;
                }
            }
            if (count_dynalloc >= 3)
            {
                /* If we need dynalloc in many bands, it's probably because our
                   initial masking rate was too low. */
                mask_avg += 4194304; /* GCONST(.25f) */
                if (mask_avg > 0)
                {
                    /* Something went really wrong in the original calculations,
                       disabling masking. */
                    mask_avg = 0;
                    diff = 0;
                    surround_dynalloc.Slice(0, mask_end).Clear();
                }
                else
                {
                    for (i = 0; i < mask_end; i++)
                    {
                        surround_dynalloc[i] = MAXG(0, surround_dynalloc[i] - 4194304 /* GCONST(.25f) */);
                    }
                }
            }
            mask_avg += 3355443; /* GCONST(.2f) */
            /* Convert to 1/64th units used for the trim */
            surround_trim = 64 * diff;
            /*printf("%d %d ", mask_avg, surround_trim);*/
            surround_masking = mask_avg;
        }
        /* Temporal VBR (but not for LFE) */
        if (st.lfe == 0)
        {
            int follow = -5242880; /* -QCONST32(10.0f, DB_SHIFT-5) */
            int frame_avg = 0;
            int offset = shortBlocks != 0 ? HALF32(SHL32(LM, DB_SHIFT - 5)) : 0;
            for (i = start; i < end; i++)
            {
                follow = MAXG(follow - 524288 /* QCONST32(1.0f, DB_SHIFT-5) */, SHR32(bandLogE[i], 5) - offset);
                if (C == 2)
                {
                    follow = MAXG(follow, SHR32(bandLogE[i + nbEBands], 5) - offset);
                }
                frame_avg += follow;
            }
            frame_avg /= (end - start);
            temporal_vbr = SUB32(SHL32(frame_avg, 5), st.spec_avg);
            temporal_vbr = MING(50331648 /* GCONST(3.f) */, MAXG(-25165824 /* -GCONST(1.5f) */, temporal_vbr));
            st.spec_avg += MULT16_32_Q15(655 /* QCONST16(.02f, 15) */, temporal_vbr);
        }
        /*for (i=0;i<21;i++)
           printf("%f ", bandLogE[i]);
        printf("\n");*/

        if (secondMdct == 0)
        {
            bandLogE.Slice(0, C * nbEBands).CopyTo(bandLogE2);
        }

        /* Last chance to catch any transient we might have missed in the
           time-domain analysis */
        if (LM > 0 && e.ec_tell() + 3 <= total_bits && isTransient == 0 && st.complexity >= 5 && st.lfe == 0 && hybrid == 0)
        {
            if (patch_transient_decision(bandLogE, oldBandE, nbEBands, start, end, C) != 0)
            {
                isTransient = 1;
                shortBlocks = M;
                compute_mdcts(mode, shortBlocks, @in, freq, C, CC, LM, st.upsample);
                BandsEnc.compute_band_energies(mode, freq, bandE, effEnd, C, LM);
                QuantBandsEnc.amp2Log2(mode, effEnd, end, bandE, bandLogE, C);
                /* Compensate for the scaling of short vs long mdcts */
                for (c = 0; c < C; c++)
                {
                    for (i = 0; i < end; i++)
                    {
                        bandLogE2[(nbEBands * c) + i] += HALF32(SHL32(LM, DB_SHIFT));
                    }
                }
                tf_estimate = 3277; /* QCONST16(.2f,14) */
            }
        }

        if (LM > 0 && e.ec_tell() + 3 <= total_bits)
        {
            e.ec_enc_bit_logp(isTransient, 3);
        }

        Span<int> X = st.X_buf.AsSpan(0, C * N); /*< Interleaved normalised MDCTs */
        /* Band normalisation */
        BandsEnc.normalise_bands(mode, freq, X, bandE, effEnd, C, M);

        enable_tf_analysis = effectiveBytes >= 15 * C && hybrid == 0 && st.complexity >= 2 && st.lfe == 0 && toneishness < 526133504 /* QCONST32(.98f, 29) */ ? 1 : 0;

        Span<int> offsets = stackalloc int[nbEBands];
        Span<int> importance = stackalloc int[nbEBands];
        Span<int> spread_weight = stackalloc int[nbEBands];

        maxDepth = dynalloc_analysis(bandLogE, bandLogE2, oldBandE, nbEBands, start, end, C, offsets,
              st.lsb_depth, mode.logN, isTransient, st.vbr, st.constrained_vbr,
              eBands, LM, effectiveBytes, out tot_boost, st.lfe, surround_dynalloc, ref st.analysis, importance, spread_weight, tone_freq, toneishness);

        Span<int> tf_res = stackalloc int[nbEBands];
        /* Disable variable tf resolution for hybrid and at very low bitrate */
        if (enable_tf_analysis != 0)
        {
            int lambda;
            lambda = IMAX(80, (20480 / effectiveBytes) + 2);
            tf_select = tf_analysis(mode, effEnd, isTransient, tf_res, lambda, X, N, LM, tf_estimate, tf_chan, importance);
            for (i = effEnd; i < end; i++)
            {
                tf_res[i] = tf_res[effEnd - 1];
            }
        }
        else if (hybrid != 0 && weak_transient != 0)
        {
            /* For weak transients, we rely on the fact that improving time resolution using
               TF on a long window is imperfect and will not result in an energy collapse at
               low bitrate. */
            for (i = 0; i < end; i++)
            {
                tf_res[i] = 1;
            }
            tf_select = 0;
        }
        else if (hybrid != 0 && effectiveBytes < 15 && st.silk_info.signalType != 2)
        {
            /* For low bitrate hybrid, we force temporal resolution to 5 ms rather than 2.5 ms. */
            for (i = 0; i < end; i++)
            {
                tf_res[i] = 0;
            }
            tf_select = isTransient;
        }
        else
        {
            for (i = 0; i < end; i++)
            {
                tf_res[i] = isTransient;
            }
            tf_select = 0;
        }

        Span<int> error = stackalloc int[C * nbEBands];
        c = 0;
        do
        {
            for (i = start; i < end; i++)
            {
                /* When the energy is stable, slightly bias energy quantization towards
                   the previous error to make the gain more stable (a constant offset is
                   better than fluctuations). */
                if (ABS32(SUB32(bandLogE[i + (c * nbEBands)], oldBandE[i + (c * nbEBands)])) < 33554432 /* GCONST(2.f) */)
                {
                    bandLogE[i + (c * nbEBands)] -= MULT16_32_Q15(8192 /* QCONST16(0.25f, 15) */, energyError[i + (c * nbEBands)]);
                }
            }
        }
        while (++c < C);
        QuantBandsEnc.quant_coarse_energy(mode, start, end, effEnd, bandLogE,
              oldBandE, (uint)total_bits, error, ref e,
              C, LM, nbAvailableBytes, st.force_intra,
              ref st.delayedIntra, st.complexity >= 4 ? 1 : 0, st.loss_rate, st.lfe);

        tf_encode(start, end, isTransient, tf_res, LM, tf_select, ref e);

        if (e.ec_tell() + 4 <= total_bits)
        {
            if (st.lfe != 0)
            {
                st.tapset_decision = 0;
                st.spread_decision = Bands.SPREAD_NORMAL;
            }
            else if (hybrid != 0)
            {
                if (st.complexity == 0)
                {
                    st.spread_decision = Bands.SPREAD_NONE;
                }
                else if (isTransient != 0)
                {
                    st.spread_decision = Bands.SPREAD_NORMAL;
                }
                else
                {
                    st.spread_decision = Bands.SPREAD_AGGRESSIVE;
                }
            }
            else if (shortBlocks != 0 || st.complexity < 3 || nbAvailableBytes < 10 * C)
            {
                if (st.complexity == 0)
                {
                    st.spread_decision = Bands.SPREAD_NONE;
                }
                else
                {
                    st.spread_decision = Bands.SPREAD_NORMAL;
                }
            }
            else
            {
                /* Disable new spreading+tapset estimator until we can show it works
                   better than the old one. So far it seems like spreading_decision()
                   works best. */
                {
                    st.spread_decision = BandsEnc.spreading_decision(mode, X,
                          ref st.tonal_average, st.spread_decision, ref st.hf_average,
                          ref st.tapset_decision, pf_on != 0 && shortBlocks == 0 ? 1 : 0, effEnd, C, M, spread_weight);
                }
                /*printf("%d %d\n", st->tapset_decision, st->spread_decision);*/
                /*printf("%f %d %f %d\n\n", st->analysis.tonality, st->spread_decision, st->analysis.tonality_slope, st->tapset_decision);*/
            }
            e.ec_enc_icdf(st.spread_decision, DotGame.AudioCodecs.Opus.Celt.Celt.spread_icdf, 5);
        }
        else
        {
            st.spread_decision = Bands.SPREAD_NORMAL;
        }

        /* For LFE, everything interesting is in the first band */
        if (st.lfe != 0)
        {
            offsets[0] = IMIN(8, effectiveBytes / 3);
        }
        Span<int> cap = stackalloc int[nbEBands];
        DotGame.AudioCodecs.Opus.Celt.Celt.init_caps(mode, cap, LM, C);

        dynalloc_logp = 6;
        total_bits <<= BITRES;
        total_boost = 0;
        tell = (int)e.ec_tell_frac();
        for (i = start; i < end; i++)
        {
            int width, quanta;
            int dynalloc_loop_logp;
            int boost;
            int j;
            width = (C * (eBands[i + 1] - eBands[i])) << LM;
            /* quanta is 6 bits, but no more than 1 bit/sample
               and no less than 1/8 bit/sample */
            quanta = IMIN(width << BITRES, IMAX(6 << BITRES, width));
            dynalloc_loop_logp = dynalloc_logp;
            boost = 0;
            for (j = 0; tell + (dynalloc_loop_logp << BITRES) < total_bits - total_boost
                  && boost < cap[i]; j++)
            {
                int flag;
                flag = j < offsets[i] ? 1 : 0;
                e.ec_enc_bit_logp(flag, (uint)dynalloc_loop_logp);
                tell = (int)e.ec_tell_frac();
                if (flag == 0)
                {
                    break;
                }
                boost += quanta;
                total_boost += quanta;
                dynalloc_loop_logp = 1;
            }
            /* Making dynalloc more likely */
            if (j != 0)
            {
                dynalloc_logp = IMAX(2, dynalloc_logp - 1);
            }
            offsets[i] = boost;
        }

        if (C == 2)
        {
            /* Always use MS for 2.5 ms frames until we can do a better analysis */
            if (LM != 0)
            {
                dual_stereo = stereo_analysis(mode, X, LM, N);
            }

            st.intensity = BandsEnc.hysteresis_decision((short)(equiv_rate / 1000),
                  intensity_thresholds, intensity_histeresis, 21, st.intensity);
            st.intensity = IMIN(end, IMAX(start, st.intensity));
        }

        alloc_trim = 5;
        if (tell + (6 << BITRES) <= total_bits - total_boost)
        {
            if (start > 0 || st.lfe != 0)
            {
                st.stereo_saving = 0;
                alloc_trim = 5;
            }
            else
            {
                alloc_trim = alloc_trim_analysis(mode, X, bandLogE,
                   end, LM, C, N, ref st.analysis, ref st.stereo_saving, tf_estimate,
                   st.intensity, surround_trim, equiv_rate);
            }
            e.ec_enc_icdf(alloc_trim, DotGame.AudioCodecs.Opus.Celt.Celt.trim_icdf, 7);
            tell = (int)e.ec_tell_frac();
        }

        /* In VBR mode the frame size must not be reduced so much that it would
            result in the encoder running out of bits.
           The margin of 2 bytes ensures that none of the bust-prevention logic
            in the decoder will have triggered so far. */
        min_allowed = ((tell + total_boost + (1 << (BITRES + 3)) - 1) >> (BITRES + 3)) + 2;
        /* Take into account the 37 bits we need to have left in the packet to
           signal a redundant frame in hybrid mode. Creating a shorter packet would
           create an entropy coder desync. */
        if (hybrid != 0)
        {
            min_allowed = IMAX(min_allowed, (tell0_frac + (37 << BITRES) + total_boost + (1 << (BITRES + 3)) - 1) >> (BITRES + 3));
        }
        /* Variable bitrate */
        if (vbr_rate > 0)
        {
            short alpha;
            int delta;
            /* The target rate in 8th bits per frame */
            int target, base_target;
            int lm_diff = mode.maxLM - LM;

            /* Don't attempt to use more than 510 kb/s, even for frames smaller than 20 ms.
               The CELT allocator will just not be able to use more than that anyway. */
            nbCompressedBytes = IMIN(nbCompressedBytes, packet_size_cap >> (3 - LM));
            if (hybrid == 0)
            {
                base_target = vbr_rate - (((40 * C) + 20) << BITRES);
            }
            else
            {
                base_target = IMAX(0, vbr_rate - (((9 * C) + 4) << BITRES));
            }

            if (st.constrained_vbr != 0)
            {
                base_target += (st.vbr_offset >> lm_diff);
            }

            if (hybrid == 0)
            {
                target = compute_vbr(mode, ref st.analysis, base_target, LM, equiv_rate,
                   st.lastCodedBands, C, st.intensity, st.constrained_vbr,
                   st.stereo_saving, tot_boost, tf_estimate, pitch_change, maxDepth,
                   st.lfe, st.energy_mask != null ? 1 : 0, surround_masking,
                   temporal_vbr);
            }
            else
            {
                target = base_target;
                /* Tonal frames (offset<100) need more bits than noisy (offset>100) ones. */
                if (st.silk_info.offset < 100)
                {
                    target += 12 << BITRES >> (3 - LM);
                }
                if (st.silk_info.offset > 100)
                {
                    target -= 18 << BITRES >> (3 - LM);
                }
                /* Boosting bitrate on transients and vowels with significant temporal
                   spikes. */
                target += (int)MULT16_16_Q14(tf_estimate - 4096 /* QCONST16(.25f,14) */, (50 << BITRES));
                /* If we have a strong transient, let's make sure it has enough bits to code
                   the first two bands, so that it can use folding rather than noise. */
                if (tf_estimate > 11469 /* QCONST16(.7f,14) */)
                {
                    target = IMAX(target, 50 << BITRES);
                }
            }
            /* The current offset is removed from the target and the space used
               so far is added*/
            target = target + tell;

            nbAvailableBytes = (target + (1 << (BITRES + 2))) >> (BITRES + 3);
            nbAvailableBytes = IMAX(min_allowed, nbAvailableBytes);
            nbAvailableBytes = IMIN(nbCompressedBytes, nbAvailableBytes);

            /* By how much did we "miss" the target on that frame */
            delta = target - vbr_rate;

            target = nbAvailableBytes << (BITRES + 3);

            /*If the frame is silent we don't adjust our drift, otherwise
              the encoder will shoot to very high rates after hitting a
              span of silence, but we do allow the bitres to refill.
              This means that we'll undershoot our target in CVBR/VBR modes
              on files with lots of silence. */
            if (silence != 0)
            {
                nbAvailableBytes = 2;
                target = (2 * 8) << BITRES;
                delta = 0;
            }

            if (st.vbr_count < 970)
            {
                st.vbr_count++;
                alpha = (short)celt_rcp(SHL32(EXTEND32(st.vbr_count + 20), 16));
            }
            else
            {
                alpha = 33; /* QCONST16(.001f,15) */
            }
            /* How many bits have we used in excess of what we're allowed */
            if (st.constrained_vbr != 0)
            {
                st.vbr_reservoir += target - vbr_rate;
            }
            /*printf ("%d\n", st->vbr_reservoir);*/

            /* Compute the offset we need to apply in order to reach the target */
            if (st.constrained_vbr != 0)
            {
                st.vbr_drift += (int)MULT16_32_Q15(alpha, (delta * (1 << lm_diff)) - st.vbr_offset - st.vbr_drift);
                st.vbr_offset = -st.vbr_drift;
            }
            /*printf ("%d\n", st->vbr_drift);*/

            if (st.constrained_vbr != 0 && st.vbr_reservoir < 0)
            {
                /* We're under the min value -- increase rate */
                int adjust = (-st.vbr_reservoir) / (8 << BITRES);
                /* Unless we're just coding silence */
                nbAvailableBytes += silence != 0 ? 0 : adjust;
                st.vbr_reservoir = 0;
                /*printf ("+%d\n", adjust);*/
            }
            nbCompressedBytes = IMIN(nbCompressedBytes, nbAvailableBytes);
            /*printf("%d\n", nbCompressedBytes*50*8);*/
            /* This moves the raw bits to take into account the new compressed size */
            e.ec_enc_shrink((uint)nbCompressedBytes);
        }

        /* Bit allocation */
        Span<int> fine_quant = stackalloc int[nbEBands];
        Span<int> pulses = stackalloc int[nbEBands];
        Span<int> fine_priority = stackalloc int[nbEBands];

        /* bits =           packet size                    - where we are - safety*/
        bits = (((int)nbCompressedBytes * 8) << BITRES) - (int)e.ec_tell_frac() - 1;
        anti_collapse_rsv = isTransient != 0 && LM >= 2 && bits >= ((LM + 2) << BITRES) ? (1 << BITRES) : 0;
        bits -= anti_collapse_rsv;
        signalBandwidth = end - 1;
        if (st.analysis.valid != 0)
        {
            int min_bandwidth;
            if (equiv_rate < (int)32000 * C)
            {
                min_bandwidth = 13;
            }
            else if (equiv_rate < (int)48000 * C)
            {
                min_bandwidth = 16;
            }
            else if (equiv_rate < (int)60000 * C)
            {
                min_bandwidth = 18;
            }
            else if (equiv_rate < (int)80000 * C)
            {
                min_bandwidth = 19;
            }
            else
            {
                min_bandwidth = 20;
            }
            signalBandwidth = IMAX(st.analysis.bandwidth, min_bandwidth);
        }
        if (st.lfe != 0)
        {
            signalBandwidth = 1;
        }
        codedBands = RateEnc.clt_compute_allocation(mode, start, end, offsets, cap,
              alloc_trim, ref st.intensity, ref dual_stereo, bits, out balance, pulses,
              fine_quant, fine_priority, C, LM, ref e, st.lastCodedBands, signalBandwidth);
        if (st.lastCodedBands != 0)
        {
            st.lastCodedBands = IMIN(st.lastCodedBands + 1, IMAX(st.lastCodedBands - 1, codedBands));
        }
        else
        {
            st.lastCodedBands = codedBands;
        }

        QuantBandsEnc.quant_fine_energy(mode, start, end, oldBandE, error, default, fine_quant, ref e, C);
        energyError.Slice(0, nbEBands * CC).Clear();

        /* Residual quantisation */
        Span<byte> collapse_masks = stackalloc byte[C * nbEBands];
        BandsEnc.quant_all_bands(mode, start, end, X, C == 2 ? X.Slice(N) : default, collapse_masks,
              st.bandE_buf.AsSpan(0, nbEBands * CC), pulses, shortBlocks, st.spread_decision,
              dual_stereo, st.intensity, tf_res, (nbCompressedBytes * (8 << BITRES)) - anti_collapse_rsv,
              balance, ref e, LM, codedBands, ref st.rng, st.complexity, st.disable_inv);

        if (anti_collapse_rsv > 0)
        {
            anti_collapse_on = st.consec_transient < 2 ? 1 : 0;
            e.ec_enc_bits((uint)anti_collapse_on, 1);
        }
        if (qext_bytes == 0)
        {
            QuantBandsEnc.quant_energy_finalise(mode, start, end, oldBandE, error, fine_quant, fine_priority, (nbCompressedBytes * 8) - e.ec_tell(), ref e, C);
        }
        c = 0;
        do
        {
            for (i = start; i < end; i++)
            {
                energyError[i + (c * nbEBands)] = MAXG(-8388608 /* -GCONST(0.5f) */, MING(8388608 /* GCONST(0.5f) */, error[i + (c * nbEBands)]));
            }
        }
        while (++c < C);
        if (silence != 0)
        {
            for (i = 0; i < C * nbEBands; i++)
            {
                oldBandE[i] = -469762048; /* -GCONST(28.f) */
            }
        }

        st.prefilter_period = pitch_index;
        st.prefilter_gain = gain1;
        st.prefilter_tapset = prefilter_tapset;

        if (CC == 2 && C == 1)
        {
            oldBandE.Slice(0, nbEBands).CopyTo(oldBandE.Slice(nbEBands));
        }

        if (isTransient == 0)
        {
            oldLogE.Slice(0, CC * nbEBands).CopyTo(oldLogE2);
            oldBandE.Slice(0, CC * nbEBands).CopyTo(oldLogE);
        }
        else
        {
            for (i = 0; i < CC * nbEBands; i++)
            {
                oldLogE[i] = MING(oldLogE[i], oldBandE[i]);
            }
        }
        /* In case start or end were to change */
        c = 0;
        do
        {
            for (i = 0; i < start; i++)
            {
                oldBandE[(c * nbEBands) + i] = 0;
                oldLogE[(c * nbEBands) + i] = oldLogE2[(c * nbEBands) + i] = -469762048; /* -GCONST(28.f) */
            }
            for (i = end; i < nbEBands; i++)
            {
                oldBandE[(c * nbEBands) + i] = 0;
                oldLogE[(c * nbEBands) + i] = oldLogE2[(c * nbEBands) + i] = -469762048; /* -GCONST(28.f) */
            }
        }
        while (++c < CC);

        if (isTransient != 0 || transient_got_disabled != 0)
        {
            st.consec_transient++;
        }
        else
        {
            st.consec_transient = 0;
        }
        st.rng = e.rng;

        /* If there's any room left (can only happen for very high rates),
           it's already filled with zeros */
        e.ec_enc_done();

        if (e.ec_get_error() != 0)
        {
            return OPUS_INTERNAL_ERROR;
        }
        else
        {
            return nbCompressedBytes;
        }
    }
}
