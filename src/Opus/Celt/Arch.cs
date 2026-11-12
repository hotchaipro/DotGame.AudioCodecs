/* Copyright (c) 2003-2008 Jean-Marc Valin
   Copyright (c) 2007-2008 CSIRO
   Copyright (c) 2007-2009 Xiph.Org Foundation
   Written by Jean-Marc Valin
   Ported to C# from libopus 1.6.1 (celt/arch.h, celt/fixed_generic.h) by Hot Chai Games LLC */
/*
   @file arch.h
   @brief Various architecture definitions for CELT
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

namespace DotGame.AudioCodecs.Opus.Celt;

/// <summary>
/// celt/arch.h and celt/fixed_generic.h for the fixed-point build (FIXED_POINT, ENABLE_RES24, no
/// QEXT): the types' constants and the arithmetic macros, with C's integer semantics. The macros
/// take int arguments and apply the same casts as C's macros do; where fixed_generic.h has a choice
/// they use its opus_int64 forms (OPUS_FAST_INT64).
/// </summary>
internal static class Arch
{
    internal const int Q15ONE = 32767;
    internal const int Q31ONE = 2147483647;

    internal const int SIG_SHIFT = 12;

    /* Safe saturation value for 32-bit signals. We need to make sure that we can
       add two sig values and that the first stages of the MDCT don't cause an overflow.
       The most constraining is the ARM_ASM comb filter where we shift left by one
       and then add two values. Because of that, we use 2^29-1. SIG_SAT must be large
       enough to fit a full-scale high-freq tone through the prefilter and comb filter,
       meaning 1.85*1.75*2^(15+SIG_SHIFT) =  434529895.
       so the limit should be about 2^31*sqrt(.5). */
    internal const int SIG_SAT = 536870911;

    internal const int NORM_SHIFT = 24;
    internal const int NORM_SCALING = 1 << NORM_SHIFT;

    internal const int DB_SHIFT = 24;

    internal const int EPSILON = 1;
    internal const int VERY_SMALL = 0;
    internal const short VERY_LARGE16 = 32767;
    internal const short Q15_ONE = 32767;

    /* celt_coef is opus_val16 without QEXT. */
    internal const int COEF_ONE = Q15ONE;

    /* ENABLE_RES24: opus_res is a 24-bit integer in an opus_val32. */
    internal const int RES_SHIFT = 8;
    internal const int MAX_ENCODING_DEPTH = 24;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int MIN16(int a, int b) => a < b ? a : b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int MAX16(int a, int b) => a > b ? a : b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int MIN32(int a, int b) => a < b ? a : b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int MAX32(int a, int b) => a > b ? a : b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int IMIN(int a, int b) => a < b ? a : b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int IMAX(int a, int b) => a > b ? a : b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int MAXG(int a, int b) => MAX32(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int MING(int a, int b) => MIN32(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int ABS16(int x) => (x ^ (x >> 31)) - (x >> 31);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int ABS32(int x) => (x ^ (x >> 31)) - (x >> 31);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static short SAT16(int x) => x > 32767 ? (short)32767 : x < -32768 ? (short)-32768 : (short)x;

    /* Multiply a 16-bit signed value by a 16-bit unsigned value. The result is a 32-bit signed value */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int MULT16_16SU(int a, int b) => (int)(short)a * (int)(ushort)b;

    /* 16x32 multiplication, followed by a 16-bit shift right. Results fits in 32 bits */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int MULT16_32_Q16(int a, int b) => (int)(((long)(short)a * b) >> 16);

    /* 16x32 multiplication, followed by a 16-bit shift right (round-to-nearest). Results fits in 32 bits */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int MULT16_32_P16(int a, int b) => (int)((((long)(short)a * b) + (1 << 15)) >> 16);

    /* 16x32 multiplication, followed by a 15-bit shift right. Results fits in 32 bits */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int MULT16_32_Q15(int a, int b) => (int)(((long)(short)a * b) >> 15);

    /* 32x32 multiplication, followed by a 16-bit shift right. Results fits in 32 bits */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int MULT32_32_Q16(int a, int b) => (int)(((long)a * (long)b) >> 16);

    /* 32x32 multiplication, followed by a 31-bit shift right. Results fits in 32 bits */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int MULT32_32_Q31(int a, int b) => (int)(((long)a * (long)b) >> 31);

    /* 32x32 multiplication, followed by a 31-bit shift right (with rounding). Results fits in 32 bits */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int MULT32_32_P31(int a, int b) => (int)((1073741824 + ((long)a * (long)b)) >> 31);

    /* 32x32 multiplication, followed by a 32-bit shift right. Results fits in 32 bits */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int MULT32_32_Q32(int a, int b) => (int)(((long)a * (long)b) >> 32);

    /* Negate a 16-bit value */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int NEG16(int x) => -x;

    /* Negate a 32-bit value */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int NEG32(int x) => -x;

    /* Change a 32-bit value into a 16-bit value. The value is assumed to fit in 16-bit, otherwise the result is undefined */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static short EXTRACT16(int x) => (short)x;

    /* Change a 16-bit value into a 32-bit value */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int EXTEND32(int x) => x;

    /* Arithmetic shift-right of a 16-bit value */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int SHR16(int a, int shift) => a >> shift;

    /* Arithmetic shift-left of a 16-bit value */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static short SHL16(int a, int shift) => (short)((ushort)a << shift);

    /* Arithmetic shift-right of a 32-bit value */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int SHR32(int a, int shift) => a >> shift;

    /* Arithmetic shift-left of a 32-bit value */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int SHL32(int a, int shift) => (int)((uint)a << shift);

    /* 32-bit arithmetic shift right with rounding-to-nearest instead of rounding down */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int PSHR32(int a, int shift) => SHR32(a + ((1 << shift) >> 1), shift);

    /* 32-bit arithmetic shift right where the argument can be negative */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int VSHR32(int a, int shift) => shift > 0 ? SHR32(a, shift) : SHL32(a, -shift);

    /* "RAW" macros, should not be used outside of this header file */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int SHR(int a, int shift) => a >> shift;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int SHL(int a, int shift) => SHL32(a, shift);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int PSHR(int a, int shift) => SHR(a + ((1 << shift) >> 1), shift);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int SATURATE(int x, int a) => x > a ? a : x < -a ? -a : x;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static short SATURATE16(int x) => EXTRACT16(x > 32767 ? 32767 : x < -32768 ? -32768 : x);

    /* Shift by a and round-to-nearest 32-bit value. Result is a 16-bit value */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static short ROUND16(int x, int a) => EXTRACT16(PSHR32(x, a));

    /* Shift by a and round-to-nearest 32-bit value. Result is a saturated 16-bit value */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static short SROUND16(int x, int a) => EXTRACT16(SATURATE(PSHR32(x, a), 32767));

    /* Divide by two */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int HALF16(int x) => SHR16(x, 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int HALF32(int x) => SHR32(x, 1);

    /* Add two 16-bit values */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static short ADD16(int a, int b) => (short)((short)a + (short)b);

    /* Subtract two 16-bit values */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int SUB16(int a, int b) => (short)a - (short)b;

    /* Add two 32-bit values */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int ADD32(int a, int b) => a + b;

    /* Subtract two 32-bit values */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int SUB32(int a, int b) => a - b;

    /* Add two 32-bit values, ignore any overflows */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int ADD32_ovflw(int a, int b) => (int)((uint)a + (uint)b);

    /* Subtract two 32-bit values, ignore any overflows */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int SUB32_ovflw(int a, int b) => (int)((uint)a - (uint)b);

    /* Negate 32-bit value, ignore any overflows */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int NEG32_ovflw(int a) => (int)(0 - (uint)a);

    /* 32-bit shift left, ignoring overflows */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int SHL32_ovflw(int a, int shift) => SHL32(a, shift);

    /* 32-bit arithmetic shift right with rounding-to-nearest, ignoring overflows */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int PSHR32_ovflw(int a, int shift) => SHR32(ADD32_ovflw(a, (1 << shift) >> 1), shift);

    /* 16x16 multiplication where the result fits in 16 bits */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int MULT16_16_16(int a, int b) => (short)a * (short)b;

    /* 32x32 multiplication where the result fits in 32 bits */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int MULT32_32_32(int a, int b) => a * b;

    /* 16x16 multiplication where the result fits in 32 bits */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int MULT16_16(int a, int b) => (int)(short)a * (int)(short)b;

    /* 16x16 multiply-add where the result fits in 32 bits */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int MAC16_16(int c, int a, int b) => ADD32(c, MULT16_16(a, b));

    /* 16x32 multiply, followed by a 15-bit shift right and 32-bit add.
        b must fit in 31 bits.
        Result fits in 32 bits. */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int MAC16_32_Q15(int c, int a, int b) => ADD32(c, ADD32(MULT16_16(a, SHR(b, 15)), SHR(MULT16_16(a, b & 0x00007fff), 15)));

    /* 16x32 multiplication, followed by a 16-bit shift right and 32-bit add.
        Results fits in 32 bits */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int MAC16_32_Q16(int c, int a, int b) => ADD32(c, ADD32(MULT16_16(a, SHR(b, 16)), SHR(MULT16_16SU(a, b & 0x0000ffff), 16)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int MULT16_16_Q11_32(int a, int b) => SHR(MULT16_16(a, b), 11);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int MULT16_16_Q11(int a, int b) => SHR(MULT16_16(a, b), 11);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int MULT16_16_Q13(int a, int b) => SHR(MULT16_16(a, b), 13);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int MULT16_16_Q14(int a, int b) => SHR(MULT16_16(a, b), 14);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int MULT16_16_Q15(int a, int b) => SHR(MULT16_16(a, b), 15);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int MULT16_16_P13(int a, int b) => SHR(ADD32(4096, MULT16_16(a, b)), 13);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int MULT16_16_P14(int a, int b) => SHR(ADD32(8192, MULT16_16(a, b)), 14);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int MULT16_16_P15(int a, int b) => SHR(ADD32(16384, MULT16_16(a, b)), 15);

    /* Divide a 32-bit value by a 16-bit value. Result fits in 16 bits */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static short DIV32_16(int a, int b) => (short)(a / (short)b);

    /* Divide a 32-bit value by a 32-bit value. Result fits in 32 bits */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int DIV32(int a, int b) => a / b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static short SIG2WORD16(int x)
    {
        x = PSHR32(x, SIG_SHIFT);
        x = MAX32(x, -32768);
        x = MIN32(x, 32767);
        return EXTRACT16(x);
    }

    /* Signal-resolution conversions for ENABLE_RES24. */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int SIG2RES(int a) => PSHR32(a, SIG_SHIFT - RES_SHIFT);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static short RES2INT16(int a) => SAT16(PSHR32(a, RES_SHIFT));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int RES2INT24(int a) => a;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int INT16TORES(int a) => SHL32(EXTEND32(a), RES_SHIFT);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int INT24TORES(int a) => a;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int ADD_RES(int a, int b) => ADD32(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int RES2SIG(int a) => SHL32(a, SIG_SHIFT - RES_SHIFT);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int MULT16_RES_Q15(int a, int b) => MULT16_32_Q15(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static short RES2VAL16(int a) => RES2INT16(a);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int INT16TOSIG(int a) => SHL32(EXTEND32(a), SIG_SHIFT);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int INT24TOSIG(int a) => SHL32(a, SIG_SHIFT - 8);

    /* celt_coef is opus_val16 without QEXT. */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int MULT_COEF_32(int a, int b) => MULT16_32_Q15(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int MULT_COEF(int a, int b) => MULT16_16_Q15(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int MULT_COEF_TAPS(int a, int b) => MULT16_16_P15(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static short COEF2VAL16(short x) => x;
}

/// <summary>
/// Stands in for C's <c>celt_sig *x[2]</c>, an array of per-channel pointers, which a Span can't be
/// put in: <c>x[c]</c> works as in C.
/// </summary>
internal readonly ref struct celt_sig_ptrs
{
    private readonly Span<int> c0;
    private readonly Span<int> c1;

    internal celt_sig_ptrs(Span<int> c0, Span<int> c1)
    {
        this.c0 = c0;
        this.c1 = c1;
    }

    internal Span<int> this[int c] => c == 0 ? this.c0 : this.c1;
}
