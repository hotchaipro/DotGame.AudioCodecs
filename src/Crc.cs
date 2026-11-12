// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using ArmAes = System.Runtime.Intrinsics.Arm.Aes;

namespace DotGame.AudioCodecs;

/// <summary>
/// An unreflected CRC (the first bit is the highest power) in a 32-bit register, from 0 with no final
/// XOR: Ogg's CRC-32 (polynomial 0x04C11DB7) and FLAC's CRC-16 (0x8005), the second with its polynomial
/// moved to the register's top 16 bits, which leaves the CRC there.
/// </summary>
/// <remarks>
/// <para>
/// With carry-less multiplication (PCLMULQDQ on x64, PMULL on Arm64), 16 bytes at a time in four lanes,
/// as in Intel's "Fast CRC Computation for Generic Polynomials Using PCLMULQDQ Instruction" (Gopal et
/// al., 2009): a 128-bit block A followed by n more bits is A·xⁿ, and A·xⁿ is congruent, modulo the
/// polynomial, to its high 64 bits times (x^(n+64) mod P) plus its low 64 bits times (xⁿ mod P), a
/// value of under 128 bits that takes the next block's place. The lanes' sum is a 128-bit value with the
/// message's CRC, which the tables then take, with the bytes left over.
/// </para>
/// <para>
/// Elsewhere (and with <c>-p:PortableVectors=true</c>), eight bytes a step from eight tables (slicing by
/// 8): table k is the CRC of a byte followed by k zero bytes.
/// </para>
/// </remarks>
internal sealed class Crc
{
    /// <summary>Ogg's page CRC (RFC 3533, 6).</summary>
    public static readonly Crc Ogg = new(0x04C11DB7, 0xE8A45605, 0xC5B9CD4C, 0xE6228B11, 0x8833794C);

    /// <summary>FLAC's frame CRC-16 (RFC 9639, 9.3), in the register's top 16 bits.</summary>
    public static readonly Crc Flac16 = new(0x80050000, 0xFF830000, 0xF9130000, 0x807D0000, 0xF9E30000);

#if OPUS_PORTABLE_VECTORS
    private const bool Portable = true;
#else
    private const bool Portable = false;
#endif

    private readonly uint[] _tables = new uint[8 * 256];
    private readonly ulong _x128;    // x^128 mod P: folds a block's low half over 128 bits
    private readonly ulong _x192;    // x^192 mod P: its high half
    private readonly ulong _x512;    // the same over 512 bits, across the four lanes
    private readonly ulong _x576;

    private Crc(uint polynomial, uint x128, uint x192, uint x512, uint x576)
    {
        for (int b = 0; b < 256; b++)
        {
            uint r = (uint)b << 24;
            for (int k = 0; k < 8; k++)
            {
                r = ((r & 0x80000000) != 0) ? (r << 1) ^ polynomial : r << 1;
            }
            this._tables[b] = r;
        }
        for (int t = 1; t < 8; t++)
        {
            for (int b = 0; b < 256; b++)
            {
                uint previous = this._tables[((t - 1) * 256) + b];
                this._tables[(t * 256) + b] = (previous << 8) ^ this._tables[previous >> 24];
            }
        }
        this._x128 = x128;
        this._x192 = x192;
        this._x512 = x512;
        this._x576 = x576;
    }

    /// <summary>Whether this machine multiplies without carries (so long inputs are 16 bytes a step).</summary>
    internal static bool CarryLess => !Portable && Vector128.IsHardwareAccelerated
        && ((Pclmulqdq.IsSupported && Ssse3.IsSupported) || (ArmAes.IsSupported && AdvSimd.Arm64.IsSupported));

    /// <summary>The CRC, in the register (FLAC's CRC-16 in its top 16 bits), after <paramref name="data"/>.</summary>
    public uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        if (CarryLess && (data.Length >= 64))
        {
            crc = this.Fold(crc, data, out int done);
            data = data[done..];
        }
        return this.Tables(crc, data);
    }

    /// <summary>As <see cref="Update"/>, with the tables only (for the tests).</summary>
    internal uint UpdateWithTables(uint crc, ReadOnlySpan<byte> data) => this.Tables(crc, data);

    private uint Tables(uint crc, ReadOnlySpan<byte> data)
    {
        ref uint t = ref MemoryMarshal.GetArrayDataReference(this._tables);
        ref byte d = ref MemoryMarshal.GetReference(data);
        int i = 0;
        for (; i + 8 <= data.Length; i += 8)
        {
            ulong word = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref d, i)));
            crc = Step(ref t, crc, word);
        }
        for (; i < data.Length; i++)
        {
            crc = (crc << 8) ^ Unsafe.Add(ref t, (int)((crc >> 24) ^ Unsafe.Add(ref d, i)));
        }
        return crc;
    }

    // Eight bytes, the first the highest of word: the first four take in the register
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Step(ref uint t, uint crc, ulong word)
    {
        uint high = crc ^ (uint)(word >> 32);
        uint low = (uint)word;
        return Unsafe.Add(ref t, (7 * 256) + (int)(high >> 24))
            ^ Unsafe.Add(ref t, (6 * 256) + (int)((high >> 16) & 0xFF))
            ^ Unsafe.Add(ref t, (5 * 256) + (int)((high >> 8) & 0xFF))
            ^ Unsafe.Add(ref t, (4 * 256) + (int)(high & 0xFF))
            ^ Unsafe.Add(ref t, (3 * 256) + (int)(low >> 24))
            ^ Unsafe.Add(ref t, (2 * 256) + (int)((low >> 16) & 0xFF))
            ^ Unsafe.Add(ref t, 256 + (int)((low >> 8) & 0xFF))
            ^ Unsafe.Add(ref t, (int)(low & 0xFF));
    }

    // The whole 16-byte blocks of data (at least four), folded to one: the CRC after them, and how many
    // bytes that took
    private uint Fold(uint crc, ReadOnlySpan<byte> data, out int done)
    {
        ref byte d = ref MemoryMarshal.GetReference(data);
        Vector128<ulong> k128 = Vector128.Create(this._x128, this._x192);
        Vector128<ulong> k512 = Vector128.Create(this._x512, this._x576);

        // The register is the first bits' to take in: it goes over the first block's top 32
        Vector128<ulong> a0 = Load(ref d) ^ Vector128.Create(0UL, (ulong)crc << 32);
        Vector128<ulong> a1 = Load(ref Unsafe.Add(ref d, 16));
        Vector128<ulong> a2 = Load(ref Unsafe.Add(ref d, 32));
        Vector128<ulong> a3 = Load(ref Unsafe.Add(ref d, 48));
        int i = 64;
        for (; i + 64 <= data.Length; i += 64)
        {
            a0 = Multiply(a0, k512) ^ Load(ref Unsafe.Add(ref d, i));
            a1 = Multiply(a1, k512) ^ Load(ref Unsafe.Add(ref d, i + 16));
            a2 = Multiply(a2, k512) ^ Load(ref Unsafe.Add(ref d, i + 32));
            a3 = Multiply(a3, k512) ^ Load(ref Unsafe.Add(ref d, i + 48));
        }
        Vector128<ulong> a = Multiply(a0, k128) ^ a1;
        a = Multiply(a, k128) ^ a2;
        a = Multiply(a, k128) ^ a3;
        for (; i + 16 <= data.Length; i += 16)
        {
            a = Multiply(a, k128) ^ Load(ref Unsafe.Add(ref d, i));
        }
        done = i;

        // The CRC of the 128-bit value, from 0, is the message's so far
        ref uint t = ref MemoryMarshal.GetArrayDataReference(this._tables);
        crc = Step(ref t, 0, a.GetElement(1));
        return Step(ref t, crc, a.GetElement(0));
    }

    // 16 bytes as a 128-bit polynomial, the first byte's top bit the highest: element 1 is the high half
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ulong> Load(ref byte source) =>
        Vector128.Shuffle(Vector128.LoadUnsafe(ref source), Vector128.Create((byte)15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0)).AsUInt64();

    // The high half times k's element 1 plus the low half times its element 0, without carries
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ulong> Multiply(Vector128<ulong> a, Vector128<ulong> k)
    {
        if (Pclmulqdq.IsSupported)
        {
            return Pclmulqdq.CarrylessMultiply(a, k, 0x00) ^ Pclmulqdq.CarrylessMultiply(a, k, 0x11);
        }
        if (ArmAes.IsSupported)
        {
            return ArmAes.PolynomialMultiplyWideningLower(a.GetLower(), k.GetLower()) ^ ArmAes.PolynomialMultiplyWideningUpper(a, k);
        }
        throw new PlatformNotSupportedException();
    }
}
