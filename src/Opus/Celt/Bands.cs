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
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using DotGame.AudioCodecs.Opus.Common;
using static DotGame.AudioCodecs.Opus.Celt.Arch;
using static DotGame.AudioCodecs.Opus.Celt.MathOps;
using static DotGame.AudioCodecs.Opus.Common.ec_dec;

namespace DotGame.AudioCodecs.Opus.Celt;

/* The state quant_all_bands() shares with the functions it calls. C holds an ec_ctx* here; a ref
   struct can't hold a ref to the ec_dec ref struct, so it holds the decoder itself, copied in at
   the start of quant_all_bands() and back out at its end (nothing else touches it in between).
   The encoder-only fields (encode, bandE, arch, theta_round, avoid_split_noise) are gone. */
internal ref struct band_ctx
{
    internal int resynth;
    internal OpusCustomMode m;
    internal int i;
    internal int intensity;
    internal int spread;
    internal int tf_change;
    internal ec_dec ec;
    internal int remaining_bits;
    internal uint seed;
    internal int disable_inv;
}

internal struct split_ctx
{
    internal int inv;
    internal int imid;
    internal int iside;
    internal int delta;
    internal int itheta;
    internal int qalloc;
}

/// <summary>
/// The decoder side of celt/bands.c: band (de)normalisation, anti-collapse, and decoding the
/// PVQ-coded spectrum (quant_all_bands and what it calls, with the encoder branches removed).
/// Where C passes NULL for a celt_norm pointer, an empty span is passed.
/// </summary>
[SkipLocalsInit]
internal static class Bands
{
    internal const int SPREAD_NONE = 0;
    internal const int SPREAD_LIGHT = 1;
    internal const int SPREAD_NORMAL = 2;
    internal const int SPREAD_AGGRESSIVE = 3;

    internal static uint celt_lcg_rand(uint seed)
    {
        return (1664525 * seed) + 1013904223;
    }

    /* This is a cos() approximation designed to be bit-exact on any platform. Bit exactness
       with this approximation is important because it has an impact on the bit allocation */
    internal static short bitexact_cos(short x)
    {
        int tmp;
        short x2;
        tmp = (4096 + ((int)(x) * (x))) >> 13;
        x2 = (short)tmp;
        x2 = (short)((32767 - x2) + FRAC_MUL16(x2, (-7651 + FRAC_MUL16(x2, (8277 + FRAC_MUL16(-626, x2))))));
        return (short)(1 + x2);
    }

    internal static int bitexact_log2tan(int isin, int icos)
    {
        int lc;
        int ls;
        lc = EC_ILOG((uint)icos);
        ls = EC_ILOG((uint)isin);
        icos <<= 15 - lc;
        isin <<= 15 - ls;
        return ((ls - lc) * (1 << 11))
              + FRAC_MUL16(isin, FRAC_MUL16(isin, -2597) + 7932)
              - FRAC_MUL16(icos, FRAC_MUL16(icos, -2597) + 7932);
    }

    /* De-normalise the energy to produce the synthesis from the unit-energy bands */
    internal static void denormalise_bands(OpusCustomMode m, ReadOnlySpan<int> X,
          Span<int> freq, ReadOnlySpan<int> bandLogE, int start,
          int end, int M, int downsample, int silence)
    {
        int i, N;
        int bound;
        int f;
        int x;
        short[] eBands = m.eBands;
        N = M * m.shortMdctSize;
        bound = M * eBands[end];
        if (downsample != 1)
        {
            bound = IMIN(bound, N / downsample);
        }
        if (silence != 0)
        {
            bound = 0;
            start = end = 0;
        }
        f = 0;
        x = M * eBands[start];
        if (start != 0)
        {
            for (i = 0; i < M * eBands[start]; i++)
            {
                freq[f++] = 0;
            }
        }
        else
        {
            f += M * eBands[start];
        }
        for (i = start; i < end; i++)
        {
            int j, band_end;
            int g;
            int lg;
            int shift;
            j = M * eBands[i];
            band_end = M * eBands[i + 1];
            lg = ADD32(bandLogE[i], SHL32((int)QuantBands.eMeans[i], DB_SHIFT - 4));
            /* Handle the integer part of the log energy */
            shift = 17 - (lg >> DB_SHIFT);
            if (shift >= 31)
            {
                shift = 0;
                g = 0;
            }
            else
            {
                /* Handle the fractional part. */
                g = SHL32(celt_exp2_db_frac(lg & ((1 << DB_SHIFT) - 1)), 2);
            }
            /* Handle extreme gains with negative shift. */
            if (shift < 0)
            {
                /* To avoid overflow, we're
                   capping the gain here, which is equivalent to a cap of 18 on lg.
                   This shouldn't trigger unless the bitstream is already corrupted. */
                g = 2147483647;
                shift = 0;
            }
            if (Simd.HasMULT32_32_Q31)
            {
                Vector128<int> vg = Vector128.Create(g);
                Vector128<int> round = Vector128.Create((1 << shift) >> 1);
                for (; j + 4 <= band_end; j += 4)
                {
                    Vector128<int> v = (Vector128.Create(X.Slice(x, 4)) << (30 - NORM_SHIFT));
                    ((Simd.MULT32_32_Q31(v, vg) + round) >> shift).CopyTo(freq.Slice(f, 4));
                    f += 4;
                    x += 4;
                }
            }
            for (; j < band_end; j++)
            {
                freq[f++] = PSHR32(MULT32_32_Q31(SHL32(X[x], 30 - NORM_SHIFT), g), shift);
                x++;
            }
        }
        freq.Slice(bound, N - bound).Clear();
    }

    /* This prevents energy collapse for transients with multiple short MDCTs */
    internal static void anti_collapse(OpusCustomMode m, Span<int> X_, ReadOnlySpan<byte> collapse_masks, int LM, int C, int size,
          int start, int end, ReadOnlySpan<int> logE, ReadOnlySpan<int> prev1logE,
          ReadOnlySpan<int> prev2logE, ReadOnlySpan<int> pulses, uint seed)
    {
        int c, i, j, k;
        for (i = start; i < end; i++)
        {
            int N0;
            short thresh, sqrt_1;
            int depth;
            int shift;
            int thresh32;

            N0 = m.eBands[i + 1] - m.eBands[i];
            /* depth in 1/8 bits */
            depth = (int)(celt_udiv((uint)(1 + pulses[i]), (uint)(m.eBands[i + 1] - m.eBands[i])) >> LM);

            thresh32 = SHR32(celt_exp2((short)-SHL16(depth, 10 - BITRES)), 1);
            thresh = (short)MULT16_32_Q15(16384 /* QCONST16(0.5f, 15) */, MIN32(32767, thresh32));
            {
                int t;
                t = N0 << LM;
                shift = celt_ilog2(t) >> 1;
                t = SHL32(t, (7 - shift) << 1);
                sqrt_1 = celt_rsqrt_norm(t);
            }

            c = 0;
            do
            {
                Span<int> X;
                int prev1;
                int prev2;
                int Ediff;
                int r;
                int renormalize = 0;
                prev1 = prev1logE[(c * m.nbEBands) + i];
                prev2 = prev2logE[(c * m.nbEBands) + i];
                if (C == 1)
                {
                    prev1 = MAXG(prev1, prev1logE[m.nbEBands + i]);
                    prev2 = MAXG(prev2, prev2logE[m.nbEBands + i]);
                }
                Ediff = logE[(c * m.nbEBands) + i] - MING(prev1, prev2);
                Ediff = MAX32(0, Ediff);

                if (Ediff < 268435456 /* GCONST(16.f) */)
                {
                    int r32 = SHR32(celt_exp2_db(-Ediff), 1);
                    r = 2 * MIN16(16383, r32);
                }
                else
                {
                    r = 0;
                }
                if (LM == 3)
                {
                    r = MULT16_16_Q14(23170, MIN32(23169, r));
                }
                r = SHR16(MIN16(thresh, r), 1);
                r = VSHR32(MULT16_16_Q15(sqrt_1, r), shift + 14 - NORM_SHIFT);
                X = X_.Slice((c * size) + (m.eBands[i] << LM));
                for (k = 0; k < 1 << LM; k++)
                {
                    /* Detect collapse */
                    if ((collapse_masks[(i * C) + c] & (1 << k)) == 0)
                    {
                        /* Fill with noise */
                        for (j = 0; j < N0; j++)
                        {
                            seed = celt_lcg_rand(seed);
                            X[(j << LM) + k] = (seed & 0x8000) != 0 ? r : -r;
                        }
                        renormalize = 1;
                    }
                }
                /* We just added some energy, so we need to renormalise */
                if (renormalize != 0)
                {
                    Vq.renormalise_vector(X, N0 << LM, Q31ONE);
                }
            }
            while (++c < C);
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

        j = 0;
        if (Simd.HasMULT32_32_Q31)
        {
            Vector128<int> vmid = Vector128.Create(mid);
            Vector128<int> vl = Vector128.Create(lgain);
            Vector128<int> vr = Vector128.Create(rgain);
            for (; j + 4 <= N; j += 4)
            {
                /* Apply mid scaling (side is already scaled) */
                Vector128<int> l4 = Simd.MULT32_32_Q31(vmid, Vector128.Create((ReadOnlySpan<int>)X.Slice(j, 4)));
                Vector128<int> r4 = Vector128.Create((ReadOnlySpan<int>)Y.Slice(j, 4));
                Simd.VSHR32(Simd.MULT32_32_Q31(vl, l4 - r4), kl - 15).CopyTo(X.Slice(j, 4));
                Simd.VSHR32(Simd.MULT32_32_Q31(vr, l4 + r4), kr - 15).CopyTo(Y.Slice(j, 4));
            }
        }
        for (; j < N; j++)
        {
            int r, l;
            /* Apply mid scaling (side is already scaled) */
            l = MULT32_32_Q31(mid, X[j]);
            r = Y[j];
            X[j] = VSHR32(MULT32_32_Q31(lgain, SUB32(l, r)), kl - 15);
            Y[j] = VSHR32(MULT32_32_Q31(rgain, ADD32(l, r)), kr - 15);
        }
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

    // Each pair (X[2j stride + i], X[(2j + 1) stride + i]) becomes their sum and difference, each
    // scaled by 1/sqrt(2) first; the pairs are independent, so four lanes take adjacent pairs (stride
    // 1 and 2) or four i at once (strides of 4 and more), each lane as the C computes it
    internal static void haar1(Span<int> X, int N0, int stride)
    {
        int i, j;
        N0 >>= 1;
        if (Simd.HasMULT32_32_Q31 && (N0 > 0) && (((stride == 1) && ((N0 & 1) == 0)) || (stride == 2) || ((stride & 3) == 0)))
        {
            Span<int> all = X[..(2 * N0 * stride)];
            ref int x = ref MemoryMarshal.GetReference(all);
            var c = Vector128.Create(1518500224);
            if (stride <= 2)
            {
                // Lanes hold a, b of two pairs (stride 1: a0 b0 a1 b1; stride 2: a0 a1 b0 b1)
                var partner = stride == 1 ? Vector128.Create(1, 0, 3, 2) : Vector128.Create(2, 3, 0, 1);
                var first = stride == 1 ? Vector128.Create(-1, 0, -1, 0) : Vector128.Create(-1, -1, 0, 0);
                for (int k = 0; k < all.Length; k += 4)
                {
                    var t = Simd.MULT32_32_Q31(c, Vector128.LoadUnsafe(ref x, (nuint)k));
                    var other = Vector128.Shuffle(t, partner);
                    Vector128.ConditionalSelect(first, t + other, other - t).StoreUnsafe(ref x, (nuint)k);
                }
                return;
            }
            for (j = 0; j < N0; j++)
            {
                ref int a = ref Unsafe.Add(ref x, stride * 2 * j);
                ref int b = ref Unsafe.Add(ref a, stride);
                for (i = 0; i < stride; i += 4)
                {
                    var ta = Simd.MULT32_32_Q31(c, Vector128.LoadUnsafe(ref a, (nuint)i));
                    var tb = Simd.MULT32_32_Q31(c, Vector128.LoadUnsafe(ref b, (nuint)i));
                    (ta + tb).StoreUnsafe(ref a, (nuint)i);
                    (ta - tb).StoreUnsafe(ref b, (nuint)i);
                }
            }
            return;
        }
        for (i = 0; i < stride; i++)
        {
            for (j = 0; j < N0; j++)
            {
                int tmp1, tmp2;
                tmp1 = MULT32_32_Q31(1518500224 /* QCONST32(.70710678f,31) */, X[(stride * 2 * j) + i]);
                tmp2 = MULT32_32_Q31(1518500224 /* QCONST32(.70710678f,31) */, X[(stride * ((2 * j) + 1)) + i]);
                X[(stride * 2 * j) + i] = ADD32(tmp1, tmp2);
                X[(stride * ((2 * j) + 1)) + i] = SUB32(tmp1, tmp2);
            }
        }
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
        return qn;
    }

    private static void compute_theta(ref band_ctx ctx, ref split_ctx sctx,
          scoped Span<int> X, scoped Span<int> Y, int N, ref int b, int B, int B0,
          int LM,
          int stereo, ref int fill)
    {
        int qn;
        int itheta = 0;
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

        m = ctx.m;
        i = ctx.i;
        intensity = ctx.intensity;

        /* Decide on the resolution to give to the split parameter theta */
        pulse_cap = m.logN[i] + (LM * (1 << BITRES));
        offset = (pulse_cap >> 1) - (stereo != 0 && N == 2 ? Rate.QTHETA_OFFSET_TWOPHASE : Rate.QTHETA_OFFSET);
        qn = compute_qn(N, b, offset, pulse_cap, stereo);
        if (stereo != 0 && i >= intensity)
        {
            qn = 1;
        }
        tell = (int)ctx.ec.ec_tell_frac();
        if (qn != 1)
        {
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
                    int fs;
                    fs = (int)ctx.ec.ec_decode((uint)ft);
                    if (fs < (x0 + 1) * p0)
                    {
                        x = fs / p0;
                    }
                    else
                    {
                        x = x0 + 1 + (fs - ((x0 + 1) * p0));
                    }
                    ctx.ec.ec_dec_update((uint)(x <= x0 ? p0 * x : (x - 1 - x0) + ((x0 + 1) * p0)), (uint)(x <= x0 ? p0 * (x + 1) : (x - x0) + ((x0 + 1) * p0)), (uint)ft);
                    itheta = x;
                }
            }
            else if (B0 > 1 || stereo != 0)
            {
                /* Uniform pdf */
                itheta = (int)ctx.ec.ec_dec_uint((uint)(qn + 1));
            }
            else
            {
                int fs = 1, ft;
                ft = ((qn >> 1) + 1) * ((qn >> 1) + 1);
                {
                    /* Triangular pdf */
                    int fl = 0;
                    int fm;
                    fm = (int)ctx.ec.ec_decode((uint)ft);

                    if (fm < (((qn >> 1) * ((qn >> 1) + 1)) >> 1))
                    {
                        itheta = (int)((isqrt32((8 * (uint)fm) + 1) - 1) >> 1);
                        fs = itheta + 1;
                        fl = (itheta * (itheta + 1)) >> 1;
                    }
                    else
                    {
                        itheta = (int)(((uint)(2 * (qn + 1))
                         - isqrt32((8 * (uint)(ft - fm - 1)) + 1)) >> 1);
                        fs = qn + 1 - itheta;
                        fl = ft - (((qn + 1 - itheta) * (qn + 2 - itheta)) >> 1);
                    }

                    ctx.ec.ec_dec_update((uint)fl, (uint)(fl + fs), (uint)ft);
                }
            }
            itheta = (int)celt_udiv((uint)(itheta * 16384), (uint)qn);
            /* NOTE: Renormalising X and Y *may* help fixed-point a bit at very high rate.
                     Let's do that at higher complexity */
        }
        else if (stereo != 0)
        {
            if (b > 2 << BITRES && ctx.remaining_bits > 2 << BITRES)
            {
                inv = ctx.ec.ec_dec_bit_logp(2);
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
        }
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
            imid = bitexact_cos((short)itheta);
            iside = bitexact_cos((short)(16384 - itheta));
            /* This is the mid vs side allocation that minimizes squared error
               in that band. */
            delta = FRAC_MUL16((N - 1) << 7, bitexact_log2tan(iside, imid));
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
                sign = (int)ctx.ec.ec_dec_bits(1);
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
        /* C's cache pointer, m->cache.bits + m->cache.index[...], is computed only where it's
           valid (LM != -1): cache[k] is m.cache.bits[cache+k]. */
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
                cm = Vq.alg_unquant(X, N, K, spread, B, ref ctx.ec, gain);
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
                                ctx.seed = celt_lcg_rand(ctx.seed);
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
                                ctx.seed = celt_lcg_rand(ctx.seed);
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

    /* quant_partition()'s split test: cache[cache[0]]+12, with C's
       cache = m->cache.bits + m->cache.index[(LM+1)*m->nbEBands+i]. */
    private static int split_threshold(OpusCustomMode m, int i, int LM)
    {
        int cache = m.cache.index[((LM + 1) * m.nbEBands) + i];
        return m.cache.bits[cache + m.cache.bits[cache]] + 12;
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
            if (!lowband.IsEmpty)
            {
                haar1(lowband, N >> k, 1 << k);
            }
            fill = bit_interleave_table[fill & 0xF] | (bit_interleave_table[fill >> 4] << 2);
        }
        B >>= recombine;
        N_B <<= recombine;

        /* Increasing the time resolution */
        while ((N_B & 1) == 0 && tf_change < 0)
        {
            if (!lowband.IsEmpty)
            {
                haar1(lowband, N_B, B);
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
                haar1(X, N_B, B);
            }

            for (k = 0; k < recombine; k++)
            {
                cm = bit_deinterleave_table[cm];
                haar1(X, N0 >> k, 1 << k);
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
                sign = (int)ctx.ec.ec_dec_bits(1);
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

    /* The decoder's quant_all_bands(): C's encode (0), bandE, complexity and arch parameters are
       gone. Y_ empty stands for C's NULL (mono). */
    internal static void quant_all_bands(OpusCustomMode m, int start, int end,
          scoped Span<int> X_, scoped Span<int> Y_, scoped Span<byte> collapse_masks,
          scoped ReadOnlySpan<int> pulses, int shortBlocks, int spread,
          int dual_stereo, int intensity, scoped ReadOnlySpan<int> tf_res, int total_bits,
          int balance, ref ec_dec ec, int LM, int codedBands,
          ref uint seed, int disable_inv)
    {
        int i;
        int remaining_bits;
        short[] eBands = m.eBands;
        scoped Span<int> norm, norm2;
        scoped Span<int> lowband_scratch;
        int B;
        int M;
        int lowband_offset;
        int update_lowband = 1;
        int C = !Y_.IsEmpty ? 2 : 1;
        int norm_offset;
        int theta_rdo = 0;
        int resynth = 1;
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
        lowband_scratch = X_.Slice(M * eBands[m.effEBands - 1]);

        lowband_offset = 0;
        ctx.ec = ec;
        ctx.intensity = intensity;
        ctx.m = m;
        ctx.seed = seed;
        ctx.spread = spread;
        ctx.disable_inv = disable_inv;
        ctx.resynth = resynth;

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
            if (lowband_offset != 0 && (spread != SPREAD_AGGRESSIVE || B > 1 || tf_change < 0))
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
                    x_cm = quant_band_stereo(ref ctx, X, Y, N, b, B,
                          effective_lowband != -1 ? norm.Slice(effective_lowband) : default, LM,
                          last != 0 ? default : norm.Slice((M * eBands[i]) - norm_offset), lowband_scratch, (int)(x_cm | y_cm));
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
        }
        seed = ctx.seed;
        ec = ctx.ec;
    }
}
