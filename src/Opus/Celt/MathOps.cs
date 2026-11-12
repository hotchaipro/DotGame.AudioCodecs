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

using System;
using System.Runtime.CompilerServices;
using DotGame.AudioCodecs.Opus.Common;
using static DotGame.AudioCodecs.Opus.Celt.Arch;

namespace DotGame.AudioCodecs.Opus.Celt;

/// <summary>
/// celt/mathops.h and celt/mathops.c: the fixed-point math approximations the decoder uses.
/// </summary>
internal static class MathOps
{
    /* Multiplies two 16-bit fractional values. Bit-exactness of this macro is important */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int FRAC_MUL16(int a, int b) => (16384 + ((int)(short)a * (short)b)) >> 15;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int celt_maxabs16(ReadOnlySpan<short> x, int len)
    {
        return Simd.MaxAbs16(x, len);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int celt_maxabs_res(ReadOnlySpan<int> x, int len)
    {
        /* opus_res should never reach such amplitude, so we should be safe. */
        return Simd.MaxAbs32(x, len);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int celt_maxabs32(ReadOnlySpan<int> x, int len)
    {
        return Simd.MaxAbs32(x, len);
    }

    /* Integer log in base2. Undefined for zero and negative numbers */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static short celt_ilog2(int x) => (short)(ec_dec.EC_ILOG((uint)x) - 1);

    /* Integer log in base2. Defined for zero, but not for negative numbers */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static short celt_zlog2(int x) => x <= 0 ? (short)0 : celt_ilog2(x);

    /*
     K0 = 1
     K1 = log(2)
     K2 = 3-4*log(2)
     K3 = 3*log(2) - 2
    */
    private const int D0 = 16383;
    private const int D1 = 22804;
    private const int D2 = 14819;
    private const int D3 = 10204;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int celt_exp2_frac(short x)
    {
        short frac;
        frac = SHL16(x, 4);
        return ADD16(D0, MULT16_16_Q15(frac, ADD16(D1, MULT16_16_Q15(frac, ADD16(D2, MULT16_16_Q15(D3, frac))))));
    }

    /* Base-2 exponential approximation (2^x). (Q10 input, Q16 output) */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int celt_exp2(short x)
    {
        int integer;
        short frac;
        integer = SHR16(x, 10);
        if (integer > 14)
        {
            return 0x7f000000;
        }
        else if (integer < -15)
        {
            return 0;
        }
        frac = (short)celt_exp2_frac((short)(x - SHL16(integer, 10)));
        return VSHR32(EXTEND32(frac), -integer - 2);
    }

    /* Without QEXT, the log-gain versions go through the Q10 ones. */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int celt_exp2_db_frac(int x) => SHL32(celt_exp2_frac((short)PSHR32(x, DB_SHIFT - 10)), 14);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int celt_exp2_db(int x) => celt_exp2((short)PSHR32(x, DB_SHIFT - 10));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int celt_div(int a, int b) => MULT32_32_Q31(a, celt_rcp(b));

    /*Compute floor(sqrt(_val)) with exact arithmetic.
      _val must be greater than 0.
      This has been tested on all possible 32-bit inputs greater than 0.*/
    internal static uint isqrt32(uint _val)
    {
        uint b;
        uint g;
        int bshift;
        /*Uses the second method from
           http://www.azillionmonkeys.com/qed/sqroot.html
          The main idea is to search for the largest binary digit b such that
           (g+b)*(g+b) <= _val, and add it to the solution g.*/
        g = 0;
        bshift = (ec_dec.EC_ILOG(_val) - 1) >> 1;
        b = 1U << bshift;
        do
        {
            uint t;
            t = ((g << 1) + b) << bshift;
            if (t <= _val)
            {
                g += b;
                _val -= t;
            }
            b >>= 1;
            bshift--;
        }
        while (bshift >= 0);
        return g;
    }

    internal static int frac_div32_q29(int a, int b)
    {
        short rcp;
        int result, rem;
        int shift = celt_ilog2(b) - 29;
        a = VSHR32(a, shift);
        b = VSHR32(b, shift);
        /* 16-bit reciprocal */
        rcp = ROUND16(celt_rcp(ROUND16(b, 16)), 3);
        result = MULT16_32_Q15(rcp, a);
        rem = PSHR32(a, 2) - MULT32_32_Q31(result, b);
        result = ADD32(result, SHL32(MULT16_32_Q15(rcp, rem), 2));
        return result;
    }

    internal static int frac_div32(int a, int b)
    {
        int result = frac_div32_q29(a, b);
        if (result >= 536870912)       /*  2^29 */
        {
            return 2147483647;          /*  2^31 - 1 */
        }
        else if (result <= -536870912) /* -2^29 */
        {
            return -2147483647;         /* -2^31 */
        }
        else
        {
            return SHL32(result, 2);
        }
    }

    /* Reciprocal sqrt approximation in the range [0.25,1) (Q16 in, Q14 out) */
    internal static short celt_rsqrt_norm(int x)
    {
        short n;
        short r;
        short r2;
        short y;
        /* Range of n is [-16384,32767] ([-0.5,1) in Q15). */
        n = (short)(x - 32768);
        /* Get a rough initial guess for the root.
           The optimal minimax quadratic approximation (using relative error) is
            r = 1.437799046117536+n*(-0.823394375837328+n*0.4096419668459485).
           Coefficients here, and the final result r, are Q14.*/
        r = ADD16(23557, MULT16_16_Q15(n, ADD16(-13490, MULT16_16_Q15(n, 6713))));
        /* We want y = x*r*r-1 in Q15, but x is 32-bit Q16 and r is Q14.
           We can compute the result from n and r using Q15 multiplies with some
            adjustment, carefully done to avoid overflow.
           Range of y is [-1564,1594]. */
        r2 = (short)MULT16_16_Q15(r, r);
        y = SHL16(SUB16(ADD16(MULT16_16_Q15(r2, n), r2), 16384), 1);
        /* Apply a 2nd-order Householder iteration: r += r*y*(y*0.375-0.5).
           This yields the Q14 reciprocal square root of the Q16 x, with a maximum
            relative error of 1.04956E-4, a (relative) RMSE of 2.80979E-5, and a
            peak absolute error of 2.26591/16384. */
        return ADD16(r, MULT16_16_Q15(r, MULT16_16_Q15(y,
                   SUB16(MULT16_16_Q15(y, 12288), 16384))));
    }

    /* Reciprocal sqrt approximation in the range [0.25,1) (Q31 in, Q29 out) */
    internal static int celt_rsqrt_norm32(int x)
    {
        int tmp;
        /* Use the first-order Newton-Raphson method to refine the root estimate.
         * r = r * (1.5 - 0.5*x*r*r) */
        int r_q29 = SHL32(celt_rsqrt_norm(SHR32(x, 31 - 16)), 15);
        /* Split evaluation in steps to avoid exploding macro expansion. */
        tmp = MULT32_32_Q31(r_q29, r_q29);
        tmp = MULT32_32_Q31(1073741824 /* Q31 */, tmp);
        tmp = MULT32_32_Q31(x, tmp);
        return SHL32(MULT32_32_Q31(r_q29, SUB32(201326592 /* Q27 */, tmp)), 4);
    }

    /* These coeffs are optimized in fixed-point to minimize both RMS and max error
       of sqrt(x) over .25<x<1 without exceeding 32767.
       The RMS error is 3.4e-5 and the max is 8.2e-5. */
    private static readonly short[] celt_sqrt_C = [23171, 11574, -2901, 1592, -1002, 336];

    /* Sqrt approximation (QX input, QX/2 output) */
    internal static int celt_sqrt(int x)
    {
        int k;
        short n;
        int rt;
        ReadOnlySpan<short> C = celt_sqrt_C;
        if (x == 0)
        {
            return 0;
        }
        else if (x >= 1073741824)
        {
            return 32767;
        }
        k = (celt_ilog2(x) >> 1) - 7;
        x = VSHR32(x, 2 * k);
        n = (short)(x - 32768);
        rt = ADD32(C[0], MULT16_16_Q15(n, ADD16(C[1], MULT16_16_Q15(n, ADD16(C[2],
                   MULT16_16_Q15(n, ADD16(C[3], MULT16_16_Q15(n, ADD16(C[4], MULT16_16_Q15(n, C[5]))))))))));
        rt = VSHR32(rt, 7 - k);
        return rt;
    }

    private const int L1 = 32767;
    private const int L2 = -7651;
    private const int L3 = 8277;
    private const int L4 = -626;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static short _celt_cos_pi_2(short x)
    {
        short x2;

        x2 = (short)MULT16_16_P15(x, x);
        return ADD16(1, MIN16(32766, ADD32(SUB16(L1, x2), MULT16_16_P15(x2, ADD32(L2, MULT16_16_P15(x2, ADD32(L3, MULT16_16_P15(L4, x2))))))));
    }

    internal static short celt_cos_norm(int x)
    {
        x = x & 0x0001ffff;
        if (x > SHL32(EXTEND32(1), 16))
        {
            x = SUB32(SHL32(EXTEND32(1), 17), x);
        }
        if ((x & 0x00007fff) != 0)
        {
            if (x < SHL32(EXTEND32(1), 15))
            {
                return _celt_cos_pi_2(EXTRACT16(x));
            }
            else
            {
                return (short)NEG16(_celt_cos_pi_2(EXTRACT16(65536 - x)));
            }
        }
        else
        {
            if ((x & 0x0000ffff) != 0)
            {
                return 0;
            }
            else if ((x & 0x0001ffff) != 0)
            {
                return -32767;
            }
            else
            {
                return 32767;
            }
        }
    }

    /* Computes a 16 bit approximate reciprocal (1/x) for a normalized Q15 input,
     * resulting in a Q15 output. */
    internal static short celt_rcp_norm16(short x)
    {
        short r;
        /* Start with a linear approximation:
           r = 1.8823529411764706-0.9411764705882353*n.
           The coefficients and the result are Q14 in the range [15420,30840].*/
        r = ADD16(30840, MULT16_16_Q15(-15420, x));
        /* Perform two Newton iterations:
           r -= r*((r*n)+(r-1.Q15))
              = r*((r*n)+(r-1.Q15)). */
        r = (short)SUB16(r, MULT16_16_Q15(r,
                  ADD16(MULT16_16_Q15(r, x), ADD16(r, -32768))));
        /* We subtract an extra 1 in the second iteration to avoid overflow; it also
            neatly compensates for truncation error in the rest of the process. */
        return (short)SUB16(r, ADD16(1, MULT16_16_Q15(r,
                     ADD16(MULT16_16_Q15(r, x), ADD16(r, -32768)))));
    }

    /* Reciprocal approximation (Q15 input, Q16 output) */
    internal static int celt_rcp(int x)
    {
        int i;
        short r;
        i = celt_ilog2(x);

        /* Compute the reciprocal of a Q15 number in the range [0, 1). */
        r = celt_rcp_norm16((short)(VSHR32(x, i - 15) - 32768));

        /* r is now the Q15 solution to 2/(n+1), with a maximum relative error
            of 7.05346E-5, a (relative) RMSE of 2.14418E-5, and a peak absolute
            error of 1.24665/32768. */
        return VSHR32(EXTEND32(r), i - 16);
    }
}
