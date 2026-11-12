/***********************************************************************
Copyright (c) 2006-2011, Skype Limited. All rights reserved.
Ported to C# from libopus 1.6.1 (silk/NSQ.c, silk/NSQ.h) by Hot Chai Games LLC
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
using static DotGame.AudioCodecs.Opus.Silk.DefineEnc;
using static DotGame.AudioCodecs.Opus.Silk.Define;
using static DotGame.AudioCodecs.Opus.Silk.LPCAnalysisFilter;
using static DotGame.AudioCodecs.Opus.Silk.SigProcFix;
using static DotGame.AudioCodecs.Opus.Silk.TablesOther;

namespace DotGame.AudioCodecs.Opus.Silk;

/// <summary>silk/NSQ.c, with the static inline functions of silk/NSQ.h (also used by silk/NSQ_del_dec.c).</summary>
[SkipLocalsInit]
internal static class NSQ
{
    /* NSQ.h */

    /* C's buf32 is read at negative offsets (buf32[ 0 ] .. buf32[ -15 ]), so it is given as the buffer it
       points into (buf) and its position there (buf32): C's buf32[ -k ] is buf[ buf32 - k ]. */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_noise_shape_quantizer_short_prediction(ReadOnlySpan<int> buf, int buf32, ReadOnlySpan<short> coef16, int order)
    {
        int @out;
        Debug.Assert(order == 10 || order == 16);

        /* Avoids introducing a bias because silk_SMLAWB() always rounds to -inf */
        @out = silk_RSHIFT(order, 1);
        @out = silk_SMLAWB(@out, buf[buf32 - 0], coef16[0]);
        @out = silk_SMLAWB(@out, buf[buf32 - 1], coef16[1]);
        @out = silk_SMLAWB(@out, buf[buf32 - 2], coef16[2]);
        @out = silk_SMLAWB(@out, buf[buf32 - 3], coef16[3]);
        @out = silk_SMLAWB(@out, buf[buf32 - 4], coef16[4]);
        @out = silk_SMLAWB(@out, buf[buf32 - 5], coef16[5]);
        @out = silk_SMLAWB(@out, buf[buf32 - 6], coef16[6]);
        @out = silk_SMLAWB(@out, buf[buf32 - 7], coef16[7]);
        @out = silk_SMLAWB(@out, buf[buf32 - 8], coef16[8]);
        @out = silk_SMLAWB(@out, buf[buf32 - 9], coef16[9]);

        if (order == 16)
        {
            @out = silk_SMLAWB(@out, buf[buf32 - 10], coef16[10]);
            @out = silk_SMLAWB(@out, buf[buf32 - 11], coef16[11]);
            @out = silk_SMLAWB(@out, buf[buf32 - 12], coef16[12]);
            @out = silk_SMLAWB(@out, buf[buf32 - 13], coef16[13]);
            @out = silk_SMLAWB(@out, buf[buf32 - 14], coef16[14]);
            @out = silk_SMLAWB(@out, buf[buf32 - 15], coef16[15]);
        }
        return @out;
    }

    /* C: const opus_int32 *data0, of which only data0[ 0 ] is read: here its value */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_NSQ_noise_shape_feedback_loop(int data0, Span<int> data1, ReadOnlySpan<short> coef, int order)
    {
        int @out;
        int tmp1, tmp2;
        int j;

        tmp2 = data0;
        tmp1 = data1[0];
        data1[0] = tmp2;

        @out = silk_RSHIFT(order, 1);
        @out = silk_SMLAWB(@out, tmp2, coef[0]);

        for (j = 2; j < order; j += 2)
        {
            tmp2 = data1[j - 1];
            data1[j - 1] = tmp1;
            @out = silk_SMLAWB(@out, tmp1, coef[j - 1]);
            tmp1 = data1[j + 0];
            data1[j + 0] = tmp2;
            @out = silk_SMLAWB(@out, tmp2, coef[j]);
        }
        data1[order - 1] = tmp1;
        @out = silk_SMLAWB(@out, tmp1, coef[order - 1]);
        /* Q11 -> Q12 */
        @out = silk_LSHIFT32(@out, 1);
        return @out;
    }

    /* NSQ.c */

    internal static void silk_NSQ(
        silk_encoder_state psEncC,                  /* I    Encoder State                   */
        silk_nsq_state NSQ,                         /* I/O  NSQ state                       */
        SideInfoIndices psIndices,                  /* I/O  Quantization Indices            */
        ReadOnlySpan<short> x16,                    /* I    Input                           */
        Span<sbyte> pulses,                         /* O    Quantized pulse signal          */
        ReadOnlySpan<short> PredCoef_Q12,           /* I    Short term prediction coefs     */
        ReadOnlySpan<short> LTPCoef_Q14,            /* I    Long term prediction coefs      */
        ReadOnlySpan<short> AR_Q13,                 /* I    Noise shaping coefs             */
        ReadOnlySpan<int> HarmShapeGain_Q14,        /* I    Long term shaping coefs         */
        ReadOnlySpan<int> Tilt_Q14,                 /* I    Spectral tilt                   */
        ReadOnlySpan<int> LF_shp_Q14,               /* I    Low frequency shaping coefs     */
        ReadOnlySpan<int> Gains_Q16,                /* I    Quantization step sizes         */
        ReadOnlySpan<int> pitchL,                   /* I    Pitch lags                      */
        int Lambda_Q10,                             /* I    Rate/distortion tradeoff        */
        int LTP_scale_Q14)                          /* I    LTP state scaling               */
    {
        int k, lag, start_idx, LSF_interpolation_flag;
        ReadOnlySpan<short> A_Q12, B_Q14, AR_shp_Q13;
        Span<short> pxq;
        Span<int> sLTP_Q15 = stackalloc int[psEncC.ltp_mem_length + psEncC.frame_length];
        Span<short> sLTP = stackalloc short[psEncC.ltp_mem_length + psEncC.frame_length];
        int HarmShapeFIRPacked_Q14;
        int offset_Q10;
        Span<int> x_sc_Q10 = stackalloc int[psEncC.subfr_length];

        NSQ.rand_seed = psIndices.Seed;

        /* Set unvoiced lag to the previous one, overwrite later for voiced */
        lag = NSQ.lagPrev;

        Debug.Assert(NSQ.prev_gain_Q16 != 0);

        offset_Q10 = silk_Quantization_Offsets_Q10[((psIndices.signalType >> 1) * 2) + psIndices.quantOffsetType];

        if (psIndices.NLSFInterpCoef_Q2 == 4)
        {
            LSF_interpolation_flag = 0;
        }
        else
        {
            LSF_interpolation_flag = 1;
        }

        /* Set up pointers to start of sub frame */
        NSQ.sLTP_shp_buf_idx = psEncC.ltp_mem_length;
        NSQ.sLTP_buf_idx = psEncC.ltp_mem_length;
        pxq = NSQ.xq.AsSpan(psEncC.ltp_mem_length);
        for (k = 0; k < psEncC.nb_subfr; k++)
        {
            A_Q12 = PredCoef_Q12[(((k >> 1) | (1 - LSF_interpolation_flag)) * MAX_LPC_ORDER)..];
            B_Q14 = LTPCoef_Q14[(k * LTP_ORDER)..];
            AR_shp_Q13 = AR_Q13[(k * MAX_SHAPE_LPC_ORDER)..];

            /* Noise shape parameters */
            Debug.Assert(HarmShapeGain_Q14[k] >= 0);
            HarmShapeFIRPacked_Q14 = silk_RSHIFT(HarmShapeGain_Q14[k], 2);
            HarmShapeFIRPacked_Q14 |= silk_LSHIFT(silk_RSHIFT(HarmShapeGain_Q14[k], 1), 16);

            NSQ.rewhite_flag = 0;
            if (psIndices.signalType == TYPE_VOICED)
            {
                /* Voiced */
                lag = pitchL[k];

                /* Re-whitening */
                if ((k & (3 - silk_LSHIFT(LSF_interpolation_flag, 1))) == 0)
                {
                    /* Rewhiten with new A coefs */
                    start_idx = psEncC.ltp_mem_length - lag - psEncC.predictLPCOrder - (LTP_ORDER / 2);
                    Debug.Assert(start_idx > 0);

                    silk_LPC_analysis_filter(sLTP[start_idx..], NSQ.xq.AsSpan(start_idx + (k * psEncC.subfr_length)),
                        A_Q12, psEncC.ltp_mem_length - start_idx, psEncC.predictLPCOrder);

                    NSQ.rewhite_flag = 1;
                    NSQ.sLTP_buf_idx = psEncC.ltp_mem_length;
                }
            }

            silk_nsq_scale_states(psEncC, NSQ, x16, x_sc_Q10, sLTP, sLTP_Q15, k, LTP_scale_Q14, Gains_Q16, pitchL, psIndices.signalType);

            silk_noise_shape_quantizer(NSQ, psIndices.signalType, x_sc_Q10, pulses, pxq, sLTP_Q15, A_Q12, B_Q14,
                AR_shp_Q13, lag, HarmShapeFIRPacked_Q14, Tilt_Q14[k], LF_shp_Q14[k], Gains_Q16[k], Lambda_Q10,
                offset_Q10, psEncC.subfr_length, psEncC.shapingLPCOrder, psEncC.predictLPCOrder);

            x16 = x16[psEncC.subfr_length..];
            pulses = pulses[psEncC.subfr_length..];
            pxq = pxq[psEncC.subfr_length..];
        }

        /* Update lagPrev for next frame */
        NSQ.lagPrev = pitchL[psEncC.nb_subfr - 1];

        /* Save quantized speech and noise shaping signals */
        NSQ.xq.AsSpan(psEncC.frame_length, psEncC.ltp_mem_length).CopyTo(NSQ.xq);
        NSQ.sLTP_shp_Q14.AsSpan(psEncC.frame_length, psEncC.ltp_mem_length).CopyTo(NSQ.sLTP_shp_Q14);
    }

    /******************************/
    /* silk_noise_shape_quantizer */
    /******************************/
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void silk_noise_shape_quantizer(
        silk_nsq_state NSQ,                     /* I/O  NSQ state                       */
        int signalType,                         /* I    Signal type                     */
        ReadOnlySpan<int> x_sc_Q10,             /* I                                    */
        Span<sbyte> pulses,                     /* O                                    */
        Span<short> xq,                         /* O                                    */
        Span<int> sLTP_Q15,                     /* I/O  LTP state                       */
        ReadOnlySpan<short> a_Q12,              /* I    Short term prediction coefs     */
        ReadOnlySpan<short> b_Q14,              /* I    Long term prediction coefs      */
        ReadOnlySpan<short> AR_shp_Q13,         /* I    Noise shaping AR coefs          */
        int lag,                                /* I    Pitch lag                       */
        int HarmShapeFIRPacked_Q14,             /* I                                    */
        int Tilt_Q14,                           /* I    Spectral tilt                   */
        int LF_shp_Q14,                         /* I                                    */
        int Gain_Q16,                           /* I                                    */
        int Lambda_Q10,                         /* I                                    */
        int offset_Q10,                         /* I                                    */
        int length,                             /* I    Input length                    */
        int shapingLPCOrder,                    /* I    Noise shaping AR filter order   */
        int predictLPCOrder)                    /* I    Prediction filter order         */
    {
        int i;
        int LTP_pred_Q13, LPC_pred_Q10, n_AR_Q12, n_LTP_Q13;
        int n_LF_Q12, r_Q10, rr_Q10, q1_Q0, q1_Q10, q2_Q10, rd1_Q20, rd2_Q20;
        int exc_Q14, LPC_exc_Q14, xq_Q14, Gain_Q10;
        int tmp1, tmp2, sLF_AR_shp_Q14;
        int psLPC_Q14, shp_lag_ptr, pred_lag_ptr;  /* C: pointers into NSQ->sLPC_Q14, NSQ->sLTP_shp_Q14, sLTP_Q15; here positions */
        int[] sLPC_Q14 = NSQ.sLPC_Q14;
        int[] sLTP_shp_Q14 = NSQ.sLTP_shp_Q14;

        shp_lag_ptr = NSQ.sLTP_shp_buf_idx - lag + (HARM_SHAPE_FIR_TAPS / 2);
        pred_lag_ptr = NSQ.sLTP_buf_idx - lag + (LTP_ORDER / 2);
        Gain_Q10 = silk_RSHIFT(Gain_Q16, 6);

        /* Set up short term AR state */
        psLPC_Q14 = NSQ_LPC_BUF_LENGTH - 1;

        for (i = 0; i < length; i++)
        {
            /* Generate dither */
            NSQ.rand_seed = silk_RAND(NSQ.rand_seed);

            /* Short-term prediction */
            LPC_pred_Q10 = silk_noise_shape_quantizer_short_prediction(sLPC_Q14, psLPC_Q14, a_Q12, predictLPCOrder);

            /* Long-term prediction */
            if (signalType == TYPE_VOICED)
            {
                /* Unrolled loop */
                /* Avoids introducing a bias because silk_SMLAWB() always rounds to -inf */
                LTP_pred_Q13 = 2;
                LTP_pred_Q13 = silk_SMLAWB(LTP_pred_Q13, sLTP_Q15[pred_lag_ptr - 0], b_Q14[0]);
                LTP_pred_Q13 = silk_SMLAWB(LTP_pred_Q13, sLTP_Q15[pred_lag_ptr - 1], b_Q14[1]);
                LTP_pred_Q13 = silk_SMLAWB(LTP_pred_Q13, sLTP_Q15[pred_lag_ptr - 2], b_Q14[2]);
                LTP_pred_Q13 = silk_SMLAWB(LTP_pred_Q13, sLTP_Q15[pred_lag_ptr - 3], b_Q14[3]);
                LTP_pred_Q13 = silk_SMLAWB(LTP_pred_Q13, sLTP_Q15[pred_lag_ptr - 4], b_Q14[4]);
                pred_lag_ptr++;
            }
            else
            {
                LTP_pred_Q13 = 0;
            }

            /* Noise shape feedback */
            Debug.Assert((shapingLPCOrder & 1) == 0);   /* check that order is even */
            n_AR_Q12 = silk_NSQ_noise_shape_feedback_loop(NSQ.sDiff_shp_Q14, NSQ.sAR2_Q14, AR_shp_Q13, shapingLPCOrder);

            n_AR_Q12 = silk_SMLAWB(n_AR_Q12, NSQ.sLF_AR_shp_Q14, Tilt_Q14);

            n_LF_Q12 = silk_SMULWB(sLTP_shp_Q14[NSQ.sLTP_shp_buf_idx - 1], LF_shp_Q14);
            n_LF_Q12 = silk_SMLAWT(n_LF_Q12, NSQ.sLF_AR_shp_Q14, LF_shp_Q14);

            Debug.Assert(lag > 0 || signalType != TYPE_VOICED);

            /* Combine prediction and noise shaping signals */
            tmp1 = silk_SUB32_ovflw(silk_LSHIFT32(LPC_pred_Q10, 2), n_AR_Q12);  /* Q12 */
            tmp1 = silk_SUB32_ovflw(tmp1, n_LF_Q12);                              /* Q12 */
            if (lag > 0)
            {
                /* Symmetric, packed FIR coefficients */
                n_LTP_Q13 = silk_SMULWB(silk_ADD_SAT32(sLTP_shp_Q14[shp_lag_ptr], sLTP_shp_Q14[shp_lag_ptr - 2]), HarmShapeFIRPacked_Q14);
                n_LTP_Q13 = silk_SMLAWT(n_LTP_Q13, sLTP_shp_Q14[shp_lag_ptr - 1], HarmShapeFIRPacked_Q14);
                n_LTP_Q13 = silk_LSHIFT(n_LTP_Q13, 1);
                shp_lag_ptr++;

                tmp2 = silk_SUB32(LTP_pred_Q13, n_LTP_Q13);                       /* Q13 */
                tmp1 = silk_ADD32_ovflw(tmp2, silk_LSHIFT32(tmp1, 1));          /* Q13 */
                tmp1 = silk_RSHIFT_ROUND(tmp1, 3);                                /* Q10 */
            }
            else
            {
                tmp1 = silk_RSHIFT_ROUND(tmp1, 2);                                /* Q10 */
            }

            r_Q10 = silk_SUB32(x_sc_Q10[i], tmp1);                              /* residual error Q10 */

            /* Flip sign depending on dither */
            if (NSQ.rand_seed < 0)
            {
                r_Q10 = -r_Q10;
            }
            r_Q10 = silk_LIMIT_32(r_Q10, -(31 << 10), 30 << 10);

            /* Find two quantization level candidates and measure their rate-distortion */
            q1_Q10 = silk_SUB32(r_Q10, offset_Q10);
            q1_Q0 = silk_RSHIFT(q1_Q10, 10);
            if (Lambda_Q10 > 2048)
            {
                /* For aggressive RDO, the bias becomes more than one pulse. */
                int rdo_offset = (Lambda_Q10 / 2) - 512;
                if (q1_Q10 > rdo_offset)
                {
                    q1_Q0 = silk_RSHIFT(q1_Q10 - rdo_offset, 10);
                }
                else if (q1_Q10 < -rdo_offset)
                {
                    q1_Q0 = silk_RSHIFT(q1_Q10 + rdo_offset, 10);
                }
                else if (q1_Q10 < 0)
                {
                    q1_Q0 = -1;
                }
                else
                {
                    q1_Q0 = 0;
                }
            }
            if (q1_Q0 > 0)
            {
                q1_Q10 = silk_SUB32(silk_LSHIFT(q1_Q0, 10), QUANT_LEVEL_ADJUST_Q10);
                q1_Q10 = silk_ADD32(q1_Q10, offset_Q10);
                q2_Q10 = silk_ADD32(q1_Q10, 1024);
                rd1_Q20 = silk_SMULBB(q1_Q10, Lambda_Q10);
                rd2_Q20 = silk_SMULBB(q2_Q10, Lambda_Q10);
            }
            else if (q1_Q0 == 0)
            {
                q1_Q10 = offset_Q10;
                q2_Q10 = silk_ADD32(q1_Q10, 1024 - QUANT_LEVEL_ADJUST_Q10);
                rd1_Q20 = silk_SMULBB(q1_Q10, Lambda_Q10);
                rd2_Q20 = silk_SMULBB(q2_Q10, Lambda_Q10);
            }
            else if (q1_Q0 == -1)
            {
                q2_Q10 = offset_Q10;
                q1_Q10 = silk_SUB32(q2_Q10, 1024 - QUANT_LEVEL_ADJUST_Q10);
                rd1_Q20 = silk_SMULBB(-q1_Q10, Lambda_Q10);
                rd2_Q20 = silk_SMULBB(q2_Q10, Lambda_Q10);
            }
            else
            {            /* Q1_Q0 < -1 */
                q1_Q10 = silk_ADD32(silk_LSHIFT(q1_Q0, 10), QUANT_LEVEL_ADJUST_Q10);
                q1_Q10 = silk_ADD32(q1_Q10, offset_Q10);
                q2_Q10 = silk_ADD32(q1_Q10, 1024);
                rd1_Q20 = silk_SMULBB(-q1_Q10, Lambda_Q10);
                rd2_Q20 = silk_SMULBB(-q2_Q10, Lambda_Q10);
            }
            rr_Q10 = silk_SUB32(r_Q10, q1_Q10);
            rd1_Q20 = silk_SMLABB(rd1_Q20, rr_Q10, rr_Q10);
            rr_Q10 = silk_SUB32(r_Q10, q2_Q10);
            rd2_Q20 = silk_SMLABB(rd2_Q20, rr_Q10, rr_Q10);

            if (rd2_Q20 < rd1_Q20)
            {
                q1_Q10 = q2_Q10;
            }

            pulses[i] = (sbyte)silk_RSHIFT_ROUND(q1_Q10, 10);

            /* Excitation */
            exc_Q14 = silk_LSHIFT(q1_Q10, 4);
            if (NSQ.rand_seed < 0)
            {
                exc_Q14 = -exc_Q14;
            }

            /* Add predictions */
            LPC_exc_Q14 = silk_ADD_LSHIFT32(exc_Q14, LTP_pred_Q13, 1);
            xq_Q14 = silk_ADD32_ovflw(LPC_exc_Q14, silk_LSHIFT32(LPC_pred_Q10, 4));

            /* Scale XQ back to normal level before saving */
            xq[i] = (short)silk_SAT16(silk_RSHIFT_ROUND(silk_SMULWW(xq_Q14, Gain_Q10), 8));

            /* Update states */
            psLPC_Q14++;
            sLPC_Q14[psLPC_Q14] = xq_Q14;
            NSQ.sDiff_shp_Q14 = silk_SUB32_ovflw(xq_Q14, silk_LSHIFT32(x_sc_Q10[i], 4));
            sLF_AR_shp_Q14 = silk_SUB32_ovflw(NSQ.sDiff_shp_Q14, silk_LSHIFT32(n_AR_Q12, 2));
            NSQ.sLF_AR_shp_Q14 = sLF_AR_shp_Q14;

            sLTP_shp_Q14[NSQ.sLTP_shp_buf_idx] = silk_SUB32_ovflw(sLF_AR_shp_Q14, silk_LSHIFT32(n_LF_Q12, 2));
            sLTP_Q15[NSQ.sLTP_buf_idx] = silk_LSHIFT(LPC_exc_Q14, 1);
            NSQ.sLTP_shp_buf_idx++;
            NSQ.sLTP_buf_idx++;

            /* Make dither dependent on quantized signal */
            NSQ.rand_seed = silk_ADD32_ovflw(NSQ.rand_seed, pulses[i]);
        }

        /* Update LPC synth buffer */
        sLPC_Q14.AsSpan(length, NSQ_LPC_BUF_LENGTH).CopyTo(sLPC_Q14);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void silk_nsq_scale_states(
        silk_encoder_state psEncC,              /* I    Encoder State                   */
        silk_nsq_state NSQ,                     /* I/O  NSQ state                       */
        ReadOnlySpan<short> x16,                /* I    input                           */
        Span<int> x_sc_Q10,                     /* O    input scaled with 1/Gain        */
        ReadOnlySpan<short> sLTP,               /* I    re-whitened LTP state in Q0     */
        Span<int> sLTP_Q15,                     /* O    LTP state matching scaled input */
        int subfr,                              /* I    subframe number                 */
        int LTP_scale_Q14,                      /* I                                    */
        ReadOnlySpan<int> Gains_Q16,            /* I                                    */
        ReadOnlySpan<int> pitchL,               /* I    Pitch lag                       */
        int signal_type)                        /* I    Signal type                     */
    {
        int i, lag;
        int gain_adj_Q16, inv_gain_Q31, inv_gain_Q26;

        lag = pitchL[subfr];
        inv_gain_Q31 = silk_INVERSE32_varQ(silk_max(Gains_Q16[subfr], 1), 47);
        Debug.Assert(inv_gain_Q31 != 0);

        /* Scale input */
        inv_gain_Q26 = silk_RSHIFT_ROUND(inv_gain_Q31, 5);
        for (i = 0; i < psEncC.subfr_length; i++)
        {
            x_sc_Q10[i] = silk_SMULWW(x16[i], inv_gain_Q26);
        }

        /* After rewhitening the LTP state is un-scaled, so scale with inv_gain_Q16 */
        if (NSQ.rewhite_flag != 0)
        {
            if (subfr == 0)
            {
                /* Do LTP downscaling */
                inv_gain_Q31 = silk_LSHIFT(silk_SMULWB(inv_gain_Q31, LTP_scale_Q14), 2);
            }
            for (i = NSQ.sLTP_buf_idx - lag - (LTP_ORDER / 2); i < NSQ.sLTP_buf_idx; i++)
            {
                Debug.Assert(i < MAX_FRAME_LENGTH);
                sLTP_Q15[i] = silk_SMULWB(inv_gain_Q31, sLTP[i]);
            }
        }

        /* Adjust for changing gain */
        if (Gains_Q16[subfr] != NSQ.prev_gain_Q16)
        {
            gain_adj_Q16 = silk_DIV32_varQ(NSQ.prev_gain_Q16, Gains_Q16[subfr], 16);

            /* Scale long-term shaping state */
            for (i = NSQ.sLTP_shp_buf_idx - psEncC.ltp_mem_length; i < NSQ.sLTP_shp_buf_idx; i++)
            {
                NSQ.sLTP_shp_Q14[i] = silk_SMULWW(gain_adj_Q16, NSQ.sLTP_shp_Q14[i]);
            }

            /* Scale long-term prediction state */
            if (signal_type == TYPE_VOICED && NSQ.rewhite_flag == 0)
            {
                for (i = NSQ.sLTP_buf_idx - lag - (LTP_ORDER / 2); i < NSQ.sLTP_buf_idx; i++)
                {
                    sLTP_Q15[i] = silk_SMULWW(gain_adj_Q16, sLTP_Q15[i]);
                }
            }

            NSQ.sLF_AR_shp_Q14 = silk_SMULWW(gain_adj_Q16, NSQ.sLF_AR_shp_Q14);
            NSQ.sDiff_shp_Q14 = silk_SMULWW(gain_adj_Q16, NSQ.sDiff_shp_Q14);

            /* Scale short-term prediction and shaping states */
            for (i = 0; i < NSQ_LPC_BUF_LENGTH; i++)
            {
                NSQ.sLPC_Q14[i] = silk_SMULWW(gain_adj_Q16, NSQ.sLPC_Q14[i]);
            }
            for (i = 0; i < MAX_SHAPE_LPC_ORDER; i++)
            {
                NSQ.sAR2_Q14[i] = silk_SMULWW(gain_adj_Q16, NSQ.sAR2_Q14[i]);
            }

            /* Save inverse gain */
            NSQ.prev_gain_Q16 = Gains_Q16[subfr];
        }
    }
}
