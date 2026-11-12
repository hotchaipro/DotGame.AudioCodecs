// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.IO;

namespace DotGame.AudioCodecs.Opus;

/// <summary>
/// Decodes Opus packets from any container (WebM, RTP, CAF) or none: libopus 1.6.1's decoder, its
/// fixed-point build, sample for sample. Lost packets are recovered from the next one's forward error
/// correction (<see cref="DecodeFec(ReadOnlySpan{byte}, Span{short})"/>) or concealed
/// (<see cref="Conceal(Span{short})"/>).
/// </summary>
/// <remarks>
/// A mono or stereo stream (any Opus packet decodes to either), at 8, 12, 16, 24 or 48 kHz. <c>short</c>
/// output is <c>opus_decode</c>'s and <c>float</c> output <c>opus_decode_float</c>'s, which keeps the
/// decoder's 24 bits. Packets are at most 120 ms, so a buffer of 120 ms always holds one.
/// </remarks>
internal sealed class OpusPacketDecoder
{
    private readonly OpusDecoderState _decoder;
    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly int[] _res;

    /// <summary>Creates a decoder.</summary>
    /// <param name="sampleRate">8000, 12000, 16000, 24000 or 48000.</param>
    /// <param name="channels">1 or 2.</param>
    /// <exception cref="ArgumentOutOfRangeException">The rate or channel count isn't one of those.</exception>
    public OpusPacketDecoder(int sampleRate, int channels)
    {
        ThrowIfBadFormat(sampleRate, channels, maxChannels: 2);
        this._sampleRate = sampleRate;
        this._channels = channels;
        this._decoder = new OpusDecoderState(sampleRate, channels);
        this._res = new int[sampleRate / 25 * 3 * channels];
    }

    /// <summary>The range decoder's final state after the last packet, to check against the encoder's.</summary>
    public uint FinalRange => this._decoder.GetFinalRange();

    /// <summary>Decodes <paramref name="packet"/> into <paramref name="samples"/>, interleaved.</summary>
    /// <returns>The frames decoded.</returns>
    /// <exception cref="ArgumentException"><paramref name="samples"/> is too small for the packet.</exception>
    /// <exception cref="InvalidDataException">The packet is damaged.</exception>
    public int Decode(ReadOnlySpan<byte> packet, Span<short> samples)
    {
        int frames = this.DecodeRes(packet, this.PacketFrames(packet, samples.Length, nameof(samples)), fec: false);
        OpusDecoderState.ResToInt16(this._res.AsSpan(0, frames * this._channels), samples);
        return frames;
    }

    /// <summary>Decodes <paramref name="packet"/> into <paramref name="samples"/>, interleaved, -1 to 1.</summary>
    /// <returns>The frames decoded.</returns>
    /// <exception cref="ArgumentException"><paramref name="samples"/> is too small for the packet.</exception>
    /// <exception cref="InvalidDataException">The packet is damaged.</exception>
    public int Decode(ReadOnlySpan<byte> packet, Span<float> samples)
    {
        int frames = this.DecodeRes(packet, this.PacketFrames(packet, samples.Length, nameof(samples)), fec: false);
        OpusDecoderState.ResToFloat(this._res.AsSpan(0, frames * this._channels), samples);
        return frames;
    }

    /// <summary>
    /// Recovers a lost packet from the forward error correction in <paramref name="nextPacket"/>, the one
    /// after it, or conceals it if that has none. <paramref name="samples"/> is the lost packet's length:
    /// a multiple of 2.5 ms.
    /// </summary>
    /// <returns>The frames decoded.</returns>
    /// <exception cref="ArgumentException"><paramref name="samples"/> isn't a multiple of 2.5 ms.</exception>
    /// <exception cref="InvalidDataException"><paramref name="nextPacket"/> is damaged.</exception>
    public int DecodeFec(ReadOnlySpan<byte> nextPacket, Span<short> samples)
    {
        int frames = this.DecodeRes(nextPacket, this.LostFrames(samples.Length, nameof(samples)), fec: true);
        OpusDecoderState.ResToInt16(this._res.AsSpan(0, frames * this._channels), samples);
        return frames;
    }

    /// <inheritdoc cref="DecodeFec(ReadOnlySpan{byte}, Span{short})"/>
    public int DecodeFec(ReadOnlySpan<byte> nextPacket, Span<float> samples)
    {
        int frames = this.DecodeRes(nextPacket, this.LostFrames(samples.Length, nameof(samples)), fec: true);
        OpusDecoderState.ResToFloat(this._res.AsSpan(0, frames * this._channels), samples);
        return frames;
    }

    /// <summary>Conceals a lost packet: <paramref name="samples"/> is its length, a multiple of 2.5 ms.</summary>
    /// <returns>The frames written.</returns>
    /// <exception cref="ArgumentException"><paramref name="samples"/> isn't a multiple of 2.5 ms.</exception>
    public int Conceal(Span<short> samples)
    {
        int frames = this.DecodeRes(default, this.LostFrames(samples.Length, nameof(samples)), fec: false);
        OpusDecoderState.ResToInt16(this._res.AsSpan(0, frames * this._channels), samples);
        return frames;
    }

    /// <inheritdoc cref="Conceal(Span{short})"/>
    public int Conceal(Span<float> samples)
    {
        int frames = this.DecodeRes(default, this.LostFrames(samples.Length, nameof(samples)), fec: false);
        OpusDecoderState.ResToFloat(this._res.AsSpan(0, frames * this._channels), samples);
        return frames;
    }

    /// <summary>Starts again, as a new decoder (OPUS_RESET_STATE): for a seek or a new stream.</summary>
    public void Reset() => this._decoder.ResetState();

    internal static void ThrowIfBadFormat(int sampleRate, int channels, int maxChannels)
    {
        if (sampleRate is not (8000 or 12000 or 16000 or 24000 or 48000))
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Opus codes at 8, 12, 16, 24 or 48 kHz.");
        }
        if ((channels < 1) || (channels > maxChannels))
        {
            throw new ArgumentOutOfRangeException(nameof(channels), channels, $"Opus codes 1 to {maxChannels} channels here.");
        }
    }

    private int DecodeRes(ReadOnlySpan<byte> packet, int frames, bool fec) =>
        this._decoder.DecodeRes(packet, this._res, frames, fec);

    // The packet's frames, checking the buffer holds them
    private int PacketFrames(ReadOnlySpan<byte> packet, int length, string paramName)
    {
        if (packet.IsEmpty)
        {
            throw new ArgumentException("The packet is empty: a lost packet is concealed with Conceal.", nameof(packet));
        }
        int frames = OpusDecoderState.opus_packet_get_nb_samples(packet, packet.Length, this._sampleRate);
        if (frames <= 0)
        {
            throw new InvalidDataException("The Opus packet is damaged: its frame count isn't valid.");
        }
        if (frames * this._channels > length)
        {
            throw new ArgumentException($"The packet is {frames} frames; the buffer holds {length / this._channels}.", paramName);
        }
        return frames;
    }

    private int LostFrames(int length, string paramName)
    {
        int frames = length / this._channels;
        int quantum = this._sampleRate / 400;
        if ((length % this._channels != 0) || (frames == 0) || (frames % quantum != 0) || (frames > this._sampleRate / 25 * 3))
        {
            throw new ArgumentException($"A lost packet is a multiple of 2.5 ms ({quantum} frames), up to 120 ms, of {this._channels} channels.", paramName);
        }
        return frames;
    }
}
