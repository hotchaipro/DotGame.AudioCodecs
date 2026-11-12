/***********************************************************************
Copyright (c) 2006-2011, Skype Limited. All rights reserved.
Ported to C# from libopus 1.6.1 (silk/encode_pulses.c) by Hot Chai Games LLC
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
using static DotGame.AudioCodecs.Opus.Silk.CodeSignsEnc;
using static DotGame.AudioCodecs.Opus.Silk.ShellCoderEnc;
using static DotGame.AudioCodecs.Opus.Silk.TablesPulsesPerBlockEnc;
using static DotGame.AudioCodecs.Opus.Silk.Define;
using static DotGame.AudioCodecs.Opus.Silk.SigProcFix;
using static DotGame.AudioCodecs.Opus.Silk.TablesOther;
using static DotGame.AudioCodecs.Opus.Silk.TablesPulsesPerBlock;

namespace DotGame.AudioCodecs.Opus.Silk;

[SkipLocalsInit]
internal static class EncodePulses
{
    /* C: the row lengths of silk_pulses_per_block_iCDF[ 10 ][ 18 ], silk_pulses_per_block_BITS_Q5[ 9 ][ 18 ]
       and silk_rate_levels_iCDF/BITS_Q5[ 2 ][ 9 ], which the ports flatten */
    private const int PULSES_ROW = SILK_MAX_PULSES + 2;
    private const int RATE_LEVELS_ROW = N_RATE_LEVELS - 1;

    /*********************************************/
    /* Encode quantization indices of excitation */
    /*********************************************/
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int combine_and_check(   /* return ok                           */
        Span<int> pulses_comb,              /* O                                   */
        ReadOnlySpan<int> pulses_in,        /* I                                   */
        int max_pulses,                     /* I    max value for sum of pulses    */
        int len)                            /* I    number of output values        */
    {
        int k, sum;

        for (k = 0; k < len; k++)
        {
            sum = pulses_in[2 * k] + pulses_in[(2 * k) + 1];
            if (sum > max_pulses)
            {
                return 1;
            }
            pulses_comb[k] = sum;
        }

        return 0;
    }

    /* Encode quantization indices of excitation */
    internal static void silk_encode_pulses(
        ref ec_enc psRangeEnc,          /* I/O  compressor data structure                   */
        int signalType,                 /* I    Signal type                                 */
        int quantOffsetType,            /* I    quantOffsetType                             */
        scoped Span<sbyte> pulses,      /* I    quantization indices                        */
        int frame_length)               /* I    Frame length                                */
    {
        int i, k, j, iter, bit, nLS, scale_down, RateLevelIndex = 0;
        int abs_q, minSumBits_Q5, sumBits_Q5;
        scoped Span<int> abs_pulses;
        scoped Span<int> sum_pulses;
        scoped Span<int> nRshifts;
        Span<int> pulses_comb = stackalloc int[8];
        scoped Span<int> abs_pulses_ptr;
        scoped ReadOnlySpan<sbyte> pulses_ptr;
        ReadOnlySpan<byte> cdf_ptr;
        ReadOnlySpan<byte> nBits_ptr;

        pulses_comb.Clear(); /* Fixing Valgrind reported problem*/

        /****************************/
        /* Prepare for shell coding */
        /****************************/
        /* Calculate number of shell blocks */
        Debug.Assert(1 << LOG2_SHELL_CODEC_FRAME_LENGTH == SHELL_CODEC_FRAME_LENGTH);
        iter = silk_RSHIFT(frame_length, LOG2_SHELL_CODEC_FRAME_LENGTH);
        if (iter * SHELL_CODEC_FRAME_LENGTH < frame_length)
        {
            Debug.Assert(frame_length == 12 * 10); /* Make sure only happens for 10 ms @ 12 kHz */
            iter++;
            pulses.Slice(frame_length, SHELL_CODEC_FRAME_LENGTH).Clear();
        }

        /* Take the absolute value of the pulses */
        abs_pulses = stackalloc int[iter * SHELL_CODEC_FRAME_LENGTH];
        Debug.Assert((SHELL_CODEC_FRAME_LENGTH & 3) == 0);
        for (i = 0; i < iter * SHELL_CODEC_FRAME_LENGTH; i += 4)
        {
            abs_pulses[i + 0] = silk_abs(pulses[i + 0]);
            abs_pulses[i + 1] = silk_abs(pulses[i + 1]);
            abs_pulses[i + 2] = silk_abs(pulses[i + 2]);
            abs_pulses[i + 3] = silk_abs(pulses[i + 3]);
        }

        /* Calc sum pulses per shell code frame */
        sum_pulses = stackalloc int[iter];
        nRshifts = stackalloc int[iter];
        abs_pulses_ptr = abs_pulses;
        for (i = 0; i < iter; i++)
        {
            nRshifts[i] = 0;

            while (true)
            {
                /* 1+1 -> 2 */
                scale_down = combine_and_check(pulses_comb, abs_pulses_ptr, silk_max_pulses_table[0], 8);
                /* 2+2 -> 4 */
                scale_down += combine_and_check(pulses_comb, pulses_comb, silk_max_pulses_table[1], 4);
                /* 4+4 -> 8 */
                scale_down += combine_and_check(pulses_comb, pulses_comb, silk_max_pulses_table[2], 2);
                /* 8+8 -> 16 */
                scale_down += combine_and_check(sum_pulses[i..], pulses_comb, silk_max_pulses_table[3], 1);

                if (scale_down != 0)
                {
                    /* We need to downscale the quantization signal */
                    nRshifts[i]++;
                    for (k = 0; k < SHELL_CODEC_FRAME_LENGTH; k++)
                    {
                        abs_pulses_ptr[k] = silk_RSHIFT(abs_pulses_ptr[k], 1);
                    }
                }
                else
                {
                    /* Jump out of while(1) loop and go to next shell coding frame */
                    break;
                }
            }
            abs_pulses_ptr = abs_pulses_ptr[SHELL_CODEC_FRAME_LENGTH..];
        }

        /**************/
        /* Rate level */
        /**************/
        /* find rate level that leads to fewest bits for coding of pulses per block info */
        minSumBits_Q5 = silk_int32_MAX;
        for (k = 0; k < N_RATE_LEVELS - 1; k++)
        {
            nBits_ptr = silk_pulses_per_block_BITS_Q5.AsSpan(k * PULSES_ROW, PULSES_ROW);
            sumBits_Q5 = silk_rate_levels_BITS_Q5[((signalType >> 1) * RATE_LEVELS_ROW) + k];
            for (i = 0; i < iter; i++)
            {
                if (nRshifts[i] > 0)
                {
                    sumBits_Q5 += nBits_ptr[SILK_MAX_PULSES + 1];
                }
                else
                {
                    sumBits_Q5 += nBits_ptr[sum_pulses[i]];
                }
            }
            if (sumBits_Q5 < minSumBits_Q5)
            {
                minSumBits_Q5 = sumBits_Q5;
                RateLevelIndex = k;
            }
        }
        psRangeEnc.ec_enc_icdf(RateLevelIndex, silk_rate_levels_iCDF.AsSpan((signalType >> 1) * RATE_LEVELS_ROW, RATE_LEVELS_ROW), 8);

        /***************************************************/
        /* Sum-Weighted-Pulses Encoding                    */
        /***************************************************/
        cdf_ptr = silk_pulses_per_block_iCDF.AsSpan(RateLevelIndex * PULSES_ROW, PULSES_ROW);
        for (i = 0; i < iter; i++)
        {
            if (nRshifts[i] == 0)
            {
                psRangeEnc.ec_enc_icdf(sum_pulses[i], cdf_ptr, 8);
            }
            else
            {
                psRangeEnc.ec_enc_icdf(SILK_MAX_PULSES + 1, cdf_ptr, 8);
                for (k = 0; k < nRshifts[i] - 1; k++)
                {
                    psRangeEnc.ec_enc_icdf(SILK_MAX_PULSES + 1, silk_pulses_per_block_iCDF.AsSpan((N_RATE_LEVELS - 1) * PULSES_ROW, PULSES_ROW), 8);
                }
                psRangeEnc.ec_enc_icdf(sum_pulses[i], silk_pulses_per_block_iCDF.AsSpan((N_RATE_LEVELS - 1) * PULSES_ROW, PULSES_ROW), 8);
            }
        }

        /******************/
        /* Shell Encoding */
        /******************/
        for (i = 0; i < iter; i++)
        {
            if (sum_pulses[i] > 0)
            {
                silk_shell_encoder(ref psRangeEnc, abs_pulses[(i * SHELL_CODEC_FRAME_LENGTH)..]);
            }
        }

        /****************/
        /* LSB Encoding */
        /****************/
        for (i = 0; i < iter; i++)
        {
            if (nRshifts[i] > 0)
            {
                pulses_ptr = pulses[(i * SHELL_CODEC_FRAME_LENGTH)..];
                nLS = nRshifts[i] - 1;
                for (k = 0; k < SHELL_CODEC_FRAME_LENGTH; k++)
                {
                    abs_q = (sbyte)silk_abs(pulses_ptr[k]);
                    for (j = nLS; j > 0; j--)
                    {
                        bit = silk_RSHIFT(abs_q, j) & 1;
                        psRangeEnc.ec_enc_icdf(bit, silk_lsb_iCDF, 8);
                    }
                    bit = abs_q & 1;
                    psRangeEnc.ec_enc_icdf(bit, silk_lsb_iCDF, 8);
                }
            }
        }

        /****************/
        /* Encode signs */
        /****************/
        silk_encode_signs(ref psRangeEnc, pulses, frame_length, signalType, quantOffsetType, sum_pulses);
    }
}
