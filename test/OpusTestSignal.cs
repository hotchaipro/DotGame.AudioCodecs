// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;

namespace DotGame.AudioCodecs.Tests;

/// <summary>
/// Test signals for the Opus encoder, 48 kHz interleaved float, from additions and multiplications
/// alone (oscillators by recurrence, noise from a linear congruential generator), so they're the same
/// bits on every machine: a hash of the encoder's output can be checked anywhere.
/// </summary>
internal static class OpusTestSignal
{
    /// <summary>
    /// <paramref name="kind"/> is "music" (chords with a beat and noise), "speech" (a voiced pulse train
    /// through two resonances, in syllables), "transients" (clicks and bursts) or "silence".
    /// </summary>
    public static float[] Make(string kind, int channels, int seconds)
    {
        int frames = 48000 * seconds;
        var pcm = new float[frames * channels];
        uint seed = 0x2545F491;
        float Noise()
        {
            seed = (seed * 1664525) + 1013904223;
            return (int)seed * (1f / 2147483648f);
        }

        switch (kind)
        {
            case "music":
                {
                    // Three oscillators y[n] = 2c y[n-1] - y[n-2]: c = cos(2 pi f / 48000) for 220, 277.18
                    // and 329.63 Hz, written as constants
                    float[] c = [0.99979f, 0.99967f, 0.99953f];
                    float[] y1 = [0f, 0f, 0f];
                    float[] y2 = [-0.0288f, -0.0363f, -0.0431f];
                    for (int i = 0; i < frames; i++)
                    {
                        float sum = 0;
                        for (int k = 0; k < 3; k++)
                        {
                            float y = (2 * c[k] * y1[k]) - y2[k];
                            y2[k] = y1[k];
                            y1[k] = y;
                            sum += y;
                        }

                        // A beat every half second: a decaying noise burst
                        int t = i % 24000;
                        float beat = t < 4800 ? Noise() * (1f - (t / 4800f)) : 0f;
                        float left = (0.25f * sum) + (0.3f * beat) + (0.01f * Noise());
                        pcm[i * channels] = left;
                        if (channels == 2)
                        {
                            pcm[(i * channels) + 1] = (0.2f * sum) - (0.25f * beat) + (0.01f * Noise());
                        }
                    }
                    break;
                }
            case "speech":
                {
                    // A glottal pulse train whose period glides between 100 and 160 Hz, through two
                    // resonators (formants near 700 and 1200 Hz), in 300 ms syllables with 100 ms gaps
                    float a1 = 1.9494f, b1 = -0.9604f;
                    float a2 = 1.8697f, b2 = -0.9604f;
                    float z1 = 0, z2 = 0, w1 = 0, w2 = 0;
                    float period = 400;
                    float phase = 0;
                    for (int i = 0; i < frames; i++)
                    {
                        int t = i % 19200;
                        bool voiced = t < 14400;
                        period += (t < 7200) ? 0.01f : -0.01f;
                        phase += 1;
                        float pulse = 0;
                        if (phase >= period)
                        {
                            phase -= period;
                            pulse = voiced ? 1f : 0f;
                        }
                        float excitation = pulse + ((voiced ? 0.002f : 0.02f) * Noise());
                        float f1 = excitation + (a1 * z1) + (b1 * z2);
                        z2 = z1;
                        z1 = f1;
                        float f2 = f1 + (a2 * w1) + (b2 * w2);
                        w2 = w1;
                        w1 = f2;
                        float sample = 0.004f * f2;
                        for (int ch = 0; ch < channels; ch++)
                        {
                            pcm[(i * channels) + ch] = ch == 0 ? sample : 0.8f * sample;
                        }
                    }
                    break;
                }
            case "transients":
                {
                    for (int i = 0; i < frames; i++)
                    {
                        int t = i % 9600;
                        float click = t < 48 ? 0.9f * (1f - (t / 48f)) : 0f;
                        float burst = (i % 33600) is >= 20000 and < 22400 ? 0.5f * Noise() : 0f;
                        for (int ch = 0; ch < channels; ch++)
                        {
                            pcm[(i * channels) + ch] = click + burst + (0.0005f * Noise());
                        }
                    }
                    break;
                }
            case "silence":
                break;
            default:
                throw new ArgumentException($"No test signal '{kind}'.", nameof(kind));
        }
        return pcm;
    }
}
