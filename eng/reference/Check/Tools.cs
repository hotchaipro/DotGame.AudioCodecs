// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Diagnostics;
using DotGame.AudioCodecs;
using DotGame.AudioCodecs.Flac;
using DotGame.AudioCodecs.Opus;
using DotGame.AudioCodecs.Wav;

/// <summary>
/// The checks against the reference tools, flac and ffmpeg, on the PATH: our FLAC files pass
/// <c>flac -t</c> and decode with <c>flac -d</c> to exactly the input, at sizes near flac's at each
/// level; ffmpeg reads our WAV files of every format as we do and we read its; ffmpeg decodes our Ogg
/// Opus files of every channel count without an error.
/// </summary>
internal static class Tools
{
    public static (int Checks, int Failures) Run(Action<string> report)
    {
        int checks = 0;
        int failures = 0;
        string work = Path.Combine(Path.GetTempPath(), "dotgame-audiocodecs-check");
        Directory.CreateDirectory(work);

        void Check(bool ok, string what)
        {
            checks++;
            if (!ok)
            {
                failures++;
                report("DIFFERS " + what);
            }
        }

        // FLAC: every level, three layouts, two widths
        Console.WriteLine("FLAC sizes, ours / flac's (bytes, no padding):");
        foreach (var (channels, bits) in new[] { (1, 16), (2, 16), (2, 24), (6, 16) })
        {
            float[] signal = Signal.Make(channels, 48000 * 4, 48000);
            string input = Path.Combine(work, "input.wav");
            WriteWav(input, signal, channels, bits == 24 ? WavSampleFormat.Pcm24 : WavSampleFormat.Pcm16);
            int[] expected = Quantize(signal, bits);
            var line = new List<string>();
            for (int level = 0; level <= 8; level++)
            {
                string ours = Path.Combine(work, "ours.flac");
                using (var file = File.Create(ours))
                using (var encoder = new FlacEncoder(file, 48000, channels, new FlacEncoderOptions { CompressionLevel = level, BitsPerSample = bits, Padding = 0 }))
                {
                    encoder.Write(signal);
                    encoder.Complete();
                }
                Check(RunTool("flac", "-t", "-s", ours).ExitCode == 0, $"flac -t of our level {level}, {channels} ch, {bits} bits");
                string decoded = Path.Combine(work, "decoded.wav");
                RunTool("flac", "-d", "-s", "-f", "-o", decoded, ours);
                Check(File.Exists(decoded) && ReadInts(decoded, bits).AsSpan().SequenceEqual(expected), $"flac -d of our level {level}, {channels} ch, {bits} bits");
                string reference = Path.Combine(work, "reference.flac");
                RunTool("flac", $"-{level}", "-s", "-f", "--no-padding", "-o", reference, input);
                long ourSize = new FileInfo(ours).Length;
                long theirSize = new FileInfo(reference).Length;
                line.Add($"-{level} {ourSize}/{theirSize} ({100.0 * ourSize / theirSize:F1}%)");
                if (level == 8)
                {
                    Check(ourSize <= theirSize * 1.005, $"our level 8 is {100.0 * ourSize / theirSize:F1}% of flac -8's size, {channels} ch, {bits} bits");
                }
            }
            Console.WriteLine($"  {channels} ch {bits} bits: " + string.Join(", ", line));
        }

        // WAV: ours read by ffmpeg, and ffmpeg's read by us, as 64-bit floats
        foreach (WavSampleFormat format in Enum.GetValues<WavSampleFormat>())
        {
            foreach (int channels in new[] { 1, 2, 6 })
            {
                float[] signal = Signal.Make(channels, 4410, 44100);
                string ours = Path.Combine(work, "ours.wav");
                WriteWav(ours, signal, channels, format);
                Check(SameAsFfmpeg(ours, channels), $"ffmpeg's reading of our {format} WAV, {channels} ch");

                string codec = format switch
                {
                    WavSampleFormat.Pcm8 => "pcm_u8",
                    WavSampleFormat.Pcm16 => "pcm_s16le",
                    WavSampleFormat.Pcm24 => "pcm_s24le",
                    WavSampleFormat.Pcm32 => "pcm_s32le",
                    WavSampleFormat.Float32 => "pcm_f32le",
                    _ => "pcm_f64le",
                };
                string theirs = Path.Combine(work, "theirs.wav");
                RunTool("ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", $"sine=frequency=440:sample_rate=44100:duration=0.2", "-af", "volume=0.7", "-ac", $"{channels}", "-c:a", codec, theirs);
                Check(SameAsFfmpeg(theirs, channels), $"our reading of ffmpeg's {format} WAV, {channels} ch");
            }
        }

        // Ogg Opus: ffmpeg decodes ours of every channel count, to the length we wrote
        foreach (int channels in new[] { 1, 2, 3, 4, 5, 6, 7, 8 })
        {
            string ours = Path.Combine(work, "ours.opus");
            using (var file = File.Create(ours))
            using (var encoder = new OpusEncoder(file, 48000, channels))
            {
                encoder.Write(Signal.Make(channels, 48000 + 777, 48000));
                encoder.Complete();
            }
            var (exitCode, output, errors) = RunTool("ffmpeg", "-v", "error", "-i", ours, "-f", "f32le", "-");
            Check((exitCode == 0) && (errors.Length == 0) && (output.Length == (48000 + 777) * channels * 4), $"ffmpeg's decode of our {channels}-channel Ogg Opus ({output.Length / 4 / channels} frames; {errors.Trim()})");
        }

        return (checks, failures);
    }

    private static void WriteWav(string path, float[] signal, int channels, WavSampleFormat format)
    {
        using var file = File.Create(path);
        using var encoder = new WavEncoder(file, signal.Length / channels == 4410 ? 44100 : 48000, channels, new WavEncoderOptions { SampleFormat = format });
        encoder.Write(signal);
        encoder.Complete();
    }

    private static int[] Quantize(float[] signal, int bits)
    {
        double scale = 1L << (bits - 1);
        var ints = new int[signal.Length];
        for (int i = 0; i < signal.Length; i++)
        {
            ints[i] = (int)Math.Clamp(Math.Round(signal[i] * scale, MidpointRounding.ToEven), -scale, scale - 1);
        }
        return ints;
    }

    private static int[] ReadInts(string path, int bits)
    {
        using var decoder = AudioDecoder.Open(File.ReadAllBytes(path));
        var floats = new float[decoder.Frames!.Value * decoder.Channels];
        decoder.Read(floats);
        float scale = 1L << (bits - 1);
        var ints = new int[floats.Length];
        for (int i = 0; i < floats.Length; i++)
        {
            ints[i] = (int)(floats[i] * scale);
        }
        return ints;
    }

    // Our float decode against ffmpeg's 64-bit float decode, rounded to float
    private static bool SameAsFfmpeg(string path, int channels)
    {
        var (exitCode, output, _) = RunTool("ffmpeg", "-v", "error", "-i", path, "-f", "f64le", "-");
        if (exitCode != 0)
        {
            return false;
        }
        using var decoder = AudioDecoder.Open(File.ReadAllBytes(path));
        if (decoder.Channels != channels)
        {
            return false;
        }
        var ours = new float[decoder.Frames!.Value * channels];
        decoder.Read(ours);
        ReadOnlySpan<double> theirs = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, double>(output);
        if (theirs.Length != ours.Length)
        {
            return false;
        }
        for (int i = 0; i < ours.Length; i++)
        {
            if (ours[i] != (float)theirs[i])
            {
                return false;
            }
        }
        return true;
    }

    private static (int ExitCode, byte[] Output, string Errors) RunTool(string tool, params string[] arguments)
    {
        var start = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)!;
        var output = new MemoryStream();
        Task copy = process.StandardOutput.BaseStream.CopyToAsync(output);
        string errors = process.StandardError.ReadToEnd();
        copy.Wait();
        process.WaitForExit();
        return (process.ExitCode, output.ToArray(), errors);
    }
}
