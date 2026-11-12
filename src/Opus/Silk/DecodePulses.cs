/***********************************************************************
Copyright (c) 2006-2011, Skype Limited. All rights reserved.
Ported to C# from libopus 1.6.1 (silk/decode_pulses.c) by Hot Chai Games LLC
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
using static DotGame.AudioCodecs.Opus.Silk.CodeSigns;
using static DotGame.AudioCodecs.Opus.Silk.Define;
using static DotGame.AudioCodecs.Opus.Silk.ShellCoder;
using static DotGame.AudioCodecs.Opus.Silk.SigProcFix;
using static DotGame.AudioCodecs.Opus.Silk.TablesOther;
using static DotGame.AudioCodecs.Opus.Silk.TablesPulsesPerBlock;

namespace DotGame.AudioCodecs.Opus.Silk;

[SkipLocalsInit]
internal static class DecodePulses
{
    private const int PULSES_PER_BLOCK_ICDF_ROW = SILK_MAX_PULSES + 2;
    private const int RATE_LEVELS_ICDF_ROW = N_RATE_LEVELS - 1;

    /*********************************************/
    /* Decode quantization indices of excitation */
    /*********************************************/
    internal static void silk_decode_pulses(
        ref ec_dec psRangeDec,          /* I/O  Compressor data structure                   */
        scoped Span<short> pulses,      /* O    Excitation signal                           */
        int signalType,                 /* I    Sigtype                                     */
        int quantOffsetType,            /* I    quantOffsetType                             */
        int frame_length)               /* I    Frame length                                */
    {
        int i, j, k, iter, abs_q, nLS, RateLevelIndex;
        Span<int> sum_pulses = stackalloc int[MAX_NB_SHELL_BLOCKS];
        Span<int> nLshifts = stackalloc int[MAX_NB_SHELL_BLOCKS];
        scoped Span<short> pulses_ptr;
        ReadOnlySpan<byte> cdf_ptr;

        /*********************/
        /* Decode rate level */
        /*********************/
        RateLevelIndex = psRangeDec.ec_dec_icdf(silk_rate_levels_iCDF.AsSpan((signalType >> 1) * RATE_LEVELS_ICDF_ROW, RATE_LEVELS_ICDF_ROW), 8);

        /* Calculate number of shell blocks */
        Debug.Assert(1 << LOG2_SHELL_CODEC_FRAME_LENGTH == SHELL_CODEC_FRAME_LENGTH);
        iter = silk_RSHIFT(frame_length, LOG2_SHELL_CODEC_FRAME_LENGTH);
        if (iter * SHELL_CODEC_FRAME_LENGTH < frame_length)
        {
            Debug.Assert(frame_length == 12 * 10); /* Make sure only happens for 10 ms @ 12 kHz */
            iter++;
        }

        /***************************************************/
        /* Sum-Weighted-Pulses Decoding                    */
        /***************************************************/
        cdf_ptr = silk_pulses_per_block_iCDF.AsSpan(RateLevelIndex * PULSES_PER_BLOCK_ICDF_ROW, PULSES_PER_BLOCK_ICDF_ROW);
        for (i = 0; i < iter; i++)
        {
            nLshifts[i] = 0;
            sum_pulses[i] = psRangeDec.ec_dec_icdf(cdf_ptr, 8);

            /* LSB indication */
            while (sum_pulses[i] == SILK_MAX_PULSES + 1)
            {
                nLshifts[i]++;
                /* When we've already got 10 LSBs, we shift the table to not allow (SILK_MAX_PULSES + 1) */
                sum_pulses[i] = psRangeDec.ec_dec_icdf(
                        silk_pulses_per_block_iCDF.AsSpan(((N_RATE_LEVELS - 1) * PULSES_PER_BLOCK_ICDF_ROW) + (nLshifts[i] == 10 ? 1 : 0)), 8);
            }
        }

        /***************************************************/
        /* Shell decoding                                  */
        /***************************************************/
        for (i = 0; i < iter; i++)
        {
            if (sum_pulses[i] > 0)
            {
                silk_shell_decoder(pulses[silk_SMULBB(i, SHELL_CODEC_FRAME_LENGTH)..], ref psRangeDec, sum_pulses[i]);
            }
            else
            {
                pulses.Slice(silk_SMULBB(i, SHELL_CODEC_FRAME_LENGTH), SHELL_CODEC_FRAME_LENGTH).Clear();
            }
        }

        /***************************************************/
        /* LSB Decoding                                    */
        /***************************************************/
        for (i = 0; i < iter; i++)
        {
            if (nLshifts[i] > 0)
            {
                nLS = nLshifts[i];
                pulses_ptr = pulses[silk_SMULBB(i, SHELL_CODEC_FRAME_LENGTH)..];
                for (k = 0; k < SHELL_CODEC_FRAME_LENGTH; k++)
                {
                    abs_q = pulses_ptr[k];
                    for (j = 0; j < nLS; j++)
                    {
                        abs_q = silk_LSHIFT(abs_q, 1);
                        abs_q += psRangeDec.ec_dec_icdf(silk_lsb_iCDF, 8);
                    }
                    pulses_ptr[k] = (short)abs_q;
                }
                /* Mark the number of pulses non-zero for sign decoding. */
                sum_pulses[i] |= nLS << 5;
            }
        }

        /****************************************/
        /* Decode and add signs to pulse signal */
        /****************************************/
        silk_decode_signs(ref psRangeDec, pulses, frame_length, signalType, quantOffsetType, sum_pulses);
    }
}
