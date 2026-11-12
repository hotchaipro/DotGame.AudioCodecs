// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Buffers.Binary;
using System.IO;

namespace DotGame.AudioCodecs.Wav;

/// <summary>
/// Decodes WAV files: 8, 16, 24 and 32-bit integer and 32 and 64-bit float samples, 1 to 8 channels,
/// in the plain and WAVE_FORMAT_EXTENSIBLE forms.
/// </summary>
/// <remarks>
/// Chunks other than <c>fmt </c> and <c>data</c> are skipped. A file whose <c>data</c> chunk is longer
/// than the file is an <see cref="InvalidDataException"/>, as is one that ends early; a data size of
/// 0xFFFFFFFF (a stream written without its length) means the samples run to the end.
/// </remarks>
public sealed class WavDecoder : AudioDecoder
{
    private const int ChunkBytes = 65536;

    private readonly ByteSource _source;
    private readonly int _bits;            // the container's bits per sample
    private readonly int _bytesPerSample;
    private readonly int _blockAlign;
    private readonly long _dataStart;
    private long? _frames;

    /// <summary>Opens <paramref name="stream"/>, from its current position, as a WAV file.</summary>
    /// <param name="stream">The file. A stream that can't seek can be read but not seeked.</param>
    /// <param name="leaveOpen">Whether disposing the decoder leaves <paramref name="stream"/> open.</param>
    /// <exception cref="InvalidDataException">The data isn't a WAV file this decoder reads, or is damaged.</exception>
    public WavDecoder(Stream stream, bool leaveOpen = false)
        : this(new StreamByteSource(stream ?? throw new ArgumentNullException(nameof(stream)), leaveOpen))
    {
    }

    /// <summary>Opens <paramref name="data"/> as a WAV file, read in place.</summary>
    /// <exception cref="InvalidDataException">The data isn't a WAV file this decoder reads, or is damaged.</exception>
    public WavDecoder(ReadOnlyMemory<byte> data)
        : this(new MemoryByteSource(data))
    {
    }

    internal WavDecoder(ByteSource source)
    {
        this._source = source;
        try
        {
            Span<byte> riff = stackalloc byte[12];
            source.ReadExactly(riff, "RIFF header");
            if (!Recognizes(riff))
            {
                throw new InvalidDataException("Not a WAV file: it has no RIFF WAVE header.");
            }

            WavSampleFormat? format = null;
            int channels = 0;
            int sampleRate = 0;
            int blockAlign = 0;
            Span<byte> header = stackalloc byte[8];
            while (true)
            {
                if (source.Peek(8).Length < 8)
                {
                    throw new InvalidDataException("The WAV file has no data chunk.");
                }
                source.ReadExactly(header, "chunk header");
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
                if (header[..4].SequenceEqual("fmt "u8))
                {
                    if (size is < 16 or > 1024)
                    {
                        throw new InvalidDataException($"The WAV file's format chunk is {size} bytes.");
                    }
                    byte[] fmt = new byte[size];
                    source.ReadExactly(fmt, "format chunk");
                    (format, channels, sampleRate, blockAlign) = ReadFormat(fmt);
                    source.Skip(size & 1);
                }
                else if (header[..4].SequenceEqual("data"u8))
                {
                    if (format is null)
                    {
                        throw new InvalidDataException("The WAV file's data comes before its format chunk.");
                    }
                    this._dataStart = source.Position;
                    if (size != uint.MaxValue)
                    {
                        if ((source.Length >= 0) && (this._dataStart + size > source.Length))
                        {
                            throw new InvalidDataException($"The WAV file is cut short: its data chunk is {size} bytes, but {source.Length - this._dataStart} follow.");
                        }
                        this._frames = size / blockAlign;
                    }
                    else if (source.Length >= 0)
                    {
                        this._frames = (source.Length - this._dataStart) / blockAlign;
                    }
                    break;
                }
                else if (!source.Skip(size + (size & 1)))
                {
                    throw new InvalidDataException("The WAV file has no data chunk.");
                }
            }

            this.SampleFormat = format.Value;
            this.SampleRate = sampleRate;
            this.Channels = channels;
            this._bits = Bits(format.Value);
            this._bytesPerSample = this._bits / 8;
            this._blockAlign = blockAlign;
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    /// <summary>How the file stores its samples.</summary>
    public WavSampleFormat SampleFormat { get; }

    /// <inheritdoc/>
    public override long? Frames => this._frames;

    /// <inheritdoc/>
    public override bool CanSeek => this._source.CanSeek;

    internal static bool Recognizes(ReadOnlySpan<byte> start) =>
        (start.Length >= 12) && start[..4].SequenceEqual("RIFF"u8) && start[8..12].SequenceEqual("WAVE"u8);

    internal static int Bits(WavSampleFormat format) => format switch
    {
        WavSampleFormat.Pcm8 => 8,
        WavSampleFormat.Pcm16 => 16,
        WavSampleFormat.Pcm24 => 24,
        WavSampleFormat.Pcm32 => 32,
        WavSampleFormat.Float32 => 32,
        _ => 64,
    };

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            this._source.Dispose();
        }
        base.Dispose(disposing);
    }

    private protected override int ReadCore(Span<short> shorts, Span<float> floats, bool isFloat)
    {
        int channels = this.Channels;
        int capacity = (isFloat ? floats.Length : shorts.Length) / channels;
        if (this._frames is long total)
        {
            capacity = (int)Math.Min(capacity, total - this.Position);
        }
        int written = 0;
        while (written < capacity)
        {
            int frames = Math.Min(capacity - written, Math.Max(1, ChunkBytes / this._blockAlign));
            ReadOnlySpan<byte> bytes = this._source.Peek(frames * this._blockAlign);
            frames = bytes.Length / this._blockAlign;
            if (frames == 0)
            {
                if (this._frames is not null)
                {
                    throw new InvalidDataException("The WAV file ends before its data chunk does.");
                }
                this._frames = this.Position + written;
                break;
            }
            int samples = frames * channels;
            int at = written * channels;
            if (isFloat)
            {
                this.Convert(bytes, frames, floats.Slice(at, samples));
            }
            else
            {
                this.Convert(bytes, frames, shorts.Slice(at, samples));
            }
            this._source.Advance(frames * this._blockAlign);
            written += frames;
        }
        return written;
    }

    private protected override long SeekCore(long frame)
    {
        this._source.Seek(this._dataStart + (frame * this._blockAlign));
        return frame;
    }

    private static (WavSampleFormat Format, int Channels, int SampleRate, int BlockAlign) ReadFormat(ReadOnlySpan<byte> fmt)
    {
        int tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
        int channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]);
        uint sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(fmt[4..]);
        int blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(fmt[12..]);
        int bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]);
        if ((tag == 0xFFFE) && (fmt.Length >= 40))
        {
            // WAVE_FORMAT_EXTENSIBLE: the format is the sub-format GUID's first two bytes
            // (KSDATAFORMAT_SUBTYPE_PCM and _IEEE_FLOAT share the rest)
            tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt[24..]);
        }
        WavSampleFormat format = (tag, bits) switch
        {
            (1, 8) => WavSampleFormat.Pcm8,
            (1, 16) => WavSampleFormat.Pcm16,
            (1, 24) => WavSampleFormat.Pcm24,
            (1, 32) => WavSampleFormat.Pcm32,
            (3, 32) => WavSampleFormat.Float32,
            (3, 64) => WavSampleFormat.Float64,
            (1 or 3, _) => throw new InvalidDataException($"WAV samples of {bits} bits aren't supported."),
            _ => throw new InvalidDataException($"WAV format 0x{tag:X4} isn't supported: only integer PCM and IEEE float are."),
        };
        if (channels is < 1 or > 8)
        {
            throw new InvalidDataException($"The WAV file has {channels} channels: 1 to 8 are supported.");
        }
        if ((sampleRate == 0) || (sampleRate > int.MaxValue))
        {
            throw new InvalidDataException($"The WAV file's sample rate is {sampleRate} Hz.");
        }
        if (blockAlign != channels * (bits / 8))
        {
            throw new InvalidDataException($"The WAV file's block alignment, {blockAlign}, isn't {channels} channels of {bits} bits.");
        }
        return (format, channels, (int)sampleRate, blockAlign);
    }

    private void Convert(ReadOnlySpan<byte> bytes, int frames, Span<short> output)
    {
        int count = frames * this.Channels;
        switch (this.SampleFormat)
        {
            case WavSampleFormat.Pcm8:
                for (int i = 0; i < count; i++)
                {
                    output[i] = (short)((bytes[i] - 128) << 8);
                }
                break;
            case WavSampleFormat.Pcm16:
                for (int i = 0; i < count; i++)
                {
                    output[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes[(2 * i)..]);
                }
                break;
            case WavSampleFormat.Pcm24:
                for (int i = 0; i < count; i++)
                {
                    output[i] = (short)BinaryPrimitives.ReadInt16LittleEndian(bytes[((3 * i) + 1)..]);
                }
                break;
            case WavSampleFormat.Pcm32:
                for (int i = 0; i < count; i++)
                {
                    output[i] = (short)(BinaryPrimitives.ReadInt32LittleEndian(bytes[(4 * i)..]) >> 16);
                }
                break;
            case WavSampleFormat.Float32:
                for (int i = 0; i < count; i++)
                {
                    output[i] = Samples.FloatToShort(BinaryPrimitives.ReadSingleLittleEndian(bytes[(4 * i)..]));
                }
                break;
            default:
                for (int i = 0; i < count; i++)
                {
                    output[i] = Samples.FloatToShort((float)BinaryPrimitives.ReadDoubleLittleEndian(bytes[(8 * i)..]));
                }
                break;
        }
    }

    private void Convert(ReadOnlySpan<byte> bytes, int frames, Span<float> output)
    {
        int count = frames * this.Channels;
        switch (this.SampleFormat)
        {
            case WavSampleFormat.Pcm8:
                for (int i = 0; i < count; i++)
                {
                    output[i] = (bytes[i] - 128) * (1f / 128);
                }
                break;
            case WavSampleFormat.Pcm16:
                for (int i = 0; i < count; i++)
                {
                    output[i] = Samples.ShortToFloat(BinaryPrimitives.ReadInt16LittleEndian(bytes[(2 * i)..]));
                }
                break;
            case WavSampleFormat.Pcm24:
                for (int i = 0; i < count; i++)
                {
                    int value = (bytes[3 * i] << 8) | (bytes[(3 * i) + 1] << 16) | (bytes[(3 * i) + 2] << 24);
                    output[i] = (value >> 8) * (1f / 8388608);
                }
                break;
            case WavSampleFormat.Pcm32:
                for (int i = 0; i < count; i++)
                {
                    output[i] = BinaryPrimitives.ReadInt32LittleEndian(bytes[(4 * i)..]) * (1f / 2147483648);
                }
                break;
            case WavSampleFormat.Float32:
                for (int i = 0; i < count; i++)
                {
                    output[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes[(4 * i)..]);
                }
                break;
            default:
                for (int i = 0; i < count; i++)
                {
                    output[i] = (float)BinaryPrimitives.ReadDoubleLittleEndian(bytes[(8 * i)..]);
                }
                break;
        }
    }
}
