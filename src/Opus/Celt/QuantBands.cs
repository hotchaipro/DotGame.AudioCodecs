/* Copyright (c) 2007-2008 CSIRO
   Copyright (c) 2007-2009 Xiph.Org Foundation
   Written by Jean-Marc Valin
   Ported to C# from libopus 1.6.1 (celt/quant_bands.c) by Hot Chai Games LLC */
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
using DotGame.AudioCodecs.Opus.Common;
using static DotGame.AudioCodecs.Opus.Celt.Arch;

namespace DotGame.AudioCodecs.Opus.Celt;

/// <summary>
/// The decoder side of celt/quant_bands.c: decoding the coarse and fine band energies.
/// </summary>
[SkipLocalsInit]
internal static class QuantBands
{
    /* Mean energy in each band quantized in Q4 */
    internal static readonly sbyte[] eMeans =
    [
        103, 100, 92, 85, 81,
        77, 72, 70, 78, 75,
        73, 71, 78, 74, 69,
        72, 70, 74, 76, 71,
        60, 60, 60, 60, 60,
    ];

    /* prediction coefficients: 0.9, 0.8, 0.65, 0.5 */
    private static readonly short[] pred_coef = [29440, 26112, 21248, 16384];
    private static readonly short[] beta_coef = [30147, 22282, 12124, 6554];
    private const short beta_intra = 4915;

    /*Parameters of the Laplace-like probability models used for the coarse energy.
      There is one pair of parameters for each frame size, prediction type
       (inter/intra), and band number.
      The first number of each pair is the probability of 0, and the second is the
       decay rate, both in Q8 precision.
      C's e_prob_model[4][2][42], flattened: [LM][intra] starts at (2*LM+intra)*42.*/
    private static readonly byte[] e_prob_model =
    [
        72, 127, 65, 129, 66, 128, 65, 128, 64, 128, 62, 128, 64, 128, 64, 128,
        92, 78, 92, 79, 92, 78, 90, 79, 116, 41, 115, 40, 114, 40, 132, 26,
        132, 26, 145, 17, 161, 12, 176, 10, 177, 11, 24, 179, 48, 138, 54, 135,
        54, 132, 53, 134, 56, 133, 55, 132, 55, 132, 61, 114, 70, 96, 74, 88,
        75, 88, 87, 74, 89, 66, 91, 67, 100, 59, 108, 50, 120, 40, 122, 37,
        97, 43, 78, 50, 83, 78, 84, 81, 88, 75, 86, 74, 87, 71, 90, 73,
        93, 74, 93, 74, 109, 40, 114, 36, 117, 34, 117, 34, 143, 17, 145, 18,
        146, 19, 162, 12, 165, 10, 178, 7, 189, 6, 190, 8, 177, 9, 23, 178,
        54, 115, 63, 102, 66, 98, 69, 99, 74, 89, 71, 91, 73, 91, 78, 89,
        86, 80, 92, 66, 93, 64, 102, 59, 103, 60, 104, 60, 117, 52, 123, 44,
        138, 35, 133, 31, 97, 38, 77, 45, 61, 90, 93, 60, 105, 42, 107, 41,
        110, 45, 116, 38, 113, 38, 112, 38, 124, 26, 132, 27, 136, 19, 140, 20,
        155, 14, 159, 16, 158, 18, 170, 13, 177, 10, 187, 8, 192, 6, 175, 9,
        159, 10, 21, 178, 59, 110, 71, 86, 75, 85, 84, 83, 91, 66, 88, 73,
        87, 72, 92, 75, 98, 72, 105, 58, 107, 54, 115, 52, 114, 55, 112, 56,
        129, 51, 132, 40, 150, 33, 140, 29, 98, 35, 77, 42, 42, 121, 96, 66,
        108, 43, 111, 40, 117, 44, 123, 32, 120, 36, 119, 33, 127, 33, 134, 34,
        139, 21, 147, 23, 152, 20, 158, 25, 154, 26, 166, 21, 173, 16, 184, 13,
        184, 10, 150, 13, 139, 15, 22, 178, 63, 114, 74, 82, 84, 83, 92, 82,
        103, 62, 96, 72, 96, 67, 101, 73, 107, 72, 113, 55, 118, 52, 125, 52,
        118, 52, 117, 55, 135, 49, 137, 39, 157, 32, 145, 29, 97, 33, 77, 40,
    ];

    private static readonly byte[] small_energy_icdf = [2, 1, 0];

    internal static void unquant_coarse_energy(OpusCustomMode m, int start, int end, scoped Span<int> oldEBands, int intra, ref ec_dec dec, int C, int LM)
    {
        ReadOnlySpan<byte> prob_model = e_prob_model.AsSpan(((2 * LM) + intra) * 42, 42);
        int i, c;
        Span<long> prev = stackalloc long[2];
        short coef;
        short beta;
        int budget;
        int tell;

        prev.Clear();
        if (intra != 0)
        {
            coef = 0;
            beta = beta_intra;
        }
        else
        {
            beta = beta_coef[LM];
            coef = pred_coef[LM];
        }

        budget = (int)(dec.storage * 8);

        /* Decode at a fixed coarse resolution */
        for (i = start; i < end; i++)
        {
            c = 0;
            do
            {
                int qi;
                int q;
                int tmp;
                /* It would be better to express this invariant as a
                   test on C at function entry, but that isn't enough
                   to make the static analyzer happy. */
                tell = dec.ec_tell();
                if (budget - tell >= 15)
                {
                    int pi;
                    pi = 2 * IMIN(i, 20);
                    qi = Laplace.ec_laplace_decode(ref dec,
                          (uint)(prob_model[pi] << 7), prob_model[pi + 1] << 6);
                }
                else if (budget - tell >= 2)
                {
                    qi = dec.ec_dec_icdf(small_energy_icdf, 2);
                    qi = (qi >> 1) ^ -(qi & 1);
                }
                else if (budget - tell >= 1)
                {
                    qi = -dec.ec_dec_bit_logp(1);
                }
                else
                {
                    qi = -1;
                }
                q = SHL32(EXTEND32(qi), DB_SHIFT);

                oldEBands[i + (c * m.nbEBands)] = MAXG(-150994944 /* -GCONST(9.f) */, oldEBands[i + (c * m.nbEBands)]);
                tmp = (int)(MULT16_32_Q15(coef, oldEBands[i + (c * m.nbEBands)]) + prev[c] + q);
                tmp = MIN32(469762048 /* GCONST(28.f) */, MAX32(-469762048 /* -GCONST(28.f) */, tmp));
                oldEBands[i + (c * m.nbEBands)] = tmp;
                prev[c] = prev[c] + q - MULT16_32_Q15(beta, q);
            }
            while (++c < C);
        }
    }

    /* prev_quant empty stands for C's NULL. */
    internal static void unquant_fine_energy(OpusCustomMode m, int start, int end, scoped Span<int> oldEBands, scoped ReadOnlySpan<int> prev_quant, scoped ReadOnlySpan<int> extra_quant, ref ec_dec dec, int C)
    {
        int i, c;
        /* Decode finer resolution */
        for (i = start; i < end; i++)
        {
            short extra, prev;
            extra = (short)extra_quant[i];
            if (extra_quant[i] <= 0)
            {
                continue;
            }
            if (dec.ec_tell() + (C * extra_quant[i]) > (int)dec.storage * 8)
            {
                continue;
            }
            prev = (short)(!prev_quant.IsEmpty ? prev_quant[i] : 0);
            c = 0;
            do
            {
                int q2;
                int offset;
                q2 = (int)dec.ec_dec_bits((uint)extra);
                offset = SUB32(VSHR32((2 * q2) + 1, extra - DB_SHIFT + 1), 8388608 /* GCONST(.5f) */);
                offset = SHR32(offset, prev);
                oldEBands[i + (c * m.nbEBands)] += offset;
            }
            while (++c < C);
        }
    }

    /* oldEBands empty stands for C's NULL. */
    internal static void unquant_energy_finalise(OpusCustomMode m, int start, int end, scoped Span<int> oldEBands, scoped ReadOnlySpan<int> fine_quant, scoped ReadOnlySpan<int> fine_priority, int bits_left, ref ec_dec dec, int C)
    {
        int i, prio, c;

        /* Use up the remaining bits */
        for (prio = 0; prio < 2; prio++)
        {
            for (i = start; i < end && bits_left >= C; i++)
            {
                if (fine_quant[i] >= Rate.MAX_FINE_BITS || fine_priority[i] != prio)
                {
                    continue;
                }
                c = 0;
                do
                {
                    int q2;
                    int offset;
                    q2 = (int)dec.ec_dec_bits(1);
                    offset = SHR32(SHL32(q2, DB_SHIFT) - 8388608 /* GCONST(.5f) */, fine_quant[i] + 1);
                    if (!oldEBands.IsEmpty)
                    {
                        oldEBands[i + (c * m.nbEBands)] += offset;
                    }
                    bits_left--;
                }
                while (++c < C);
            }
        }
    }
}
