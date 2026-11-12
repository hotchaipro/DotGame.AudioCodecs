// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.IO;
using DotGame.AudioCodecs.Flac;
using DotGame.AudioCodecs.Opus;

namespace DotGame.AudioCodecs.Tests;

/// <summary>
/// The CRCs: the carry-less path (where the machine has one) and the tables both give the CRC a bit at a
/// time does, at every length and split; and <see cref="AudioDecoderOptions.SkipChecksums"/> skips them.
/// </summary>
public sealed class CrcTests
{
    [Fact]
    public void TheCheckValues_AreTheCatalogs()
    {
        // CRC-16/UMTS and CRC-32/POSIX (whose final XOR Ogg doesn't have) of "123456789"
        Assert.Equal(0xFEE8, FlacFrame.Crc16("123456789"u8));
        Assert.Equal(0x765E7680u ^ 0xFFFFFFFFu, Crc.Ogg.Update(0, "123456789"u8));
    }

    [Theory]
    [InlineData(0x04C11DB7u)]
    [InlineData(0x80050000u)]
    public void EveryLengthAndSplit_GivesTheBitwiseCrc(uint polynomial)
    {
        Crc crc = polynomial == 0x04C11DB7u ? Crc.Ogg : Crc.Flac16;
        var random = new Random(polynomial == 0x04C11DB7u ? 1 : 2);
        byte[] data = new byte[9000];
        random.NextBytes(data);
        foreach (int length in Lengths())
        {
            ReadOnlySpan<byte> span = data.AsSpan(random.Next(16), length);
            uint start = (uint)random.Next() << 1;
            if (polynomial == 0x80050000u)
            {
                start &= 0xFFFF0000;
            }
            uint expected = Bitwise(polynomial, start, span);
            Assert.Equal(expected, crc.Update(start, span));
            Assert.Equal(expected, crc.UpdateWithTables(start, span));
            int split = random.Next(length + 1);
            Assert.Equal(expected, crc.Update(crc.Update(start, span[..split]), span[split..]));
        }
    }

    [Fact]
    public void SkipChecksums_DecodesAFlacFrameWithAWrongCrc16()
    {
        byte[] file = File.ReadAllBytes(FlacDecoderTests.Fixture("stereo-lpc12.flac"));
        short[] expected;
        using (var decoder = new FlacDecoder(file))
        {
            expected = OpusDecoderTests.ReadAll(decoder, 4096);
        }
        file[^1] ^= 0x01;   // the last frame's CRC-16

        Assert.Throws<InvalidDataException>(() =>
        {
            using var decoder = new FlacDecoder(file);
            OpusDecoderTests.ReadAll(decoder, 4096);
        });
        var skip = new AudioDecoderOptions { SkipChecksums = true };
        using (var decoder = new FlacDecoder(file, skip))
        {
            Assert.Equal(expected, OpusDecoderTests.ReadAll(decoder, 4096));
        }
        using (var decoder = AudioDecoder.Open(new UnseekableStream(file), skip))
        {
            Assert.Equal(expected, OpusDecoderTests.ReadAll(decoder, 4096));
        }
    }

    [Fact]
    public void SkipChecksums_DecodesAnOggPageWithAWrongCrc()
    {
        byte[] file = File.ReadAllBytes(OpusDecoderTests.Fixture("celt-mono-64k"));
        short[] expected;
        using (var decoder = new OpusDecoder(file))
        {
            expected = OpusDecoderTests.ReadAll(decoder, 4096);
        }
        int page = file.AsSpan(file.Length / 2).IndexOf("OggS"u8) + (file.Length / 2);
        file[page + 22] ^= 0x01;

        Assert.Throws<InvalidDataException>(() =>
        {
            using var decoder = new OpusDecoder(file);
            OpusDecoderTests.ReadAll(decoder, 4096);
        });
        var skip = new AudioDecoderOptions { SkipChecksums = true };
        using (var decoder = new OpusDecoder(new UnseekableStream(file), skip))
        {
            Assert.Equal(expected, OpusDecoderTests.ReadAll(decoder, 4096));
        }
        using (var decoder = AudioDecoder.Open(file, skip))
        {
            Assert.Equal(expected, OpusDecoderTests.ReadAll(decoder, 4096));
        }
    }

    private static int[] Lengths()
    {
        var lengths = new int[300 + 8];
        for (int i = 0; i < 300; i++)
        {
            lengths[i] = i;
        }
        int[] longer = [511, 512, 513, 1000, 4095, 4096, 4111, 8975];
        longer.CopyTo(lengths, 300);
        return lengths;
    }

    // A bit at a time, the first bit the highest power
    private static uint Bitwise(uint polynomial, uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            crc ^= (uint)b << 24;
            for (int k = 0; k < 8; k++)
            {
                crc = ((crc & 0x80000000) != 0) ? (crc << 1) ^ polynomial : crc << 1;
            }
        }
        return crc;
    }
}
