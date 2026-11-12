/***********************************************************************
Copyright (c) 2006-2011, Skype Limited. All rights reserved.
Ported to C# from libopus 1.6.1 (silk/shell_coder.c) by Hot Chai Games LLC
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
using System.Diagnostics;
using System.Runtime.CompilerServices;
using DotGame.AudioCodecs.Opus.Common;
using static DotGame.AudioCodecs.Opus.Silk.Define;
using static DotGame.AudioCodecs.Opus.Silk.TablesPulsesPerBlock;

namespace DotGame.AudioCodecs.Opus.Silk;

/* shell coder; pulse-subframe length is hardcoded */
/// <summary>The encoder half of silk/shell_coder.c (the decoder's is in <see cref="ShellCoder"/>).</summary>
[SkipLocalsInit]
internal static class ShellCoderEnc
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void combine_pulses(
        Span<int> @out,                 /* O    combined pulses vector [len] */
        ReadOnlySpan<int> @in,          /* I    input vector       [2 * len] */
        int len)                        /* I    number of OUTPUT samples     */
    {
        int k;
        for (k = 0; k < len; k++)
        {
            @out[k] = @in[2 * k] + @in[(2 * k) + 1];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void encode_split(
        ref ec_enc psRangeEnc,          /* I/O  compressor data structure                   */
        int p_child1,                   /* I    pulse amplitude of first child subframe     */
        int p,                          /* I    pulse amplitude of current subframe         */
        scoped ReadOnlySpan<byte> shell_table) /* I    table of shell cdfs                         */
    {
        if (p > 0)
        {
            psRangeEnc.ec_enc_icdf(p_child1, shell_table[silk_shell_code_table_offsets[p]..], 8);
        }
    }

    /* Shell encoder, operates on one shell code frame of 16 pulses */
    internal static void silk_shell_encoder(
        ref ec_enc psRangeEnc,          /* I/O  compressor data structure                   */
        scoped ReadOnlySpan<int> pulses0)      /* I    data: nonnegative pulse amplitudes          */
    {
        Span<int> pulses1 = stackalloc int[8];
        Span<int> pulses2 = stackalloc int[4];
        Span<int> pulses3 = stackalloc int[2];
        Span<int> pulses4 = stackalloc int[1];

        /* this function operates on one shell code frame of 16 pulses */
        Debug.Assert(SHELL_CODEC_FRAME_LENGTH == 16);

        /* tree representation per pulse-subframe */
        combine_pulses(pulses1, pulses0, 8);
        combine_pulses(pulses2, pulses1, 4);
        combine_pulses(pulses3, pulses2, 2);
        combine_pulses(pulses4, pulses3, 1);

        encode_split(ref psRangeEnc, pulses3[0], pulses4[0], silk_shell_code_table3);

        encode_split(ref psRangeEnc, pulses2[0], pulses3[0], silk_shell_code_table2);

        encode_split(ref psRangeEnc, pulses1[0], pulses2[0], silk_shell_code_table1);
        encode_split(ref psRangeEnc, pulses0[0], pulses1[0], silk_shell_code_table0);
        encode_split(ref psRangeEnc, pulses0[2], pulses1[1], silk_shell_code_table0);

        encode_split(ref psRangeEnc, pulses1[2], pulses2[1], silk_shell_code_table1);
        encode_split(ref psRangeEnc, pulses0[4], pulses1[2], silk_shell_code_table0);
        encode_split(ref psRangeEnc, pulses0[6], pulses1[3], silk_shell_code_table0);

        encode_split(ref psRangeEnc, pulses2[2], pulses3[1], silk_shell_code_table2);

        encode_split(ref psRangeEnc, pulses1[4], pulses2[2], silk_shell_code_table1);
        encode_split(ref psRangeEnc, pulses0[8], pulses1[4], silk_shell_code_table0);
        encode_split(ref psRangeEnc, pulses0[10], pulses1[5], silk_shell_code_table0);

        encode_split(ref psRangeEnc, pulses1[6], pulses2[3], silk_shell_code_table1);
        encode_split(ref psRangeEnc, pulses0[12], pulses1[6], silk_shell_code_table0);
        encode_split(ref psRangeEnc, pulses0[14], pulses1[7], silk_shell_code_table0);
    }
}
