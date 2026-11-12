// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;

namespace DotGame.AudioCodecs.Flac;

/// <summary>
/// The settings of a <see cref="FlacEncoder"/>: flac's, with its defaults, so setting nothing writes a
/// file laid out as <c>flac</c> writes one with no options.
/// </summary>
public sealed class FlacEncoderOptions
{
    /// <summary>
    /// How hard the encoder tries, 0 (fastest) to 8 (smallest), as flac's <c>-0</c> to <c>-8</c>; the
    /// default is 5. 0 to 2 use 1,152-sample blocks and fixed predictors only; 3 to 8 use 4,096-sample
    /// blocks and linear prediction of up to order 6, 8 or 12. Stereo is coded as left and right (0, 3),
    /// as an estimate favors (1, 4), or as the best of the four ways (2, 5 to 8). 6 to 8 also fit the
    /// prediction to halves (and at 8, thirds) of a block, as flac's <c>subdivide_tukey</c> does.
    /// </summary>
    public int CompressionLevel { get; init; } = 5;

    /// <summary>
    /// The bits of each sample, 4 to 24; the default is 16. <c>float</c> samples are rounded to it and
    /// clipped at ±1; <c>short</c> ones are widened or rounded to it.
    /// </summary>
    public int BitsPerSample { get; init; } = 16;

    /// <summary>The samples of each block (flac's <c>-b</c>), 16 to 65,535; <see langword="null"/> is the level's.</summary>
    public int? BlockSize { get; init; }

    /// <summary>The highest linear prediction order (flac's <c>-l</c>), 0 (fixed predictors only) to 32; <see langword="null"/> is the level's.</summary>
    public int? MaxLpcOrder { get; init; }

    /// <summary>Whether every prediction order is tried, not only the two the errors favor (flac's <c>-e</c>).</summary>
    public bool ExhaustiveModelSearch { get; init; }

    /// <summary>
    /// Whether STREAMINFO holds the samples' MD5, as flac writes it; the default is yes.
    /// <see langword="false"/> leaves it zero, which RFC 9639 allows.
    /// </summary>
    public bool Md5 { get; init; } = true;

    /// <summary>The bytes of a PADDING block, room for later tags (flac's <c>--padding</c>); the default is 8,192, 0 for none.</summary>
    public int Padding { get; init; } = 8192;

    /// <summary>Comments, each "KEY=value" (flac's <c>-T</c>), in a VORBIS_COMMENT block with the vendor string.</summary>
    public IReadOnlyList<string> Comments { get; init; } = [];

    internal static FlacEncoderOptions Default { get; } = new();

    /// <summary>The frame encoder's settings for these options.</summary>
    internal FlacFrameEncoder.Settings ToSettings(int channels)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(this.CompressionLevel, nameof(this.CompressionLevel));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(this.CompressionLevel, 8, nameof(this.CompressionLevel));
        ArgumentOutOfRangeException.ThrowIfLessThan(this.BitsPerSample, 4, nameof(this.BitsPerSample));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(this.BitsPerSample, 24, nameof(this.BitsPerSample));
        if (this.BlockSize is int size)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(size, 16, nameof(this.BlockSize));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(size, 65535, nameof(this.BlockSize));
        }
        if (this.MaxLpcOrder is int order)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(order, nameof(this.MaxLpcOrder));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(order, 32, nameof(this.MaxLpcOrder));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(this.Padding, nameof(this.Padding));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(this.Padding, (1 << 24) - 1, nameof(this.Padding));
        ArgumentNullException.ThrowIfNull(this.Comments, nameof(this.Comments));
        Comment.Validate(this.Comments, nameof(this.Comments));

        // flac 1.5's presets: block size, LPC order, the highest partition order (-r), the stereo
        // search (-m, -M) and the apodization (-A: tukey(0.5), subdivide_tukey(2) or (3))
        (int blockSize, int lpc, int partitions, FlacFrameEncoder.StereoMode stereo, int subdivisions) = this.CompressionLevel switch
        {
            0 => (1152, 0, 3, FlacFrameEncoder.StereoMode.Independent, 1),
            1 => (1152, 0, 3, FlacFrameEncoder.StereoMode.Adaptive, 1),
            2 => (1152, 0, 3, FlacFrameEncoder.StereoMode.Exhaustive, 1),
            3 => (4096, 6, 4, FlacFrameEncoder.StereoMode.Independent, 1),
            4 => (4096, 8, 4, FlacFrameEncoder.StereoMode.Adaptive, 1),
            5 => (4096, 8, 5, FlacFrameEncoder.StereoMode.Exhaustive, 1),
            6 => (4096, 8, 6, FlacFrameEncoder.StereoMode.Exhaustive, 2),
            7 => (4096, 12, 6, FlacFrameEncoder.StereoMode.Exhaustive, 2),
            _ => (4096, 12, 6, FlacFrameEncoder.StereoMode.Exhaustive, 3),
        };
        if (channels != 2)
        {
            stereo = FlacFrameEncoder.StereoMode.Independent;
        }
        return new FlacFrameEncoder.Settings(this.BlockSize ?? blockSize, this.MaxLpcOrder ?? lpc, partitions, stereo,
            this.ExhaustiveModelSearch, this.BitsPerSample, subdivisions);
    }
}
