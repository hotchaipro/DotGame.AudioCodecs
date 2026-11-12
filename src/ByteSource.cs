// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.IO;

namespace DotGame.AudioCodecs;

/// <summary>
/// The bytes a decoder reads: memory, read in place, or a stream, through a buffer. Positions count
/// from where the source started (a stream's position when the decoder was opened).
/// </summary>
internal abstract class ByteSource : IDisposable
{
    /// <summary>The position of the next byte <see cref="Peek"/> returns.</summary>
    public abstract long Position { get; }

    /// <summary>The length, or -1 when it isn't known (a stream that can't seek).</summary>
    public abstract long Length { get; }

    /// <summary>Whether <see cref="Seek"/> works.</summary>
    public abstract bool CanSeek { get; }

    /// <summary>
    /// The next <paramref name="count"/> bytes, without consuming them: fewer only at the end. The span
    /// is valid until the next call.
    /// </summary>
    public abstract ReadOnlySpan<byte> Peek(int count);

    /// <summary>Consumes <paramref name="count"/> bytes, which must have been peeked.</summary>
    public abstract void Advance(int count);

    /// <summary>Moves to <paramref name="position"/> (at most the length).</summary>
    public abstract void Seek(long position);

    /// <summary>Consumes <paramref name="count"/> bytes, peeked or not; false if the source ends first.</summary>
    public virtual bool Skip(long count)
    {
        while (count > 0)
        {
            int chunk = (int)Math.Min(count, 65536);
            int got = this.Peek(chunk).Length;
            if (got == 0)
            {
                return false;
            }
            this.Advance(got);
            count -= got;
        }
        return true;
    }

    /// <summary>Copies the next <paramref name="destination"/>.Length bytes; throws if the source ends first.</summary>
    public void ReadExactly(Span<byte> destination, string what)
    {
        while (!destination.IsEmpty)
        {
            ReadOnlySpan<byte> got = this.Peek(Math.Min(destination.Length, 65536));
            if (got.IsEmpty)
            {
                throw new InvalidDataException($"The file ends in its {what}.");
            }
            got.CopyTo(destination);
            this.Advance(got.Length);
            destination = destination[got.Length..];
        }
    }

    public virtual void Dispose()
    {
    }
}

/// <summary>Memory, read in place.</summary>
internal sealed class MemoryByteSource(ReadOnlyMemory<byte> data) : ByteSource
{
    private int _position;

    public override long Position => this._position;

    public override long Length => data.Length;

    public override bool CanSeek => true;

    public override ReadOnlySpan<byte> Peek(int count) =>
        data.Span.Slice(this._position, Math.Min(count, data.Length - this._position));

    public override void Advance(int count) => this._position += count;

    public override void Seek(long position) => this._position = (int)Math.Clamp(position, 0, data.Length);

    public override bool Skip(long count)
    {
        long target = this._position + count;
        this._position = (int)Math.Min(target, data.Length);
        return target <= data.Length;
    }
}

/// <summary>A stream, through a buffer that grows to the largest peek.</summary>
internal sealed class StreamByteSource : ByteSource
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly long _origin;
    private byte[] _buffer = new byte[65536];
    private int _start;           // the next byte in _buffer
    private int _end;             // the end of the bytes read into it
    private long _bufferPosition; // the source position of _buffer[_start]
    private bool _ended;

    public StreamByteSource(Stream stream, bool leaveOpen)
    {
        if (!stream.CanRead)
        {
            throw new ArgumentException("The stream can't be read.", nameof(stream));
        }
        this._stream = stream;
        this._leaveOpen = leaveOpen;
        this._origin = stream.CanSeek ? stream.Position : 0;
    }

    public override long Position => this._bufferPosition;

    public override long Length => this._stream.CanSeek ? this._stream.Length - this._origin : -1;

    public override bool CanSeek => this._stream.CanSeek;

    public override ReadOnlySpan<byte> Peek(int count)
    {
        if ((this._end - this._start < count) && !this._ended)
        {
            this.Fill(count);
        }
        return this._buffer.AsSpan(this._start, Math.Min(count, this._end - this._start));
    }

    public override void Advance(int count)
    {
        this._start += count;
        this._bufferPosition += count;
    }

    public override void Seek(long position)
    {
        long buffered = this._bufferPosition - this._start;
        if ((position >= buffered) && (position <= buffered + this._end))
        {
            // Within what's buffered
            this._start = (int)(position - buffered);
            this._bufferPosition = position;
            return;
        }
        this._stream.Position = this._origin + Math.Clamp(position, 0, this.Length);
        this._bufferPosition = this._stream.Position - this._origin;
        this._start = 0;
        this._end = 0;
        this._ended = false;
    }

    public override bool Skip(long count)
    {
        if (this._stream.CanSeek && (count > this._end - this._start))
        {
            long target = this._bufferPosition + count;
            this.Seek(Math.Min(target, this.Length));
            return target <= this.Length;
        }
        return base.Skip(count);
    }

    public override void Dispose()
    {
        if (!this._leaveOpen)
        {
            this._stream.Dispose();
        }
        base.Dispose();
    }

    private void Fill(int count)
    {
        // Move what's left to the front, growing the buffer to hold the peek
        int left = this._end - this._start;
        if (count > this._buffer.Length)
        {
            var grown = new byte[Math.Max(count, this._buffer.Length * 2)];
            this._buffer.AsSpan(this._start, left).CopyTo(grown);
            this._buffer = grown;
        }
        else if (this._start > 0)
        {
            this._buffer.AsSpan(this._start, left).CopyTo(this._buffer);
        }
        this._start = 0;
        this._end = left;
        while (this._end < count)
        {
            int read = this._stream.Read(this._buffer, this._end, this._buffer.Length - this._end);
            if (read == 0)
            {
                this._ended = true;
                return;
            }
            this._end += read;
        }
    }
}
