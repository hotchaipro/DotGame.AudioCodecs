// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Buffers.Binary;
using System.IO;

namespace DotGame.AudioCodecs.Ogg;

/// <summary>
/// Writes one logical Ogg stream (RFC 3533): packets are gathered into a page until the caller flushes
/// it, or until one doesn't fit, when the page is flushed first (a packet longer than a page is split
/// across pages, the later ones marked as continuing it).
/// </summary>
internal sealed class OggWriter
{
    private readonly Stream _output;
    private readonly int _serial;
    private readonly byte[] _lacing = new byte[255];
    private byte[] _body = new byte[8192];
    private int _bodyLength;
    private int _segments;
    private int _packetsEnding;       // packets that end on the page being gathered
    private long _granule = -1;       // the last of them's granule position
    private bool _continued;          // the page continues a packet from the last
    private bool _beginning = true;
    private uint _sequence;
    private readonly byte[] _page = new byte[OggReader.MaxPageLength];

    public OggWriter(Stream output, int serial)
    {
        this._output = output;
        this._serial = serial;
    }

    /// <summary>The packets that end on the page being gathered.</summary>
    public int PacketCount => this._packetsEnding;

    /// <summary>
    /// Adds <paramref name="packet"/>, which ends at <paramref name="granule"/>. Flushes first if it
    /// wouldn't fit the page being gathered but would fit an empty one.
    /// </summary>
    public void Add(ReadOnlySpan<byte> packet, long granule)
    {
        int laces = (packet.Length / 255) + 1;
        if ((this._segments + laces > 255) && (laces <= 255) && (this._segments > 0))
        {
            this.Flush(last: false);
        }

        int remaining = packet.Length;
        int at = 0;
        while (true)
        {
            if (this._segments == 255)
            {
                // The packet goes on in the next page
                this.Flush(last: false);
                this._continued = true;
            }
            int lace = Math.Min(remaining, 255);
            this._lacing[this._segments++] = (byte)lace;
            this.Append(packet.Slice(at, lace));
            at += lace;
            remaining -= lace;
            if (lace < 255)
            {
                break;
            }
        }
        this._packetsEnding++;
        this._granule = granule;
    }

    /// <summary>Writes the page being gathered (an empty one if <paramref name="last"/> and there's none).</summary>
    public void Flush(bool last)
    {
        if ((this._segments == 0) && !last)
        {
            return;
        }
        int header = 27 + this._segments;
        int length = header + this._bodyLength;
        Span<byte> page = this._page.AsSpan(0, length);
        "OggS"u8.CopyTo(page);
        page[4] = 0;
        page[5] = (byte)((this._continued ? 1 : 0) | (this._beginning ? 2 : 0) | (last ? 4 : 0));
        BinaryPrimitives.WriteInt64LittleEndian(page[6..], (this._packetsEnding > 0) || last ? this._granule : -1);
        BinaryPrimitives.WriteInt32LittleEndian(page[14..], this._serial);
        BinaryPrimitives.WriteUInt32LittleEndian(page[18..], this._sequence++);
        BinaryPrimitives.WriteUInt32LittleEndian(page[22..], 0);
        page[26] = (byte)this._segments;
        this._lacing.AsSpan(0, this._segments).CopyTo(page[27..]);
        this._body.AsSpan(0, this._bodyLength).CopyTo(page[header..]);
        BinaryPrimitives.WriteUInt32LittleEndian(page[22..], OggCrc.OfPage(page));
        this._output.Write(page);

        this._segments = 0;
        this._bodyLength = 0;
        this._packetsEnding = 0;
        this._continued = false;
        this._beginning = false;
    }

    /// <summary>Sets the granule position of the page being gathered: the end of the stream, for its last page.</summary>
    public void SetGranule(long granule) => this._granule = granule;

    private void Append(ReadOnlySpan<byte> data)
    {
        if (this._bodyLength + data.Length > this._body.Length)
        {
            Array.Resize(ref this._body, Math.Max(this._bodyLength + data.Length, this._body.Length * 2));
        }
        data.CopyTo(this._body.AsSpan(this._bodyLength));
        this._bodyLength += data.Length;
    }
}
