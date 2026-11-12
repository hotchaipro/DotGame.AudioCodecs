/* Copyright (c) 2007-2008 CSIRO
   Copyright (c) 2007-2009 Xiph.Org Foundation
   Copyright (c) 2008 Gregory Maxwell
   Written by Jean-Marc Valin and Gregory Maxwell
   Ported to C# from libopus 1.6.1 (celt/modes.c, celt/modes.h, celt/static_modes_fixed.h) by Hot Chai Games LLC */
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

namespace DotGame.AudioCodecs.Opus.Celt;

/* modes.h */
internal sealed class PulseCache
{
    internal int size;
    internal short[] index;
    internal byte[] bits;
    internal byte[] caps;

    internal PulseCache(int size, short[] index, byte[] bits, byte[] caps)
    {
        this.size = size;
        this.index = index;
        this.bits = bits;
        this.caps = caps;
    }
}

/* Mode definition (opaque)
 @brief Mode definition
 */
internal sealed class OpusCustomMode
{
    internal int Fs;
    internal int overlap;
    internal int nbEBands;
    internal int effEBands;
    internal short[] preemph;
    internal short[] eBands;   /*< Definition for each "pseudo-critical band" */
    internal int maxLM;
    internal int nbShortMdcts;
    internal int shortMdctSize;
    internal int nbAllocVectors; /*< Number of lines in the matrix below */
    internal byte[] allocVectors;   /*< Number of bits in each band for several rates */
    internal short[] logN;
    internal short[] window;
    internal mdct_lookup mdct;
    internal PulseCache cache;

    internal OpusCustomMode(int Fs, int overlap, int nbEBands, int effEBands, short[] preemph, short[] eBands,
        int maxLM, int nbShortMdcts, int shortMdctSize, int nbAllocVectors, byte[] allocVectors, short[] logN,
        short[] window, mdct_lookup mdct, PulseCache cache)
    {
        this.Fs = Fs;
        this.overlap = overlap;
        this.nbEBands = nbEBands;
        this.effEBands = effEBands;
        this.preemph = preemph;
        this.eBands = eBands;
        this.maxLM = maxLM;
        this.nbShortMdcts = nbShortMdcts;
        this.shortMdctSize = shortMdctSize;
        this.nbAllocVectors = nbAllocVectors;
        this.allocVectors = allocVectors;
        this.logN = logN;
        this.window = window;
        this.mdct = mdct;
        this.cache = cache;
    }
}

/// <summary>
/// celt/modes.c without CUSTOM_MODES: the band layout and allocation tables, and the one static
/// mode (48 kHz, 960 samples, 120 overlap) built from celt/static_modes_fixed.h's tables.
/// </summary>
internal static class Modes
{
    internal const int MAX_PERIOD = 1024;
    internal const int DEC_PITCH_BUF_SIZE = 2048;

    private const int OPUS_OK = 0;
    private const int OPUS_BAD_ARG = -1;

    internal static readonly short[] eband5ms =
    [
        /*0  200 400 600 800  1k 1.2 1.4 1.6  2k 2.4 2.8 3.2  4k 4.8 5.6 6.8  8k 9.6 12k 15.6 */
        0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 12, 14, 16, 20, 24, 28, 34, 40, 48, 60, 78, 100,
    ];

    /* Alternate tuning (partially derived from Vorbis) */
    internal const int BITALLOC_SIZE = 11;

    /* Bit allocation table in units of 1/32 bit/sample (0.1875 dB SNR) */
    internal static readonly byte[] band_allocation =
    [
        /*0  200 400 600 800  1k 1.2 1.4 1.6  2k 2.4 2.8 3.2  4k 4.8 5.6 6.8  8k 9.6 12k 15.6 */
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        90, 80, 75, 69, 63, 56, 49, 40, 34, 29, 20, 18, 10, 0, 0, 0, 0, 0, 0, 0, 0,
        110, 100, 90, 84, 78, 71, 65, 58, 51, 45, 39, 32, 26, 20, 12, 0, 0, 0, 0, 0, 0,
        118, 110, 103, 93, 86, 80, 75, 70, 65, 59, 53, 47, 40, 31, 23, 15, 4, 0, 0, 0, 0,
        126, 119, 112, 104, 95, 89, 83, 78, 72, 66, 60, 54, 47, 39, 32, 25, 17, 12, 1, 0, 0,
        134, 127, 120, 114, 103, 97, 91, 85, 78, 72, 66, 60, 54, 47, 41, 35, 29, 23, 16, 10, 1,
        144, 137, 130, 124, 113, 107, 101, 95, 88, 82, 76, 70, 64, 57, 51, 45, 39, 33, 26, 15, 1,
        152, 145, 138, 132, 123, 117, 111, 105, 98, 92, 86, 80, 74, 67, 61, 55, 49, 43, 36, 20, 1,
        162, 155, 148, 142, 133, 127, 121, 115, 108, 102, 96, 90, 84, 77, 71, 65, 59, 53, 46, 30, 1,
        172, 165, 158, 152, 143, 137, 131, 125, 118, 112, 106, 100, 94, 87, 81, 75, 69, 63, 56, 45, 20,
        200, 200, 200, 200, 200, 200, 200, 200, 198, 193, 188, 183, 178, 173, 168, 163, 158, 153, 148, 129, 104,
    ];

    /* static_modes_fixed.h's kiss_fft_state, mdct_lookup and CELTMode definitions. */
    private static readonly kiss_twiddle_cpx[] fft_twiddles48000_960 = make_twiddles(StaticModesFixed.fft_twiddles48000_960);

    private static readonly kiss_fft_state fft_state48000_960_0 = new kiss_fft_state(
        480,    /* nfft */
        17476,  /* scale */
        8,      /* scale_shift */
        -1,     /* shift */
        [5, 96, 3, 32, 4, 8, 2, 4, 4, 1, 0, 0, 0, 0, 0, 0],    /* factors */
        StaticModesFixed.fft_bitrev480,    /* bitrev */
        fft_twiddles48000_960);

    private static readonly kiss_fft_state fft_state48000_960_1 = new kiss_fft_state(
        240,    /* nfft */
        17476,  /* scale */
        7,      /* scale_shift */
        1,      /* shift */
        [5, 48, 3, 16, 4, 4, 4, 1, 0, 0, 0, 0, 0, 0, 0, 0],    /* factors */
        StaticModesFixed.fft_bitrev240,    /* bitrev */
        fft_twiddles48000_960);

    private static readonly kiss_fft_state fft_state48000_960_2 = new kiss_fft_state(
        120,    /* nfft */
        17476,  /* scale */
        6,      /* scale_shift */
        2,      /* shift */
        [5, 24, 3, 8, 2, 4, 4, 1, 0, 0, 0, 0, 0, 0, 0, 0],    /* factors */
        StaticModesFixed.fft_bitrev120,    /* bitrev */
        fft_twiddles48000_960);

    private static readonly kiss_fft_state fft_state48000_960_3 = new kiss_fft_state(
        60,     /* nfft */
        17476,  /* scale */
        5,      /* scale_shift */
        3,      /* shift */
        [5, 12, 3, 4, 4, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],    /* factors */
        StaticModesFixed.fft_bitrev60,    /* bitrev */
        fft_twiddles48000_960);

    private static readonly OpusCustomMode mode48000_960_120 = new OpusCustomMode(
        48000,    /* Fs */
        120,      /* overlap */
        21,       /* nbEBands */
        21,       /* effEBands */
        [27853, 0, 4096, 8192],    /* preemph */
        eband5ms,    /* eBands */
        3,        /* maxLM */
        8,        /* nbShortMdcts */
        120,      /* shortMdctSize */
        11,       /* nbAllocVectors */
        band_allocation,    /* allocVectors */
        StaticModesFixed.logN400,    /* logN */
        StaticModesFixed.window120,    /* window */
        new mdct_lookup(1920, 3, [fft_state48000_960_0, fft_state48000_960_1, fft_state48000_960_2, fft_state48000_960_3],
            StaticModesFixed.mdct_twiddles960),    /* mdct */
        new PulseCache(392, StaticModesFixed.cache_index50, StaticModesFixed.cache_bits50, StaticModesFixed.cache_caps50)); /* cache */

    /* List of all the available modes */
    private const int TOTAL_MODES = 1;
    private static readonly OpusCustomMode[] static_mode_list = [mode48000_960_120];

    private static kiss_twiddle_cpx[] make_twiddles(short[] ri)
    {
        kiss_twiddle_cpx[] tw = new kiss_twiddle_cpx[ri.Length / 2];
        for (int k = 0; k < tw.Length; k++)
        {
            tw[k].r = ri[2 * k];
            tw[k].i = ri[(2 * k) + 1];
        }
        return tw;
    }

    internal static OpusCustomMode? opus_custom_mode_create(int Fs, int frame_size, out int error)
    {
        int i;
        for (i = 0; i < TOTAL_MODES; i++)
        {
            int j;
            for (j = 0; j < 4; j++)
            {
                if (Fs == static_mode_list[i].Fs &&
                      (frame_size << j) == static_mode_list[i].shortMdctSize * static_mode_list[i].nbShortMdcts)
                {
                    error = OPUS_OK;
                    return static_mode_list[i];
                }
            }
        }
        error = OPUS_BAD_ARG;
        return null;
    }
}
