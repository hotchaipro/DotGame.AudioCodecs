// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Buffers.Binary;
using System.Numerics;

namespace DotGame.AudioCodecs.Flac;

/// <summary>
/// MD5 (RFC 1321), incremental: the hash STREAMINFO keeps of a FLAC file's samples. Managed, so it gives
/// the same result on every platform, the browser included, where .NET has no MD5.
/// </summary>
internal sealed class Md5
{
    private readonly byte[] _block = new byte[64];
    private uint _a = 0x67452301;
    private uint _b = 0xefcdab89;
    private uint _c = 0x98badcfe;
    private uint _d = 0x10325476;
    private int _filled;
    private long _length;

    public void Append(ReadOnlySpan<byte> data)
    {
        this._length += data.Length;
        if (this._filled > 0)
        {
            int take = Math.Min(64 - this._filled, data.Length);
            data[..take].CopyTo(this._block.AsSpan(this._filled));
            this._filled += take;
            data = data[take..];
            if (this._filled < 64)
            {
                return;
            }
            this.Transform(this._block);
            this._filled = 0;
        }
        while (data.Length >= 64)
        {
            this.Transform(data[..64]);
            data = data[64..];
        }
        data.CopyTo(this._block);
        this._filled = data.Length;
    }

    /// <summary>The hash of everything appended; the object can't be used after.</summary>
    public void Finish(Span<byte> hash)
    {
        long bits = this._length * 8;
        Span<byte> pad = stackalloc byte[72];
        pad.Clear();
        pad[0] = 0x80;
        int padLength = ((this._filled < 56) ? 56 : 120) - this._filled;
        BinaryPrimitives.WriteInt64LittleEndian(pad[padLength..], bits);
        this.Append(pad[..(padLength + 8)]);
        BinaryPrimitives.WriteUInt32LittleEndian(hash, this._a);
        BinaryPrimitives.WriteUInt32LittleEndian(hash[4..], this._b);
        BinaryPrimitives.WriteUInt32LittleEndian(hash[8..], this._c);
        BinaryPrimitives.WriteUInt32LittleEndian(hash[12..], this._d);
    }

    // The 64 steps written out (RFC 1321's reference order), so the words and constants are
    // registers and immediates rather than table lookups; each step's word, constant and the
    // register it replaces are added before the round function, and G is (b & d) + (c & ~d), so
    // only the last operations wait on the step before
    private void Transform(ReadOnlySpan<byte> block)
    {
        block = block[..64];
        uint m0 = BinaryPrimitives.ReadUInt32LittleEndian(block[0..]);
        uint m1 = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);
        uint m2 = BinaryPrimitives.ReadUInt32LittleEndian(block[8..]);
        uint m3 = BinaryPrimitives.ReadUInt32LittleEndian(block[12..]);
        uint m4 = BinaryPrimitives.ReadUInt32LittleEndian(block[16..]);
        uint m5 = BinaryPrimitives.ReadUInt32LittleEndian(block[20..]);
        uint m6 = BinaryPrimitives.ReadUInt32LittleEndian(block[24..]);
        uint m7 = BinaryPrimitives.ReadUInt32LittleEndian(block[28..]);
        uint m8 = BinaryPrimitives.ReadUInt32LittleEndian(block[32..]);
        uint m9 = BinaryPrimitives.ReadUInt32LittleEndian(block[36..]);
        uint m10 = BinaryPrimitives.ReadUInt32LittleEndian(block[40..]);
        uint m11 = BinaryPrimitives.ReadUInt32LittleEndian(block[44..]);
        uint m12 = BinaryPrimitives.ReadUInt32LittleEndian(block[48..]);
        uint m13 = BinaryPrimitives.ReadUInt32LittleEndian(block[52..]);
        uint m14 = BinaryPrimitives.ReadUInt32LittleEndian(block[56..]);
        uint m15 = BinaryPrimitives.ReadUInt32LittleEndian(block[60..]);
        uint a = this._a;
        uint b = this._b;
        uint c = this._c;
        uint d = this._d;
        a = b + BitOperations.RotateLeft(a + (0xd76aa478 + m0) + (d ^ (b & (c ^ d))), 7);
        d = a + BitOperations.RotateLeft(d + (0xe8c7b756 + m1) + (c ^ (a & (b ^ c))), 12);
        c = d + BitOperations.RotateLeft(c + (0x242070db + m2) + (b ^ (d & (a ^ b))), 17);
        b = c + BitOperations.RotateLeft(b + (0xc1bdceee + m3) + (a ^ (c & (d ^ a))), 22);
        a = b + BitOperations.RotateLeft(a + (0xf57c0faf + m4) + (d ^ (b & (c ^ d))), 7);
        d = a + BitOperations.RotateLeft(d + (0x4787c62a + m5) + (c ^ (a & (b ^ c))), 12);
        c = d + BitOperations.RotateLeft(c + (0xa8304613 + m6) + (b ^ (d & (a ^ b))), 17);
        b = c + BitOperations.RotateLeft(b + (0xfd469501 + m7) + (a ^ (c & (d ^ a))), 22);
        a = b + BitOperations.RotateLeft(a + (0x698098d8 + m8) + (d ^ (b & (c ^ d))), 7);
        d = a + BitOperations.RotateLeft(d + (0x8b44f7af + m9) + (c ^ (a & (b ^ c))), 12);
        c = d + BitOperations.RotateLeft(c + (0xffff5bb1 + m10) + (b ^ (d & (a ^ b))), 17);
        b = c + BitOperations.RotateLeft(b + (0x895cd7be + m11) + (a ^ (c & (d ^ a))), 22);
        a = b + BitOperations.RotateLeft(a + (0x6b901122 + m12) + (d ^ (b & (c ^ d))), 7);
        d = a + BitOperations.RotateLeft(d + (0xfd987193 + m13) + (c ^ (a & (b ^ c))), 12);
        c = d + BitOperations.RotateLeft(c + (0xa679438e + m14) + (b ^ (d & (a ^ b))), 17);
        b = c + BitOperations.RotateLeft(b + (0x49b40821 + m15) + (a ^ (c & (d ^ a))), 22);
        a = b + BitOperations.RotateLeft(a + (0xf61e2562 + m1) + (c & ~d) + (b & d), 5);
        d = a + BitOperations.RotateLeft(d + (0xc040b340 + m6) + (b & ~c) + (a & c), 9);
        c = d + BitOperations.RotateLeft(c + (0x265e5a51 + m11) + (a & ~b) + (d & b), 14);
        b = c + BitOperations.RotateLeft(b + (0xe9b6c7aa + m0) + (d & ~a) + (c & a), 20);
        a = b + BitOperations.RotateLeft(a + (0xd62f105d + m5) + (c & ~d) + (b & d), 5);
        d = a + BitOperations.RotateLeft(d + (0x02441453 + m10) + (b & ~c) + (a & c), 9);
        c = d + BitOperations.RotateLeft(c + (0xd8a1e681 + m15) + (a & ~b) + (d & b), 14);
        b = c + BitOperations.RotateLeft(b + (0xe7d3fbc8 + m4) + (d & ~a) + (c & a), 20);
        a = b + BitOperations.RotateLeft(a + (0x21e1cde6 + m9) + (c & ~d) + (b & d), 5);
        d = a + BitOperations.RotateLeft(d + (0xc33707d6 + m14) + (b & ~c) + (a & c), 9);
        c = d + BitOperations.RotateLeft(c + (0xf4d50d87 + m3) + (a & ~b) + (d & b), 14);
        b = c + BitOperations.RotateLeft(b + (0x455a14ed + m8) + (d & ~a) + (c & a), 20);
        a = b + BitOperations.RotateLeft(a + (0xa9e3e905 + m13) + (c & ~d) + (b & d), 5);
        d = a + BitOperations.RotateLeft(d + (0xfcefa3f8 + m2) + (b & ~c) + (a & c), 9);
        c = d + BitOperations.RotateLeft(c + (0x676f02d9 + m7) + (a & ~b) + (d & b), 14);
        b = c + BitOperations.RotateLeft(b + (0x8d2a4c8a + m12) + (d & ~a) + (c & a), 20);
        a = b + BitOperations.RotateLeft(a + (0xfffa3942 + m5) + (b ^ (c ^ d)), 4);
        d = a + BitOperations.RotateLeft(d + (0x8771f681 + m8) + (a ^ (b ^ c)), 11);
        c = d + BitOperations.RotateLeft(c + (0x6d9d6122 + m11) + (d ^ (a ^ b)), 16);
        b = c + BitOperations.RotateLeft(b + (0xfde5380c + m14) + (c ^ (d ^ a)), 23);
        a = b + BitOperations.RotateLeft(a + (0xa4beea44 + m1) + (b ^ (c ^ d)), 4);
        d = a + BitOperations.RotateLeft(d + (0x4bdecfa9 + m4) + (a ^ (b ^ c)), 11);
        c = d + BitOperations.RotateLeft(c + (0xf6bb4b60 + m7) + (d ^ (a ^ b)), 16);
        b = c + BitOperations.RotateLeft(b + (0xbebfbc70 + m10) + (c ^ (d ^ a)), 23);
        a = b + BitOperations.RotateLeft(a + (0x289b7ec6 + m13) + (b ^ (c ^ d)), 4);
        d = a + BitOperations.RotateLeft(d + (0xeaa127fa + m0) + (a ^ (b ^ c)), 11);
        c = d + BitOperations.RotateLeft(c + (0xd4ef3085 + m3) + (d ^ (a ^ b)), 16);
        b = c + BitOperations.RotateLeft(b + (0x04881d05 + m6) + (c ^ (d ^ a)), 23);
        a = b + BitOperations.RotateLeft(a + (0xd9d4d039 + m9) + (b ^ (c ^ d)), 4);
        d = a + BitOperations.RotateLeft(d + (0xe6db99e5 + m12) + (a ^ (b ^ c)), 11);
        c = d + BitOperations.RotateLeft(c + (0x1fa27cf8 + m15) + (d ^ (a ^ b)), 16);
        b = c + BitOperations.RotateLeft(b + (0xc4ac5665 + m2) + (c ^ (d ^ a)), 23);
        a = b + BitOperations.RotateLeft(a + (0xf4292244 + m0) + (c ^ (b | ~d)), 6);
        d = a + BitOperations.RotateLeft(d + (0x432aff97 + m7) + (b ^ (a | ~c)), 10);
        c = d + BitOperations.RotateLeft(c + (0xab9423a7 + m14) + (a ^ (d | ~b)), 15);
        b = c + BitOperations.RotateLeft(b + (0xfc93a039 + m5) + (d ^ (c | ~a)), 21);
        a = b + BitOperations.RotateLeft(a + (0x655b59c3 + m12) + (c ^ (b | ~d)), 6);
        d = a + BitOperations.RotateLeft(d + (0x8f0ccc92 + m3) + (b ^ (a | ~c)), 10);
        c = d + BitOperations.RotateLeft(c + (0xffeff47d + m10) + (a ^ (d | ~b)), 15);
        b = c + BitOperations.RotateLeft(b + (0x85845dd1 + m1) + (d ^ (c | ~a)), 21);
        a = b + BitOperations.RotateLeft(a + (0x6fa87e4f + m8) + (c ^ (b | ~d)), 6);
        d = a + BitOperations.RotateLeft(d + (0xfe2ce6e0 + m15) + (b ^ (a | ~c)), 10);
        c = d + BitOperations.RotateLeft(c + (0xa3014314 + m6) + (a ^ (d | ~b)), 15);
        b = c + BitOperations.RotateLeft(b + (0x4e0811a1 + m13) + (d ^ (c | ~a)), 21);
        a = b + BitOperations.RotateLeft(a + (0xf7537e82 + m4) + (c ^ (b | ~d)), 6);
        d = a + BitOperations.RotateLeft(d + (0xbd3af235 + m11) + (b ^ (a | ~c)), 10);
        c = d + BitOperations.RotateLeft(c + (0x2ad7d2bb + m2) + (a ^ (d | ~b)), 15);
        b = c + BitOperations.RotateLeft(b + (0xeb86d391 + m9) + (d ^ (c | ~a)), 21);
        this._a += a;
        this._b += b;
        this._c += c;
        this._d += d;
    }
}
