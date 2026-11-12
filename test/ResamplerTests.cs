// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using DotGame.AudioCodecs.Opus;

namespace DotGame.AudioCodecs.Tests;

/// <summary>The streaming resampler, and the Ogg Opus encoder that resamples rates libopus doesn't code.</summary>
public sealed class ResamplerTests
{
    private static float[] Resample(float[] input, int channels, int from, int to, int piece)
    {
        var output = new List<float>();
        var resampler = new Resampler(channels, from, to);
        for (int at = 0; at < input.Length; at += piece * channels)
        {
            resampler.Write(input.AsSpan(at, Math.Min(piece * channels, input.Length - at)), frames => output.AddRange(frames));
        }
        resampler.Flush(frames => output.AddRange(frames));
        return [.. output];
    }

    [Theory]
    [InlineData(44100, 48000, 2)]
    [InlineData(48000, 44100, 1)]
    [InlineData(22050, 48000, 1)]
    [InlineData(96000, 48000, 2)]
    [InlineData(37000, 48000, 3)]
    public void PiecesOfAnySize_GiveTheSameOutput_OfTheRightLength(int from, int to, int channels)
    {
        float[] input = EncoderTests.Signal(channels, from / 3, from);
        float[] whole = Resample(input, channels, from, to, input.Length / channels);

        Assert.Equal(((((long)input.Length / channels) * to) + (from / 2)) / from, whole.Length / channels);
        foreach (int piece in (ReadOnlySpan<int>)[1, 7, 100, 4097])
        {
            Assert.Equal(whole, Resample(input, channels, from, to, piece));
        }
    }

    [Fact]
    public void AConstant_StaysConstant()
    {
        var input = new float[44100];
        input.AsSpan().Fill(0.5f);
        float[] output = Resample(input, 1, 44100, 48000, 1000);

        // Away from the ends, where the silence before and after is in the filter
        for (int i = 100; i < output.Length - 100; i++)
        {
            Assert.InRange(output[i], 0.5f - 1e-6f, 0.5f + 1e-6f);
        }
    }

    [Theory]
    [InlineData(44100, 2, true)]
    [InlineData(44100, 1, false)]
    [InlineData(22050, 2, true)]
    [InlineData(11025, 1, true)]
    [InlineData(32000, 6, false)]
    [InlineData(96000, 2, true)]
    public void Opus_AtOtherRates_IsResampledTo48kHz(int rate, int channels, bool floats)
    {
        // A tone, which resampled is the same tone at 48 kHz
        int frames = rate + 4321;
        var signal = new float[frames * channels];
        for (int f = 0; f < frames; f++)
        {
            for (int c = 0; c < channels; c++)
            {
                signal[(f * channels) + c] = (float)(0.5 * Math.Sin(2 * Math.PI * (440 + (110 * c)) * f / rate));
            }
        }
        var file = new MemoryStream();
        using (var encoder = new OpusEncoder(file, rate, channels, leaveOpen: true))
        {
            if (floats)
            {
                encoder.Write(signal);
            }
            else
            {
                encoder.Write(EncoderTests.ToShorts(signal));
            }
            encoder.Complete();
        }

        using var decoder = new OpusDecoder(file.ToArray());
        long expected = (((long)frames * 48000) + (rate / 2)) / rate;
        Assert.Equal(expected, decoder.Frames);
        var output = new float[expected * channels];
        Assert.Equal(expected, decoder.Read(output));
        for (int c = 0; c < channels; c++)
        {
            if ((channels >= 6) && (c == 3))
            {
                continue;
            }
            double power = 0;
            double error = 0;
            for (int f = 4800; f < expected - 4800; f++)
            {
                double s = 0.5 * Math.Sin(2 * Math.PI * (440 + (110 * c)) * f / 48000.0);
                double e = output[(f * channels) + c] - s;
                power += s * s;
                error += e * e;
            }
            double snr = 10 * Math.Log10(power / error);
            Assert.True(snr > 15, $"{rate} Hz, channel {c}: {snr:F1} dB");
        }
    }
}
