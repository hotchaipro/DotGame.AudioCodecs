// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Buffers.Binary;

/// <summary>The checks' input: test signals made with arithmetic alone, so they're the same everywhere.</summary>
internal static class Signal
{
    /// <summary>
    /// Music-like tones, a sweep, speech-like bursts and a little noise, different in each channel, at up
    /// to 70% of full scale.
    /// </summary>
    public static float[] Make(int channels, int frames, int rate)
    {
        var samples = new float[frames * channels];
        uint noise = 22222;
        for (int f = 0; f < frames; f++)
        {
            double t = (double)f / rate;
            double burst = (Math.Sin(2 * Math.PI * 3 * t) > 0.2) ? 1 : 0.1;
            for (int c = 0; c < channels; c++)
            {
                noise = (noise * 1664525) + 1013904223;
                double white = ((noise >> 8) / 8388608.0) - 1;
                double sweep = Math.Sin(2 * Math.PI * (200 + (c * 30)) * t * (1 + t));
                double v = (0.25 * Math.Sin(2 * Math.PI * (220 + (55 * c)) * t))
                    + (0.15 * sweep)
                    + (0.15 * burst * Math.Sin(2 * Math.PI * (1500 + (100 * c)) * t))
                    + (0.05 * white);
                samples[(f * channels) + c] = (float)v;
            }
        }
        return samples;
    }

    public static short[] ToShorts(float[] samples)
    {
        var shorts = new short[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            shorts[i] = (short)Math.Clamp(Math.Round(samples[i] * 32768.0, MidpointRounding.ToEven), short.MinValue, short.MaxValue);
        }
        return shorts;
    }

    /// <summary>WAVE order to Opus's Vorbis order: WAVE channel i is Opus channel [i].</summary>
    public static byte[] OpusOrder(int channels) => channels switch
    {
        1 => [0],
        2 => [0, 1],
        3 => [0, 2, 1],
        4 => [0, 1, 2, 3],
        5 => [0, 2, 1, 3, 4],
        6 => [0, 2, 1, 5, 3, 4],
        7 => [0, 2, 1, 6, 5, 3, 4],
        _ => [0, 2, 1, 7, 5, 6, 3, 4],
    };
}

/// <summary>Ogg pages read without the library: the OpusHead packet and the audio packets.</summary>
internal static class Ogg
{
    public static (byte[] Head, List<byte[]> Packets) Read(byte[] file)
    {
        var packets = new List<byte[]>();
        var packet = new List<byte>();
        int offset = 0;
        while (offset + 27 <= file.Length)
        {
            if (BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(offset)) != 0x4F676753)
            {
                throw new InvalidDataException($"No Ogg page at {offset}.");
            }
            int segments = file[offset + 26];
            int body = offset + 27 + segments;
            for (int s = 0; s < segments; s++)
            {
                int lace = file[offset + 27 + s];
                packet.AddRange(file.AsSpan(body, lace));
                body += lace;
                if (lace < 255)
                {
                    packets.Add([.. packet]);
                    packet.Clear();
                }
            }
            offset = body;
        }
        byte[] head = packets[0];
        packets.RemoveRange(0, 2);
        return (head, packets);
    }
}
