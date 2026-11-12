// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;

namespace DotGame.AudioCodecs.Opus;

/// <summary>
/// The settings of an <see cref="OpusEncoder"/>: libopus's,
/// with opusenc's defaults, so setting nothing encodes as <c>opusenc</c> does with no options.
/// </summary>
public sealed class OpusEncoderOptions
{
    /// <summary>
    /// The target bitrate in bits a second (opusenc's <c>--bitrate</c>, in kbit/s there): 500 to 750,000
    /// a channel. <see langword="null"/> is opusenc's default: 64 kbit/s for each Opus stream and 32 more
    /// for each coupled (stereo) one, so 64 for mono, 96 for stereo and 320 for 5.1 at 48 kHz, scaled
    /// down by (rate + 16 kHz) / 64 kHz for inputs below 44.1 kHz.
    /// </summary>
    public int? Bitrate { get; init; }

    /// <summary>How the bitrate is held (opusenc's <c>--vbr</c>, <c>--cvbr</c>, <c>--hard-cbr</c>). The default is VBR.</summary>
    public OpusBitrateMode BitrateMode { get; init; } = OpusBitrateMode.Vbr;

    /// <summary>The encoder's effort, 0 (fastest) to 10 (best; the default), as opusenc's <c>--comp</c>.</summary>
    public int Complexity { get; init; } = 10;

    /// <summary>
    /// The duration of each packet (opusenc's <c>--framesize</c>). The default is 20 ms. Packets of 40 ms
    /// or more are several frames each.
    /// </summary>
    public OpusFrameDuration FrameDuration { get; init; } = OpusFrameDuration.Ms20;

    /// <summary>What the encoder is tuned for. The default is general audio, as opusenc's.</summary>
    public OpusApplication Application { get; init; } = OpusApplication.Audio;

    /// <summary>A hint of what the signal is (opusenc's <c>--music</c>, <c>--speech</c>). The default is to detect it.</summary>
    public OpusSignal Signal { get; init; } = OpusSignal.Auto;

    /// <summary>The widest audio bandwidth coded. The default, fullband, lets the encoder choose by bitrate.</summary>
    public OpusBandwidth MaxBandwidth { get; init; } = OpusBandwidth.Fullband;

    /// <summary>The packet loss to expect, 0 to 100 percent (opusenc's <c>--expect-loss</c>). The default is 0.</summary>
    public int ExpectedPacketLoss { get; init; }

    /// <summary>Whether packets carry forward error correction for the one before (SILK and hybrid modes, with <see cref="ExpectedPacketLoss"/>).</summary>
    public bool InbandFec { get; init; }

    /// <summary>Whether silence is sent as rare, tiny packets (discontinuous transmission).</summary>
    public bool Dtx { get; init; }

    /// <summary>
    /// Whether stereo may use phase inversion, which codes stereo better but sounds worse downmixed to
    /// mono. The default is yes; opusenc's <c>--no-phase-inv</c> is <see langword="false"/>.
    /// </summary>
    public bool PhaseInversion { get; init; } = true;

    internal static OpusEncoderOptions Default { get; } = new();

    /// <summary>Throws for a setting out of range, naming it.</summary>
    internal void Validate(int channels, string paramName)
    {
        if (this.Bitrate is int bitrate && ((bitrate < 500) || (bitrate > 750_000L * channels)))
        {
            throw new ArgumentOutOfRangeException(paramName, bitrate, $"The Opus bitrate is 500 to {750_000 * channels} bits a second for {channels} channels.");
        }
        if (!Enum.IsDefined(this.BitrateMode))
        {
            throw new ArgumentOutOfRangeException(paramName, this.BitrateMode, "Not an Opus bitrate mode.");
        }
        ArgumentOutOfRangeException.ThrowIfNegative(this.Complexity, paramName);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(this.Complexity, 10, paramName);
        if (!Enum.IsDefined(this.FrameDuration))
        {
            throw new ArgumentOutOfRangeException(paramName, this.FrameDuration, "Not an Opus frame duration.");
        }
        if (!Enum.IsDefined(this.Application))
        {
            throw new ArgumentOutOfRangeException(paramName, this.Application, "Not an Opus application.");
        }
        if (!Enum.IsDefined(this.Signal))
        {
            throw new ArgumentOutOfRangeException(paramName, this.Signal, "Not an Opus signal type.");
        }
        if (!Enum.IsDefined(this.MaxBandwidth))
        {
            throw new ArgumentOutOfRangeException(paramName, this.MaxBandwidth, "Not an Opus bandwidth.");
        }
        ArgumentOutOfRangeException.ThrowIfNegative(this.ExpectedPacketLoss, paramName);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(this.ExpectedPacketLoss, 100, paramName);
        if ((this.FrameDuration < OpusFrameDuration.Ms10) && (this.Application == OpusApplication.RestrictedSilk))
        {
            throw new ArgumentException("SILK needs frames of 10 ms or more.", paramName);
        }
    }

    /// <summary>
    /// The bitrate: <see cref="Bitrate"/>, or opusenc's default for <paramref name="streams"/> Opus streams,
    /// <paramref name="coupled"/> of them stereo, from input at <paramref name="sampleRate"/>.
    /// </summary>
    internal int BitrateFor(int streams, int coupled, int sampleRate, int channels)
    {
        if (this.Bitrate is int bitrate)
        {
            return bitrate;
        }
        int rate = sampleRate < 44100 ? sampleRate : 48000;
        int kilohertz = Math.Min(48, Math.Max(8, (rate + 1000) / 1000));
        long value = ((((64000L * streams) + (32000L * coupled)) * (kilohertz + 16)) + 32) >> 6;
        return (int)Math.Min(value, 750_000L * channels);
    }

    /// <summary>The samples a packet of <see cref="FrameDuration"/> holds at <paramref name="sampleRate"/>.</summary>
    internal int FrameSize(int sampleRate) => this.FrameDuration switch
    {
        OpusFrameDuration.Ms2_5 => sampleRate / 400,
        OpusFrameDuration.Ms5 => sampleRate / 200,
        OpusFrameDuration.Ms10 => sampleRate / 100,
        OpusFrameDuration.Ms20 => sampleRate / 50,
        OpusFrameDuration.Ms40 => sampleRate / 25,
        OpusFrameDuration.Ms60 => sampleRate * 3 / 50,
        OpusFrameDuration.Ms80 => sampleRate * 2 / 25,
        OpusFrameDuration.Ms100 => sampleRate / 10,
        _ => sampleRate * 3 / 25,
    };
}

/// <summary>The settings of an Ogg Opus file that aren't the encoder's.</summary>
public sealed class OpusFileOptions
{
    /// <summary>
    /// The Ogg stream's serial number (opusenc's <c>--serial</c>). opusenc picks one at random; the default
    /// here is 0, so the same samples always give the same file.
    /// </summary>
    public int Serial { get; init; }

    /// <summary>
    /// The longest a packet waits before its page is written (opusenc's <c>--max-delay</c>): 0 to 1 second,
    /// the default. Shorter pages cost a little size but let a stream be played sooner.
    /// </summary>
    public TimeSpan MaxPageDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Comments, each "KEY=value" (opusenc's <c>--comment</c>), after the vendor string.</summary>
    public IReadOnlyList<string> Comments { get; init; } = [];

    internal static OpusFileOptions Default { get; } = new();

    internal void Validate(string paramName)
    {
        if ((this.MaxPageDelay < TimeSpan.Zero) || (this.MaxPageDelay > TimeSpan.FromSeconds(1)))
        {
            throw new ArgumentOutOfRangeException(paramName, this.MaxPageDelay, "The page delay is 0 to 1 second.");
        }
        ArgumentNullException.ThrowIfNull(this.Comments, paramName);
        Comment.Validate(this.Comments, paramName);
    }
}

/// <summary>How an Opus encoder holds its bitrate.</summary>
public enum OpusBitrateMode
{
    /// <summary>Variable: each packet as large as its audio needs, averaging the bitrate.</summary>
    Vbr,

    /// <summary>Variable within a bound: no packet much larger than the bitrate allows.</summary>
    ConstrainedVbr,

    /// <summary>Constant: every packet the same size.</summary>
    Cbr,
}

/// <summary>The duration of an Opus packet.</summary>
public enum OpusFrameDuration
{
#pragma warning disable CA1707 // 2.5 ms, as opusenc's --framesize 2.5
    /// <summary>2.5 ms (CELT only).</summary>
    Ms2_5,
#pragma warning restore CA1707

    /// <summary>5 ms (CELT only).</summary>
    Ms5,

    /// <summary>10 ms.</summary>
    Ms10,

    /// <summary>20 ms.</summary>
    Ms20,

    /// <summary>40 ms.</summary>
    Ms40,

    /// <summary>60 ms.</summary>
    Ms60,

    /// <summary>80 ms.</summary>
    Ms80,

    /// <summary>100 ms.</summary>
    Ms100,

    /// <summary>120 ms.</summary>
    Ms120,
}

/// <summary>What an Opus encoder is tuned for.</summary>
public enum OpusApplication
{
    /// <summary>Music and general audio: the most faithful (OPUS_APPLICATION_AUDIO).</summary>
    Audio,

    /// <summary>Speech: the most intelligible (OPUS_APPLICATION_VOIP).</summary>
    Voip,

    /// <summary>The lowest delay: CELT only (OPUS_APPLICATION_RESTRICTED_LOWDELAY).</summary>
    RestrictedLowDelay,

    /// <summary>SILK only, for speech decoders that have nothing else (OPUS_APPLICATION_RESTRICTED_SILK).</summary>
    RestrictedSilk,

    /// <summary>CELT only, with the usual delay (OPUS_APPLICATION_RESTRICTED_CELT).</summary>
    RestrictedCelt,
}

/// <summary>A hint of what an Opus encoder's signal is.</summary>
public enum OpusSignal
{
    /// <summary>Detected from the signal.</summary>
    Auto,

    /// <summary>Music.</summary>
    Music,

    /// <summary>Speech.</summary>
    Speech,
}

/// <summary>An Opus audio bandwidth.</summary>
public enum OpusBandwidth
{
    /// <summary>4 kHz.</summary>
    Narrowband,

    /// <summary>6 kHz.</summary>
    Mediumband,

    /// <summary>8 kHz.</summary>
    Wideband,

    /// <summary>12 kHz.</summary>
    SuperWideband,

    /// <summary>20 kHz.</summary>
    Fullband,
}
