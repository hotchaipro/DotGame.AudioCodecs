// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.IO;
using DotGame.AudioCodecs.Flac;
using DotGame.AudioCodecs.Opus;
using DotGame.AudioCodecs.Wav;

namespace DotGame.AudioCodecs;

/// <summary>
/// Encodes an audio file in chunks: each <see cref="Write(ReadOnlySpan{short})"/> adds frames, and
/// <see cref="Complete"/> finishes the file. <see cref="WavEncoder"/>, <see cref="FlacEncoder"/> and
/// <see cref="OpusEncoder"/> write one format each.
/// </summary>
/// <remarks>
/// <para>
/// A frame is one sample of each channel. Samples are interleaved in WAVE channel order (front left,
/// front right, center, LFE, back left, back right, ...) for every format. <c>short</c> samples are
/// 16-bit; <c>float</c> samples are -1 to 1, and are rounded and clipped at ±1 where the format stores
/// integers.
/// </para>
/// <para>
/// <see cref="Complete"/> writes what a file can only have at its end (sizes, totals, checksums, the
/// last page); <see cref="Dispose()"/> without it leaves the file visibly unfinished rather than
/// quietly making it look whole. An encoder isn't thread-safe.
/// </para>
/// </remarks>
public abstract class AudioEncoder : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private bool _completed;
    private bool _disposed;

    private protected AudioEncoder(Stream stream, int sampleRate, int channels, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite)
        {
            throw new ArgumentException("The stream can't be written.", nameof(stream));
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channels, 8);
        this._stream = stream;
        this._leaveOpen = leaveOpen;
        this.SampleRate = sampleRate;
        this.Channels = channels;
    }

    /// <summary>The sample rate of the frames written, in hertz.</summary>
    public int SampleRate { get; }

    /// <summary>The channel count.</summary>
    public int Channels { get; }

    /// <summary>The frames written so far.</summary>
    public long Position { get; private set; }

    /// <summary>The stream the file is written to.</summary>
    private protected Stream Stream => this._stream;

    /// <summary>Encodes <paramref name="samples"/>, whole frames, interleaved.</summary>
    /// <exception cref="ArgumentException"><paramref name="samples"/> isn't a whole number of frames.</exception>
    /// <exception cref="InvalidOperationException">The file is complete.</exception>
    public void Write(ReadOnlySpan<short> samples)
    {
        int frames = this.CheckWrite(samples.Length, nameof(samples));
        this.WriteCore(samples, default, isFloat: false);
        this.Position += frames;
    }

    /// <summary>Encodes <paramref name="samples"/>, whole frames, interleaved, -1 to 1.</summary>
    /// <exception cref="ArgumentException"><paramref name="samples"/> isn't a whole number of frames.</exception>
    /// <exception cref="InvalidOperationException">The file is complete.</exception>
    public void Write(ReadOnlySpan<float> samples)
    {
        int frames = this.CheckWrite(samples.Length, nameof(samples));
        this.WriteCore(default, samples, isFloat: true);
        this.Position += frames;
    }

    /// <summary>
    /// Finishes the file: encodes what's buffered and writes what only the end can have. Nothing can be
    /// written after it. The stream is flushed but not closed.
    /// </summary>
    /// <exception cref="InvalidOperationException">The file is already complete.</exception>
    public void Complete()
    {
        ObjectDisposedException.ThrowIf(this._disposed, this);
        if (this._completed)
        {
            throw new InvalidOperationException("The file is already complete.");
        }
        this._completed = true;
        this.CompleteCore();
        this._stream.Flush();
    }

    /// <summary>
    /// Closes the stream (unless it was opened to be left open). Without <see cref="Complete"/> first,
    /// the file is left unfinished.
    /// </summary>
    public void Dispose()
    {
        this.Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases the stream.</summary>
    /// <param name="disposing">Whether this is <see cref="Dispose()"/>, not a finalizer.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (this._disposed)
        {
            return;
        }
        this._disposed = true;
        if (disposing && !this._leaveOpen)
        {
            this._stream.Dispose();
        }
    }

    /// <summary>Encodes whole frames from <paramref name="shorts"/> (or <paramref name="floats"/>, when <paramref name="isFloat"/>).</summary>
    private protected abstract void WriteCore(ReadOnlySpan<short> shorts, ReadOnlySpan<float> floats, bool isFloat);

    /// <summary>Encodes what's buffered and finishes the file.</summary>
    private protected abstract void CompleteCore();

    /// <summary>Throws unless <paramref name="stream"/> can seek: for formats whose header holds totals.</summary>
    private protected static void ThrowIfNotSeekable(Stream stream, string format)
    {
        if (!stream.CanSeek)
        {
            throw new ArgumentException($"{format} puts its sizes in its header, so the stream must be able to seek.", nameof(stream));
        }
    }

    private int CheckWrite(int length, string paramName)
    {
        ObjectDisposedException.ThrowIf(this._disposed, this);
        if (this._completed)
        {
            throw new InvalidOperationException("The file is complete: nothing more can be written.");
        }
        if (length % this.Channels != 0)
        {
            throw new ArgumentException($"The {length} samples aren't a whole number of {this.Channels}-channel frames.", paramName);
        }
        return length / this.Channels;
    }
}
