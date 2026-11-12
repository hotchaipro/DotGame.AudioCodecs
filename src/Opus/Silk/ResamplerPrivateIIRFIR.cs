/***********************************************************************
Copyright (c) 2006-2011, Skype Limited. All rights reserved.
Ported to C# from libopus 1.6.1 (silk/resampler_private_IIR_FIR.c) by Hot Chai Games LLC
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
using static DotGame.AudioCodecs.Opus.Silk.ResamplerPrivate;
using static DotGame.AudioCodecs.Opus.Silk.ResamplerPrivateUp2HQ;
using static DotGame.AudioCodecs.Opus.Silk.ResamplerRom;
using static DotGame.AudioCodecs.Opus.Silk.SigProcFix;

namespace DotGame.AudioCodecs.Opus.Silk;

[SkipLocalsInit]
internal static class ResamplerPrivateIIRFIR
{
    private const int FIR_12_ROW = RESAMPLER_ORDER_FIR_12 / 2;

    /* C returns the advanced out pointer; this returns out advanced past the samples written */
    private static Span<short> silk_resampler_private_IIR_FIR_INTERPOL(
        Span<short> @out,
        scoped ReadOnlySpan<short> buf,
        int max_index_Q16,
        int index_increment_Q16)
    {
        int index_Q16, res_Q15;
        scoped ReadOnlySpan<short> buf_ptr;
        int table_index;

        /* Interpolate upsampled signal and store in output array */
        for (index_Q16 = 0; index_Q16 < max_index_Q16; index_Q16 += index_increment_Q16)
        {
            table_index = silk_SMULWB(index_Q16 & 0xFFFF, 12);
            buf_ptr = buf[(index_Q16 >> 16)..];

            /* C: silk_resampler_frac_FIR_12[ t ][ m ] is silk_resampler_frac_FIR_12[ t * 4 + m ] */
            res_Q15 = silk_SMULBB(buf_ptr[0], silk_resampler_frac_FIR_12[(table_index * FIR_12_ROW) + 0]);
            res_Q15 = silk_SMLABB(res_Q15, buf_ptr[1], silk_resampler_frac_FIR_12[(table_index * FIR_12_ROW) + 1]);
            res_Q15 = silk_SMLABB(res_Q15, buf_ptr[2], silk_resampler_frac_FIR_12[(table_index * FIR_12_ROW) + 2]);
            res_Q15 = silk_SMLABB(res_Q15, buf_ptr[3], silk_resampler_frac_FIR_12[(table_index * FIR_12_ROW) + 3]);
            res_Q15 = silk_SMLABB(res_Q15, buf_ptr[4], silk_resampler_frac_FIR_12[((11 - table_index) * FIR_12_ROW) + 3]);
            res_Q15 = silk_SMLABB(res_Q15, buf_ptr[5], silk_resampler_frac_FIR_12[((11 - table_index) * FIR_12_ROW) + 2]);
            res_Q15 = silk_SMLABB(res_Q15, buf_ptr[6], silk_resampler_frac_FIR_12[((11 - table_index) * FIR_12_ROW) + 1]);
            res_Q15 = silk_SMLABB(res_Q15, buf_ptr[7], silk_resampler_frac_FIR_12[((11 - table_index) * FIR_12_ROW) + 0]);
            @out[0] = (short)silk_SAT16(silk_RSHIFT_ROUND(res_Q15, 15));
            @out = @out[1..];
        }
        return @out;
    }

    /* Upsample using a combination of allpass-based 2x upsampling and FIR interpolation */
    internal static void silk_resampler_private_IIR_FIR(
        silk_resampler_state_struct S,  /* I/O  Resampler state             */
        Span<short> @out,               /* O    Output signal               */
        ReadOnlySpan<short> @in,        /* I    Input signal                */
        int inLen)                      /* I    Number of input samples     */
    {
        int nSamplesIn;
        int max_index_Q16, index_increment_Q16;

        /* C: ALLOC( buf, 2 * S->batchSize + RESAMPLER_ORDER_FIR_12, opus_int16 ), at most this size */
        Span<short> buf = stackalloc short[(2 * RESAMPLER_MAX_BATCH_SIZE_IN) + RESAMPLER_ORDER_FIR_12];
        buf = buf[..((2 * S.batchSize) + RESAMPLER_ORDER_FIR_12)];

        /* Copy buffered samples to start of buffer */
        S.sFIR.i16[..RESAMPLER_ORDER_FIR_12].CopyTo(buf);

        /* Iterate over blocks of frameSizeIn input samples */
        index_increment_Q16 = S.invRatio_Q16;
        while (true)
        {
            nSamplesIn = silk_min(inLen, S.batchSize);

            /* Upsample 2x */
            silk_resampler_private_up2_HQ(S.sIIR, buf[RESAMPLER_ORDER_FIR_12..], @in, nSamplesIn);

            max_index_Q16 = silk_LSHIFT32(nSamplesIn, 16 + 1);         /* + 1 because 2x upsampling */
            @out = silk_resampler_private_IIR_FIR_INTERPOL(@out, buf, max_index_Q16, index_increment_Q16);
            @in = @in[nSamplesIn..];
            inLen -= nSamplesIn;

            if (inLen > 0)
            {
                /* More iterations to do; copy last part of filtered signal to beginning of buffer */
                buf.Slice(nSamplesIn << 1, RESAMPLER_ORDER_FIR_12).CopyTo(buf);
            }
            else
            {
                break;
            }
        }

        /* Copy last part of filtered signal to the state for the next call */
        buf.Slice(nSamplesIn << 1, RESAMPLER_ORDER_FIR_12).CopyTo(S.sFIR.i16);
    }
}
