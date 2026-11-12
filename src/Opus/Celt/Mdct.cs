/* Copyright (c) 2007-2008 CSIRO
   Copyright (c) 2007-2008 Xiph.Org Foundation
   Written by Jean-Marc Valin
   Ported to C# from libopus 1.6.1 (celt/mdct.c, celt/mdct.h) by Hot Chai Games LLC */
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
using static DotGame.AudioCodecs.Opus.Celt.Arch;
using static DotGame.AudioCodecs.Opus.Celt.MathOps;

namespace DotGame.AudioCodecs.Opus.Celt;

/* mdct.h */
internal sealed class mdct_lookup
{
    internal int n;
    internal int maxshift;
    internal kiss_fft_state[] kfft;
    internal short[] trig;

    internal mdct_lookup(int n, int maxshift, kiss_fft_state[] kfft, short[] trig)
    {
        this.n = n;
        this.maxshift = maxshift;
        this.kfft = kfft;
        this.trig = trig;
    }
}

/// <summary>
/// celt/mdct.c: the inverse MDCT with weighted overlap-add (the decoder's only transform).
/// </summary>
internal static class Mdct
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int S_MUL(int a, int b) => MULT16_32_Q15(b, a);

    /* Compute a backward MDCT (no scaling) and performs weighted overlap-add
        (scales implicitly by 1/2) */
    /* Vectorised (four lanes, each the scalar's operations) where the elements are contiguous: the
       scan of the input, the pre-rotation's arithmetic (its bit-reversed stores stay scalar), the
       post-rotation and the TDAC mirror. Long blocks only for the first two (stride 1). */
    internal static void clt_mdct_backward(mdct_lookup l, ReadOnlySpan<int> @in, Span<int> @out,
        ReadOnlySpan<short> window, int overlap, int shift, int stride)
    {
        int i;
        int N, N2, N4;
        ReadOnlySpan<short> trig;
        int pre_shift, post_shift, fft_shift;
        bool simd = Vector128.IsHardwareAccelerated;
        N = l.n;
        trig = l.trig;
        for (i = 0; i < shift; i++)
        {
            N >>= 1;
            trig = trig.Slice(N);
        }
        N2 = N >> 1;
        N4 = N >> 2;
        {
            int sumval = N2;
            int maxval = 0;
            i = 0;
            if (simd && stride == 1)
            {
                Vector128<int> vmax = Vector128<int>.Zero;
                Vector128<int> vsum = Vector128<int>.Zero;
                for (; i + 4 <= N2; i += 4)
                {
                    Vector128<int> x = Vector128.Create((ReadOnlySpan<int>)@in.Slice(i, 4));
                    vmax = Vector128.Max(vmax, Vector128.Abs(x));
                    vsum += Vector128.Abs((x >> 11));
                }
                maxval = Math.Max(Math.Max(vmax.GetElement(0), vmax.GetElement(1)), Math.Max(vmax.GetElement(2), vmax.GetElement(3)));
                sumval = ADD32_ovflw(sumval, Vector128.Sum(vsum));
            }
            for (; i < N2; i++)
            {
                maxval = MAX32(maxval, ABS32(@in[i * stride]));
                sumval = ADD32_ovflw(sumval, ABS32(SHR32(@in[i * stride], 11)));
            }
            pre_shift = IMAX(0, 29 - celt_zlog2(1 + maxval));
            /* Worst-case where all the energy goes to a single sample. */
            post_shift = IMAX(0, 19 - celt_ilog2(ABS32(sumval)));
            post_shift = IMIN(post_shift, pre_shift);
            fft_shift = pre_shift - post_shift;
        }
        /* Pre-rotate */
        {
            /* Temp pointers to make it really clear to the compiler what we're doing */
            int xp1 = 0;
            int xp2 = stride * (N2 - 1);
            Span<int> yp = @out.Slice(overlap >> 1);
            ReadOnlySpan<short> t = trig;
            ReadOnlySpan<short> bitrev = l.kfft[shift].bitrev;
            i = 0;
            if (simd && stride == 1)
            {
                for (; i + 4 <= N4; i += 4)
                {
                    /* x1 is in[2i], in[2i+2], ...; x2 is in[N2-1-2i], in[N2-3-2i], ... */
                    Simd.Unzip(Vector128.Create((ReadOnlySpan<int>)@in.Slice(2 * i, 4)), Vector128.Create((ReadOnlySpan<int>)@in.Slice((2 * i) + 4, 4)), out Vector128<int> x1, out _);
                    int b = N2 - 8 - (2 * i);
                    Simd.Unzip(Vector128.Create((ReadOnlySpan<int>)@in.Slice(b, 4)), Vector128.Create((ReadOnlySpan<int>)@in.Slice(b + 4, 4)), out _, out Vector128<int> x2);
                    x1 = (x1 << pre_shift);
                    x2 = (Simd.Reverse(x2) << pre_shift);
                    Vector128<int> ta = Simd.LoadWiden(t.Slice(i));
                    Vector128<int> tb = Simd.LoadWiden(t.Slice(N4 + i));
                    Vector128<int> vyr = Simd.MULT16_32_Q15(ta, x2) + Simd.MULT16_32_Q15(tb, x1);
                    Vector128<int> vyi = Simd.MULT16_32_Q15(ta, x1) - Simd.MULT16_32_Q15(tb, x2);
                    /* We swap real and imag because we use an FFT instead of an IFFT.
                       Storing the pre-rotation directly in the bitrev order. */
                    int r0 = 2 * bitrev[i], r1 = 2 * bitrev[i + 1], r2 = 2 * bitrev[i + 2], r3 = 2 * bitrev[i + 3];
                    yp[r0 + 1] = vyr.GetElement(0);
                    yp[r0] = vyi.GetElement(0);
                    yp[r1 + 1] = vyr.GetElement(1);
                    yp[r1] = vyi.GetElement(1);
                    yp[r2 + 1] = vyr.GetElement(2);
                    yp[r2] = vyi.GetElement(2);
                    yp[r3 + 1] = vyr.GetElement(3);
                    yp[r3] = vyi.GetElement(3);
                }
                xp1 = 2 * i;
                xp2 = N2 - 1 - (2 * i);
            }
            for (; i < N4; i++)
            {
                int rev;
                int yr, yi;
                int x1, x2;
                rev = bitrev[i];
                x1 = SHL32_ovflw(@in[xp1], pre_shift);
                x2 = SHL32_ovflw(@in[xp2], pre_shift);
                yr = ADD32_ovflw(S_MUL(x2, t[i]), S_MUL(x1, t[N4 + i]));
                yi = SUB32_ovflw(S_MUL(x1, t[i]), S_MUL(x2, t[N4 + i]));
                /* We swap real and imag because we use an FFT instead of an IFFT. */
                yp[(2 * rev) + 1] = yr;
                yp[2 * rev] = yi;
                /* Storing the pre-rotation directly in the bitrev order. */
                xp1 += 2 * stride;
                xp2 -= 2 * stride;
            }
        }

        KissFft.opus_fft_impl(l.kfft[shift], MemoryMarshal.Cast<int, kiss_fft_cpx>(@out.Slice(overlap >> 1)), fft_shift);

        /* Post-rotate and de-shuffle from both ends of the buffer at once to make
           it in-place. */
        {
            int yp0 = overlap >> 1;
            int yp1 = (overlap >> 1) + N2 - 2;
            ReadOnlySpan<short> t = trig;

            /* Loop to (N4+1)>>1 to handle odd N4. When N4 is odd, the
               middle pair will be computed twice. */
            i = 0;
            if (simd && (N4 & 1) == 0)
            {
                Vector128<int> round = Vector128.Create((1 << post_shift) >> 1);
                for (; i + 4 <= N4 >> 1; i += 4)
                {
                    /* The front four pairs from yp0 up, the back four from yp1 down: (im, re) each.
                       We swap real and imag because we're using an FFT instead of an IFFT. */
                    Span<int> front = @out.Slice(yp0, 8);
                    Span<int> back = @out.Slice(yp1 - 6, 8);
                    Simd.Unzip(Vector128.Create((ReadOnlySpan<int>)front.Slice(0, 4)), Vector128.Create((ReadOnlySpan<int>)front.Slice(4, 4)), out Vector128<int> imf, out Vector128<int> ref_);
                    Simd.Unzip(Vector128.Create((ReadOnlySpan<int>)back.Slice(0, 4)), Vector128.Create((ReadOnlySpan<int>)back.Slice(4, 4)), out Vector128<int> imb, out Vector128<int> reb);
                    imb = Simd.Reverse(imb);
                    reb = Simd.Reverse(reb);
                    Vector128<int> t0f = Simd.LoadWiden(t.Slice(i));
                    Vector128<int> t1f = Simd.LoadWiden(t.Slice(N4 + i));
                    Vector128<int> t0b = Simd.Reverse(Simd.LoadWiden(t.Slice(N4 - i - 4)));
                    Vector128<int> t1b = Simd.Reverse(Simd.LoadWiden(t.Slice(N2 - i - 4)));
                    /* We'd scale up by 2 here, but instead it's done when mixing the windows */
                    Vector128<int> yrf = ((Simd.MULT16_32_Q15(t0f, ref_) + Simd.MULT16_32_Q15(t1f, imf) + round) >> post_shift);
                    Vector128<int> yif = ((Simd.MULT16_32_Q15(t1f, ref_) - Simd.MULT16_32_Q15(t0f, imf) + round) >> post_shift);
                    Vector128<int> yrb = ((Simd.MULT16_32_Q15(t0b, reb) + Simd.MULT16_32_Q15(t1b, imb) + round) >> post_shift);
                    Vector128<int> yib = ((Simd.MULT16_32_Q15(t1b, reb) - Simd.MULT16_32_Q15(t0b, imb) + round) >> post_shift);
                    /* The front gets (yr, the back's yi) pairs; the back, from yp1 down, (yr, the front's yi) */
                    Simd.Zip(yrf, yib, out Vector128<int> f0, out Vector128<int> f1);
                    Simd.Zip(Simd.Reverse(yrb), Simd.Reverse(yif), out Vector128<int> b0, out Vector128<int> b1);
                    f0.CopyTo(front.Slice(0, 4));
                    f1.CopyTo(front.Slice(4, 4));
                    b0.CopyTo(back.Slice(0, 4));
                    b1.CopyTo(back.Slice(4, 4));
                    yp0 += 8;
                    yp1 -= 8;
                }
            }
            for (; i < (N4 + 1) >> 1; i++)
            {
                int re, im, yr, yi;
                short t0, t1;
                /* We swap real and imag because we're using an FFT instead of an IFFT. */
                re = @out[yp0 + 1];
                im = @out[yp0];
                t0 = t[i];
                t1 = t[N4 + i];
                /* We'd scale up by 2 here, but instead it's done when mixing the windows */
                yr = PSHR32_ovflw(ADD32_ovflw(S_MUL(re, t0), S_MUL(im, t1)), post_shift);
                yi = PSHR32_ovflw(SUB32_ovflw(S_MUL(re, t1), S_MUL(im, t0)), post_shift);
                /* We swap real and imag because we're using an FFT instead of an IFFT. */
                re = @out[yp1 + 1];
                im = @out[yp1];
                @out[yp0] = yr;
                @out[yp1 + 1] = yi;

                t0 = t[N4 - i - 1];
                t1 = t[N2 - i - 1];
                /* We'd scale up by 2 here, but instead it's done when mixing the windows */
                yr = PSHR32_ovflw(ADD32_ovflw(S_MUL(re, t0), S_MUL(im, t1)), post_shift);
                yi = PSHR32_ovflw(SUB32_ovflw(S_MUL(re, t1), S_MUL(im, t0)), post_shift);
                @out[yp1] = yr;
                @out[yp0 + 1] = yi;
                yp0 += 2;
                yp1 -= 2;
            }
        }

        /* Mirror on both sides for TDAC */
        {
            int xp1 = overlap - 1;
            int yp1 = 0;
            int wp1 = 0;
            int wp2 = overlap - 1;

            i = 0;
            if (simd)
            {
                for (; i + 4 <= overlap / 2; i += 4)
                {
                    /* x2 = out[i..], x1 = out[overlap-1-i..] (down), wp1 = i up, wp2 = overlap-1-i down */
                    Span<int> lo = @out.Slice(i, 4);
                    Span<int> hi = @out.Slice(overlap - 4 - i, 4);
                    Vector128<int> x2 = Vector128.Create((ReadOnlySpan<int>)lo);
                    Vector128<int> x1 = Simd.Reverse(Vector128.Create((ReadOnlySpan<int>)hi));
                    Vector128<int> w1 = Simd.LoadWiden(window.Slice(i));
                    Vector128<int> w2 = Simd.Reverse(Simd.LoadWiden(window.Slice(overlap - 4 - i)));
                    Vector128<int> ylo = Simd.MULT16_32_Q15(w2, x2) - Simd.MULT16_32_Q15(w1, x1);
                    Vector128<int> yhi = Simd.MULT16_32_Q15(w1, x2) + Simd.MULT16_32_Q15(w2, x1);
                    ylo.CopyTo(lo);
                    Simd.Reverse(yhi).CopyTo(hi);
                }
                xp1 = overlap - 1 - i;
                yp1 = i;
                wp1 = i;
                wp2 = overlap - 1 - i;
            }
            for (; i < overlap / 2; i++)
            {
                int x1, x2;
                x1 = @out[xp1];
                x2 = @out[yp1];
                @out[yp1++] = SUB32_ovflw(S_MUL(x2, window[wp2]), S_MUL(x1, window[wp1]));
                @out[xp1--] = ADD32_ovflw(S_MUL(x2, window[wp1]), S_MUL(x1, window[wp2]));
                wp1++;
                wp2--;
            }
        }
    }
}
