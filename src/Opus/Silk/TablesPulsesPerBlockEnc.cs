/***********************************************************************
Copyright (c) 2006-2011, Skype Limited. All rights reserved.
Ported to C# from libopus 1.6.1 (silk/tables_pulses_per_block.c) by Hot Chai Games LLC
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

namespace DotGame.AudioCodecs.Opus.Silk;

/// <summary>The tables of silk/tables_pulses_per_block.c that only the encoder uses.</summary>
internal static class TablesPulsesPerBlockEnc
{
    internal static readonly byte[] silk_max_pulses_table =
    [
        8, 10, 12, 16,
    ];

    /* C: opus_uint8 silk_pulses_per_block_BITS_Q5[ 9 ][ 18 ], flattened row by row */
    internal static readonly byte[] silk_pulses_per_block_BITS_Q5 =
    [
        31, 57, 107, 160, 205, 205, 255, 255,
        255, 255, 255, 255, 255, 255, 255, 255,
        255, 255,
        69, 47, 67, 111, 166, 205, 255, 255,
        255, 255, 255, 255, 255, 255, 255, 255,
        255, 255,
        82, 74, 79, 95, 109, 128, 145, 160,
        173, 205, 205, 205, 224, 255, 255, 224,
        255, 224,
        125, 74, 59, 69, 97, 141, 182, 255,
        255, 255, 255, 255, 255, 255, 255, 255,
        255, 255,
        173, 115, 85, 73, 76, 92, 115, 145,
        173, 205, 224, 224, 255, 255, 255, 255,
        255, 255,
        166, 134, 113, 102, 101, 102, 107, 118,
        125, 138, 145, 155, 166, 182, 192, 192,
        205, 150,
        224, 182, 134, 101, 83, 79, 85, 97,
        120, 145, 173, 205, 224, 255, 255, 255,
        255, 255,
        255, 224, 192, 150, 120, 101, 92, 89,
        93, 102, 118, 134, 160, 182, 192, 224,
        224, 224,
        255, 224, 224, 182, 155, 134, 118, 109,
        104, 102, 106, 111, 118, 131, 145, 160,
        173, 131,
    ];

    /* C: opus_uint8 silk_rate_levels_BITS_Q5[ 2 ][ 9 ], flattened row by row */
    internal static readonly byte[] silk_rate_levels_BITS_Q5 =
    [
        131, 74, 141, 79, 80, 138, 95, 104,
        134,
        95, 99, 91, 125, 93, 76, 123, 115,
        123,
    ];
}
