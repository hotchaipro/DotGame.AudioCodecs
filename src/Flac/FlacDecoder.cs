// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace DotGame.AudioCodecs.Flac;

/// <summary>
/// Decodes FLAC files (RFC 9639): 1 to 8 channels of 4 to 32-bit samples, every frame and subframe
/// type, checking each frame's CRC-8 and CRC-16.
/// </summary>
/// <remarks>
/// <para>
/// <c>float</c> output is exact for up to 24 bits; <c>short</c> output keeps the top 16 bits of wider
/// samples (as libsndfile and ffmpeg do) and widens narrower ones. Metadata other than STREAMINFO,
/// VORBIS_COMMENT and SEEKTABLE is skipped. The STREAMINFO MD5 isn't checked: the CRCs catch damage, and
/// hashing would cost more than decoding.
/// </para>
/// <para>
/// A seek goes by the seek table when the file has one, and by bisection otherwise, as libFLAC does,
/// then decodes from the frame that holds the target: the samples are exactly a decode from the start's.
/// </para>
/// </remarks>
public sealed class FlacDecoder : AudioDecoder
{
    private readonly ByteSource _source;
    private readonly int _minBlockSize;
    private readonly int _maxBlockSize;
    private readonly int _peekLength;          // bytes that always hold a whole frame
    private readonly long _totalSamples;       // 0: unknown
    private readonly long _firstFrame;         // where the frames start
    private readonly (long Sample, long Offset)[] _seekPoints;
    private readonly int[][] _blocks;
    private long? _frames;
    private int _pendingOffset;                // frames of _blocks not yet read: from here
    private int _pendingCount;
    private long _blockStart;                  // the first sample of the block in _blocks
    private bool _ended;
    private readonly bool _checkCrcs;          // each frame's CRC-16

    /// <summary>Opens <paramref name="stream"/>, from its current position, as a FLAC file.</summary>
    /// <param name="stream">The file. A stream that can't seek can be read but not seeked.</param>
    /// <param name="options">How it reads: whether it checks checksums. <see langword="null"/> is the defaults.</param>
    /// <param name="leaveOpen">Whether disposing the decoder leaves <paramref name="stream"/> open.</param>
    /// <exception cref="InvalidDataException">The data isn't a FLAC file this decoder reads, or is damaged.</exception>
    public FlacDecoder(Stream stream, AudioDecoderOptions? options = null, bool leaveOpen = false)
        : this(new StreamByteSource(stream ?? throw new ArgumentNullException(nameof(stream)), leaveOpen), options ?? AudioDecoderOptions.Default)
    {
    }

    /// <summary>Opens <paramref name="data"/> as a FLAC file, read in place.</summary>
    /// <exception cref="InvalidDataException">The data isn't a FLAC file this decoder reads, or is damaged.</exception>
    public FlacDecoder(ReadOnlyMemory<byte> data)
        : this(new MemoryByteSource(data), AudioDecoderOptions.Default)
    {
    }

    /// <summary>Opens <paramref name="data"/> as a FLAC file, read in place, as <paramref name="options"/> say.</summary>
    /// <param name="data">The file.</param>
    /// <param name="options">How it reads: whether it checks checksums. <see langword="null"/> is the defaults.</param>
    /// <exception cref="InvalidDataException">The data isn't a FLAC file this decoder reads, or is damaged.</exception>
    public FlacDecoder(ReadOnlyMemory<byte> data, AudioDecoderOptions? options)
        : this(new MemoryByteSource(data), options ?? AudioDecoderOptions.Default)
    {
    }

    internal FlacDecoder(ByteSource source, AudioDecoderOptions options)
    {
        this._source = source;
        this._checkCrcs = !options.SkipChecksums;
        try
        {
            Span<byte> start = stackalloc byte[4];
            source.ReadExactly(start, "signature");
            if (!Recognizes(start))
            {
                throw new InvalidDataException("Not a FLAC file: it doesn't start with \"fLaC\".");
            }

            bool sawStreamInfo = false;
            var seekPoints = new List<(long, long)>();
            string[] comments = [];
            Span<byte> blockHeader = stackalloc byte[4];
            bool last;
            do
            {
                source.ReadExactly(blockHeader, "metadata");
                last = (blockHeader[0] & 0x80) != 0;
                int type = blockHeader[0] & 0x7F;
                int length = (blockHeader[1] << 16) | (blockHeader[2] << 8) | blockHeader[3];
                if (!sawStreamInfo && (type != 0))
                {
                    throw new InvalidDataException("The FLAC file's first metadata block isn't STREAMINFO.");
                }
                if ((type == 0) || (type == 3) || (type == 4))
                {
                    if ((source.Length >= 0) && (source.Position + length > source.Length))
                    {
                        throw new InvalidDataException("The FLAC file is cut short in its metadata.");
                    }
                    byte[] block = new byte[length];
                    source.ReadExactly(block, "metadata");
                    if (type == 0)
                    {
                        if (sawStreamInfo || (length < 34))
                        {
                            throw new InvalidDataException("The FLAC file's STREAMINFO isn't valid.");
                        }
                        this.ReadStreamInfo(block, out this._minBlockSize, out this._maxBlockSize, out this._totalSamples);
                        sawStreamInfo = true;
                    }
                    else if (type == 3)
                    {
                        ReadSeekTable(block, seekPoints);
                    }
                    else
                    {
                        comments = Comment.Read(block, "FLAC");
                    }
                }
                else if ((type == 127) || !source.Skip(length))
                {
                    throw new InvalidDataException(type == 127 ? "The FLAC file has an invalid metadata block." : "The FLAC file is cut short in its metadata.");
                }
            }
            while (!last);

            this.Comments = comments;
            this._firstFrame = source.Position;
            this._seekPoints = [.. seekPoints];
            this._frames = this._totalSamples > 0 ? this._totalSamples : null;
            int channels = this.Channels;
            this._blocks = new int[channels][];
            for (int c = 0; c < channels; c++)
            {
                this._blocks[c] = new int[this._maxBlockSize];
            }

            // A frame can't be longer than its header, verbatim samples a bit wider than any escape
            // code allows, and its CRC
            this._peekLength = 32 + (channels * ((this._maxBlockSize * 33 / 8) + 64));
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    /// <summary>The bits of each sample, 4 to 32.</summary>
    public int BitsPerSample { get; private set; }

    /// <summary>The comments of the VORBIS_COMMENT block, each "KEY=value".</summary>
    public IReadOnlyList<string> Comments { get; }

    /// <inheritdoc/>
    public override long? Frames => this._frames;

    /// <inheritdoc/>
    public override bool CanSeek => this._source.CanSeek;

    internal static bool Recognizes(ReadOnlySpan<byte> start) => (start.Length >= 4) && start[..4].SequenceEqual("fLaC"u8);

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
        int bits = this.BitsPerSample;
        int capacity = (isFloat ? floats.Length : shorts.Length) / channels;
        int written = 0;
        while (written < capacity)
        {
            if (this._pendingCount == 0)
            {
                if (!this.DecodeNext())
                {
                    break;
                }
                continue;
            }
            int count = Math.Min(this._pendingCount, capacity - written);
            int from = this._pendingOffset;
            if (isFloat)
            {
                Span<float> output = floats.Slice(written * channels, count * channels);
                float scale = Samples.Scale(bits);
                for (int c = 0; c < channels; c++)
                {
                    ReadOnlySpan<int> block = this._blocks[c].AsSpan(from, count);
                    for (int i = 0; i < block.Length; i++)
                    {
                        output[(i * channels) + c] = block[i] * scale;
                    }
                }
            }
            else
            {
                Span<short> output = shorts.Slice(written * channels, count * channels);
                int shift = bits - 16;
                for (int c = 0; c < channels; c++)
                {
                    ReadOnlySpan<int> block = this._blocks[c].AsSpan(from, count);
                    if (shift == 0)
                    {
                        for (int i = 0; i < block.Length; i++)
                        {
                            output[(i * channels) + c] = (short)block[i];
                        }
                    }
                    else if (shift > 0)
                    {
                        for (int i = 0; i < block.Length; i++)
                        {
                            output[(i * channels) + c] = (short)(block[i] >> shift);
                        }
                    }
                    else
                    {
                        for (int i = 0; i < block.Length; i++)
                        {
                            output[(i * channels) + c] = (short)(block[i] << -shift);
                        }
                    }
                }
            }
            this._pendingOffset += count;
            this._pendingCount -= count;
            written += count;
        }
        return written;
    }

    private protected override long SeekCore(long frame)
    {
        // A frame at or before the target: from the seek table (a point whose frame is where it says),
        // or by bisection. Without STREAMINFO's total, a read from the start ends at the first bytes
        // that aren't a frame, so a seek decodes from the start too, to end where it would.
        long offset = this._firstFrame;
        long sample = 0;
        if (this._totalSamples > 0)
        {
            foreach (var (pointSample, pointOffset) in this._seekPoints)
            {
                if (pointSample > frame)
                {
                    break;
                }
                if (this.FrameAt(this._firstFrame + pointOffset) == pointSample)
                {
                    sample = pointSample;
                    offset = this._firstFrame + pointOffset;
                }
            }
            if (frame - sample > 10L * this._maxBlockSize)
            {
                (offset, sample) = this.Bisect(frame, offset, sample);
            }
        }

        this._source.Seek(offset);
        this._ended = false;
        this._pendingOffset = 0;
        this._pendingCount = 0;
        this._blockStart = sample;
        while (this.DecodeNext())
        {
            long end = this._blockStart + this._pendingCount;
            if (end > frame)
            {
                int skip = (int)(frame - this._blockStart);
                this._pendingOffset += skip;
                this._pendingCount -= skip;
                break;
            }
            this._pendingOffset += this._pendingCount;
            this._pendingCount = 0;
        }
        return frame;
    }

    // Decodes the next frame into _blocks: false at the end
    private bool DecodeNext()
    {
        if (this._ended)
        {
            return false;
        }
        this._blockStart += this._pendingOffset + this._pendingCount;
        this._pendingOffset = 0;
        this._pendingCount = 0;
        if ((this._totalSamples > 0) && (this._blockStart >= this._totalSamples))
        {
            this._ended = true;
            return false;
        }

        ReadOnlySpan<byte> data = this._source.Peek(this._peekLength);
        if (!FlacFrame.TryReadHeader(data, out FlacFrame.Header header))
        {
            // Bytes after the last frame that aren't a frame end the stream (ID3 or padding after it)
            this._ended = true;
            if (this._totalSamples > 0)
            {
                throw new InvalidDataException(data.Length < 2
                    ? $"The FLAC file ends after {this._blockStart} of its {this._totalSamples} samples."
                    : $"The FLAC file is damaged after {this._blockStart} samples: no frame header follows.");
            }
            this._frames = this._blockStart;
            return false;
        }
        this.CheckHeader(header);
        if (this.FirstSample(header) != this._blockStart)
        {
            // A frame's number says where it is; one out of place would send a seek somewhere a read
            // from the start doesn't go
            throw new InvalidDataException($"A FLAC frame says it starts at sample {this.FirstSample(header)}; it follows {this._blockStart} samples.");
        }
        int length = FlacFrame.Decode(data, header, header.Bits == 0 ? this.BitsPerSample : header.Bits, this._blocks, complete: true, this._checkCrcs);
        this._source.Advance(length);

        int count = header.BlockSize;
        if (this._totalSamples > 0)
        {
            // STREAMINFO's length is exact: a last block is only as long as it says
            count = (int)Math.Min(count, this._totalSamples - this._blockStart);
        }
        this._pendingCount = count;
        return true;
    }

    private void CheckHeader(in FlacFrame.Header header)
    {
        if (header.Channels != this.Channels)
        {
            throw new InvalidDataException($"A FLAC frame has {header.Channels} channels; the stream has {this.Channels}.");
        }
        if ((header.Bits != 0) && (header.Bits != this.BitsPerSample))
        {
            throw new InvalidDataException($"A FLAC frame has {header.Bits}-bit samples; the stream has {this.BitsPerSample}.");
        }
        if ((header.SampleRate != 0) && (header.SampleRate != this.SampleRate))
        {
            throw new InvalidDataException($"A FLAC frame is at {header.SampleRate} Hz; the stream is at {this.SampleRate}.");
        }
        if (header.BlockSize > this._maxBlockSize)
        {
            throw new InvalidDataException($"A FLAC frame has {header.BlockSize} samples, more than STREAMINFO's {this._maxBlockSize}.");
        }
    }

    private void ReadStreamInfo(ReadOnlySpan<byte> block, out int minBlockSize, out int maxBlockSize, out long totalSamples)
    {
        minBlockSize = BinaryPrimitives.ReadUInt16BigEndian(block);
        maxBlockSize = BinaryPrimitives.ReadUInt16BigEndian(block[2..]);
        ulong packed = BinaryPrimitives.ReadUInt64BigEndian(block[10..]);
        int sampleRate = (int)(packed >> 44);
        int channels = (int)((packed >> 41) & 7) + 1;
        int bits = (int)((packed >> 36) & 31) + 1;
        totalSamples = (long)(packed & 0xF_FFFF_FFFF);
        if ((maxBlockSize < 16) || (minBlockSize > maxBlockSize))
        {
            throw new InvalidDataException($"The FLAC file's block sizes, {minBlockSize} to {maxBlockSize}, aren't valid.");
        }
        if (sampleRate == 0)
        {
            throw new InvalidDataException("The FLAC file's sample rate is 0.");
        }
        if (bits < 4)
        {
            throw new InvalidDataException($"FLAC samples of {bits} bits aren't valid.");
        }
        this.SampleRate = sampleRate;
        this.Channels = channels;
        this.BitsPerSample = bits;
    }

    private static void ReadSeekTable(ReadOnlySpan<byte> block, List<(long, long)> points)
    {
        for (int at = 0; at + 18 <= block.Length; at += 18)
        {
            ulong sample = BinaryPrimitives.ReadUInt64BigEndian(block[at..]);
            ulong offset = BinaryPrimitives.ReadUInt64BigEndian(block[(at + 8)..]);
            if ((sample == ulong.MaxValue) || (sample > long.MaxValue) || (offset > long.MaxValue))
            {
                continue;
            }
            if ((points.Count > 0) && ((long)sample <= points[^1].Item1))
            {
                continue;
            }
            points.Add(((long)sample, (long)offset));
        }
    }

    // The first sample of the frame header at offset, or -1 if there isn't one
    private long FrameAt(long offset)
    {
        if ((offset < this._firstFrame) || ((this._source.Length >= 0) && (offset >= this._source.Length)))
        {
            return -1;
        }
        this._source.Seek(offset);
        return FlacFrame.TryReadHeader(this._source.Peek(this._peekLength), out FlacFrame.Header header) && (header.Channels == this.Channels)
            ? this.FirstSample(header)
            : -1;
    }

    // The first sample of a frame header, from its frame number for a fixed block size
    private long FirstSample(in FlacFrame.Header header) =>
        header.VariableBlockSize ? header.Number : header.Number * this._minBlockSize;

    // The last frame at or before target, between a known frame (offset, sample) and the end
    private (long Offset, long Sample) Bisect(long target, long offset, long sample)
    {
        long lo = offset;
        long hi = this._source.Length;
        long bestOffset = offset;
        long bestSample = sample;
        while (hi - lo > 2L * this._peekLength)
        {
            long mid = lo + ((hi - lo) / 2);
            if (!this.FindFrame(mid, hi, out long found, out long first))
            {
                hi = mid;
                continue;
            }
            if (first <= target)
            {
                bestOffset = found;
                bestSample = first;
                lo = found + 1;
            }
            else
            {
                hi = mid;
            }
        }
        return (bestOffset, bestSample);
    }

    // The next frame at or after from, before limit, with a valid header whose sample is plausible
    private bool FindFrame(long from, long limit, out long offset, out long sample)
    {
        for (long at = from; at < limit;)
        {
            this._source.Seek(at);
            ReadOnlySpan<byte> window = this._source.Peek(65536);
            if (window.Length < 6)
            {
                break;
            }
            int i = 0;
            for (; i + 1 < window.Length; i++)
            {
                if ((window[i] == 0xFF) && ((window[i + 1] & 0xFE) == 0xF8))
                {
                    this._source.Seek(at + i);
                    ReadOnlySpan<byte> candidate = this._source.Peek(this._peekLength);
                    if (FlacFrame.TryReadHeader(candidate, out FlacFrame.Header header)
                        && (header.Channels == this.Channels) && (header.BlockSize <= this._maxBlockSize))
                    {
                        long first = this.FirstSample(header);
                        if ((this._totalSamples == 0) || (first < this._totalSamples))
                        {
                            offset = at + i;
                            sample = first;
                            return true;
                        }
                    }
                    this._source.Seek(at);
                    window = this._source.Peek(65536);
                }
            }
            at += Math.Max(1, i);
        }
        offset = 0;
        sample = 0;
        return false;
    }
}
