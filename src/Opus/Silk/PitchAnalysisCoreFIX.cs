/***********************************************************************
Copyright (c) 2006-2011, Skype Limited. All rights reserved.
Ported to C# from libopus 1.6.1 (silk/fixed/pitch_analysis_core_FIX.c) by Hot Chai Games LLC
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
using DotGame.AudioCodecs.Opus.Celt;
using static DotGame.AudioCodecs.Opus.Silk.Lin2log;
using static DotGame.AudioCodecs.Opus.Silk.PitchEstDefinesEnc;
using static DotGame.AudioCodecs.Opus.Silk.PitchEstTablesEnc;
using static DotGame.AudioCodecs.Opus.Silk.ResamplerDown2;
using static DotGame.AudioCodecs.Opus.Silk.ResamplerDown23;
using static DotGame.AudioCodecs.Opus.Silk.SigProcFixEnc;
using static DotGame.AudioCodecs.Opus.Silk.SortEnc;
using static DotGame.AudioCodecs.Opus.Silk.VectorOpsFIX;
using static DotGame.AudioCodecs.Opus.Silk.PitchEstDefines;
using static DotGame.AudioCodecs.Opus.Silk.PitchEstTables;
using static DotGame.AudioCodecs.Opus.Silk.SigProcFix;
using static DotGame.AudioCodecs.Opus.Silk.SumSqrShift;

namespace DotGame.AudioCodecs.Opus.Silk;

/***********************************************************
* Pitch analyser function
********************************************************** */
[SkipLocalsInit]
internal static class PitchAnalysisCoreFIX
{
    private const int SCRATCH_SIZE = 22;
    private const int SF_LENGTH_4KHZ = PE_SUBFR_LENGTH_MS * 4;
    private const int SF_LENGTH_8KHZ = PE_SUBFR_LENGTH_MS * 8;
    private const int MIN_LAG_4KHZ = PE_MIN_LAG_MS * 4;
    private const int MIN_LAG_8KHZ = PE_MIN_LAG_MS * 8;
    private const int MAX_LAG_4KHZ = PE_MAX_LAG_MS * 4;
    private const int MAX_LAG_8KHZ = (PE_MAX_LAG_MS * 8) - 1;
    private const int CSTRIDE_4KHZ = MAX_LAG_4KHZ + 1 - MIN_LAG_4KHZ;
    private const int CSTRIDE_8KHZ = MAX_LAG_8KHZ + 3 - (MIN_LAG_8KHZ - 2);
    private const int D_COMP_MIN = MIN_LAG_8KHZ - 3;
    private const int D_COMP_MAX = MAX_LAG_8KHZ + 4;
    private const int D_COMP_STRIDE = D_COMP_MAX - D_COMP_MIN;

    /* C: typedef opus_int32 silk_pe_stage3_vals[ PE_NB_STAGE3_LAGS ]; arrays of them are flattened, so
       C's matrix_ptr( x, k, j, N )[ l ] is x[ ( ( k * N ) + j ) * PE_NB_STAGE3_LAGS + l ] */

    /*************************************************************/
    /*      FIXED POINT CORE PITCH ANALYSIS FUNCTION             */
    /*************************************************************/
    internal static int silk_pitch_analysis_core(  /* O    Voicing estimate: 0 voiced, 1 unvoiced                      */
        ReadOnlySpan<short> frame_unscaled, /* I    Signal of length PE_FRAME_LENGTH_MS*Fs_kHz                  */
        Span<int> pitch_out,                /* O    4 pitch lag values                                          */
        ref short lagIndex,                 /* O    Lag Index                                                   */
        ref sbyte contourIndex,             /* O    Pitch contour Index                                         */
        ref int LTPCorr_Q15,                /* I/O  Normalized correlation; input: value from previous frame    */
        int prevLag,                        /* I    Last lag of previous frame; set to zero is unvoiced         */
        int search_thres1_Q16,              /* I    First stage threshold for lag candidates 0 - 1              */
        int search_thres2_Q13,              /* I    Final threshold for lag candidates 0 - 1                    */
        int Fs_kHz,                         /* I    Sample frequency (kHz)                                      */
        int complexity,                     /* I    Complexity setting, 0-2, where 2 is highest                 */
        int nb_subfr)                       /* I    number of 5 ms subframes                                    */
    {
        scoped Span<short> frame_8kHz_buf;
        scoped Span<short> frame_4kHz;
        scoped Span<short> frame_scaled;
        Span<int> filt_state = stackalloc int[6];
        scoped ReadOnlySpan<short> frame, frame_8kHz;
        int i, k, d, j;
        scoped Span<short> C;
        scoped Span<int> xcorr32;
        int target_ptr, basis_ptr;          /* C: pointers into frame_4kHz / frame_8kHz / frame; here positions */
        int cross_corr, normalizer, energy, energy_basis, energy_target;
        Span<int> d_srch = stackalloc int[PE_D_SRCH_LENGTH];
        int Cmax, length_d_srch, length_d_comp, shift;
        scoped Span<short> d_comp;
        int sum, threshold, lag_counter;
        int CBimax, CBimax_new, CBimax_old, lag, start_lag, end_lag, lag_new;
        Span<int> CC = stackalloc int[PE_NB_CBKS_STAGE2_EXT];
        int CCmax, CCmax_b, CCmax_new_b, CCmax_new;
        scoped Span<int> energies_st3;
        scoped Span<int> cross_corr_st3;
        int frame_length, frame_length_8kHz, frame_length_4kHz;
        int sf_length;
        int min_lag;
        int max_lag;
        int contour_bias_Q15, diff;
        int nb_cbk_search, cbk_size;
        int delta_lag_log2_sqr_Q7, lag_log2_Q7, prevLag_log2_Q7, prev_lag_bias_Q13;
        ReadOnlySpan<sbyte> Lag_CB_ptr;

        /* Check for valid sampling frequency */
        Debug.Assert(Fs_kHz == 8 || Fs_kHz == 12 || Fs_kHz == 16);

        /* Check for valid complexity setting */
        Debug.Assert(complexity >= SILK_PE_MIN_COMPLEX);
        Debug.Assert(complexity <= SILK_PE_MAX_COMPLEX);

        Debug.Assert(search_thres1_Q16 >= 0 && search_thres1_Q16 <= (1 << 16));
        Debug.Assert(search_thres2_Q13 >= 0 && search_thres2_Q13 <= (1 << 13));

        /* Set up frame lengths max / min lag for the sampling frequency */
        frame_length = (PE_LTP_MEM_LENGTH_MS + (nb_subfr * PE_SUBFR_LENGTH_MS)) * Fs_kHz;
        frame_length_4kHz = (PE_LTP_MEM_LENGTH_MS + (nb_subfr * PE_SUBFR_LENGTH_MS)) * 4;
        frame_length_8kHz = (PE_LTP_MEM_LENGTH_MS + (nb_subfr * PE_SUBFR_LENGTH_MS)) * 8;
        sf_length = PE_SUBFR_LENGTH_MS * Fs_kHz;
        min_lag = PE_MIN_LAG_MS * Fs_kHz;
        max_lag = (PE_MAX_LAG_MS * Fs_kHz) - 1;

        /* Downscale input if necessary */
        silk_sum_sqr_shift(out energy, out shift, frame_unscaled, frame_length);
        shift += 3 - silk_CLZ32(energy);        /* at least two bits headroom */
        frame_scaled = stackalloc short[frame_length];
        if (shift > 0)
        {
            shift = silk_RSHIFT(shift + 1, 1);
            for (i = 0; i < frame_length; i++)
            {
                frame_scaled[i] = (short)silk_RSHIFT(frame_unscaled[i], shift);
            }
            frame = frame_scaled;
        }
        else
        {
            frame = frame_unscaled;
        }

        frame_8kHz_buf = stackalloc short[(Fs_kHz == 8) ? 1 : frame_length_8kHz];
        /* Resample from input sampled at Fs_kHz to 8 kHz */
        if (Fs_kHz == 16)
        {
            filt_state[..2].Clear();
            silk_resampler_down2(filt_state, frame_8kHz_buf, frame, frame_length);
            frame_8kHz = frame_8kHz_buf;
        }
        else if (Fs_kHz == 12)
        {
            filt_state[..6].Clear();
            silk_resampler_down2_3(filt_state, frame_8kHz_buf, frame, frame_length);
            frame_8kHz = frame_8kHz_buf;
        }
        else
        {
            Debug.Assert(Fs_kHz == 8);
            frame_8kHz = frame;
        }

        /* Decimate again to 4 kHz */
        filt_state[..2].Clear();/* Set state to zero */
        frame_4kHz = stackalloc short[frame_length_4kHz];
        silk_resampler_down2(filt_state, frame_4kHz, frame_8kHz, frame_length_8kHz);

        /* Low-pass filter */
        for (i = frame_length_4kHz - 1; i > 0; i--)
        {
            frame_4kHz[i] = silk_ADD_SAT16(frame_4kHz[i], frame_4kHz[i - 1]);
        }

        /******************************************************************************
        * FIRST STAGE, operating in 4 khz
        ******************************************************************************/
        C = stackalloc short[nb_subfr * CSTRIDE_8KHZ];
        xcorr32 = stackalloc int[MAX_LAG_4KHZ - MIN_LAG_4KHZ + 1];
        C[..((nb_subfr >> 1) * CSTRIDE_4KHZ)].Clear();
        target_ptr = silk_LSHIFT(SF_LENGTH_4KHZ, 2);
        for (k = 0; k < nb_subfr >> 1; k++)
        {
            /* Check that we are within range of the array */
            Debug.Assert(target_ptr >= 0);
            Debug.Assert(target_ptr + SF_LENGTH_8KHZ <= frame_length_4kHz);

            basis_ptr = target_ptr - MIN_LAG_4KHZ;

            /* Check that we are within range of the array */
            Debug.Assert(basis_ptr >= 0);
            Debug.Assert(basis_ptr + SF_LENGTH_8KHZ <= frame_length_4kHz);

            Pitch.celt_pitch_xcorr(frame_4kHz[target_ptr..], frame_4kHz[(target_ptr - MAX_LAG_4KHZ)..], xcorr32, SF_LENGTH_8KHZ, MAX_LAG_4KHZ - MIN_LAG_4KHZ + 1);

            /* Calculate first vector products before loop */
            cross_corr = xcorr32[MAX_LAG_4KHZ - MIN_LAG_4KHZ];
            normalizer = silk_inner_prod_aligned(frame_4kHz[target_ptr..], frame_4kHz[target_ptr..], SF_LENGTH_8KHZ);
            normalizer = silk_ADD32(normalizer, silk_inner_prod_aligned(frame_4kHz[basis_ptr..], frame_4kHz[basis_ptr..], SF_LENGTH_8KHZ));
            normalizer = silk_ADD32(normalizer, silk_SMULBB(SF_LENGTH_8KHZ, 4000));

            C[(k * CSTRIDE_4KHZ) + 0] =
                (short)silk_DIV32_varQ(cross_corr, normalizer, 13 + 1);                      /* Q13 */

            /* From now on normalizer is computed recursively */
            for (d = MIN_LAG_4KHZ + 1; d <= MAX_LAG_4KHZ; d++)
            {
                basis_ptr--;

                /* Check that we are within range of the array */
                Debug.Assert(basis_ptr >= 0);
                Debug.Assert(basis_ptr + SF_LENGTH_8KHZ <= frame_length_4kHz);

                cross_corr = xcorr32[MAX_LAG_4KHZ - d];

                /* Add contribution of new sample and remove contribution from oldest sample */
                normalizer = silk_ADD32(normalizer,
                    silk_SMULBB(frame_4kHz[basis_ptr], frame_4kHz[basis_ptr]) -
                    silk_SMULBB(frame_4kHz[basis_ptr + SF_LENGTH_8KHZ], frame_4kHz[basis_ptr + SF_LENGTH_8KHZ]));

                C[(k * CSTRIDE_4KHZ) + d - MIN_LAG_4KHZ] =
                    (short)silk_DIV32_varQ(cross_corr, normalizer, 13 + 1);                  /* Q13 */
            }
            /* Update target pointer */
            target_ptr += SF_LENGTH_8KHZ;
        }

        /* Combine two subframes into single correlation measure and apply short-lag bias */
        if (nb_subfr == PE_MAX_NB_SUBFR)
        {
            for (i = MAX_LAG_4KHZ; i >= MIN_LAG_4KHZ; i--)
            {
                sum = C[(0 * CSTRIDE_4KHZ) + i - MIN_LAG_4KHZ]
                    + C[(1 * CSTRIDE_4KHZ) + i - MIN_LAG_4KHZ];               /* Q14 */
                sum = silk_SMLAWB(sum, sum, silk_LSHIFT(-i, 4));                                /* Q14 */
                C[i - MIN_LAG_4KHZ] = (short)sum;                                            /* Q14 */
            }
        }
        else
        {
            /* Only short-lag bias */
            for (i = MAX_LAG_4KHZ; i >= MIN_LAG_4KHZ; i--)
            {
                sum = silk_LSHIFT(C[i - MIN_LAG_4KHZ], 1);                          /* Q14 */
                sum = silk_SMLAWB(sum, sum, silk_LSHIFT(-i, 4));                                /* Q14 */
                C[i - MIN_LAG_4KHZ] = (short)sum;                                            /* Q14 */
            }
        }

        /* Sort */
        length_d_srch = silk_ADD_LSHIFT32(4, complexity, 1);
        Debug.Assert(3 * length_d_srch <= PE_D_SRCH_LENGTH);
        silk_insertion_sort_decreasing_int16(C, d_srch, CSTRIDE_4KHZ,
                                              length_d_srch);

        /* Escape if correlation is very low already here */
        Cmax = C[0];                                                    /* Q14 */
        if (Cmax < SILK_FIX_CONST(0.2, 14))
        {
            pitch_out[..nb_subfr].Clear();
            LTPCorr_Q15 = 0;
            lagIndex = 0;
            contourIndex = 0;
            return 1;
        }

        threshold = silk_SMULWB(search_thres1_Q16, Cmax);
        for (i = 0; i < length_d_srch; i++)
        {
            /* Convert to 8 kHz indices for the sorted correlation that exceeds the threshold */
            if (C[i] > threshold)
            {
                d_srch[i] = silk_LSHIFT(d_srch[i] + MIN_LAG_4KHZ, 1);
            }
            else
            {
                length_d_srch = i;
                break;
            }
        }
        Debug.Assert(length_d_srch > 0);

        d_comp = stackalloc short[D_COMP_STRIDE];
        for (i = D_COMP_MIN; i < D_COMP_MAX; i++)
        {
            d_comp[i - D_COMP_MIN] = 0;
        }
        for (i = 0; i < length_d_srch; i++)
        {
            d_comp[d_srch[i] - D_COMP_MIN] = 1;
        }

        /* Convolution */
        for (i = D_COMP_MAX - 1; i >= MIN_LAG_8KHZ; i--)
        {
            d_comp[i - D_COMP_MIN] +=
                (short)(d_comp[i - 1 - D_COMP_MIN] + d_comp[i - 2 - D_COMP_MIN]);
        }

        length_d_srch = 0;
        for (i = MIN_LAG_8KHZ; i < MAX_LAG_8KHZ + 1; i++)
        {
            if (d_comp[i + 1 - D_COMP_MIN] > 0)
            {
                d_srch[length_d_srch] = i;
                length_d_srch++;
            }
        }

        /* Convolution */
        for (i = D_COMP_MAX - 1; i >= MIN_LAG_8KHZ; i--)
        {
            d_comp[i - D_COMP_MIN] += (short)(d_comp[i - 1 - D_COMP_MIN]
                + d_comp[i - 2 - D_COMP_MIN] + d_comp[i - 3 - D_COMP_MIN]);
        }

        length_d_comp = 0;
        for (i = MIN_LAG_8KHZ; i < D_COMP_MAX; i++)
        {
            if (d_comp[i - D_COMP_MIN] > 0)
            {
                d_comp[length_d_comp] = (short)(i - 2);
                length_d_comp++;
            }
        }

        /**********************************************************************************
        ** SECOND STAGE, operating at 8 kHz, on lag sections with high correlation
        *************************************************************************************/

        /*********************************************************************************
        * Find energy of each subframe projected onto its history, for a range of delays
        *********************************************************************************/
        C[..(nb_subfr * CSTRIDE_8KHZ)].Clear();

        target_ptr = PE_LTP_MEM_LENGTH_MS * 8;
        for (k = 0; k < nb_subfr; k++)
        {
            /* Check that we are within range of the array */
            Debug.Assert(target_ptr >= 0);
            Debug.Assert(target_ptr + SF_LENGTH_8KHZ <= frame_length_8kHz);

            energy_target = silk_ADD32(silk_inner_prod_aligned(frame_8kHz[target_ptr..], frame_8kHz[target_ptr..], SF_LENGTH_8KHZ), 1);
            for (j = 0; j < length_d_comp; j++)
            {
                d = d_comp[j];
                basis_ptr = target_ptr - d;

                /* Check that we are within range of the array */
                Debug.Assert(basis_ptr >= 0);
                Debug.Assert(basis_ptr + SF_LENGTH_8KHZ <= frame_length_8kHz);

                cross_corr = silk_inner_prod_aligned(frame_8kHz[target_ptr..], frame_8kHz[basis_ptr..], SF_LENGTH_8KHZ);
                if (cross_corr > 0)
                {
                    energy_basis = silk_inner_prod_aligned(frame_8kHz[basis_ptr..], frame_8kHz[basis_ptr..], SF_LENGTH_8KHZ);
                    C[(k * CSTRIDE_8KHZ) + d - (MIN_LAG_8KHZ - 2)] =
                        (short)silk_DIV32_varQ(cross_corr,
                                                 silk_ADD32(energy_target,
                                                             energy_basis),
                                                 13 + 1);                                      /* Q13 */
                }
                else
                {
                    C[(k * CSTRIDE_8KHZ) + d - (MIN_LAG_8KHZ - 2)] = 0;
                }
            }
            target_ptr += SF_LENGTH_8KHZ;
        }

        /* search over lag range and lags codebook */
        /* scale factor for lag codebook, as a function of center lag */

        CCmax = silk_int32_MIN;
        CCmax_b = silk_int32_MIN;

        CBimax = 0; /* To avoid returning undefined lag values */
        lag = -1;   /* To check if lag with strong enough correlation has been found */

        if (prevLag > 0)
        {
            if (Fs_kHz == 12)
            {
                prevLag = silk_DIV32_16(silk_LSHIFT(prevLag, 1), 3);
            }
            else if (Fs_kHz == 16)
            {
                prevLag = silk_RSHIFT(prevLag, 1);
            }
            prevLag_log2_Q7 = silk_lin2log(prevLag);
        }
        else
        {
            prevLag_log2_Q7 = 0;
        }
        Debug.Assert(search_thres2_Q13 == silk_SAT16(search_thres2_Q13));
        /* Set up stage 2 codebook based on number of subframes */
        if (nb_subfr == PE_MAX_NB_SUBFR)
        {
            cbk_size = PE_NB_CBKS_STAGE2_EXT;
            Lag_CB_ptr = silk_CB_lags_stage2;
            if (Fs_kHz == 8 && complexity > SILK_PE_MIN_COMPLEX)
            {
                /* If input is 8 khz use a larger codebook here because it is last stage */
                nb_cbk_search = PE_NB_CBKS_STAGE2_EXT;
            }
            else
            {
                nb_cbk_search = PE_NB_CBKS_STAGE2;
            }
        }
        else
        {
            cbk_size = PE_NB_CBKS_STAGE2_10MS;
            Lag_CB_ptr = silk_CB_lags_stage2_10_ms;
            nb_cbk_search = PE_NB_CBKS_STAGE2_10MS;
        }

        for (k = 0; k < length_d_srch; k++)
        {
            d = d_srch[k];
            for (j = 0; j < nb_cbk_search; j++)
            {
                CC[j] = 0;
                for (i = 0; i < nb_subfr; i++)
                {
                    int d_subfr;
                    /* Try all codebooks */
                    d_subfr = d + Lag_CB_ptr[(i * cbk_size) + j];
                    CC[j] = CC[j]
                        + C[(i * CSTRIDE_8KHZ) + d_subfr - (MIN_LAG_8KHZ - 2)];
                }
            }
            /* Find best codebook */
            CCmax_new = silk_int32_MIN;
            CBimax_new = 0;
            for (i = 0; i < nb_cbk_search; i++)
            {
                if (CC[i] > CCmax_new)
                {
                    CCmax_new = CC[i];
                    CBimax_new = i;
                }
            }

            /* Bias towards shorter lags */
            lag_log2_Q7 = silk_lin2log(d); /* Q7 */
            Debug.Assert(lag_log2_Q7 == silk_SAT16(lag_log2_Q7));
            Debug.Assert(nb_subfr * SILK_FIX_CONST(PE_SHORTLAG_BIAS, 13) == silk_SAT16(nb_subfr * SILK_FIX_CONST(PE_SHORTLAG_BIAS, 13)));
            CCmax_new_b = CCmax_new - silk_RSHIFT(silk_SMULBB(nb_subfr * SILK_FIX_CONST(PE_SHORTLAG_BIAS, 13), lag_log2_Q7), 7); /* Q13 */

            /* Bias towards previous lag */
            Debug.Assert(nb_subfr * SILK_FIX_CONST(PE_PREVLAG_BIAS, 13) == silk_SAT16(nb_subfr * SILK_FIX_CONST(PE_PREVLAG_BIAS, 13)));
            if (prevLag > 0)
            {
                delta_lag_log2_sqr_Q7 = lag_log2_Q7 - prevLag_log2_Q7;
                Debug.Assert(delta_lag_log2_sqr_Q7 == silk_SAT16(delta_lag_log2_sqr_Q7));
                delta_lag_log2_sqr_Q7 = silk_RSHIFT(silk_SMULBB(delta_lag_log2_sqr_Q7, delta_lag_log2_sqr_Q7), 7);
                prev_lag_bias_Q13 = silk_RSHIFT(silk_SMULBB(nb_subfr * SILK_FIX_CONST(PE_PREVLAG_BIAS, 13), LTPCorr_Q15), 15); /* Q13 */
                prev_lag_bias_Q13 = silk_DIV32(silk_MUL(prev_lag_bias_Q13, delta_lag_log2_sqr_Q7), delta_lag_log2_sqr_Q7 + SILK_FIX_CONST(0.5, 7));
                CCmax_new_b -= prev_lag_bias_Q13; /* Q13 */
            }

            if (CCmax_new_b > CCmax_b &&                                    /* Find maximum biased correlation                  */
                CCmax_new > silk_SMULBB(nb_subfr, search_thres2_Q13) &&     /* Correlation needs to be high enough to be voiced */
                silk_CB_lags_stage2[CBimax_new] <= MIN_LAG_8KHZ)            /* Lag must be in range                             */
            {
                CCmax_b = CCmax_new_b;
                CCmax = CCmax_new;
                lag = d;
                CBimax = CBimax_new;
            }
        }

        if (lag == -1)
        {
            /* No suitable candidate found */
            pitch_out[..nb_subfr].Clear();
            LTPCorr_Q15 = 0;
            lagIndex = 0;
            contourIndex = 0;
            return 1;
        }

        /* Output normalized correlation */
        LTPCorr_Q15 = silk_LSHIFT(silk_DIV32_16(CCmax, nb_subfr), 2);
        Debug.Assert(LTPCorr_Q15 >= 0);

        if (Fs_kHz > 8)
        {
            /* Search in original signal */

            CBimax_old = CBimax;
            /* Compensate for decimation */
            Debug.Assert(lag == silk_SAT16(lag));
            if (Fs_kHz == 12)
            {
                lag = silk_RSHIFT(silk_SMULBB(lag, 3), 1);
            }
            else if (Fs_kHz == 16)
            {
                lag = silk_LSHIFT(lag, 1);
            }
            else
            {
                lag = silk_SMULBB(lag, 3);
            }

            lag = silk_LIMIT_int(lag, min_lag, max_lag);
            start_lag = silk_max_int(lag - 2, min_lag);
            end_lag = silk_min_int(lag + 2, max_lag);
            lag_new = lag;                                    /* to avoid undefined lag */
            CBimax = 0;                                       /* to avoid undefined lag */

            CCmax = silk_int32_MIN;
            /* pitch lags according to second stage */
            for (k = 0; k < nb_subfr; k++)
            {
                pitch_out[k] = lag + (2 * silk_CB_lags_stage2[(k * PE_NB_CBKS_STAGE2_EXT) + CBimax_old]);
            }

            /* Set up codebook parameters according to complexity setting and frame length */
            if (nb_subfr == PE_MAX_NB_SUBFR)
            {
                nb_cbk_search = silk_nb_cbk_searchs_stage3[complexity];
                cbk_size = PE_NB_CBKS_STAGE3_MAX;
                Lag_CB_ptr = silk_CB_lags_stage3;
            }
            else
            {
                nb_cbk_search = PE_NB_CBKS_STAGE3_10MS;
                cbk_size = PE_NB_CBKS_STAGE3_10MS;
                Lag_CB_ptr = silk_CB_lags_stage3_10_ms;
            }

            /* Calculate the correlations and energies needed in stage 3 */
            energies_st3 = stackalloc int[nb_subfr * nb_cbk_search * PE_NB_STAGE3_LAGS];
            cross_corr_st3 = stackalloc int[nb_subfr * nb_cbk_search * PE_NB_STAGE3_LAGS];
            silk_P_Ana_calc_corr_st3(cross_corr_st3, frame, start_lag, sf_length, nb_subfr, complexity);
            silk_P_Ana_calc_energy_st3(energies_st3, frame, start_lag, sf_length, nb_subfr, complexity);

            lag_counter = 0;
            Debug.Assert(lag == silk_SAT16(lag));
            contour_bias_Q15 = silk_DIV32_16(SILK_FIX_CONST(PE_FLATCONTOUR_BIAS, 15), lag);

            target_ptr = PE_LTP_MEM_LENGTH_MS * Fs_kHz;
            energy_target = silk_ADD32(silk_inner_prod_aligned(frame[target_ptr..], frame[target_ptr..], nb_subfr * sf_length), 1);
            for (d = start_lag; d <= end_lag; d++)
            {
                for (j = 0; j < nb_cbk_search; j++)
                {
                    cross_corr = 0;
                    energy = energy_target;
                    for (k = 0; k < nb_subfr; k++)
                    {
                        cross_corr = silk_ADD32(cross_corr,
                            cross_corr_st3[(((k * nb_cbk_search) + j) * PE_NB_STAGE3_LAGS) + lag_counter]);
                        energy = silk_ADD32(energy,
                            energies_st3[(((k * nb_cbk_search) + j) * PE_NB_STAGE3_LAGS) + lag_counter]);
                        Debug.Assert(energy >= 0);
                    }
                    if (cross_corr > 0)
                    {
                        CCmax_new = silk_DIV32_varQ(cross_corr, energy, 13 + 1);          /* Q13 */
                        /* Reduce depending on flatness of contour */
                        diff = silk_int16_MAX - silk_MUL(contour_bias_Q15, j);            /* Q15 */
                        Debug.Assert(diff == silk_SAT16(diff));
                        CCmax_new = silk_SMULWB(CCmax_new, diff);                         /* Q14 */
                    }
                    else
                    {
                        CCmax_new = 0;
                    }

                    if (CCmax_new > CCmax && (d + silk_CB_lags_stage3[j]) <= max_lag)
                    {
                        CCmax = CCmax_new;
                        lag_new = d;
                        CBimax = j;
                    }
                }
                lag_counter++;
            }

            for (k = 0; k < nb_subfr; k++)
            {
                pitch_out[k] = lag_new + Lag_CB_ptr[(k * cbk_size) + CBimax];
                pitch_out[k] = silk_LIMIT(pitch_out[k], min_lag, PE_MAX_LAG_MS * Fs_kHz);
            }
            lagIndex = (short)(lag_new - min_lag);
            contourIndex = (sbyte)CBimax;
        }
        else
        {        /* Fs_kHz == 8 */
            /* Save Lags */
            for (k = 0; k < nb_subfr; k++)
            {
                pitch_out[k] = lag + Lag_CB_ptr[(k * cbk_size) + CBimax];
                pitch_out[k] = silk_LIMIT(pitch_out[k], MIN_LAG_8KHZ, PE_MAX_LAG_MS * 8);
            }
            lagIndex = (short)(lag - MIN_LAG_8KHZ);
            contourIndex = (sbyte)CBimax;
        }
        Debug.Assert(lagIndex >= 0);
        /* return as voiced */
        return 0;
    }

    /***********************************************************************
     * Calculates the correlations used in stage 3 search. In order to cover
     * the whole lag codebook for all the searched offset lags (lag +- 2),
     * the following correlations are needed in each sub frame:
     *
     * sf1: lag range [-8,...,7] total 16 correlations
     * sf2: lag range [-4,...,4] total 9 correlations
     * sf3: lag range [-3,....4] total 8 correltions
     * sf4: lag range [-6,....8] total 15 correlations
     *
     * In total 48 correlations. The direct implementation computed in worst
     * case 4*12*5 = 240 correlations, but more likely around 120.
     ***********************************************************************/
    private static void silk_P_Ana_calc_corr_st3(
        Span<int> cross_corr_st3,       /* O 3 DIM correlation array */
        ReadOnlySpan<short> frame,      /* I vector to correlate         */
        int start_lag,                  /* I lag offset to search around */
        int sf_length,                  /* I length of a 5 ms subframe   */
        int nb_subfr,                   /* I number of subframes         */
        int complexity)                 /* I Complexity setting          */
    {
        int target_ptr;                 /* C: pointer into frame; here a position */
        int i, j, k, lag_counter, lag_low, lag_high;
        int nb_cbk_search, delta, idx, cbk_size;
        Span<int> scratch_mem = stackalloc int[SCRATCH_SIZE];
        Span<int> xcorr32 = stackalloc int[SCRATCH_SIZE];
        ReadOnlySpan<sbyte> Lag_range_ptr, Lag_CB_ptr;

        Debug.Assert(complexity >= SILK_PE_MIN_COMPLEX);
        Debug.Assert(complexity <= SILK_PE_MAX_COMPLEX);

        if (nb_subfr == PE_MAX_NB_SUBFR)
        {
            Lag_range_ptr = silk_Lag_range_stage3.AsSpan(complexity * PE_MAX_NB_SUBFR * 2);
            Lag_CB_ptr = silk_CB_lags_stage3;
            nb_cbk_search = silk_nb_cbk_searchs_stage3[complexity];
            cbk_size = PE_NB_CBKS_STAGE3_MAX;
        }
        else
        {
            Debug.Assert(nb_subfr == PE_MAX_NB_SUBFR >> 1);
            Lag_range_ptr = silk_Lag_range_stage3_10_ms;
            Lag_CB_ptr = silk_CB_lags_stage3_10_ms;
            nb_cbk_search = PE_NB_CBKS_STAGE3_10MS;
            cbk_size = PE_NB_CBKS_STAGE3_10MS;
        }

        target_ptr = silk_LSHIFT(sf_length, 2); /* Pointer to middle of frame */
        for (k = 0; k < nb_subfr; k++)
        {
            lag_counter = 0;

            /* Calculate the correlations for each subframe */
            lag_low = Lag_range_ptr[(k * 2) + 0];
            lag_high = Lag_range_ptr[(k * 2) + 1];
            Debug.Assert(lag_high - lag_low + 1 <= SCRATCH_SIZE);
            Pitch.celt_pitch_xcorr(frame[target_ptr..], frame[(target_ptr - start_lag - lag_high)..], xcorr32, sf_length, lag_high - lag_low + 1);
            for (j = lag_low; j <= lag_high; j++)
            {
                Debug.Assert(lag_counter < SCRATCH_SIZE);
                scratch_mem[lag_counter] = xcorr32[lag_high - j];
                lag_counter++;
            }

            delta = Lag_range_ptr[(k * 2) + 0];
            for (i = 0; i < nb_cbk_search; i++)
            {
                /* Fill out the 3 dim array that stores the correlations for */
                /* each code_book vector for each start lag */
                idx = Lag_CB_ptr[(k * cbk_size) + i] - delta;
                for (j = 0; j < PE_NB_STAGE3_LAGS; j++)
                {
                    Debug.Assert(idx + j < SCRATCH_SIZE);
                    Debug.Assert(idx + j < lag_counter);
                    cross_corr_st3[(((k * nb_cbk_search) + i) * PE_NB_STAGE3_LAGS) + j] =
                        scratch_mem[idx + j];
                }
            }
            target_ptr += sf_length;
        }
    }

    /********************************************************************/
    /* Calculate the energies for first two subframes. The energies are */
    /* calculated recursively.                                          */
    /********************************************************************/
    private static void silk_P_Ana_calc_energy_st3(
        Span<int> energies_st3,         /* O 3 DIM energy array */
        ReadOnlySpan<short> frame,      /* I vector to calc energy in    */
        int start_lag,                  /* I lag offset to search around */
        int sf_length,                  /* I length of one 5 ms subframe */
        int nb_subfr,                   /* I number of subframes         */
        int complexity)                 /* I Complexity setting          */
    {
        int target_ptr, basis_ptr;      /* C: pointers into frame; here positions */
        int energy;
        int k, i, j, lag_counter;
        int nb_cbk_search, delta, idx, cbk_size, lag_diff;
        Span<int> scratch_mem = stackalloc int[SCRATCH_SIZE];
        ReadOnlySpan<sbyte> Lag_range_ptr, Lag_CB_ptr;

        Debug.Assert(complexity >= SILK_PE_MIN_COMPLEX);
        Debug.Assert(complexity <= SILK_PE_MAX_COMPLEX);

        if (nb_subfr == PE_MAX_NB_SUBFR)
        {
            Lag_range_ptr = silk_Lag_range_stage3.AsSpan(complexity * PE_MAX_NB_SUBFR * 2);
            Lag_CB_ptr = silk_CB_lags_stage3;
            nb_cbk_search = silk_nb_cbk_searchs_stage3[complexity];
            cbk_size = PE_NB_CBKS_STAGE3_MAX;
        }
        else
        {
            Debug.Assert(nb_subfr == PE_MAX_NB_SUBFR >> 1);
            Lag_range_ptr = silk_Lag_range_stage3_10_ms;
            Lag_CB_ptr = silk_CB_lags_stage3_10_ms;
            nb_cbk_search = PE_NB_CBKS_STAGE3_10MS;
            cbk_size = PE_NB_CBKS_STAGE3_10MS;
        }

        target_ptr = silk_LSHIFT(sf_length, 2);
        for (k = 0; k < nb_subfr; k++)
        {
            lag_counter = 0;

            /* Calculate the energy for first lag */
            basis_ptr = target_ptr - (start_lag + Lag_range_ptr[(k * 2) + 0]);
            energy = silk_inner_prod_aligned(frame[basis_ptr..], frame[basis_ptr..], sf_length);
            Debug.Assert(energy >= 0);
            scratch_mem[lag_counter] = energy;
            lag_counter++;

            lag_diff = Lag_range_ptr[(k * 2) + 1] - Lag_range_ptr[(k * 2) + 0] + 1;
            for (i = 1; i < lag_diff; i++)
            {
                /* remove part outside new window */
                energy -= silk_SMULBB(frame[basis_ptr + sf_length - i], frame[basis_ptr + sf_length - i]);
                Debug.Assert(energy >= 0);

                /* add part that comes into window */
                energy = silk_ADD_SAT32(energy, silk_SMULBB(frame[basis_ptr - i], frame[basis_ptr - i]));
                Debug.Assert(energy >= 0);
                Debug.Assert(lag_counter < SCRATCH_SIZE);
                scratch_mem[lag_counter] = energy;
                lag_counter++;
            }

            delta = Lag_range_ptr[(k * 2) + 0];
            for (i = 0; i < nb_cbk_search; i++)
            {
                /* Fill out the 3 dim array that stores the correlations for    */
                /* each code_book vector for each start lag                     */
                idx = Lag_CB_ptr[(k * cbk_size) + i] - delta;
                for (j = 0; j < PE_NB_STAGE3_LAGS; j++)
                {
                    Debug.Assert(idx + j < SCRATCH_SIZE);
                    Debug.Assert(idx + j < lag_counter);
                    energies_st3[(((k * nb_cbk_search) + i) * PE_NB_STAGE3_LAGS) + j] =
                        scratch_mem[idx + j];
                    Debug.Assert(energies_st3[(((k * nb_cbk_search) + i) * PE_NB_STAGE3_LAGS) + j] >= 0);
                }
            }
            target_ptr += sf_length;
        }
    }
}
