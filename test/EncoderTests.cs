// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.IO;
using System.Security.Cryptography;
using DotGame.AudioCodecs.Flac;
using DotGame.AudioCodecs.Opus;
using DotGame.AudioCodecs.Wav;

namespace DotGame.AudioCodecs.Tests;

/// <summary>Each encoder's files, read back by the decoders: exactly for WAV and FLAC, closely for Opus.</summary>
public sealed class EncoderTests
{
    // A chord and noise in each channel, a little different in each, at 70% of full scale
    internal static float[] Signal(int channels, int frames, int sampleRate, int seed = 1)
    {
        var random = new Random(seed);
        var samples = new float[frames * channels];
        for (int f = 0; f < frames; f++)
        {
            double t = (double)f / sampleRate;
            for (int c = 0; c < channels; c++)
            {
                double v = (0.3 * Math.Sin(2 * Math.PI * (220 + (55 * c)) * t))
                    + (0.2 * Math.Sin(2 * Math.PI * 1318.5 * t))
                    + (0.15 * Math.Sin(2 * Math.PI * (3000 + (100 * c)) * t))
                    + (0.05 * ((random.NextDouble() * 2) - 1));
                samples[(f * channels) + c] = (float)v;
            }
        }
        return samples;
    }

    internal static short[] ToShorts(float[] samples)
    {
        var shorts = new short[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            shorts[i] = (short)Math.Clamp(Math.Round(samples[i] * 32768.0, MidpointRounding.ToEven), short.MinValue, short.MaxValue);
        }
        return shorts;
    }

    [Theory]
    [InlineData(WavSampleFormat.Pcm8, 1)]
    [InlineData(WavSampleFormat.Pcm16, 2)]
    [InlineData(WavSampleFormat.Pcm24, 2)]
    [InlineData(WavSampleFormat.Pcm32, 1)]
    [InlineData(WavSampleFormat.Float32, 2)]
    [InlineData(WavSampleFormat.Float64, 6)]
    [InlineData(WavSampleFormat.Pcm16, 8)]
    public void Wav_ReadsBackWhatWasWritten(WavSampleFormat format, int channels)
    {
        float[] input = Signal(channels, 12345, 44100);
        var file = new MemoryStream();
        using (var encoder = new WavEncoder(file, 44100, channels, new WavEncoderOptions { SampleFormat = format }, leaveOpen: true))
        {
            encoder.Write(input.AsSpan(0, 1000 * channels));
            encoder.Write(input.AsSpan(1000 * channels));
            encoder.Complete();
        }

        using var decoder = AudioDecoder.Open(file.ToArray());
        var wav = Assert.IsType<WavDecoder>(decoder);
        Assert.Equal(format, wav.SampleFormat);
        Assert.Equal(44100, decoder.SampleRate);
        Assert.Equal(channels, decoder.Channels);
        Assert.Equal(12345, decoder.Frames);
        var output = new float[input.Length];
        Assert.Equal(12345, decoder.Read(output));
        float step = format switch
        {
            WavSampleFormat.Pcm8 => 1f / 128,
            WavSampleFormat.Pcm16 => 1f / 32768,
            WavSampleFormat.Pcm24 => 1f / 8388608,
            _ => 1e-7f,
        };
        for (int i = 0; i < input.Length; i++)
        {
            Assert.InRange(output[i] - input[i], -step, step);
        }
    }

    [Fact]
    public void Wav_Pcm16_IsThePlain44ByteHeader_AndTheShortsAsTheyAre()
    {
        short[] input = ToShorts(Signal(2, 1000, 48000));
        var file = new MemoryStream();
        using (var encoder = new WavEncoder(file, 48000, 2))
        {
            encoder.Write(input);
            encoder.Complete();
        }
        byte[] bytes = file.ToArray();
        Assert.Equal(44 + (input.Length * 2), bytes.Length);

        using var decoder = new WavDecoder(bytes);
        var output = new short[input.Length];
        decoder.Read(output);
        Assert.Equal(input, output);
    }

    [Fact]
    public void Wav_LeftIncomplete_HasZeroSizes()
    {
        var file = new MemoryStream();
        using (var encoder = new WavEncoder(file, 48000, 1, leaveOpen: true))
        {
            encoder.Write(new short[100]);
        }
        using var decoder = new WavDecoder(file.ToArray());
        Assert.Equal(0, decoder.Frames);
    }

    [Theory]
    [InlineData(0, 16, 2)]
    [InlineData(1, 16, 2)]
    [InlineData(2, 16, 2)]
    [InlineData(3, 16, 1)]
    [InlineData(4, 16, 2)]
    [InlineData(5, 16, 2)]
    [InlineData(6, 24, 2)]
    [InlineData(7, 8, 1)]
    [InlineData(8, 16, 2)]
    [InlineData(5, 24, 6)]
    [InlineData(8, 12, 8)]
    public void Flac_DecodesToExactlyWhatWasWritten(int level, int bits, int channels)
    {
        float[] signal = Signal(channels, 50000, 48000);
        var options = new FlacEncoderOptions { CompressionLevel = level, BitsPerSample = bits, Comments = ["TITLE=Test", "ARTIST=Nobody"] };
        var file = new MemoryStream();
        using (var encoder = new FlacEncoder(file, 48000, channels, options))
        {
            encoder.Write(signal.AsSpan(0, 333 * channels));
            encoder.Write(signal.AsSpan(333 * channels));
            encoder.Complete();
        }
        byte[] bytes = file.ToArray();

        using var decoder = new FlacDecoder(bytes);
        Assert.Equal(bits, decoder.BitsPerSample);
        Assert.Equal(50000, decoder.Frames);
        Assert.Equal(["TITLE=Test", "ARTIST=Nobody"], decoder.Comments);
        var output = new float[signal.Length];
        Assert.Equal(50000, decoder.Read(output));
        float scale = 1L << (bits - 1);
        var md5Input = new byte[signal.Length * ((bits + 7) / 8)];
        for (int i = 0; i < signal.Length; i++)
        {
            int expected = (int)Math.Clamp(Math.Round(signal[i] * (double)scale, MidpointRounding.ToEven), -scale, scale - 1);
            Assert.Equal(expected, (int)(output[i] * scale));
            for (int b = 0; b < (bits + 7) / 8; b++)
            {
                md5Input[(i * ((bits + 7) / 8)) + b] = (byte)(expected >> (8 * b));
            }
        }

        // STREAMINFO's MD5 is of the samples, little-endian, in whole bytes
#pragma warning disable CA5351 // FLAC's own checksum, not security
        Assert.Equal(MD5.HashData(md5Input), bytes.AsSpan(8 + 18, 16).ToArray());
#pragma warning restore CA5351
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(8, 6)]
    [InlineData(5, 1)]
    public void Flac_Write_AllocatesNothing_OnceStarted(int level, int channels)
    {
        float[] signal = Signal(channels, 4096 * 6, 48000);
        using var encoder = new FlacEncoder(new MemoryStream(new byte[1 << 22]), 48000, channels, new FlacEncoderOptions { CompressionLevel = level });
        encoder.Write(signal.AsSpan(0, 4096 * 2 * channels));

        Allocations.AssertNone(() => encoder.Write(signal.AsSpan(4096 * 2 * channels)));
    }

    [Fact]
    public void Flac_HigherLevels_AreSmaller()
    {
        short[] input = ToShorts(Signal(2, 96000, 48000));
        long previous = long.MaxValue;
        foreach (int level in (ReadOnlySpan<int>)[0, 3, 5, 8])
        {
            var file = new MemoryStream();
            using (var encoder = new FlacEncoder(file, 48000, 2, new FlacEncoderOptions { CompressionLevel = level, Padding = 0 }, leaveOpen: true))
            {
                encoder.Write(input);
                encoder.Complete();
            }
            Assert.True(file.Length <= previous, $"level {level}: {file.Length} bytes, more than {previous}");
            previous = file.Length;
        }
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(2, false)]
    [InlineData(6, true)]
    [InlineData(8, false)]
    public void Opus_DecodesToTheInput_Closely(int channels, bool floats)
    {
        float[] signal = Signal(channels, 48000 + 123, 48000);
        var file = new MemoryStream();
        using (var encoder = new OpusEncoder(file, 48000, channels, fileOptions: new OpusFileOptions { Comments = ["TITLE=Test"] }))
        {
            if (floats)
            {
                encoder.Write(signal);
            }
            else
            {
                encoder.Write(ToShorts(signal));
            }
            encoder.Complete();
        }

        using var decoder = new OpusDecoder(file.ToArray());
        Assert.Equal(channels, decoder.Channels);
        Assert.Equal(48123, decoder.Frames);
        Assert.Equal(["TITLE=Test"], decoder.Comments);
        var output = new float[signal.Length];
        Assert.Equal(48123, decoder.Read(output));

        // Each channel comes back in its place (WAVE order in and out) and close to its input; the
        // LFE (WAVE channel 3 of 6 and more) is coded below 120 Hz only, so it isn't compared
        for (int c = 0; c < channels; c++)
        {
            if ((channels >= 6) && (c == 3))
            {
                continue;
            }
            double signalPower = 0;
            double error = 0;
            for (int f = 4800; f < 48000; f++)
            {
                double s = signal[(f * channels) + c];
                double e = output[(f * channels) + c] - s;
                signalPower += s * s;
                error += e * e;
            }
            double snr = 10 * Math.Log10(signalPower / error);
            Assert.True(snr > 6, $"channel {c}: {snr:F1} dB");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(5000)]
    public void Opus_AFileOfOnePage_DecodesToItsLength(int frames)
    {
        // One page, first and last: its granule position trims the end, below the page's samples
        var file = new MemoryStream();
        using (var encoder = new OpusEncoder(file, 48000, 2, leaveOpen: true))
        {
            encoder.Write(ToShorts(Signal(2, frames, 48000)));
            encoder.Complete();
        }

        using var decoder = new OpusDecoder(file.ToArray());
        Assert.Equal(frames, decoder.Frames);
        Assert.Equal(frames, decoder.Read(new short[(frames + 1000) * 2]));
        using var streamed = new OpusDecoder(new UnseekableStream(file.ToArray()));
        Assert.Equal(frames, streamed.Read(new short[(frames + 1000) * 2]));
    }

    [Fact]
    public void Opus_PacketEncoder_AtLibopusRates()
    {
        foreach (int rate in (ReadOnlySpan<int>)[8000, 12000, 16000, 24000, 48000])
        {
            var encoder = new OpusPacketEncoder(rate, 1, new OpusEncoderOptions { Application = OpusApplication.Voip });
            var decoder = new OpusPacketDecoder(rate, 1);
            Assert.Equal(rate / 50, encoder.FrameSize);
            short[] frame = ToShorts(Signal(1, encoder.FrameSize, rate));
            var packet = new byte[4000];
            var output = new short[encoder.FrameSize];
            int length = encoder.Encode(frame, packet);
            Assert.Equal(encoder.FrameSize, decoder.Decode(packet.AsSpan(0, length), output));
            Assert.Equal(encoder.FinalRange, decoder.FinalRange);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpusPacketEncoder(44100, 2));
        Assert.Throws<ArgumentException>(() => new OpusPacketEncoder(48000, 2).Encode(new short[100], new byte[4000]));
    }

    [Fact]
    public void Encoders_RefuseWritesAfterComplete_AndPartialFrames()
    {
        using var encoder = new WavEncoder(new MemoryStream(), 48000, 2);
        Assert.Throws<ArgumentException>(() => encoder.Write(new short[3]));
        encoder.Complete();
        Assert.Throws<InvalidOperationException>(() => encoder.Write(new short[2]));
        Assert.Throws<InvalidOperationException>(encoder.Complete);
        Assert.Throws<ArgumentException>(() => new FlacEncoder(new UnseekableStream([]), 48000, 2));
        Assert.Throws<ArgumentException>(() => new FlacEncoder(new MemoryStream(), 48000, 2, new FlacEncoderOptions { Comments = ["no equals sign"] }));
    }
}
