// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using DotGame.AudioCodecs.Ogg;

namespace DotGame.AudioCodecs.Opus;

/// <summary>
/// Decodes Ogg Opus files (RFC 7845): 1 to 8 channels (channel mapping families 0 and 1), always at
/// 48 kHz, with libopus 1.6.1's decoder, its fixed-point build, sample for sample.
/// </summary>
/// <remarks>
/// <para>
/// The pre-skip is dropped, the end trimmed to the last page's granule position and the header's output
/// gain applied, so the samples are the encoder's input, delayed by nothing. More than two channels come
/// out in WAVE order (as opusdec writes them), mapped from Opus's Vorbis order.
/// </para>
/// <para>
/// A seek finds its page by bisection and starts decoding 400 ms before the target, discarding up to it,
/// so the decoder has converged on what a decode from the start gives: within the decoder's rounding, at
/// 84 dB or better on test signals (RFC 7845 asks for at least 80 ms). Seeking to 0 gives exactly the
/// samples of a decode from the start.
/// </para>
/// </remarks>
public sealed class OpusDecoder : AudioDecoder
{
    /// <summary>Decoded before a seek's target and dropped, so the decoder converges: 400 ms.</summary>
    private const int PreRoll = 19200;

    private const int MaxPacketFrames = 5760;     // 120 ms
    private const int Rate = 48000;

    // Opus's Vorbis order to WAVE order: WAVE channel i is Opus channel Order[n][i]
    private static readonly byte[][] WaveOrder =
    [
        [0], [0, 1], [0, 2, 1], [0, 1, 2, 3], [0, 2, 1, 3, 4], [0, 2, 1, 5, 3, 4], [0, 2, 1, 6, 5, 3, 4], [0, 2, 1, 7, 5, 6, 3, 4],
    ];

    private readonly ByteSource _source;
    private readonly OggReader _reader;
    private readonly OpusDecoderState? _single;
    private readonly OpusMultistreamDecoder? _multi;
    private readonly int _preSkip;
    private readonly int _gain;
    private readonly double _gainScale;        // 10^(gain / 5120): Q7.8 dB as a factor
    private readonly long _dataStart;          // the first audio page
    private readonly int[] _res;               // a packet's samples, opus_res, Opus order
    private readonly int[] _ordered;           // the same in WAVE order (more than two channels)
    private readonly byte[] _order;
    private long _startGranule;                // the granule position of the first audio sample
    private long? _frames;
    private long _cursor;                      // the granule position the next packet starts at
    private long _discard;                     // decoded samples still to drop
    private int _pendingOffset;                // frames of _res not yet read: from here
    private int _pendingCount;
    private bool _ended;

    /// <summary>Opens <paramref name="stream"/>, from its current position, as an Ogg Opus file.</summary>
    /// <param name="stream">The file. A stream that can't seek can be read but not seeked.</param>
    /// <param name="options">How it reads: whether it checks checksums. <see langword="null"/> is the defaults.</param>
    /// <param name="leaveOpen">Whether disposing the decoder leaves <paramref name="stream"/> open.</param>
    /// <exception cref="InvalidDataException">The data isn't an Ogg Opus file this decoder reads, or is damaged.</exception>
    public OpusDecoder(Stream stream, AudioDecoderOptions? options = null, bool leaveOpen = false)
        : this(new StreamByteSource(stream ?? throw new ArgumentNullException(nameof(stream)), leaveOpen), options ?? AudioDecoderOptions.Default)
    {
    }

    /// <summary>Opens <paramref name="data"/> as an Ogg Opus file, read in place.</summary>
    /// <exception cref="InvalidDataException">The data isn't an Ogg Opus file this decoder reads, or is damaged.</exception>
    public OpusDecoder(ReadOnlyMemory<byte> data)
        : this(new MemoryByteSource(data), AudioDecoderOptions.Default)
    {
    }

    /// <summary>Opens <paramref name="data"/> as an Ogg Opus file, read in place, as <paramref name="options"/> say.</summary>
    /// <param name="data">The file.</param>
    /// <param name="options">How it reads: whether it checks checksums. <see langword="null"/> is the defaults.</param>
    /// <exception cref="InvalidDataException">The data isn't an Ogg Opus file this decoder reads, or is damaged.</exception>
    public OpusDecoder(ReadOnlyMemory<byte> data, AudioDecoderOptions? options)
        : this(new MemoryByteSource(data), options ?? AudioDecoderOptions.Default)
    {
    }

    internal OpusDecoder(ByteSource source, AudioDecoderOptions options)
    {
        this._source = source;
        try
        {
            this._reader = new OggReader(source) { CheckCrcs = !options.SkipChecksums };
            if (!this._reader.TryRead(out ReadOnlySpan<byte> head, out _) || (head.Length < 19) || !head[..8].SequenceEqual("OpusHead"u8))
            {
                throw new InvalidDataException("Not an Ogg Opus file: its first packet isn't an OpusHead.");
            }
            if ((head[8] & 0xF0) != 0)
            {
                throw new InvalidDataException($"Ogg Opus version {head[8]} isn't supported.");
            }
            int channels = head[9];
            this._preSkip = BinaryPrimitives.ReadUInt16LittleEndian(head[10..]);
            this._gain = BinaryPrimitives.ReadInt16LittleEndian(head[16..]);
            this._gainScale = Math.Pow(10, this._gain / 5120.0);
            int family = head[18];
            if (family == 0)
            {
                if (channels is < 1 or > 2)
                {
                    throw new InvalidDataException($"The Ogg Opus file has {channels} channels in mapping family 0, which allows 1 or 2.");
                }
                this._single = new OpusDecoderState(Rate, channels);
            }
            else if (family == 1)
            {
                if ((channels is < 1 or > 8) || (head.Length < 21 + channels))
                {
                    throw new InvalidDataException($"The Ogg Opus file has {channels} channels in mapping family 1, which allows 1 to 8.");
                }
                int streams = head[19];
                int coupled = head[20];
                try
                {
                    this._multi = new OpusMultistreamDecoder(Rate, channels, streams, coupled, head.Slice(21, channels));
                }
                catch (ArgumentException e)
                {
                    throw new InvalidDataException("The Ogg Opus file's channel mapping isn't valid: " + e.Message, e);
                }
            }
            else
            {
                throw new InvalidDataException($"Ogg Opus channel mapping family {family} isn't supported: families 0 and 1 are.");
            }

            if (!this._reader.TryRead(out ReadOnlySpan<byte> tags, out _) || !tags.StartsWith("OpusTags"u8))
            {
                throw new InvalidDataException("The Ogg Opus file has no OpusTags header.");
            }
            this.Comments = Comment.Read(tags[8..], "Opus");

            // An Opus packet of 120 ms, eight channels and the most bytes a frame has is far below this
            this._reader.MaxPacketLength = 1 << 20;
            this._dataStart = source.Position;

            this.SampleRate = Rate;
            this.Channels = channels;
            this._res = new int[MaxPacketFrames * channels];
            this._ordered = channels > 2 ? new int[MaxPacketFrames * channels] : this._res;
            this._order = WaveOrder[channels - 1];

            this.FindStart();
            if (source.CanSeek && (source.Length >= 0))
            {
                long end = this.LastGranule();
                if (end < this._startGranule)
                {
                    throw new InvalidDataException("The Ogg Opus file ends before it starts: its last granule position is before its first.");
                }
                this._frames = Math.Max(0, end - this._startGranule - this._preSkip);
            }
            this._reader.SeekToPage(this._dataStart);
            this._cursor = this._startGranule;
            this._discard = this._preSkip;
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    /// <summary>The comments of the OpusTags header, each "KEY=value".</summary>
    public IReadOnlyList<string> Comments { get; }

    /// <inheritdoc/>
    public override long? Frames => this._frames;

    /// <inheritdoc/>
    public override bool CanSeek => this._source.CanSeek;

    internal static bool Recognizes(ReadOnlySpan<byte> start) =>
        (start.Length >= 36) && start[..4].SequenceEqual("OggS"u8) && start[28..36].SequenceEqual("OpusHead"u8);

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
            if (this._frames is long frames)
            {
                count = (int)Math.Min(count, frames - (this.Position + written));
                if (count <= 0)
                {
                    this._pendingCount = 0;
                    break;
                }
            }
            ReadOnlySpan<int> res = this._ordered.AsSpan(this._pendingOffset * channels, count * channels);
            if (isFloat)
            {
                Span<float> output = floats.Slice(written * channels, count * channels);
                if (this._gain == 0)
                {
                    OpusDecoderState.ResToFloat(res, output);
                }
                else
                {
                    // The gain on the decoder's 24-bit samples, as opusfile applies it
                    double scale = this._gainScale / 8388608.0;
                    for (int i = 0; i < output.Length; i++)
                    {
                        output[i] = (float)(res[i] * scale);
                    }
                }
            }
            else
            {
                Span<short> output = shorts.Slice(written * channels, count * channels);
                if (this._gain == 0)
                {
                    OpusDecoderState.ResToInt16(res, output);
                }
                else
                {
                    // The gain on the 24-bit samples, then rounded to 16 bits once and saturated
                    double scale = this._gainScale / 256.0;
                    for (int i = 0; i < output.Length; i++)
                    {
                        output[i] = (short)Math.Clamp(Math.Round(res[i] * scale, MidpointRounding.ToEven), short.MinValue, short.MaxValue);
                    }
                }
            }
            this._pendingOffset += count;
            this._pendingCount -= count;
            written += count;
        }
        if ((written < capacity) && this._ended)
        {
            if (this._frames is not long total)
            {
                throw new InvalidDataException("The Ogg Opus stream ends without its last page.");
            }
            if (this.Position + written < total)
            {
                throw new InvalidDataException("The Ogg Opus file ends before its last granule position.");
            }
        }
        return written;
    }

    private protected override long SeekCore(long frame)
    {
        long target = this._startGranule + this._preSkip + frame;
        long from = frame == 0 ? this._startGranule : Math.Max(this._startGranule, target - PreRoll);

        // The last page whose packets all end at or before the pre-roll's start; from the first audio
        // page when that's the start
        if ((from > this._startGranule) && this.FindPageBefore(from, out long offset, out long granule) && (granule >= this._startGranule))
        {
            this._reader.ResumeAfter(offset);
            this._cursor = granule;
        }
        else
        {
            this._reader.SeekToPage(this._dataStart);
            this._cursor = this._startGranule;
        }
        this._ended = false;
        this._pendingCount = 0;
        this.Reset();

        // Pass over the packets that end before it, then decode from the one that holds it
        while (this._reader.TryRead(out ReadOnlySpan<byte> packet, out bool lastOnPage))
        {
            if (packet.IsEmpty)
            {
                continue;
            }
            int samples = PacketSamples(packet);
            if (this._cursor + samples <= from)
            {
                this._cursor += samples;
                this.CheckGranule(lastOnPage);
                continue;
            }
            this._discard = target - this._cursor;
            this.Decode(packet);
            this.CheckGranule(lastOnPage);
            return frame;
        }
        this._ended = true;
        return frame;
    }

    private void Reset()
    {
        this._single?.ResetState();
        this._multi?.ResetState();
    }

    // Decodes the next packet into the pending frames: false at the end
    private bool DecodeNext()
    {
        while (!this._ended)
        {
            if (!this._reader.TryRead(out ReadOnlySpan<byte> packet, out bool lastOnPage))
            {
                this._ended = true;
                return false;
            }
            if (packet.IsEmpty)
            {
                continue;
            }
            if (this._reader.PageIsLast && (this._frames is null))
            {
                // The last page's granule position is the end: known now for a stream that can't seek
                if (this._reader.PageGranule < 0)
                {
                    throw new InvalidDataException("The Ogg Opus file's last page has no granule position.");
                }
                if (this._reader.PageGranule < this._cursor)
                {
                    // The last page's position trims only its own packets
                    throw new InvalidDataException("The Ogg Opus file's last page ends before the page before it does.");
                }
                this._frames = Math.Max(0, this._reader.PageGranule - this._startGranule - this._preSkip);
            }
            this.Decode(packet);
            this.CheckGranule(lastOnPage);
            if (this._pendingCount > 0)
            {
                return true;
            }
        }
        return false;
    }

    private void Decode(ReadOnlySpan<byte> packet)
    {
        int channels = this.Channels;
        int decoded = this._single is { } single
            ? single.DecodeRes(packet, this._res, MaxPacketFrames)
            : this._multi!.Decode(packet, this._res, MaxPacketFrames);
        this._cursor += decoded;
        if (channels > 2)
        {
            ReadOnlySpan<int> res = this._res.AsSpan(0, decoded * channels);
            Span<int> ordered = this._ordered.AsSpan(0, decoded * channels);
            ReadOnlySpan<byte> order = this._order;
            for (int f = 0; f < decoded; f++)
            {
                int at = f * channels;
                for (int c = 0; c < channels; c++)
                {
                    ordered[at + c] = res[at + order[c]];
                }
            }
        }
        int drop = (int)Math.Min(this._discard, decoded);
        this._discard -= drop;
        this._pendingOffset = drop;
        this._pendingCount = decoded - drop;
    }

    // A page's granule position is where its last packet ends (but on the last page, which may trim
    // the end): one that isn't is damage, which would send a seek somewhere a read from the start
    // doesn't go
    private void CheckGranule(bool lastOnPage)
    {
        if (lastOnPage && !this._reader.PageIsLast && (this._reader.PageGranule >= 0) && (this._reader.PageGranule != this._cursor))
        {
            throw new InvalidDataException($"The Ogg Opus page at byte {this._reader.PageOffset} says its packets end at {this._reader.PageGranule}; they end at {this._cursor}.");
        }
    }

    private static int PacketSamples(ReadOnlySpan<byte> packet)
    {
        int samples = OpusDecoderState.opus_packet_get_nb_samples(packet, packet.Length, Rate);
        if (samples <= 0)
        {
            throw new InvalidDataException("The Ogg Opus file has a packet that isn't Opus.");
        }
        return samples;
    }

    // The first audio sample's granule position: the first audio page's, less the samples of the
    // packets that end on it (RFC 7845, 4.5), read without consuming the page
    private void FindStart()
    {
        ReadOnlySpan<byte> header = this._source.Peek(27);
        if ((header.Length < 27) || !header[..4].SequenceEqual("OggS"u8))
        {
            this._startGranule = 0;
            return;
        }
        int segments = header[26];
        ReadOnlySpan<byte> page = this._source.Peek(27 + segments);
        if (page.Length < 27 + segments)
        {
            this._startGranule = 0;
            return;
        }
        int length = 27 + segments;
        for (int i = 0; i < segments; i++)
        {
            length += page[27 + i];
        }
        page = this._source.Peek(length);
        long granule = BinaryPrimitives.ReadInt64LittleEndian(page[6..]);
        if ((page.Length < length) || (granule < 0) || ((page[5] & 1) != 0))
        {
            this._startGranule = 0;
            return;
        }
        long samples = 0;
        int at = 27 + segments;
        int packetStart = at;
        for (int i = 0; i < segments; i++)
        {
            int lace = page[27 + i];
            at += lace;
            if (lace < 255)
            {
                if (at > packetStart)
                {
                    samples += PacketSamples(page[packetStart..at]);
                }
                packetStart = at;
            }
        }
        if (granule < samples)
        {
            // On the last page too, the granule position trims the end (RFC 7845, 4.5): the stream starts at 0
            if ((page[5] & 4) == 0)
            {
                throw new InvalidDataException("The Ogg Opus file's first audio page ends before its own samples.");
            }
            this._startGranule = 0;
            return;
        }
        this._startGranule = granule - samples;
    }
    // The granule position of the stream's last page: the one marked as the end of the stream, as a
    // read from the start ends there (what follows it is ignored)
    private long LastGranule()
    {
        long length = this._source.Length;
        for (long window = 65536; ; window *= 4)
        {
            long from = Math.Max(this._dataStart, length - window);
            long at = from;
            long previous = -1;
            while (this._reader.FindPage(at, length, out long offset, out int pageLength, out long granule, out bool last))
            {
                if (last)
                {
                    if (granule < 0)
                    {
                        throw new InvalidDataException("The Ogg Opus file's last page has no granule position.");
                    }
                    if (granule < previous)
                    {
                        // The last page's position trims only its own packets
                        throw new InvalidDataException("The Ogg Opus file's last page ends before the page before it does.");
                    }
                    return granule;
                }
                if (granule >= 0)
                {
                    previous = granule;
                }
                at = offset + pageLength;
            }
            if (from == this._dataStart)
            {
                throw new InvalidDataException("The Ogg Opus file has no last page: it's cut short.");
            }
        }
    }

    // Bisection for the last audio page whose granule position is at most target
    private bool FindPageBefore(long target, out long bestOffset, out long bestGranule)
    {
        bestOffset = -1;
        bestGranule = -1;
        long lo = this._dataStart;
        long hi = this._source.Length;
        while (hi - lo > 65536)
        {
            long mid = lo + ((hi - lo) / 2);
            if (!this.FindGranulePage(mid, hi, out long offset, out int length, out long granule))
            {
                hi = mid;
                continue;
            }
            if (granule <= target)
            {
                bestOffset = offset;
                bestGranule = granule;
                lo = offset + length;
            }
            else
            {
                hi = mid;
            }
        }
        for (long at = lo; this.FindGranulePage(at, hi, out long offset, out int length, out long granule); at = offset + length)
        {
            if (granule > target)
            {
                break;
            }
            bestOffset = offset;
            bestGranule = granule;
        }
        return bestOffset >= 0;
    }

    private bool FindGranulePage(long from, long limit, out long offset, out int length, out long granule)
    {
        while (this._reader.FindPage(from, limit, out offset, out length, out granule))
        {
            if (granule >= 0)
            {
                return true;
            }
            from = offset + length;
        }
        return false;
    }
}
