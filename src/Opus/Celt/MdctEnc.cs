/* Copyright (c) 2007-2008 CSIRO
   Copyright (c) 2007-2008 Xiph.Org Foundation
   Written by Jean-Marc Valin
   Ported to C# from libopus 1.6.1 (celt/mdct.c) by Hot Chai Games LLC */
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

/// <summary>
/// The encoder side of celt/mdct.c: the forward MDCT (windowing, folding, pre-rotation, the FFT and
/// post-rotation).
/// </summary>
[SkipLocalsInit]
internal static class MdctEnc
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int S_MUL(int a, int b) => MULT16_32_Q15(b, a);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int S_MUL2(int a, int b) => MULT16_32_Q16(b, a);

    // Four shorts from there, sign-extended (eight bytes read, no more)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Load4(ref short from, int at) =>
        Vector128.WidenLower(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<long>(ref Unsafe.As<short, byte>(ref Unsafe.Add(ref from, at)))).AsInt16());

    /* Forward MDCT trashes the input array */
    internal static void clt_mdct_forward(mdct_lookup l, ReadOnlySpan<int> @in, Span<int> @out,
          ReadOnlySpan<short> window, int overlap, int shift, int stride)
    {
        int i;
        int N, N2, N4;
        kiss_fft_state st = l.kfft[shift];
        ReadOnlySpan<short> trig;
        short scale;
        /* Allows us to scale with MULT16_32_Q16(), which is faster than
           MULT16_32_Q15() on ARM. */
        int scale_shift = st.scale_shift - 1;
        int headroom;
        scale = st.scale;

        N = l.n;
        trig = l.trig;
        for (i = 0; i < shift; i++)
        {
            N >>= 1;
            trig = trig.Slice(N);
        }
        N2 = N >> 1;
        N4 = N >> 2;

        Span<int> f = stackalloc int[N2];
        Span<kiss_fft_cpx> f2 = stackalloc kiss_fft_cpx[N4];

        // The loops index with refs, after these checks of their furthest reads and writes
        if ((@in.Length < N2 + overlap) || (window.Length < overlap) || (trig.Length < N2) || (@out.Length <= stride * (N2 - 1)))
        {
            throw new ArgumentException("The MDCT's buffers are too short.");
        }
        ref int input = ref MemoryMarshal.GetReference(@in);
        ref short w = ref MemoryMarshal.GetReference(window);
        ref int y = ref MemoryMarshal.GetReference(f);

        /* Consider the input to be composed of four blocks: [a, b, c, d] */
        /* Window, shuffle, fold */
        {
            /* C's pointers, as offsets: xp1 into in, xp2 into in, yp into f, wp1 and wp2 into window */
            int xp1 = overlap >> 1;
            int xp2 = N2 - 1 + (overlap >> 1);
            int yp = 0;
            int wp1 = overlap >> 1;
            int wp2 = (overlap >> 1) - 1;
            for (i = 0; i < ((overlap + 3) >> 2); i++)
            {
                /* Real part arranged as -d-cR, Imag part arranged as -b+aR*/
                Unsafe.Add(ref y, yp++) = S_MUL(Unsafe.Add(ref input, xp1 + N2), Unsafe.Add(ref w, wp2)) + S_MUL(Unsafe.Add(ref input, xp2), Unsafe.Add(ref w, wp1));
                Unsafe.Add(ref y, yp++) = S_MUL(Unsafe.Add(ref input, xp1), Unsafe.Add(ref w, wp1)) - S_MUL(Unsafe.Add(ref input, xp2 - N2), Unsafe.Add(ref w, wp2));
                xp1 += 2;
                xp2 -= 2;
                wp1 += 2;
                wp2 -= 2;
            }
            wp1 = 0;
            wp2 = overlap - 1;
            for (; i < N4 - ((overlap + 3) >> 2); i++)
            {
                /* Real part arranged as a-bR, Imag part arranged as -c-dR */
                Unsafe.Add(ref y, yp++) = Unsafe.Add(ref input, xp2);
                Unsafe.Add(ref y, yp++) = Unsafe.Add(ref input, xp1);
                xp1 += 2;
                xp2 -= 2;
            }
            for (; i < N4; i++)
            {
                /* Real part arranged as a-bR, Imag part arranged as -c-dR */
                Unsafe.Add(ref y, yp++) = -S_MUL(Unsafe.Add(ref input, xp1 - N2), Unsafe.Add(ref w, wp1)) + S_MUL(Unsafe.Add(ref input, xp2), Unsafe.Add(ref w, wp2));
                Unsafe.Add(ref y, yp++) = S_MUL(Unsafe.Add(ref input, xp1), Unsafe.Add(ref w, wp2)) + S_MUL(Unsafe.Add(ref input, xp2 + N2), Unsafe.Add(ref w, wp1));
                xp1 += 2;
                xp2 -= 2;
                wp1 += 2;
                wp2 -= 2;
            }
        }
        ref short t = ref MemoryMarshal.GetReference(trig);
        ref kiss_fft_cpx z = ref MemoryMarshal.GetReference(f2);
        /* Pre-rotation */
        {
            ref short bitrev = ref MemoryMarshal.GetArrayDataReference(st.bitrev);
            if (st.bitrev.Length < N4)
            {
                throw new ArgumentException("The FFT's bit reversal is too short.");
            }
            int maxval = 1;
            i = 0;
            if (Simd.HasMULT32_32_Q31)
            {
                // Four at a time: the pairs split into real and imaginary parts (each pair a 64-bit
                // lane, narrowed), each lane the scalar loop's arithmetic, the results paired again
                // and scattered
                var scaleV = Vector128.Create((int)scale);
                var max = Vector128<int>.Zero;
                ref long pairs = ref Unsafe.As<int, long>(ref y);
                for (; i + 4 <= N4; i += 4)
                {
                    var p0 = Vector128.LoadUnsafe(ref pairs, (nuint)i);
                    var p1 = Vector128.LoadUnsafe(ref pairs, (nuint)(i + 2));
                    var re = Vector128.Narrow(p0, p1);
                    var im = Vector128.Narrow(p0 >> 32, p1 >> 32);
                    var t0 = Load4(ref t, i);
                    var t1 = Load4(ref t, N4 + i);
                    var yr = Simd.MULT16_32_Q16(scaleV, Simd.MULT16_32_Q15(t0, re) - Simd.MULT16_32_Q15(t1, im));
                    var yi = Simd.MULT16_32_Q16(scaleV, Simd.MULT16_32_Q15(t0, im) + Simd.MULT16_32_Q15(t1, re));
                    max = Vector128.Max(max, Vector128.Max(Vector128.Abs(yr), Vector128.Abs(yi)));
                    var low = Vector128.WidenLower(yr.AsUInt32()) | (Vector128.WidenLower(yi.AsUInt32()) << 32);
                    var high = Vector128.WidenUpper(yr.AsUInt32()) | (Vector128.WidenUpper(yi.AsUInt32()) << 32);
                    for (int k = 0; k < 4; k++)
                    {
                        int to = Unsafe.Add(ref bitrev, i + k);
                        if ((uint)to >= (uint)N4)
                        {
                            throw new ArgumentException("The FFT's bit reversal is out of range.");
                        }
                        Unsafe.As<kiss_fft_cpx, ulong>(ref Unsafe.Add(ref z, to)) = k < 2 ? low.GetElement(k) : high.GetElement(k - 2);
                    }
                }
                for (int k = 0; k < 4; k++)
                {
                    maxval = MAX32(maxval, max.GetElement(k));
                }
            }
            for (; i < N4; i++)
            {
                kiss_fft_cpx yc;
                int t0 = Unsafe.Add(ref t, i);
                int t1 = Unsafe.Add(ref t, N4 + i);
                int re = Unsafe.Add(ref y, 2 * i);
                int im = Unsafe.Add(ref y, (2 * i) + 1);
                int yr = S_MUL(re, t0) - S_MUL(im, t1);
                int yi = S_MUL(im, t0) + S_MUL(re, t1);
                /* For QEXT, it's best to scale before the FFT, but otherwise it's best to scale after.
                   For floating-point it doesn't matter. */
                yc.r = S_MUL2(yr, scale);
                yc.i = S_MUL2(yi, scale);
                maxval = MAX32(maxval, MAX32(ABS32(yc.r), ABS32(yc.i)));
                int to = Unsafe.Add(ref bitrev, i);
                if ((uint)to >= (uint)N4)
                {
                    throw new ArgumentException("The FFT's bit reversal is out of range.");
                }
                Unsafe.Add(ref z, to) = yc;
            }
            headroom = IMAX(0, IMIN(scale_shift, 28 - celt_ilog2(maxval)));
        }

        /* N/4 complex FFT, does not downscale anymore */
        KissFft.opus_fft_impl(st, f2, scale_shift - headroom);

        /* Post-rotate */
        {
            /* C's pointers, as offsets: yp1 and yp2 into out */
            ref int o = ref MemoryMarshal.GetReference(@out);
            int yp1 = 0;
            int yp2 = stride * (N2 - 1);
            i = 0;
            if (Simd.HasMULT32_32_Q31)
            {
                var round = Vector128.Create((1 << headroom) >> 1);
                ref long pairs = ref Unsafe.As<kiss_fft_cpx, long>(ref z);
                for (; i + 4 <= N4; i += 4)
                {
                    var p0 = Vector128.LoadUnsafe(ref pairs, (nuint)i);
                    var p1 = Vector128.LoadUnsafe(ref pairs, (nuint)(i + 2));
                    var vr = Vector128.Narrow(p0, p1);
                    var vi = Vector128.Narrow(p0 >> 32, p1 >> 32);
                    var t0 = Load4(ref t, i);
                    var t1 = Load4(ref t, N4 + i);
                    var yr = (Simd.MULT16_32_Q15(t1, vi) - Simd.MULT16_32_Q15(t0, vr) + round) >> headroom;
                    var yi = (Simd.MULT16_32_Q15(t1, vr) + Simd.MULT16_32_Q15(t0, vi) + round) >> headroom;
                    for (int k = 0; k < 4; k++)
                    {
                        Unsafe.Add(ref o, yp1) = yr.GetElement(k);
                        Unsafe.Add(ref o, yp2) = yi.GetElement(k);
                        yp1 += 2 * stride;
                        yp2 -= 2 * stride;
                    }
                }
            }
            for (; i < N4; i++)
            {
                int t0 = Unsafe.Add(ref t, i);
                int t1 = Unsafe.Add(ref t, N4 + i);
                kiss_fft_cpx v = Unsafe.Add(ref z, i);
                Unsafe.Add(ref o, yp1) = PSHR32(S_MUL(v.i, t1) - S_MUL(v.r, t0), headroom);
                Unsafe.Add(ref o, yp2) = PSHR32(S_MUL(v.r, t1) + S_MUL(v.i, t0), headroom);
                yp1 += 2 * stride;
                yp2 -= 2 * stride;
            }
        }
    }
}
