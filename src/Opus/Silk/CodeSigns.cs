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

[SkipLocalsInit]
internal static class CodeSigns
{
    /* Not in C: C decodes each sign with the two-entry iCDF { silk_sign_iCDF[ k ], 0 } built on its
       stack. ec_dec.ec_dec_icdf's table parameter isn't scoped, so a stackalloc'd table can't be
       passed with the ref ec_dec; these are the same two-entry tables, { silk_sign_iCDF[ k ], 0 } at
       2 * k, made once. */
    private static readonly byte[] silk_sign_iCDF_pairs = MakeSignPairs();

    private static byte[] MakeSignPairs()
    {
        byte[] pairs = new byte[2 * silk_sign_iCDF.Length];
        for (int k = 0; k < silk_sign_iCDF.Length; k++)
        {
            pairs[2 * k] = silk_sign_iCDF[k];
            pairs[(2 * k) + 1] = 0;
        }
        return pairs;
    }

    /* shifting avoids if-statement */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int silk_dec_map(int a) => silk_LSHIFT(a, 1) - 1;

    /* Decodes signs of excitation */
    internal static void silk_decode_signs(
        ref ec_dec psRangeDec,              /* I/O  Compressor data structure                   */
        scoped Span<short> pulses,          /* I/O  pulse signal                                */
        int length,                         /* I    length of input                             */
        int signalType,                     /* I    Signal type                                 */
        int quantOffsetType,                /* I    Quantization offset type                    */
        scoped ReadOnlySpan<int> sum_pulses) /* I    Sum of absolute pulses per block            */
    {
        int i, j, p;
        ReadOnlySpan<byte> icdf;
        scoped Span<short> q_ptr;
        int icdf_ptr;

        q_ptr = pulses;
        i = silk_SMULBB(7, silk_ADD_LSHIFT(quantOffsetType, signalType, 1));
        icdf_ptr = i;    /* C: icdf_ptr = &silk_sign_iCDF[ i ] */
        length = silk_RSHIFT(length + (SHELL_CODEC_FRAME_LENGTH / 2), LOG2_SHELL_CODEC_FRAME_LENGTH);
        for (i = 0; i < length; i++)
        {
            p = sum_pulses[i];
            if (p > 0)
            {
                /* C: icdf[ 0 ] = icdf_ptr[ silk_min( p & 0x1F, 6 ) ]; icdf[ 1 ] = 0 */
                icdf = silk_sign_iCDF_pairs.AsSpan(2 * (icdf_ptr + silk_min(p & 0x1F, 6)), 2);
                for (j = 0; j < SHELL_CODEC_FRAME_LENGTH; j++)
                {
                    if (q_ptr[j] > 0)
                    {
                        /* attach sign */
                        /* implementation with shift, subtraction, multiplication */
                        q_ptr[j] = (short)(q_ptr[j] * silk_dec_map(psRangeDec.ec_dec_icdf(icdf, 8)));
                    }
                }
            }
            q_ptr = q_ptr[SHELL_CODEC_FRAME_LENGTH..];
        }
    }
}
