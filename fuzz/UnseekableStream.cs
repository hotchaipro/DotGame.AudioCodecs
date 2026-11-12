// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.IO;

namespace DotGame.AudioCodecs.Fuzz;

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
