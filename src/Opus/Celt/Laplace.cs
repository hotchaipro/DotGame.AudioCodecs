/* Copyright (c) 2007 CSIRO
   Copyright (c) 2007-2009 Xiph.Org Foundation
   Written by Jean-Marc Valin
   Ported to C# from libopus 1.6.1 (celt/laplace.c) by Hot Chai Games LLC */
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

using DotGame.AudioCodecs.Opus.Common;

namespace DotGame.AudioCodecs.Opus.Celt;

/// <summary>
/// The decoder side of celt/laplace.c: the Laplace-like distribution of the coarse energy.
/// </summary>
internal static class Laplace
{
    /* The minimum probability of an energy delta (out of 32768). */
    private const int LAPLACE_LOG_MINP = 0;
    private const uint LAPLACE_MINP = 1U << LAPLACE_LOG_MINP;
    /* The minimum number of guaranteed representable energy deltas (in one
        direction). */
    private const int LAPLACE_NMIN = 16;

    /* When called, decay is positive and at most 11456. */
    private static uint ec_laplace_get_freq1(uint fs0, int decay)
    {
        uint ft;
        ft = 32768 - (LAPLACE_MINP * (2 * LAPLACE_NMIN)) - fs0;
        return (ft * (uint)(16384 - decay)) >> 15;
    }

    internal static int ec_laplace_decode(ref ec_dec dec, uint fs, int decay)
    {
        int val = 0;
        uint fl;
        uint fm;
        fm = dec.ec_decode_bin(15);
        fl = 0;
        if (fm >= fs)
        {
            val++;
            fl = fs;
            fs = ec_laplace_get_freq1(fs, decay) + LAPLACE_MINP;
            /* Search the decaying part of the PDF.*/
            while (fs > LAPLACE_MINP && fm >= fl + (2 * fs))
            {
                fs *= 2;
                fl += fs;
                fs = ((fs - (2 * LAPLACE_MINP)) * (uint)decay) >> 15;
                fs += LAPLACE_MINP;
                val++;
            }
            /* Everything beyond that has probability LAPLACE_MINP. */
            if (fs <= LAPLACE_MINP)
            {
                int di;
                di = (int)((fm - fl) >> (LAPLACE_LOG_MINP + 1));
                val += di;
                fl += (uint)(2 * di) * LAPLACE_MINP;
            }
            if (fm < fl + fs)
            {
                val = -val;
            }
            else
            {
                fl += fs;
            }
        }
        dec.ec_dec_update(fl, fl + fs < 32768U ? fl + fs : 32768U, 32768);
        return val;
    }
}
