/***********************************************************************
Copyright (c) 2006-2011, Skype Limited. All rights reserved.
Ported to C# from libopus 1.6.1 (silk/PLC.c, silk/PLC.h) by Hot Chai Games LLC
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
using static DotGame.AudioCodecs.Opus.Silk.Bwexpander;
using static DotGame.AudioCodecs.Opus.Silk.Define;
using static DotGame.AudioCodecs.Opus.Silk.LPCAnalysisFilter;
using static DotGame.AudioCodecs.Opus.Silk.LPCInvPredGain;
using static DotGame.AudioCodecs.Opus.Silk.SigProcFix;
using static DotGame.AudioCodecs.Opus.Silk.SumSqrShift;

namespace DotGame.AudioCodecs.Opus.Silk;

[SkipLocalsInit]
internal static class PLC
{
    /* PLC.h */
    internal const double BWE_COEF = 0.99;
    internal const int V_PITCH_GAIN_START_MIN_Q14 = 11469;               /* 0.7 in Q14               */
    internal const int V_PITCH_GAIN_START_MAX_Q14 = 15565;               /* 0.95 in Q14              */
    internal const int MAX_PITCH_LAG_MS = 18;
    internal const int RAND_BUF_SIZE = 128;
    internal const int RAND_BUF_MASK = RAND_BUF_SIZE - 1;
    internal const int LOG2_INV_LPC_GAIN_HIGH_THRES = 3;                 /* 2^3 = 8 dB LPC gain      */
    internal const int LOG2_INV_LPC_GAIN_LOW_THRES = 8;                  /* 2^8 = 24 dB LPC gain     */
    internal const int PITCH_DRIFT_FAC_Q16 = 655;                        /* 0.01 in Q16              */

    /* PLC.c */
    private const int NB_ATT = 2;
    private static readonly short[] HARM_ATT_Q15 = [32440, 31130]; /* 0.99, 0.95 */
    private static readonly short[] PLC_RAND_ATTENUATE_V_Q15 = [31130, 26214]; /* 0.95, 0.8 */
    private static readonly short[] PLC_RAND_ATTENUATE_UV_Q15 = [32440, 29491]; /* 0.99, 0.9 */

    /* C: SILK_FIX_CONST( BWE_COEF, 16 ) */
    private const int BWE_COEF_Q16 = (int)((BWE_COEF * (1L << 16)) + 0.5);

    internal static void silk_PLC_Reset(
        silk_decoder_state psDec)       /* I/O Decoder state        */
    {
        psDec.sPLC.pitchL_Q8 = silk_LSHIFT(psDec.frame_length, 8 - 1);
        psDec.sPLC.prevGain_Q16[0] = 1 << 16;
        psDec.sPLC.prevGain_Q16[1] = 1 << 16;
        psDec.sPLC.subfr_length = 20;
        psDec.sPLC.nb_subfr = 2;
    }

    internal static void silk_PLC(
        silk_decoder_state psDec,           /* I/O Decoder state        */
        silk_decoder_control psDecCtrl,     /* I/O Decoder control      */
        Span<short> frame,                  /* I/O  signal              */
        int lost)                           /* I Loss flag              */
    {
        /* PLC control function */
        if (psDec.fs_kHz != psDec.sPLC.fs_kHz)
        {
            silk_PLC_Reset(psDec);
            psDec.sPLC.fs_kHz = psDec.fs_kHz;
        }

        if (lost != 0)
        {
            /****************************/
            /* Generate Signal          */
            /****************************/
            silk_PLC_conceal(psDec, psDecCtrl, frame);

            psDec.lossCnt++;
        }
        else
        {
            /****************************/
            /* Update state             */
            /****************************/
            silk_PLC_update(psDec, psDecCtrl);
        }
    }

    /**************************************************/
    /* Update state of PLC                            */
    /**************************************************/
    private static void silk_PLC_update(
        silk_decoder_state psDec,           /* I/O Decoder state        */
        silk_decoder_control psDecCtrl)     /* I/O Decoder control      */
    {
        int LTP_Gain_Q14, temp_LTP_Gain_Q14;
        int i, j;
        silk_PLC_struct psPLC;

        psPLC = psDec.sPLC;

        /* Update parameters used in case of packet loss */
        psDec.prevSignalType = psDec.indices.signalType;
        LTP_Gain_Q14 = 0;
        if (psDec.indices.signalType == TYPE_VOICED)
        {
            /* Find the parameters for the last subframe which contains a pitch pulse */
            for (j = 0; j * psDec.subfr_length < psDecCtrl.pitchL[psDec.nb_subfr - 1]; j++)
            {
                if (j == psDec.nb_subfr)
                {
                    break;
                }
                temp_LTP_Gain_Q14 = 0;
                for (i = 0; i < LTP_ORDER; i++)
                {
                    temp_LTP_Gain_Q14 += psDecCtrl.LTPCoef_Q14[((psDec.nb_subfr - 1 - j) * LTP_ORDER) + i];
                }
                if (temp_LTP_Gain_Q14 > LTP_Gain_Q14)
                {
                    LTP_Gain_Q14 = temp_LTP_Gain_Q14;
                    psDecCtrl.LTPCoef_Q14.AsSpan(silk_SMULBB(psDec.nb_subfr - 1 - j, LTP_ORDER), LTP_ORDER).CopyTo(psPLC.LTPCoef_Q14);

                    psPLC.pitchL_Q8 = silk_LSHIFT(psDecCtrl.pitchL[psDec.nb_subfr - 1 - j], 8);
                }
            }

            psPLC.LTPCoef_Q14.AsSpan(0, LTP_ORDER).Clear();
            psPLC.LTPCoef_Q14[LTP_ORDER / 2] = (short)LTP_Gain_Q14;

            /* Limit LT coefs */
            if (LTP_Gain_Q14 < V_PITCH_GAIN_START_MIN_Q14)
            {
                int scale_Q10;
                int tmp;

                tmp = silk_LSHIFT(V_PITCH_GAIN_START_MIN_Q14, 10);
                scale_Q10 = silk_DIV32(tmp, silk_max(LTP_Gain_Q14, 1));
                for (i = 0; i < LTP_ORDER; i++)
                {
                    psPLC.LTPCoef_Q14[i] = (short)silk_RSHIFT(silk_SMULBB(psPLC.LTPCoef_Q14[i], scale_Q10), 10);
                }
            }
            else if (LTP_Gain_Q14 > V_PITCH_GAIN_START_MAX_Q14)
            {
                int scale_Q14;
                int tmp;

                tmp = silk_LSHIFT(V_PITCH_GAIN_START_MAX_Q14, 14);
                scale_Q14 = silk_DIV32(tmp, silk_max(LTP_Gain_Q14, 1));
                for (i = 0; i < LTP_ORDER; i++)
                {
                    psPLC.LTPCoef_Q14[i] = (short)silk_RSHIFT(silk_SMULBB(psPLC.LTPCoef_Q14[i], scale_Q14), 14);
                }
            }
        }
        else
        {
            psPLC.pitchL_Q8 = silk_LSHIFT(silk_SMULBB(psDec.fs_kHz, 18), 8);
            psPLC.LTPCoef_Q14.AsSpan(0, LTP_ORDER).Clear();
        }

        /* Save LPC coefficients */
        psDecCtrl.PredCoef_Q12_row(1)[..psDec.LPC_order].CopyTo(psPLC.prevLPC_Q12);
        psPLC.prevLTP_scale_Q14 = (short)psDecCtrl.LTP_scale_Q14;

        /* Save last two gains */
        psDecCtrl.Gains_Q16.AsSpan(psDec.nb_subfr - 2, 2).CopyTo(psPLC.prevGain_Q16);

        psPLC.subfr_length = psDec.subfr_length;
        psPLC.nb_subfr = psDec.nb_subfr;
    }

    private static void silk_PLC_energy(out int energy1, out int shift1, out int energy2, out int shift2,
          ReadOnlySpan<int> exc_Q14, ReadOnlySpan<int> prevGain_Q10, int subfr_length, int nb_subfr)
    {
        int i, k;
        /* C: ALLOC( exc_buf, 2*subfr_length, opus_int16 ), at most this size */
        Span<short> exc_buf = stackalloc short[2 * MAX_SUB_FRAME_LENGTH];
        scoped Span<short> exc_buf_ptr;
        /* Find random noise component */
        /* Scale previous excitation signal */
        exc_buf_ptr = exc_buf;
        for (k = 0; k < 2; k++)
        {
            for (i = 0; i < subfr_length; i++)
            {
                exc_buf_ptr[i] = (short)silk_SAT16(silk_RSHIFT(
                    silk_SMULWW(exc_Q14[i + ((k + nb_subfr - 2) * subfr_length)], prevGain_Q10[k]), 8));
            }
            exc_buf_ptr = exc_buf_ptr[subfr_length..];
        }
        /* Find the subframe with lowest energy of the last two and use that as random noise generator */
        silk_sum_sqr_shift(out energy1, out shift1, exc_buf, subfr_length);
        silk_sum_sqr_shift(out energy2, out shift2, exc_buf[subfr_length..], subfr_length);
    }

    private static void silk_PLC_conceal(
        silk_decoder_state psDec,           /* I/O Decoder state        */
        silk_decoder_control psDecCtrl,     /* I/O Decoder control      */
        Span<short> frame)                  /* O LPC residual signal    */
    {
        int i, j, k;
        int lag, idx, sLTP_buf_idx, shift1, shift2;
        int rand_seed, harm_Gain_Q15, rand_Gain_Q15, inv_gain_Q30;
        int energy1, energy2;
        ReadOnlySpan<int> rand_ptr;
        scoped Span<int> pred_lag_ptr;
        int LPC_pred_Q10, LTP_pred_Q12;
        short rand_scale_Q14;
        Span<short> B_Q14;
        scoped Span<int> sLPC_Q14_ptr;
        Span<short> A_Q12 = stackalloc short[MAX_LPC_ORDER];
        /* C: VLAs of ltp_mem_length + frame_length and ltp_mem_length elements (no SMALL_FOOTPRINT); here at their largest sizes */
        Span<short> sLTP = stackalloc short[LTP_MEM_LENGTH_MS * MAX_FS_KHZ];
        Span<int> sLTP_Q14 = stackalloc int[(LTP_MEM_LENGTH_MS * MAX_FS_KHZ) + MAX_FRAME_LENGTH];
        silk_PLC_struct psPLC = psDec.sPLC;
        Span<int> prevGain_Q10 = stackalloc int[2];

        prevGain_Q10[0] = silk_RSHIFT(psPLC.prevGain_Q16[0], 6);
        prevGain_Q10[1] = silk_RSHIFT(psPLC.prevGain_Q16[1], 6);

        if (psDec.first_frame_after_reset != 0)
        {
            Array.Clear(psPLC.prevLPC_Q12);
        }

        silk_PLC_energy(out energy1, out shift1, out energy2, out shift2, psDec.exc_Q14, prevGain_Q10, psDec.subfr_length, psDec.nb_subfr);

        if (silk_RSHIFT(energy1, shift2) < silk_RSHIFT(energy2, shift1))
        {
            /* First sub-frame has lowest energy */
            rand_ptr = psDec.exc_Q14.AsSpan(silk_max_int(0, ((psPLC.nb_subfr - 1) * psPLC.subfr_length) - RAND_BUF_SIZE));
        }
        else
        {
            /* Second sub-frame has lowest energy */
            rand_ptr = psDec.exc_Q14.AsSpan(silk_max_int(0, (psPLC.nb_subfr * psPLC.subfr_length) - RAND_BUF_SIZE));
        }

        /* Set up Gain to random noise component */
        B_Q14 = psPLC.LTPCoef_Q14;
        rand_scale_Q14 = psPLC.randScale_Q14;

        /* Set up attenuation gains */
        harm_Gain_Q15 = HARM_ATT_Q15[silk_min_int(NB_ATT - 1, psDec.lossCnt)];
        if (psDec.prevSignalType == TYPE_VOICED)
        {
            rand_Gain_Q15 = PLC_RAND_ATTENUATE_V_Q15[silk_min_int(NB_ATT - 1, psDec.lossCnt)];
        }
        else
        {
            rand_Gain_Q15 = PLC_RAND_ATTENUATE_UV_Q15[silk_min_int(NB_ATT - 1, psDec.lossCnt)];
        }

        /* LPC concealment. Apply BWE to previous LPC */
        silk_bwexpander(psPLC.prevLPC_Q12, psDec.LPC_order, BWE_COEF_Q16);

        /* Preload LPC coefficients to array on stack. Gives small performance gain */
        psPLC.prevLPC_Q12.AsSpan(0, psDec.LPC_order).CopyTo(A_Q12);

        /* First Lost frame */
        if (psDec.lossCnt == 0)
        {
            rand_scale_Q14 = 1 << 14;

            /* Reduce random noise Gain for voiced frames */
            if (psDec.prevSignalType == TYPE_VOICED)
            {
                for (i = 0; i < LTP_ORDER; i++)
                {
                    rand_scale_Q14 -= B_Q14[i];
                }
                rand_scale_Q14 = silk_max_16(3277, rand_scale_Q14); /* 0.2 */
                rand_scale_Q14 = (short)silk_RSHIFT(silk_SMULBB(rand_scale_Q14, psPLC.prevLTP_scale_Q14), 14);
            }
            else
            {
                /* Reduce random noise for unvoiced frames with high LPC gain */
                int invGain_Q30, down_scale_Q30;

                invGain_Q30 = silk_LPC_inverse_pred_gain(psPLC.prevLPC_Q12, psDec.LPC_order);

                down_scale_Q30 = silk_min_32(silk_RSHIFT(1 << 30, LOG2_INV_LPC_GAIN_HIGH_THRES), invGain_Q30);
                down_scale_Q30 = silk_max_32(silk_RSHIFT(1 << 30, LOG2_INV_LPC_GAIN_LOW_THRES), down_scale_Q30);
                down_scale_Q30 = silk_LSHIFT(down_scale_Q30, LOG2_INV_LPC_GAIN_HIGH_THRES);

                rand_Gain_Q15 = silk_RSHIFT(silk_SMULWB(down_scale_Q30, rand_Gain_Q15), 14);
            }
        }

        rand_seed = psPLC.rand_seed;
        lag = silk_RSHIFT_ROUND(psPLC.pitchL_Q8, 8);
        sLTP_buf_idx = psDec.ltp_mem_length;

        /* Rewhiten LTP state */
        idx = psDec.ltp_mem_length - lag - psDec.LPC_order - (LTP_ORDER / 2);
        Debug.Assert(idx > 0);
        silk_LPC_analysis_filter(sLTP[idx..], psDec.outBuf.AsSpan(idx), A_Q12, psDec.ltp_mem_length - idx, psDec.LPC_order);
        /* Scale LTP state */
        inv_gain_Q30 = silk_INVERSE32_varQ(psPLC.prevGain_Q16[1], 46);
        inv_gain_Q30 = silk_min(inv_gain_Q30, silk_int32_MAX >> 1);
        for (i = idx + psDec.LPC_order; i < psDec.ltp_mem_length; i++)
        {
            sLTP_Q14[i] = silk_SMULWB(inv_gain_Q30, sLTP[i]);
        }

        /***************************/
        /* LTP synthesis filtering */
        /***************************/
        for (k = 0; k < psDec.nb_subfr; k++)
        {
            /* Set up pointer. C: pred_lag_ptr = &sLTP_Q14[ sLTP_buf_idx - lag + LTP_ORDER / 2 ], read back
               to pred_lag_ptr[ -4 ]; here pred_lag_ptr starts 4 earlier, so C's pred_lag_ptr[ -m ] is
               pred_lag_ptr[ 4 - m ] */
            pred_lag_ptr = sLTP_Q14[(sLTP_buf_idx - lag + (LTP_ORDER / 2) - 4)..];
            for (i = 0; i < psDec.subfr_length; i++)
            {
                /* Unrolled loop */
                /* Avoids introducing a bias because silk_SMLAWB() always rounds to -inf */
                LTP_pred_Q12 = 2;
                LTP_pred_Q12 = silk_SMLAWB(LTP_pred_Q12, pred_lag_ptr[4 - 0], B_Q14[0]);
                LTP_pred_Q12 = silk_SMLAWB(LTP_pred_Q12, pred_lag_ptr[4 - 1], B_Q14[1]);
                LTP_pred_Q12 = silk_SMLAWB(LTP_pred_Q12, pred_lag_ptr[4 - 2], B_Q14[2]);
                LTP_pred_Q12 = silk_SMLAWB(LTP_pred_Q12, pred_lag_ptr[4 - 3], B_Q14[3]);
                LTP_pred_Q12 = silk_SMLAWB(LTP_pred_Q12, pred_lag_ptr[4 - 4], B_Q14[4]);
                pred_lag_ptr = pred_lag_ptr[1..];

                /* Generate LPC excitation */
                rand_seed = silk_RAND(rand_seed);
                idx = silk_RSHIFT(rand_seed, 25) & RAND_BUF_MASK;
                sLTP_Q14[sLTP_buf_idx] = silk_LSHIFT32(silk_SMLAWB(LTP_pred_Q12, rand_ptr[idx], rand_scale_Q14), 2);
                sLTP_buf_idx++;
            }

            /* Gradually reduce LTP gain */
            for (j = 0; j < LTP_ORDER; j++)
            {
                B_Q14[j] = (short)silk_RSHIFT(silk_SMULBB(harm_Gain_Q15, B_Q14[j]), 15);
            }
            /* Gradually reduce excitation gain */
            rand_scale_Q14 = (short)silk_RSHIFT(silk_SMULBB(rand_scale_Q14, rand_Gain_Q15), 15);

            /* Slowly increase pitch lag */
            psPLC.pitchL_Q8 = silk_SMLAWB(psPLC.pitchL_Q8, psPLC.pitchL_Q8, PITCH_DRIFT_FAC_Q16);
            psPLC.pitchL_Q8 = silk_min_32(psPLC.pitchL_Q8, silk_LSHIFT(silk_SMULBB(MAX_PITCH_LAG_MS, psDec.fs_kHz), 8));
            lag = silk_RSHIFT_ROUND(psPLC.pitchL_Q8, 8);
        }

        /***************************/
        /* LPC synthesis filtering */
        /***************************/
        sLPC_Q14_ptr = sLTP_Q14[(psDec.ltp_mem_length - MAX_LPC_ORDER)..];

        /* Copy LPC state */
        psDec.sLPC_Q14_buf.AsSpan(0, MAX_LPC_ORDER).CopyTo(sLPC_Q14_ptr);

        Debug.Assert(psDec.LPC_order >= 10); /* check that unrolling works */
        for (i = 0; i < psDec.frame_length; i++)
        {
            /* partly unrolled */
            /* Avoids introducing a bias because silk_SMLAWB() always rounds to -inf */
            LPC_pred_Q10 = silk_RSHIFT(psDec.LPC_order, 1);
            LPC_pred_Q10 = silk_SMLAWB(LPC_pred_Q10, sLPC_Q14_ptr[MAX_LPC_ORDER + i - 1], A_Q12[0]);
            LPC_pred_Q10 = silk_SMLAWB(LPC_pred_Q10, sLPC_Q14_ptr[MAX_LPC_ORDER + i - 2], A_Q12[1]);
            LPC_pred_Q10 = silk_SMLAWB(LPC_pred_Q10, sLPC_Q14_ptr[MAX_LPC_ORDER + i - 3], A_Q12[2]);
            LPC_pred_Q10 = silk_SMLAWB(LPC_pred_Q10, sLPC_Q14_ptr[MAX_LPC_ORDER + i - 4], A_Q12[3]);
            LPC_pred_Q10 = silk_SMLAWB(LPC_pred_Q10, sLPC_Q14_ptr[MAX_LPC_ORDER + i - 5], A_Q12[4]);
            LPC_pred_Q10 = silk_SMLAWB(LPC_pred_Q10, sLPC_Q14_ptr[MAX_LPC_ORDER + i - 6], A_Q12[5]);
            LPC_pred_Q10 = silk_SMLAWB(LPC_pred_Q10, sLPC_Q14_ptr[MAX_LPC_ORDER + i - 7], A_Q12[6]);
            LPC_pred_Q10 = silk_SMLAWB(LPC_pred_Q10, sLPC_Q14_ptr[MAX_LPC_ORDER + i - 8], A_Q12[7]);
            LPC_pred_Q10 = silk_SMLAWB(LPC_pred_Q10, sLPC_Q14_ptr[MAX_LPC_ORDER + i - 9], A_Q12[8]);
            LPC_pred_Q10 = silk_SMLAWB(LPC_pred_Q10, sLPC_Q14_ptr[MAX_LPC_ORDER + i - 10], A_Q12[9]);
            for (j = 10; j < psDec.LPC_order; j++)
            {
                LPC_pred_Q10 = silk_SMLAWB(LPC_pred_Q10, sLPC_Q14_ptr[MAX_LPC_ORDER + i - j - 1], A_Q12[j]);
            }

            /* Add prediction to LPC excitation */
            sLPC_Q14_ptr[MAX_LPC_ORDER + i] = silk_ADD_SAT32(sLPC_Q14_ptr[MAX_LPC_ORDER + i],
                                                silk_LSHIFT_SAT32(LPC_pred_Q10, 4));

            /* Scale with Gain */
            frame[i] = (short)silk_SAT16(silk_SAT16(silk_RSHIFT_ROUND(silk_SMULWW(sLPC_Q14_ptr[MAX_LPC_ORDER + i], prevGain_Q10[1]), 8)));
        }

        /* Save LPC state */
        sLPC_Q14_ptr.Slice(psDec.frame_length, MAX_LPC_ORDER).CopyTo(psDec.sLPC_Q14_buf);

        /**************************************/
        /* Update states                      */
        /**************************************/
        psPLC.rand_seed = rand_seed;
        psPLC.randScale_Q14 = rand_scale_Q14;
        for (i = 0; i < MAX_NB_SUBFR; i++)
        {
            psDecCtrl.pitchL[i] = lag;
        }
    }

    /* Glues concealed frames with new good received frames */
    internal static void silk_PLC_glue_frames(
        silk_decoder_state psDec,       /* I/O decoder state        */
        Span<short> frame,              /* I/O signal               */
        int length)                     /* I length of signal       */
    {
        int i, energy_shift;
        int energy;
        silk_PLC_struct psPLC;
        psPLC = psDec.sPLC;

        if (psDec.lossCnt != 0)
        {
            /* Calculate energy in concealed residual */
            silk_sum_sqr_shift(out psPLC.conc_energy, out psPLC.conc_energy_shift, frame, length);

            psPLC.last_frame_lost = 1;
        }
        else
        {
            if (psDec.sPLC.last_frame_lost != 0)
            {
                /* Calculate residual in decoded signal if last frame was lost */
                silk_sum_sqr_shift(out energy, out energy_shift, frame, length);

                /* Normalize energies */
                if (energy_shift > psPLC.conc_energy_shift)
                {
                    psPLC.conc_energy = silk_RSHIFT(psPLC.conc_energy, energy_shift - psPLC.conc_energy_shift);
                }
                else if (energy_shift < psPLC.conc_energy_shift)
                {
                    energy = silk_RSHIFT(energy, psPLC.conc_energy_shift - energy_shift);
                }

                /* Fade in the energy difference */
                if (energy > psPLC.conc_energy)
                {
                    int frac_Q24, LZ;
                    int gain_Q16, slope_Q16;

                    LZ = silk_CLZ32(psPLC.conc_energy);
                    LZ = LZ - 1;
                    psPLC.conc_energy = silk_LSHIFT(psPLC.conc_energy, LZ);
                    energy = silk_RSHIFT(energy, silk_max_32(24 - LZ, 0));

                    frac_Q24 = silk_DIV32(psPLC.conc_energy, silk_max(energy, 1));

                    gain_Q16 = silk_LSHIFT(silk_SQRT_APPROX(frac_Q24), 4);
                    slope_Q16 = silk_DIV32_16((1 << 16) - gain_Q16, length);
                    /* Make slope 4x steeper to avoid missing onsets after DTX */
                    slope_Q16 = silk_LSHIFT(slope_Q16, 2);
                    for (i = 0; i < length; i++)
                    {
                        frame[i] = (short)silk_SMULWB(gain_Q16, frame[i]);
                        gain_Q16 += slope_Q16;
                        if (gain_Q16 > 1 << 16)
                        {
                            break;
                        }
                    }
                }
            }
            psPLC.last_frame_lost = 0;
        }
    }
}
