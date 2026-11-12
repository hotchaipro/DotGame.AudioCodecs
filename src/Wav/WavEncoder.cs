// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Buffers.Binary;
using System.IO;

namespace DotGame.AudioCodecs.Wav;

/// <summary>
/// Encodes WAV files: 8, 16, 24 and 32-bit integer or 32 and 64-bit float samples
/// (<see cref="WavEncoderOptions.SampleFormat"/>), 1 to 8 channels, at any rate.
/// </summary>
/// <remarks>
/// <para>
/// Mono and stereo 8 and 16-bit files have the plain 16-byte format; float ones the 18-byte format and
/// a <c>fact</c> chunk, as the RIFF specification asks. More channels, or integers of more bits, use
/// WAVE_FORMAT_EXTENSIBLE with the usual speaker positions for the count (as ffmpeg and opusenc
/// assume): 3 is FL FR FC, 4 FL FR BL BR, 5 FL FR FC BL BR, 6 5.1, 7 6.1 and 8 7.1. Nothing else is
/// written, so a file depends only on its samples.
/// </para>
/// <para>
/// The header holds the data's size, so the stream must be able to seek: <see cref="AudioEncoder.Complete"/>
/// writes the sizes. A file left without it has sizes of zero. A WAV file holds at most 4 GB of samples.
/// </para>
/// </remarks>
public sealed class WavEncoder : AudioEncoder
{
    private const int ChunkFrames = 4096;

    private readonly WavSampleFormat _format;
    private readonly int _bytesPerSample;
    private readonly int _headerLength;
    private readonly long _start;
    private readonly byte[] _chunk;
    private long _dataBytes;

    /// <summary>Starts a WAV file in <paramref name="stream"/>, at its current position.</summary>
    /// <param name="stream">Where the file goes; it must be able to seek.</param>
    /// <param name="sampleRate">The sample rate, in hertz.</param>
    /// <param name="channels">The channel count, 1 to 8.</param>
    /// <param name="options">How samples are stored; the default is 16-bit integers.</param>
    /// <param name="leaveOpen">Whether disposing the encoder leaves <paramref name="stream"/> open.</param>
    /// <exception cref="ArgumentException"><paramref name="stream"/> can't write or seek.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The rate or channel count is out of range.</exception>
    public WavEncoder(Stream stream, int sampleRate, int channels, WavEncoderOptions? options = null, bool leaveOpen = false)
        : base(stream, sampleRate, channels, leaveOpen)
    {
        ThrowIfNotSeekable(stream, "WAV");
        this._format = (options ?? new WavEncoderOptions()).SampleFormat;
        if (!Enum.IsDefined(this._format))
        {
            throw new ArgumentOutOfRangeException(nameof(options), this._format, "Not a WAV sample format.");
        }
        this._bytesPerSample = WavDecoder.Bits(this._format) / 8;
        this._chunk = new byte[ChunkFrames * channels * this._bytesPerSample];
        this._start = stream.Position;

        Span<byte> header = stackalloc byte[80];
        this._headerLength = this.WriteHeader(header, dataBytes: 0, frames: 0);
        stream.Write(header[..this._headerLength]);
    }

    private bool IsFloat => this._format is WavSampleFormat.Float32 or WavSampleFormat.Float64;

    private bool IsExtensible => (this.Channels > 2) || (!this.IsFloat && (WavDecoder.Bits(this._format) > 16));

    private protected override void WriteCore(ReadOnlySpan<short> shorts, ReadOnlySpan<float> floats, bool isFloat)
    {
        int channels = this.Channels;
        int total = (isFloat ? floats.Length : shorts.Length) / channels;
        for (int done = 0; done < total;)
        {
            int frames = Math.Min(ChunkFrames, total - done);
            int samples = frames * channels;
            Span<byte> bytes = this._chunk.AsSpan(0, samples * this._bytesPerSample);
            if (isFloat)
            {
                this.Convert(floats.Slice(done * channels, samples), bytes);
            }
            else
            {
                this.Convert(shorts.Slice(done * channels, samples), bytes);
            }
            if (this._dataBytes + bytes.Length > uint.MaxValue - this._headerLength)
            {
                throw new InvalidOperationException("A WAV file holds at most 4 GB of samples.");
            }
            this.Stream.Write(bytes);
            this._dataBytes += bytes.Length;
            done += frames;
        }
    }

    private protected override void CompleteCore()
    {
        if ((this._dataBytes & 1) != 0)
        {
            // Chunks are padded to an even length
            this.Stream.WriteByte(0);
        }
        long end = this.Stream.Position;
        Span<byte> header = stackalloc byte[80];
        this.WriteHeader(header, this._dataBytes, this._dataBytes / (this.Channels * this._bytesPerSample));
        this.Stream.Position = this._start;
        this.Stream.Write(header[..this._headerLength]);
        this.Stream.Position = end;
    }

    // The header with the given sizes: RIFF, fmt (and fact for float), and the data chunk's header
    private int WriteHeader(Span<byte> h, long dataBytes, long frames)
    {
        int bits = this._bytesPerSample * 8;
        int blockAlign = this.Channels * this._bytesPerSample;
        bool extensible = this.IsExtensible;
        int formatSize = extensible ? 40 : this.IsFloat ? 18 : 16;
        bool fact = this.IsFloat;
        int length = 12 + 8 + formatSize + (fact ? 12 : 0) + 8;
        long riffSize = length - 8 + dataBytes + (dataBytes & 1);

        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], (uint)riffSize);
        "WAVE"u8.CopyTo(h[8..]);
        "fmt "u8.CopyTo(h[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], (uint)formatSize);
        int tag = this.IsFloat ? 3 : 1;
        BinaryPrimitives.WriteUInt16LittleEndian(h[20..], (ushort)(extensible ? 0xFFFE : tag));
        BinaryPrimitives.WriteUInt16LittleEndian(h[22..], (ushort)this.Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(h[24..], (uint)this.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(h[28..], (uint)((long)this.SampleRate * blockAlign));
        BinaryPrimitives.WriteUInt16LittleEndian(h[32..], (ushort)blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(h[34..], (ushort)bits);
        int at = 36;
        if (formatSize > 16)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(h[at..], (ushort)(formatSize - 18));
            at += 2;
        }
        if (extensible)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(h[at..], (ushort)bits);
            BinaryPrimitives.WriteUInt32LittleEndian(h[(at + 2)..], ChannelMask(this.Channels));
            // KSDATAFORMAT_SUBTYPE_PCM or _IEEE_FLOAT: the tag, then 00000000-0010-0080-00AA00389B71
            BinaryPrimitives.WriteUInt32LittleEndian(h[(at + 6)..], (uint)tag);
            ReadOnlySpan<byte> guid = [0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71];
            guid.CopyTo(h[(at + 10)..]);
            at += 22;
        }
        if (fact)
        {
            "fact"u8.CopyTo(h[at..]);
            BinaryPrimitives.WriteUInt32LittleEndian(h[(at + 4)..], 4);
            BinaryPrimitives.WriteUInt32LittleEndian(h[(at + 8)..], (uint)frames);
            at += 12;
        }
        "data"u8.CopyTo(h[at..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[(at + 4)..], (uint)dataBytes);
        return at + 8;
    }

    // The speaker positions of WAVE channel order for a channel count (KSAUDIO_SPEAKER_*)
    private static uint ChannelMask(int channels) => channels switch
    {
        1 => 0x4,      // FC
        2 => 0x3,      // FL FR
        3 => 0x7,      // FL FR FC
        4 => 0x33,     // FL FR BL BR
        5 => 0x37,     // FL FR FC BL BR
        6 => 0x3F,     // FL FR FC LFE BL BR
        7 => 0x70F,    // FL FR FC LFE BC SL SR
        _ => 0x63F,    // FL FR FC LFE BL BR SL SR
    };

    private void Convert(ReadOnlySpan<short> input, Span<byte> output)
    {
        switch (this._format)
        {
            case WavSampleFormat.Pcm8:
                for (int i = 0; i < input.Length; i++)
                {
                    output[i] = (byte)(Samples.ShortToInt(input[i], 8) + 128);
                }
                break;
            case WavSampleFormat.Pcm16:
                for (int i = 0; i < input.Length; i++)
                {
                    BinaryPrimitives.WriteInt16LittleEndian(output[(2 * i)..], input[i]);
                }
                break;
            case WavSampleFormat.Pcm24:
                for (int i = 0; i < input.Length; i++)
                {
                    output[3 * i] = 0;
                    BinaryPrimitives.WriteInt16LittleEndian(output[((3 * i) + 1)..], input[i]);
                }
                break;
            case WavSampleFormat.Pcm32:
                for (int i = 0; i < input.Length; i++)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(output[(4 * i)..], input[i] << 16);
                }
                break;
            case WavSampleFormat.Float32:
                for (int i = 0; i < input.Length; i++)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(output[(4 * i)..], Samples.ShortToFloat(input[i]));
                }
                break;
            default:
                for (int i = 0; i < input.Length; i++)
                {
                    BinaryPrimitives.WriteDoubleLittleEndian(output[(8 * i)..], input[i] / 32768.0);
                }
                break;
        }
    }

    private void Convert(ReadOnlySpan<float> input, Span<byte> output)
    {
        switch (this._format)
        {
            case WavSampleFormat.Pcm8:
                for (int i = 0; i < input.Length; i++)
                {
                    output[i] = (byte)(Samples.FloatToInt(input[i], 8) + 128);
                }
                break;
            case WavSampleFormat.Pcm16:
                for (int i = 0; i < input.Length; i++)
                {
                    BinaryPrimitives.WriteInt16LittleEndian(output[(2 * i)..], Samples.FloatToShort(input[i]));
                }
                break;
            case WavSampleFormat.Pcm24:
                for (int i = 0; i < input.Length; i++)
                {
                    int value = Samples.FloatToInt(input[i], 24);
                    output[3 * i] = (byte)value;
                    output[(3 * i) + 1] = (byte)(value >> 8);
                    output[(3 * i) + 2] = (byte)(value >> 16);
                }
                break;
            case WavSampleFormat.Pcm32:
                for (int i = 0; i < input.Length; i++)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(output[(4 * i)..], Samples.FloatToInt(input[i], 32));
                }
                break;
            case WavSampleFormat.Float32:
                for (int i = 0; i < input.Length; i++)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(output[(4 * i)..], input[i]);
                }
                break;
            default:
                for (int i = 0; i < input.Length; i++)
                {
                    BinaryPrimitives.WriteDoubleLittleEndian(output[(8 * i)..], input[i]);
                }
                break;
        }
    }
}
