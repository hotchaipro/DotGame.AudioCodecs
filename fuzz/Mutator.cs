// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace DotGame.AudioCodecs.Fuzz;

/// <summary>
/// Makes new inputs from old ones: AFL's havoc (bit flips, interesting values, arithmetic, blocks
/// deleted, copied and inserted, truncation, splicing two inputs), and mutations that know each
/// format's structure, so a change gets past the checks that would otherwise reject it at once: an Ogg
/// file's page CRCs and lacing, a FLAC file's frame CRC-8 and CRC-16, a WAV file's RIFF and chunk sizes.
/// </summary>
internal sealed class Mutator
{
    private static readonly int[] Interesting8 = [0, 1, 2, 3, 4, 7, 8, 15, 16, 31, 32, 63, 64, 100, 127, 128, 129, 192, 254, 255];
    private static readonly int[] Interesting16 = [0, 1, 2, 8, 16, 32, 64, 128, 255, 256, 512, 1000, 1024, 4096, 8191, 8192, 8193, 16383, 16384, 32767, 32768, 65535];
    private static readonly long[] Interesting32 = [0, 1, 2, 16, 255, 256, 65535, 65536, 0x7FFF_FFFF, 0x8000_0000, 0xFFFF_FFFF, 0x0100_0000, 8192, 8193, 16384, 1 << 20];

    private readonly Random random;
    private readonly int maxLength;

    public Mutator(
        int seed,
        int maxLength)
    {
        this.random = new Random(seed);
        this.maxLength = maxLength;
    }

    public byte[] Mutate(
        byte[] parent,
        IReadOnlyList<byte[]> corpus)
    {
        List<byte> data = [.. parent];
        Format kind = Detect(parent);
        int rounds = 1 << this.random.Next(1, 5);
        for (int i = 0; i < rounds; i++)
        {
            if ((this.random.Next(4) == 0) && (kind != Format.Any))
            {
                this.Structured(data, kind, corpus);
            }
            else
            {
                this.Havoc(data, corpus);
            }

            if (data.Count > this.maxLength)
            {
                data.RemoveRange(this.maxLength, data.Count - this.maxLength);
            }
        }

        byte[] result = [.. data];

        // Most of the time, make the sizes and checksums right again, so the change gets as far as
        // the code that reads what was changed
        if (this.random.Next(10) != 0)
        {
            if (kind == Format.Opus)
            {
                FixOgg(result);
            }
            else if (kind == Format.Flac)
            {
                FixFlac(result);
            }
            else if (kind == Format.Wav)
            {
                FixRiff(result);
            }
        }

        return result;
    }

    private static Format Detect(
        byte[] data)
    {
        if ((data.Length >= 4) && (data[0] == (byte)'O') && (data[1] == (byte)'g') && (data[2] == (byte)'g') && (data[3] == (byte)'S'))
        {
            return Format.Opus;
        }

        if ((data.Length >= 4) && (data[0] == (byte)'f') && (data[1] == (byte)'L') && (data[2] == (byte)'a') && (data[3] == (byte)'C'))
        {
            return Format.Flac;
        }

        if ((data.Length >= 12) && (data[0] == (byte)'R') && (data[8] == (byte)'W'))
        {
            return Format.Wav;
        }

        return Format.Any;
    }

    private void Havoc(
        List<byte> data,
        IReadOnlyList<byte[]> corpus)
    {
        int count = data.Count;
        switch (this.random.Next(16))
        {
            case 0 when count > 0:
                data[this.random.Next(count)] ^= (byte)(1 << this.random.Next(8));
                break;
            case 1 when count > 0:
                data[this.random.Next(count)] = (byte)Interesting8[this.random.Next(Interesting8.Length)];
                break;
            case 2 when count > 1:
                WriteInt(data, this.random.Next(count - 1), 2, Interesting16[this.random.Next(Interesting16.Length)]);
                break;
            case 3 when count > 3:
                WriteInt(data, this.random.Next(count - 3), 4, Interesting32[this.random.Next(Interesting32.Length)]);
                break;
            case 4 when count > 0:
                {
                    int at = this.random.Next(count);
                    data[at] = (byte)(data[at] + this.random.Next(-35, 36));
                    break;
                }

            case 5 when count > 1:
                {
                    int at = this.random.Next(count - 1);
                    int value = ((data[at] << 8) | data[at + 1]) + this.random.Next(-35, 36);
                    WriteInt(data, at, 2, value);
                    break;
                }

            case 6 when count > 0:
                data[this.random.Next(count)] = (byte)this.random.Next(256);
                break;
            case 7 when count > 1:
                {
                    // Delete a block
                    int length = this.BlockLength(count);
                    data.RemoveRange(this.random.Next(count - length + 1), length);
                    break;
                }

            case 8 when count > 0:
                {
                    // Copy a block over another
                    int length = this.BlockLength(count);
                    int from = this.random.Next(count - length + 1);
                    int to = this.random.Next(count - length + 1);
                    byte[] block = data.GetRange(from, length).ToArray();
                    for (int i = 0; i < length; i++)
                    {
                        data[to + i] = block[i];
                    }

                    break;
                }

            case 9 when count > 0:
                {
                    // Insert a copy of a block
                    int length = this.BlockLength(count);
                    int from = this.random.Next(count - length + 1);
                    data.InsertRange(this.random.Next(count + 1), data.GetRange(from, length));
                    break;
                }

            case 10:
                {
                    // Insert random or repeated bytes
                    int length = this.random.Next(1, 33);
                    var block = new byte[length];
                    if (this.random.Next(2) == 0)
                    {
                        this.random.NextBytes(block);
                    }
                    else
                    {
                        Array.Fill(block, (byte)Interesting8[this.random.Next(Interesting8.Length)]);
                    }

                    data.InsertRange(this.random.Next(count + 1), block);
                    break;
                }

            case 11 when count > 1:
                {
                    // Truncate
                    int keep = this.random.Next(1, count);
                    data.RemoveRange(keep, count - keep);
                    break;
                }
            case 12 when (count > 1) && (corpus.Count > 0):
                {
                    // Splice: this input's start, another's end
                    byte[] other = corpus[this.random.Next(corpus.Count)];
                    int at = this.random.Next(count);
                    int from = this.random.Next(other.Length + 1);
                    data.RemoveRange(at, count - at);
                    data.AddRange(other.AsSpan(from).ToArray());
                    break;
                }

            case 13 when count > 8:
                {
                    // Flip several bits near each other
                    int at = this.random.Next(count - 8);
                    for (int i = 0; i < 8; i++)
                    {
                        if (this.random.Next(3) == 0)
                        {
                            data[at + i] ^= (byte)(1 << this.random.Next(8));
                        }
                    }

                    break;
                }

            case 14 when count > 0:
                {
                    // Zero or saturate a block
                    int length = this.BlockLength(count);
                    int at = this.random.Next(count - length + 1);
                    byte value = (this.random.Next(2) == 0) ? (byte)0 : (byte)0xFF;
                    for (int i = 0; i < length; i++)
                    {
                        data[at + i] = value;
                    }

                    break;
                }

            default:
                if (count > 0)
                {
                    data[this.random.Next(count)] = (byte)this.random.Next(256);
                }

                break;
        }
    }

    private int BlockLength(
        int count)
    {
        int max = Math.Min(count, this.random.Next(4) == 0 ? 1024 : 32);
        return this.random.Next(1, max + 1);
    }

    private static void WriteInt(
        List<byte> data,
        int at,
        int size,
        long value)
    {
        bool bigEndian = (value & 1) == 0;
        for (int i = 0; i < size; i++)
        {
            int shift = bigEndian ? (8 * (size - 1 - i)) : (8 * i);
            data[at + i] = (byte)(value >> shift);
        }
    }

    private void Structured(
        List<byte> data,
        Format kind,
        IReadOnlyList<byte[]> corpus)
    {
        switch (kind)
        {
            case Format.Opus:
                this.MutateOgg(data, corpus);
                break;
            case Format.Flac:
                this.MutateFlac(data);
                break;
            default:
                this.MutateWav(data);
                break;
        }
    }

    // Ogg ------------------------------------------------------------------------------------------

    // The offsets and lengths of the pages, as far as they parse
    private static List<(int Offset, int Length)> Pages(
        IReadOnlyList<byte> data)
    {
        var pages = new List<(int, int)>();
        int at = 0;
        while ((at + 27 <= data.Count) && (data[at] == (byte)'O') && (data[at + 1] == (byte)'g') && (data[at + 2] == (byte)'g') && (data[at + 3] == (byte)'S'))
        {
            int segments = data[at + 26];
            if (at + 27 + segments > data.Count)
            {
                break;
            }
            int length = 27 + segments;
            for (int i = 0; i < segments; i++)
            {
                length += data[at + 27 + i];
            }
            if (at + length > data.Count)
            {
                break;
            }
            pages.Add((at, length));
            at += length;
        }
        return pages;
    }

    private void MutateOgg(
        List<byte> data,
        IReadOnlyList<byte[]> corpus)
    {
        List<(int Offset, int Length)> pages = Pages(data);
        if (pages.Count == 0)
        {
            return;
        }
        var (offset, length) = pages[this.random.Next(pages.Count)];
        switch (this.random.Next(8))
        {
            case 0:
                // A header field: flags, granule position, serial, sequence
                {
                    int field = this.random.Next(4);
                    int at = offset + (field switch { 0 => 5, 1 => 6 + this.random.Next(8), 2 => 14 + this.random.Next(4), _ => 18 + this.random.Next(4) });
                    data[at] = (byte)(this.random.Next(2) == 0 ? this.random.Next(256) : data[at] ^ (1 << this.random.Next(8)));
                    break;
                }
            case 1:
                // A lacing value, then the page's body made to fit it
                {
                    int segments = data[offset + 26];
                    if (segments == 0)
                    {
                        break;
                    }
                    int lace = offset + 27 + this.random.Next(segments);
                    int old = data[lace];
                    int now = this.random.Next(4) == 0 ? 255 : this.random.Next(256);
                    data[lace] = (byte)now;
                    int bodyEnd = offset + length;
                    if (now > old)
                    {
                        data.InsertRange(bodyEnd, new byte[now - old]);
                    }
                    else
                    {
                        data.RemoveRange(bodyEnd - (old - now), old - now);
                    }
                    break;
                }
            case 2 when pages.Count > 1:
                // A page dropped or repeated
                if (this.random.Next(2) == 0)
                {
                    data.RemoveRange(offset, length);
                }
                else
                {
                    data.InsertRange(offset + length, data.GetRange(offset, length));
                }
                break;
            case 3 when pages.Count > 2:
                // Two pages swapped
                {
                    var other = pages[this.random.Next(pages.Count)];
                    if (other.Offset > offset)
                    {
                        List<byte> a = data.GetRange(offset, length);
                        List<byte> b = data.GetRange(other.Offset, other.Length);
                        data.RemoveRange(other.Offset, other.Length);
                        data.InsertRange(other.Offset, a);
                        data.RemoveRange(offset, length);
                        data.InsertRange(offset, b);
                    }
                    break;
                }
            case 4:
                // A packet's first byte, its TOC: mode, bandwidth, frame size, stereo, frame count
                {
                    int segments = data[offset + 26];
                    int body = offset + 27 + segments;
                    if (body < offset + length)
                    {
                        data[body] = (byte)this.random.Next(256);
                    }
                    break;
                }
            case 5 when corpus.Count > 0:
                // A page from another file
                {
                    byte[] other = corpus[this.random.Next(corpus.Count)];
                    List<(int Offset, int Length)> theirs = Pages(other);
                    if (theirs.Count > 0)
                    {
                        var (o, l) = theirs[this.random.Next(theirs.Count)];
                        data.InsertRange(offset, other.AsSpan(o, l).ToArray());
                    }
                    break;
                }
            case 6 when offset == 0:
                // OpusHead: the channels, pre-skip, gain or mapping
                {
                    int head = 28;
                    if (head + 19 <= data.Count)
                    {
                        int at = head + (this.random.Next(5) switch { 0 => 9, 1 => 10, 2 => 16, 3 => 18, _ => 19 + this.random.Next(10) });
                        if (at < data.Count)
                        {
                            data[at] = (byte)this.random.Next(256);
                        }
                    }
                    break;
                }
            default:
                // Bytes inside the page's body
                {
                    int body = offset + 27 + data[offset + 26];
                    if (body < offset + length)
                    {
                        data[body + this.random.Next(offset + length - body)] ^= (byte)(1 << this.random.Next(8));
                    }
                    break;
                }
        }
    }

    // Makes each page's CRC right again, as far as the pages parse
    private static void FixOgg(
        byte[] data)
    {
        foreach (var (offset, length) in Pages(data))
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset + 22), 0);
            uint crc = 0;
            for (int i = 0; i < length; i++)
            {
                crc = (crc << 8) ^ OggTable[(int)((crc >> 24) ^ data[offset + i])];
            }
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset + 22), crc);
        }
    }

    private static readonly uint[] OggTable = MakeOggTable();

    private static uint[] MakeOggTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint r = i << 24;
            for (int k = 0; k < 8; k++)
            {
                r = (r & 0x80000000) != 0 ? (r << 1) ^ 0x04C11DB7 : r << 1;
            }
            table[i] = r;
        }
        return table;
    }

    // FLAC -----------------------------------------------------------------------------------------

    // Where the frames start: after the metadata blocks
    private static int FirstFrame(
        IReadOnlyList<byte> data)
    {
        int at = 4;
        while (at + 4 <= data.Count)
        {
            bool last = (data[at] & 0x80) != 0;
            at += 4 + ((data[at + 1] << 16) | (data[at + 2] << 8) | data[at + 3]);
            if (last)
            {
                return at;
            }
        }
        return data.Count;
    }

    // The frames' starts: each sync code after the metadata
    private static List<int> Frames(
        IReadOnlyList<byte> data)
    {
        var frames = new List<int>();
        for (int i = FirstFrame(data); i + 1 < data.Count; i++)
        {
            if ((data[i] == 0xFF) && ((data[i + 1] & 0xFE) == 0xF8))
            {
                frames.Add(i);
            }
        }
        return frames;
    }

    private void MutateFlac(
        List<byte> data)
    {
        int first = FirstFrame(data);
        List<int> frames = Frames(data);
        switch (this.random.Next(6))
        {
            case 0 when first > 8:
                // STREAMINFO: block sizes, frame sizes, rate, channels, bits, total samples
                {
                    int at = 8 + this.random.Next(Math.Min(18, first - 8));
                    data[at] = (byte)(this.random.Next(2) == 0 ? this.random.Next(256) : data[at] ^ (1 << this.random.Next(8)));
                    break;
                }
            case 1 when frames.Count > 0:
                // A frame header's block size, rate, channels, bits or number
                {
                    int at = frames[this.random.Next(frames.Count)] + 2 + this.random.Next(4);
                    if (at < data.Count)
                    {
                        data[at] = (byte)this.random.Next(256);
                    }
                    break;
                }
            case 2 when frames.Count > 0:
                // A subframe header: type, order, wasted bits
                {
                    int at = frames[this.random.Next(frames.Count)] + 5 + this.random.Next(4);
                    if (at < data.Count)
                    {
                        data[at] ^= (byte)(1 << this.random.Next(8));
                    }
                    break;
                }
            case 3 when frames.Count > 1:
                // A frame dropped or repeated
                {
                    int index = this.random.Next(frames.Count - 1);
                    int length = frames[index + 1] - frames[index];
                    if (this.random.Next(2) == 0)
                    {
                        data.RemoveRange(frames[index], length);
                    }
                    else
                    {
                        data.InsertRange(frames[index + 1], data.GetRange(frames[index], length));
                    }
                    break;
                }
            case 4 when first > 8:
                // A metadata block's type or length
                {
                    data[4 + this.random.Next(4)] = (byte)this.random.Next(256);
                    break;
                }
            default:
                // Bits in the residuals
                if (frames.Count > 0)
                {
                    int at = frames[this.random.Next(frames.Count)] + 8 + this.random.Next(256);
                    if (at < data.Count)
                    {
                        data[at] ^= (byte)(1 << this.random.Next(8));
                    }
                }
                break;
        }
    }

    // Makes each frame's CRC-8 and CRC-16 right again, taking each frame to end where the next begins
    private static void FixFlac(
        byte[] data)
    {
        List<int> frames = Frames(data);
        for (int f = 0; f < frames.Count; f++)
        {
            int start = frames[f];
            int end = f + 1 < frames.Count ? frames[f + 1] : data.Length;
            int header = HeaderLength(data, start);
            if ((header > 0) && (start + header < end))
            {
                data[start + header] = Crc8(data.AsSpan(start, header));
            }
            if (end - start >= header + 3)
            {
                ushort crc = Crc16(data.AsSpan(start, end - start - 2));
                data[end - 2] = (byte)(crc >> 8);
                data[end - 1] = (byte)crc;
            }
        }
    }

    // A frame header's bytes before its CRC-8, or 0 if it doesn't parse
    private static int HeaderLength(
        byte[] data,
        int start)
    {
        if (start + 5 > data.Length)
        {
            return 0;
        }
        int blockCode = data[start + 2] >> 4;
        int rateCode = data[start + 2] & 15;
        int at = start + 4;
        int first = data[at++];
        int more = 0;
        while ((more < 7) && ((first & (0x80 >> more)) != 0))
        {
            more++;
        }
        at += Math.Max(0, more - 1);
        at += blockCode switch { 6 => 1, 7 => 2, _ => 0 };
        at += rateCode switch { 12 => 1, 13 or 14 => 2, _ => 0 };
        return at <= data.Length ? at - start : 0;
    }

    private static byte Crc8(
        ReadOnlySpan<byte> data)
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

    private static ushort Crc16(
        ReadOnlySpan<byte> data)
    {
        uint crc = 0;
        foreach (byte b in data)
        {
            crc ^= (uint)b << 8;
            for (int k = 0; k < 8; k++)
            {
                crc = (crc & 0x8000) != 0 ? ((crc << 1) ^ 0x8005) & 0xFFFF : (crc << 1) & 0xFFFF;
            }
        }
        return (ushort)crc;
    }

    // WAV ------------------------------------------------------------------------------------------

    private void MutateWav(
        List<byte> data)
    {
        if (data.Count < 44)
        {
            return;
        }
        switch (this.random.Next(4))
        {
            case 0:
                // The format chunk: tag, channels, rate, alignment, bits
                {
                    int at = 20 + this.random.Next(16);
                    data[at] = (byte)(this.random.Next(2) == 0 ? this.random.Next(256) : data[at] ^ (1 << this.random.Next(8)));
                    break;
                }
            case 1:
                // An extra chunk before the data, of any size
                {
                    int size = this.random.Next(4) == 0 ? this.random.Next(int.MaxValue) : this.random.Next(64);
                    var chunk = new List<byte> { (byte)'L', (byte)'I', (byte)'S', (byte)'T', (byte)size, (byte)(size >> 8), (byte)(size >> 16), (byte)(size >> 24) };
                    chunk.AddRange(new byte[Math.Min(size, 64)]);
                    data.InsertRange(12, chunk);
                    break;
                }
            case 2:
                // The data chunk's size
                {
                    for (int at = 12; at + 8 <= data.Count; at++)
                    {
                        if ((data[at] == (byte)'d') && (data[at + 1] == (byte)'a') && (data[at + 2] == (byte)'t') && (data[at + 3] == (byte)'a'))
                        {
                            WriteInt(data, at + 4, 4, (this.random.Next(2) == 0) ? 0xFFFF_FFFF : this.random.Next(int.MaxValue));
                            break;
                        }
                    }
                    break;
                }
            default:
                // The format chunk's size
                WriteInt(data, 16, 4, this.random.Next(2) == 0 ? this.random.Next(14, 48) : this.random.Next(int.MaxValue));
                break;
        }
    }

    // Makes the RIFF size right again
    private static void FixRiff(
        byte[] data)
    {
        if (data.Length >= 8)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)(data.Length - 8));
        }
    }
}