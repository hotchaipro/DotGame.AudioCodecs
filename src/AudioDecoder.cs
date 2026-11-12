// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.IO;
using DotGame.AudioCodecs.Flac;
using DotGame.AudioCodecs.Opus;
using DotGame.AudioCodecs.Wav;

namespace DotGame.AudioCodecs;

/// <summary>
/// Decodes an audio file in chunks: each <see cref="Read(Span{short})"/> fills the caller's buffer with
/// the next frames, interleaved, and <see cref="Seek"/> moves to any frame. <see cref="Open(Stream, AudioDecoderOptions, bool)"/>
/// finds the format from the file's first bytes; <see cref="WavDecoder"/>, <see cref="FlacDecoder"/> and
/// <see cref="OpusDecoder"/> open one format.
/// </summary>
/// <remarks>
/// <para>
/// A frame is one sample of each channel. Samples are interleaved in WAVE channel order (front left,
/// front right, center, LFE, back left, back right, ...) for every format. <c>short</c> samples are
/// 16-bit; <c>float</c> samples are -1 to 1.
/// </para>
/// <para>
/// To decode a whole file, read once into a buffer of <see cref="Frames"/> × <see cref="Channels"/>
/// samples. A decoder allocates its buffers when it's created, so reading allocates nothing. Damaged
/// data is an <see cref="InvalidDataException"/>: checksums are checked (unless
/// <see cref="AudioDecoderOptions.SkipChecksums"/>) and nothing carries on past an error. A decoder isn't thread-safe: one thread reads and seeks at a time.
/// </para>
/// </remarks>
public abstract class AudioDecoder : IDisposable
{
    private bool _disposed;

    private protected AudioDecoder()
    {
    }

    /// <summary>The sample rate, in hertz.</summary>
    public int SampleRate { get; private protected set; }

    /// <summary>The channel count.</summary>
    public int Channels { get; private protected set; }

    /// <summary>
    /// The file's length in frames, or <see langword="null"/> while it isn't known: a stream that can't
    /// seek, whose length is known only at its end, or a FLAC file whose header doesn't say.
    /// </summary>
    public abstract long? Frames { get; }

    /// <summary>The frame the next <see cref="Read(Span{short})"/> starts at.</summary>
    public long Position { get; private protected set; }

    /// <summary>Whether <see cref="Seek"/> works: the source is memory or a seekable stream.</summary>
    public abstract bool CanSeek { get; }

    /// <summary>
    /// Opens <paramref name="stream"/> as whichever format its first bytes show: WAV, FLAC or Ogg Opus.
    /// The decoder reads from the stream's current position.
    /// </summary>
    /// <param name="stream">The file. A stream that can't seek can be read but not seeked.</param>
    /// <param name="options">How it reads: whether it checks checksums. <see langword="null"/> is the defaults.</param>
    /// <param name="leaveOpen">Whether disposing the decoder leaves <paramref name="stream"/> open.</param>
    /// <exception cref="InvalidDataException">The data is none of the formats, or is damaged.</exception>
    public static AudioDecoder Open(Stream stream, AudioDecoderOptions? options = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return Open(new StreamByteSource(stream, leaveOpen), options ?? AudioDecoderOptions.Default);
    }

    /// <summary>
    /// Opens <paramref name="data"/> as whichever format its first bytes show: WAV, FLAC or Ogg Opus.
    /// The decoder reads the memory in place, with no copy, so it mustn't change while the decoder is used.
    /// </summary>
    /// <exception cref="InvalidDataException">The data is none of the formats, or is damaged.</exception>
    public static AudioDecoder Open(ReadOnlyMemory<byte> data) => Open(new MemoryByteSource(data), AudioDecoderOptions.Default);

    /// <summary>As <see cref="Open(ReadOnlyMemory{byte})"/>, reading as <paramref name="options"/> say.</summary>
    /// <param name="data">The file.</param>
    /// <param name="options">How it reads: whether it checks checksums. <see langword="null"/> is the defaults.</param>
    /// <exception cref="InvalidDataException">The data is none of the formats, or is damaged.</exception>
    public static AudioDecoder Open(ReadOnlyMemory<byte> data, AudioDecoderOptions? options) =>
        Open(new MemoryByteSource(data), options ?? AudioDecoderOptions.Default);

    private static AudioDecoder Open(ByteSource source, AudioDecoderOptions options)
    {
        try
        {
            ReadOnlySpan<byte> start = source.Peek(36);
            if (WavDecoder.Recognizes(start))
            {
                return new WavDecoder(source);
            }
            if (FlacDecoder.Recognizes(start))
            {
                return new FlacDecoder(source, options);
            }
            if (OpusDecoder.Recognizes(start))
            {
                return new OpusDecoder(source, options);
            }
            throw new InvalidDataException(start.Length < 4
                ? "The data is too short to be audio."
                : $"The data isn't WAV, FLAC or Ogg Opus: it starts with {Describe(start)}.");
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Positions the next read at <paramref name="frame"/>. Seeking gives the samples a read from the
    /// start would give (for Opus, as near as its decoder allows: see <see cref="OpusDecoder"/>).
    /// </summary>
    /// <exception cref="NotSupportedException">The source can't seek (<see cref="CanSeek"/>).</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="frame"/> is negative or after the end.</exception>
    public void Seek(long frame)
    {
        this.ThrowIfDisposed();
        if (!this.CanSeek)
        {
            throw new NotSupportedException("The decoder's source can't seek.");
        }
        ArgumentOutOfRangeException.ThrowIfNegative(frame);
        if (this.Frames is long frames)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(frame, frames);
        }
        this.Position = this.SeekCore(frame);
    }

    /// <summary>
    /// Decodes the next frames into <paramref name="samples"/>, interleaved, filling it unless the file
    /// ends first.
    /// </summary>
    /// <returns>The frames read (samples ÷ <see cref="Channels"/>); 0 at the end.</returns>
    /// <exception cref="ArgumentException"><paramref name="samples"/> isn't a whole number of frames.</exception>
    /// <exception cref="InvalidDataException">The data is damaged.</exception>
    public int Read(Span<short> samples)
    {
        this.ThrowIfDisposed();
        this.ThrowIfPartialFrames(samples.Length, nameof(samples));
        int frames = this.ReadCore(samples, default, isFloat: false);
        this.Position += frames;
        return frames;
    }

    /// <summary>
    /// Decodes the next frames into <paramref name="samples"/>, interleaved, -1 to 1, filling it unless
    /// the file ends first.
    /// </summary>
    /// <returns>The frames read (samples ÷ <see cref="Channels"/>); 0 at the end.</returns>
    /// <exception cref="ArgumentException"><paramref name="samples"/> isn't a whole number of frames.</exception>
    /// <exception cref="InvalidDataException">The data is damaged.</exception>
    public int Read(Span<float> samples)
    {
        this.ThrowIfDisposed();
        this.ThrowIfPartialFrames(samples.Length, nameof(samples));
        int frames = this.ReadCore(default, samples, isFloat: true);
        this.Position += frames;
        return frames;
    }

    /// <summary>Closes the source (unless it was opened to be left open).</summary>
    public void Dispose()
    {
        this.Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases the source.</summary>
    /// <param name="disposing">Whether this is <see cref="Dispose()"/>, not a finalizer.</param>
    protected virtual void Dispose(bool disposing)
    {
        this._disposed = true;
    }

    /// <summary>
    /// Decodes up to the destination's whole frames into <paramref name="shorts"/> (or
    /// <paramref name="floats"/>, when <paramref name="isFloat"/>), filling it unless the file ends.
    /// </summary>
    private protected abstract int ReadCore(Span<short> shorts, Span<float> floats, bool isFloat);

    /// <summary>Seeks to <paramref name="frame"/>, already checked, and returns the new position.</summary>
    private protected abstract long SeekCore(long frame);

    private protected void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(this._disposed, this);

    private void ThrowIfPartialFrames(int length, string paramName)
    {
        if (length % this.Channels != 0)
        {
            throw new ArgumentException($"The buffer's {length} samples aren't a whole number of {this.Channels}-channel frames.", paramName);
        }
    }

    private static string Describe(ReadOnlySpan<byte> start)
    {
        var text = new char[Math.Min(4, start.Length)];
        for (int i = 0; i < text.Length; i++)
        {
            text[i] = start[i] is >= 0x20 and < 0x7F ? (char)start[i] : '.';
        }
        return $"\"{new string(text)}\"";
    }
}
