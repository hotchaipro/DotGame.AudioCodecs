/***********************************************************************
Copyright (c) 2006-2011, Skype Limited. All rights reserved.
Ported to C# from libopus 1.6.1 (silk/code_signs.c) by Hot Chai Games LLC
Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions
are met:
- Redistributions of source code must retain the above copyright notice,
this list of conditions and the following disclaimer.
- Redistributions in binary form must reproduce the above copyright
notice, this list of conditions and the following disclaimer in the
documentation and/or other materials provided with the distribution.
- Neither the name of Internet Society, IETF or IETF Trust, nor the
names of specific contributors, may be used to endorse or promote
products derived from this software without specific prior written
permission.
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
POSSIBILITY OF SUCH DAMAGE.
***********************************************************************/

using System;
using System.Runtime.CompilerServices;
using DotGame.AudioCodecs.Opus.Common;
using static DotGame.AudioCodecs.Opus.Silk.Define;
using static DotGame.AudioCodecs.Opus.Silk.SigProcFix;
using static DotGame.AudioCodecs.Opus.Silk.TablesPulsesPerBlock;

namespace DotGame.AudioCodecs.Opus.Silk;

/// <summary>The encoder half of silk/code_signs.c (the decoder's is in <see cref="CodeSigns"/>).</summary>
[SkipLocalsInit]
internal static class CodeSignsEnc
{
    /* shifting avoids if-statement */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int silk_enc_map(int a) => silk_RSHIFT(a, 15) + 1;

    /* Encodes signs of excitation */
    internal static void silk_encode_signs(
        ref ec_enc psRangeEnc,              /* I/O  Compressor data structure                   */
        scoped ReadOnlySpan<sbyte> pulses,         /* I    pulse signal                                */
        int length,                         /* I    length of input                             */
        int signalType,                     /* I    Signal type                                 */
        int quantOffsetType,                /* I    Quantization offset type                    */
        scoped ReadOnlySpan<int> sum_pulses)       /* I    Sum of absolute pulses per block            */
    {
        int i, j, p;
        Span<byte> icdf = stackalloc byte[2];
        scoped ReadOnlySpan<sbyte> q_ptr;
        ReadOnlySpan<byte> icdf_ptr;

        icdf[1] = 0;
        q_ptr = pulses;
        i = silk_SMULBB(7, silk_ADD_LSHIFT(quantOffsetType, signalType, 1));
        icdf_ptr = silk_sign_iCDF.AsSpan(i);
        length = silk_RSHIFT(length + (SHELL_CODEC_FRAME_LENGTH / 2), LOG2_SHELL_CODEC_FRAME_LENGTH);
        for (i = 0; i < length; i++)
        {
            p = sum_pulses[i];
            if (p > 0)
            {
                icdf[0] = icdf_ptr[silk_min(p & 0x1F, 6)];
                for (j = 0; j < SHELL_CODEC_FRAME_LENGTH; j++)
                {
                    if (q_ptr[j] != 0)
                    {
                        psRangeEnc.ec_enc_icdf(silk_enc_map(q_ptr[j]), icdf, 8);
                    }
                }
            }
            q_ptr = q_ptr[SHELL_CODEC_FRAME_LENGTH..];
        }
    }
}
