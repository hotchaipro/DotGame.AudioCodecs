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
[SkipLocalsInit]
internal static class ShellCoder
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void decode_split(
        ref short p_child1,             /* O    pulse amplitude of first child subframe     */
        ref short p_child2,             /* O    pulse amplitude of second child subframe    */
        ref ec_dec psRangeDec,          /* I/O  Compressor data structure                   */
        int p,                          /* I    pulse amplitude of current subframe         */
        ReadOnlySpan<byte> shell_table) /* I    table of shell cdfs                         */
    {
        if (p > 0)
        {
            p_child1 = (short)psRangeDec.ec_dec_icdf(shell_table[silk_shell_code_table_offsets[p]..], 8);
            p_child2 = (short)(p - p_child1);
        }
        else
        {
            p_child1 = 0;
            p_child2 = 0;
        }
    }

    /* Shell decoder, operates on one shell code frame of 16 pulses */
    internal static void silk_shell_decoder(
        scoped Span<short> pulses0,            /* O    data: nonnegative pulse amplitudes          */
        ref ec_dec psRangeDec,          /* I/O  Compressor data structure                   */
        int pulses4)                    /* I    number of pulses per pulse-subframe         */
    {
        Span<short> pulses3 = stackalloc short[2];
        Span<short> pulses2 = stackalloc short[4];
        Span<short> pulses1 = stackalloc short[8];

        /* this function operates on one shell code frame of 16 pulses */
        Debug.Assert(SHELL_CODEC_FRAME_LENGTH == 16);

        decode_split(ref pulses3[0], ref pulses3[1], ref psRangeDec, pulses4, silk_shell_code_table3);

        decode_split(ref pulses2[0], ref pulses2[1], ref psRangeDec, pulses3[0], silk_shell_code_table2);

        decode_split(ref pulses1[0], ref pulses1[1], ref psRangeDec, pulses2[0], silk_shell_code_table1);
        decode_split(ref pulses0[0], ref pulses0[1], ref psRangeDec, pulses1[0], silk_shell_code_table0);
        decode_split(ref pulses0[2], ref pulses0[3], ref psRangeDec, pulses1[1], silk_shell_code_table0);

        decode_split(ref pulses1[2], ref pulses1[3], ref psRangeDec, pulses2[1], silk_shell_code_table1);
        decode_split(ref pulses0[4], ref pulses0[5], ref psRangeDec, pulses1[2], silk_shell_code_table0);
        decode_split(ref pulses0[6], ref pulses0[7], ref psRangeDec, pulses1[3], silk_shell_code_table0);

        decode_split(ref pulses2[2], ref pulses2[3], ref psRangeDec, pulses3[1], silk_shell_code_table2);

        decode_split(ref pulses1[4], ref pulses1[5], ref psRangeDec, pulses2[2], silk_shell_code_table1);
        decode_split(ref pulses0[8], ref pulses0[9], ref psRangeDec, pulses1[4], silk_shell_code_table0);
        decode_split(ref pulses0[10], ref pulses0[11], ref psRangeDec, pulses1[5], silk_shell_code_table0);

        decode_split(ref pulses1[6], ref pulses1[7], ref psRangeDec, pulses2[3], silk_shell_code_table1);
        decode_split(ref pulses0[12], ref pulses0[13], ref psRangeDec, pulses1[6], silk_shell_code_table0);
        decode_split(ref pulses0[14], ref pulses0[15], ref psRangeDec, pulses1[7], silk_shell_code_table0);
    }
}
