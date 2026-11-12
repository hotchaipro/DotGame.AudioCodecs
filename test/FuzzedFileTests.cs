// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using DotGame.AudioCodecs.Flac;
using DotGame.AudioCodecs.Opus;
using DotGame.AudioCodecs.Wav;

namespace DotGame.AudioCodecs.Tests;

/// <summary>
/// The damaged files the fuzzer found (fuzz/README.md), in TestData/Fuzz, each checked as the fuzzer
/// checks it: a decoder throws only <see cref="InvalidDataException"/> for it, and otherwise gives the
/// same samples from memory and from a stream that can't seek, as <c>short</c> and as <c>float</c>, and
/// again after a seek to the start.
/// </summary>
public sealed class FuzzedFileTests
{
    public static TheoryData<string> Files()
    {
        var data = new TheoryData<string>();
        foreach (string file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "TestData", "Fuzz"), "*", SearchOption.AllDirectories))
        {
            data.Add(Path.GetRelativePath(Path.Combine(AppContext.BaseDirectory, "TestData", "Fuzz"), file));
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void ADamagedFile_IsAnErrorOrDecodesTheSameEveryWay(string name)
    {
        byte[] file = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "Fuzz", name));
        string format = Path.GetDirectoryName(name)!;
        try
        {
            short[] whole;
            using (AudioDecoder decoder = Open(format, file))
            {
                whole = OpusDecoderTests.ReadAll(decoder, 4801);
                Assert.Equal(decoder.Frames ?? (whole.Length / decoder.Channels), whole.Length / decoder.Channels);
                decoder.Seek(0);
                Assert.Equal(whole, OpusDecoderTests.ReadAll(decoder, 1000));
                if (decoder.Frames is long frames)
                {
                    decoder.Seek(frames);
                    Assert.Equal(0, decoder.Read(new short[16 * decoder.Channels]));
                }
            }
            using (AudioDecoder decoder = Open(format, file))
            {
                var floats = new List<float>();
                var chunk = new float[999 * decoder.Channels];
                int read;
                while ((read = decoder.Read(chunk)) > 0)
                {
                    floats.AddRange(chunk.AsSpan(0, read * decoder.Channels));
                }
                Assert.Equal(whole.Length, floats.Count);
            }
            using (AudioDecoder decoder = Open(format, new UnseekableStream(file)))
            {
                Assert.Equal(whole, OpusDecoderTests.ReadAll(decoder, 777));
            }
        }
        catch (InvalidDataException)
        {
            // Damaged in a way the decoders report
        }
    }

    private static AudioDecoder Open(string format, byte[] file) => format switch
    {
        "Wav" => new WavDecoder(file),
        "Flac" => new FlacDecoder(file),
        "Opus" => new OpusDecoder(file),
        _ => AudioDecoder.Open(file),
    };

    private static AudioDecoder Open(string format, Stream stream) => format switch
    {
        "Wav" => new WavDecoder(stream),
        "Flac" => new FlacDecoder(stream),
        "Opus" => new OpusDecoder(stream),
        _ => AudioDecoder.Open(stream),
    };
}
