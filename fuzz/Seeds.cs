// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using DotGame.AudioCodecs.Flac;
using DotGame.AudioCodecs.Opus;
using DotGame.AudioCodecs.Wav;

namespace DotGame.AudioCodecs.Fuzz;

/// <summary>
/// The inputs fuzzing starts from: the tests' files, and short files the encoders write in each of their
/// modes, with lengths that aren't multiples of the blocks and frames.
/// </summary>
internal static class Seeds
{
    public static List<byte[]> For(Format format, string testData)
    {
        var seeds = new List<byte[]>();
        if (format == Format.Packet)
        {
            return Packets(testData);
        }
        foreach (string name in format == Format.Any ? new[] { "Wav", "Flac", "Opus" } : new[] { format.ToString() })
        {
            string folder = Path.Combine(testData, name);
            if (Directory.Exists(folder))
            {
                foreach (string file in Directory.GetFiles(folder))
                {
                    byte[] bytes = File.ReadAllBytes(file);
                    if ((bytes.Length <= 64 << 10) && (Path.GetExtension(file) is not ".pcm"))
                    {
                        seeds.Add(bytes);
                    }
                }
            }
            seeds.AddRange(Encoded(name));
        }
        return seeds;
    }

    private static IEnumerable<byte[]> Encoded(string format)
    {
        foreach ((int channels, int frames) in new[] { (1, 1), (1, 777), (2, 2500), (6, 1200), (8, 300) })
        {
            float[] signal = Signal(channels, frames);
            if (format == "Wav")
            {
                foreach (WavSampleFormat sample in Enum.GetValues<WavSampleFormat>())
                {
                    yield return Encode(s => new WavEncoder(s, 44100, channels, new WavEncoderOptions { SampleFormat = sample }, leaveOpen: true), signal);
                }
            }
            else if (format == "Flac")
            {
                foreach ((int level, int bits) in new[] { (0, 16), (5, 24), (8, 8), (3, 12) })
                {
                    yield return Encode(s => new FlacEncoder(s, 22050, channels, new FlacEncoderOptions { CompressionLevel = level, BitsPerSample = bits, BlockSize = 192, Padding = 0, Comments = ["A=b"] }, leaveOpen: true), signal);
                }
            }
            else
            {
                foreach (OpusEncoderOptions options in new OpusEncoderOptions[]
                {
                    new(),
                    new() { Bitrate = 8000, Application = OpusApplication.Voip },
                    new() { FrameDuration = OpusFrameDuration.Ms2_5, Application = OpusApplication.RestrictedLowDelay },
                    new() { FrameDuration = OpusFrameDuration.Ms60, Bitrate = 24000 },
                })
                {
                    yield return Encode(s => new OpusEncoder(s, 48000, channels, options, new OpusFileOptions { MaxPageDelay = TimeSpan.FromMilliseconds(40) }, leaveOpen: true), signal);
                }
            }
        }
    }

    // Raw packets for the packet target: each a two-byte length, then the bytes
    private static List<byte[]> Packets(string testData)
    {
        var seeds = new List<byte[]>();
        foreach (byte[] file in For(Format.Opus, testData))
        {
            var output = new List<byte>();
            int at = 0;
            var packet = new List<byte>();
            int count = 0;
            while ((at + 27 <= file.Length) && (count < 40))
            {
                int segments = file[at + 26];
                int body = at + 27 + segments;
                for (int s = 0; (s < segments) && (body <= file.Length); s++)
                {
                    int lace = file[at + 27 + s];
                    packet.AddRange(file.AsSpan(body, Math.Min(lace, file.Length - body)));
                    body += lace;
                    if (lace < 255)
                    {
                        if (count++ >= 2)
                        {
                            output.Add((byte)(packet.Count >> 8));
                            output.Add((byte)packet.Count);
                            output.AddRange(packet);
                        }
                        packet.Clear();
                    }
                }
                at = body;
            }
            if (output.Count > 0)
            {
                seeds.Add([.. output]);
            }
        }
        return seeds;
    }

    private static byte[] Encode(Func<Stream, AudioEncoder> create, float[] signal)
    {
        var stream = new MemoryStream();
        using (AudioEncoder encoder = create(stream))
        {
            encoder.Write(signal);
            encoder.Complete();
        }
        return stream.ToArray();
    }

    private static float[] Signal(int channels, int frames)
    {
        var samples = new float[frames * channels];
        for (int f = 0; f < frames; f++)
        {
            for (int c = 0; c < channels; c++)
            {
                samples[(f * channels) + c] = (float)(0.6 * Math.Sin(f * (0.05 + (0.01 * c))));
            }
        }
        return samples;
    }
}
