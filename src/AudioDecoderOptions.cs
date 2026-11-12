// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using DotGame.AudioCodecs.Flac;
using DotGame.AudioCodecs.Opus;

namespace DotGame.AudioCodecs;

/// <summary>The settings of a <see cref="FlacDecoder"/> or <see cref="OpusDecoder"/> (WAV has none).</summary>
public sealed class AudioDecoderOptions
{
    /// <summary>
    /// Whether the checksums over the audio go unchecked as a file is read: each Ogg page's CRC and each
    /// FLAC frame's CRC-16. The default, <see langword="false"/>, checks them, so damage to the audio is an
    /// <see cref="System.IO.InvalidDataException"/>. Skipping them saves their time, at the cost of
    /// decoding damage the format still parses as noise instead, perhaps differently from memory, from a
    /// stream and after a seek.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The checks are cheap where the processor multiplies without carries (PCLMULQDQ on x64, PMULL on
    /// Arm64): under 1% of a decode, on a 2019 phone too. Elsewhere (WebAssembly) they're table lookups, a
    /// few percent of a FLAC decode and about 1% of an Opus one.
    /// </para>
    /// <para>
    /// The checks that find where a page or frame starts are kept: a FLAC frame header's CRC-8, and the
    /// CRC of an Ogg page found by a seek or at the file's end.
    /// </para>
    /// </remarks>
    public bool SkipChecksums { get; init; }

    internal static AudioDecoderOptions Default { get; } = new();
}
