/*Copyright (c) 2003-2004, Mark Borgerding
  Lots of modifications by Jean-Marc Valin
  Copyright (c) 2005-2007, Xiph.Org Foundation
  Copyright (c) 2008,      Xiph.Org Foundation, CSIRO
  Ported to C# from libopus 1.6.1 (celt/kiss_fft.c, celt/kiss_fft.h, celt/_kiss_fft_guts.h) by Hot Chai Games LLC

  All rights reserved.

  Redistribution and use in source and binary forms, with or without
   modification, are permitted provided that the following conditions are met:

    * Redistributions of source code must retain the above copyright notice,
       this list of conditions and the following disclaimer.
    * Redistributions in binary form must reproduce the above copyright notice,
       this list of conditions and the following disclaimer in the
       documentation and/or other materials provided with the distribution.

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
  POSSIBILITY OF SUCH DAMAGE.*/

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using static DotGame.AudioCodecs.Opus.Celt.Arch;

namespace DotGame.AudioCodecs.Opus.Celt;

/* kiss_fft.h: kiss_fft_scalar is opus_int32 and kiss_twiddle_scalar is celt_coef (opus_val16). */
internal struct kiss_fft_cpx
{
    internal int r;
    internal int i;
}

internal struct kiss_twiddle_cpx
{
    internal short r;
    internal short i;
}

[SkipLocalsInit]
internal sealed class kiss_fft_state
{
    internal int nfft;
    internal short scale;
    internal int scale_shift;
    internal int shift;
    internal short[] factors;
    internal short[] bitrev;
    internal kiss_twiddle_cpx[] twiddles;
    // Each stage's twiddles for the vector butterflies (null for the stages they don't do), made
    // the first time the state is used: for each pair of butterflies j, j + 1 and each multiple k
    // of the twiddle stride, (w.r, w.r, w'.r, w'.r) then (w.i, w.i, w'.i, w'.i)
    internal int[]?[]? simd_twiddles;

    internal kiss_fft_state(int nfft, short scale, int scale_shift, int shift, short[] factors, short[] bitrev,
        kiss_twiddle_cpx[] twiddles)
    {
        this.nfft = nfft;
        this.scale = scale;
        this.scale_shift = scale_shift;
        this.shift = shift;
        this.factors = factors;
        this.bitrev = bitrev;
        this.twiddles = twiddles;
    }
}

/// <summary>
/// celt/kiss_fft.c (the fixed-point FFT the inverse MDCT uses) with the complex macros of
/// celt/_kiss_fft_guts.h. Only the static modes' radices (2 after 4, 3, 4, 5) are needed.
/// </summary>
[SkipLocalsInit]
internal static class KissFft
{
    internal const int MAXFACTORS = 8;

    /* _kiss_fft_guts.h, FIXED_POINT without QEXT. */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int S_MUL(int a, int b) => MULT16_32_Q15(b, a);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void C_MUL(ref kiss_fft_cpx m, in kiss_fft_cpx a, in kiss_twiddle_cpx b)
    {
        m.r = SUB32_ovflw(S_MUL(a.r, b.r), S_MUL(a.i, b.i));
        m.i = ADD32_ovflw(S_MUL(a.r, b.i), S_MUL(a.i, b.r));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void C_MULBYSCALAR(ref kiss_fft_cpx c, int s)
    {
        c.r = S_MUL(c.r, s);
        c.i = S_MUL(c.i, s);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void C_ADD(ref kiss_fft_cpx res, in kiss_fft_cpx a, in kiss_fft_cpx b)
    {
        res.r = ADD32_ovflw(a.r, b.r);
        res.i = ADD32_ovflw(a.i, b.i);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void C_SUB(ref kiss_fft_cpx res, in kiss_fft_cpx a, in kiss_fft_cpx b)
    {
        res.r = SUB32_ovflw(a.r, b.r);
        res.i = SUB32_ovflw(a.i, b.i);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void C_ADDTO(ref kiss_fft_cpx res, in kiss_fft_cpx a)
    {
        res.r = ADD32_ovflw(res.r, a.r);
        res.i = ADD32_ovflw(res.i, a.i);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int HALF_OF(int x) => x >> 1;

    private static void kf_bfly2(Span<kiss_fft_cpx> Fout, int m, int N)
    {
        int Fout2;
        int i;
        int f = 0;
        {
            short tw;
            tw = 23170; /* QCONST32(0.7071067812f, COEF_SHIFT-1) */
            /* We know that m==4 here because the radix-2 is just after a radix-4 */
            for (i = 0; i < N; i++)
            {
                kiss_fft_cpx t = default;
                Fout2 = f + 4;
                t = Fout[Fout2 + 0];
                C_SUB(ref Fout[Fout2 + 0], Fout[f + 0], t);
                C_ADDTO(ref Fout[f + 0], t);

                t.r = S_MUL(ADD32_ovflw(Fout[Fout2 + 1].r, Fout[Fout2 + 1].i), tw);
                t.i = S_MUL(SUB32_ovflw(Fout[Fout2 + 1].i, Fout[Fout2 + 1].r), tw);
                C_SUB(ref Fout[Fout2 + 1], Fout[f + 1], t);
                C_ADDTO(ref Fout[f + 1], t);

                t.r = Fout[Fout2 + 2].i;
                t.i = NEG32_ovflw(Fout[Fout2 + 2].r);
                C_SUB(ref Fout[Fout2 + 2], Fout[f + 2], t);
                C_ADDTO(ref Fout[f + 2], t);

                t.r = S_MUL(SUB32_ovflw(Fout[Fout2 + 3].i, Fout[Fout2 + 3].r), tw);
                t.i = S_MUL(NEG32_ovflw(ADD32_ovflw(Fout[Fout2 + 3].i, Fout[Fout2 + 3].r)), tw);
                C_SUB(ref Fout[Fout2 + 3], Fout[f + 3], t);
                C_ADDTO(ref Fout[f + 3], t);
                f += 8;
            }
        }
    }

    private static void kf_bfly4(Span<kiss_fft_cpx> Fout, int fstride, kiss_fft_state st, int m, int N, int mm, int[]? simd)
    {
        if (simd != null && m != 1)
        {
            kf_bfly4_simd(Fout, m, N, mm, simd);
            return;
        }
        int i;

        if (m == 1)
        {
            /* Degenerate case where all the twiddles are 1. */
            int f = 0;
            i = 0;
            if (Vector128.IsHardwareAccelerated)
            {
                Span<int> v = MemoryMarshal.Cast<kiss_fft_cpx, int>(Fout);
                Vector128<int> sign02 = Vector128.Create(1, 1, -1, -1);
                Vector128<int> sign13 = Vector128.Create(1, -1, -1, 1);
                for (; i < N; i++)
                {
                    /* a = (F0, F1), b = (F2, F3): c = (F0+F2, F1+F3), d = (F0-F2, F1-F3) */
                    Vector128<int> a = Vector128.Create((ReadOnlySpan<int>)v.Slice(8 * i, 4));
                    Vector128<int> b = Vector128.Create((ReadOnlySpan<int>)v.Slice((8 * i) + 4, 4));
                    Vector128<int> c = a + b;
                    Vector128<int> d = a - b;
                    /* F0 = c.lo + c.hi, F2 = c.lo - c.hi */
                    Vector128<int> f02 = Vector128.Shuffle(c, Vector128.Create(0, 1, 0, 1)) + (Vector128.Shuffle(c, Vector128.Create(2, 3, 2, 3)) * sign02);
                    /* F1 = (s0.r + s1.i, s0.i - s1.r), F3 = (s0.r - s1.i, s0.i + s1.r), s0 = d.lo, s1 = d.hi */
                    Vector128<int> f13 = Vector128.Shuffle(d, Vector128.Create(0, 1, 0, 1)) + (Vector128.Shuffle(d, Vector128.Create(3, 2, 3, 2)) * sign13);
                    Vector128.Create(f02.GetLower(), f13.GetLower()).CopyTo(v.Slice(8 * i, 4));
                    Vector128.Create(f02.GetUpper(), f13.GetUpper()).CopyTo(v.Slice((8 * i) + 4, 4));
                }
                f = 4 * i;
            }
            for (; i < N; i++)
            {
                kiss_fft_cpx scratch0 = default, scratch1 = default;

                C_SUB(ref scratch0, Fout[f], Fout[f + 2]);
                C_ADDTO(ref Fout[f], Fout[f + 2]);
                C_ADD(ref scratch1, Fout[f + 1], Fout[f + 3]);
                C_SUB(ref Fout[f + 2], Fout[f], scratch1);
                C_ADDTO(ref Fout[f], scratch1);
                C_SUB(ref scratch1, Fout[f + 1], Fout[f + 3]);

                Fout[f + 1].r = ADD32_ovflw(scratch0.r, scratch1.i);
                Fout[f + 1].i = SUB32_ovflw(scratch0.i, scratch1.r);
                Fout[f + 3].r = SUB32_ovflw(scratch0.r, scratch1.i);
                Fout[f + 3].i = ADD32_ovflw(scratch0.i, scratch1.r);
                f += 4;
            }
        }
        else
        {
            int j;
            int m2 = 2 * m;
            int m3 = 3 * m;
            ReadOnlySpan<kiss_twiddle_cpx> twiddles = st.twiddles;
            for (i = 0; i < N; i++)
            {
                Span<kiss_fft_cpx> F = Fout.Slice(i * mm);
                int tw1 = 0, tw2 = 0, tw3 = 0;
                /* m is guaranteed to be a multiple of 4. */
                for (j = 0; j < m; j++)
                {
                    ref kiss_fft_cpx f0 = ref F[j];
                    ref kiss_fft_cpx f1 = ref F[j + m];
                    ref kiss_fft_cpx f2 = ref F[j + m2];
                    ref kiss_fft_cpx f3 = ref F[j + m3];
                    kiss_twiddle_cpx w1 = twiddles[tw1], w2 = twiddles[tw2], w3 = twiddles[tw3];
                    int s0r = SUB32_ovflw(S_MUL(f1.r, w1.r), S_MUL(f1.i, w1.i));
                    int s0i = ADD32_ovflw(S_MUL(f1.r, w1.i), S_MUL(f1.i, w1.r));
                    int s1r = SUB32_ovflw(S_MUL(f2.r, w2.r), S_MUL(f2.i, w2.i));
                    int s1i = ADD32_ovflw(S_MUL(f2.r, w2.i), S_MUL(f2.i, w2.r));
                    int s2r = SUB32_ovflw(S_MUL(f3.r, w3.r), S_MUL(f3.i, w3.i));
                    int s2i = ADD32_ovflw(S_MUL(f3.r, w3.i), S_MUL(f3.i, w3.r));

                    int f0r = f0.r, f0i = f0.i;
                    int s5r = SUB32_ovflw(f0r, s1r), s5i = SUB32_ovflw(f0i, s1i);
                    f0r = ADD32_ovflw(f0r, s1r);
                    f0i = ADD32_ovflw(f0i, s1i);
                    int s3r = ADD32_ovflw(s0r, s2r), s3i = ADD32_ovflw(s0i, s2i);
                    int s4r = SUB32_ovflw(s0r, s2r), s4i = SUB32_ovflw(s0i, s2i);
                    f2.r = SUB32_ovflw(f0r, s3r);
                    f2.i = SUB32_ovflw(f0i, s3i);
                    tw1 += fstride;
                    tw2 += fstride * 2;
                    tw3 += fstride * 3;
                    f0.r = ADD32_ovflw(f0r, s3r);
                    f0.i = ADD32_ovflw(f0i, s3i);

                    f1.r = ADD32_ovflw(s5r, s4i);
                    f1.i = SUB32_ovflw(s5i, s4r);
                    f3.r = SUB32_ovflw(s5r, s4i);
                    f3.i = ADD32_ovflw(s5i, s4r);
                }
            }
        }
    }

    private static void kf_bfly3(Span<kiss_fft_cpx> Fout, int fstride, kiss_fft_state st, int m, int N, int mm, int[]? simd)
    {
        if (simd != null)
        {
            kf_bfly3_simd(Fout, m, N, mm, simd);
            return;
        }
        int i;
        int j;
        int m2 = 2 * m;
        ReadOnlySpan<kiss_twiddle_cpx> twiddles = st.twiddles;
        /* epi3.r = -16384 is unused */
        const int epi3i = -28378; /* -QCONST32(0.86602540f, COEF_SHIFT-1) */
        for (i = 0; i < N; i++)
        {
            Span<kiss_fft_cpx> F = Fout.Slice(i * mm);
            int tw1 = 0, tw2 = 0;
            /* For non-custom modes, m is guaranteed to be a multiple of 4. */
            for (j = 0; j < m; j++)
            {
                ref kiss_fft_cpx f0 = ref F[j];
                ref kiss_fft_cpx f1 = ref F[j + m];
                ref kiss_fft_cpx f2 = ref F[j + m2];
                kiss_twiddle_cpx w1 = twiddles[tw1], w2 = twiddles[tw2];
                int s1r = SUB32_ovflw(S_MUL(f1.r, w1.r), S_MUL(f1.i, w1.i));
                int s1i = ADD32_ovflw(S_MUL(f1.r, w1.i), S_MUL(f1.i, w1.r));
                int s2r = SUB32_ovflw(S_MUL(f2.r, w2.r), S_MUL(f2.i, w2.i));
                int s2i = ADD32_ovflw(S_MUL(f2.r, w2.i), S_MUL(f2.i, w2.r));

                int s3r = ADD32_ovflw(s1r, s2r), s3i = ADD32_ovflw(s1i, s2i);
                int s0r = SUB32_ovflw(s1r, s2r), s0i = SUB32_ovflw(s1i, s2i);
                tw1 += fstride;
                tw2 += fstride * 2;

                int f0r = f0.r, f0i = f0.i;
                int f1r = SUB32_ovflw(f0r, HALF_OF(s3r));
                int f1i = SUB32_ovflw(f0i, HALF_OF(s3i));

                s0r = S_MUL(s0r, epi3i);
                s0i = S_MUL(s0i, epi3i);

                f0.r = ADD32_ovflw(f0r, s3r);
                f0.i = ADD32_ovflw(f0i, s3i);

                f2.r = ADD32_ovflw(f1r, s0i);
                f2.i = SUB32_ovflw(f1i, s0r);

                f1.r = SUB32_ovflw(f1r, s0i);
                f1.i = ADD32_ovflw(f1i, s0r);
            }
        }
    }

    private static void kf_bfly5(Span<kiss_fft_cpx> Fout, int fstride, kiss_fft_state st, int m, int N, int mm, int[]? simd)
    {
        if (simd != null)
        {
            kf_bfly5_simd(Fout, m, N, mm, simd);
            return;
        }
        int i, u;
        ReadOnlySpan<kiss_twiddle_cpx> tw = st.twiddles;
        const int yar = 10126;  /* QCONST32(0.30901699f, COEF_SHIFT-1) */
        const int yai = -31164; /* -QCONST32(0.95105652f, COEF_SHIFT-1) */
        const int ybr = -26510; /* -QCONST32(0.80901699f, COEF_SHIFT-1) */
        const int ybi = -19261; /* -QCONST32(0.58778525f, COEF_SHIFT-1) */

        for (i = 0; i < N; i++)
        {
            Span<kiss_fft_cpx> F = Fout.Slice(i * mm);
            /* For non-custom modes, m is guaranteed to be a multiple of 4. */
            for (u = 0; u < m; ++u)
            {
                ref kiss_fft_cpx f0 = ref F[u];
                ref kiss_fft_cpx f1 = ref F[u + m];
                ref kiss_fft_cpx f2 = ref F[u + (2 * m)];
                ref kiss_fft_cpx f3 = ref F[u + (3 * m)];
                ref kiss_fft_cpx f4 = ref F[u + (4 * m)];
                int s0r = f0.r, s0i = f0.i;
                kiss_twiddle_cpx w1 = tw[u * fstride], w2 = tw[2 * u * fstride], w3 = tw[3 * u * fstride], w4 = tw[4 * u * fstride];

                int s1r = SUB32_ovflw(S_MUL(f1.r, w1.r), S_MUL(f1.i, w1.i));
                int s1i = ADD32_ovflw(S_MUL(f1.r, w1.i), S_MUL(f1.i, w1.r));
                int s2r = SUB32_ovflw(S_MUL(f2.r, w2.r), S_MUL(f2.i, w2.i));
                int s2i = ADD32_ovflw(S_MUL(f2.r, w2.i), S_MUL(f2.i, w2.r));
                int s3r = SUB32_ovflw(S_MUL(f3.r, w3.r), S_MUL(f3.i, w3.i));
                int s3i = ADD32_ovflw(S_MUL(f3.r, w3.i), S_MUL(f3.i, w3.r));
                int s4r = SUB32_ovflw(S_MUL(f4.r, w4.r), S_MUL(f4.i, w4.i));
                int s4i = ADD32_ovflw(S_MUL(f4.r, w4.i), S_MUL(f4.i, w4.r));

                int s7r = ADD32_ovflw(s1r, s4r), s7i = ADD32_ovflw(s1i, s4i);
                int s10r = SUB32_ovflw(s1r, s4r), s10i = SUB32_ovflw(s1i, s4i);
                int s8r = ADD32_ovflw(s2r, s3r), s8i = ADD32_ovflw(s2i, s3i);
                int s9r = SUB32_ovflw(s2r, s3r), s9i = SUB32_ovflw(s2i, s3i);

                f0.r = ADD32_ovflw(s0r, ADD32_ovflw(s7r, s8r));
                f0.i = ADD32_ovflw(s0i, ADD32_ovflw(s7i, s8i));

                int s5r = ADD32_ovflw(s0r, ADD32_ovflw(S_MUL(s7r, yar), S_MUL(s8r, ybr)));
                int s5i = ADD32_ovflw(s0i, ADD32_ovflw(S_MUL(s7i, yar), S_MUL(s8i, ybr)));

                int s6r = ADD32_ovflw(S_MUL(s10i, yai), S_MUL(s9i, ybi));
                int s6i = NEG32_ovflw(ADD32_ovflw(S_MUL(s10r, yai), S_MUL(s9r, ybi)));

                f1.r = SUB32_ovflw(s5r, s6r);
                f1.i = SUB32_ovflw(s5i, s6i);
                f4.r = ADD32_ovflw(s5r, s6r);
                f4.i = ADD32_ovflw(s5i, s6i);

                int s11r = ADD32_ovflw(s0r, ADD32_ovflw(S_MUL(s7r, ybr), S_MUL(s8r, yar)));
                int s11i = ADD32_ovflw(s0i, ADD32_ovflw(S_MUL(s7i, ybr), S_MUL(s8i, yar)));
                int s12r = SUB32_ovflw(S_MUL(s9i, yai), S_MUL(s10i, ybi));
                int s12i = SUB32_ovflw(S_MUL(s10r, ybi), S_MUL(s9r, yai));

                f2.r = ADD32_ovflw(s11r, s12r);
                f2.i = ADD32_ovflw(s11i, s12i);
                f3.r = SUB32_ovflw(s11r, s12r);
                f3.i = SUB32_ovflw(s11i, s12i);
            }
        }
    }

    // The stages' vector twiddles: the stages the vector butterflies do (radix 3, 4 or 5, with
    // an even number of butterflies per group) get a table, as opus_fft_impl walks them
    private static int[]?[] simd_twiddles(kiss_fft_state st)
    {
        var tables = new int[]?[MAXFACTORS];
        if (!Vector128.IsHardwareAccelerated)
        {
            return tables;
        }
        int shift = st.shift > 0 ? st.shift : 0;
        Span<int> fstride = stackalloc int[MAXFACTORS + 1];
        int L = 0;
        int m;
        fstride[0] = 1;
        do
        {
            int p = st.factors[2 * L];
            m = st.factors[(2 * L) + 1];
            fstride[L + 1] = fstride[L] * p;
            L++;
        }
        while (m != 1);
        for (int i = 0; i < L; i++)
        {
            int p = st.factors[2 * i];
            int stageM = st.factors[(2 * i) + 1];
            if ((p != 3 && p != 4 && p != 5) || stageM < 2 || (stageM & 1) != 0)
            {
                continue;
            }
            int ts = fstride[i] << shift;
            var table = new int[(stageM / 2) * (p - 1) * 8];
            for (int j = 0; j < stageM; j += 2)
            {
                for (int k = 1; k < p; k++)
                {
                    kiss_twiddle_cpx w0 = st.twiddles[k * j * ts];
                    kiss_twiddle_cpx w1 = st.twiddles[k * (j + 1) * ts];
                    int b = (((j >> 1) * (p - 1)) + (k - 1)) * 8;
                    table[b] = w0.r;
                    table[b + 1] = w0.r;
                    table[b + 2] = w1.r;
                    table[b + 3] = w1.r;
                    table[b + 4] = w0.i;
                    table[b + 5] = w0.i;
                    table[b + 6] = w1.i;
                    table[b + 7] = w1.i;
                }
            }
            tables[i] = table;
        }
        return tables;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Load(ReadOnlySpan<int> x, int complex) => Vector128.Create(x.Slice(2 * complex, 4));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store(Vector128<int> v, Span<int> x, int complex) => v.CopyTo(x.Slice(2 * complex, 4));

    private static void kf_bfly4_simd(Span<kiss_fft_cpx> Fout, int m, int N, int mm, int[] simd)
    {
        Span<int> x = MemoryMarshal.Cast<kiss_fft_cpx, int>(Fout);
        ReadOnlySpan<int> tw = simd;
        int m2 = 2 * m;
        int m3 = 3 * m;
        for (int i = 0; i < N; i++)
        {
            int f = i * mm;
            for (int j = 0; j < m; j += 2)
            {
                ReadOnlySpan<int> w = tw.Slice((j >> 1) * 24, 24);
                Vector128<int> v0 = Load(x, f + j);
                Vector128<int> s0 = Simd.C_MUL(Load(x, f + j + m), Vector128.Create(w.Slice(0, 4)), Vector128.Create(w.Slice(4, 4)));
                Vector128<int> s1 = Simd.C_MUL(Load(x, f + j + m2), Vector128.Create(w.Slice(8, 4)), Vector128.Create(w.Slice(12, 4)));
                Vector128<int> s2 = Simd.C_MUL(Load(x, f + j + m3), Vector128.Create(w.Slice(16, 4)), Vector128.Create(w.Slice(20, 4)));
                Vector128<int> s5 = v0 - s1;
                v0 += s1;
                Vector128<int> s3 = s0 + s2;
                Vector128<int> s4 = s0 - s2;
                Store(v0 - s3, x, f + j + m2);
                Store(v0 + s3, x, f + j);
                Vector128<int> s4t = Simd.NegI(Simd.Swap(s4));
                Store(s5 + s4t, x, f + j + m);
                Store(s5 - s4t, x, f + j + m3);
            }
        }
    }

    private static void kf_bfly3_simd(Span<kiss_fft_cpx> Fout, int m, int N, int mm, int[] simd)
    {
        Span<int> x = MemoryMarshal.Cast<kiss_fft_cpx, int>(Fout);
        ReadOnlySpan<int> tw = simd;
        int m2 = 2 * m;
        Vector128<int> epi3i = Vector128.Create(-28378); /* -QCONST32(0.86602540f, COEF_SHIFT-1) */
        for (int i = 0; i < N; i++)
        {
            int f = i * mm;
            for (int j = 0; j < m; j += 2)
            {
                ReadOnlySpan<int> w = tw.Slice((j >> 1) * 16, 16);
                Vector128<int> v0 = Load(x, f + j);
                Vector128<int> s1 = Simd.C_MUL(Load(x, f + j + m), Vector128.Create(w.Slice(0, 4)), Vector128.Create(w.Slice(4, 4)));
                Vector128<int> s2 = Simd.C_MUL(Load(x, f + j + m2), Vector128.Create(w.Slice(8, 4)), Vector128.Create(w.Slice(12, 4)));
                Vector128<int> s3 = s1 + s2;
                Vector128<int> s0 = s1 - s2;
                Vector128<int> fm = v0 - (s3 >> 1);
                s0 = Simd.MULT16_32_Q15(epi3i, s0);
                Store(v0 + s3, x, f + j);
                Vector128<int> s0t = Simd.NegI(Simd.Swap(s0));
                Store(fm + s0t, x, f + j + m2);
                Store(fm - s0t, x, f + j + m);
            }
        }
    }

    private static void kf_bfly5_simd(Span<kiss_fft_cpx> Fout, int m, int N, int mm, int[] simd)
    {
        Span<int> x = MemoryMarshal.Cast<kiss_fft_cpx, int>(Fout);
        ReadOnlySpan<int> tw = simd;
        Vector128<int> yar = Vector128.Create(10126);  /* QCONST32(0.30901699f, COEF_SHIFT-1) */
        Vector128<int> yai = Vector128.Create(-31164); /* -QCONST32(0.95105652f, COEF_SHIFT-1) */
        Vector128<int> ybr = Vector128.Create(-26510); /* -QCONST32(0.80901699f, COEF_SHIFT-1) */
        Vector128<int> ybi = Vector128.Create(-19261); /* -QCONST32(0.58778525f, COEF_SHIFT-1) */
        for (int i = 0; i < N; i++)
        {
            int f = i * mm;
            for (int u = 0; u < m; u += 2)
            {
                ReadOnlySpan<int> w = tw.Slice((u >> 1) * 32, 32);
                Vector128<int> s0 = Load(x, f + u);
                Vector128<int> s1 = Simd.C_MUL(Load(x, f + u + m), Vector128.Create(w.Slice(0, 4)), Vector128.Create(w.Slice(4, 4)));
                Vector128<int> s2 = Simd.C_MUL(Load(x, f + u + (2 * m)), Vector128.Create(w.Slice(8, 4)), Vector128.Create(w.Slice(12, 4)));
                Vector128<int> s3 = Simd.C_MUL(Load(x, f + u + (3 * m)), Vector128.Create(w.Slice(16, 4)), Vector128.Create(w.Slice(20, 4)));
                Vector128<int> s4 = Simd.C_MUL(Load(x, f + u + (4 * m)), Vector128.Create(w.Slice(24, 4)), Vector128.Create(w.Slice(28, 4)));

                Vector128<int> s7 = s1 + s4;
                Vector128<int> s10 = s1 - s4;
                Vector128<int> s8 = s2 + s3;
                Vector128<int> s9 = s2 - s3;

                Store(s0 + (s7 + s8), x, f + u);

                Vector128<int> s5 = s0 + (Simd.MULT16_32_Q15(yar, s7) + Simd.MULT16_32_Q15(ybr, s8));
                Vector128<int> s6 = Simd.NegI(Simd.Swap(Simd.MULT16_32_Q15(yai, s10) + Simd.MULT16_32_Q15(ybi, s9)));
                Store(s5 - s6, x, f + u + m);
                Store(s5 + s6, x, f + u + (4 * m));

                Vector128<int> s11 = s0 + (Simd.MULT16_32_Q15(ybr, s7) + Simd.MULT16_32_Q15(yar, s8));
                Vector128<int> s12 = Simd.NegR(Simd.Swap(Simd.MULT16_32_Q15(ybi, s10) - Simd.MULT16_32_Q15(yai, s9)));
                Store(s11 + s12, x, f + u + (2 * m));
                Store(s11 - s12, x, f + u + (3 * m));
            }
        }
    }

    private static void fft_downshift(Span<kiss_fft_cpx> x, int N, ref int total, int step)
    {
        int shift;
        shift = IMIN(step, total);
        total -= shift;
        if (shift > 0 && Vector128.IsHardwareAccelerated)
        {
            /* SHR32 by 1, or PSHR32 by more, on every r and i */
            Span<int> v = MemoryMarshal.Cast<kiss_fft_cpx, int>(x.Slice(0, N));
            Vector128<int> round = Vector128.Create(shift == 1 ? 0 : (1 << shift) >> 1);
            int j = 0;
            for (; j + 4 <= v.Length; j += 4)
            {
                ((Vector128.Create((ReadOnlySpan<int>)v.Slice(j, 4)) + round) >> shift).CopyTo(v.Slice(j, 4));
            }
            for (; j < v.Length; j++)
            {
                v[j] = shift == 1 ? SHR32(v[j], 1) : PSHR32(v[j], shift);
            }
        }
        else if (shift == 1)
        {
            int i;
            for (i = 0; i < N; i++)
            {
                x[i].r = SHR32(x[i].r, 1);
                x[i].i = SHR32(x[i].i, 1);
            }
        }
        else if (shift > 0)
        {
            int i;
            for (i = 0; i < N; i++)
            {
                x[i].r = PSHR32(x[i].r, shift);
                x[i].i = PSHR32(x[i].i, shift);
            }
        }
    }

    internal static void opus_fft_impl(kiss_fft_state st, Span<kiss_fft_cpx> fout, int downshift)
    {
        int m2, m;
        int p;
        int L;
        Span<int> fstride = stackalloc int[MAXFACTORS];
        int i;
        int shift;

        /* st->shift can be -1 */
        shift = st.shift > 0 ? st.shift : 0;

        fstride[0] = 1;
        L = 0;
        do
        {
            p = st.factors[2 * L];
            m = st.factors[(2 * L) + 1];
            fstride[L + 1] = fstride[L] * p;
            L++;
        }
        while (m != 1);
        m = st.factors[(2 * L) - 1];
        int[]?[] simd = st.simd_twiddles ??= simd_twiddles(st);
        for (i = L - 1; i >= 0; i--)
        {
            if (i != 0)
            {
                m2 = st.factors[(2 * i) - 1];
            }
            else
            {
                m2 = 1;
            }
            switch (st.factors[2 * i])
            {
                case 2:
                    fft_downshift(fout, st.nfft, ref downshift, 1);
                    kf_bfly2(fout, m, fstride[i]);
                    break;
                case 4:
                    fft_downshift(fout, st.nfft, ref downshift, 2);
                    kf_bfly4(fout, fstride[i] << shift, st, m, fstride[i], m2, simd[i]);
                    break;
                case 3:
                    fft_downshift(fout, st.nfft, ref downshift, 2);
                    kf_bfly3(fout, fstride[i] << shift, st, m, fstride[i], m2, simd[i]);
                    break;
                case 5:
                    fft_downshift(fout, st.nfft, ref downshift, 3);
                    kf_bfly5(fout, fstride[i] << shift, st, m, fstride[i], m2, simd[i]);
                    break;
            }
            m = m2;
        }
        fft_downshift(fout, st.nfft, ref downshift, downshift);
    }
}
