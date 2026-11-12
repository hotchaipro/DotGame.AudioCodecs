/*Copyright (c) 2003-2004, Mark Borgerding
  Lots of modifications by Jean-Marc Valin
  Copyright (c) 2005-2007, Xiph.Org Foundation
  Copyright (c) 2008,      Xiph.Org Foundation, CSIRO
  Ported to C# from libopus 1.6.1 (celt/kiss_fft.c, celt/_kiss_fft_guts.h) by Hot Chai Games LLC

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
using System.Diagnostics;
using System.Runtime.CompilerServices;
using static DotGame.AudioCodecs.Opus.Celt.Arch;

namespace DotGame.AudioCodecs.Opus.Celt;

/// <summary>
/// The encoder's parts of celt/kiss_fft.c: the forward FFT with its input scaling and bit
/// reversal (opus_fft_c, used by the tonality analysis) and the inverse (opus_ifft_c). The
/// butterflies are the decoder's (<see cref="KissFft.opus_fft_impl"/>).
/// </summary>
internal static class KissFftEnc
{
    /* _kiss_fft_guts.h, FIXED_POINT without QEXT. */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int S_MUL2(int a, int b) => MULT16_32_Q16(b, a);

    internal static void opus_fft_c(kiss_fft_state st, ReadOnlySpan<kiss_fft_cpx> fin, Span<kiss_fft_cpx> fout)
    {
        int i;
        short scale;
        /* Allows us to scale with MULT16_32_Q16(), which is faster than
           MULT16_32_Q15() on ARM. */
        int scale_shift = st.scale_shift - 1;
        scale = st.scale;

        Debug.Assert(!fin.Overlaps(fout), "In-place FFT not supported");
        /* Bit-reverse the input */
        for (i = 0; i < st.nfft; i++)
        {
            kiss_fft_cpx x = fin[i];
            fout[st.bitrev[i]].r = S_MUL2(x.r, scale);
            fout[st.bitrev[i]].i = S_MUL2(x.i, scale);
        }
        KissFft.opus_fft_impl(st, fout, scale_shift);
    }

    internal static void opus_ifft_c(kiss_fft_state st, ReadOnlySpan<kiss_fft_cpx> fin, Span<kiss_fft_cpx> fout)
    {
        int i;
        Debug.Assert(!fin.Overlaps(fout), "In-place FFT not supported");
        /* Bit-reverse the input */
        for (i = 0; i < st.nfft; i++)
        {
            fout[st.bitrev[i]] = fin[i];
        }
        for (i = 0; i < st.nfft; i++)
        {
            fout[i].i = -fout[i].i;
        }
        KissFft.opus_fft_impl(st, fout, 0);
        for (i = 0; i < st.nfft; i++)
        {
            fout[i].i = -fout[i].i;
        }
    }
}
