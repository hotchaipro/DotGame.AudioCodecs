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
/// The encoder side of celt/quant_bands.c: quantising the coarse (two-pass intra/inter) and fine
/// band energies, and amp2Log2. The tables the decoder's <see cref="QuantBands"/> keeps private
/// are repeated here; eMeans is the decoder's.
/// </summary>
[SkipLocalsInit]
internal static class QuantBandsEnc
{
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
        /*120 sample frames.*/
        /*Inter*/
        72, 127, 65, 129, 66, 128, 65, 128, 64, 128, 62, 128, 64, 128,
        64, 128, 92, 78, 92, 79, 92, 78, 90, 79, 116, 41, 115, 40,
        114, 40, 132, 26, 132, 26, 145, 17, 161, 12, 176, 10, 177, 11,
        /*Intra*/
        24, 179, 48, 138, 54, 135, 54, 132, 53, 134, 56, 133, 55, 132,
        55, 132, 61, 114, 70, 96, 74, 88, 75, 88, 87, 74, 89, 66,
        91, 67, 100, 59, 108, 50, 120, 40, 122, 37, 97, 43, 78, 50,
        /*240 sample frames.*/
        /*Inter*/
        83, 78, 84, 81, 88, 75, 86, 74, 87, 71, 90, 73, 93, 74,
        93, 74, 109, 40, 114, 36, 117, 34, 117, 34, 143, 17, 145, 18,
        146, 19, 162, 12, 165, 10, 178, 7, 189, 6, 190, 8, 177, 9,
        /*Intra*/
        23, 178, 54, 115, 63, 102, 66, 98, 69, 99, 74, 89, 71, 91,
        73, 91, 78, 89, 86, 80, 92, 66, 93, 64, 102, 59, 103, 60,
        104, 60, 117, 52, 123, 44, 138, 35, 133, 31, 97, 38, 77, 45,
        /*480 sample frames.*/
        /*Inter*/
        61, 90, 93, 60, 105, 42, 107, 41, 110, 45, 116, 38, 113, 38,
        112, 38, 124, 26, 132, 27, 136, 19, 140, 20, 155, 14, 159, 16,
        158, 18, 170, 13, 177, 10, 187, 8, 192, 6, 175, 9, 159, 10,
        /*Intra*/
        21, 178, 59, 110, 71, 86, 75, 85, 84, 83, 91, 66, 88, 73,
        87, 72, 92, 75, 98, 72, 105, 58, 107, 54, 115, 52, 114, 55,
        112, 56, 129, 51, 132, 40, 150, 33, 140, 29, 98, 35, 77, 42,
        /*960 sample frames.*/
        /*Inter*/
        42, 121, 96, 66, 108, 43, 111, 40, 117, 44, 123, 32, 120, 36,
        119, 33, 127, 33, 134, 34, 139, 21, 147, 23, 152, 20, 158, 25,
        154, 26, 166, 21, 173, 16, 184, 13, 184, 10, 150, 13, 139, 15,
        /*Intra*/
        22, 178, 63, 114, 74, 82, 84, 83, 92, 82, 103, 62, 96, 72,
        96, 67, 101, 73, 107, 72, 113, 55, 118, 52, 125, 52, 118, 52,
        117, 55, 135, 49, 137, 39, 157, 32, 145, 29, 97, 33, 77, 40,
    ];

    private static readonly byte[] small_energy_icdf = [2, 1, 0];

    private static int loss_distortion(scoped ReadOnlySpan<int> eBands, scoped ReadOnlySpan<int> oldEBands, int start, int end, int len, int C)
    {
        int c, i;
        int dist = 0;
        c = 0;
        do
        {
            for (i = start; i < end; i++)
            {
                int d = PSHR32(SUB32(eBands[i + (c * len)], oldEBands[i + (c * len)]), DB_SHIFT - 7);
                dist = MAC16_16(dist, d, d);
            }
        }
        while (++c < C);
        return MIN32(200, SHR32(dist, 14));
    }

    private static int quant_coarse_energy_impl(OpusCustomMode m, int start, int end,
          scoped ReadOnlySpan<int> eBands, scoped Span<int> oldEBands,
          int budget, int tell,
          scoped ReadOnlySpan<byte> prob_model, scoped Span<int> error, ref ec_enc enc,
          int C, int LM, int intra, int max_decay, int lfe)
    {
        int i, c;
        int badness = 0;
        Span<int> prev = stackalloc int[2];
        short coef;
        short beta;

        prev.Clear();
        if (tell + 3 <= budget)
        {
            enc.ec_enc_bit_logp(intra, 3);
        }
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

        /* Encode at a fixed coarse resolution */
        for (i = start; i < end; i++)
        {
            c = 0;
            do
            {
                int bits_left;
                int qi, qi0;
                int q;
                int x;
                int f, tmp;
                int oldE;
                int decay_bound;
                x = eBands[i + (c * m.nbEBands)];
                oldE = MAXG(-150994944 /* -GCONST(9.f) */, oldEBands[i + (c * m.nbEBands)]);
                f = x - MULT16_32_Q15(coef, oldE) - prev[c];
                /* Rounding to nearest integer here is really important! */
                qi = (f + 8388608 /* QCONST32(.5f,DB_SHIFT) */) >> DB_SHIFT;
                decay_bound = MAXG(-469762048 /* -GCONST(28.f) */, SUB32((int)oldEBands[i + (c * m.nbEBands)], max_decay));
                /* Prevent the energy from going down too quickly (e.g. for bands
                   that have just one bin) */
                if (qi < 0 && x < decay_bound)
                {
                    qi += (int)SHR32(SUB32(decay_bound, x), DB_SHIFT);
                    if (qi > 0)
                    {
                        qi = 0;
                    }
                }
                qi0 = qi;
                /* If we don't have enough bits to encode all the energy, just assume
                    something safe. */
                tell = enc.ec_tell();
                bits_left = budget - tell - (3 * C * (end - i));
                if (i != start && bits_left < 30)
                {
                    if (bits_left < 24)
                    {
                        qi = IMIN(1, qi);
                    }
                    if (bits_left < 16)
                    {
                        qi = IMAX(-1, qi);
                    }
                }
                if (lfe != 0 && i >= 2)
                {
                    qi = IMIN(qi, 0);
                }
                if (budget - tell >= 15)
                {
                    int pi;
                    pi = 2 * IMIN(i, 20);
                    LaplaceEnc.ec_laplace_encode(ref enc, ref qi,
                          (uint)(prob_model[pi] << 7), prob_model[pi + 1] << 6);
                }
                else if (budget - tell >= 2)
                {
                    qi = IMAX(-1, IMIN(qi, 1));
                    enc.ec_enc_icdf((2 * qi) ^ -(qi < 0 ? 1 : 0), small_energy_icdf, 2);
                }
                else if (budget - tell >= 1)
                {
                    qi = IMIN(0, qi);
                    enc.ec_enc_bit_logp(-qi, 1);
                }
                else
                {
                    qi = -1;
                }
                error[i + (c * m.nbEBands)] = f - SHL32(qi, DB_SHIFT);
                badness += ABS32(qi0 - qi);
                q = (int)SHL32(EXTEND32(qi), DB_SHIFT);

                tmp = MULT16_32_Q15(coef, oldE) + prev[c] + q;
                tmp = MAX32(-469762048 /* -GCONST(28.f) */, tmp);
                oldEBands[i + (c * m.nbEBands)] = tmp;
                prev[c] = prev[c] + q - MULT16_32_Q15(beta, q);
            }
            while (++c < C);
        }
        return lfe != 0 ? 0 : badness;
    }

    internal static void quant_coarse_energy(OpusCustomMode m, int start, int end, int effEnd,
          scoped ReadOnlySpan<int> eBands, scoped Span<int> oldEBands, uint budget,
          scoped Span<int> error, ref ec_enc enc, int C, int LM, int nbAvailableBytes,
          int force_intra, ref int delayedIntra, int two_pass, int loss_rate, int lfe)
    {
        int intra;
        int max_decay;
        ec_enc enc_start_state;
        uint tell;
        int badness1 = 0;
        int intra_bias;
        int new_distortion;

        intra = force_intra != 0 || (two_pass == 0 && delayedIntra > 2 * C * (end - start) && nbAvailableBytes > (end - start) * C) ? 1 : 0;
        intra_bias = (int)((budget * (uint)delayedIntra * (uint)loss_rate) / (uint)(C * 512));
        new_distortion = loss_distortion(eBands, oldEBands, start, effEnd, m.nbEBands, C);

        tell = (uint)enc.ec_tell();
        if (tell + 3 > budget)
        {
            two_pass = intra = 0;
        }

        max_decay = 268435456; /* GCONST(16.f) */
        if (end - start > 10)
        {
            max_decay = SHL32(MIN32(SHR32(max_decay, DB_SHIFT - 3), EXTEND32(nbAvailableBytes)), DB_SHIFT - 3);
        }
        if (lfe != 0)
        {
            max_decay = 50331648; /* GCONST(3.f) */
        }
        enc_start_state = enc;

        Span<int> oldEBands_intra = stackalloc int[C * m.nbEBands];
        Span<int> error_intra = stackalloc int[C * m.nbEBands];
        oldEBands.Slice(0, C * m.nbEBands).CopyTo(oldEBands_intra);

        if (two_pass != 0 || intra != 0)
        {
            badness1 = quant_coarse_energy_impl(m, start, end, eBands, oldEBands_intra, (int)budget,
                  (int)tell, e_prob_model.AsSpan(((2 * LM) + 1) * 42, 42), error_intra, ref enc, C, LM, 1, max_decay, lfe);
        }

        if (intra == 0)
        {
            Span<byte> intra_buf;
            ec_enc enc_intra_state;
            int tell_intra;
            uint nstart_bytes;
            uint nintra_bytes;
            uint save_bytes;
            int badness2;

            tell_intra = (int)enc.ec_tell_frac();

            enc_intra_state = enc;

            nstart_bytes = enc_start_state.ec_range_bytes();
            nintra_bytes = enc_intra_state.ec_range_bytes();
            intra_buf = enc_intra_state.ec_get_buffer().Slice((int)nstart_bytes);
            save_bytes = nintra_bytes - nstart_bytes;
            /* C allocates at least one byte (ALLOC_NONE) when there's nothing to save. */
            Span<byte> intra_bits = stackalloc byte[(int)save_bytes];
            /* Copy bits from intra bit-stream */
            intra_buf.Slice(0, (int)(nintra_bytes - nstart_bytes)).CopyTo(intra_bits);

            enc = enc_start_state;

            badness2 = quant_coarse_energy_impl(m, start, end, eBands, oldEBands, (int)budget,
                  (int)tell, e_prob_model.AsSpan(((2 * LM) + intra) * 42, 42), error, ref enc, C, LM, 0, max_decay, lfe);

            if (two_pass != 0 && (badness1 < badness2 || (badness1 == badness2 && ((int)enc.ec_tell_frac()) + intra_bias > tell_intra)))
            {
                enc = enc_intra_state;
                /* Copy intra bits to bit-stream */
                intra_bits.Slice(0, (int)(nintra_bytes - nstart_bytes)).CopyTo(intra_buf);
                oldEBands_intra.Slice(0, C * m.nbEBands).CopyTo(oldEBands);
                error_intra.Slice(0, C * m.nbEBands).CopyTo(error);
                intra = 1;
            }
        }
        else
        {
            oldEBands_intra.Slice(0, C * m.nbEBands).CopyTo(oldEBands);
            error_intra.Slice(0, C * m.nbEBands).CopyTo(error);
        }

        if (intra != 0)
        {
            delayedIntra = new_distortion;
        }
        else
        {
            delayedIntra = ADD32(MULT16_32_Q15(MULT16_16_Q15(pred_coef[LM], pred_coef[LM]), delayedIntra),
                  new_distortion);
        }
    }

    /* prev_quant empty stands for C's NULL. */
    internal static void quant_fine_energy(OpusCustomMode m, int start, int end, scoped Span<int> oldEBands, scoped Span<int> error, scoped ReadOnlySpan<int> prev_quant, scoped ReadOnlySpan<int> extra_quant, ref ec_enc enc, int C)
    {
        int i, c;
        /* Encode finer resolution */
        for (i = start; i < end; i++)
        {
            short extra, prev;
            extra = (short)(1 << extra_quant[i]);
            if (extra_quant[i] <= 0)
            {
                continue;
            }
            if (enc.ec_tell() + (C * extra_quant[i]) > (int)enc.storage * 8)
            {
                continue;
            }
            prev = (short)(!prev_quant.IsEmpty ? prev_quant[i] : 0);
            c = 0;
            do
            {
                int q2;
                int offset;
                /* Has to be without rounding */
                q2 = VSHR32(ADD32(error[i + (c * m.nbEBands)], SHR32(8388608 /* GCONST(.5f) */, prev)), DB_SHIFT - extra_quant[i] - prev);
                if (q2 > extra - 1)
                {
                    q2 = extra - 1;
                }
                if (q2 < 0)
                {
                    q2 = 0;
                }
                enc.ec_enc_bits((uint)q2, (uint)extra_quant[i]);
                offset = SUB32(VSHR32((2 * q2) + 1, extra_quant[i] - DB_SHIFT + 1), 8388608 /* GCONST(.5f) */);
                offset = SHR32(offset, prev);
                oldEBands[i + (c * m.nbEBands)] += offset;
                error[i + (c * m.nbEBands)] -= offset;
                /*printf ("%f ", error[i] - offset);*/
            }
            while (++c < C);
        }
    }

    /* oldEBands empty stands for C's NULL. */
    internal static void quant_energy_finalise(OpusCustomMode m, int start, int end, scoped Span<int> oldEBands, scoped Span<int> error, scoped ReadOnlySpan<int> fine_quant, scoped ReadOnlySpan<int> fine_priority, int bits_left, ref ec_enc enc, int C)
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
                    q2 = error[i + (c * m.nbEBands)] < 0 ? 0 : 1;
                    enc.ec_enc_bits((uint)q2, 1);
                    offset = SHR32(SHL32(q2, DB_SHIFT) - 8388608 /* GCONST(.5f) */, fine_quant[i] + 1);
                    if (!oldEBands.IsEmpty)
                    {
                        oldEBands[i + (c * m.nbEBands)] += offset;
                    }
                    error[i + (c * m.nbEBands)] -= offset;
                    bits_left--;
                }
                while (++c < C);
            }
        }
    }

    internal static void amp2Log2(OpusCustomMode m, int effEnd, int end,
          scoped ReadOnlySpan<int> bandE, scoped Span<int> bandLogE, int C)
    {
        int c, i;
        c = 0;
        do
        {
            for (i = 0; i < effEnd; i++)
            {
                bandLogE[i + (c * m.nbEBands)] =
                      MathOpsEnc.celt_log2_db(bandE[i + (c * m.nbEBands)])
                      - SHL32((int)QuantBands.eMeans[i], DB_SHIFT - 4);
                /* Compensate for bandE[] being Q12 but celt_log2() taking a Q14 input. */
                bandLogE[i + (c * m.nbEBands)] += 33554432; /* GCONST(2.f) */
            }
            for (i = effEnd; i < end; i++)
            {
                bandLogE[(c * m.nbEBands) + i] = -234881024; /* -GCONST(14.f) */
            }
        }
        while (++c < C);
    }
}
