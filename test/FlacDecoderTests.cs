// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.IO;
using System.Runtime.InteropServices;
using DotGame.AudioCodecs.Flac;

namespace DotGame.AudioCodecs.Tests;

/// <summary>
/// The FLAC decoder against ffmpeg's: each file in TestData/Flac has the 16-bit PCM ffmpeg decodes,
/// covering every stereo decorrelation, the fixed predictors, linear prediction of orders 8, 12 and 32,
/// 24-bit samples, silence and a block size that isn't a power of two, from synthesized sources
/// (FixtureSources).
/// </summary>
public sealed class FlacDecoderTests
{
    public static TheoryData<string, int, int> Fixtures => new()
    {
        { "stereo-indep", 44100, 2 },
        { "stereo-left_side", 44100, 2 },
        { "stereo-right_side", 44100, 2 },
        { "stereo-mid_side", 44100, 2 },
        { "stereo-silence", 44100, 2 },
        { "stereo-oddblock", 44100, 2 },
        { "mono-fixed", 22050, 1 },
        { "mono-24bit", 48000, 1 },
        { "stereo-lpc32", 48000, 2 },
        { "stereo-lpc12", 48000, 2 },
        { "mono-lpc8", 48000, 1 },
    };

    internal static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "TestData", "Flac", name);

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Decodes_ToTheSamplesFfmpegDecodes(string name, int sampleRate, int channels)
    {
        byte[] file = File.ReadAllBytes(Fixture(name + ".flac"));
        byte[] expected = File.ReadAllBytes(Fixture(name + ".pcm"));
        int frames = expected.Length / 2 / channels;

        using var decoder = AudioDecoder.Open(file);
        Assert.IsType<FlacDecoder>(decoder);
        Assert.Equal(sampleRate, decoder.SampleRate);
        Assert.Equal(channels, decoder.Channels);
        Assert.Equal(frames, decoder.Frames);
        var samples = new short[frames * channels];
        Assert.Equal(frames, decoder.Read(samples));
        Assert.Equal(0, decoder.Read(samples));
        Assert.Equal(expected, MemoryMarshal.AsBytes(samples.AsSpan()).ToArray());

        using var streamed = new FlacDecoder(new UnseekableStream(file));
        Assert.Equal(samples, OpusDecoderTests.ReadAll(streamed, 1000));
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Reads_InChunks_AndSeeks_ToExactlyTheSamplesOfOneRead(string name, int sampleRate, int channels)
    {
        _ = sampleRate;
        byte[] file = File.ReadAllBytes(Fixture(name + ".flac"));
        short[] whole;
        using (var decoder = new FlacDecoder(file))
        {
            whole = OpusDecoderTests.ReadAll(decoder, 1 << 20);
        }
        foreach (int chunk in (ReadOnlySpan<int>)[1, 7, 1000, 4096, 4097])
        {
            using var decoder = new FlacDecoder(new MemoryStream(file));
            Assert.Equal(whole, OpusDecoderTests.ReadAll(decoder, chunk));
        }

        int frames = whole.Length / channels;
        using var seeking = new FlacDecoder(file);
        foreach (int frame in (ReadOnlySpan<int>)[frames - 1, 0, frames / 3, 4096, 4095, frames / 2, 1])
        {
            seeking.Seek(frame);
            var chunk = new short[Math.Min(500, frames - frame) * channels];
            Assert.Equal(chunk.Length / channels, seeking.Read(chunk));
            Assert.Equal(whole.AsSpan(frame * channels, chunk.Length).ToArray(), chunk);
        }
    }

    [Fact]
    public void Float_IsExact_For24Bits()
    {
        using var decoder = new FlacDecoder(File.ReadAllBytes(Fixture("mono-24bit.flac")));
        Assert.Equal(24, decoder.BitsPerSample);
        var floats = new float[(int)decoder.Frames!.Value];
        decoder.Read(floats);
        foreach (float sample in floats)
        {
            float scaled = sample * 8388608f;
            Assert.Equal(MathF.Round(scaled), scaled);
        }
    }

    [Fact]
    public void ATruncatedFile_FailsAsBadData()
    {
        byte[] file = File.ReadAllBytes(Fixture("stereo-indep.flac"));

        Assert.Throws<InvalidDataException>(() =>
        {
            using var decoder = new FlacDecoder(file.AsMemory(0, file.Length / 2));
            decoder.Read(new short[1 << 20]);
        });
        Assert.Throws<InvalidDataException>(() => new FlacDecoder(file.AsMemory(0, 20)));
        Assert.Throws<InvalidDataException>(() => new FlacDecoder("RIFF\0\0\0\0WAVE"u8.ToArray()));
    }

    [Fact]
    public void ADamagedFrame_FailsItsCrc()
    {
        byte[] file = File.ReadAllBytes(Fixture("stereo-lpc12.flac"));
        file[file.Length / 2] ^= 0x04;

        Assert.Throws<InvalidDataException>(() =>
        {
            using var decoder = new FlacDecoder(file);
            decoder.Read(new short[1 << 20]);
        });
    }

    [Fact]
    public void Open_TellsTheFormatsApart()
    {
        Assert.Throws<InvalidDataException>(() => AudioDecoder.Open("fLa"u8.ToArray()));
        Assert.Throws<InvalidDataException>(() => AudioDecoder.Open("ID3 and an MP3, which isn't supported"u8.ToArray()));
        Assert.IsType<FlacDecoder>(AudioDecoder.Open(File.ReadAllBytes(Fixture("mono-fixed.flac"))));
    }
}
