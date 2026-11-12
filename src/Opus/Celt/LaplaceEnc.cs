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

using System.Diagnostics;
using DotGame.AudioCodecs.Opus.Common;
using static DotGame.AudioCodecs.Opus.Celt.Arch;

namespace DotGame.AudioCodecs.Opus.Celt;

/// <summary>
/// The encoder side of celt/laplace.c: the Laplace-like distribution of the coarse energy.
/// </summary>
internal static class LaplaceEnc
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

    internal static void ec_laplace_encode(ref ec_enc enc, ref int value, uint fs, int decay)
    {
        uint fl;
        int val = value;
        fl = 0;
        if (val != 0)
        {
            int s;
            int i;
            s = -(val < 0 ? 1 : 0);
            val = (val + s) ^ s;
            fl = fs;
            fs = ec_laplace_get_freq1(fs, decay);
            /* Search the decaying part of the PDF.*/
            for (i = 1; fs > 0 && i < val; i++)
            {
                fs *= 2;
                fl += fs + (2 * LAPLACE_MINP);
                fs = (fs * (uint)decay) >> 15;
            }
            /* Everything beyond that has probability LAPLACE_MINP. */
            if (fs == 0)
            {
                int di;
                int ndi_max;
                ndi_max = (int)((32768 - fl + LAPLACE_MINP - 1) >> LAPLACE_LOG_MINP);
                ndi_max = (ndi_max - s) >> 1;
                di = IMIN(val - i, ndi_max - 1);
                fl += (uint)(((2 * di) + 1 + s) * (int)LAPLACE_MINP);
                /* C's IMIN(LAPLACE_MINP, 32768-fl) compares as unsigned. */
                fs = LAPLACE_MINP < 32768 - fl ? LAPLACE_MINP : 32768 - fl;
                value = (i + di + s) ^ s;
            }
            else
            {
                fs += LAPLACE_MINP;
                fl += fs & ~(uint)s;
            }
            Debug.Assert(fl + fs <= 32768);
            Debug.Assert(fs > 0);
        }
        enc.ec_encode_bin(fl, fl + fs, 15);
    }
}
