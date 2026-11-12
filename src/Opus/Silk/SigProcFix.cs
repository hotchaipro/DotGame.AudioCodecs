/***********************************************************************
Copyright (c) 2006-2011, Skype Limited. All rights reserved.
Ported to C# from libopus 1.6.1 (silk/SigProc_FIX.h, silk/macros.h, silk/Inlines.h, silk/typedef.h) by Hot Chai Games LLC
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

using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace DotGame.AudioCodecs.Opus.Silk;

/// <summary>
/// The fixed-point macros and inline functions of silk/SigProc_FIX.h, silk/macros.h, silk/Inlines.h
/// and the limits of silk/typedef.h (generic C versions; OPUS_FAST_INT64 forms).
/// </summary>
internal static class SigProcFix
{
    /* typedef.h */
    internal const long silk_int64_MAX = 0x7FFFFFFFFFFFFFFFL;
    internal const long silk_int64_MIN = unchecked((long)0x8000000000000000UL);
    internal const int silk_int32_MAX = 0x7FFFFFFF;
    internal const int silk_int32_MIN = unchecked((int)0x80000000);
    internal const short silk_int16_MAX = 0x7FFF;
    internal const short silk_int16_MIN = unchecked((short)0x8000);
    internal const sbyte silk_int8_MAX = 0x7F;
    internal const sbyte silk_int8_MIN = unchecked((sbyte)0x80);
    internal const byte silk_uint8_MAX = 0xFF;

    internal const int silk_TRUE = 1;
    internal const int silk_FALSE = 0;

    /* SigProc_FIX.h */
    internal const int SILK_MAX_ORDER_LPC = 24;             /* max order of the LPC analysis in schur() and k2a() */

    /* PSEUDO-RANDOM GENERATOR                                                          */
    /* Make sure to store the result as the seed for the next call (also in between     */
    /* frames), otherwise result won't be random at all. When only using some of the    */
    /* bits, take the most significant bits by right-shifting.                          */
    internal const int RAND_MULTIPLIER = 196314165;
    internal const int RAND_INCREMENT = 907633515;

    /* macros.h */

    /* (a32 * (opus_int32)((opus_int16)(b32))) >> 16 output have to be 32bit int */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_SMULWB(int a32, int b32) => (int)((a32 * (long)(short)b32) >> 16);

    /* a32 + (b32 * (opus_int32)((opus_int16)(c32))) >> 16 output have to be 32bit int */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_SMLAWB(int a32, int b32, int c32) => (int)(a32 + ((b32 * (long)(short)c32) >> 16));

    /* (a32 * (b32 >> 16)) >> 16 */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_SMULWT(int a32, int b32) => (int)((a32 * (long)(b32 >> 16)) >> 16);

    /* a32 + (b32 * (c32 >> 16)) >> 16 */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_SMLAWT(int a32, int b32, int c32) => (int)(a32 + ((b32 * ((long)c32 >> 16)) >> 16));

    /* (opus_int32)((opus_int16)(a3))) * (opus_int32)((opus_int16)(b32)) output have to be 32bit int */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_SMULBB(int a32, int b32) => (short)a32 * (short)b32;

    /* a32 + (opus_int32)((opus_int16)(b32)) * (opus_int32)((opus_int16)(c32)) output have to be 32bit int */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_SMLABB(int a32, int b32, int c32) => a32 + ((short)b32 * (short)c32);

    /* (opus_int32)((opus_int16)(a32)) * (b32 >> 16) */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_SMULBT(int a32, int b32) => (short)a32 * (b32 >> 16);

    /* a32 + (opus_int32)((opus_int16)(b32)) * (c32 >> 16) */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_SMLABT(int a32, int b32, int c32) => a32 + ((short)b32 * (c32 >> 16));

    /* a64 + (b32 * c32) */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long silk_SMLAL(long a64, int b32, int c32) => silk_ADD64(a64, (long)b32 * (long)c32);

    /* (a32 * b32) >> 16 */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_SMULWW(int a32, int b32) => (int)(((long)a32 * b32) >> 16);

    /* a32 + ((b32 * c32) >> 16) */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_SMLAWW(int a32, int b32, int c32) => (int)(a32 + (((long)b32 * c32) >> 16));

    /* add/subtract with output saturated */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_ADD_SAT32(int a, int b)
    {
        return (((uint)a + (uint)b) & 0x80000000) == 0
            ? ((((uint)a & (uint)b) & 0x80000000) != 0 ? silk_int32_MIN : a + b)
            : ((((uint)a | (uint)b) & 0x80000000) == 0 ? silk_int32_MAX : a + b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_SUB_SAT32(int a, int b)
    {
        return (((uint)a - (uint)b) & 0x80000000) == 0
            ? (((uint)a & ((uint)b ^ 0x80000000) & 0x80000000) != 0 ? silk_int32_MIN : a - b)
            : ((((uint)a ^ 0x80000000) & (uint)b & 0x80000000) != 0 ? silk_int32_MAX : a - b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int EC_ILOG(uint x) => 32 - BitOperations.LeadingZeroCount(x);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_CLZ16(short in16) => 32 - EC_ILOG((uint)((in16 << 16) | 0x8000));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_CLZ32(int in32) => in32 != 0 ? 32 - EC_ILOG((uint)in32) : 32;

    /* Inlines.h */

    /* count leading zeros of opus_int64 */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_CLZ64(long @in)
    {
        int in_upper;

        in_upper = (int)silk_RSHIFT64(@in, 32);
        if (in_upper == 0)
        {
            /* Search in the lower 32 bits */
            return 32 + silk_CLZ32((int)@in);
        }
        else
        {
            /* Search in the upper 32 bits */
            return silk_CLZ32(in_upper);
        }
    }

    /* get number of leading zeros and fractional part (the bits right after the leading one */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void silk_CLZ_FRAC(
        int @in,            /* I  input                               */
        out int lz,         /* O  number of leading zeros             */
        out int frac_Q7)    /* O  the 7 bits right after the leading one */
    {
        int lzeros = silk_CLZ32(@in);

        lz = lzeros;
        frac_Q7 = silk_ROR32(@in, 24 - lzeros) & 0x7f;
    }

    /* Approximation of square root                                          */
    /* Accuracy: < +/- 10%  for output values > 15                           */
    /*           < +/- 2.5% for output values > 120                          */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_SQRT_APPROX(int x)
    {
        int y, lz, frac_Q7;

        if (x <= 0)
        {
            return 0;
        }

        silk_CLZ_FRAC(x, out lz, out frac_Q7);

        if ((lz & 1) != 0)
        {
            y = 32768;
        }
        else
        {
            y = 46214;        /* 46214 = sqrt(2) * 32768 */
        }

        /* get scaling right */
        y >>= silk_RSHIFT(lz, 1);

        /* increment using fractional part of input */
        y = silk_SMLAWB(y, y, silk_SMULBB(213, frac_Q7));

        return y;
    }

    /* Divide two int32 values and return result as int32 in a given Q-domain */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_DIV32_varQ(   /* O    returns a good approximation of "(a32 << Qres) / b32" */
        int a32,                           /* I    numerator (Q0)                  */
        int b32,                           /* I    denominator (Q0)                */
        int Qres)                          /* I    Q-domain of result (>= 0)       */
    {
        int a_headrm, b_headrm, lshift;
        int b32_inv, a32_nrm, b32_nrm, result;

        Debug.Assert(b32 != 0);
        Debug.Assert(Qres >= 0);

        /* Compute number of bits head room and normalize inputs */
        a_headrm = silk_CLZ32(silk_abs(a32)) - 1;
        a32_nrm = silk_LSHIFT(a32, a_headrm);                                       /* Q: a_headrm                  */
        b_headrm = silk_CLZ32(silk_abs(b32)) - 1;
        b32_nrm = silk_LSHIFT(b32, b_headrm);                                       /* Q: b_headrm                  */

        /* Inverse of b32, with 14 bits of precision */
        b32_inv = silk_DIV32_16(silk_int32_MAX >> 2, silk_RSHIFT(b32_nrm, 16));   /* Q: 29 + 16 - b_headrm        */

        /* First approximation */
        result = silk_SMULWB(a32_nrm, b32_inv);                                     /* Q: 29 + a_headrm - b_headrm  */

        /* Compute residual by subtracting product of denominator and first approximation */
        /* It's OK to overflow because the final value of a32_nrm should always be small */
        a32_nrm = silk_SUB32_ovflw(a32_nrm, silk_LSHIFT_ovflw(silk_SMMUL(b32_nrm, result), 3));  /* Q: a_headrm   */

        /* Refinement */
        result = silk_SMLAWB(result, a32_nrm, b32_inv);                             /* Q: 29 + a_headrm - b_headrm  */

        /* Convert to Qres domain */
        lshift = 29 + a_headrm - b_headrm - Qres;
        if (lshift < 0)
        {
            return silk_LSHIFT_SAT32(result, -lshift);
        }
        else
        {
            if (lshift < 32)
            {
                return silk_RSHIFT(result, lshift);
            }
            else
            {
                /* Avoid undefined result */
                return 0;
            }
        }
    }

    /* Invert int32 value and return result as int32 in a given Q-domain */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_INVERSE32_varQ(   /* O    returns a good approximation of "(1 << Qres) / b32" */
        int b32,                               /* I    denominator (Q0)                */
        int Qres)                              /* I    Q-domain of result (> 0)        */
    {
        int b_headrm, lshift;
        int b32_inv, b32_nrm, err_Q32, result;

        Debug.Assert(b32 != 0);
        Debug.Assert(Qres > 0);

        /* Compute number of bits head room and normalize input */
        b_headrm = silk_CLZ32(silk_abs(b32)) - 1;
        b32_nrm = silk_LSHIFT(b32, b_headrm);                                       /* Q: b_headrm                */

        /* Inverse of b32, with 14 bits of precision */
        b32_inv = silk_DIV32_16(silk_int32_MAX >> 2, silk_RSHIFT(b32_nrm, 16));   /* Q: 29 + 16 - b_headrm    */

        /* First approximation */
        result = silk_LSHIFT(b32_inv, 16);                                          /* Q: 61 - b_headrm            */

        /* Compute residual by subtracting product of denominator and first approximation from one */
        err_Q32 = silk_LSHIFT((1 << 29) - silk_SMULWB(b32_nrm, b32_inv), 3);        /* Q32                        */

        /* Refinement */
        result = silk_SMLAWW(result, err_Q32, b32_inv);                             /* Q: 61 - b_headrm            */

        /* Convert to Qres domain */
        lshift = 61 - b_headrm - Qres;
        if (lshift <= 0)
        {
            return silk_LSHIFT_SAT32(result, -lshift);
        }
        else
        {
            if (lshift < 32)
            {
                return silk_RSHIFT(result, lshift);
            }
            else
            {
                /* Avoid undefined result */
                return 0;
            }
        }
    }

    /* SigProc_FIX.h */

    /* Rotate a32 right by 'rot' bits. Negative rot values result in rotating
       left. Output is 32bit int. */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_ROR32(int a32, int rot)
    {
        uint x = (uint)a32;
        uint r = (uint)rot;
        uint m = (uint)-rot;
        if (rot == 0)
        {
            return a32;
        }
        else if (rot < 0)
        {
            return (int)((x << (int)m) | (x >> (int)(32 - m)));
        }
        else
        {
            return (int)((x << (int)(32 - r)) | (x >> (int)r));
        }
    }

    /* (a32 * b32) output have to be 32bit int */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_MUL(int a32, int b32) => a32 * b32;

    /* a32 + (b32 * c32) output have to be 32bit int */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_MLA(int a32, int b32, int c32) => silk_ADD32(a32, b32 * c32);

    /* ((a32 >> 16)  * (b32 >> 16)) output have to be 32bit int */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_SMULTT(int a32, int b32) => (a32 >> 16) * (b32 >> 16);

    /* a32 + ((a32 >> 16)  * (b32 >> 16)) output have to be 32bit int */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_SMLATT(int a32, int b32, int c32) => silk_ADD32(a32, (b32 >> 16) * (c32 >> 16));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long silk_SMLALBB(long a64, short b16, short c16) => silk_ADD64(a64, (long)(b16 * c16));

    /* (a32 * b32) */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long silk_SMULL(int a32, int b32) => (long)a32 * b32;

    /* Adds two signed 32-bit values in a way that can overflow, while not relying on undefined behaviour
       (just standard two's complement implementation-specific behaviour) */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_ADD32_ovflw(int a, int b) => (int)((uint)a + (uint)b);

    /* Subtractss two signed 32-bit values in a way that can overflow, while not relying on undefined behaviour
       (just standard two's complement implementation-specific behaviour) */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_SUB32_ovflw(int a, int b) => (int)((uint)a - (uint)b);

    /* Multiply-accumulate macros that allow overflow in the addition (ie, no asserts in debug mode) */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_MLA_ovflw(int a32, int b32, int c32) => silk_ADD32_ovflw(a32, (int)((uint)b32 * (uint)c32));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_SMLABB_ovflw(int a32, int b32, int c32) => silk_ADD32_ovflw(a32, (short)b32 * (short)c32);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_DIV32_16(int a32, int b16) => a32 / b16;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_DIV32(int a32, int b32) => a32 / b32;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_ADD16(int a, int b) => a + b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_ADD32(int a, int b) => a + b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long silk_ADD64(long a, long b) => a + b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_SUB16(int a, int b) => a - b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_SUB32(int a, int b) => a - b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long silk_SUB64(long a, long b) => a - b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_SAT8(int a) => a > silk_int8_MAX ? silk_int8_MAX : (a < silk_int8_MIN ? silk_int8_MIN : a);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_SAT16(int a) => a > silk_int16_MAX ? silk_int16_MAX : (a < silk_int16_MIN ? silk_int16_MIN : a);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long silk_SAT32(long a) => a > silk_int32_MAX ? silk_int32_MAX : (a < silk_int32_MIN ? silk_int32_MIN : a);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static short silk_ADD_SAT16(int a, int b) => (short)silk_SAT16(silk_ADD32(a, b));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static short silk_SUB_SAT16(int a, int b) => (short)silk_SAT16(silk_SUB32(a, b));

    /* Saturation for positive input values */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long silk_POS_SAT32(long a) => a > silk_int32_MAX ? silk_int32_MAX : a;

    /* Add with saturation for positive input values */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_ADD_POS_SAT32(int a, int b) => (((uint)a + (uint)b) & 0x80000000) != 0 ? silk_int32_MAX : a + b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static short silk_LSHIFT16(int a, int shift) => (short)((ushort)a << shift);   /* shift >= 0, shift < 16 */

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_LSHIFT32(int a, int shift) => (int)((uint)a << shift);         /* shift >= 0, shift < 32 */

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long silk_LSHIFT64(long a, int shift) => (long)((ulong)a << shift);     /* shift >= 0, shift < 64 */

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_LSHIFT(int a, int shift) => silk_LSHIFT32(a, shift);           /* shift >= 0, shift < 32 */

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_RSHIFT32(int a, int shift) => a >> shift;                       /* shift >= 0, shift < 32 */

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long silk_RSHIFT64(long a, int shift) => a >> shift;                     /* shift >= 0, shift < 64 */

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_RSHIFT(int a, int shift) => silk_RSHIFT32(a, shift);           /* shift >= 0, shift < 32 */

    /* saturates before shifting */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_LSHIFT_SAT32(int a, int shift) =>
        silk_LSHIFT32(silk_LIMIT(a, silk_RSHIFT32(silk_int32_MIN, shift), silk_RSHIFT32(silk_int32_MAX, shift)), shift);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_LSHIFT_ovflw(int a, int shift) => (int)((uint)a << shift);     /* shift >= 0, allowed to overflow */

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint silk_LSHIFT_uint(uint a, int shift) => a << shift;                 /* shift >= 0 */

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint silk_RSHIFT_uint(uint a, int shift) => a >> shift;                 /* shift >= 0 */

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_ADD_LSHIFT(int a, int b, int shift) => a + silk_LSHIFT(b, shift);               /* shift >= 0 */

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_ADD_LSHIFT32(int a, int b, int shift) => silk_ADD32(a, silk_LSHIFT32(b, shift)); /* shift >= 0 */

    /* C: (a) + silk_RSHIFT_uint((b), (shift)) with an int a and an opus_uint32 b: unsigned arithmetic */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint silk_ADD_RSHIFT_uint(int a, uint b, int shift) => (uint)a + silk_RSHIFT_uint(b, shift); /* shift >= 0 */

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_ADD_RSHIFT(int a, int b, int shift) => a + silk_RSHIFT(b, shift);               /* shift >= 0 */

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_ADD_RSHIFT32(int a, int b, int shift) => silk_ADD32(a, silk_RSHIFT32(b, shift)); /* shift >= 0 */

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_SUB_LSHIFT32(int a, int b, int shift) => silk_SUB32(a, silk_LSHIFT32(b, shift)); /* shift >= 0 */

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_SUB_RSHIFT32(int a, int b, int shift) => silk_SUB32(a, silk_RSHIFT32(b, shift)); /* shift >= 0 */

    /* Requires that shift > 0 */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_RSHIFT_ROUND(int a, int shift) =>
        shift == 1 ? (a >> 1) + (a & 1) : ((a >> (shift - 1)) + 1) >> 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long silk_RSHIFT_ROUND64(long a, int shift) =>
        shift == 1 ? (a >> 1) + (a & 1) : ((a >> (shift - 1)) + 1) >> 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_min(int a, int b) => (a < b) ? a : b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_max(int a, int b) => (a > b) ? a : b;

    /* silk_min() versions with typecast in the function call */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_min_int(int a, int b) => (a < b) ? a : b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static short silk_min_16(short a, short b) => (a < b) ? a : b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_min_32(int a, int b) => (a < b) ? a : b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long silk_min_64(long a, long b) => (a < b) ? a : b;

    /* silk_min() versions with typecast in the function call */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_max_int(int a, int b) => (a > b) ? a : b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static short silk_max_16(short a, short b) => (a > b) ? a : b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_max_32(int a, int b) => (a > b) ? a : b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long silk_max_64(long a, long b) => (a > b) ? a : b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_LIMIT(int a, int limit1, int limit2) =>
        limit1 > limit2
            ? (a > limit1 ? limit1 : (a < limit2 ? limit2 : a))
            : (a > limit2 ? limit2 : (a < limit1 ? limit1 : a));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_LIMIT_int(int a, int limit1, int limit2) => silk_LIMIT(a, limit1, limit2);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_LIMIT_16(int a, int limit1, int limit2) => silk_LIMIT(a, limit1, limit2);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_LIMIT_32(int a, int limit1, int limit2) => silk_LIMIT(a, limit1, limit2);

    /* Be careful, silk_abs returns wrong when input equals to silk_intXX_MIN */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_abs(int a) => (a ^ (a >> 31)) - (a >> 31);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_abs_int32(int a) => (a ^ (a >> 31)) - (a >> 31);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long silk_abs_int64(long a) => (a > 0) ? a : -a;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_sign(int a) => a > 0 ? 1 : (a < 0 ? -1 : 0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_RAND(int seed) => silk_MLA_ovflw(RAND_INCREMENT, seed, RAND_MULTIPLIER);

    /*    silk_SMMUL: Signed top word multiply. */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int silk_SMMUL(int a32, int b32) => (int)silk_RSHIFT64(silk_SMULL(a32, b32), 32);
}
