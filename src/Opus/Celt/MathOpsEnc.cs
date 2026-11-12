/* Copyright (c) 2002-2008 Jean-Marc Valin
   Copyright (c) 2007-2008 CSIRO
   Copyright (c) 2007-2009 Xiph.Org Foundation
   Copyright (c) 2024 Arm Limited
   Written by Jean-Marc Valin, and Yunho Huh
   Ported to C# from libopus 1.6.1 (celt/mathops.h, celt/mathops.c) by Hot Chai Games LLC */
/*
   @file mathops.h
   @brief Various math functions
*/
/*
   Redistribution and use in source and binary forms, with or without
   modification, are permitted provided that the following conditions
   are met:

   - Redistributions of source code must retain the above copyright
   notice, this list of conditions and the following disclaimer.

   - Redistributions in binary form must reproduce the above copyright
   notice, this list of conditions and the following disclaimer in the
   documentation and/or other materials provided with the distribution.

   THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
   ``AS IS'' AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
   LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
   A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER
   OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL,
   EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO,
   PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR
   PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF
   LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
   NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
   SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
*/

using System.Diagnostics;
using System.Runtime.CompilerServices;
using static DotGame.AudioCodecs.Opus.Celt.Arch;
using static DotGame.AudioCodecs.Opus.Celt.MathOps;

namespace DotGame.AudioCodecs.Opus.Celt;

/// <summary>
/// The parts of celt/mathops.h and celt/mathops.c (fixed point, no QEXT) the encoder uses and the
/// decoder's <see cref="MathOps"/> doesn't have: the logarithm, the 32-bit square root and
/// reciprocal, and the arctangents.
/// </summary>
internal static class MathOpsEnc
{
    private static readonly short[] celt_log2_C = [-6801 + (1 << (13 - 10)), 15746, -5217, 2545, -1401];

    /** Base-2 logarithm approximation (log2(x)). (Q14 input, Q10 output) */
    internal static short celt_log2(int x)
    {
        int i;
        short n, frac;
        /* -0.41509302963303146, 0.9609890551383969, -0.31836011537636605,
            0.15530808010959576, -0.08556153059057618 */
        short[] C = celt_log2_C;
        if (x == 0)
        {
            return -32767;
        }
        i = celt_ilog2(x);
        n = (short)(VSHR32(x, i - 15) - 32768 - 16384);
        frac = ADD16(C[0], MULT16_16_Q15(n, ADD16(C[1], MULT16_16_Q15(n, ADD16(C[2], MULT16_16_Q15(n, ADD16(C[3], MULT16_16_Q15(n, C[4]))))))));
        return (short)(SHL32(i - 13, 10) + SHR32(frac, 14 - 10));
    }

    /* Without QEXT, the log-gain version goes through the Q10 one. */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int celt_log2_db(int x) => SHL32(EXTEND32(celt_log2(x)), DB_SHIFT - 10);

    /* Perform fixed-point arithmetic to approximate the square root. When the input
     * is in Qx format, the output will be in Q(x/2 + 16) format. */
    internal static int celt_sqrt32(int x)
    {
        int k;
        int x_frac;
        if (x == 0)
        {
            return 0;
        }
        else if (x >= 1073741824)
        {
            return 2147483647; /* 2^31 -1 */
        }
        k = celt_ilog2(x) >> 1;
        x_frac = VSHR32(x, (2 * (k - 14)) - 1);
        x_frac = MULT32_32_Q31(celt_rsqrt_norm32(x_frac), x_frac);
        if (k < 12)
        {
            return PSHR32(x_frac, 12 - k);
        }
        else
        {
            return SHL32(x_frac, k - 12);
        }
    }

    /* Computes a 32 bit approximated reciprocal (1/x) for a normalized Q31 input,
     * resulting in a Q30 output. The expected input range is [0.5f, 1.0f) in Q31
     * and the expected output range is [1.0f, 2.0f) in Q30. */
    internal static int celt_rcp_norm32(int x)
    {
        int r_q30;
        Debug.Assert(x >= 1073741824);
        r_q30 = SHL32(EXTEND32(celt_rcp_norm16((short)(SHR32(x, 15) - 32768))), 16);
        /* Solving f(y) = a - 1/y using the Newton Method
         * Note: f(y)' = 1/y^2
         * r = r - f(r)/f(r)' = r - (x * r*r - r)
         *   = r - r*(r*x - 1)
         * where
         *   - r means 1/y's approximation.
         *   - x means a, the input of function.
         * Please note that:
         *   - It adds 1 to avoid overflow
         *   - -1.0f in Q30 is -1073741824. */
        return SUB32(r_q30, ADD32(SHL32(
                     MULT32_32_Q31(ADD32(MULT32_32_Q31(r_q30, x), -1073741824),
                                   r_q30), 1), 1));
    }

    /* Approximation constants of celt_atan_norm(). */
    private const int ATAN_2_OVER_PI = 1367130551;   /* Q31 */
    private const int ATAN_COEFF_A03 = -715791936;   /* Q31 */
    private const int ATAN_COEFF_A05 = 857391616;    /* Q32 */
    private const int ATAN_COEFF_A07 = -1200579328;  /* Q33 */
    private const int ATAN_COEFF_A09 = 1682636672;   /* Q34 */
    private const int ATAN_COEFF_A11 = -1985085440;  /* Q35 */
    private const int ATAN_COEFF_A13 = 1583306112;   /* Q36 */
    private const int ATAN_COEFF_A15 = -598602432;   /* Q37 */

    /* Computes atan(x) multiplied by 2/PI. The input value (x) should be within the
     * range of -1 to 1 and represented in Q30 format. The function will return the
     * result in Q30 format. */
    internal static int celt_atan_norm(int x)
    {
        int x_sq_q30;
        int x_q31;
        int tmp;
        /* The expected x is in the range of [-1.0f, 1.0f] */
        Debug.Assert((x <= 1073741824) && (x >= -1073741824));

        /* If x = 1.0f, returns 0.5f */
        if (x == 1073741824)
        {
            return 536870912; /* 0.5f (Q30) */
        }
        /* If x = 1.0f, returns 0.5f */
        if (x == -1073741824)
        {
            return -536870912; /* -0.5f (Q30) */
        }
        x_q31 = SHL32(x, 1);
        x_sq_q30 = MULT32_32_Q31(x_q31, x);
        /* Split evaluation in steps to avoid exploding macro expansion. */
        tmp = MULT32_32_Q31(x_sq_q30, ATAN_COEFF_A15);
        tmp = MULT32_32_Q31(x_sq_q30, ADD32(ATAN_COEFF_A13, tmp));
        tmp = MULT32_32_Q31(x_sq_q30, ADD32(ATAN_COEFF_A11, tmp));
        tmp = MULT32_32_Q31(x_sq_q30, ADD32(ATAN_COEFF_A09, tmp));
        tmp = MULT32_32_Q31(x_sq_q30, ADD32(ATAN_COEFF_A07, tmp));
        tmp = MULT32_32_Q31(x_sq_q30, ADD32(ATAN_COEFF_A05, tmp));
        tmp = MULT32_32_Q31(x_sq_q30, ADD32(ATAN_COEFF_A03, tmp));
        tmp = ADD32(x, MULT32_32_Q31(x_q31, tmp));
        return MULT32_32_Q31(ATAN_2_OVER_PI, tmp);
    }

    /* Calculates the arctangent of y/x, multiplies the result by 2/pi, and returns
     * the value in Q30 format. Both input values (x and y) must be within the range
     * of 0 to 1 and represented in Q30 format. Inputs must be zero or greater, and
     * at least one input must be non-zero. */
    internal static int celt_atan2p_norm(int y, int x)
    {
        Debug.Assert(x >= 0 && y >= 0);
        if (y == 0 && x == 0)
        {
            return 0;
        }
        else if (y < x)
        {
            return celt_atan_norm(SHR32(frac_div32(y, x), 1));
        }
        else
        {
            Debug.Assert(y > 0);
            return 1073741824 /* 1.0f Q30 */ -
                   celt_atan_norm(SHR32(frac_div32(x, y), 1));
        }
    }

    private const int M1 = 32767;
    private const int M2 = -21;
    private const int M3 = -11943;
    private const int M4 = 4936;

    /* Atan approximation using a 4th order polynomial. Input is in Q15 format
       and normalized by pi/4. Output is in Q15 format */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static short celt_atan01(short x)
    {
        return (short)MULT16_16_P15(x, ADD32(M1, MULT16_16_P15(x, ADD32(M2, MULT16_16_P15(x, ADD32(M3, MULT16_16_P15(M4, x)))))));
    }

    /* atan2() approximation valid for positive input values */
    internal static short celt_atan2p(short y, short x)
    {
        if (x == 0 && y == 0)
        {
            return 0;
        }
        else if (y < x)
        {
            int arg;
            arg = celt_div(SHL32(EXTEND32(y), 15), x);
            if (arg >= 32767)
            {
                arg = 32767;
            }
            return (short)SHR16(celt_atan01(EXTRACT16(arg)), 1);
        }
        else
        {
            int arg;
            arg = celt_div(SHL32(EXTEND32(x), 15), y);
            if (arg >= 32767)
            {
                arg = 32767;
            }
            return (short)(25736 - SHR16(celt_atan01(EXTRACT16(arg)), 1));
        }
    }

    /* Approximation constants of celt_cos_norm32(). */
    private const int COS_NORM_COEFF_A0 = 134217720;   /* Q27 */
    private const int COS_NORM_COEFF_A1 = -662336704;  /* Q29 */
    private const int COS_NORM_COEFF_A2 = 544710848;   /* Q31 */
    private const int COS_NORM_COEFF_A3 = -178761936;  /* Q33 */
    private const int COS_NORM_COEFF_A4 = 29487206;    /* Q35 */

    /* Calculates the cosine of (PI*0.5*x) where the input x ranges from -1 to 1 and
     * is in Q30 format. The output will also be in Q31 format. */
    internal static int celt_cos_norm32(int x)
    {
        int x_sq_q29, tmp;
        /* The expected x is in the range of [-1.0f, 1.0f] */
        Debug.Assert((x >= -1073741824) && (x <= 1073741824));
        /* Make cos(+/- pi/2) exactly zero. */
        if (ABS32(x) == 1 << 30)
        {
            return 0;
        }
        x_sq_q29 = MULT32_32_Q31(x, x);
        /* Split evaluation in steps to avoid exploding macro expansion. */
        tmp = ADD32(COS_NORM_COEFF_A3, MULT32_32_Q31(x_sq_q29, COS_NORM_COEFF_A4));
        tmp = ADD32(COS_NORM_COEFF_A2, MULT32_32_Q31(x_sq_q29, tmp));
        tmp = ADD32(COS_NORM_COEFF_A1, MULT32_32_Q31(x_sq_q29, tmp));
        return SHL32(ADD32(COS_NORM_COEFF_A0, MULT32_32_Q31(x_sq_q29, tmp)), 4);
    }
}
