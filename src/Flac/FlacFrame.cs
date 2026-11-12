// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.IO;

namespace DotGame.AudioCodecs.Flac;

/// <summary>
/// One FLAC frame (RFC 9639, 9): its header, with the CRC-8 checked, and its subframes, decoded to
/// the channels' samples, with the frame's CRC-16 checked.
/// </summary>
/// <remarks>
/// The whole of the frame format: fixed and variable block sizes, every channel assignment, constant,
/// verbatim, fixed and LPC subframes, wasted bits, and both Rice coding methods with escapes. About as
/// fast as libFLAC 1.5.0: bits come from a 64-bit cache, Rice codes are read whole, the common LPC
/// orders are unrolled with the latest sample's product last, and 32-bit sums are used where libFLAC's
/// bound shows they can't overflow.
/// </remarks>
internal static partial class FlacFrame
{
    /// <summary>A frame header's fields.</summary>
    internal struct Header
    {
        public int BlockSize;
        public int SampleRate;        // 0: STREAMINFO's
        public int Assignment;        // 0 to 7: independent channels; 8, 9, 10: left/side, side/right, mid/side
        public int Bits;              // 0: STREAMINFO's
        public long Number;           // the frame number, or with a variable block size the first sample's
        public bool VariableBlockSize;
        public int Length;            // the header's bytes, CRC-8 included

        public readonly int Channels => this.Assignment <= 7 ? this.Assignment + 1 : 2;
    }

    /// <summary>
    /// Parses the frame header at the start of <paramref name="data"/>, checking its sync code, reserved
    /// values and CRC-8. False if it isn't a valid header (or <paramref name="data"/> is too short).
    /// </summary>
    public static bool TryReadHeader(ReadOnlySpan<byte> data, out Header header)
    {
        header = default;
        if ((data.Length < 6) || (data[0] != 0xFF) || ((data[1] & 0xFE) != 0xF8))
        {
            return false;
        }
        header.VariableBlockSize = (data[1] & 1) != 0;
        int blockSizeCode = data[2] >> 4;
        int rateCode = data[2] & 15;
        header.Assignment = data[3] >> 4;
        int sizeCode = (data[3] >> 1) & 7;
        if ((blockSizeCode == 0) || (rateCode == 15) || (header.Assignment > 10) || (sizeCode == 3) || ((data[3] & 1) != 0))
        {
            return false;
        }

        // The frame or sample number, in UTF-8's form of up to 7 bytes
        int at = 4;
        int first = data[at++];
        int more = 0;
        while ((more < 7) && ((first & (0x80 >> more)) != 0))
        {
            more++;
        }
        if ((more == 1) || (more > 7) || (!header.VariableBlockSize && (more > 6)))
        {
            return false;
        }
        long number = more == 0 ? first : first & (0x7F >> more);
        for (int i = 1; i < more; i++)
        {
            if ((at >= data.Length) || ((data[at] & 0xC0) != 0x80))
            {
                return false;
            }
            number = (number << 6) | (uint)(data[at++] & 0x3F);
        }
        header.Number = number;

        int extra = blockSizeCode switch { 6 => 1, 7 => 2, _ => 0 } + rateCode switch { 12 => 1, 13 or 14 => 2, _ => 0 };
        if (at + extra + 1 > data.Length)
        {
            return false;
        }
        header.BlockSize = blockSizeCode switch
        {
            1 => 192,
            >= 2 and <= 5 => 576 << (blockSizeCode - 2),
            6 => data[at++] + 1,
            7 => ((data[at++] << 8) | data[at++]) + 1,
            _ => 256 << (blockSizeCode - 8),
        };
        header.SampleRate = rateCode switch
        {
            0 => 0,
            1 => 88200,
            2 => 176400,
            3 => 192000,
            4 => 8000,
            5 => 16000,
            6 => 22050,
            7 => 24000,
            8 => 32000,
            9 => 44100,
            10 => 48000,
            11 => 96000,
            12 => data[at++] * 1000,
            13 => (data[at++] << 8) | data[at++],
            _ => ((data[at++] << 8) | data[at++]) * 10,
        };
        header.Bits = sizeCode switch
        {
            0 => 0,
            1 => 8,
            2 => 12,
            4 => 16,
            5 => 20,
            6 => 24,
            _ => 32,
        };
        if (Crc8(data[..at]) != data[at])
        {
            return false;
        }
        header.Length = at + 1;
        return true;
    }

    /// <summary>
    /// Decodes the frame at the start of <paramref name="data"/>, whose header is
    /// <paramref name="header"/>, into the first channels of <paramref name="blocks"/>, checking its CRC-16
    /// (with <paramref name="checkCrc"/>).
    /// Returns the frame's length in bytes, or 0 if <paramref name="data"/> ends before it does (and
    /// <paramref name="complete"/> says more may follow).
    /// </summary>
    /// <exception cref="InvalidDataException">The frame is damaged.</exception>
    public static int Decode(ReadOnlySpan<byte> data, in Header header, int bits, int[][] blocks, bool complete, bool checkCrc = true)
    {
        var reader = new BitReader(data);
        reader.Skip(header.Length * 8L);
        int channels = header.Channels;
        int blockSize = header.BlockSize;
        try
        {
            for (int channel = 0; channel < channels; channel++)
            {
                // The side channel has a bit more
                int a = header.Assignment;
                bool side = ((a == 8) && (channel == 1)) || ((a == 9) && (channel == 0)) || ((a == 10) && (channel == 1));
                ReadSubframe(ref reader, blocks[channel].AsSpan(0, blockSize), bits + (side ? 1 : 0));
            }
            reader.AlignToByte();
            reader.Skip(16);
        }
        catch (InvalidDataException) when (!complete && reader.AtEnd)
        {
            // Ran out of the bytes given: the caller reads more and decodes again
            return 0;
        }
        int length = (int)(reader.BytePosition);
        if (checkCrc && Crc16(data[..(length - 2)]) != ((data[length - 2] << 8) | data[length - 1]))
        {
            throw new InvalidDataException("A FLAC frame is damaged: its CRC-16 is wrong.");
        }
        if (header.Assignment >= 8)
        {
            Decorrelate(header.Assignment, blocks, blockSize);
        }
        return length;
    }

    /// <summary>The frame header's CRC-8: polynomial 0x07, from 0.</summary>
    public static byte Crc8(ReadOnlySpan<byte> data)
    {
        uint crc = 0;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
            {
                crc = (crc & 0x80) != 0 ? ((crc << 1) ^ 0x07) & 0xFF : (crc << 1) & 0xFF;
            }
        }
        return (byte)crc;
    }

    /// <summary>The frame's CRC-16: polynomial 0x8005, from 0 (<see cref="Crc.Flac16"/>).</summary>
    public static ushort Crc16(ReadOnlySpan<byte> data) => (ushort)(Crc.Flac16.Update(0, data) >> 16);

    private static void ReadSubframe(ref BitReader reader, Span<int> samples, int width)
    {
        if (reader.Read(1) != 0)
        {
            throw new InvalidDataException("FLAC subframe has a bad padding bit.");
        }
        int type = (int)reader.Read(6);
        int wasted = 0;
        if (reader.Read(1) == 1)
        {
            wasted = reader.ReadUnary() + 1;
            width -= wasted;
        }
        if ((width < 1) || (width > 32))
        {
            throw new InvalidDataException($"FLAC subframe has samples of {width} bits.");
        }

        if (type == 0)
        {
            samples.Fill(reader.ReadSigned(width));
        }
        else if (type == 1)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = reader.ReadSigned(width);
            }
        }
        else if ((type >= 8) && (type <= 12))
        {
            int order = type - 8;
            ReadWarmUp(ref reader, samples, order, width);
            ReadResidual(ref reader, samples, order);
            PredictFixed(samples, order);
        }
        else if (type >= 32)
        {
            int order = (type & 31) + 1;
            ReadWarmUp(ref reader, samples, order, width);
            int precision = (int)reader.Read(4) + 1;
            if (precision == 16)
            {
                throw new InvalidDataException("FLAC subframe has an invalid coefficient precision.");
            }
            int shift = reader.ReadSigned(5);
            if (shift < 0)
            {
                throw new InvalidDataException("FLAC subframe has a negative prediction shift.");
            }
            Span<int> coefficients = stackalloc int[32];
            for (int i = 0; i < order; i++)
            {
                coefficients[i] = reader.ReadSigned(precision);
            }
            ReadResidual(ref reader, samples, order);
            PredictLinear(samples, coefficients[..order], shift, width + precision + Log2(order) <= 32);
        }
        else
        {
            throw new InvalidDataException($"FLAC subframe has a reserved type {type}.");
        }

        if (wasted > 0)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] <<= wasted;
            }
        }
    }

    private static void ReadWarmUp(ref BitReader reader, Span<int> samples, int order, int width)
    {
        if (order > samples.Length)
        {
            throw new InvalidDataException("FLAC subframe's predictor is longer than its block.");
        }
        for (int i = 0; i < order; i++)
        {
            samples[i] = reader.ReadSigned(width);
        }
    }

    // The residual follows the warm-up samples, in 2^order partitions with a Rice parameter each
    private static void ReadResidual(ref BitReader reader, Span<int> samples, int predictorOrder)
    {
        int method = (int)reader.Read(2);
        if (method > 1)
        {
            throw new InvalidDataException("FLAC residual has a reserved coding method.");
        }
        int parameterBits = method == 0 ? 4 : 5;
        int escape = method == 0 ? 15 : 31;
        int partitionOrder = (int)reader.Read(4);
        int partitions = 1 << partitionOrder;
        int perPartition = samples.Length >> partitionOrder;
        if ((perPartition << partitionOrder) != samples.Length || perPartition < predictorOrder)
        {
            throw new InvalidDataException("FLAC residual's partitions don't fit its block.");
        }

        int at = predictorOrder;
        for (int partition = 0; partition < partitions; partition++)
        {
            int count = partition == 0 ? perPartition - predictorOrder : perPartition;
            int parameter = (int)reader.Read(parameterBits);
            if (parameter == escape)
            {
                int bits = (int)reader.Read(5);
                for (int i = 0; i < count; i++)
                {
                    samples[at++] = bits == 0 ? 0 : reader.ReadSigned(bits);
                }
            }
            else
            {
                reader.ReadRiceRun(samples.Slice(at, count), parameter);
                at += count;
            }
        }
    }

    // The residual is in place after the warm-up; each sample adds its prediction, the samples before
    // it kept in locals
    private static void PredictFixed(Span<int> s, int order)
    {
        if (s.Length <= order)
        {
            return;
        }
        ref int r = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(s);
        int n = s.Length;
        if (order == 1)
        {
            int x1 = s[0];
            for (int i = 1; i < n; i++)
            {
                int x = System.Runtime.CompilerServices.Unsafe.Add(ref r, i) + x1;
                System.Runtime.CompilerServices.Unsafe.Add(ref r, i) = x;
                x1 = x;
            }
        }
        else if (order == 2)
        {
            int x1 = s[1];
            int x2 = s[0];
            for (int i = 2; i < n; i++)
            {
                int x = System.Runtime.CompilerServices.Unsafe.Add(ref r, i) + ((2 * x1) - x2);
                System.Runtime.CompilerServices.Unsafe.Add(ref r, i) = x;
                x2 = x1;
                x1 = x;
            }
        }
        else if (order == 3)
        {
            int x1 = s[2];
            int x2 = s[1];
            int x3 = s[0];
            for (int i = 3; i < n; i++)
            {
                int x = System.Runtime.CompilerServices.Unsafe.Add(ref r, i) + ((3 * (x1 - x2)) + x3);
                System.Runtime.CompilerServices.Unsafe.Add(ref r, i) = x;
                x3 = x2;
                x2 = x1;
                x1 = x;
            }
        }
        else if (order == 4)
        {
            int x1 = s[3];
            int x2 = s[2];
            int x3 = s[1];
            int x4 = s[0];
            for (int i = 4; i < n; i++)
            {
                int x = System.Runtime.CompilerServices.Unsafe.Add(ref r, i) + ((4 * (x1 + x3)) - (6 * x2) - x4);
                System.Runtime.CompilerServices.Unsafe.Add(ref r, i) = x;
                x4 = x3;
                x3 = x2;
                x2 = x1;
                x1 = x;
            }
        }
    }

    // 8 taps in 32 bits, unrolled (the narrow bound holds for every partial sum too), the last
    // samples in locals; the latest sample's product is added last, so it alone waits on the one before
    private static void PredictNarrow8(Span<int> s, ReadOnlySpan<int> c, int shift, int start)
    {
        int c0 = c[0], c1 = c[1], c2 = c[2], c3 = c[3], c4 = c[4], c5 = c[5], c6 = c[6], c7 = c[7];
        if ((start < 8) || (start >= s.Length))
        {
            return;
        }
        int x1 = s[start - 1], x2 = s[start - 2], x3 = s[start - 3], x4 = s[start - 4], x5 = s[start - 5], x6 = s[start - 6], x7 = s[start - 7], x8 = s[start - 8];
        ref int r = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(s);
        for (int i = start; i < s.Length; i++)
        {
            int partial = (c7 * x8) + (c6 * x7) + (c5 * x6) + (c4 * x5) + (c3 * x4) + (c2 * x3) + (c1 * x2);
            int sample = System.Runtime.CompilerServices.Unsafe.Add(ref r, i) + ((partial + (c0 * x1)) >> shift);
            System.Runtime.CompilerServices.Unsafe.Add(ref r, i) = sample;
            x8 = x7;
            x7 = x6;
            x6 = x5;
            x5 = x4;
            x4 = x3;
            x3 = x2;
            x2 = x1;
            x1 = sample;
        }
    }

    // 12 taps in 32 bits, unrolled (the narrow bound holds for every partial sum too), the last
    // samples in locals; the latest sample's product is added last, so it alone waits on the one before
    private static void PredictNarrow12(Span<int> s, ReadOnlySpan<int> c, int shift, int start)
    {
        int c0 = c[0], c1 = c[1], c2 = c[2], c3 = c[3], c4 = c[4], c5 = c[5], c6 = c[6], c7 = c[7], c8 = c[8], c9 = c[9], c10 = c[10], c11 = c[11];
        if ((start < 12) || (start >= s.Length))
        {
            return;
        }
        int x1 = s[start - 1], x2 = s[start - 2], x3 = s[start - 3], x4 = s[start - 4], x5 = s[start - 5], x6 = s[start - 6], x7 = s[start - 7], x8 = s[start - 8], x9 = s[start - 9], x10 = s[start - 10], x11 = s[start - 11], x12 = s[start - 12];
        ref int r = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(s);
        for (int i = start; i < s.Length; i++)
        {
            int partial = (c11 * x12) + (c10 * x11) + (c9 * x10) + (c8 * x9) + (c7 * x8) + (c6 * x7) + (c5 * x6) + (c4 * x5) + (c3 * x4) + (c2 * x3) + (c1 * x2);
            int sample = System.Runtime.CompilerServices.Unsafe.Add(ref r, i) + ((partial + (c0 * x1)) >> shift);
            System.Runtime.CompilerServices.Unsafe.Add(ref r, i) = sample;
            x12 = x11;
            x11 = x10;
            x10 = x9;
            x9 = x8;
            x8 = x7;
            x7 = x6;
            x6 = x5;
            x5 = x4;
            x4 = x3;
            x3 = x2;
            x2 = x1;
            x1 = sample;
        }
    }

    // Eight taps in 64 bits, unrolled, as libFLAC's restore_signal_wide is for its usual orders; the
    // last eight samples are kept in locals, x1 the latest, so each sample is one load and one store
    private static void PredictWide8(Span<int> s, ReadOnlySpan<int> c, int shift, int start)
    {
        long c0 = c[0], c1 = c[1], c2 = c[2], c3 = c[3], c4 = c[4], c5 = c[5], c6 = c[6], c7 = c[7];
        if (start < 8)
        {
            return;
        }
        long x1 = s[start - 1], x2 = s[start - 2], x3 = s[start - 3], x4 = s[start - 4];
        long x5 = s[start - 5], x6 = s[start - 6], x7 = s[start - 7], x8 = s[start - 8];
        Span<int> rest = s[start..];
        for (int i = 0; i < rest.Length; i++)
        {
            // NOTE: The latest sample last, so only its product waits on the sample before
            long sum = (c7 * x8) + (c6 * x7) + (c5 * x6) + (c4 * x5) + (c3 * x4) + (c2 * x3) + (c1 * x2) + (c0 * x1);
            int sample = rest[i] + (int)(sum >> shift);
            rest[i] = sample;
            x8 = x7;
            x7 = x6;
            x6 = x5;
            x5 = x4;
            x4 = x3;
            x3 = x2;
            x2 = x1;
            x1 = sample;
        }
    }

    // Twelve taps in 64 bits, unrolled, as PredictWide8
    private static void PredictWide12(Span<int> s, ReadOnlySpan<int> c, int shift, int start)
    {
        long c0 = c[0], c1 = c[1], c2 = c[2], c3 = c[3], c4 = c[4], c5 = c[5];
        long c6 = c[6], c7 = c[7], c8 = c[8], c9 = c[9], c10 = c[10], c11 = c[11];
        if (start < 12)
        {
            return;
        }
        long x1 = s[start - 1], x2 = s[start - 2], x3 = s[start - 3], x4 = s[start - 4];
        long x5 = s[start - 5], x6 = s[start - 6], x7 = s[start - 7], x8 = s[start - 8];
        long x9 = s[start - 9], x10 = s[start - 10], x11 = s[start - 11], x12 = s[start - 12];
        Span<int> rest = s[start..];
        for (int i = 0; i < rest.Length; i++)
        {
            // NOTE: The latest sample last, so only its product waits on the sample before
            long sum = (c11 * x12) + (c10 * x11) + (c9 * x10) + (c8 * x9) + (c7 * x8) + (c6 * x7)
                + (c5 * x6) + (c4 * x5) + (c3 * x4) + (c2 * x3) + (c1 * x2) + (c0 * x1);
            int sample = rest[i] + (int)(sum >> shift);
            rest[i] = sample;
            x12 = x11;
            x11 = x10;
            x10 = x9;
            x9 = x8;
            x8 = x7;
            x7 = x6;
            x6 = x5;
            x5 = x4;
            x4 = x3;
            x3 = x2;
            x2 = x1;
            x1 = sample;
        }
    }

    // libFLAC's FLAC__bitmath_ilog2: the index of the highest set bit
    private static int Log2(int value) => 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)value);

    // The prediction from the order samples before each one. In 32 bits when libFLAC's bound says the
    // sum can't overflow (the sample width, the coefficients' precision and log2 of the order fit 32
    // bits), as libFLAC's decoder does then; in 64 otherwise. Either is exact.
    private static void PredictLinear(Span<int> s, ReadOnlySpan<int> coefficients, int shift, bool narrow)
    {
        int order = coefficients.Length;

        // The coefficients in sample order, so the sum walks both forwards
        Span<int> c = stackalloc int[32];
        for (int j = 0; j < order; j++)
        {
            c[order - 1 - j] = coefficients[j];
        }
        ReadOnlySpan<int> forward = c[..order];
        if (!narrow)
        {
            if (order <= 12)
            {
                // Orders up to 8 or 12 as that many taps, the coefficients beyond the order zero: the
                // samples before the first 8 or 12 with the general loop
                int taps = order <= 8 ? 8 : 12;
                Span<int> padded = stackalloc int[12];
                coefficients.CopyTo(padded);
                int start = Math.Min(taps, s.Length);
                for (int i = order; i < start; i++)
                {
                    long sum = 0;
                    for (int j = 0; j < order; j++)
                    {
                        sum += (long)coefficients[j] * s[i - 1 - j];
                    }
                    s[i] += (int)(sum >> shift);
                }
                if (taps == 8)
                {
                    PredictWide8(s, padded, shift, start);
                }
                else
                {
                    PredictWide12(s, padded, shift, start);
                }
                return;
            }

            // The 64-bit products in vectors (NEON's or SSE4.1's widening multiplies)
            for (int i = order; i < s.Length; i++)
            {
                long sum = Opus.Celt.Simd.InnerProduct64(forward, s.Slice(i - order, order), order);
                s[i] += (int)(sum >> shift);
            }
            return;
        }
        if (order <= 12)
        {
            // As the wide path: 8 or 12 taps, the coefficients past the order zero
            int taps = order <= 8 ? 8 : 12;
            Span<int> padded = stackalloc int[12];
            padded.Clear();
            coefficients.CopyTo(padded);
            int start = Math.Min(taps, s.Length);
            for (int i = order; i < start; i++)
            {
                int sum = 0;
                for (int j = 0; j < order; j++)
                {
                    sum += coefficients[j] * s[i - 1 - j];
                }
                s[i] += sum >> shift;
            }
            if (taps == 8)
            {
                PredictNarrow8(s, padded, shift, start);
            }
            else
            {
                PredictNarrow12(s, padded, shift, start);
            }
            return;
        }
        if (System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated && (order >= 8))
        {
            int vectors = order & ~3;
            for (int i = order; i < s.Length; i++)
            {
                ReadOnlySpan<int> history = s.Slice(i - order, order);
                var acc = System.Runtime.Intrinsics.Vector128<int>.Zero;
                int j = 0;
                for (; j < vectors; j += 4)
                {
                    acc += System.Runtime.Intrinsics.Vector128.Create(forward.Slice(j, 4)) * System.Runtime.Intrinsics.Vector128.Create(history.Slice(j, 4));
                }
                int sum = System.Runtime.Intrinsics.Vector128.Sum(acc);
                for (; j < order; j++)
                {
                    sum += forward[j] * history[j];
                }
                s[i] += sum >> shift;
            }
            return;
        }
        for (int i = order; i < s.Length; i++)
        {
            ReadOnlySpan<int> history = s.Slice(i - order, order);
            int sum = 0;
            for (int j = 0; j < order; j++)
            {
                sum += forward[j] * history[j];
            }
            s[i] += sum >> shift;
        }
    }

    private static void Decorrelate(int assignment, int[][] blocks, int blockSize)
    {
        int[] a = blocks[0];
        int[] b = blocks[1];
        switch (assignment)
        {
            case 8:
                // Left, side: right = left - side
                for (int i = 0; i < blockSize; i++)
                {
                    b[i] = a[i] - b[i];
                }
                break;
            case 9:
                // Side, right: left = side + right
                for (int i = 0; i < blockSize; i++)
                {
                    a[i] += b[i];
                }
                break;
            case 10:
                // Mid, side
                for (int i = 0; i < blockSize; i++)
                {
                    int side = b[i];
                    int mid = (a[i] << 1) | (side & 1);
                    a[i] = (mid + side) >> 1;
                    b[i] = (mid - side) >> 1;
                }
                break;
        }
    }

    // Big-endian bits, as FLAC writes them, through a 64-bit cache: the next bits are its top ones,
    // and it holds _count of them, the rest zero
    private ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private ulong _cache;
        private int _count;
        private int _next;   // the next byte to load into the cache

        public BitReader(ReadOnlySpan<byte> data)
        {
            this._data = data;
        }

        private readonly long Position => ((long)this._next * 8) - this._count;

        public readonly long BytePosition => (this.Position + 7) >> 3;

        /// <summary>Whether every byte has been loaded: a read that fails now ran out of data.</summary>
        public readonly bool AtEnd => this._next >= this._data.Length;

        // Tops the cache up to at least 57 bits, or as many as are left
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private void Refill()
        {
            if (this._next + 8 <= this._data.Length)
            {
                // Whole bytes that fit below the bits held: at least one, as fewer than 57 are
                int take = (63 - this._count) >> 3;
                ulong bytes = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(this._data.Slice(this._next, 8)) >> (64 - (take * 8));
                this._cache |= bytes << (64 - this._count - (take * 8));
                this._next += take;
                this._count += take * 8;
            }
            else
            {
                this.RefillTail();
            }
        }

        // NOTE: To 63 bits at most, as the fast refill fills, so no read shifts by 64 (which C# takes as 0)
        private void RefillTail()
        {
            while ((this._count <= 55) && (this._next < this._data.Length))
            {
                this._cache |= (ulong)this._data[this._next++] << (56 - this._count);
                this._count += 8;
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public uint Read(int count)
        {
            uint value = this.Peek(count);
            this._cache <<= count;
            this._count -= count;
            return value;
        }

        /// <summary>The next <paramref name="count"/> bits (up to 32) without reading them.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public uint Peek(int count)
        {
            if (count == 0)
            {
                return 0;
            }
            if (this._count < count)
            {
                this.Refill();
                if (this._count < count)
                {
                    throw new InvalidDataException("FLAC file ends early.");
                }
            }
            return (uint)(this._cache >> (64 - count));
        }

        public int ReadSigned(int count)
        {
            uint value = this.Read(count);
            int unused = 32 - count;
            return unused <= 0 ? (int)value : ((int)(value << unused)) >> unused;
        }

        public int ReadUnary()
        {
            int zeros = 0;
            while (true)
            {
                if (this._count == 0)
                {
                    this.Refill();
                    if (this._count == 0)
                    {
                        throw new InvalidDataException("FLAC file ends early.");
                    }
                }
                int leading = System.Numerics.BitOperations.LeadingZeroCount(this._cache);
                if (leading < this._count)
                {
                    this._cache <<= leading + 1;
                    this._count -= leading + 1;
                    return zeros + leading;
                }

                // Every bit held is a zero
                zeros += this._count;
                this._cache = 0;
                this._count = 0;
            }
        }

        /// <summary>
        /// A Rice code with parameter <paramref name="parameter"/> (unary quotient, then that many
        /// bits), unfolded to a signed value.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public int ReadRice(int parameter)
        {
            if (this._count < 32)
            {
                this.Refill();
            }
            int leading = System.Numerics.BitOperations.LeadingZeroCount(this._cache);
            uint folded;
            if (leading + 1 + parameter <= this._count)
            {
                // The whole code is in the cache
                ulong rest = this._cache << (leading + 1);
                folded = ((uint)leading << parameter) | (parameter == 0 ? 0u : (uint)(rest >> (64 - parameter)));
                this._cache = rest << parameter;
                this._count -= leading + 1 + parameter;
            }
            else
            {
                folded = ((uint)this.ReadUnary() << parameter) | this.Read(parameter);
            }
            return (int)(folded >> 1) ^ -(int)(folded & 1);
        }

        /// <summary>
        /// <see cref="ReadRice"/> for each of <paramref name="run"/>, the cache in locals for the whole
        /// run (codes that don't fit what's cached go through <see cref="ReadRice"/>).
        /// </summary>
        public void ReadRiceRun(Span<int> run, int parameter)
        {
            ulong cache = this._cache;
            int count = this._count;
            int next = this._next;
            ref byte data = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(this._data);
            int length = this._data.Length;
            ref int output = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(run);
            for (int i = 0; i < run.Length; i++)
            {
                if ((count < 32) && (next + 8 <= length))
                {
                    int take = (63 - count) >> 3;
                    ulong bytes = System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(System.Runtime.CompilerServices.Unsafe.ReadUnaligned<ulong>(ref System.Runtime.CompilerServices.Unsafe.Add(ref data, next))) >> (64 - (take * 8));
                    cache |= bytes << (64 - count - (take * 8));
                    next += take;
                    count += take * 8;
                }
                int leading = System.Numerics.BitOperations.LeadingZeroCount(cache);
                int total = leading + 1 + parameter;
                if (total <= count)
                {
                    ulong rest = cache << (leading + 1);
                    uint folded = ((uint)leading << parameter) | (uint)((rest >> 1) >> (63 - parameter));
                    cache = rest << parameter;
                    count -= total;
                    System.Runtime.CompilerServices.Unsafe.Add(ref output, i) = (int)(folded >> 1) ^ -(int)(folded & 1);
                }
                else
                {
                    // Near the end, or a long code: the general path, with the state handed over
                    (this._cache, this._count, this._next) = (cache, count, next);
                    System.Runtime.CompilerServices.Unsafe.Add(ref output, i) = this.ReadRice(parameter);
                    (cache, count, next) = (this._cache, this._count, this._next);
                }
            }
            (this._cache, this._count, this._next) = (cache, count, next);
        }

        public void Skip(long count)
        {
            if (count <= this._count)
            {
                // Within the cache (a shift by 64 would leave it, so it's cleared instead)
                this._cache = count == 64 ? 0 : this._cache << (int)count;
                this._count -= (int)count;
                return;
            }
            long target = this.Position + count;
            if (target > (long)this._data.Length * 8)
            {
                this._next = this._data.Length;
                throw new InvalidDataException("FLAC file ends early.");
            }
            this._next = (int)(target >> 3);
            this._cache = 0;
            this._count = 0;
            int offset = (int)(target & 7);
            if (offset > 0)
            {
                this.Refill();
                this._cache <<= offset;
                this._count -= offset;
            }
        }

        // The bits read so far end at a byte when the cache holds a multiple of 8
        public void AlignToByte()
        {
            int drop = this._count & 7;
            this._cache <<= drop;
            this._count -= drop;
        }

        // A frame or sample number, in UTF-8's form of up to 7 bytes
        public void SkipUtf8Number()
        {
            uint first = this.Read(8);
            int more = 0;
            while ((more < 7) && ((first & (0x80u >> more)) != 0))
            {
                more++;
            }
            if (more == 1 || more > 7)
            {
                throw new InvalidDataException("FLAC frame has a bad frame number.");
            }
            this.Skip(more == 0 ? 0 : (more - 1) * 8);
        }
    }
}
