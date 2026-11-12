// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using DotGame.AudioCodecs.Opus;

namespace DotGame.AudioCodecs.Tests;

/// <summary>
/// The Ogg Opus decoder and the packet decoder. Each file in TestData/Opus was made by ffmpeg 9.0.2 with
/// libopus 1.6.1 from synthesized music and speech (FixtureSources), to cover CELT, SILK and hybrid, mono
/// and stereo, and 2.5 to 60 ms frames. The hashes are of the samples native libopus 1.6.1 (its
/// fixed-point build, which the decoder is a port of) decodes, with the pre-skip dropped and the end
/// trimmed to the last granule position.
/// </summary>
public sealed class OpusDecoderTests
{
    public static TheoryData<string> Fixtures => new()
    {
        "celt-stereo-112k", "celt-mono-64k", "celt-2_5ms", "celt-60ms", "silk-nb-mono",
        "silk-wb-mono", "silk-stereo", "silk-60ms", "hybrid-mono", "hybrid-stereo",
    };

    internal static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "TestData", "Opus", name + ".opus");

    internal static short[] Whole(byte[] file)
    {
        using var decoder = new OpusDecoder(file);
        var samples = new short[checked((int)decoder.Frames!.Value * decoder.Channels)];
        Assert.Equal(decoder.Frames.Value, decoder.Read(samples));
        return samples;
    }

    [Theory]
    [InlineData("celt-stereo-112k", 2, 192000, "045dfa97302221e793f3203633b1fe13859973da2ea16fabf1ab98aac06e0ab0")]
    [InlineData("celt-mono-64k", 1, 144000, "ddebac4c722a08e7a16bc8bdda8b404cdcb84d5018e7718194334c975dc00e2a")]
    [InlineData("celt-2_5ms", 2, 96000, "e5a8897ca1f100aa97ab9153acfcfc8c24cc1892747aaa481c2b8a300aff6009")]
    [InlineData("celt-60ms", 2, 96000, "da90d5e0eaca981ce36d1b3ebcd91a59ceb27cb30d48c1341f84de94c7d5cf26")]
    [InlineData("silk-nb-mono", 1, 51409, "65fb4aeb693ea178636bc5e8cc374a2ffb5fd9dc57b804f7f48787745038ae31")]
    [InlineData("silk-wb-mono", 1, 51409, "85bb6f18691f0807d9061438577bdc6a25be5f39df05b2b9549316a65abdc4ec")]
    [InlineData("silk-stereo", 2, 51409, "2d0cd129e7999b950ee01233ab9823680f429b173325bc326612ece2716a8c0b")]
    [InlineData("silk-60ms", 1, 51409, "b444bfd44e679df95c1a9dc4d01b5b52ef83732f793423a3edd143db5d0e18c4")]
    [InlineData("hybrid-mono", 1, 51409, "64d1afe7551e4cefdc0e2b9ce98a2c6e5ddc1dd7604fe3cb131a3b08a00f1987")]
    [InlineData("hybrid-stereo", 2, 144000, "ae26a7244ec8334f2f23f6849a9db342843010513560eb20c860f3d15626754f")]
    public void Decodes_ToTheSamplesLibopusDecodes(string name, int channels, int frames, string sha256)
    {
        byte[] file = File.ReadAllBytes(Fixture(name));
        using var decoder = AudioDecoder.Open(file);
        Assert.IsType<OpusDecoder>(decoder);
        Assert.Equal(48000, decoder.SampleRate);
        Assert.Equal(channels, decoder.Channels);
        Assert.Equal(frames, decoder.Frames);

        var samples = new short[frames * channels];
        Assert.Equal(frames, decoder.Read(samples));
        Assert.Equal(0, decoder.Read(samples));
        Assert.Equal(sha256, Hash(samples));

        // From a stream that can't seek, its length known only at the end
        using var stream = new UnseekableStream(file);
        using var streamed = AudioDecoder.Open(stream);
        Assert.Null(streamed.Frames);
        Assert.False(streamed.CanSeek);
        var again = new short[frames * channels];
        Assert.Equal(frames, streamed.Read(again));
        Assert.Equal(0, streamed.Read(again));
        Assert.Equal(frames, streamed.Frames);
        Assert.Equal(sha256, Hash(again));
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Reads_InChunks_TheSamplesOfOneRead(string name)
    {
        byte[] file = File.ReadAllBytes(Fixture(name));
        short[] whole = Whole(file);
        foreach (int chunkFrames in (ReadOnlySpan<int>)[1, 7, 960, 1013, 4801])
        {
            using var decoder = new OpusDecoder(new MemoryStream(file));
            Assert.Equal(whole, ReadAll(decoder, chunkFrames));
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Float_IsTheDecodersOwn24Bits_AndRoundsToTheShorts(string name)
    {
        byte[] file = File.ReadAllBytes(Fixture(name));
        short[] shorts = Whole(file);
        using var decoder = new OpusDecoder(file);
        var floats = new float[shorts.Length];
        decoder.Read(floats);
        for (int i = 0; i < shorts.Length; i++)
        {
            // RES2INT16 of the same 24-bit sample: rounded, then saturated
            float scaled = floats[i] * 32768f;
            Assert.InRange(shorts[i], Math.Max(short.MinValue, (int)MathF.Floor(scaled)), Math.Min(short.MaxValue, (int)MathF.Ceiling(scaled)));
        }
    }

    [Theory]
    [InlineData("celt-stereo-112k", 48123)]
    [InlineData("celt-60ms", 70001)]
    [InlineData("silk-wb-mono", 20000)]
    [InlineData("hybrid-stereo", 100000)]
    public void Seek_LandsOnTheFrame_ConvergedOnADecodeFromTheStart(string name, int frame)
    {
        byte[] file = File.ReadAllBytes(Fixture(name));
        short[] whole = Whole(file);
        using var decoder = new OpusDecoder(new MemoryStream(file));
        decoder.Seek(frame);
        Assert.Equal(frame, decoder.Position);

        int channels = decoder.Channels;
        var chunk = new short[4800 * channels];
        Assert.Equal(4800, decoder.Read(chunk));

        // After the pre-roll the decoder has nearly converged on the samples of a decode from the
        // start: what's left is far below the signal, and a sample off would be heard as a seam
        double signal = 0;
        double error = 0;
        for (int i = 0; i < chunk.Length; i++)
        {
            double expected = whole[(frame * channels) + i];
            signal += expected * expected;
            error += (chunk[i] - expected) * (chunk[i] - expected);
        }
        Assert.True(signal > 0);
        double snr = 10 * Math.Log10(signal / Math.Max(error, 1));
        Assert.True(snr > 80, $"{snr:F1} dB");
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Seek_ToTheStart_IsExact_AfterReading(string name)
    {
        byte[] file = File.ReadAllBytes(Fixture(name));
        using var decoder = new OpusDecoder(file);
        var chunk = new short[30000 * decoder.Channels];
        decoder.Read(chunk);

        decoder.Seek(0);

        Assert.Equal(Whole(file), ReadAll(decoder, 4800));
    }

    [Fact]
    public void Read_StopsAtTheEnd()
    {
        using var decoder = new OpusDecoder(File.ReadAllBytes(Fixture("celt-mono-64k")));
        decoder.Seek(143990);
        var chunk = new short[4800];

        Assert.Equal(10, decoder.Read(chunk));
        Assert.Equal(0, decoder.Read(chunk));

        decoder.Seek(144000);
        Assert.Equal(0, decoder.Read(chunk));
        Assert.Throws<ArgumentOutOfRangeException>(() => decoder.Seek(144001));
    }

    [Fact]
    public void Read_AllocatesNothing_OnceOpen()
    {
        using var decoder = new OpusDecoder(File.ReadAllBytes(Fixture("celt-stereo-112k")));
        var chunk = new short[4800 * 2];
        var floats = new float[4800 * 2];
        decoder.Read(chunk);
        decoder.Read(floats);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20; i++)
        {
            decoder.Read(chunk);
            decoder.Read(floats);
        }
        decoder.Seek(1000);
        decoder.Read(chunk);

        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    [Fact]
    public void AFileCutShort_FailsAsBadData()
    {
        byte[] file = File.ReadAllBytes(Fixture("celt-mono-64k"));

        Assert.Throws<InvalidDataException>(() =>
        {
            using var decoder = new OpusDecoder(new UnseekableStream(file.AsSpan(0, file.Length - 100).ToArray()));
            decoder.Read(new short[200000]);
        });
    }

    [Fact]
    public void ADamagedPage_FailsItsCrc()
    {
        byte[] file = File.ReadAllBytes(Fixture("celt-mono-64k"));
        file[file.Length / 2] ^= 0x10;

        Assert.Throws<InvalidDataException>(() =>
        {
            using var decoder = new OpusDecoder(file);
            decoder.Read(new short[200000]);
        });
    }

    [Fact]
    public void AnInvalidChannelMapping_FailsAsBadData()
    {
        byte[] file = File.ReadAllBytes(Fixture("celt-mono-64k"));
        file[28 + 18] = 1; // channel mapping family 1, but no mapping table follows
        Ogg.FixCrc(file, 0);

        Assert.Throws<InvalidDataException>(() => new OpusDecoder(file));
    }

    [Fact]
    public void TheOutputGain_IsApplied()
    {
        byte[] file = File.ReadAllBytes(Fixture("silk-nb-mono"));
        short[] plain = Whole(file);
        // OpusHead output gain, Q7.8 dB: about -6 dB, about half
        short gain = (short)Math.Round(-6.0206 * 256);
        file[28 + 16] = (byte)gain;
        file[28 + 17] = (byte)(gain >> 8);
        Ogg.FixCrc(file, 0);
        double scale = Math.Pow(10, gain / (20.0 * 256));

        short[] quieter = Whole(file);

        Assert.InRange(scale, 0.499, 0.501);
        for (int i = 0; i < plain.Length; i++)
        {
            int expected = (int)Math.Round(plain[i] * scale);
            Assert.InRange(quieter[i], expected - 1, expected + 1);
        }
    }

    [Theory]
    [InlineData("celt-stereo-112k")]
    [InlineData("silk-stereo")]
    [InlineData("hybrid-stereo")]
    [InlineData("celt-60ms")]
    public void PacketDecoder_GivesTheFileDecodersSamples_AndAllocatesNothing(string name)
    {
        byte[] file = File.ReadAllBytes(Fixture(name));
        var packets = Ogg.Packets(file, out int channels, out int preSkip);
        var decoder = new OpusPacketDecoder(48000, channels);
        var pcm = new short[5760 * channels];
        var all = new List<short>();
        foreach (byte[] packet in packets)
        {
            int frames = decoder.Decode(packet, pcm);
            all.AddRange(pcm.AsSpan(0, frames * channels));
        }
        short[] whole = Whole(file);
        Assert.Equal(whole, all.GetRange(preSkip * channels, whole.Length));

        decoder.Reset();
        long before = GC.GetAllocatedBytesForCurrentThread();
        foreach (byte[] packet in packets)
        {
            decoder.Decode(packet, pcm);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void PacketDecoder_ConcealsALostPacket()
    {
        var packets = Ogg.Packets(File.ReadAllBytes(Fixture("silk-wb-mono")), out int channels, out _);
        var decoder = new OpusPacketDecoder(48000, channels);
        var pcm = new short[960 * channels];
        decoder.Decode(packets[10], pcm);

        Assert.Equal(960, decoder.Conceal(pcm));
        Assert.Equal(960, decoder.DecodeFec(packets[12], pcm));
        Assert.Throws<ArgumentException>(() => decoder.Conceal(new short[100]));
        Assert.Throws<ArgumentException>(() => decoder.Decode(packets[13], new short[10]));
    }

    internal static short[] ReadAll(AudioDecoder decoder, int chunkFrames)
    {
        var all = new List<short>();
        var chunk = new short[chunkFrames * decoder.Channels];
        int read;
        while ((read = decoder.Read(chunk)) > 0)
        {
            all.AddRange(chunk.AsSpan(0, read * decoder.Channels));
        }
        return [.. all];
    }

    internal static string Hash(short[] samples) =>
        Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(samples.AsSpan()))).ToLowerInvariant();
}
