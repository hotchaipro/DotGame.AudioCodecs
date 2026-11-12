/* Copyright (c) 2001-2011 Timothy B. Terriberry
   Copyright (c) 2008-2009 Xiph.Org Foundation
   Ported to C# from libopus 1.6.1 (celt/entenc.c, celt/entcode.c, celt/entcode.h,
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
using System.Runtime.CompilerServices;

namespace DotGame.AudioCodecs.Opus.Common;

/// <summary>
/// The range encoder: C's ec_enc, over a packet's bytes. It's a ref struct, passed by ref where C
/// passes an ec_enc*; a copy (C's <c>ec_enc save = *enc</c>) shares the buffer, as C's does.
/// </summary>
/// <remarks>
/// A range encoder. See entdec.c and the references for implementation details: G.N.N. Martin,
/// "Range encoding: an algorithm for removing redundancy from a digitised message" (1979), and
/// A. Moffat, R. Neal and I. H. Witten, "Arithmetic Coding Revisited" (1998).
/// </remarks>
internal ref struct ec_enc
{
    /*Buffered output.*/
    internal Span<byte> buf;
    /*The size of the buffer.*/
    internal uint storage;
    /*The offset at which the last byte containing raw bits was written.*/
    internal uint end_offs;
    /*Bits that will be written at the end.*/
    internal uint end_window;
    /*Number of valid bits in end_window.*/
    internal int nend_bits;
    /*The total number of whole bits written.
      This does not include partial bits currently in the range coder.*/
    internal int nbits_total;
    /*The offset at which the next range coder byte will be written.*/
    internal uint offs;
    /*The number of values in the current range.*/
    internal uint rng;
    /*The low end of the current range.*/
    internal uint val;
    /*The number of outstanding carry propagating symbols.*/
    internal uint ext;
    /*A buffered output symbol, awaiting carry propagation.*/
    internal int rem;
    /*Nonzero if an error occurred.*/
    internal int error;

    private int ec_write_byte(uint _value)
    {
        if (this.offs + this.end_offs >= this.storage)
        {
            return -1;
        }
        this.buf[(int)this.offs++] = (byte)_value;
        return 0;
    }

    private int ec_write_byte_at_end(uint _value)
    {
        if (this.offs + this.end_offs >= this.storage)
        {
            return -1;
        }
        this.buf[(int)(this.storage - ++this.end_offs)] = (byte)_value;
        return 0;
    }

    /*Outputs a symbol, with a carry bit.
      If there is a potential to propagate a carry over several symbols, they are
       buffered until it can be determined whether or not an actual carry will
       occur.
      If the counter for the buffered symbols overflows, then the stream becomes
       undecodable.
      This gives a theoretical limit of a few billion symbols in a single packet on
       32-bit systems.
      The alternative is to truncate the range in order to force a carry, but
       requires similar carry tracking in the decoder, needlessly slowing it down.*/
    private void ec_enc_carry_out(int _c)
    {
        if (_c != (int)ec_dec.EC_SYM_MAX)
        {
            /*No further carry propagation possible, flush buffer.*/
            int carry;
            carry = _c >> ec_dec.EC_SYM_BITS;
            /*Don't output a byte on the first write.
              This compare should be taken care of by branch-prediction thereafter.*/
            if (this.rem >= 0)
            {
                this.error |= this.ec_write_byte((uint)(this.rem + carry));
            }
            if (this.ext > 0)
            {
                uint sym;
                sym = (ec_dec.EC_SYM_MAX + (uint)carry) & ec_dec.EC_SYM_MAX;
                do
                {
                    this.error |= this.ec_write_byte(sym);
                }
                while (--this.ext > 0);
            }
            this.rem = (int)((uint)_c & ec_dec.EC_SYM_MAX);
        }
        else
        {
            this.ext++;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ec_enc_normalize()
    {
        /*If the range is too small, output some bits and rescale it.*/
        while (this.rng <= ec_dec.EC_CODE_BOT)
        {
            this.ec_enc_carry_out((int)(this.val >> ec_dec.EC_CODE_SHIFT));
            /*Move the next-to-high-order symbol into the high-order position.*/
            this.val = (this.val << ec_dec.EC_SYM_BITS) & (ec_dec.EC_CODE_TOP - 1);
            this.rng <<= ec_dec.EC_SYM_BITS;
            this.nbits_total += ec_dec.EC_SYM_BITS;
        }
    }

    internal void ec_enc_init(Span<byte> _buf, uint _size)
    {
        this.buf = _buf;
        this.end_offs = 0;
        this.end_window = 0;
        this.nend_bits = 0;
        /*This is the offset from which ec_tell() will subtract partial bits.*/
        this.nbits_total = ec_dec.EC_CODE_BITS + 1;
        this.offs = 0;
        this.rng = ec_dec.EC_CODE_TOP;
        this.rem = -1;
        this.val = 0;
        this.ext = 0;
        this.storage = _size;
        this.error = 0;
    }

    internal void ec_encode(uint _fl, uint _fh, uint _ft)
    {
        uint r;
        r = ec_dec.celt_udiv(this.rng, _ft);
        if (_fl > 0)
        {
            this.val += this.rng - (r * (_ft - _fl));
            this.rng = r * (_fh - _fl);
        }
        else
        {
            this.rng -= r * (_ft - _fh);
        }
        this.ec_enc_normalize();
    }

    internal void ec_encode_bin(uint _fl, uint _fh, uint _bits)
    {
        uint r;
        r = this.rng >> (int)_bits;
        if (_fl > 0)
        {
            this.val += this.rng - (r * ((1U << (int)_bits) - _fl));
            this.rng = r * (_fh - _fl);
        }
        else
        {
            this.rng -= r * ((1U << (int)_bits) - _fh);
        }
        this.ec_enc_normalize();
    }

    /*The probability of having a "one" is 1/(1<<_logp).*/
    internal void ec_enc_bit_logp(int _val, uint _logp)
    {
        uint r;
        uint s;
        uint l;
        r = this.rng;
        l = this.val;
        s = r >> (int)_logp;
        r -= s;
        if (_val != 0)
        {
            this.val = l + r;
        }
        this.rng = _val != 0 ? s : r;
        this.ec_enc_normalize();
    }

    internal void ec_enc_icdf(int _s, scoped ReadOnlySpan<byte> _icdf, uint _ftb)
    {
        uint r;
        r = this.rng >> (int)_ftb;
        if (_s > 0)
        {
            this.val += this.rng - (r * _icdf[_s - 1]);
            this.rng = r * (uint)(_icdf[_s - 1] - _icdf[_s]);
        }
        else
        {
            this.rng -= r * _icdf[_s];
        }
        this.ec_enc_normalize();
    }

    internal void ec_enc_icdf16(int _s, scoped ReadOnlySpan<ushort> _icdf, uint _ftb)
    {
        uint r;
        r = this.rng >> (int)_ftb;
        if (_s > 0)
        {
            this.val += this.rng - (r * _icdf[_s - 1]);
            this.rng = r * (uint)(_icdf[_s - 1] - _icdf[_s]);
        }
        else
        {
            this.rng -= r * _icdf[_s];
        }
        this.ec_enc_normalize();
    }

    internal void ec_enc_uint(uint _fl, uint _ft)
    {
        uint ft;
        uint fl;
        int ftb;
        /*In order to optimize EC_ILOG(), it is undefined for the value 0.*/
        System.Diagnostics.Debug.Assert(_ft > 1);
        _ft--;
        ftb = ec_dec.EC_ILOG(_ft);
        if (ftb > ec_dec.EC_UINT_BITS)
        {
            ftb -= ec_dec.EC_UINT_BITS;
            ft = (_ft >> ftb) + 1;
            fl = _fl >> ftb;
            this.ec_encode(fl, fl + 1, ft);
            this.ec_enc_bits(_fl & ((1U << ftb) - 1U), (uint)ftb);
        }
        else
        {
            this.ec_encode(_fl, _fl + 1, _ft + 1);
        }
    }

    internal void ec_enc_bits(uint _fl, uint _bits)
    {
        uint window;
        int used;
        window = this.end_window;
        used = this.nend_bits;
        System.Diagnostics.Debug.Assert(_bits > 0);
        if (used + _bits > ec_dec.EC_WINDOW_SIZE)
        {
            do
            {
                this.error |= this.ec_write_byte_at_end(window & ec_dec.EC_SYM_MAX);
                window >>= ec_dec.EC_SYM_BITS;
                used -= ec_dec.EC_SYM_BITS;
            }
            while (used >= ec_dec.EC_SYM_BITS);
        }
        window |= _fl << used;
        used += (int)_bits;
        this.end_window = window;
        this.nend_bits = used;
        this.nbits_total += (int)_bits;
    }

    internal void ec_enc_patch_initial_bits(uint _val, uint _nbits)
    {
        int shift;
        uint mask;
        System.Diagnostics.Debug.Assert(_nbits <= ec_dec.EC_SYM_BITS);
        shift = ec_dec.EC_SYM_BITS - (int)_nbits;
        mask = ((1U << (int)_nbits) - 1) << shift;
        if (this.offs > 0)
        {
            /*The first byte has been finalized.*/
            this.buf[0] = (byte)((this.buf[0] & ~mask) | (_val << shift));
        }
        else if (this.rem >= 0)
        {
            /*The first byte is still awaiting carry propagation.*/
            this.rem = (int)(((uint)this.rem & ~mask) | (_val << shift));
        }
        else if (this.rng <= (ec_dec.EC_CODE_TOP >> (int)_nbits))
        {
            /*The renormalization loop has never been run.*/
            this.val = (this.val & ~(mask << ec_dec.EC_CODE_SHIFT))
                | (_val << (ec_dec.EC_CODE_SHIFT + shift));
        }
        else
        {
            /*The encoder hasn't even encoded _nbits of data yet.*/
            this.error = -1;
        }
    }

    internal void ec_enc_shrink(uint _size)
    {
        System.Diagnostics.Debug.Assert(this.offs + this.end_offs <= _size);
        this.buf.Slice((int)(this.storage - this.end_offs), (int)this.end_offs)
            .CopyTo(this.buf.Slice((int)(_size - this.end_offs)));
        this.storage = _size;
    }

    internal void ec_enc_done()
    {
        uint window;
        int used;
        uint msk;
        uint end;
        int l;
        /*We output the minimum number of bits that ensures that the symbols encoded
           thus far will be decoded correctly regardless of the bits that follow.*/
        l = ec_dec.EC_CODE_BITS - ec_dec.EC_ILOG(this.rng);
        msk = (ec_dec.EC_CODE_TOP - 1) >> l;
        end = (this.val + msk) & ~msk;
        if ((end | msk) >= this.val + this.rng)
        {
            l++;
            msk >>= 1;
            end = (this.val + msk) & ~msk;
        }
        while (l > 0)
        {
            this.ec_enc_carry_out((int)(end >> ec_dec.EC_CODE_SHIFT));
            end = (end << ec_dec.EC_SYM_BITS) & (ec_dec.EC_CODE_TOP - 1);
            l -= ec_dec.EC_SYM_BITS;
        }
        /*If we have a buffered byte flush it into the output buffer.*/
        if ((this.rem >= 0) || (this.ext > 0))
        {
            this.ec_enc_carry_out(0);
        }
        /*If we have buffered extra bits, flush them as well.*/
        window = this.end_window;
        used = this.nend_bits;
        while (used >= ec_dec.EC_SYM_BITS)
        {
            this.error |= this.ec_write_byte_at_end(window & ec_dec.EC_SYM_MAX);
            window >>= ec_dec.EC_SYM_BITS;
            used -= ec_dec.EC_SYM_BITS;
        }
        /*Clear any excess space and add any remaining extra bits to the last byte.*/
        if (this.error == 0)
        {
            if (!this.buf.IsEmpty)
            {
                this.buf.Slice((int)this.offs, (int)(this.storage - this.offs - this.end_offs)).Clear();
            }
            if (used > 0)
            {
                /*If there's no range coder data at all, give up.*/
                if (this.end_offs >= this.storage)
                {
                    this.error = -1;
                }
                else
                {
                    l = -l;
                    /*If we've busted, don't add too many extra bits to the last byte; it
                       would corrupt the range coder data, and that's more important.*/
                    if ((this.offs + this.end_offs >= this.storage) && (l < used))
                    {
                        window &= (1U << l) - 1;
                        this.error = -1;
                    }
                    this.buf[(int)(this.storage - this.end_offs - 1)] |= (byte)window;
                }
            }
        }
    }

    /// <summary>C's ec_get_buffer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal readonly Span<byte> ec_get_buffer() => this.buf;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal readonly uint ec_range_bytes() => this.offs;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal readonly int ec_get_error() => this.error;

    /*Returns the number of bits "used" by the encoded symbols so far.*/
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal readonly int ec_tell() => this.nbits_total - ec_dec.EC_ILOG(this.rng);

    private static readonly uint[] correction = [35733, 38967, 42495, 46340, 50535, 55109, 60097, 65535];

    /*Returns the number of bits "used" by the encoded symbols so far, scaled by 2**BITRES:
      the linear function and lookup libopus compiles.*/
    internal readonly uint ec_tell_frac()
    {
        uint nbits;
        uint r;
        int l;
        uint b;
        nbits = (uint)this.nbits_total << ec_dec.BITRES;
        l = ec_dec.EC_ILOG(this.rng);
        r = this.rng >> (l - 16);
        b = (r >> 12) - 8;
        b += r > correction[b] ? 1U : 0U;
        l = (l << 3) + (int)b;
        return nbits - (uint)l;
    }
}
