// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.IO;

namespace DotGame.AudioCodecs.Flac;

/// <summary>
/// Encodes FLAC files (RFC 9639): 1 to 8 channels of 4 to 24-bit samples at 1 Hz to 1,048,575 Hz,
/// with flac's settings and defaults (<see cref="FlacEncoderOptions"/>). Our own encoder, not libFLAC:
/// its files decode with <c>flac -d</c> to exactly the input, and at level 8 it's as compact as
/// <c>flac -8</c> or more.
/// </summary>
/// <remarks>
/// <para>
/// Metadata is STREAMINFO, then VORBIS_COMMENT (the vendor string and any comments), then PADDING, as
/// flac writes them; no seek table
/// (the decoders seek without one). Mono and more than two channels are coded independently, stereo with
/// the best decorrelation the level searches for. The same samples and settings always give the same
/// bytes.
/// </para>
/// <para>
/// STREAMINFO holds the totals and the MD5, so the stream must be able to seek:
/// <see cref="AudioEncoder.Complete"/> writes them. A file left without it has zero totals.
/// </para>
/// </remarks>
public sealed class FlacEncoder : AudioEncoder
{
    private readonly FlacFrameEncoder.Settings _settings;
    private readonly long _streamInfo;          // where STREAMINFO's fields start
    private readonly int[][] _blocks;
    private readonly double[][] _windows;
    private readonly FlacFrameEncoder.Scratch _scratch;
    private readonly FlacFrameEncoder.BitWriter _frame;
    private readonly Md5? _md5;
    private readonly byte[] _md5Bytes;
    private int _filled;
    private int _hashed;                  // the block's frames already in the MD5, from the input
    private long _frameNumber;
    private int _minFrame = int.MaxValue;
    private int _maxFrame;

    /// <summary>Starts a FLAC file in <paramref name="stream"/>, at its current position.</summary>
    /// <param name="stream">Where the file goes; it must be able to seek.</param>
    /// <param name="sampleRate">The sample rate, 1 to 1,048,575 Hz.</param>
    /// <param name="channels">The channel count, 1 to 8.</param>
    /// <param name="options">The settings; the default is flac's.</param>
    /// <param name="leaveOpen">Whether disposing the encoder leaves <paramref name="stream"/> open.</param>
    /// <exception cref="ArgumentException"><paramref name="stream"/> can't write or seek, or a comment isn't KEY=value.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The rate, channel count or a setting is out of range.</exception>
    public FlacEncoder(Stream stream, int sampleRate, int channels, FlacEncoderOptions? options = null, bool leaveOpen = false)
        : base(stream, sampleRate, channels, leaveOpen)
    {
        ThrowIfNotSeekable(stream, "FLAC");
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sampleRate, (1 << 20) - 1);
        options ??= FlacEncoderOptions.Default;
        this._settings = options.ToSettings(channels);

        int blockSize = this._settings.BlockSize;
        this._blocks = new int[channels][];
        for (int c = 0; c < channels; c++)
        {
            this._blocks[c] = new int[blockSize];
        }
        this._windows = FlacFrameEncoder.Windows(blockSize, this._settings.Subdivisions);
        this._scratch = new FlacFrameEncoder.Scratch(blockSize, this._settings.MaxLpcOrder, this._settings.MaxPartitionOrder, channels);
        this._frame = new FlacFrameEncoder.BitWriter(64 + (blockSize * channels * 4));
        this._md5 = options.Md5 ? new Md5() : null;
        this._md5Bytes = new byte[blockSize * channels * ((this._settings.Bits + 7) / 8)];

        // fLaC, then STREAMINFO (its fields filled in at the end), the comments and the padding
        bool padding = options.Padding > 0;
        var header = new FlacFrameEncoder.BitWriter(64);
        header.Write(0x664C6143, 32);
        header.Write(0, 1);
        header.Write(0, 7);
        header.Write(34, 24);
        stream.Write(header.Bytes);
        this._streamInfo = stream.Position;
        stream.Write(new byte[34]);
        var comments = new byte[4 + Comment.Length(Comment.FlacVendor, options.Comments)];
        int length = Comment.Write(comments.AsSpan(4), Comment.FlacVendor, options.Comments);
        comments[0] = (byte)(padding ? 4 : 0x84);
        comments[1] = (byte)(length >> 16);
        comments[2] = (byte)(length >> 8);
        comments[3] = (byte)length;
        stream.Write(comments.AsSpan(0, 4 + length));
        if (padding)
        {
            var block = new byte[4 + options.Padding];
            block[0] = 0x81;
            block[1] = (byte)(options.Padding >> 16);
            block[2] = (byte)(options.Padding >> 8);
            block[3] = (byte)options.Padding;
            stream.Write(block);
        }
    }

    private protected override void WriteCore(ReadOnlySpan<short> shorts, ReadOnlySpan<float> floats, bool isFloat)
    {
        int channels = this.Channels;
        int bits = this._settings.Bits;
        int blockSize = this._settings.BlockSize;
        int total = (isFloat ? floats.Length : shorts.Length) / channels;
        for (int done = 0; done < total;)
        {
            int frames = Math.Min(blockSize - this._filled, total - done);

            // 16-bit samples are the MD5's input as they are (little-endian, interleaved)
            bool direct = (this._md5 is not null) && !isFloat && (bits == 16) && BitConverter.IsLittleEndian;
            if (direct)
            {
                if (this._hashed < this._filled)
                {
                    this._md5!.Append(this.SampleBytes(this._hashed, this._filled));
                }
                this._md5!.Append(System.Runtime.InteropServices.MemoryMarshal.AsBytes(shorts.Slice(done * channels, frames * channels)));
                this._hashed = this._filled + frames;
            }
            for (int c = 0; c < channels; c++)
            {
                Span<int> block = this._blocks[c].AsSpan(this._filled, frames);
                int at = (done * channels) + c;
                if (isFloat)
                {
                    for (int i = 0; i < frames; i++, at += channels)
                    {
                        block[i] = Samples.FloatToInt(floats[at], bits);
                    }
                }
                else
                {
                    for (int i = 0; i < frames; i++, at += channels)
                    {
                        block[i] = Samples.ShortToInt(shorts[at], bits);
                    }
                }
            }
            this._filled += frames;
            done += frames;
            if (this._filled == blockSize)
            {
                this.EncodeBlock();
            }
        }
    }

    private protected override void CompleteCore()
    {
        if (this._filled > 0)
        {
            this.EncodeBlock();
        }

        long frames = this.Position;
        var info = new FlacFrameEncoder.BitWriter(34);
        info.Write((uint)this._settings.BlockSize, 16);
        info.Write((uint)this._settings.BlockSize, 16);
        info.Write(frames == 0 ? 0u : (uint)this._minFrame, 24);
        info.Write((uint)this._maxFrame, 24);
        info.Write((uint)this.SampleRate, 20);
        info.Write((uint)(this.Channels - 1), 3);
        info.Write((uint)(this._settings.Bits - 1), 5);
        info.Write((uint)((ulong)frames >> 32), 4);
        info.Write((uint)frames, 32);
        Span<byte> hash = stackalloc byte[16];
        hash.Clear();
        this._md5?.Finish(hash);
        for (int i = 0; i < 16; i += 4)
        {
            info.Write(((uint)hash[i] << 24) | ((uint)hash[i + 1] << 16) | ((uint)hash[i + 2] << 8) | hash[i + 3], 32);
        }

        long end = this.Stream.Position;
        this.Stream.Position = this._streamInfo;
        this.Stream.Write(info.Bytes);
        this.Stream.Position = end;
    }

    private void EncodeBlock()
    {
        int n = this._filled;
        if ((this._frameNumber > (1L << 31) - 1) || (this._frameNumber * this._settings.BlockSize >= (1L << 36)))
        {
            throw new InvalidOperationException("A FLAC file holds at most 2^36 samples.");
        }
        if (this._hashed < n)
        {
            this._md5?.Append(this.SampleBytes(this._hashed, n));
        }
        this._hashed = 0;
        this._frame.Reset();
        double[][] windows = n == this._settings.BlockSize ? this._windows : FlacFrameEncoder.Windows(n, this._settings.Subdivisions);
        FlacFrameEncoder.WriteFrame(this._frame, this._blocks, this.Channels, this.SampleRate, n, this._frameNumber, windows, this._scratch, this._settings);
        ReadOnlySpan<byte> bytes = this._frame.Bytes;
        this.Stream.Write(bytes);
        this._minFrame = Math.Min(this._minFrame, bytes.Length);
        this._maxFrame = Math.Max(this._maxFrame, bytes.Length);
        this._frameNumber++;
        this._filled = 0;
    }

    // The MD5's input: the block's samples from one frame to another, interleaved, little-endian, in
    // whole bytes
    private ReadOnlySpan<byte> SampleBytes(int from, int to)
    {
        int channels = this.Channels;
        int width = (this._settings.Bits + 7) / 8;
        Span<byte> bytes = this._md5Bytes.AsSpan(0, (to - from) * channels * width);
        int at = 0;
        for (int i = from; i < to; i++)
        {
            for (int c = 0; c < channels; c++)
            {
                int sample = this._blocks[c][i];
                for (int b = 0; b < width; b++)
                {
                    bytes[at++] = (byte)(sample >> (8 * b));
                }
            }
        }
        return bytes;
    }
}
