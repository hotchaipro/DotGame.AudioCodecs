// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

namespace DotGame.AudioCodecs.Wav;

/// <summary>How a WAV file stores its samples.</summary>
public enum WavSampleFormat
{
    /// <summary>8-bit integers, unsigned (as WAV stores 8 bits).</summary>
    Pcm8,

    /// <summary>16-bit integers.</summary>
    Pcm16,

    /// <summary>24-bit integers.</summary>
    Pcm24,

    /// <summary>32-bit integers.</summary>
    Pcm32,

#pragma warning disable CA1720 // The formats' own names

    /// <summary>32-bit IEEE floats, -1 to 1.</summary>
    Float32,

    /// <summary>64-bit IEEE floats, -1 to 1.</summary>
    Float64,
#pragma warning restore CA1720
}

/// <summary>The settings of a <see cref="WavEncoder"/>.</summary>
public sealed class WavEncoderOptions
{
    /// <summary>How samples are stored. The default is 16-bit integers.</summary>
    public WavSampleFormat SampleFormat { get; init; } = WavSampleFormat.Pcm16;
}
