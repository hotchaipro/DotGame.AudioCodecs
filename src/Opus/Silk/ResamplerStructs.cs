/***********************************************************************
Copyright (c) 2006-2011, Skype Limited. All rights reserved.
Ported to C# from libopus 1.6.1 (silk/resampler_structs.h) by Hot Chai Games LLC
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
using System.Runtime.InteropServices;

namespace DotGame.AudioCodecs.Opus.Silk;

/// <summary>The defines of silk/resampler_structs.h.</summary>
internal static class ResamplerStructs
{
    internal const int SILK_RESAMPLER_MAX_FIR_ORDER = 36;
    internal const int SILK_RESAMPLER_MAX_IIR_ORDER = 6;
}

/// <summary>
/// C's union { opus_int32 i32[ SILK_RESAMPLER_MAX_FIR_ORDER ]; opus_int16 i16[ SILK_RESAMPLER_MAX_FIR_ORDER ]; }:
/// i16 views the same memory as i32, as in C (little-endian).
/// </summary>
internal sealed class silk_resampler_sFIR
{
    internal readonly int[] i32 = new int[ResamplerStructs.SILK_RESAMPLER_MAX_FIR_ORDER];

    internal Span<short> i16 => MemoryMarshal.Cast<int, short>(this.i32.AsSpan());
}

internal sealed class silk_resampler_state_struct
{
    internal readonly int[] sIIR = new int[ResamplerStructs.SILK_RESAMPLER_MAX_IIR_ORDER]; /* this must be the first element of this struct */
    internal readonly silk_resampler_sFIR sFIR = new();
    internal readonly short[] delayBuf = new short[96];
    internal int resampler_function;
    internal int batchSize;
    internal int invRatio_Q16;
    internal int FIR_Order;
    internal int FIR_Fracs;
    internal int Fs_in_kHz;
    internal int Fs_out_kHz;
    internal int inputDelay;

    /* C: const opus_int16 *Coefs, NULL (empty here) until set */
    internal short[] Coefs = [];

    /// <summary>silk_memset( S, 0, sizeof( silk_resampler_state_struct ) ).</summary>
    internal void Clear()
    {
        Array.Clear(this.sIIR);
        Array.Clear(this.sFIR.i32);
        Array.Clear(this.delayBuf);
        this.resampler_function = 0;
        this.batchSize = 0;
        this.invRatio_Q16 = 0;
        this.FIR_Order = 0;
        this.FIR_Fracs = 0;
        this.Fs_in_kHz = 0;
        this.Fs_out_kHz = 0;
        this.inputDelay = 0;
        this.Coefs = [];
    }

    /// <summary>silk_memcpy( this, src, sizeof( silk_resampler_state_struct ) ).</summary>
    internal void CopyFrom(silk_resampler_state_struct src)
    {
        src.sIIR.AsSpan().CopyTo(this.sIIR);
        src.sFIR.i32.AsSpan().CopyTo(this.sFIR.i32);
        src.delayBuf.AsSpan().CopyTo(this.delayBuf);
        this.resampler_function = src.resampler_function;
        this.batchSize = src.batchSize;
        this.invRatio_Q16 = src.invRatio_Q16;
        this.FIR_Order = src.FIR_Order;
        this.FIR_Fracs = src.FIR_Fracs;
        this.Fs_in_kHz = src.Fs_in_kHz;
        this.Fs_out_kHz = src.Fs_out_kHz;
        this.inputDelay = src.inputDelay;
        this.Coefs = src.Coefs;
    }
}
