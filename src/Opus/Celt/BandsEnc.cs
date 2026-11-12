/* Copyright (c) 2007-2008 CSIRO
   Copyright (c) 2007-2009 Xiph.Org Foundation
   Copyright (c) 2008-2009 Gregory Maxwell
   Written by Jean-Marc Valin and Gregory Maxwell
   Ported to C# from libopus 1.6.1 (celt/bands.c, celt/bands.h) by Hot Chai Games LLC */
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
using System.Runtime.Intrinsics;
using DotGame.AudioCodecs.Opus.Common;
using static DotGame.AudioCodecs.Opus.Celt.Arch;
using static DotGame.AudioCodecs.Opus.Celt.MathOps;
using static DotGame.AudioCodecs.Opus.Common.ec_dec;

namespace DotGame.AudioCodecs.Opus.Celt;

/// <summary>
/// The encoder side of celt/bands.c: band energies and normalisation, the spreading decision, and
/// quantising the spectrum (quant_all_bands and what it calls, with C's encode resolved to 1).
/// The helpers the decoder's <see cref="Bands"/> keeps private are repeated here. Where C passes
/// NULL for a celt_norm pointer, an empty span is passed.
/// </summary>
[SkipLocalsInit]
internal static class BandsEnc
{
    /* The state quant_all_bands() shares with the functions it calls. C holds an ec_ctx* here; a
       ref struct can't hold a ref to the ec_enc ref struct, so it holds the encoder itself, copied
       in at the start of quant_all_bands() and back out at its end (nothing else touches it in
       between). encode (always 1) and arch are gone. */
    internal ref struct band_ctx
    {
        internal int resynth;
        internal OpusCustomMode m;
        internal int i;
        internal int intensity;
        internal int spread;
        internal int tf_change;
        internal ec_enc ec;
        internal int remaining_bits;
        internal ReadOnlySpan<int> bandE;
        internal uint seed;
        internal int theta_round;
        internal int disable_inv;
        internal int avoid_split_noise;
    }

    internal static int hysteresis_decision(short val, ReadOnlySpan<short> thresholds, ReadOnlySpan<short> hysteresis, int N, int prev)
    {
        int i;
        for (i = 0; i < N; i++)
        {
            if (val < thresholds[i])
            {
                break;
            }
        }
        if (i > prev && val < thresholds[prev] + hysteresis[prev])
        {
            i = prev;
        }
        if (i < prev && val > thresholds[prev - 1] - hysteresis[prev - 1])
        {
            i = prev;
        }
        return i;
    }

    /* Compute the amplitude (sqrt energy) in each of the bands */
    internal static void compute_band_energies(OpusCustomMode m, ReadOnlySpan<int> X, Span<int> bandE, int end, int C, int LM)
    {
        int i, c, N;
        short[] eBands = m.eBands;
        N = m.shortMdctSize << LM;
        c = 0;
        do
        {
            for (i = 0; i < end; i++)
            {
                int j;
                int maxval = 0;
                int sum = 0;

                maxval = celt_maxabs32(X.Slice((c * N) + (eBands[i] << LM)), (eBands[i + 1] - eBands[i]) << LM);
                if (maxval > 0)
                {
                    int shift = IMAX(0, 30 - celt_ilog2(maxval + (maxval >> 14) + 1) - ((((m.logN[i] + 7) >> BITRES) + LM + 1) >> 1));
                    j = eBands[i] << LM;
                    sum = Simd.SumSquaresQ31(X.Slice(j + (c * N), (eBands[i + 1] << LM) - j), shift);
                    bandE[i + (c * m.nbEBands)] = MAX32(maxval, PSHR32(MathOpsEnc.celt_sqrt32(SHR32(sum, 1)), shift));
                }
                else
                {
                    bandE[i + (c * m.nbEBands)] = EPSILON;
                }
            }
        }
        while (++c < C);
    }

    /* Normalise each band such that the energy is one. */
    internal static void normalise_bands(OpusCustomMode m, ReadOnlySpan<int> freq, Span<int> X, ReadOnlySpan<int> bandE, int end, int C, int M)
    {
        int i, c, N;
        short[] eBands = m.eBands;
        N = M * m.shortMdctSize;
        c = 0;
        do
        {
            i = 0;
            do
            {
                int j, shift;
                int E;
                int g;
                E = bandE[i + (c * m.nbEBands)];
                /* For very low energies, we need this to make sure not to prevent energy rounding from
                   blowing up the normalized signal. */
                if (E < 10)
                {
                    E += EPSILON;
                }
                shift = 30 - celt_zlog2(E);
                E = SHL32(E, shift);
                g = MathOpsEnc.celt_rcp_norm32(E);
                j = M * eBands[i];
                int bandEnd = M * eBands[i + 1];
                if (Simd.HasMULT32_32_Q31 && (bandEnd - j >= 4))
                {
                    // Four at a time, each lane as the scalar step
                    ReadOnlySpan<int> from = freq.Slice(j + (c * N), bandEnd - j);
                    Span<int> to = X.Slice(j + (c * N), bandEnd - j);
                    var gain = Vector128.Create(g);
                    var round = Vector128.Create((1 << (30 - NORM_SHIFT)) >> 1);
                    int k = 0;
                    for (; k + 4 <= from.Length; k += 4)
                    {
                        var v = Simd.MULT32_32_Q31(gain, Vector128.Create(from.Slice(k, 4)) << shift);
                        ((v + round) >> (30 - NORM_SHIFT)).CopyTo(to.Slice(k, 4));
                    }
                    j += k;
                }
                for (; j < bandEnd; j++)
                {
                    X[j + (c * N)] = PSHR32(MULT32_32_Q31(g, SHL32(freq[j + (c * N)], shift)), 30 - NORM_SHIFT);
                }
            }
            while (++i < end);
        }
        while (++c < C);
    }

    /* Compute the weights to use for optimizing normalized distortion across
       channels. We use the amplitude to weight square distortion, which means
       that we use the square root of the value we would have been using if we
       wanted to minimize the MSE in the non-normalized domain. This roughly
       corresponds to some quick-and-dirty perceptual experiments I ran to
       measure inter-aural masking (there doesn't seem to be any published data
       on the topic). */
    private static void compute_channel_weights(int Ex, int Ey, Span<short> w)
    {
        int minE;
        int shift;
        minE = MIN32(Ex, Ey);
        /* Adjustment to make the weights a bit more conservative. */
        Ex = ADD32(Ex, minE / 3);
        Ey = ADD32(Ey, minE / 3);
        shift = celt_ilog2(EPSILON + MAX32(Ex, Ey)) - 14;
        w[0] = (short)VSHR32(Ex, shift);
        w[1] = (short)VSHR32(Ey, shift);
    }

    private static void intensity_stereo(OpusCustomMode m, Span<int> X, ReadOnlySpan<int> Y, ReadOnlySpan<int> bandE, int bandID, int N)
    {
        int i = bandID;
        int j;
        short a1, a2;
        short left, right;
        short norm;
        int shift = celt_zlog2(MAX32(bandE[i], bandE[i + m.nbEBands])) - 13;
        left = (short)VSHR32(bandE[i], shift);
        right = (short)VSHR32(bandE[i + m.nbEBands], shift);
        norm = (short)(EPSILON + celt_sqrt(EPSILON + MULT16_16(left, left) + MULT16_16(right, right)));
        left = (short)MIN32(left, norm - 1);
        right = (short)MIN32(right, norm - 1);
        a1 = DIV32_16(SHL32(EXTEND32(left), 15), norm);
        a2 = DIV32_16(SHL32(EXTEND32(right), 15), norm);
        for (j = 0; j < N; j++)
        {
            X[j] = ADD32(MULT16_32_Q15(a1, X[j]), MULT16_32_Q15(a2, Y[j]));
            /* Side is not encoded, no need to calculate */
        }
    }

    private static void stereo_split(Span<int> X, Span<int> Y, int N)
    {
        int j;
        for (j = 0; j < N; j++)
        {
            int r, l;
            l = MULT32_32_Q31(1518500224 /* QCONST32(.70710678f,31) */, X[j]);
            r = MULT32_32_Q31(1518500224 /* QCONST32(.70710678f,31) */, Y[j]);
            X[j] = ADD32(l, r);
            Y[j] = SUB32(r, l);
        }
    }

    private static void stereo_merge(Span<int> X, Span<int> Y, int mid, int N)
    {
        int j;
        int xp = 0, side = 0;
        int El, Er;
        int kl, kr;
        int t, lgain, rgain;

        /* Compute the norm of X+Y and X-Y as |X|^2 + |Y|^2 +/- sum(xy) */
        xp = Vq.celt_inner_prod_norm_shift(Y, X, N);
        side = Vq.celt_inner_prod_norm_shift(Y, Y, N);
        /* Compensating for the mid normalization */
        xp = MULT32_32_Q31(mid, xp);
        /* mid and side are in Q15, not Q14 like X and Y */
        El = SHR32(MULT32_32_Q31(mid, mid), 3) + side - (2 * xp);
        Er = SHR32(MULT32_32_Q31(mid, mid), 3) + side + (2 * xp);
        if (Er < 161061 /* QCONST32(6e-4f, 28) */ || El < 161061 /* QCONST32(6e-4f, 28) */)
        {
            X.Slice(0, N).CopyTo(Y);
            return;
        }

        kl = celt_ilog2(El) >> 1;
        kr = celt_ilog2(Er) >> 1;
        t = VSHR32(El, (kl << 1) - 29);
        lgain = celt_rsqrt_norm32(t);
        t = VSHR32(Er, (kr << 1) - 29);
        rgain = celt_rsqrt_norm32(t);

        if (kl < 7)
        {
            kl = 7;
        }
        if (kr < 7)
        {
            kr = 7;
        }

        for (j = 0; j < N; j++)
        {
            int r, l;
            /* Apply mid scaling (side is already scaled) */
            l = MULT32_32_Q31(mid, X[j]);
            r = Y[j];
            X[j] = VSHR32(MULT32_32_Q31(lgain, SUB32(l, r)), kl - 15);
            Y[j] = VSHR32(MULT32_32_Q31(rgain, ADD32(l, r)), kr - 15);
        }
    }

    /* Decide whether we should spread the pulses in the current frame */
    internal static int spreading_decision(OpusCustomMode m, ReadOnlySpan<int> X, ref int average,
          int last_decision, ref int hf_average, ref int tapset_decision, int update_hf,
          int end, int C, int M, ReadOnlySpan<int> spread_weight)
    {
        int i, c, N0;
        int sum = 0, nbBands = 0;
        short[] eBands = m.eBands;
        int decision;
        int hf_sum = 0;
        Span<int> tcount = stackalloc int[3];

        Debug.Assert(end > 0);

        N0 = M * m.shortMdctSize;

        if (M * (eBands[end] - eBands[end - 1]) <= 8)
        {
            return Bands.SPREAD_NONE;
        }
        c = 0;
        do
        {
            for (i = 0; i < end; i++)
            {
                int j, N, tmp = 0;
                ReadOnlySpan<int> x = X.Slice((M * eBands[i]) + (c * N0));
                tcount.Clear();
                N = M * (eBands[i + 1] - eBands[i]);
                if (N <= 8)
                {
                    continue;
                }
                /* Compute rough CDF of |x[j]| */
                /* Counted without branches, which the signal's values would mispredict: x2N is a
                   16-bit value times N, so x2N - limit can't overflow and its sign bit says x2N < limit. */
                int t0 = 0, t1 = 0, t2 = 0;
                x = x.Slice(0, N);
                for (j = 0; j < x.Length; j++)
                {
                    int x2N; /* Q13 */

                    x2N = MULT16_16(MULT16_16_Q15(SHR32(x[j], NORM_SHIFT - 14), SHR32(x[j], NORM_SHIFT - 14)), N);
                    t0 -= (x2N - 2048 /* QCONST16(0.25f,13) */) >> 31;
                    t1 -= (x2N - 512 /* QCONST16(0.0625f,13) */) >> 31;
                    t2 -= (x2N - 128 /* QCONST16(0.015625f,13) */) >> 31;
                }
                tcount[0] = t0;
                tcount[1] = t1;
                tcount[2] = t2;

                /* Only include four last bands (8 kHz and up) */
                if (i > m.nbEBands - 4)
                {
                    hf_sum += (int)celt_udiv((uint)(32 * (tcount[1] + tcount[0])), (uint)N);
                }
                tmp = (2 * tcount[2] >= N ? 1 : 0) + (2 * tcount[1] >= N ? 1 : 0) + (2 * tcount[0] >= N ? 1 : 0);
                sum += tmp * spread_weight[i];
                nbBands += spread_weight[i];
            }
        }
        while (++c < C);

        if (update_hf != 0)
        {
            if (hf_sum != 0)
            {
                hf_sum = (int)celt_udiv((uint)hf_sum, (uint)(C * (4 - m.nbEBands + end)));
            }
            hf_average = (hf_average + hf_sum) >> 1;
            hf_sum = hf_average;
            if (tapset_decision == 2)
            {
                hf_sum += 4;
            }
            else if (tapset_decision == 0)
            {
                hf_sum -= 4;
            }
            if (hf_sum > 22)
            {
                tapset_decision = 2;
            }
            else if (hf_sum > 18)
            {
                tapset_decision = 1;
            }
            else
            {
                tapset_decision = 0;
            }
        }
        /*printf("%d %d %d\n", hf_sum, *hf_average, *tapset_decision);*/
        Debug.Assert(nbBands > 0); /* end has to be non-zero */
        Debug.Assert(sum >= 0);
        sum = (int)celt_udiv((uint)((int)sum << 8), (uint)nbBands);
        /* Recursive averaging */
        sum = (sum + average) >> 1;
        average = sum;
        /* Hysteresis */
        sum = ((3 * sum) + (((3 - last_decision) << 7) + 64) + 2) >> 2;
        if (sum < 80)
        {
            decision = Bands.SPREAD_AGGRESSIVE;
        }
        else if (sum < 256)
        {
            decision = Bands.SPREAD_NORMAL;
        }
        else if (sum < 384)
        {
            decision = Bands.SPREAD_LIGHT;
        }
        else
        {
            decision = Bands.SPREAD_NONE;
        }
        return decision;
    }

    /* Indexing table for converting from natural Hadamard to ordery Hadamard
       This is essentially a bit-reversed Gray, on top of which we've added
       an inversion of the order because we want the DC at the end rather than
       the beginning. The lines are for N=2, 4, 8, 16 */
    private static readonly int[] ordery_table =
    [
        1, 0,
        3, 0, 2, 1,
        7, 0, 4, 3, 6, 1, 5, 2,
        15, 0, 8, 7, 12, 3, 11, 4, 14, 1, 9, 6, 13, 2, 10, 5,
    ];

    private static void deinterleave_hadamard(Span<int> X, int N0, int stride, int hadamard)
    {
        int i, j;
        int N;
        N = N0 * stride;
        Span<int> tmp = stackalloc int[N];
        Debug.Assert(stride > 0);
        if (hadamard != 0)
        {
            ReadOnlySpan<int> ordery = ordery_table.AsSpan(stride - 2);
            for (i = 0; i < stride; i++)
            {
                for (j = 0; j < N0; j++)
                {
                    tmp[(ordery[i] * N0) + j] = X[(j * stride) + i];
                }
            }
        }
        else
        {
            for (i = 0; i < stride; i++)
            {
                for (j = 0; j < N0; j++)
                {
                    tmp[(i * N0) + j] = X[(j * stride) + i];
                }
            }
        }
        tmp.CopyTo(X);
    }

    private static void interleave_hadamard(Span<int> X, int N0, int stride, int hadamard)
    {
        int i, j;
        int N;
        N = N0 * stride;
        Span<int> tmp = stackalloc int[N];
        if (hadamard != 0)
        {
            ReadOnlySpan<int> ordery = ordery_table.AsSpan(stride - 2);
            for (i = 0; i < stride; i++)
            {
                for (j = 0; j < N0; j++)
                {
                    tmp[(j * stride) + i] = X[(ordery[i] * N0) + j];
                }
            }
        }
        else
        {
            for (i = 0; i < stride; i++)
            {
                for (j = 0; j < N0; j++)
                {
                    tmp[(j * stride) + i] = X[(i * N0) + j];
                }
            }
        }
        tmp.CopyTo(X);
    }

    private static readonly short[] exp2_table8 =
        [16384, 17866, 19483, 21247, 23170, 25267, 27554, 30048];

    private static int compute_qn(int N, int b, int offset, int pulse_cap, int stereo)
    {
        int qn, qb;
        int N2 = (2 * N) - 1;
        if (stereo != 0 && N == 2)
        {
            N2--;
        }
        /* The upper limit ensures that in a stereo split with itheta==16384, we'll
            always have enough bits left over to code at least one pulse in the
            side; otherwise it would collapse, since it doesn't get folded. */
        qb = celt_sudiv(b + (N2 * offset), N2);
        qb = IMIN(b - pulse_cap - (4 << BITRES), qb);

        qb = IMIN(8 << BITRES, qb);

        if (qb < (1 << BITRES >> 1))
        {
            qn = 1;
        }
        else
        {
            qn = exp2_table8[qb & 0x7] >> (14 - (qb >> BITRES));
            qn = (qn + 1) >> 1 << 1;
        }
        Debug.Assert(qn <= 256);
        return qn;
    }

    private static void compute_theta(ref band_ctx ctx, ref split_ctx sctx,
          scoped Span<int> X, scoped Span<int> Y, int N, ref int b, int B, int B0,
          int LM,
          int stereo, ref int fill)
    {
        int qn;
        int itheta = 0;
        int itheta_q30 = 0;
        int delta;
        int imid, iside;
        int qalloc;
        int pulse_cap;
        int offset;
        int tell;
        int inv = 0;
        OpusCustomMode m;
        int i;
        int intensity;
        ReadOnlySpan<int> bandE;

        m = ctx.m;
        i = ctx.i;
        intensity = ctx.intensity;
        bandE = ctx.bandE;

        /* Decide on the resolution to give to the split parameter theta */
        pulse_cap = m.logN[i] + (LM * (1 << BITRES));
        offset = (pulse_cap >> 1) - (stereo != 0 && N == 2 ? Rate.QTHETA_OFFSET_TWOPHASE : Rate.QTHETA_OFFSET);
        qn = compute_qn(N, b, offset, pulse_cap, stereo);
        if (stereo != 0 && i >= intensity)
        {
            qn = 1;
        }
        {
            /* theta is the atan() of the ratio between the (normalized)
               side and mid. With just that parameter, we can re-scale both
               mid and side because we know that 1) they have unit norm and
               2) they are orthogonal. */
            itheta_q30 = VqEnc.stereo_itheta(X, Y, stereo, N);
            itheta = itheta_q30 >> 16;
        }
        tell = (int)ctx.ec.ec_tell_frac();
        if (qn != 1)
        {
            {
                if (stereo == 0 || ctx.theta_round == 0)
                {
                    itheta = ((itheta * (int)qn) + 8192) >> 14;
                    if (stereo == 0 && ctx.avoid_split_noise != 0 && itheta > 0 && itheta < qn)
                    {
                        /* Check if the selected value of theta will cause the bit allocation
                           to inject noise on one side. If so, make sure the energy of that side
                           is zero. */
                        int unquantized = (int)celt_udiv((uint)((int)itheta * 16384), (uint)qn);
                        imid = Bands.bitexact_cos((short)unquantized);
                        iside = Bands.bitexact_cos((short)(16384 - unquantized));
                        delta = FRAC_MUL16((N - 1) << 7, Bands.bitexact_log2tan(iside, imid));
                        if (delta > b)
                        {
                            itheta = qn;
                        }
                        else if (delta < -b)
                        {
                            itheta = 0;
                        }
                    }
                }
                else
                {
                    int down;
                    /* Bias quantization towards itheta=0 and itheta=16384. */
                    int bias = itheta > 8192 ? 32767 / qn : -32767 / qn;
                    down = IMIN(qn - 1, IMAX(0, ((itheta * (int)qn) + bias) >> 14));
                    if (ctx.theta_round < 0)
                    {
                        itheta = down;
                    }
                    else
                    {
                        itheta = down + 1;
                    }
                }
            }
            /* Entropy coding of the angle. We use a uniform pdf for the
               time split, a step for stereo, and a triangular one for the rest. */
            if (stereo != 0 && N > 2)
            {
                int p0 = 3;
                int x = itheta;
                int x0 = qn / 2;
                int ft = (p0 * (x0 + 1)) + x0;
                /* Use a probability of p0 up to itheta=8192 and then use 1 after */
                {
                    ctx.ec.ec_encode((uint)(x <= x0 ? p0 * x : (x - 1 - x0) + ((x0 + 1) * p0)), (uint)(x <= x0 ? p0 * (x + 1) : (x - x0) + ((x0 + 1) * p0)), (uint)ft);
                }
            }
            else if (B0 > 1 || stereo != 0)
            {
                /* Uniform pdf */
                ctx.ec.ec_enc_uint((uint)itheta, (uint)(qn + 1));
            }
            else
            {
                int fs = 1, ft;
                ft = ((qn >> 1) + 1) * ((qn >> 1) + 1);
                {
                    int fl;

                    fs = itheta <= (qn >> 1) ? itheta + 1 : qn + 1 - itheta;
                    fl = itheta <= (qn >> 1) ? (itheta * (itheta + 1)) >> 1 :
                     ft - (((qn + 1 - itheta) * (qn + 2 - itheta)) >> 1);

                    ctx.ec.ec_encode((uint)fl, (uint)(fl + fs), (uint)ft);
                }
            }
            Debug.Assert(itheta >= 0);
            itheta = (int)celt_udiv((uint)((int)itheta * 16384), (uint)qn);
            if (stereo != 0)
            {
                if (itheta == 0)
                {
                    intensity_stereo(m, X, Y, bandE, i, N);
                }
                else
                {
                    stereo_split(X, Y, N);
                }
            }
            /* NOTE: Renormalising X and Y *may* help fixed-point a bit at very high rate.
                     Let's do that at higher complexity */
        }
        else if (stereo != 0)
        {
            {
                inv = itheta > 8192 && ctx.disable_inv == 0 ? 1 : 0;
                if (inv != 0)
                {
                    int j;
                    for (j = 0; j < N; j++)
                    {
                        Y[j] = -Y[j];
                    }
                }
                intensity_stereo(m, X, Y, bandE, i, N);
            }
            if (b > 2 << BITRES && ctx.remaining_bits > 2 << BITRES)
            {
                ctx.ec.ec_enc_bit_logp(inv, 2);
            }
            else
            {
                inv = 0;
            }
            /* inv flag override to avoid problems with downmixing. */
            if (ctx.disable_inv != 0)
            {
                inv = 0;
            }
            itheta = 0;
            itheta_q30 = 0;
        }
        _ = itheta_q30;
        qalloc = (int)(ctx.ec.ec_tell_frac() - (uint)tell);
        b -= qalloc;

        if (itheta == 0)
        {
            imid = 32767;
            iside = 0;
            fill &= (1 << B) - 1;
            delta = -16384;
        }
        else if (itheta == 16384)
        {
            imid = 0;
            iside = 32767;
            fill &= ((1 << B) - 1) << B;
            delta = 16384;
        }
        else
        {
            imid = Bands.bitexact_cos((short)itheta);
            iside = Bands.bitexact_cos((short)(16384 - itheta));
            /* This is the mid vs side allocation that minimizes squared error
               in that band. */
            delta = FRAC_MUL16((N - 1) << 7, Bands.bitexact_log2tan(iside, imid));
        }

        sctx.inv = inv;
        sctx.imid = imid;
        sctx.iside = iside;
        sctx.delta = delta;
        sctx.itheta = itheta;
        sctx.qalloc = qalloc;
    }

    private static uint quant_band_n1(ref band_ctx ctx, scoped Span<int> X, scoped Span<int> Y,
          scoped Span<int> lowband_out)
    {
        int c;
        int stereo;
        Span<int> x = X;

        stereo = !Y.IsEmpty ? 1 : 0;
        c = 0;
        do
        {
            int sign = 0;
            if (ctx.remaining_bits >= 1 << BITRES)
            {
                {
                    sign = x[0] < 0 ? 1 : 0;
                    ctx.ec.ec_enc_bits((uint)sign, 1);
                }
                ctx.remaining_bits -= 1 << BITRES;
            }
            if (ctx.resynth != 0)
            {
                x[0] = sign != 0 ? -NORM_SCALING : NORM_SCALING;
            }
            x = Y;
        }
        while (++c < 1 + stereo);
        if (!lowband_out.IsEmpty)
        {
            lowband_out[0] = SHR32(X[0], 4);
        }
        return 1;
    }

    /* quant_partition()'s split test: cache[cache[0]]+12, with C's
       cache = m->cache.bits + m->cache.index[(LM+1)*m->nbEBands+i]. */
    private static int split_threshold(OpusCustomMode m, int i, int LM)
    {
        int cache = m.cache.index[((LM + 1) * m.nbEBands) + i];
        return m.cache.bits[cache + m.cache.bits[cache]] + 12;
    }

    /* This function is responsible for encoding and decoding a mono partition.
       It can split the band in two and transmit the energy difference with
       the two half-bands. It can be called recursively so bands can end up being
       split in 8 parts. */
    private static uint quant_partition(ref band_ctx ctx, scoped Span<int> X,
          int N, int b, int B, scoped Span<int> lowband,
          int LM,
          int gain, int fill)
    {
        int q;
        int curr_bits;
        int imid = 0, iside = 0;
        int B0 = B;
        int mid = 0, side = 0;
        uint cm = 0;
        scoped Span<int> Y = default;
        OpusCustomMode m;
        int i;
        int spread;

        m = ctx.m;
        i = ctx.i;
        spread = ctx.spread;

        /* If we need 1.5 more bit than we can produce, split the band in two. */
        /* C's cache pointer is computed only where it's valid (LM != -1). */
        if (LM != -1 && b > split_threshold(m, i, LM) && N > 2)
        {
            int mbits, sbits, delta;
            int itheta;
            int qalloc;
            split_ctx sctx = default;
            scoped Span<int> next_lowband2 = default;
            int rebalance;

            N >>= 1;
            Y = X.Slice(N);
            LM -= 1;
            if (B == 1)
            {
                fill = (fill & 1) | (fill << 1);
            }
            B = (B + 1) >> 1;

            compute_theta(ref ctx, ref sctx, X, Y, N, ref b, B, B0, LM, 0, ref fill);
            imid = sctx.imid;
            iside = sctx.iside;
            delta = sctx.delta;
            itheta = sctx.itheta;
            qalloc = sctx.qalloc;
            mid = SHL32(EXTEND32(imid), 16);
            side = SHL32(EXTEND32(iside), 16);

            /* Give more bits to low-energy MDCTs than they would otherwise deserve */
            if (B0 > 1 && (itheta & 0x3fff) != 0)
            {
                if (itheta > 8192)
                {
                    /* Rough approximation for pre-echo masking */
                    delta -= delta >> (4 - LM);
                }
                else
                {
                    /* Corresponds to a forward-masking slope of 1.5 dB per 10 ms */
                    delta = IMIN(0, delta + (N << BITRES >> (5 - LM)));
                }
            }
            mbits = IMAX(0, IMIN(b, (b - delta) / 2));
            sbits = b - mbits;
            ctx.remaining_bits -= qalloc;

            if (!lowband.IsEmpty)
            {
                next_lowband2 = lowband.Slice(N); /* >32-bit split case */
            }

            rebalance = ctx.remaining_bits;
            if (mbits >= sbits)
            {
                cm = quant_partition(ref ctx, X, N, mbits, B, lowband, LM,
                      MULT32_32_Q31(gain, mid), fill);
                rebalance = mbits - (rebalance - ctx.remaining_bits);
                if (rebalance > 3 << BITRES && itheta != 0)
                {
                    sbits += rebalance - (3 << BITRES);
                }
                cm |= quant_partition(ref ctx, Y, N, sbits, B, next_lowband2, LM,
                      MULT32_32_Q31(gain, side), fill >> B) << (B0 >> 1);
            }
            else
            {
                cm = quant_partition(ref ctx, Y, N, sbits, B, next_lowband2, LM,
                      MULT32_32_Q31(gain, side), fill >> B) << (B0 >> 1);
                rebalance = sbits - (rebalance - ctx.remaining_bits);
                if (rebalance > 3 << BITRES && itheta != 16384)
                {
                    mbits += rebalance - (3 << BITRES);
                }
                cm |= quant_partition(ref ctx, X, N, mbits, B, lowband, LM,
                      MULT32_32_Q31(gain, mid), fill);
            }
        }
        else
        {
            /* This is the basic no-split case */
            q = Rate.bits2pulses(m, i, LM, b);
            curr_bits = Rate.pulses2bits(m, i, LM, q);
            ctx.remaining_bits -= curr_bits;

            /* Ensures we can never bust the budget */
            while (ctx.remaining_bits < 0 && q > 0)
            {
                ctx.remaining_bits += curr_bits;
                q--;
                curr_bits = Rate.pulses2bits(m, i, LM, q);
                ctx.remaining_bits -= curr_bits;
            }

            if (q != 0)
            {
                int K = Rate.get_pulses(q);

                /* Finally do the actual quantization */
                {
                    cm = VqEnc.alg_quant(X, N, K, spread, B, ref ctx.ec, gain, ctx.resynth);
                }
            }
            else
            {
                /* If there's no pulse, fill the band anyway */
                int j;
                if (ctx.resynth != 0)
                {
                    uint cm_mask;
                    /* B can be as large as 16, so this shift might overflow an int on a
                       16-bit platform; use a long to get defined behavior.*/
                    cm_mask = (uint)(1UL << B) - 1;
                    fill = (int)((uint)fill & cm_mask);
                    if (fill == 0)
                    {
                        X.Slice(0, N).Clear();
                    }
                    else
                    {
                        if (lowband.IsEmpty)
                        {
                            /* Noise */
                            for (j = 0; j < N; j++)
                            {
                                ctx.seed = Bands.celt_lcg_rand(ctx.seed);
                                X[j] = SHL32((int)ctx.seed >> 20, NORM_SHIFT - 14);
                            }
                            cm = cm_mask;
                        }
                        else
                        {
                            /* Folded spectrum */
                            for (j = 0; j < N; j++)
                            {
                                short tmp;
                                ctx.seed = Bands.celt_lcg_rand(ctx.seed);
                                /* About 48 dB below the "normal" folding level */
                                tmp = 4096; /* QCONST16(1.0f/256, NORM_SHIFT-4) */
                                tmp = (ctx.seed & 0x8000) != 0 ? tmp : (short)-tmp;
                                X[j] = lowband[j] + tmp;
                            }
                            cm = (uint)fill;
                        }
                        Vq.renormalise_vector(X, N, gain);
                    }
                }
            }
        }

        return cm;
    }

    private static readonly byte[] bit_interleave_table =
    [
        0, 1, 1, 1, 2, 3, 3, 3, 2, 3, 3, 3, 2, 3, 3, 3,
    ];

    private static readonly byte[] bit_deinterleave_table =
    [
        0x00, 0x03, 0x0C, 0x0F, 0x30, 0x33, 0x3C, 0x3F,
        0xC0, 0xC3, 0xCC, 0xCF, 0xF0, 0xF3, 0xFC, 0xFF,
    ];

    /* This function is responsible for encoding and decoding a band for the mono case. */
    private static uint quant_band(ref band_ctx ctx, scoped Span<int> X,
          int N, int b, int B, scoped Span<int> lowband,
          int LM, scoped Span<int> lowband_out,
          int gain, scoped Span<int> lowband_scratch, int fill)
    {
        int N0 = N;
        int N_B = N;
        int N_B0;
        int B0 = B;
        int time_divide = 0;
        int recombine = 0;
        int longBlocks;
        uint cm = 0;
        int k;
        int tf_change;

        tf_change = ctx.tf_change;

        longBlocks = B0 == 1 ? 1 : 0;

        N_B = (int)celt_udiv((uint)N_B, (uint)B);

        /* Special case for one sample */
        if (N == 1)
        {
            return quant_band_n1(ref ctx, X, default, lowband_out);
        }

        if (tf_change > 0)
        {
            recombine = tf_change;
        }
        /* Band recombining to increase frequency resolution */

        if (!lowband_scratch.IsEmpty && !lowband.IsEmpty && (recombine != 0 || ((N_B & 1) == 0 && tf_change < 0) || B0 > 1))
        {
            lowband.Slice(0, N).CopyTo(lowband_scratch);
            lowband = lowband_scratch;
        }

        for (k = 0; k < recombine; k++)
        {
            Bands.haar1(X, N >> k, 1 << k);
            if (!lowband.IsEmpty)
            {
                Bands.haar1(lowband, N >> k, 1 << k);
            }
            fill = bit_interleave_table[fill & 0xF] | (bit_interleave_table[fill >> 4] << 2);
        }
        B >>= recombine;
        N_B <<= recombine;

        /* Increasing the time resolution */
        while ((N_B & 1) == 0 && tf_change < 0)
        {
            Bands.haar1(X, N_B, B);
            if (!lowband.IsEmpty)
            {
                Bands.haar1(lowband, N_B, B);
            }
            fill |= fill << B;
            B <<= 1;
            N_B >>= 1;
            time_divide++;
            tf_change++;
        }
        B0 = B;
        N_B0 = N_B;

        /* Reorganize the samples in time order instead of frequency order */
        if (B0 > 1)
        {
            deinterleave_hadamard(X, N_B >> recombine, B0 << recombine, longBlocks);
            if (!lowband.IsEmpty)
            {
                deinterleave_hadamard(lowband, N_B >> recombine, B0 << recombine, longBlocks);
            }
        }

        {
            cm = quant_partition(ref ctx, X, N, b, B, lowband, LM, gain, fill);
        }

        /* This code is used by the decoder and by the resynthesis-enabled encoder */
        if (ctx.resynth != 0)
        {
            /* Undo the sample reorganization going from time order to frequency order */
            if (B0 > 1)
            {
                interleave_hadamard(X, N_B >> recombine, B0 << recombine, longBlocks);
            }

            /* Undo time-freq changes that we did earlier */
            N_B = N_B0;
            B = B0;
            for (k = 0; k < time_divide; k++)
            {
                B >>= 1;
                N_B <<= 1;
                cm |= cm >> B;
                Bands.haar1(X, N_B, B);
            }

            for (k = 0; k < recombine; k++)
            {
                cm = bit_deinterleave_table[cm];
                Bands.haar1(X, N0 >> k, 1 << k);
            }
            B <<= recombine;

            /* Scale output for later folding */
            if (!lowband_out.IsEmpty)
            {
                int j;
                short n;
                n = (short)celt_sqrt(SHL32(EXTEND32(N0), 22));
                for (j = 0; j < N0; j++)
                {
                    lowband_out[j] = MULT16_32_Q15(n, X[j]);
                }
            }
            cm &= (uint)((1 << B) - 1);
        }
        return cm;
    }

    private const int MIN_STEREO_ENERGY = 2;

    /* This function is responsible for encoding and decoding a band for the stereo case. */
    private static uint quant_band_stereo(ref band_ctx ctx, scoped Span<int> X, scoped Span<int> Y,
          int N, int b, int B, scoped Span<int> lowband,
          int LM, scoped Span<int> lowband_out,
          scoped Span<int> lowband_scratch, int fill)
    {
        int imid = 0, iside = 0;
        int inv = 0;
        int mid = 0, side = 0;
        uint cm = 0;
        int mbits, sbits, delta;
        int itheta;
        int qalloc;
        split_ctx sctx = default;
        int orig_fill;

        /* Special case for one sample */
        if (N == 1)
        {
            return quant_band_n1(ref ctx, X, Y, lowband_out);
        }

        orig_fill = fill;

        {
            if (ctx.bandE[ctx.i] < MIN_STEREO_ENERGY || ctx.bandE[ctx.m.nbEBands + ctx.i] < MIN_STEREO_ENERGY)
            {
                if (ctx.bandE[ctx.i] > ctx.bandE[ctx.m.nbEBands + ctx.i])
                {
                    X.Slice(0, N).CopyTo(Y);
                }
                else
                {
                    Y.Slice(0, N).CopyTo(X);
                }
            }
        }
        compute_theta(ref ctx, ref sctx, X, Y, N, ref b, B, B, LM, 1, ref fill);
        inv = sctx.inv;
        imid = sctx.imid;
        iside = sctx.iside;
        delta = sctx.delta;
        itheta = sctx.itheta;
        qalloc = sctx.qalloc;
        mid = SHL32(EXTEND32(imid), 16);
        side = SHL32(EXTEND32(iside), 16);

        /* This is a special case for N=2 that only works for stereo and takes
           advantage of the fact that mid and side are orthogonal to encode
           the side with just one bit. */
        if (N == 2)
        {
            int c;
            int sign = 0;
            scoped Span<int> x2, y2;
            mbits = b;
            sbits = 0;
            /* Only need one bit for the side. */
            if (itheta != 0 && itheta != 16384)
            {
                sbits = 1 << BITRES;
            }
            mbits -= sbits;
            c = itheta > 8192 ? 1 : 0;
            ctx.remaining_bits -= qalloc + sbits;

            x2 = c != 0 ? Y : X;
            y2 = c != 0 ? X : Y;
            if (sbits != 0)
            {
                {
                    /* Here we only need to encode a sign for the side. */
                    /* FIXME: Need to increase fixed-point precision? */
                    sign = MULT32_32_Q31(x2[0], y2[1]) - MULT32_32_Q31(x2[1], y2[0]) < 0 ? 1 : 0;
                    ctx.ec.ec_enc_bits((uint)sign, 1);
                }
            }
            sign = 1 - (2 * sign);
            /* We use orig_fill here because we want to fold the side, but if
               itheta==16384, we'll have cleared the low bits of fill. */
            cm = quant_band(ref ctx, x2, N, mbits, B, lowband, LM, lowband_out, Q31ONE,
                  lowband_scratch, orig_fill);
            /* We don't split N=2 bands, so cm is either 1 or 0 (for a fold-collapse),
               and there's no need to worry about mixing with the other channel. */
            y2[0] = -sign * x2[1];
            y2[1] = sign * x2[0];
            if (ctx.resynth != 0)
            {
                int tmp;
                X[0] = MULT32_32_Q31(mid, X[0]);
                X[1] = MULT32_32_Q31(mid, X[1]);
                Y[0] = MULT32_32_Q31(side, Y[0]);
                Y[1] = MULT32_32_Q31(side, Y[1]);
                tmp = X[0];
                X[0] = SUB32(tmp, Y[0]);
                Y[0] = ADD32(tmp, Y[0]);
                tmp = X[1];
                X[1] = SUB32(tmp, Y[1]);
                Y[1] = ADD32(tmp, Y[1]);
            }
        }
        else
        {
            /* "Normal" split code */
            int rebalance;

            mbits = IMAX(0, IMIN(b, (b - delta) / 2));
            sbits = b - mbits;
            ctx.remaining_bits -= qalloc;

            rebalance = ctx.remaining_bits;
            if (mbits >= sbits)
            {
                /* In stereo mode, we do not apply a scaling to the mid because we need the normalized
                   mid for folding later. */
                cm = quant_band(ref ctx, X, N, mbits, B, lowband, LM, lowband_out, Q31ONE,
                      lowband_scratch, fill);
                rebalance = mbits - (rebalance - ctx.remaining_bits);
                if (rebalance > 3 << BITRES && itheta != 0)
                {
                    sbits += rebalance - (3 << BITRES);
                }
                /* For a stereo split, the high bits of fill are always zero, so no
                   folding will be done to the side. */
                cm |= quant_band(ref ctx, Y, N, sbits, B, default, LM, default, side, default, fill >> B);
            }
            else
            {
                /* For a stereo split, the high bits of fill are always zero, so no
                   folding will be done to the side. */
                cm = quant_band(ref ctx, Y, N, sbits, B, default, LM, default, side, default, fill >> B);
                rebalance = sbits - (rebalance - ctx.remaining_bits);
                if (rebalance > 3 << BITRES && itheta != 16384)
                {
                    mbits += rebalance - (3 << BITRES);
                }
                /* In stereo mode, we do not apply a scaling to the mid because we need the normalized
                   mid for folding later. */
                cm |= quant_band(ref ctx, X, N, mbits, B, lowband, LM, lowband_out, Q31ONE,
                      lowband_scratch, fill);
            }
        }

        /* This code is used by the decoder and by the resynthesis-enabled encoder */
        if (ctx.resynth != 0)
        {
            if (N != 2)
            {
                stereo_merge(X, Y, mid, N);
            }
            if (inv != 0)
            {
                int j;
                for (j = 0; j < N; j++)
                {
                    Y[j] = -Y[j];
                }
            }
        }
        return cm;
    }

    private static void special_hybrid_folding(OpusCustomMode m, Span<int> norm, Span<int> norm2, int start, int M, int dual_stereo)
    {
        int n1, n2;
        short[] eBands = m.eBands;
        n1 = M * (eBands[start + 1] - eBands[start]);
        n2 = M * (eBands[start + 2] - eBands[start + 1]);
        /* Duplicate enough of the first band folding data to be able to fold the second band.
           Copies no data for CELT-only mode. */
        norm.Slice((2 * n1) - n2, n2 - n1).CopyTo(norm.Slice(n1));
        if (dual_stereo != 0)
        {
            norm2.Slice((2 * n1) - n2, n2 - n1).CopyTo(norm2.Slice(n1));
        }
    }

    /* The encoder's quant_all_bands(): C's encode is 1 and arch is gone. Y_ empty stands for C's
       NULL (mono). bandE is kept in the band context, so it isn't scoped. */
    internal static void quant_all_bands(OpusCustomMode m, int start, int end,
          scoped Span<int> X_, scoped Span<int> Y_, scoped Span<byte> collapse_masks,
          ReadOnlySpan<int> bandE, scoped ReadOnlySpan<int> pulses, int shortBlocks, int spread,
          int dual_stereo, int intensity, scoped ReadOnlySpan<int> tf_res, int total_bits,
          int balance, ref ec_enc ec, int LM, int codedBands,
          ref uint seed, int complexity, int disable_inv)
    {
        int i;
        int remaining_bits;
        short[] eBands = m.eBands;
        scoped Span<int> norm, norm2;
        scoped Span<int> lowband_scratch;
        int resynth_alloc;
        int B;
        int M;
        int lowband_offset;
        int update_lowband = 1;
        int C = !Y_.IsEmpty ? 2 : 1;
        int norm_offset;
        int theta_rdo = !Y_.IsEmpty && dual_stereo == 0 && complexity >= 8 ? 1 : 0;
        int resynth = theta_rdo;
        band_ctx ctx = default;

        M = 1 << LM;
        B = shortBlocks != 0 ? M : 1;
        norm_offset = M * eBands[start];
        /* No need to allocate norm for the last band because we don't need an
           output in that band. */
        Span<int> _norm = stackalloc int[C * ((M * eBands[m.nbEBands - 1]) - norm_offset)];
        norm = _norm;
        norm2 = norm.Slice((M * eBands[m.nbEBands - 1]) - norm_offset);

        /* For decoding, we can use the last band as scratch space because we don't need that
           scratch space for the last band and we don't care about the data there until we're
           decoding the last band. */
        if (resynth != 0)
        {
            resynth_alloc = M * (eBands[m.nbEBands] - eBands[m.nbEBands - 1]);
        }
        else
        {
            resynth_alloc = 1; /* ALLOC_NONE */
        }
        Span<int> _lowband_scratch = stackalloc int[resynth_alloc];
        if (resynth != 0)
        {
            lowband_scratch = _lowband_scratch;
        }
        else
        {
            lowband_scratch = X_.Slice(M * eBands[m.effEBands - 1]);
        }
        Span<int> X_save = stackalloc int[resynth_alloc];
        Span<int> Y_save = stackalloc int[resynth_alloc];
        Span<int> X_save2 = stackalloc int[resynth_alloc];
        Span<int> Y_save2 = stackalloc int[resynth_alloc];
        Span<int> norm_save2 = stackalloc int[resynth_alloc];

        lowband_offset = 0;
        ctx.bandE = bandE;
        ctx.ec = ec;
        ctx.intensity = intensity;
        ctx.m = m;
        ctx.seed = seed;
        ctx.spread = spread;
        ctx.disable_inv = disable_inv;
        ctx.resynth = resynth;
        ctx.theta_round = 0;
        Span<byte> bytes_save = stackalloc byte[theta_rdo != 0 ? 1275 : 1];
        /* C's opus_val16 w[2] of the theta RDO, hoisted out of the band loop. */
        Span<short> w = stackalloc short[2];

        /* Avoid injecting noise in the first band on transients. */
        ctx.avoid_split_noise = B > 1 ? 1 : 0;
        for (i = start; i < end; i++)
        {
            int tell;
            int b;
            int N;
            int curr_balance;
            int effective_lowband = -1;
            scoped Span<int> X, Y;
            int tf_change = 0;
            uint x_cm;
            uint y_cm;
            int last;

            ctx.i = i;
            last = i == end - 1 ? 1 : 0;

            X = X_.Slice(M * eBands[i]);
            if (!Y_.IsEmpty)
            {
                Y = Y_.Slice(M * eBands[i]);
            }
            else
            {
                Y = default;
            }
            N = (M * eBands[i + 1]) - (M * eBands[i]);
            Debug.Assert(N > 0);
            tell = (int)ctx.ec.ec_tell_frac();

            /* Compute how many bits we want to allocate to this band */
            if (i != start)
            {
                balance -= tell;
            }
            remaining_bits = total_bits - tell - 1;
            ctx.remaining_bits = remaining_bits;
            if (i <= codedBands - 1)
            {
                curr_balance = celt_sudiv(balance, IMIN(3, codedBands - i));
                b = IMAX(0, IMIN(16383, IMIN(remaining_bits + 1, pulses[i] + curr_balance)));
            }
            else
            {
                b = 0;
            }

            if (resynth != 0 && ((M * eBands[i]) - N >= M * eBands[start] || i == start + 1) && (update_lowband != 0 || lowband_offset == 0))
            {
                lowband_offset = i;
            }
            if (i == start + 1)
            {
                special_hybrid_folding(m, norm, norm2, start, M, dual_stereo);
            }

            tf_change = tf_res[i];
            ctx.tf_change = tf_change;
            if (i >= m.effEBands)
            {
                X = norm;
                if (!Y_.IsEmpty)
                {
                    Y = norm;
                }
                lowband_scratch = default;
            }
            if (last != 0 && theta_rdo == 0)
            {
                lowband_scratch = default;
            }

            /* Get a conservative estimate of the collapse_mask's for the bands we're
               going to be folding from. */
            if (lowband_offset != 0 && (spread != Bands.SPREAD_AGGRESSIVE || B > 1 || tf_change < 0))
            {
                int fold_start;
                int fold_end;
                int fold_i;
                /* This ensures we never repeat spectral content within one band */
                effective_lowband = IMAX(0, (M * eBands[lowband_offset]) - norm_offset - N);
                fold_start = lowband_offset;
                while (M * eBands[--fold_start] > effective_lowband + norm_offset)
                {
                }
                fold_end = lowband_offset - 1;
                while (++fold_end < i && M * eBands[fold_end] < effective_lowband + norm_offset + N)
                {
                }
                x_cm = y_cm = 0;
                fold_i = fold_start;
                do
                {
                    x_cm |= collapse_masks[(fold_i * C) + 0];
                    y_cm |= collapse_masks[(fold_i * C) + C - 1];
                }
                while (++fold_i < fold_end);
            }
            /* Otherwise, we'll be using the LCG to fold, so all blocks will (almost
               always) be non-zero. */
            else
            {
                x_cm = y_cm = (uint)((1 << B) - 1);
            }

            if (dual_stereo != 0 && i == intensity)
            {
                int j;

                /* Switch off dual stereo to do intensity. */
                dual_stereo = 0;
                if (resynth != 0)
                {
                    for (j = 0; j < (M * eBands[i]) - norm_offset; j++)
                    {
                        norm[j] = HALF32(norm[j] + norm2[j]);
                    }
                }
            }
            if (dual_stereo != 0)
            {
                x_cm = quant_band(ref ctx, X, N, b / 2, B,
                      effective_lowband != -1 ? norm.Slice(effective_lowband) : default, LM,
                      last != 0 ? default : norm.Slice((M * eBands[i]) - norm_offset), Q31ONE, lowband_scratch, (int)x_cm);
                y_cm = quant_band(ref ctx, Y, N, b / 2, B,
                      effective_lowband != -1 ? norm2.Slice(effective_lowband) : default, LM,
                      last != 0 ? default : norm2.Slice((M * eBands[i]) - norm_offset), Q31ONE, lowband_scratch, (int)y_cm);
            }
            else
            {
                if (!Y.IsEmpty)
                {
                    if (theta_rdo != 0 && i < intensity)
                    {
                        ec_enc ec_save, ec_save2;
                        band_ctx ctx_save, ctx_save2;
                        int dist0, dist1;
                        uint cm, cm2;
                        int nstart_bytes, nend_bytes, save_bytes;
                        Span<byte> bytes_buf;
                        compute_channel_weights(bandE[i], bandE[i + m.nbEBands], w);
                        /* Make a copy. */
                        cm = x_cm | y_cm;
                        ec_save = ctx.ec;
                        ctx_save = ctx;
                        X.Slice(0, N).CopyTo(X_save);
                        Y.Slice(0, N).CopyTo(Y_save);
                        /* Encode and round down. */
                        ctx.theta_round = -1;
                        x_cm = quant_band_stereo(ref ctx, X, Y, N, b, B,
                              effective_lowband != -1 ? norm.Slice(effective_lowband) : default, LM,
                              last != 0 ? default : norm.Slice((M * eBands[i]) - norm_offset), lowband_scratch, (int)cm);
                        dist0 = MULT16_32_Q15(w[0], Vq.celt_inner_prod_norm_shift(X_save, X, N)) + MULT16_32_Q15(w[1], Vq.celt_inner_prod_norm_shift(Y_save, Y, N));

                        /* Save first result. */
                        cm2 = x_cm;
                        ec_save2 = ctx.ec;
                        ctx_save2 = ctx;
                        X.Slice(0, N).CopyTo(X_save2);
                        Y.Slice(0, N).CopyTo(Y_save2);
                        if (last == 0)
                        {
                            norm.Slice((M * eBands[i]) - norm_offset, N).CopyTo(norm_save2);
                        }
                        nstart_bytes = (int)ec_save.offs;
                        nend_bytes = (int)ec_save.storage;
                        bytes_buf = ec_save.buf.Slice(nstart_bytes);
                        save_bytes = nend_bytes - nstart_bytes;
                        bytes_buf.Slice(0, save_bytes).CopyTo(bytes_save);
                        /* Restore */
                        ctx.ec = ec_save;
                        ctx = ctx_save;
                        X_save.Slice(0, N).CopyTo(X);
                        Y_save.Slice(0, N).CopyTo(Y);
                        if (i == start + 1)
                        {
                            special_hybrid_folding(m, norm, norm2, start, M, dual_stereo);
                        }
                        /* Encode and round up. */
                        ctx.theta_round = 1;
                        x_cm = quant_band_stereo(ref ctx, X, Y, N, b, B,
                              effective_lowband != -1 ? norm.Slice(effective_lowband) : default, LM,
                              last != 0 ? default : norm.Slice((M * eBands[i]) - norm_offset), lowband_scratch, (int)cm);
                        dist1 = MULT16_32_Q15(w[0], Vq.celt_inner_prod_norm_shift(X_save, X, N)) + MULT16_32_Q15(w[1], Vq.celt_inner_prod_norm_shift(Y_save, Y, N));
                        if (dist0 >= dist1)
                        {
                            x_cm = cm2;
                            ctx.ec = ec_save2;
                            ctx = ctx_save2;
                            X_save2.Slice(0, N).CopyTo(X);
                            Y_save2.Slice(0, N).CopyTo(Y);
                            if (last == 0)
                            {
                                norm_save2.Slice(0, N).CopyTo(norm.Slice((M * eBands[i]) - norm_offset));
                            }
                            bytes_save.Slice(0, save_bytes).CopyTo(bytes_buf);
                        }
                    }
                    else
                    {
                        ctx.theta_round = 0;
                        x_cm = quant_band_stereo(ref ctx, X, Y, N, b, B,
                              effective_lowband != -1 ? norm.Slice(effective_lowband) : default, LM,
                              last != 0 ? default : norm.Slice((M * eBands[i]) - norm_offset), lowband_scratch, (int)(x_cm | y_cm));
                    }
                }
                else
                {
                    x_cm = quant_band(ref ctx, X, N, b, B,
                          effective_lowband != -1 ? norm.Slice(effective_lowband) : default, LM,
                          last != 0 ? default : norm.Slice((M * eBands[i]) - norm_offset), Q31ONE, lowband_scratch, (int)(x_cm | y_cm));
                }
                y_cm = x_cm;
            }
            collapse_masks[(i * C) + 0] = (byte)x_cm;
            collapse_masks[(i * C) + C - 1] = (byte)y_cm;
            balance += pulses[i] + tell;

            /* Update the folding position only as long as we have 1 bit/sample depth. */
            update_lowband = b > (N << BITRES) ? 1 : 0;
            /* We only need to avoid noise on a split for the first band. After that, we
               have folding. */
            ctx.avoid_split_noise = 0;
        }
        seed = ctx.seed;
        ec = ctx.ec;
    }
}
