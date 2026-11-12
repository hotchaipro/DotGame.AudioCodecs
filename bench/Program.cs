// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using DotGame.AudioCodecs;
using DotGame.AudioCodecs.Flac;
using DotGame.AudioCodecs.Opus;
using DotGame.AudioCodecs.Wav;

// DotGame.AudioCodecs's side of the benchmarks (README.md), as native/audiobench.c times the C libraries:
//
//   DotGame.AudioCodecs.Bench opus-decode|flac-decode file   to 16-bit interleaved samples
//   DotGame.AudioCodecs.Bench opus-encode|flac-encode file.wav   with the default options (opusenc's, flac -5)
//
// Prints "<median ms> <output bytes>": the median of at least 15 runs and 2 seconds, after two to warm up.
if (args.Length < 2)
{
    Console.Error.WriteLine("usage: DotGame.AudioCodecs.Bench opus-decode|opus-encode|flac-decode|flac-encode file");
    return 1;
}

byte[] file = File.ReadAllBytes(args[1]);
Func<long> run = args[0].EndsWith("-decode", StringComparison.Ordinal) ? Decode(file) : Encode(file, args[0] == "opus-encode");
run();
run();
var times = new List<double>();
long bytes = 0;
var total = Stopwatch.StartNew();
while ((times.Count < 15) || (total.Elapsed.TotalSeconds < 2))
{
    long start = Stopwatch.GetTimestamp();
    bytes = run();
    times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
}
times.Sort();
Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{times[times.Count / 2]:F3} {bytes}"));
return 0;

static Func<long> Decode(byte[] file)
{
    short[] samples;
    using (AudioDecoder probe = AudioDecoder.Open(file))
    {
        samples = new short[probe.Frames!.Value * probe.Channels];
    }
    return () =>
    {
        using AudioDecoder decoder = AudioDecoder.Open(file);
        return decoder.Read(samples) * decoder.Channels * sizeof(short);
    };
}

static Func<long> Encode(byte[] file, bool opus)
{
    short[] pcm;
    int rate;
    int channels;
    using (var wav = new WavDecoder(file))
    {
        rate = wav.SampleRate;
        channels = wav.Channels;
        pcm = new short[wav.Frames!.Value * channels];
        wav.Read(pcm);
    }
    var output = new MemoryStream(file.Length);
    return () =>
    {
        output.SetLength(0);
        using AudioEncoder encoder = opus
            ? new OpusEncoder(output, rate, channels, leaveOpen: true)
            : new FlacEncoder(output, rate, channels, leaveOpen: true);
        encoder.Write(pcm);
        encoder.Complete();
        return output.Length;
    };
}
