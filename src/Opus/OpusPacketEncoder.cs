// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;

namespace DotGame.AudioCodecs.Opus;

/// <summary>
/// Encodes Opus packets, one frame of samples each, for any container (WebM, RTP, CAF) or none: libopus
/// 1.6.1's encoder, its fixed-point build, packet for packet.
/// </summary>
/// <remarks>
/// <para>
/// A mono or stereo stream at 8, 12, 16, 24 or 48 kHz. Each <see cref="Encode(ReadOnlySpan{short}, Span{byte})"/>
/// takes exactly <see cref="FrameSize"/> frames. <c>short</c> samples are 16-bit input and <c>float</c>
/// ones 24-bit, as libopus's <c>opus_encode</c> and <c>opus_encode_float</c> take them; on CoreCLR the
/// packets and <see cref="FinalRange"/> are byte for byte libopus's (fixed point, generic C). A
/// decoder starts <see cref="Lookahead"/> samples late: an Ogg file's pre-skip.
/// </para>
/// <para>A 4000-byte packet buffer always suffices; a smaller one caps the packet's size.</para>
/// </remarks>
internal sealed class OpusPacketEncoder
{
    private readonly OpusEncoderState _encoder;
    private readonly int _channels;

    /// <summary>Creates an encoder.</summary>
    /// <param name="sampleRate">8000, 12000, 16000, 24000 or 48000.</param>
    /// <param name="channels">1 or 2.</param>
    /// <param name="options">The settings; the default is opusenc's.</param>
    /// <exception cref="ArgumentOutOfRangeException">The rate, channels or a setting is out of range.</exception>
    public OpusPacketEncoder(int sampleRate, int channels, OpusEncoderOptions? options = null)
    {
        OpusPacketDecoder.ThrowIfBadFormat(sampleRate, channels, maxChannels: 2);
        options ??= OpusEncoderOptions.Default;
        options.Validate(channels, nameof(options));
        this._channels = channels;
        this._encoder = Create(sampleRate, channels, options, options.BitrateFor(1, channels - 1, sampleRate, channels));
        this.FrameSize = options.FrameSize(sampleRate);
    }

    /// <summary>The frames each packet takes: the frame duration at the sample rate.</summary>
    public int FrameSize { get; }

    /// <summary>The samples a decoder outputs before the first input sample: an Ogg Opus file's pre-skip.</summary>
    public int Lookahead => this._encoder.GetLookahead();

    /// <summary>The range coder's final state after the last packet, for checking a decoder against it.</summary>
    public uint FinalRange => this._encoder.GetFinalRange();

    /// <summary>Encodes one frame of <see cref="FrameSize"/> frames, interleaved, into <paramref name="packet"/>.</summary>
    /// <returns>The packet's length in bytes.</returns>
    /// <exception cref="ArgumentException"><paramref name="frame"/> isn't <see cref="FrameSize"/> frames.</exception>
    public int Encode(ReadOnlySpan<short> frame, Span<byte> packet)
    {
        this.ThrowIfWrongLength(frame.Length, nameof(frame));
        return Check(this._encoder.Encode(frame, this.FrameSize, packet), nameof(packet));
    }

    /// <summary>Encodes one frame of <see cref="FrameSize"/> frames, interleaved, -1 to 1, into <paramref name="packet"/>.</summary>
    /// <returns>The packet's length in bytes.</returns>
    /// <exception cref="ArgumentException"><paramref name="frame"/> isn't <see cref="FrameSize"/> frames.</exception>
    public int Encode(ReadOnlySpan<float> frame, Span<byte> packet)
    {
        this.ThrowIfWrongLength(frame.Length, nameof(frame));
        return Check(this._encoder.Encode(frame, this.FrameSize, packet), nameof(packet));
    }

    /// <summary>Starts again, as a new encoder with the same settings (OPUS_RESET_STATE).</summary>
    public void Reset() => this._encoder.ResetState();

    /// <summary>A libopus encoder with <paramref name="options"/> applied as opusenc applies them.</summary>
    internal static OpusEncoderState Create(int sampleRate, int channels, OpusEncoderOptions options, int bitrate)
    {
        var encoder = new OpusEncoderState(sampleRate, channels, options.Application switch
        {
            OpusApplication.Audio => OpusEncoderState.OPUS_APPLICATION_AUDIO,
            OpusApplication.Voip => OpusEncoderState.OPUS_APPLICATION_VOIP,
            OpusApplication.RestrictedLowDelay => OpusEncoderState.OPUS_APPLICATION_RESTRICTED_LOWDELAY,
            OpusApplication.RestrictedSilk => OpusEncoderState.OPUS_APPLICATION_RESTRICTED_SILK,
            _ => OpusEncoderState.OPUS_APPLICATION_RESTRICTED_CELT,
        });
        Configure(encoder, options, bitrate);
        return encoder;
    }

    /// <summary>The settings opusenc sets, in its order.</summary>
    internal static void Configure(OpusEncoderState encoder, OpusEncoderOptions options, int bitrate)
    {
        encoder.SetBitrate(bitrate);
        encoder.SetVBR(options.BitrateMode == OpusBitrateMode.Cbr ? 0 : 1);
        encoder.SetVBRConstraint(options.BitrateMode == OpusBitrateMode.ConstrainedVbr ? 1 : 0);
        if (options.Signal != OpusSignal.Auto)
        {
            encoder.SetSignal(options.Signal == OpusSignal.Music ? OpusEncoderState.OPUS_SIGNAL_MUSIC : OpusEncoderState.OPUS_SIGNAL_VOICE);
        }
        encoder.SetComplexity(options.Complexity);
        encoder.SetPacketLossPerc(options.ExpectedPacketLoss);
        if (options.InbandFec)
        {
            encoder.SetInbandFEC(1);
        }
        if (options.Dtx)
        {
            encoder.SetDTX(1);
        }
        if (options.MaxBandwidth != OpusBandwidth.Fullband)
        {
            encoder.SetMaxBandwidth(1101 + (int)options.MaxBandwidth);
        }
        if (!options.PhaseInversion)
        {
            encoder.SetPhaseInversionDisabled(1);
        }
    }

    private static int Check(int length, string paramName)
    {
        if (length < 0)
        {
            throw length == -2
                ? new ArgumentException("The packet buffer is too small.", paramName)
                : new InvalidOperationException($"The Opus encoder failed ({OpusDefines.opus_strerror(length)}).");
        }
        return length;
    }

    private void ThrowIfWrongLength(int length, string paramName)
    {
        if (length != this.FrameSize * this._channels)
        {
            throw new ArgumentException($"A frame is {this.FrameSize} frames of {this._channels} channels ({this.FrameSize * this._channels} samples), not {length} samples.", paramName);
        }
    }
}
