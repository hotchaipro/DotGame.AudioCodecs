/***********************************************************************
Copyright (c) 2006-2011, Skype Limited. All rights reserved.
Ported to C# from libopus 1.6.1 (silk/fixed/vector_ops_FIX.c) by Hot Chai Games LLC
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
using DotGame.AudioCodecs.Opus.Celt;
using static DotGame.AudioCodecs.Opus.Silk.SigProcFix;

namespace DotGame.AudioCodecs.Opus.Silk;

internal static class VectorOpsFIX
{
    /* Copy and multiply a vector by a constant */
    internal static void silk_scale_copy_vector16(
        Span<short> data_out,
        ReadOnlySpan<short> data_in,
        int gain_Q16,                   /* I    Gain in Q16                                                 */
        int dataSize)                   /* I    Length                                                      */
    {
        int i;
        int tmp32;

        for (i = 0; i < dataSize; i++)
        {
            tmp32 = silk_SMULWB(gain_Q16, data_in[i]);
            data_out[i] = (short)tmp32; /* C: silk_CHECK_FIT16( tmp32 ) */
        }
    }

    /* Multiply a vector by a constant */
    internal static void silk_scale_vector32_Q26_lshift_18(
        Span<int> data1,                /* I/O  Q0/Q18                                                      */
        int gain_Q26,                   /* I    Q26                                                         */
        int dataSize)                   /* I    length                                                      */
    {
        int i;

        for (i = 0; i < dataSize; i++)
        {
            data1[i] = (int)silk_RSHIFT64(silk_SMULL(data1[i], gain_Q26), 8);    /* OUTPUT: Q18 */
        }
    }

    /* sum = for(i=0;i<len;i++)inVec1[i]*inVec2[i];      ---        inner product   */
    internal static int silk_inner_prod_aligned(
        ReadOnlySpan<short> inVec1,     /*    I input vector 1                                              */
        ReadOnlySpan<short> inVec2,     /*    I input vector 2                                              */
        int len)                        /*    I vector lengths                                              */
    {
        /* C: #ifdef FIXED_POINT */
        return Pitch.celt_inner_prod(inVec1, inVec2, len);
    }

    internal static long silk_inner_prod16(
        ReadOnlySpan<short> inVec1,     /*    I input vector 1                                              */
        ReadOnlySpan<short> inVec2,     /*    I input vector 2                                              */
        int len)                        /*    I vector lengths                                              */
    {
        int i;
        long sum = 0;

        for (i = 0; i < len; i++)
        {
            sum = silk_SMLALBB(sum, inVec1[i], inVec2[i]);
        }
        return sum;
    }
}
