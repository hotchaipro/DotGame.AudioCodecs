// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Buffers.Binary;
using System.IO;

namespace DotGame.AudioCodecs.Ogg;

/// <summary>
/// Reads the packets of an Ogg file's first logical stream (RFC 3533), page by page, checking each page's
/// CRC; other streams' pages are skipped. A packet within one page is a slice of it; one that spans pages
/// is joined in a buffer the reader reuses.
/// </summary>
/// <remarks>
/// Reading is strict: a page that isn't where the last one ended, fails its CRC, or skips a sequence
/// number of the stream is an <see cref="InvalidDataException"/>. <see cref="FindPage(long, long, out long, out int, out long)"/> resynchronizes
/// after a seek, finding the next page whose CRC is right.
/// </remarks>
internal sealed class OggReader
{
    /// <summary>The longest page: the header, 255 lacing values, 255 segments of 255 bytes.</summary>
    public const int MaxPageLength = 27 + 255 + (255 * 255);

    private readonly ByteSource _source;
    private readonly byte[] _page = new byte[MaxPageLength];
    private byte[] _joined = new byte[1024];
    private int _joinedLength;
    private bool _joining;            // a packet continues from the last page
    private int _segments;            // the current page's lacing values
    private int _segment;             // the next one to read
    private int _body;                // where its data starts in _page
    private bool _havePage;
    private bool _serialKnown;
    private uint _sequence;           // the current page's sequence number
    private bool _endChecked;         // nothing of the stream follows its last page

    public OggReader(ByteSource source)
    {
        this._source = source;
    }

    /// <summary>
    /// The longest packet the reader joins from pages; a longer one is an <see cref="InvalidDataException"/>,
    /// so a stream of pages that never end a packet can't take all the memory. 16 MB at first, for headers
    /// with large comments; the caller lowers it for audio.
    /// </summary>
    public int MaxPacketLength { get; set; } = 16 << 20;

    /// <summary>
    /// Whether the pages read in order have their CRCs checked (<see cref="AudioDecoderOptions.SkipChecksums"/>);
    /// the pages <see cref="FindPage(long, long, out long, out int, out long)"/> finds always do, as that's
    /// how a page is told from data that happens to hold its capture pattern.
    /// </summary>
    public bool CheckCrcs { get; init; } = true;

    /// <summary>The stream's serial number, once its first page is read.</summary>
    public int Serial { get; private set; }

    /// <summary>The current page's granule position: -1 when no packet ends on it.</summary>
    public long PageGranule { get; private set; } = -1;

    /// <summary>Whether the current page is the stream's last.</summary>
    public bool PageIsLast { get; private set; }

    /// <summary>Where the current page starts in the source.</summary>
    public long PageOffset { get; private set; }

    /// <summary>Whether the current page's last packet is complete on it (its granule is that packet's end).</summary>
    public bool PageEndsPacket => (this._segments == 0) || (this._page[27 + this._segments - 1] < 255);

    /// <summary>
    /// The next packet of the stream, and whether it's the last to end on the current page; false at the
    /// end of the stream. The span is valid until the next call.
    /// </summary>
    public bool TryRead(out ReadOnlySpan<byte> packet, out bool lastOnPage)
    {
        while (true)
        {
            if (!this._havePage || (this._segment == this._segments))
            {
                if (this._havePage && this.PageIsLast)
                {
                    break;
                }
                if (!this.NextPage())
                {
                    break;
                }
                continue;
            }

            int start = this._body;
            int length = 0;
            int lace;
            do
            {
                lace = this._page[27 + this._segment];
                this._segment++;
                length += lace;
            }
            while ((lace == 255) && (this._segment < this._segments));
            this._body += length;

            if (lace == 255)
            {
                // Continues on the next page
                this.Append(this._page.AsSpan(start, length));
                this._joining = true;
                continue;
            }

            lastOnPage = this.IsLastEndingHere();
            if (this._joining)
            {
                this.Append(this._page.AsSpan(start, length));
                this._joining = false;
                packet = this._joined.AsSpan(0, this._joinedLength);
                this._joinedLength = 0;
                return true;
            }
            packet = this._page.AsSpan(start, length);
            return true;
        }

        if (this._joining)
        {
            throw new InvalidDataException("The Ogg stream ends in the middle of a packet.");
        }
        packet = default;
        lastOnPage = false;
        return false;
    }

    /// <summary>
    /// Moves to the page at <paramref name="offset"/> (one <see cref="FindPage(long, long, out long, out int, out long)"/> found) and reads it,
    /// dropping the packets that end on it, so the next packet is the one that starts at its granule.
    /// </summary>
    public void ResumeAfter(long offset)
    {
        this._source.Seek(offset);
        this._havePage = false;
        this._joining = false;
        this._joinedLength = 0;
        if (!this.NextPage(expectContinuation: false))
        {
            throw new InvalidDataException("The Ogg page to resume after can't be read.");
        }
        // Skip the packets that end here; the last segments may begin the next packet
        while (this._segment < this._segments)
        {
            int start = this._body;
            int length = 0;
            int lace;
            do
            {
                lace = this._page[27 + this._segment];
                this._segment++;
                length += lace;
            }
            while ((lace == 255) && (this._segment < this._segments));
            this._body += length;
            if (lace == 255)
            {
                this.Append(this._page.AsSpan(start, length));
                this._joining = true;
            }
        }
    }

    /// <summary>Moves to <paramref name="offset"/>, a page of the stream whose packets are read next.</summary>
    public void SeekToPage(long offset)
    {
        this._source.Seek(offset);
        this._havePage = false;
        this._joining = false;
        this._joinedLength = 0;
    }

    /// <summary>
    /// The first page of the stream that starts at or after <paramref name="from"/> and before
    /// <paramref name="limit"/>, found by its capture pattern and checked by its CRC: its offset, length
    /// and granule position. False if there's none.
    /// </summary>
    public bool FindPage(long from, long limit, out long offset, out int length, out long granule) =>
        this.FindPage(from, limit, out offset, out length, out granule, out _);

    /// <summary>
    /// As <see cref="FindPage(long, long, out long, out int, out long)"/>, and whether the page found is
    /// the stream's last (its end-of-stream flag).
    /// </summary>
    public bool FindPage(long from, long limit, out long offset, out int length, out long granule, out bool last)
    {
        last = false;
        long at = from;
        while (at < limit)
        {
            this._source.Seek(at);
            ReadOnlySpan<byte> window = this._source.Peek(MaxPageLength + 4);
            int found = window.IndexOf("OggS"u8);
            if (found < 0)
            {
                if (window.Length < 4)
                {
                    break;
                }
                at += window.Length - 3;
                continue;
            }
            at += found;
            if ((at < limit) && TryParse(window[found..], true, out length, out granule, out int serial, out byte flags, out _)
                && (!this._serialKnown || (serial == this.Serial)))
            {
                offset = at;
                last = (flags & 4) != 0;
                return true;
            }
            at++;
        }
        offset = 0;
        length = 0;
        granule = -1;
        return false;
    }

    // After the last page: another page of the stream, anywhere after it, is damage (a stream ends
    // once, so the end is the same wherever it's looked for); other streams' pages (a chained file's
    // next link) and bytes that aren't pages are passed over
    private void ThrowIfPagesFollow()
    {
        while (true)
        {
            ReadOnlySpan<byte> window = this._source.Peek(MaxPageLength + 4);
            if (window.Length < 27)
            {
                this._source.Advance(window.Length);
                return;
            }
            int found = window.IndexOf("OggS"u8);
            if (found < 0)
            {
                this._source.Advance(window.Length - 3);
                continue;
            }
            if (TryParse(window[found..], true, out int length, out _, out int serial, out _, out _))
            {
                if (serial == this.Serial)
                {
                    throw new InvalidDataException($"The Ogg stream goes on after its last page, at byte {this._source.Position + found}.");
                }
                this._source.Advance(found + length);
            }
            else
            {
                this._source.Advance(found + 1);
            }
        }
    }

    // Reads the stream's next page at the source's position, skipping other streams'
    private bool NextPage(bool expectContinuation = true)
    {
        while (true)
        {
            long offset = this._source.Position;
            ReadOnlySpan<byte> start = this._source.Peek(27);
            if (start.IsEmpty)
            {
                return false;
            }
            if ((start.Length < 27) || !start[..4].SequenceEqual("OggS"u8))
            {
                throw new InvalidDataException($"The Ogg file has no page at byte {offset}.");
            }
            int header = 27 + start[26];
            ReadOnlySpan<byte> lacing = this._source.Peek(header);
            if (lacing.Length < header)
            {
                throw new InvalidDataException($"The Ogg page at byte {offset} is cut short.");
            }
            int length = header;
            for (int i = 27; i < header; i++)
            {
                length += lacing[i];
            }
            ReadOnlySpan<byte> page = this._source.Peek(length);
            if (!TryParse(page, this.CheckCrcs, out _, out long granule, out int serial, out byte flags, out uint sequence))
            {
                throw new InvalidDataException(page.Length < length
                    ? $"The Ogg page at byte {offset} is cut short."
                    : $"The Ogg page at byte {offset} is damaged: its CRC is wrong.");
            }
            page.CopyTo(this._page);
            this._source.Advance(length);

            if (!this._serialKnown)
            {
                this.Serial = serial;
                this._serialKnown = true;
            }
            else if (serial != this.Serial)
            {
                continue;
            }

            bool continued = (flags & 1) != 0;
            if (expectContinuation && this._havePage && (sequence != this._sequence + 1))
            {
                throw new InvalidDataException($"The Ogg stream skips from page {this._sequence} to {sequence}: a page is missing.");
            }
            if (expectContinuation && (continued != this._joining))
            {
                throw new InvalidDataException(continued
                    ? $"The Ogg page at byte {offset} continues a packet that wasn't started."
                    : $"The Ogg page at byte {offset} drops the end of a packet.");
            }
            if (!expectContinuation && continued)
            {
                // Resuming: the continued part belongs to a packet already passed
                this.SkipContinuation();
            }
            else
            {
                this._segment = 0;
                this._body = 27 + this._page[26];
            }
            this._segments = this._page[26];
            this._sequence = sequence;
            this.PageGranule = granule;
            this.PageIsLast = (flags & 4) != 0;
            this.PageOffset = offset;
            this._havePage = true;
            if (this.PageIsLast && !this._endChecked)
            {
                // As soon as the last page is read, so a decoder that stops at its last sample still sees it
                this.ThrowIfPagesFollow();
                this._endChecked = true;
            }
            return true;
        }
    }

    // On a page that starts by continuing a packet, moves past that packet's segments
    private void SkipContinuation()
    {
        int segments = this._page[26];
        int body = 27 + segments;
        int segment = 0;
        while (segment < segments)
        {
            int lace = this._page[27 + segment];
            segment++;
            body += lace;
            if (lace < 255)
            {
                break;
            }
        }
        this._segment = segment;
        this._body = body;
    }

    private bool IsLastEndingHere()
    {
        for (int i = this._segment; i < this._segments; i++)
        {
            if (this._page[27 + i] < 255)
            {
                return false;
            }
        }
        return true;
    }

    // Parses a page header at the start of data and checks the whole page is there and (with checkCrc)
    // its CRC right
    private static bool TryParse(ReadOnlySpan<byte> data, bool checkCrc, out int length, out long granule, out int serial, out byte flags, out uint sequence)
    {
        length = 0;
        granule = -1;
        serial = 0;
        flags = 0;
        sequence = 0;
        if ((data.Length < 27) || !data[..4].SequenceEqual("OggS"u8) || (data[4] != 0) || ((data[5] & ~7) != 0))
        {
            return false;
        }
        int segments = data[26];
        if (data.Length < 27 + segments)
        {
            return false;
        }
        length = 27 + segments;
        foreach (byte lace in data.Slice(27, segments))
        {
            length += lace;
        }
        if (data.Length < length)
        {
            return false;
        }
        if (checkCrc && (OggCrc.OfPage(data[..length]) != BinaryPrimitives.ReadUInt32LittleEndian(data[22..])))
        {
            return false;
        }
        flags = data[5];
        granule = BinaryPrimitives.ReadInt64LittleEndian(data[6..]);
        if (granule < -1)
        {
            // -1 is "no packet ends here"; any other negative position isn't a page's
            return false;
        }
        serial = BinaryPrimitives.ReadInt32LittleEndian(data[14..]);
        sequence = BinaryPrimitives.ReadUInt32LittleEndian(data[18..]);
        return true;
    }

    private void Append(ReadOnlySpan<byte> data)
    {
        if (this._joinedLength + data.Length > this.MaxPacketLength)
        {
            throw new InvalidDataException($"An Ogg packet runs past {this.MaxPacketLength} bytes.");
        }
        if (this._joinedLength + data.Length > this._joined.Length)
        {
            Array.Resize(ref this._joined, Math.Max(this._joinedLength + data.Length, this._joined.Length * 2));
        }
        data.CopyTo(this._joined.AsSpan(this._joinedLength));
        this._joinedLength += data.Length;
    }
}
