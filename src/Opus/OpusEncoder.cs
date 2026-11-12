// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using DotGame.AudioCodecs.Ogg;

namespace DotGame.AudioCodecs.Opus;

/// <summary>
/// Encodes Ogg Opus files (RFC 7845) with libopus 1.6.1's encoder, its fixed-point build, packet for
/// packet: 1 to 8 channels (mapping family 0 for one or two, family 1 above, as opusenc), from input at
/// any rate.
/// </summary>
/// <remarks>
/// <para>
/// The settings are libopus's and the file's own (<see cref="OpusEncoderOptions"/>, <see cref="OpusFileOptions"/>),
/// with opusenc's defaults. Input at 8, 12, 16, 24 or 48 kHz, libopus's rates, is coded as it is; any
/// other rate is resampled to 48 kHz first, as opusenc does (a Kaiser-windowed sinc filter, with no
/// delay), and the header records the input's rate. More than two channels are taken in WAVE order and
/// coded in Opus's Vorbis order, as opusenc does.
/// </para>
/// <para>
/// The pre-skip is the encoder's lookahead. The end is padded as libopusenc pads it, so every input
/// sample comes out of a decoder and the last frame codes no click: the input extended by linear
/// prediction for 120 samples, faded out, then silence; the last page's granule position trims the
/// padding off again.
/// </para>
/// <para>
/// At libopus's rates the first <see cref="AudioEncoder.Write(ReadOnlySpan{short})"/> sets the input's
/// depth: <c>short</c> samples are 16-bit (libopus's <c>opus_encode</c>), <c>float</c> ones 24-bit
/// (<c>opus_encode_float</c>), so on CoreCLR the packets are libopus's byte for byte; later writes of the
/// other type are converted to it. Resampled input is coded as <c>float</c>. Pages go to the stream as
/// they fill, so it needn't seek.
/// </para>
/// </remarks>
public sealed class OpusEncoder : AudioEncoder
{
    // WAVE order to Opus's Vorbis order: WAVE channel i is Opus channel Order[n][i]
    private static readonly byte[][] OpusOrder =
    [
        [0], [0, 1], [0, 2, 1], [0, 1, 2, 3], [0, 2, 1, 3, 4], [0, 2, 1, 5, 3, 4], [0, 2, 1, 6, 5, 3, 4], [0, 2, 1, 7, 5, 6, 3, 4],
    ];

    private readonly OpusEncoderState? _single;
    private readonly OpusMultistreamEncoder? _multi;
    private readonly OggWriter _ogg;
    private readonly int _frameSize;           // at the coding rate
    private readonly int _granuleScale;        // 48 kHz samples per coded sample
    private readonly int _preSkip;             // in 48 kHz samples
    private readonly long _maxDelay;           // in 48 kHz samples
    private readonly byte[] _order;
    private readonly byte[] _packet;           // a packet's most, as libopusenc allows: (1277 × 6 + 2) bytes a stream
    private readonly float[] _history;         // the last input frames, Opus order, for the end extension
    private readonly Resampler? _resampler;    // to 48 kHz, for rates libopus doesn't code
    private readonly Resampler.OutputHandler? _onResampled;
    private readonly float[]? _reordered;      // input in Opus order, for the resampler
    private int _historyCount;
    private short[]? _shortFrame;
    private float[]? _floatFrame;
    private int _filled;                       // frames in the frame buffer
    private long _granule;                     // the end of the packets encoded, 48 kHz
    private long _pageStart;                   // the start of the page's first packet

    /// <summary>Starts an Ogg Opus file in <paramref name="stream"/>.</summary>
    /// <param name="stream">Where the file goes.</param>
    /// <param name="sampleRate">The input's rate, 1 Hz to 768 kHz; other than 8, 12, 16, 24 or 48 kHz, it's resampled to 48 kHz.</param>
    /// <param name="channels">The channel count, 1 to 8.</param>
    /// <param name="options">The encoder's settings; the default is opusenc's.</param>
    /// <param name="fileOptions">The file's settings: serial number, page delay, comments.</param>
    /// <param name="leaveOpen">Whether disposing the encoder leaves <paramref name="stream"/> open.</param>
    /// <exception cref="ArgumentException"><paramref name="stream"/> can't be written, or a comment isn't KEY=value.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The rate, channel count or a setting is out of range.</exception>
    public OpusEncoder(Stream stream, int sampleRate, int channels, OpusEncoderOptions? options = null,
        OpusFileOptions? fileOptions = null, bool leaveOpen = false)
        : base(stream, sampleRate, channels, leaveOpen)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sampleRate, 768000);
        options ??= OpusEncoderOptions.Default;
        fileOptions ??= OpusFileOptions.Default;
        options.Validate(channels, nameof(options));
        fileOptions.Validate(nameof(fileOptions));

        bool native = sampleRate is 8000 or 12000 or 16000 or 24000 or 48000;
        int codingRate = native ? sampleRate : 48000;
        this._frameSize = options.FrameSize(codingRate);
        this._granuleScale = 48000 / codingRate;
        this._order = OpusOrder[channels - 1];
        this._maxDelay = (long)Math.Round(fileOptions.MaxPageDelay.TotalSeconds * 48000);
        if (!native)
        {
            this._resampler = new Resampler(channels, sampleRate, 48000);
            this._onResampled = this.TakeResampled;
            this._reordered = new float[1024 * channels];
            this._floatFrame = new float[this._frameSize * channels];
        }

        int lookahead;
        Span<byte> head = stackalloc byte[21 + 8];
        int headLength;
        if (channels <= 2)
        {
            this._single = OpusPacketEncoder.Create(codingRate, channels, options, options.BitrateFor(1, channels - 1, sampleRate, channels));
            lookahead = this._single.GetLookahead();
            headLength = 19;
        }
        else
        {
            this._multi = new OpusMultistreamEncoder(codingRate, channels, options, sampleRate);
            lookahead = this._multi.GetLookahead();
            headLength = 21 + channels;
            head[19] = (byte)this._multi.Streams;
            head[20] = (byte)this._multi.CoupledStreams;
            this._multi.Mapping.CopyTo(head[21..]);
        }
        this._preSkip = lookahead * this._granuleScale;
        int streams = this._multi?.Streams ?? 1;
        this._packet = new byte[((1277 * 6) + 2) * streams];
        this._history = new float[EndExtension.History * channels];

        // RFC 7845, 5.1: version 1, the channels, the pre-skip, the input's rate, no gain, the family
        "OpusHead"u8.CopyTo(head);
        head[8] = 1;
        head[9] = (byte)channels;
        BinaryPrimitives.WriteUInt16LittleEndian(head[10..], (ushort)this._preSkip);
        BinaryPrimitives.WriteUInt32LittleEndian(head[12..], (uint)sampleRate);
        BinaryPrimitives.WriteInt16LittleEndian(head[16..], 0);
        head[18] = (byte)(channels <= 2 ? 0 : 1);

        this._ogg = new OggWriter(stream, fileOptions.Serial);
        this._ogg.Add(head[..headLength], 0);
        this._ogg.Flush(last: false);

        // 5.2: the vendor string and the comments, on pages of their own
        var tags = new byte[8 + Comment.Length(Comment.OpusVendor, fileOptions.Comments)];
        "OpusTags"u8.CopyTo(tags);
        Comment.Write(tags.AsSpan(8), Comment.OpusVendor, fileOptions.Comments);
        this._ogg.Add(tags, 0);
        this._ogg.Flush(last: false);
    }

    private protected override void WriteCore(ReadOnlySpan<short> shorts, ReadOnlySpan<float> floats, bool isFloat)
    {
        int channels = this.Channels;
        int total = (isFloat ? floats.Length : shorts.Length) / channels;
        if (this._resampler is { } resampler)
        {
            // In Opus order, kept for the end extension, then through the resampler a piece at a time
            for (int done = 0; done < total;)
            {
                int frames = Math.Min(1024, total - done);
                Span<float> reordered = this._reordered.AsSpan(0, frames * channels);
                ReadOnlySpan<byte> order = this._order;
                for (int f = 0; f < frames; f++)
                {
                    int input = (done + f) * channels;
                    for (int c = 0; c < channels; c++)
                    {
                        reordered[(f * channels) + order[c]] = isFloat ? floats[input + c] : Samples.ShortToFloat(shorts[input + c]);
                    }
                }
                this.Remember(reordered);
                resampler.Write(reordered, this._onResampled!);
                done += frames;
            }
            return;
        }

        if ((this._shortFrame is null) && (this._floatFrame is null))
        {
            if (isFloat)
            {
                this._floatFrame = new float[this._frameSize * channels];
            }
            else
            {
                this._shortFrame = new short[this._frameSize * channels];
            }
        }
        for (int done = 0; done < total;)
        {
            int frames = Math.Min(this._frameSize - this._filled, total - done);
            this.Copy(shorts, floats, isFloat, done, frames);
            this._filled += frames;
            done += frames;
            if (this._filled == this._frameSize)
            {
                this.EncodeFrame();
            }
        }
    }

    private protected override void CompleteCore()
    {
        if ((this._shortFrame is null) && (this._floatFrame is null))
        {
            this._floatFrame = new float[this._frameSize * this.Channels];
        }

        // As libopusenc drains: the input extended by linear prediction, faded out (before resampling,
        // as it extends), then silence, for enough frames that the last input sample, delayed by the
        // lookahead, comes out
        int channels = this.Channels;
        Span<float> extension = stackalloc float[EndExtension.Length * channels];
        EndExtension.Extend(this._history, this._historyCount, channels, extension);
        long end;
        int extended = EndExtension.Length;
        if (this._resampler is { } resampler)
        {
            end = ((((this.Position * 48000) + (this.SampleRate / 2)) / this.SampleRate)) + this._preSkip;
            resampler.Write(extension, this._onResampled!);
            resampler.Flush(this._onResampled!);
        }
        else
        {
            end = (this.Position * this._granuleScale) + this._preSkip;
            extended = 0;
        }
        while (this._granule < end)
        {
            int take = Math.Min(this._frameSize - this._filled, EndExtension.Length - extended);
            for (int i = 0; i < take * channels; i++)
            {
                int at = (this._filled * channels) + i;
                float sample = extension[(extended * channels) + i];
                if (this._floatFrame is { } floatFrame)
                {
                    floatFrame[at] = sample;
                }
                else
                {
                    this._shortFrame![at] = Samples.FloatToShort(sample);
                }
            }
            this._filled += take;
            extended += take;
            this.ClearRest();
            this._filled = this._frameSize;
            this.EncodeFrame();
        }
        this._ogg.SetGranule(end);
        this._ogg.Flush(last: true);
    }

    // The resampler's output, at 48 kHz in Opus order, into frames
    private void TakeResampled(ReadOnlySpan<float> frames)
    {
        int channels = this.Channels;
        float[] frame = this._floatFrame!;
        while (!frames.IsEmpty)
        {
            int take = Math.Min(this._frameSize - this._filled, frames.Length / channels);
            frames[..(take * channels)].CopyTo(frame.AsSpan(this._filled * channels));
            frames = frames[(take * channels)..];
            this._filled += take;
            if (this._filled == this._frameSize)
            {
                this.EncodeFrame();
            }
        }
    }

    // Keeps the last frames (Opus order) of the input for the end extension
    private void Remember(ReadOnlySpan<float> frames)
    {
        int channels = this.Channels;
        int count = frames.Length / channels;
        int keep = Math.Min(count, EndExtension.History);
        int shift = Math.Min(this._historyCount, EndExtension.History - keep);
        Span<float> history = this._history;
        history.Slice(history.Length - (shift * channels), shift * channels).CopyTo(history.Slice(history.Length - ((shift + keep) * channels)));
        frames[((count - keep) * channels)..].CopyTo(history[(history.Length - (keep * channels))..]);
        this._historyCount = shift + keep;
    }

    // Copies frames from the input to the frame buffer, in Opus channel order, converting to its type,
    // and keeps the last of them for the end extension
    private void Copy(ReadOnlySpan<short> shorts, ReadOnlySpan<float> floats, bool isFloat, int from, int frames)
    {
        int channels = this.Channels;
        ReadOnlySpan<byte> order = this._order;
        int keep = Math.Min(frames, EndExtension.History);
        int shift = Math.Min(this._historyCount, EndExtension.History - keep);
        Span<float> history = this._history;
        history.Slice(history.Length - (shift * channels), shift * channels).CopyTo(history.Slice(history.Length - ((shift + keep) * channels)));
        this._historyCount = shift + keep;

        // Mono and stereo are in Opus order already: whole runs at a time
        if (channels <= 2)
        {
            Span<float> kept = history[^(keep * channels)..];
            int last = (from + frames - keep) * channels;
            if (isFloat)
            {
                floats.Slice(last, keep * channels).CopyTo(kept);
            }
            else
            {
                ToFloats(shorts.Slice(last, keep * channels), kept);
            }

            int n = frames * channels;
            int at = from * channels;
            int to = this._filled * channels;
            if (this._floatFrame is { } floatFrame)
            {
                if (isFloat)
                {
                    floats.Slice(at, n).CopyTo(floatFrame.AsSpan(to));
                }
                else
                {
                    ToFloats(shorts.Slice(at, n), floatFrame.AsSpan(to, n));
                }
            }
            else if (!isFloat)
            {
                shorts.Slice(at, n).CopyTo(this._shortFrame.AsSpan(to));
            }
            else
            {
                Span<short> frame = this._shortFrame.AsSpan(to, n);
                for (int i = 0; i < n; i++)
                {
                    frame[i] = Samples.FloatToShort(floats[at + i]);
                }
            }
            return;
        }

        for (int f = frames - keep; f < frames; f++)
        {
            int input = (from + f) * channels;
            int output = history.Length - ((frames - f) * channels);
            for (int c = 0; c < channels; c++)
            {
                history[output + order[c]] = isFloat ? floats[input + c] : Samples.ShortToFloat(shorts[input + c]);
            }
        }
        for (int f = 0; f < frames; f++)
        {
            int input = (from + f) * channels;
            int output = (this._filled + f) * channels;
            for (int c = 0; c < channels; c++)
            {
                if (this._floatFrame is { } floatFrame)
                {
                    floatFrame[output + order[c]] = isFloat ? floats[input + c] : Samples.ShortToFloat(shorts[input + c]);
                }
                else
                {
                    this._shortFrame![output + order[c]] = isFloat ? Samples.FloatToShort(floats[input + c]) : shorts[input + c];
                }
            }
        }
    }

    // As Samples.ShortToFloat, eight at a time
    private static void ToFloats(ReadOnlySpan<short> source, Span<float> destination)
    {
        destination = destination[..source.Length];
        int i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            ref short s = ref MemoryMarshal.GetReference(source);
            ref float d = ref MemoryMarshal.GetReference(destination);
            var scale = Vector128.Create(1f / 32768f);
            for (; i + 8 <= source.Length; i += 8)
            {
                var (low, high) = Vector128.Widen(Vector128.LoadUnsafe(ref s, (nuint)i));
                (Vector128.ConvertToSingle(low) * scale).StoreUnsafe(ref d, (nuint)i);
                (Vector128.ConvertToSingle(high) * scale).StoreUnsafe(ref d, (nuint)(i + 4));
            }
        }
        for (; i < source.Length; i++)
        {
            destination[i] = Samples.ShortToFloat(source[i]);
        }
    }

    private void ClearRest()
    {
        int from = this._filled * this.Channels;
        this._floatFrame?.AsSpan(from).Clear();
        this._shortFrame?.AsSpan(from).Clear();
    }

    private void EncodeFrame()
    {
        int length;
        if (this._single is { } single)
        {
            length = this._floatFrame is { } floatFrame
                ? single.Encode(floatFrame, this._frameSize, this._packet)
                : single.Encode(this._shortFrame, this._frameSize, this._packet);
        }
        else
        {
            length = this._floatFrame is { } floatFrame
                ? this._multi!.Encode(floatFrame, this._frameSize, this._packet)
                : this._multi!.Encode(this._shortFrame, this._frameSize, this._packet);
        }
        if (length < 0)
        {
            throw new InvalidOperationException($"The Opus encoder failed ({OpusDefines.opus_strerror(length)}).");
        }
        this._filled = 0;

        // A page holds packets until the next would start MaxPageDelay after its first
        long start = this._granule;
        if ((this._ogg.PacketCount > 0) && (start - this._pageStart >= this._maxDelay))
        {
            this._ogg.Flush(last: false);
        }
        this._granule = start + ((long)this._frameSize * this._granuleScale);
        this._ogg.Add(this._packet.AsSpan(0, length), this._granule);
        if (this._ogg.PacketCount == 1)
        {
            this._pageStart = start;
        }
    }
}
