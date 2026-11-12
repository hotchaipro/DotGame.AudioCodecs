/***********************************************************************
Copyright (c) 2006-2011, Skype Limited. All rights reserved.
Ported to C# from libopus 1.6.1 (silk/fixed/corrMatrix_FIX.c) by Hot Chai Games LLC
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
using static DotGame.AudioCodecs.Opus.Silk.VectorOpsFIX;
using static DotGame.AudioCodecs.Opus.Silk.SigProcFix;
using static DotGame.AudioCodecs.Opus.Silk.SumSqrShift;

namespace DotGame.AudioCodecs.Opus.Silk;

/**********************************************************************
 * Correlation Matrix Computations for LS estimate.
 **********************************************************************/
internal static class CorrMatrixFIX
{
    /* Calculates correlation vector X'*t */
    internal static void silk_corrVector_FIX(
        ReadOnlySpan<short> x,          /* I    x vector [L + order - 1] used to form data matrix X                         */
        ReadOnlySpan<short> t,          /* I    Target vector [L]                                                           */
        int L,                          /* I    Length of vectors                                                           */
        int order,                      /* I    Max lag for correlation                                                     */
        Span<int> Xt,                   /* O    Pointer to X'*t correlation vector [order]                                  */
        int rshifts)                    /* I    Right shifts of correlations                                                */
    {
        int lag, i;
        int ptr1;                       /* C: pointer into x; here a position */
        ReadOnlySpan<short> ptr2;
        int inner_prod;

        ptr1 = order - 1; /* Points to first sample of column 0 of X: X[:,0] */
        ptr2 = t;
        /* Calculate X'*t */
        if (rshifts > 0)
        {
            /* Right shifting used */
            for (lag = 0; lag < order; lag++)
            {
                inner_prod = 0;
                for (i = 0; i < L; i++)
                {
                    inner_prod = silk_ADD_RSHIFT32(inner_prod, silk_SMULBB(x[ptr1 + i], ptr2[i]), rshifts);
                }
                Xt[lag] = inner_prod; /* X[:,lag]'*t */
                ptr1--; /* Go to next column of X */
            }
        }
        else
        {
            Debug.Assert(rshifts == 0);
            for (lag = 0; lag < order; lag++)
            {
                Xt[lag] = silk_inner_prod_aligned(x[ptr1..], ptr2, L); /* X[:,lag]'*t */
                ptr1--; /* Go to next column of X */
            }
        }
    }

    /* Calculates correlation matrix X'*X */
    internal static void silk_corrMatrix_FIX(
        ReadOnlySpan<short> x,          /* I    x vector [L + order - 1] used to form data matrix X                         */
        int L,                          /* I    Length of vectors                                                           */
        int order,                      /* I    Max lag for correlation                                                     */
        Span<int> XX,                   /* O    Pointer to X'*X correlation matrix [ order x order ]                        */
        out int nrg,                    /* O    Energy of x vector                                                            */
        out int rshifts)                /* O    Right shifts of correlations and energy                                     */
    {
        int i, j, lag;
        int energy;
        int ptr1, ptr2;                 /* C: pointers into x; here positions */

        /* Calculate energy to find shift used to fit in 32 bits */
        silk_sum_sqr_shift(out nrg, out rshifts, x, L + order - 1);
        energy = nrg;

        /* Calculate energy of first column (0) of X: X[:,0]'*X[:,0] */
        /* Remove contribution of first order - 1 samples */
        for (i = 0; i < order - 1; i++)
        {
            energy -= silk_RSHIFT32(silk_SMULBB(x[i], x[i]), rshifts);
        }

        /* Calculate energy of remaining columns of X: X[:,j]'*X[:,j] */
        /* Fill out the diagonal of the correlation matrix */
        XX[(0 * order) + 0] = energy;
        Debug.Assert(energy >= 0);
        ptr1 = order - 1; /* First sample of column 0 of X */
        for (j = 1; j < order; j++)
        {
            energy = silk_SUB32(energy, silk_RSHIFT32(silk_SMULBB(x[ptr1 + L - j], x[ptr1 + L - j]), rshifts));
            energy = silk_ADD32(energy, silk_RSHIFT32(silk_SMULBB(x[ptr1 - j], x[ptr1 - j]), rshifts));
            XX[(j * order) + j] = energy;
            Debug.Assert(energy >= 0);
        }

        ptr2 = order - 2; /* First sample of column 1 of X */
        /* Calculate the remaining elements of the correlation matrix */
        if (rshifts > 0)
        {
            /* Right shifting used */
            for (lag = 1; lag < order; lag++)
            {
                /* Inner product of column 0 and column lag: X[:,0]'*X[:,lag] */
                energy = 0;
                for (i = 0; i < L; i++)
                {
                    energy += silk_RSHIFT32(silk_SMULBB(x[ptr1 + i], x[ptr2 + i]), rshifts);
                }
                /* Calculate remaining off diagonal: X[:,j]'*X[:,j + lag] */
                XX[(lag * order) + 0] = energy;
                XX[(0 * order) + lag] = energy;
                for (j = 1; j < (order - lag); j++)
                {
                    energy = silk_SUB32(energy, silk_RSHIFT32(silk_SMULBB(x[ptr1 + L - j], x[ptr2 + L - j]), rshifts));
                    energy = silk_ADD32(energy, silk_RSHIFT32(silk_SMULBB(x[ptr1 - j], x[ptr2 - j]), rshifts));
                    XX[((lag + j) * order) + j] = energy;
                    XX[(j * order) + lag + j] = energy;
                }
                ptr2--; /* Update pointer to first sample of next column (lag) in X */
            }
        }
        else
        {
            for (lag = 1; lag < order; lag++)
            {
                /* Inner product of column 0 and column lag: X[:,0]'*X[:,lag] */
                energy = silk_inner_prod_aligned(x[ptr1..], x[ptr2..], L);
                XX[(lag * order) + 0] = energy;
                XX[(0 * order) + lag] = energy;
                /* Calculate remaining off diagonal: X[:,j]'*X[:,j + lag] */
                for (j = 1; j < (order - lag); j++)
                {
                    energy = silk_SUB32(energy, silk_SMULBB(x[ptr1 + L - j], x[ptr2 + L - j]));
                    energy = silk_SMLABB(energy, x[ptr1 - j], x[ptr2 - j]);
                    XX[((lag + j) * order) + j] = energy;
                    XX[(j * order) + lag + j] = energy;
                }
                ptr2--;/* Update pointer to first sample of next column (lag) in X */
            }
        }
    }
}
