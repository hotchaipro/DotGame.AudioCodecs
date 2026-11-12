// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace DotGame.AudioCodecs.Tests;

/// <summary>A stream over bytes that can't seek, and reads in short, uneven pieces, as a network might.</summary>
internal sealed class UnseekableStream(byte[] data) : Stream
{
    private int _position;
    private int _reads;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int piece = Math.Min(count, Math.Min(data.Length - this._position, 997 + (this._reads++ % 3 * 1500)));
        Array.Copy(data, this._position, buffer, offset, piece);
        this._position += piece;
        return piece;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Ogg pages for tests: packets read without the library, and CRCs made right after an edit.</summary>
internal static class Ogg
{
    /// <summary>Recomputes the CRC of the page at <paramref name="offset"/>.</summary>
    public static void FixCrc(byte[] file, int offset)
    {
        int segments = file[offset + 26];
        int length = 27 + segments;
        for (int i = 0; i < segments; i++)
        {
            length += file[offset + 27 + i];
        }
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(offset + 22), 0);
        uint crc = 0;
        for (int i = 0; i < length; i++)
        {
            crc = (crc << 8) ^ Table[(int)((crc >> 24) ^ file[offset + i])];
        }
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(offset + 22), crc);
    }

    /// <summary>The audio packets of an Ogg Opus file: not the two headers, nor empty ones.</summary>
    public static List<byte[]> Packets(byte[] file, out int channels, out int preSkip)
    {
        var packets = new List<byte[]>();
        var packet = new List<byte>();
        int offset = 0;
        while (offset + 27 <= file.Length)
        {
            int segments = file[offset + 26];
            int body = offset + 27 + segments;
            for (int s = 0; s < segments; s++)
            {
                int lace = file[offset + 27 + s];
                packet.AddRange(file.AsSpan(body, lace));
                body += lace;
                if (lace < 255)
                {
                    packets.Add([.. packet]);
                    packet.Clear();
                }
            }
            offset = body;
        }
        channels = packets[0][9];
        preSkip = BinaryPrimitives.ReadUInt16LittleEndian(packets[0].AsSpan(10));
        packets.RemoveRange(0, 2);
        packets.RemoveAll(p => p.Length == 0);
        return packets;
    }

    private static readonly uint[] Table = MakeTable();

    private static uint[] MakeTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint r = i << 24;
            for (int k = 0; k < 8; k++)
            {
                r = (r & 0x80000000) != 0 ? (r << 1) ^ 0x04C11DB7 : r << 1;
            }
            table[i] = r;
        }
        return table;
    }
}
