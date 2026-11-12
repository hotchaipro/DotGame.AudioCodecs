/* Copyright (c) 2007-2008 CSIRO
   Copyright (c) 2007-2010 Xiph.Org Foundation
   Copyright (c) 2008 Gregory Maxwell
   Written by Jean-Marc Valin and Gregory Maxwell
   Ported to C# from libopus 1.6.1 (celt/celt_decoder.c) by Hot Chai Games LLC */
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
using System.Diagnostics;
using System.Runtime.CompilerServices;
using DotGame.AudioCodecs.Opus.Common;
using static DotGame.AudioCodecs.Opus.Celt.Arch;
using static DotGame.AudioCodecs.Opus.Celt.MathOps;
using static DotGame.AudioCodecs.Opus.Common.ec_dec;

namespace DotGame.AudioCodecs.Opus.Celt;

/* Decoder state
 @brief Decoder state
 */
[SkipLocalsInit]
internal sealed class OpusCustomDecoder
{
    internal OpusCustomMode mode;
    internal int overlap;
    internal int channels;
    internal int stream_channels;

    internal int downsample;
    internal int start, end;
    internal int signalling;
    internal int disable_inv;
    internal int complexity;

    /* Everything beyond this point gets cleared on a reset */
    internal uint rng;
    internal int error;
    internal int last_pitch_index;
    internal int loss_duration;
    internal int plc_duration;
    internal int last_frame_type;
    internal int skip_plc;
    internal int postfilter_period;
    internal int postfilter_period_old;
    internal short postfilter_gain;
    internal short postfilter_gain_old;
    internal int postfilter_tapset;
    internal int postfilter_tapset_old;
    internal int prefilter_and_fold;

    internal readonly int[] preemph_memD = new int[2];

    /* C keeps these in one allocation after the struct; here they are separate arrays. */
    internal readonly int[] _decode_mem; /* Size = channels*(DECODE_BUFFER_SIZE+mode->overlap) */
    internal readonly int[] oldEBands;   /* Size = 2*mode->nbEBands */
    internal readonly int[] oldLogE;     /* Size = 2*mode->nbEBands */
    internal readonly int[] oldLogE2;    /* Size = 2*mode->nbEBands */
    internal readonly int[] backgroundLogE; /* Size = 2*mode->nbEBands */
    internal readonly short[] lpc;       /*  Size = channels*CELT_LPC_ORDER */

    /* The port's buffer for the normalised spectrum X[] that celt_decode_with_ec() and
       celt_decode_lost() ALLOC per call in C: 2 channels of the largest frame. */
    internal readonly int[] X_buf;

    /* celt_decoder_init(st, sampling_rate, channels) */
    internal OpusCustomDecoder(int sampling_rate, int channels)
    {
        OpusCustomMode mode = Modes.opus_custom_mode_create(48000, 960, out _)
            ?? throw new InvalidOperationException("The static CELT mode is missing.");
        /* opus_custom_decoder_init(st, mode, channels) */
        if (channels < 0 || channels > 2)
        {
            throw new ArgumentException("CELT supports 1 or 2 channels.", nameof(channels));
        }

        this._decode_mem = new int[channels * (CeltDecoder.DECODE_BUFFER_SIZE + mode.overlap)];
        this.oldEBands = new int[2 * mode.nbEBands];
        this.oldLogE = new int[2 * mode.nbEBands];
        this.oldLogE2 = new int[2 * mode.nbEBands];
        this.backgroundLogE = new int[2 * mode.nbEBands];
        this.lpc = new short[channels * CeltLpc.CELT_LPC_ORDER];
        this.X_buf = new int[2 * mode.shortMdctSize * mode.nbShortMdcts];

        this.mode = mode;
        this.overlap = mode.overlap;
        this.stream_channels = this.channels = channels;

        this.downsample = 1;
        this.start = 0;
        this.end = this.mode.effEBands;
        this.signalling = 1;
        this.disable_inv = channels == 1 ? 1 : 0;
        this.complexity = 0;

        this.ResetState();

        this.downsample = Celt.resampling_factor(sampling_rate);
        if (this.downsample == 0)
        {
            throw new ArgumentException("Unsupported sampling rate.", nameof(sampling_rate));
        }
    }

    /* opus_custom_decoder_ctl requests. Where C returns OPUS_BAD_ARG, these throw. */

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

    /* CELT_SET_CHANNELS */
    internal void SetChannels(int value)
    {
        if (value < 1 || value > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        this.stream_channels = value;
    }

    /* OPUS_RESET_STATE */
    internal void ResetState()
    {
        int i;
        this.rng = 0;
        this.error = 0;
        this.last_pitch_index = 0;
        this.loss_duration = 0;
        this.plc_duration = 0;
        this.last_frame_type = 0;
        this.skip_plc = 0;
        this.postfilter_period = 0;
        this.postfilter_period_old = 0;
        this.postfilter_gain = 0;
        this.postfilter_gain_old = 0;
        this.postfilter_tapset = 0;
        this.postfilter_tapset_old = 0;
        this.prefilter_and_fold = 0;
        Array.Clear(this.preemph_memD);
        Array.Clear(this._decode_mem);
        Array.Clear(this.oldEBands);
        Array.Clear(this.oldLogE);
        Array.Clear(this.oldLogE2);
        Array.Clear(this.backgroundLogE);
        Array.Clear(this.lpc);
        for (i = 0; i < 2 * this.mode.nbEBands; i++)
        {
            this.oldLogE[i] = this.oldLogE2[i] = -469762048; /* -GCONST(28.f) */
        }
        this.skip_plc = 1;
        this.last_frame_type = CeltDecoder.FRAME_NONE;
    }

    /* OPUS_GET_PITCH */
    internal int GetPitch() => this.postfilter_period;

    /* CELT_GET_MODE */
    internal OpusCustomMode GetMode() => this.mode;

    /* CELT_SET_SIGNALLING */
    internal void SetSignalling(int value) => this.signalling = value;

    /* OPUS_GET_FINAL_RANGE */
    internal uint GetFinalRange() => this.rng;

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
}

/// <summary>
/// celt/celt_decoder.c: CELT frame decoding, packet-loss concealment (pitch-based and
/// noise-based), the post-filter and the de-emphasis.
/// </summary>
[SkipLocalsInit]
internal static class CeltDecoder
{
    /* The maximum pitch lag to allow in the pitch-based PLC. It's possible to save
       CPU time in the PLC pitch search by making this smaller than MAX_PERIOD. The
       current value corresponds to a pitch of 66.67 Hz. */
    private const int PLC_PITCH_LAG_MAX = 720;
    /* The minimum pitch lag to allow in the pitch-based PLC. This corresponds to a
       pitch of 480 Hz. */
    private const int PLC_PITCH_LAG_MIN = 100;

    internal const int FRAME_NONE = 0;
    internal const int FRAME_NORMAL = 1;
    internal const int FRAME_PLC_NOISE = 2;
    internal const int FRAME_PLC_PERIODIC = 3;
    internal const int FRAME_PLC_NEURAL = 4;
    internal const int FRAME_DRED = 5;

    internal const int DECODE_BUFFER_SIZE = Modes.DEC_PITCH_BUF_SIZE;

    private const int OPUS_BAD_ARG = -1;
    private const int OPUS_INTERNAL_ERROR = -3;

    private const int CELT_LPC_ORDER = CeltLpc.CELT_LPC_ORDER;

    /* Make basic checks on the CELT state to ensure we don't end
       up writing all over memory. */
    [Conditional("DEBUG")]
    private static void validate_celt_decoder(OpusCustomDecoder st)
    {
        Debug.Assert(st.overlap == 120);
        Debug.Assert(st.end <= 21);
        Debug.Assert(st.channels == 1 || st.channels == 2);
        Debug.Assert(st.stream_channels == 1 || st.stream_channels == 2);
        Debug.Assert(st.downsample > 0);
        Debug.Assert(st.start == 0 || st.start == 17);
        Debug.Assert(st.start < st.end);
        Debug.Assert(st.last_pitch_index <= PLC_PITCH_LAG_MAX);
        Debug.Assert(st.last_pitch_index >= PLC_PITCH_LAG_MIN || st.last_pitch_index == 0);
        Debug.Assert(st.postfilter_period < Modes.MAX_PERIOD);
        Debug.Assert(st.postfilter_period >= Celt.COMBFILTER_MINPERIOD || st.postfilter_period == 0);
        Debug.Assert(st.postfilter_period_old < Modes.MAX_PERIOD);
        Debug.Assert(st.postfilter_period_old >= Celt.COMBFILTER_MINPERIOD || st.postfilter_period_old == 0);
        Debug.Assert(st.postfilter_tapset <= 2);
        Debug.Assert(st.postfilter_tapset >= 0);
        Debug.Assert(st.postfilter_tapset_old <= 2);
        Debug.Assert(st.postfilter_tapset_old >= 0);
    }

    /* OPUS_MOVE(mem, mem+N, length): moves the history left by N samples. A forward copy is
       exact for a left shift, so it's done in chunks that don't overlap. */
    private static void shift_history(Span<int> mem, int N, int length)
    {
        int i = 0;
        for (; i + N <= length; i += N)
        {
            mem.Slice(i + N, N).CopyTo(mem.Slice(i));
        }
        mem.Slice(i + N, length - i).CopyTo(mem.Slice(i));
    }

    /* C's decode_mem[c]: channel c's part of st->_decode_mem. */
    private static Span<int> channel_mem(OpusCustomDecoder st, int c)
    {
        int size = DECODE_BUFFER_SIZE + st.overlap;
        return c < st.channels ? st._decode_mem.AsSpan(c * size, size) : default;
    }

    /* Special case for stereo with no downsampling and no accumulation. This is
       quite common and we can make it faster by processing both channels in the
       same loop, reducing overhead due to the dependency loop in the IIR filter. */
    private static void deemphasis_stereo_simple(celt_sig_ptrs @in, Span<int> pcm, int N, short coef0,
          Span<int> mem)
    {
        Span<int> x0;
        Span<int> x1;
        int m0, m1;
        int j;
        x0 = @in[0];
        x1 = @in[1];
        m0 = mem[0];
        m1 = mem[1];
        for (j = 0; j < N; j++)
        {
            int tmp0, tmp1;
            /* Add VERY_SMALL to x[] first to reduce dependency chain. */
            tmp0 = SATURATE(x0[j] + VERY_SMALL + m0, SIG_SAT);
            tmp1 = SATURATE(x1[j] + VERY_SMALL + m1, SIG_SAT);
            m0 = MULT16_32_Q15(coef0, tmp0);
            m1 = MULT16_32_Q15(coef0, tmp1);
            pcm[2 * j] = SIG2RES(tmp0);
            pcm[(2 * j) + 1] = SIG2RES(tmp1);
        }
        mem[0] = m0;
        mem[1] = m1;
    }

    private static void deemphasis(celt_sig_ptrs @in, Span<int> pcm, int N, int C, int downsample, ReadOnlySpan<short> coef,
          Span<int> mem, int accum)
    {
        int c;
        int Nd;
        int apply_downsampling = 0;
        short coef0;
        /* Short version for common case. */
        if (downsample == 1 && C == 2 && accum == 0)
        {
            deemphasis_stereo_simple(@in, pcm, N, coef[0], mem);
            return;
        }
        Span<int> scratch = stackalloc int[N];
        coef0 = coef[0];
        Nd = N / downsample;
        c = 0;
        do
        {
            int j;
            Span<int> x;
            Span<int> y;
            int m = mem[c];
            x = @in[c];
            y = pcm.Slice(c);
            if (downsample > 1)
            {
                /* Shortcut for the standard (non-custom modes) case */
                for (j = 0; j < N; j++)
                {
                    int tmp = SATURATE(x[j] + VERY_SMALL + m, SIG_SAT);
                    m = MULT16_32_Q15(coef0, tmp);
                    scratch[j] = tmp;
                }
                apply_downsampling = 1;
            }
            else
            {
                /* Shortcut for the standard (non-custom modes) case */
                if (accum != 0)
                {
                    for (j = 0; j < N; j++)
                    {
                        int tmp = SATURATE(x[j] + m + VERY_SMALL, SIG_SAT);
                        m = MULT16_32_Q15(coef0, tmp);
                        y[j * C] = ADD_RES(y[j * C], SIG2RES(tmp));
                    }
                }
                else
                {
                    for (j = 0; j < N; j++)
                    {
                        int tmp = SATURATE(x[j] + VERY_SMALL + m, SIG_SAT);
                        m = MULT16_32_Q15(coef0, tmp);
                        y[j * C] = SIG2RES(tmp);
                    }
                }
            }
            mem[c] = m;

            if (apply_downsampling != 0)
            {
                /* Perform down-sampling */
                if (accum != 0)
                {
                    for (j = 0; j < Nd; j++)
                    {
                        y[j * C] = ADD_RES(y[j * C], SIG2RES(scratch[j * downsample]));
                    }
                }
                else
                {
                    for (j = 0; j < Nd; j++)
                    {
                        y[j * C] = SIG2RES(scratch[j * downsample]);
                    }
                }
            }
        }
        while (++c < C);
    }

    private static void celt_synthesis(OpusCustomMode mode, ReadOnlySpan<int> X, celt_sig_ptrs out_syn,
                        ReadOnlySpan<int> oldBandE, int start, int effEnd, int C, int CC,
                        int isTransient, int LM, int downsample,
                        int silence)
    {
        int c, i;
        int M;
        int b;
        int B;
        int N, NB;
        int shift;
        int nbEBands;
        int overlap;

        overlap = mode.overlap;
        nbEBands = mode.nbEBands;
        N = mode.shortMdctSize << LM;
        Span<int> freq = stackalloc int[N]; /*< Interleaved signal MDCTs */
        M = 1 << LM;

        if (isTransient != 0)
        {
            B = M;
            NB = mode.shortMdctSize;
            shift = mode.maxLM;
        }
        else
        {
            B = 1;
            NB = mode.shortMdctSize << LM;
            shift = mode.maxLM - LM;
        }

        if (CC == 2 && C == 1)
        {
            /* Copying a mono streams to two channels */
            Span<int> freq2;
            Bands.denormalise_bands(mode, X, freq, oldBandE, start, effEnd, M,
                  downsample, silence);
            /* Store a temporary copy in the output buffer because the IMDCT destroys its input. */
            freq2 = out_syn[1].Slice(overlap / 2);
            freq.CopyTo(freq2);
            for (b = 0; b < B; b++)
            {
                Mdct.clt_mdct_backward(mode.mdct, freq2.Slice(b), out_syn[0].Slice(NB * b), mode.window, overlap, shift, B);
            }
            for (b = 0; b < B; b++)
            {
                Mdct.clt_mdct_backward(mode.mdct, freq.Slice(b), out_syn[1].Slice(NB * b), mode.window, overlap, shift, B);
            }
        }
        else if (CC == 1 && C == 2)
        {
            /* Downmixing a stereo stream to mono */
            Span<int> freq2;
            freq2 = out_syn[0].Slice(overlap / 2);
            Bands.denormalise_bands(mode, X, freq, oldBandE, start, effEnd, M,
                  downsample, silence);
            /* Use the output buffer as temp array before downmixing. */
            Bands.denormalise_bands(mode, X.Slice(N), freq2, oldBandE.Slice(nbEBands), start, effEnd, M,
                  downsample, silence);
            for (i = 0; i < N; i++)
            {
                freq[i] = ADD32(HALF32(freq[i]), HALF32(freq2[i]));
            }
            for (b = 0; b < B; b++)
            {
                Mdct.clt_mdct_backward(mode.mdct, freq.Slice(b), out_syn[0].Slice(NB * b), mode.window, overlap, shift, B);
            }
        }
        else
        {
            /* Normal case (mono or stereo) */
            c = 0;
            do
            {
                Bands.denormalise_bands(mode, X.Slice(c * N), freq, oldBandE.Slice(c * nbEBands), start, effEnd, M,
                      downsample, silence);
                for (b = 0; b < B; b++)
                {
                    Mdct.clt_mdct_backward(mode.mdct, freq.Slice(b), out_syn[c].Slice(NB * b), mode.window, overlap, shift, B);
                }
            }
            while (++c < CC);
        }
        /* Saturate IMDCT output so that we can't overflow in the pitch postfilter
           or in the */
        c = 0;
        do
        {
            Span<int> o = out_syn[c];
            for (i = 0; i < N; i++)
            {
                o[i] = SATURATE(o[i], SIG_SAT);
            }
        }
        while (++c < CC);
    }

    private static void tf_decode(int start, int end, int isTransient, scoped Span<int> tf_res, int LM, ref ec_dec dec)
    {
        int i, curr, tf_select;
        int tf_select_rsv;
        int tf_changed;
        int logp;
        uint budget;
        uint tell;

        budget = dec.storage * 8;
        tell = (uint)dec.ec_tell();
        logp = isTransient != 0 ? 2 : 4;
        tf_select_rsv = LM > 0 && tell + (uint)logp + 1 <= budget ? 1 : 0;
        budget -= (uint)tf_select_rsv;
        tf_changed = curr = 0;
        for (i = start; i < end; i++)
        {
            if (tell + (uint)logp <= budget)
            {
                curr ^= dec.ec_dec_bit_logp((uint)logp);
                tell = (uint)dec.ec_tell();
                tf_changed |= curr;
            }
            tf_res[i] = curr;
            logp = isTransient != 0 ? 4 : 5;
        }
        tf_select = 0;
        if (tf_select_rsv != 0 &&
          Celt.tf_select_table[(LM * 8) + (4 * isTransient) + 0 + tf_changed] !=
          Celt.tf_select_table[(LM * 8) + (4 * isTransient) + 2 + tf_changed])
        {
            tf_select = dec.ec_dec_bit_logp(1);
        }
        for (i = start; i < end; i++)
        {
            tf_res[i] = Celt.tf_select_table[(LM * 8) + (4 * isTransient) + (2 * tf_select) + tf_res[i]];
        }
    }

    private static int celt_plc_pitch_search(OpusCustomDecoder st, celt_sig_ptrs decode_mem, int C)
    {
        int pitch_index;
        Span<short> lp_pitch_buf = stackalloc short[DECODE_BUFFER_SIZE >> 1];
        _ = st;
        Pitch.pitch_downsample(decode_mem, lp_pitch_buf,
              DECODE_BUFFER_SIZE >> 1, C, 2);
        Pitch.pitch_search(lp_pitch_buf.Slice(PLC_PITCH_LAG_MAX >> 1), lp_pitch_buf,
              DECODE_BUFFER_SIZE - PLC_PITCH_LAG_MAX,
              PLC_PITCH_LAG_MAX - PLC_PITCH_LAG_MIN, out pitch_index);
        pitch_index = PLC_PITCH_LAG_MAX - pitch_index;
        return pitch_index;
    }

    private static void prefilter_and_fold(OpusCustomDecoder st, int N)
    {
        int c;
        int CC;
        int i;
        int overlap;
        OpusCustomMode mode;
        int decode_buffer_size;
        decode_buffer_size = DECODE_BUFFER_SIZE;
        mode = st.mode;
        overlap = st.overlap;
        CC = st.channels;
        Span<int> etmp = stackalloc int[overlap];
        celt_sig_ptrs decode_mem = new celt_sig_ptrs(channel_mem(st, 0), channel_mem(st, 1));

        c = 0;
        do
        {
            /* Apply the pre-filter to the MDCT overlap for the next frame because
               the post-filter will be re-applied in the decoder after the MDCT
               overlap. */
            Celt.comb_filter(etmp, decode_mem[c], decode_buffer_size - N,
               st.postfilter_period_old, st.postfilter_period, overlap,
               (short)-st.postfilter_gain_old, (short)-st.postfilter_gain,
               st.postfilter_tapset_old, st.postfilter_tapset, default, 0);

            /* Simulate TDAC on the concealed audio so that it blends with the
               MDCT of the next frame. */
            for (i = 0; i < overlap / 2; i++)
            {
                decode_mem[c][decode_buffer_size - N + i] =
                   MULT16_32_Q15(COEF2VAL16(mode.window[i]), etmp[overlap - 1 - i])
                   + MULT16_32_Q15(COEF2VAL16(mode.window[overlap - i - 1]), etmp[i]);
            }
        }
        while (++c < CC);
    }

    private static void celt_decode_lost(OpusCustomDecoder st, int N, int LM)
    {
        int c;
        int i;
        int C = st.channels;
        celt_sig_ptrs decode_mem;
        celt_sig_ptrs out_syn;
        Span<short> lpc;
        Span<int> oldBandE, oldLogE, oldLogE2, backgroundLogE;
        OpusCustomMode mode;
        int nbEBands;
        int overlap;
        int start;
        int loss_duration;
        int curr_frame_type;
        short[] eBands;
        int decode_buffer_size;
        int max_period;
        decode_buffer_size = DECODE_BUFFER_SIZE;
        max_period = Modes.MAX_PERIOD;
        mode = st.mode;
        nbEBands = mode.nbEBands;
        overlap = mode.overlap;
        eBands = mode.eBands;

        {
            Span<int> m0 = channel_mem(st, 0);
            Span<int> m1 = channel_mem(st, 1);
            decode_mem = new celt_sig_ptrs(m0, m1);
            out_syn = new celt_sig_ptrs(m0.Slice(decode_buffer_size - N), C == 2 ? m1.Slice(decode_buffer_size - N) : default);
        }
        oldBandE = st.oldEBands;
        oldLogE = st.oldLogE;
        oldLogE2 = st.oldLogE2;
        backgroundLogE = st.backgroundLogE;
        lpc = st.lpc;

        loss_duration = st.loss_duration;
        start = st.start;
        curr_frame_type = FRAME_PLC_PERIODIC;
        if (st.plc_duration >= 40 || start != 0 || st.skip_plc != 0)
        {
            curr_frame_type = FRAME_PLC_NOISE;
        }

        if (curr_frame_type == FRAME_PLC_NOISE)
        {
            /* Noise-based PLC/CNG */
            Span<int> X;
            uint seed;
            int end;
            int effEnd;
            int decay;
            end = st.end;
            effEnd = IMAX(start, IMIN(end, mode.effEBands));

            X = st.X_buf.AsSpan(0, C * N);   /*< Interleaved normalised MDCTs */
            c = 0;
            do
            {
                shift_history(decode_mem[c], N, decode_buffer_size - N + overlap);
            }
            while (++c < C);

            if (st.prefilter_and_fold != 0)
            {
                prefilter_and_fold(st, N);
            }

            /* Energy decay */
            decay = loss_duration == 0 ? 25165824 /* GCONST(1.5f) */ : 8388608 /* GCONST(.5f) */;
            c = 0;
            do
            {
                for (i = start; i < end; i++)
                {
                    oldBandE[(c * nbEBands) + i] = MAXG(backgroundLogE[(c * nbEBands) + i], oldBandE[(c * nbEBands) + i] - decay);
                }
            }
            while (++c < C);
            seed = st.rng;
            for (c = 0; c < C; c++)
            {
                for (i = start; i < effEnd; i++)
                {
                    int j;
                    int boffs;
                    int blen;
                    boffs = (N * c) + (eBands[i] << LM);
                    blen = (eBands[i + 1] - eBands[i]) << LM;
                    for (j = 0; j < blen; j++)
                    {
                        seed = Bands.celt_lcg_rand(seed);
                        X[boffs + j] = SHL32((int)seed >> 20, NORM_SHIFT - 14);
                    }
                    Vq.renormalise_vector(X.Slice(boffs), blen, Q31ONE);
                }
            }
            st.rng = seed;

            celt_synthesis(mode, X, out_syn, oldBandE, start, effEnd, C, C, 0, LM, st.downsample, 0);

            /* Run the postfilter with the last parameters. */
            c = 0;
            do
            {
                st.postfilter_period = IMAX(st.postfilter_period, Celt.COMBFILTER_MINPERIOD);
                st.postfilter_period_old = IMAX(st.postfilter_period_old, Celt.COMBFILTER_MINPERIOD);
                Celt.comb_filter(out_syn[c], decode_mem[c], decode_buffer_size - N, st.postfilter_period_old, st.postfilter_period, mode.shortMdctSize,
                      st.postfilter_gain_old, st.postfilter_gain, st.postfilter_tapset_old, st.postfilter_tapset,
                      mode.window, overlap);
                if (LM != 0)
                {
                    Celt.comb_filter(out_syn[c].Slice(mode.shortMdctSize), decode_mem[c], decode_buffer_size - N + mode.shortMdctSize, st.postfilter_period, st.postfilter_period, N - mode.shortMdctSize,
                          st.postfilter_gain, st.postfilter_gain, st.postfilter_tapset, st.postfilter_tapset,
                          mode.window, overlap);
                }
            }
            while (++c < C);
            st.postfilter_period_old = st.postfilter_period;
            st.postfilter_gain_old = st.postfilter_gain;
            st.postfilter_tapset_old = st.postfilter_tapset;

            st.prefilter_and_fold = 0;
            /* Skip regular PLC until we get two consecutive packets. */
            st.skip_plc = 1;
        }
        else
        {
            int exc_length;
            /* Pitch-based PLC */
            ReadOnlySpan<short> window;
            scoped Span<short> exc;
            short fade = Q15ONE;
            int pitch_index;
            int curr_neural;
            int last_neural;

            curr_neural = curr_frame_type == FRAME_PLC_NEURAL || curr_frame_type == FRAME_DRED ? 1 : 0;
            last_neural = st.last_frame_type == FRAME_PLC_NEURAL || st.last_frame_type == FRAME_DRED ? 1 : 0;
            if (st.last_frame_type != FRAME_PLC_PERIODIC && !(last_neural != 0 && curr_neural != 0))
            {
                st.last_pitch_index = pitch_index = celt_plc_pitch_search(st, decode_mem, C);
            }
            else
            {
                pitch_index = st.last_pitch_index;
                fade = 26214; /* QCONST16(.8f,15) */
            }

            /* We want the excitation for 2 pitch periods in order to look for a
               decaying signal, but we can't get more than MAX_PERIOD. */
            exc_length = IMIN(2 * pitch_index, max_period);

            Span<short> _exc = stackalloc short[max_period + CELT_LPC_ORDER];
            Span<short> fir_tmp = stackalloc short[exc_length];
            Span<int> ac = stackalloc int[CELT_LPC_ORDER + 1];
            Span<short> lpc_mem = stackalloc short[CELT_LPC_ORDER];
            exc = _exc.Slice(CELT_LPC_ORDER);
            window = mode.window;
            c = 0;
            do
            {
                short decay;
                short attenuation;
                int S1 = 0;
                Span<int> buf;
                int extrapolation_offset;
                int extrapolation_len;
                int j;

                buf = decode_mem[c];
                for (i = 0; i < max_period + CELT_LPC_ORDER; i++)
                {
                    /* exc[i-CELT_LPC_ORDER] */
                    _exc[i] = SROUND16(buf[decode_buffer_size - max_period - CELT_LPC_ORDER + i], SIG_SHIFT);
                }

                if (st.last_frame_type != FRAME_PLC_PERIODIC && !(last_neural != 0 && curr_neural != 0))
                {
                    /* Compute LPC coefficients for the last MAX_PERIOD samples before
                       the first loss so we can work in the excitation-filter domain. */
                    CeltLpc._celt_autocorr(exc, ac, window, overlap,
                           CELT_LPC_ORDER, max_period);
                    /* Add a noise floor of -40 dB. */
                    ac[0] += SHR32(ac[0], 13);
                    /* Use lag windowing to stabilize the Levinson-Durbin recursion. */
                    for (i = 1; i <= CELT_LPC_ORDER; i++)
                    {
                        /*ac[i] *= exp(-.5*(2*M_PI*.002*i)*(2*M_PI*.002*i));*/
                        ac[i] -= MULT16_32_Q15(2 * i * i, ac[i]);
                    }
                    CeltLpc._celt_lpc(lpc.Slice(c * CELT_LPC_ORDER), ac, CELT_LPC_ORDER);
                    /* For fixed-point, apply bandwidth expansion until we can guarantee that
                       no overflow can happen in the IIR filter. This means:
                       32768*sum(abs(filter)) < 2^31 */
                    while (true)
                    {
                        short tmp = Q15ONE;
                        int sum = 4096; /* QCONST16(1., SIG_SHIFT) */
                        for (i = 0; i < CELT_LPC_ORDER; i++)
                        {
                            sum += ABS16(lpc[(c * CELT_LPC_ORDER) + i]);
                        }
                        if (sum < 65535)
                        {
                            break;
                        }
                        for (i = 0; i < CELT_LPC_ORDER; i++)
                        {
                            tmp = (short)MULT16_16_Q15(32440 /* QCONST16(.99f,15) */, tmp);
                            lpc[(c * CELT_LPC_ORDER) + i] = (short)MULT16_16_Q15(lpc[(c * CELT_LPC_ORDER) + i], tmp);
                        }
                    }
                }
                /* Initialize the LPC history with the samples just before the start
                   of the region for which we're computing the excitation. */
                {
                    /* Compute the excitation for exc_length samples before the loss. We need the copy
                       because celt_fir() cannot filter in-place. */
                    CeltLpc.celt_fir(_exc, CELT_LPC_ORDER + max_period - exc_length, lpc.Slice(c * CELT_LPC_ORDER),
                          fir_tmp, exc_length, CELT_LPC_ORDER);
                    fir_tmp.Slice(0, exc_length).CopyTo(exc.Slice(max_period - exc_length));
                }

                /* Check if the waveform is decaying, and if so how fast.
                   We do this to avoid adding energy when concealing in a segment
                   with decaying energy. */
                {
                    int E1 = 1, E2 = 1;
                    int decay_length;
                    int shift = IMAX(0, (2 * celt_zlog2(celt_maxabs16(exc.Slice(max_period - exc_length), exc_length))) - 20);
                    decay_length = exc_length >> 1;
                    for (i = 0; i < decay_length; i++)
                    {
                        short e;
                        e = exc[max_period - decay_length + i];
                        E1 += SHR32(MULT16_16(e, e), shift);
                        e = exc[max_period - (2 * decay_length) + i];
                        E2 += SHR32(MULT16_16(e, e), shift);
                    }
                    E1 = MIN32(E1, E2);
                    decay = (short)celt_sqrt(frac_div32(SHR32(E1, 1), E2));
                }

                /* Move the decoder memory one frame to the left to give us room to
                   add the data for the new frame. We ignore the overlap that extends
                   past the end of the buffer, because we aren't going to use it. */
                buf.Slice(N, decode_buffer_size - N).CopyTo(buf);

                /* Extrapolate from the end of the excitation with a period of
                   "pitch_index", scaling down each period by an additional factor of
                   "decay". */
                extrapolation_offset = max_period - pitch_index;
                /* We need to extrapolate enough samples to cover a complete MDCT
                   window (including overlap/2 samples on both sides). */
                extrapolation_len = N + overlap;
                /* We also apply fading if this is not the first loss. */
                attenuation = (short)MULT16_16_Q15(fade, decay);
                for (i = j = 0; i < extrapolation_len; i++, j++)
                {
                    short tmp;
                    if (j >= pitch_index)
                    {
                        j -= pitch_index;
                        attenuation = (short)MULT16_16_Q15(attenuation, decay);
                    }
                    buf[decode_buffer_size - N + i] =
                          SHL32(EXTEND32(MULT16_16_Q15(attenuation,
                                exc[extrapolation_offset + j])), SIG_SHIFT);
                    /* Compute the energy of the previously decoded signal whose
                       excitation we're copying. */
                    tmp = SROUND16(
                          buf[decode_buffer_size - max_period - N + extrapolation_offset + j],
                          SIG_SHIFT);
                    S1 += SHR32(MULT16_16(tmp, tmp), 11);
                }
                {
                    /* Copy the last decoded samples (prior to the overlap region) to
                       synthesis filter memory so we can have a continuous signal. */
                    for (i = 0; i < CELT_LPC_ORDER; i++)
                    {
                        lpc_mem[i] = SROUND16(buf[decode_buffer_size - N - 1 - i], SIG_SHIFT);
                    }
                    /* Apply the synthesis filter to convert the excitation back into
                       the signal domain. */
                    CeltLpc.celt_iir(buf.Slice(decode_buffer_size - N), lpc.Slice(c * CELT_LPC_ORDER),
                          buf.Slice(decode_buffer_size - N), extrapolation_len, CELT_LPC_ORDER,
                          lpc_mem);
                    for (i = 0; i < extrapolation_len; i++)
                    {
                        buf[decode_buffer_size - N + i] = SATURATE(buf[decode_buffer_size - N + i], SIG_SAT);
                    }
                }

                /* Check if the synthesis energy is higher than expected, which can
                   happen with the signal changes during our window. If so,
                   attenuate. */
                {
                    int S2 = 0;
                    for (i = 0; i < extrapolation_len; i++)
                    {
                        short tmp = SROUND16(buf[decode_buffer_size - N + i], SIG_SHIFT);
                        S2 += SHR32(MULT16_16(tmp, tmp), 11);
                    }
                    /* This checks for an "explosion" in the synthesis. */
                    if (!(S1 > SHR32(S2, 2)))
                    {
                        for (i = 0; i < extrapolation_len; i++)
                        {
                            buf[decode_buffer_size - N + i] = 0;
                        }
                    }
                    else if (S1 < S2)
                    {
                        short ratio = (short)celt_sqrt(frac_div32(SHR32(S1, 1) + 1, S2 + 1));
                        for (i = 0; i < overlap; i++)
                        {
                            short tmp_g = (short)(Q15ONE
                                  - MULT16_16_Q15(COEF2VAL16(window[i]), Q15ONE - ratio));
                            buf[decode_buffer_size - N + i] =
                                  MULT16_32_Q15(tmp_g, buf[decode_buffer_size - N + i]);
                        }
                        for (i = overlap; i < extrapolation_len; i++)
                        {
                            buf[decode_buffer_size - N + i] =
                                  MULT16_32_Q15(ratio, buf[decode_buffer_size - N + i]);
                        }
                    }
                }
            }
            while (++c < C);

            st.prefilter_and_fold = 1;
        }

        /* Saturate to something large to avoid wrap-around. */
        st.loss_duration = IMIN(10000, loss_duration + (1 << LM));
        st.plc_duration = IMIN(10000, st.plc_duration + (1 << LM));
        st.last_frame_type = curr_frame_type;
    }

    /* celt_decode_with_ec(), with celt_decode_with_ec_dred()'s body (no DRED, deep PLC or QEXT).
       data empty stands for C's NULL; haveDec false for C's dec == NULL, when the frame is
       decoded with a range decoder over data. */
    internal static int celt_decode_with_ec(OpusCustomDecoder st, ReadOnlySpan<byte> data,
          int len, scoped Span<int> pcm, int frame_size, ref ec_dec dec, bool haveDec, int accum)
    {
        int c, i, N;
        int spread_decision;
        int bits;
        ec_dec _dec = default;
        celt_sig_ptrs decode_mem;
        celt_sig_ptrs out_syn;
        Span<int> oldBandE, oldLogE, oldLogE2, backgroundLogE;

        int shortBlocks;
        int isTransient;
        int intra_ener;
        int CC = st.channels;
        int LM, M;
        int start;
        int end;
        int effEnd;
        int codedBands;
        int alloc_trim;
        int postfilter_pitch;
        short postfilter_gain;
        int intensity = 0;
        int dual_stereo = 0;
        int total_bits;
        int balance;
        int tell;
        int dynalloc_logp;
        int postfilter_tapset;
        int anti_collapse_rsv;
        int anti_collapse_on = 0;
        int silence;
        int C = st.stream_channels;
        OpusCustomMode mode;
        int nbEBands;
        int overlap;
        short[] eBands;
        int max_background_increase;
        int decode_buffer_size;
        decode_buffer_size = DECODE_BUFFER_SIZE;

        validate_celt_decoder(st);
        mode = st.mode;
        nbEBands = mode.nbEBands;
        overlap = mode.overlap;
        eBands = mode.eBands;
        start = st.start;
        end = st.end;
        frame_size *= st.downsample;

        oldBandE = st.oldEBands;
        oldLogE = st.oldLogE;
        oldLogE2 = st.oldLogE2;
        backgroundLogE = st.backgroundLogE;

        {
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
        }
        M = 1 << LM;

        if (len < 0 || len > 1275 || pcm.IsEmpty)
        {
            return OPUS_BAD_ARG;
        }

        N = M * mode.shortMdctSize;
        {
            Span<int> m0 = channel_mem(st, 0);
            Span<int> m1 = channel_mem(st, 1);
            decode_mem = new celt_sig_ptrs(m0, m1);
            out_syn = new celt_sig_ptrs(m0.Slice(decode_buffer_size - N), CC == 2 ? m1.Slice(decode_buffer_size - N) : default);
        }

        effEnd = end;
        if (effEnd > mode.effEBands)
        {
            effEnd = mode.effEBands;
        }

        if (data.IsEmpty || len <= 1)
        {
            celt_decode_lost(st, N, LM);
            deemphasis(out_syn, pcm, N, CC, st.downsample, mode.preemph, st.preemph_memD, accum);
            return frame_size / st.downsample;
        }

        /* Check if there are at least two packets received consecutively before
         * turning on the pitch-based PLC */
        if (st.loss_duration == 0)
        {
            st.skip_plc = 0;
        }

        scoped ref ec_dec d = ref dec;
        if (!haveDec)
        {
            _dec.ec_dec_init(data, (uint)len);
            d = ref _dec;
        }

        if (C == 1)
        {
            for (i = 0; i < nbEBands; i++)
            {
                oldBandE[i] = MAXG(oldBandE[i], oldBandE[nbEBands + i]);
            }
        }

        total_bits = len * 8;
        tell = d.ec_tell();

        if (tell >= total_bits)
        {
            silence = 1;
        }
        else if (tell == 1)
        {
            silence = d.ec_dec_bit_logp(15);
        }
        else
        {
            silence = 0;
        }
        if (silence != 0)
        {
            /* Pretend we've read all the remaining bits */
            tell = len * 8;
            d.nbits_total += tell - d.ec_tell();
        }

        postfilter_gain = 0;
        postfilter_pitch = 0;
        postfilter_tapset = 0;
        if (start == 0 && tell + 16 <= total_bits)
        {
            if (d.ec_dec_bit_logp(1) != 0)
            {
                int qg, octave;
                octave = (int)d.ec_dec_uint(6);
                postfilter_pitch = (int)((16 << octave) + d.ec_dec_bits((uint)(4 + octave)) - 1);
                qg = (int)d.ec_dec_bits(3);
                if (d.ec_tell() + 2 <= total_bits)
                {
                    postfilter_tapset = d.ec_dec_icdf(Celt.tapset_icdf, 2);
                }
                postfilter_gain = (short)(3072 /* QCONST16(.09375f,15) */ * (qg + 1));
            }
            tell = d.ec_tell();
        }

        if (LM > 0 && tell + 3 <= total_bits)
        {
            isTransient = d.ec_dec_bit_logp(3);
            tell = d.ec_tell();
        }
        else
        {
            isTransient = 0;
        }

        if (isTransient != 0)
        {
            shortBlocks = M;
        }
        else
        {
            shortBlocks = 0;
        }

        /* Decode the global flags (first symbols in the stream) */
        intra_ener = tell + 3 <= total_bits ? d.ec_dec_bit_logp(3) : 0;
        /* If recovering from packet loss, make sure we make the energy prediction safe to reduce the
           risk of getting loud artifacts. */
        if (intra_ener == 0 && st.loss_duration != 0)
        {
            c = 0;
            do
            {
                int safety = 0;
                int missing = IMIN(10, st.loss_duration >> LM);
                if (LM == 0)
                {
                    safety = 25165824; /* GCONST(1.5f) */
                }
                else if (LM == 1)
                {
                    safety = 8388608; /* GCONST(.5f) */
                }
                for (i = start; i < end; i++)
                {
                    if (oldBandE[(c * nbEBands) + i] < MAXG(oldLogE[(c * nbEBands) + i], oldLogE2[(c * nbEBands) + i]))
                    {
                        /* If energy is going down already, continue the trend. */
                        int slope;
                        int E0, E1, E2;
                        E0 = oldBandE[(c * nbEBands) + i];
                        E1 = oldLogE[(c * nbEBands) + i];
                        E2 = oldLogE2[(c * nbEBands) + i];
                        slope = MAX32(E1 - E0, HALF32(E2 - E0));
                        slope = MING(slope, 33554432 /* GCONST(2.f) */);
                        E0 -= MAX32(0, (1 + missing) * slope);
                        oldBandE[(c * nbEBands) + i] = MAX32(-335544320 /* -GCONST(20.f) */, E0);
                    }
                    else
                    {
                        /* Otherwise take the min of the last frames. */
                        oldBandE[(c * nbEBands) + i] = MING(MING(oldBandE[(c * nbEBands) + i], oldLogE[(c * nbEBands) + i]), oldLogE2[(c * nbEBands) + i]);
                    }
                    /* Shorter frames have more natural fluctuations -- play it safe. */
                    oldBandE[(c * nbEBands) + i] -= safety;
                }
            }
            while (++c < 2);
        }
        /* Get band energies */
        QuantBands.unquant_coarse_energy(mode, start, end, oldBandE,
              intra_ener, ref d, C, LM);

        Span<int> tf_res = stackalloc int[nbEBands];
        tf_decode(start, end, isTransient, tf_res, LM, ref d);

        tell = d.ec_tell();
        spread_decision = Bands.SPREAD_NORMAL;
        if (tell + 4 <= total_bits)
        {
            spread_decision = d.ec_dec_icdf(Celt.spread_icdf, 5);
        }

        Span<int> cap = stackalloc int[nbEBands];

        Celt.init_caps(mode, cap, LM, C);

        Span<int> offsets = stackalloc int[nbEBands];

        dynalloc_logp = 6;
        total_bits <<= BITRES;
        tell = (int)d.ec_tell_frac();
        for (i = start; i < end; i++)
        {
            int width, quanta;
            int dynalloc_loop_logp;
            int boost;
            width = (C * (eBands[i + 1] - eBands[i])) << LM;
            /* quanta is 6 bits, but no more than 1 bit/sample
               and no less than 1/8 bit/sample */
            quanta = IMIN(width << BITRES, IMAX(6 << BITRES, width));
            dynalloc_loop_logp = dynalloc_logp;
            boost = 0;
            while (tell + (dynalloc_loop_logp << BITRES) < total_bits && boost < cap[i])
            {
                int flag;
                flag = d.ec_dec_bit_logp((uint)dynalloc_loop_logp);
                tell = (int)d.ec_tell_frac();
                if (flag == 0)
                {
                    break;
                }
                boost += quanta;
                total_bits -= quanta;
                dynalloc_loop_logp = 1;
            }
            offsets[i] = boost;
            /* Making dynalloc more likely */
            if (boost > 0)
            {
                dynalloc_logp = IMAX(2, dynalloc_logp - 1);
            }
        }

        Span<int> fine_quant = stackalloc int[nbEBands];
        alloc_trim = tell + (6 << BITRES) <= total_bits ?
              d.ec_dec_icdf(Celt.trim_icdf, 7) : 5;

        bits = ((len * 8) << BITRES) - (int)d.ec_tell_frac() - 1;
        anti_collapse_rsv = isTransient != 0 && LM >= 2 && bits >= ((LM + 2) << BITRES) ? (1 << BITRES) : 0;
        bits -= anti_collapse_rsv;

        Span<int> pulses = stackalloc int[nbEBands];
        Span<int> fine_priority = stackalloc int[nbEBands];

        codedBands = Rate.clt_compute_allocation(mode, start, end, offsets, cap,
              alloc_trim, ref intensity, ref dual_stereo, bits, out balance, pulses,
              fine_quant, fine_priority, C, LM, ref d);

        QuantBands.unquant_fine_energy(mode, start, end, oldBandE, default, fine_quant, ref d, C);

        Span<int> X = st.X_buf.AsSpan(0, C * N);   /*< Interleaved normalised MDCTs */

        c = 0;
        do
        {
            shift_history(decode_mem[c], N, decode_buffer_size - N + overlap);
        }
        while (++c < CC);

        /* Decode fixed codebook */
        Span<byte> collapse_masks = stackalloc byte[C * nbEBands];

        Bands.quant_all_bands(mode, start, end, X, C == 2 ? X.Slice(N) : default, collapse_masks,
              pulses, shortBlocks, spread_decision, dual_stereo, intensity, tf_res,
              (len * (8 << BITRES)) - anti_collapse_rsv, balance, ref d, LM, codedBands, ref st.rng,
              st.disable_inv);

        if (anti_collapse_rsv > 0)
        {
            anti_collapse_on = (int)d.ec_dec_bits(1);
        }
        QuantBands.unquant_energy_finalise(mode, start, end, oldBandE,
              fine_quant, fine_priority, (len * 8) - d.ec_tell(), ref d, C);
        if (anti_collapse_on != 0)
        {
            Bands.anti_collapse(mode, X, collapse_masks, LM, C, N,
                  start, end, oldBandE, oldLogE, oldLogE2, pulses, st.rng);
        }

        if (silence != 0)
        {
            for (i = 0; i < C * nbEBands; i++)
            {
                oldBandE[i] = -469762048; /* -GCONST(28.f) */
            }
        }
        if (st.prefilter_and_fold != 0)
        {
            prefilter_and_fold(st, N);
        }
        celt_synthesis(mode, X, out_syn, oldBandE, start, effEnd,
                       C, CC, isTransient, LM, st.downsample, silence);

        c = 0;
        do
        {
            st.postfilter_period = IMAX(st.postfilter_period, Celt.COMBFILTER_MINPERIOD);
            st.postfilter_period_old = IMAX(st.postfilter_period_old, Celt.COMBFILTER_MINPERIOD);
            Celt.comb_filter(out_syn[c], decode_mem[c], decode_buffer_size - N, st.postfilter_period_old, st.postfilter_period, mode.shortMdctSize,
                  st.postfilter_gain_old, st.postfilter_gain, st.postfilter_tapset_old, st.postfilter_tapset,
                  mode.window, overlap);
            if (LM != 0)
            {
                Celt.comb_filter(out_syn[c].Slice(mode.shortMdctSize), decode_mem[c], decode_buffer_size - N + mode.shortMdctSize, st.postfilter_period, postfilter_pitch, N - mode.shortMdctSize,
                      st.postfilter_gain, postfilter_gain, st.postfilter_tapset, postfilter_tapset,
                      mode.window, overlap);
            }
        }
        while (++c < CC);
        st.postfilter_period_old = st.postfilter_period;
        st.postfilter_gain_old = st.postfilter_gain;
        st.postfilter_tapset_old = st.postfilter_tapset;
        st.postfilter_period = postfilter_pitch;
        st.postfilter_gain = postfilter_gain;
        st.postfilter_tapset = postfilter_tapset;
        if (LM != 0)
        {
            st.postfilter_period_old = st.postfilter_period;
            st.postfilter_gain_old = st.postfilter_gain;
            st.postfilter_tapset_old = st.postfilter_tapset;
        }

        if (C == 1)
        {
            oldBandE.Slice(0, nbEBands).CopyTo(oldBandE.Slice(nbEBands));
        }

        if (isTransient == 0)
        {
            oldLogE.Slice(0, 2 * nbEBands).CopyTo(oldLogE2);
            oldBandE.Slice(0, 2 * nbEBands).CopyTo(oldLogE);
        }
        else
        {
            for (i = 0; i < 2 * nbEBands; i++)
            {
                oldLogE[i] = MING(oldLogE[i], oldBandE[i]);
            }
        }
        /* In normal circumstances, we only allow the noise floor to increase by
           up to 2.4 dB/second, but when we're in DTX we give the weight of
           all missing packets to the update packet. */
        max_background_increase = IMIN(160, st.loss_duration + M) * 16777; /* GCONST(0.001f) */
        for (i = 0; i < 2 * nbEBands; i++)
        {
            backgroundLogE[i] = MING(backgroundLogE[i] + max_background_increase, oldBandE[i]);
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
        while (++c < 2);
        st.rng = d.rng;

        deemphasis(out_syn, pcm, N, CC, st.downsample, mode.preemph, st.preemph_memD, accum);
        st.loss_duration = 0;
        st.plc_duration = 0;
        st.last_frame_type = FRAME_NORMAL;
        st.prefilter_and_fold = 0;
        if (d.ec_tell() > 8 * len)
        {
            return OPUS_INTERNAL_ERROR;
        }
        if (d.ec_get_error() != 0)
        {
            st.error = 1;
        }
        return frame_size / st.downsample;
    }
}
