/* Copyright (c) 2007-2008 CSIRO
   Copyright (c) 2007-2009 Xiph.Org Foundation
   Written by Jean-Marc Valin
   Ported to C# from libopus 1.6.1 (celt/vq.c) by Hot Chai Games LLC */
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
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using DotGame.AudioCodecs.Opus.Common;
using static DotGame.AudioCodecs.Opus.Celt.Arch;
using static DotGame.AudioCodecs.Opus.Celt.MathOps;

namespace DotGame.AudioCodecs.Opus.Celt;

/// <summary>
/// The encoder side of celt/vq.c: the PVQ search (op_pvq_search), the algebraic pulse quantiser
/// (alg_quant) and the stereo angle (stereo_itheta). The rotations and renormalisation are the
/// decoder's (<see cref="Vq"/>); normalise_residual and extract_collapse_mask, private there, are
/// repeated here.
/// </summary>
[SkipLocalsInit]
internal static class VqEnc
{
    /** Normalizes the decoded integer pvq codeword to unit norm. */
    private static void normalise_residual(ReadOnlySpan<int> iy, Span<int> X,
          int N, int Ryy, int gain, int shift)
    {
        int i;
        int k;
        int t;
        int g;

        k = celt_ilog2(Ryy) >> 1;
        t = VSHR32(Ryy, (2 * (k - 7)) - 15);
        g = MULT32_32_Q31(celt_rsqrt_norm32(t), gain);
        i = 0;
        _ = shift;
        do
        {
            X[i] = VSHR32(MULT16_32_Q15(iy[i], g), k + 15 - NORM_SHIFT);
        }
        while (++i < N);
    }

    private static uint extract_collapse_mask(ReadOnlySpan<int> iy, int N, int B)
    {
        uint collapse_mask;
        int N0;
        int i;
        if (B <= 1)
        {
            return 1;
        }
        /*NOTE: As a minor optimization, we could be passing around log2(B), not B, for both this and for
           exp_rotation().*/
        N0 = (int)ec_dec.celt_udiv((uint)N, (uint)B);
        collapse_mask = 0;
        i = 0;
        do
        {
            int j;
            uint tmp = 0;
            j = 0;
            do
            {
                tmp |= (uint)iy[(i * N0) + j];
            }
            while (++j < N0);
            collapse_mask |= (uint)((tmp != 0 ? 1 : 0) << i);
        }
        while (++i < B);
        return collapse_mask;
    }

    // The position op_pvq_search's scan picks: the first of the highest scores Rxy^2 / Ryy (in
    // Q15, each truncated to 16 bits as the C's are). Vectors for long bands, the C's loop for
    // the rest; both give the same position.
    private static int BestPosition(ReadOnlySpan<int> X, ReadOnlySpan<int> y, int N, int xy, short yy, int rshift)
    {
        X = X.Slice(0, N);
        y = y.Slice(0, N);
        int best_id;
        int best_num;
        int best_den;
        int j;
        if (Vector128.IsHardwareAccelerated && (N >= 8))
        {
            ref int xs = ref MemoryMarshal.GetReference(X);
            ref int ys = ref MemoryMarshal.GetReference(y);
            var xyv = Vector128.Create(xy);
            var yyv = Vector128.Create((int)yy);

            /* Rxy = EXTRACT16(SHR32(xy + X[j], rshift)), then MULT16_16_Q15(Rxy, Rxy) to 16 bits;
               Ryy = ADD16(yy, y[j]). Each lane starts from its first position. */
            var bestNum = ToInt16((xyv + Vector128.LoadUnsafe(ref xs)) >> rshift);
            bestNum = ToInt16((bestNum * bestNum) >> 15);
            var bestDen = ToInt16(yyv + Vector128.LoadUnsafe(ref ys));
            var bestId = Vector128.Create(0, 1, 2, 3);
            var ids = bestId;
            var four = Vector128.Create(4);
            for (j = 4; j + 4 <= N; j += 4)
            {
                ids += four;
                var rxy = ToInt16((xyv + Vector128.LoadUnsafe(ref xs, (nuint)j)) >> rshift);
                rxy = ToInt16((rxy * rxy) >> 15);
                var ryy = ToInt16(yyv + Vector128.LoadUnsafe(ref ys, (nuint)j));
                var better = Vector128.GreaterThan(bestDen * rxy, ryy * bestNum);
                bestNum = Vector128.ConditionalSelect(better, rxy, bestNum);
                bestDen = Vector128.ConditionalSelect(better, ryy, bestDen);
                bestId = Vector128.ConditionalSelect(better, ids, bestId);
            }

            best_num = bestNum.GetElement(0);
            best_den = bestDen.GetElement(0);
            best_id = bestId.GetElement(0);
            for (int lane = 1; lane < 4; lane++)
            {
                int num = bestNum.GetElement(lane);
                int den = bestDen.GetElement(lane);
                int id = bestId.GetElement(lane);
                int ours = best_den * num;
                int theirs = den * best_num;
                if ((ours > theirs) || ((ours == theirs) && (id < best_id)))
                {
                    best_num = num;
                    best_den = den;
                    best_id = id;
                }
            }
        }
        else
        {
            /* Position 0 first, as the C's loop starts */
            short Rxy0 = EXTRACT16(SHR32(ADD32(xy, EXTEND32(X[0])), rshift));
            best_den = ADD16(yy, y[0]);
            best_num = (short)MULT16_16_Q15(Rxy0, Rxy0);
            best_id = 0;
            j = 1;
        }

        for (; j < N; j++)
        {
            /* Temporary sums of the new pulse(s) */
            short Rxy = EXTRACT16(SHR32(ADD32(xy, EXTEND32(X[j])), rshift));
            /* We're multiplying y[j] by two so we don't have to do it here */
            short Ryy = ADD16(yy, y[j]);
            Rxy = (short)MULT16_16_Q15(Rxy, Rxy);
            if (MULT16_16(best_den, Rxy) > MULT16_16(Ryy, best_num))
            {
                best_den = Ryy;
                best_num = Rxy;
                best_id = j;
            }
        }

        return best_id;
    }

    // Each lane truncated to 16 bits and sign-extended, as C's casts to opus_val16 do
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> ToInt16(Vector128<int> v) => (v << 16) >> 16;

    internal static short op_pvq_search(Span<int> X, Span<int> iy, int K, int N)
    {
        int i, j;
        int pulsesLeft;
        int sum;
        int xy;
        short yy;

        Span<int> y = stackalloc int[N];
        Span<int> signx = stackalloc int[N];
        {
            int shift = (celt_ilog2(1 + Vq.celt_inner_prod_norm_shift(X, X, N)) + 1) / 2;
            shift = IMAX(0, shift + (NORM_SHIFT - 14) - 14);
            Vq.norm_scaledown(X, N, shift);
        }
        /* Get rid of the sign */
        sum = 0;
        j = 0;
        do
        {
            signx[j] = X[j] < 0 ? 1 : 0;
            /* OPT: Make sure the compiler doesn't use a branch on ABS16(). */
            X[j] = ABS16(X[j]);
            iy[j] = 0;
            y[j] = 0;
        }
        while (++j < N);

        xy = yy = 0;

        pulsesLeft = K;

        /* Do a pre-search by projecting on the pyramid */
        if (K > (N >> 1))
        {
            short rcp;
            j = 0;
            do
            {
                sum += X[j];
            }
            while (++j < N);

            /* If X is too small, just replace it with a pulse at 0 */
            if (sum <= K)
            {
                X[0] = 16384; /* QCONST16(1.f,14) */
                j = 1;
                do
                {
                    X[j] = 0;
                }
                while (++j < N);
                sum = 16384; /* QCONST16(1.f,14) */
            }
            rcp = EXTRACT16(MULT16_32_Q16(K, celt_rcp(sum)));
            j = 0;
            do
            {
                /* It's really important to round *towards zero* here */
                iy[j] = MULT16_16_Q15(X[j], rcp);
                y[j] = (int)iy[j];
                yy = (short)MAC16_16(yy, y[j], y[j]);
                xy = MAC16_16(xy, X[j], y[j]);
                y[j] *= 2;
                pulsesLeft -= iy[j];
            }
            while (++j < N);
        }
        Debug.Assert(pulsesLeft >= 0);

        /* This should never happen, but just in case it does (e.g. on silence)
           we fill the first bin with pulses. */
        if (pulsesLeft > N + 3)
        {
            short tmp = (short)pulsesLeft;
            yy = (short)MAC16_16(yy, tmp, tmp);
            yy = (short)MAC16_16(yy, tmp, y[0]);
            iy[0] += pulsesLeft;
            pulsesLeft = 0;
        }

        for (i = 0; i < pulsesLeft; i++)
        {
            int best_id;
            int rshift;
            rshift = 1 + celt_ilog2(K - pulsesLeft + i + 1);
            /* The squared magnitude term gets added anyway, so we might as well
               add it outside the loop */
            yy = ADD16(yy, 1);

            /* The C's scan keeps the first position whose score num/den beats every other
               (num * best_den > best_num * den, exact in 32 bits). Its result is the earliest of
               the best scores, so four lanes each keep their own earliest best, and the lanes'
               bests merge to it: the higher score, or the lower position when scores tie. */
            best_id = BestPosition(X, y, N, xy, yy, rshift);

            /* Updating the sums of the new pulse(s) */
            xy = ADD32(xy, EXTEND32(X[best_id]));
            /* We're multiplying y[j] by two so we don't have to do it here */
            yy = ADD16(yy, y[best_id]);

            /* Only now that we've made the final choice, update y/iy */
            /* Multiplying y[j] by 2 so we don't have to do it everywhere else */
            y[best_id] += 2;
            iy[best_id]++;
        }

        /* Put the original sign back */
        j = 0;
        do
        {
            /*iy[j] = signx[j] ? -iy[j] : iy[j];*/
            /* OPT: The is more likely to be compiled without a branch than the code above
               but has the same performance otherwise. */
            iy[j] = (iy[j] ^ -signx[j]) + signx[j];
        }
        while (++j < N);
        return yy;
    }

    /* The encoder's alg_quant(); resynth is C's flag (the decoded vector is only rebuilt when
       it's set). */
    internal static uint alg_quant(scoped Span<int> X, int N, int K, int spread, int B, ref ec_enc enc,
          int gain, int resynth)
    {
        int yy;
        uint collapse_mask;

        Debug.Assert(K > 0, "alg_quant() needs at least one pulse");
        Debug.Assert(N > 1, "alg_quant() needs at least two dimensions");

        /* Covers vectorization by up to 4. */
        Span<int> iy = stackalloc int[N + 3];

        Vq.exp_rotation(X, N, 1, B, K, spread);

        {
            yy = op_pvq_search(X, iy, K, N);
            collapse_mask = extract_collapse_mask(iy, N, B);
            CwrsEnc.encode_pulses(iy, N, K, ref enc);
            if (resynth != 0)
            {
                normalise_residual(iy, X, N, yy, gain, 0);
            }
        }

        if (resynth != 0)
        {
            Vq.exp_rotation(X, N, -1, B, K, spread);
        }

        return collapse_mask;
    }

    internal static int stereo_itheta(ReadOnlySpan<int> X, ReadOnlySpan<int> Y, int stereo, int N)
    {
        int i;
        int itheta;
        int mid, side;
        int Emid, Eside;

        Emid = Eside = 0;
        if (stereo != 0)
        {
            for (i = 0; i < N; i++)
            {
                int m, s;
                m = PSHR32(ADD32(X[i], Y[i]), NORM_SHIFT - 13);
                s = PSHR32(SUB32(X[i], Y[i]), NORM_SHIFT - 13);
                Emid = MAC16_16(Emid, m, m);
                Eside = MAC16_16(Eside, s, s);
            }
        }
        else
        {
            Emid += Vq.celt_inner_prod_norm_shift(X, X, N);
            Eside += Vq.celt_inner_prod_norm_shift(Y, Y, N);
        }
        mid = MathOpsEnc.celt_sqrt32(Emid);
        side = MathOpsEnc.celt_sqrt32(Eside);
        itheta = MathOpsEnc.celt_atan2p_norm(side, mid);

        return itheta;
    }
}
