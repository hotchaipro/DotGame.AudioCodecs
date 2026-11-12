/* Copyright (c) 2001-2011 Timothy B. Terriberry
   Copyright (c) 2008-2009 Xiph.Org Foundation
   Ported to C# from libopus 1.6.1 (celt/entdec.c, celt/entcode.c, celt/entcode.h,
   celt/mfrngcod.h) by Hot Chai Games LLC */
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
using System.Numerics;
using System.Runtime.CompilerServices;

namespace DotGame.AudioCodecs.Opus.Common;

/// <summary>
/// The range decoder: C's ec_dec, over a packet's bytes. It's a ref struct, passed by ref where C
/// passes an ec_dec*.
/// </summary>
internal ref struct ec_dec
{
    // mfrngcod.h and entcode.h
    internal const int EC_SYM_BITS = 8;
    internal const int EC_CODE_BITS = 32;
    internal const uint EC_SYM_MAX = (1U << EC_SYM_BITS) - 1;
    internal const int EC_CODE_SHIFT = EC_CODE_BITS - EC_SYM_BITS - 1;
    internal const uint EC_CODE_TOP = 1U << (EC_CODE_BITS - 1);
    internal const uint EC_CODE_BOT = EC_CODE_TOP >> EC_SYM_BITS;
    internal const int EC_CODE_EXTRA = ((EC_CODE_BITS - 2) % EC_SYM_BITS) + 1;
    internal const int EC_WINDOW_SIZE = sizeof(uint) * 8;
    internal const int EC_UINT_BITS = 8;
    internal const int BITRES = 3;

    /*Buffered input.*/
    internal ReadOnlySpan<byte> buf;
    /*The size of the buffer.*/
    internal uint storage;
    /*The offset at which the last byte containing raw bits was read.*/
    internal uint end_offs;
    /*Bits that will be read from the end.*/
    internal uint end_window;
    /*Number of valid bits in end_window.*/
    internal int nend_bits;
    /*The total number of whole bits read.
      This does not include partial bits currently in the range coder.*/
    internal int nbits_total;
    /*The offset at which the next range coder byte will be read.*/
    internal uint offs;
    /*The number of values in the current range.*/
    internal uint rng;
    /*The difference between the top of the current range and the input value, minus one.*/
    internal uint val;
    /*The saved normalization factor from ec_decode().*/
    internal uint ext;
    /*A buffered input symbol, awaiting carry propagation.*/
    internal int rem;
    /*Nonzero if an error occurred.*/
    internal int error;

    /// <summary>EC_ILOG: the number of bits needed to hold <paramref name="x"/>, which isn't 0.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int EC_ILOG(uint x) => 32 - BitOperations.LeadingZeroCount(x);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint celt_udiv(uint n, uint d) => n / d;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int celt_sudiv(int n, int d) => n / d;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ec_read_byte() => this.offs < this.storage ? this.buf[(int)this.offs++] : 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ec_read_byte_from_end() => this.end_offs < this.storage ? this.buf[(int)(this.storage - ++this.end_offs)] : 0;

    /*Normalizes the contents of val and rng so that rng lies entirely in the
       high-order symbol.*/
    private void ec_dec_normalize()
    {
        /*If the range is too small, rescale it and input some bits.*/
        while (this.rng <= EC_CODE_BOT)
        {
            int sym;
            this.nbits_total += EC_SYM_BITS;
            this.rng <<= EC_SYM_BITS;
            /*Use up the remaining bits from our last symbol.*/
            sym = this.rem;
            /*Read the next value from the input.*/
            this.rem = this.ec_read_byte();
            /*Take the rest of the bits we need from this new symbol.*/
            sym = ((sym << EC_SYM_BITS) | this.rem) >> (EC_SYM_BITS - EC_CODE_EXTRA);
            /*And subtract them from val, capped to be less than EC_CODE_TOP.*/
            this.val = ((this.val << EC_SYM_BITS) + (EC_SYM_MAX & ~(uint)sym)) & (EC_CODE_TOP - 1);
        }
    }

    internal void ec_dec_init(ReadOnlySpan<byte> _buf, uint _storage)
    {
        this.buf = _buf;
        this.storage = _storage;
        this.end_offs = 0;
        this.end_window = 0;
        this.nend_bits = 0;
        /*This is the offset from which ec_tell() will subtract partial bits.
          The final value after the ec_dec_normalize() call will be the same as in
           the encoder, but we have to compensate for the bits that are added there.*/
        this.nbits_total = EC_CODE_BITS + 1
            - ((EC_CODE_BITS - EC_CODE_EXTRA) / EC_SYM_BITS * EC_SYM_BITS);
        this.offs = 0;
        this.rng = 1U << EC_CODE_EXTRA;
        this.rem = this.ec_read_byte();
        this.val = this.rng - 1 - (uint)(this.rem >> (EC_SYM_BITS - EC_CODE_EXTRA));
        this.error = 0;
        /*Normalize the interval.*/
        this.ec_dec_normalize();
    }

    internal uint ec_decode(uint _ft)
    {
        uint s;
        this.ext = celt_udiv(this.rng, _ft);
        s = this.val / this.ext;
        return _ft - Math.Min(s + 1, _ft);
    }

    internal uint ec_decode_bin(uint _bits)
    {
        uint s;
        this.ext = this.rng >> (int)_bits;
        s = this.val / this.ext;
        return (1U << (int)_bits) - Math.Min(s + 1U, 1U << (int)_bits);
    }

    internal void ec_dec_update(uint _fl, uint _fh, uint _ft)
    {
        uint s;
        s = this.ext * (_ft - _fh);
        this.val -= s;
        this.rng = _fl > 0 ? this.ext * (_fh - _fl) : this.rng - s;
        this.ec_dec_normalize();
    }

    /*The probability of having a "one" is 1/(1<<_logp).*/
    internal int ec_dec_bit_logp(uint _logp)
    {
        uint r;
        uint d;
        uint s;
        int ret;
        r = this.rng;
        d = this.val;
        s = r >> (int)_logp;
        ret = d < s ? 1 : 0;
        if (ret == 0)
        {
            this.val = d - s;
        }
        this.rng = ret != 0 ? s : r - s;
        this.ec_dec_normalize();
        return ret;
    }

    internal int ec_dec_icdf(scoped ReadOnlySpan<byte> _icdf, uint _ftb)
    {
        uint r;
        uint d;
        uint s;
        uint t;
        int ret;
        s = this.rng;
        d = this.val;
        r = s >> (int)_ftb;
        ret = -1;
        do
        {
            t = s;
            s = r * _icdf[++ret];
        }
        while (d < s);
        this.val = d - s;
        this.rng = t - s;
        this.ec_dec_normalize();
        return ret;
    }

    internal int ec_dec_icdf16(scoped ReadOnlySpan<ushort> _icdf, uint _ftb)
    {
        uint r;
        uint d;
        uint s;
        uint t;
        int ret;
        s = this.rng;
        d = this.val;
        r = s >> (int)_ftb;
        ret = -1;
        do
        {
            t = s;
            s = r * _icdf[++ret];
        }
        while (d < s);
        this.val = d - s;
        this.rng = t - s;
        this.ec_dec_normalize();
        return ret;
    }

    internal uint ec_dec_uint(uint _ft)
    {
        uint ft;
        uint s;
        int ftb;
        /*In order to optimize EC_ILOG(), it is undefined for the value 0.*/
        _ft--;
        ftb = EC_ILOG(_ft);
        if (ftb > EC_UINT_BITS)
        {
            uint t;
            ftb -= EC_UINT_BITS;
            ft = (_ft >> ftb) + 1;
            s = this.ec_decode(ft);
            this.ec_dec_update(s, s + 1, ft);
            t = (s << ftb) | this.ec_dec_bits((uint)ftb);
            if (t <= _ft)
            {
                return t;
            }
            this.error = 1;
            return _ft;
        }
        else
        {
            _ft++;
            s = this.ec_decode(_ft);
            this.ec_dec_update(s, s + 1, _ft);
            return s;
        }
    }

    internal uint ec_dec_bits(uint _bits)
    {
        uint window;
        int available;
        uint ret;
        window = this.end_window;
        available = this.nend_bits;
        if ((uint)available < _bits)
        {
            do
            {
                window |= (uint)this.ec_read_byte_from_end() << available;
                available += EC_SYM_BITS;
            }
            while (available <= EC_WINDOW_SIZE - EC_SYM_BITS);
        }
        ret = window & (uint)((1UL << (int)_bits) - 1U);
        window = _bits < 32 ? window >> (int)_bits : 0;
        available -= (int)_bits;
        this.end_window = window;
        this.nend_bits = available;
        this.nbits_total += (int)_bits;
        return ret;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal readonly uint ec_range_bytes() => this.offs;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal readonly int ec_get_error() => this.error;

    /*Returns the number of bits "used" by the decoded symbols so far.*/
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal readonly int ec_tell() => this.nbits_total - EC_ILOG(this.rng);

    private static readonly uint[] correction = [35733, 38967, 42495, 46340, 50535, 55109, 60097, 65535];

    /*Returns the number of bits "used" by the decoded symbols so far, scaled by 2**BITRES.
      The faster version libopus compiles: a linear function, then a lookup to determine the
      exact transition thresholds.*/
    internal readonly uint ec_tell_frac()
    {
        uint nbits;
        uint r;
        int l;
        uint b;
        nbits = (uint)this.nbits_total << BITRES;
        l = EC_ILOG(this.rng);
        r = this.rng >> (l - 16);
        b = (r >> 12) - 8;
        b += r > correction[b] ? 1U : 0U;
        l = (l << 3) + (int)b;
        return nbits - (uint)l;
    }
}
