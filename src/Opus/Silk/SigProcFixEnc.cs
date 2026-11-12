/***********************************************************************
Copyright (c) 2006-2011, Skype Limited. All rights reserved.
Ported to C# from libopus 1.6.1 (silk/SigProc_FIX.h) by Hot Chai Games LLC
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

using System.Runtime.CompilerServices;
using static DotGame.AudioCodecs.Opus.Silk.SigProcFix;

namespace DotGame.AudioCodecs.Opus.Silk;

/// <summary>The macros of silk/SigProc_FIX.h that only the encoder uses (the rest are in <see cref="SigProcFix"/>).</summary>
internal static class SigProcFixEnc
{
    /* Macro to convert floating-point constants to fixed-point.
       C: ((opus_int32)((C) * ((opus_int64)1 << (Q)) + 0.5)). When C is a float, C multiplies in float, but
       2^Q is a power of two, so the float product is exact and equals the double product of the float's
       value: passing the float (as C's expression computes it, in float) to the double parameter gives
       the same result. */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int SILK_FIX_CONST(double C, int Q) => (int)((C * (1L << Q)) + 0.5);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long silk_ADD_SAT64(long a, long b)
    {
        return ((ulong)(a + b) & 0x8000000000000000UL) == 0
            ? (((ulong)(a & b) & 0x8000000000000000UL) != 0 ? silk_int64_MIN : a + b)
            : (((ulong)(a | b) & 0x8000000000000000UL) == 0 ? silk_int64_MAX : a + b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long silk_SUB_SAT64(long a, long b)
    {
        return ((ulong)(a - b) & 0x8000000000000000UL) == 0
            ? (((ulong)a & ((ulong)b ^ 0x8000000000000000UL) & 0x8000000000000000UL) != 0 ? silk_int64_MIN : a - b)
            : ((((ulong)a ^ 0x8000000000000000UL) & (ulong)b & 0x8000000000000000UL) != 0 ? silk_int64_MAX : a - b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_NSHIFT_MUL_32_32(int a, int b) => -(31 - ((32 - silk_CLZ32(silk_abs(a))) + (32 - silk_CLZ32(silk_abs(b)))));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_NSHIFT_MUL_16_16(int a, int b) => -(15 - ((16 - silk_CLZ16((short)silk_abs(a))) + (16 - silk_CLZ16((short)silk_abs(b)))));
}
