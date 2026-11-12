/***********************************************************************
Copyright (c) 2006-2011, Skype Limited. All rights reserved.
Ported to C# from libopus 1.6.1 (silk/tables_LTP.c) by Hot Chai Games LLC
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

/// <summary>The tables of silk/tables_LTP.c that only the encoder uses (the rest are in <see cref="TablesLTP"/>).</summary>
internal static class TablesLTPEnc
{
    private static readonly byte[] silk_LTP_gain_BITS_Q5_0 =
    [
        15, 131, 138, 138, 155, 155, 173, 173,
    ];

    private static readonly byte[] silk_LTP_gain_BITS_Q5_1 =
    [
        69, 93, 115, 118, 131, 138, 141, 138,
        150, 150, 155, 150, 155, 160, 166, 160,
    ];

    private static readonly byte[] silk_LTP_gain_BITS_Q5_2 =
    [
        131, 128, 134, 141, 141, 141, 145, 145,
        145, 150, 155, 155, 155, 155, 160, 160,
        160, 160, 166, 166, 173, 173, 182, 192,
        182, 192, 192, 192, 205, 192, 205, 224,
    ];

    internal static readonly byte[][] silk_LTP_gain_BITS_Q5_ptrs =
    [
        silk_LTP_gain_BITS_Q5_0,
        silk_LTP_gain_BITS_Q5_1,
        silk_LTP_gain_BITS_Q5_2,
    ];

    /* Maximum frequency-dependent response of the pitch taps above,
       computed as max(abs(freqz(taps))) */
    private static readonly byte[] silk_LTP_gain_vq_0_gain =
    [
        46, 2, 90, 87, 93, 91, 82, 98,
    ];

    private static readonly byte[] silk_LTP_gain_vq_1_gain =
    [
        109, 120, 118, 12, 113, 115, 117, 119,
        99, 59, 87, 111, 63, 111, 112, 80,
    ];

    private static readonly byte[] silk_LTP_gain_vq_2_gain =
    [
        126, 124, 125, 124, 129, 121, 126, 23,
        132, 127, 127, 127, 126, 127, 122, 133,
        130, 134, 101, 118, 119, 145, 126, 86,
        124, 120, 123, 119, 170, 173, 107, 109,
    ];

    internal static readonly byte[][] silk_LTP_vq_gain_ptrs_Q7 =
    [
        silk_LTP_gain_vq_0_gain,
        silk_LTP_gain_vq_1_gain,
        silk_LTP_gain_vq_2_gain,
    ];

    internal static readonly sbyte[] silk_LTP_vq_sizes =
    [
        8, 16, 32,
    ];
}
