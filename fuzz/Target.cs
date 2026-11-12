// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.IO;
using DotGame.AudioCodecs.Flac;
using DotGame.AudioCodecs.Opus;
using DotGame.AudioCodecs.Wav;

namespace DotGame.AudioCodecs.Fuzz;

/// <summary>
/// The decoders the fuzzer runs: one format's, <see cref="AudioDecoder.Open(ReadOnlyMemory{byte})"/>,
/// which tells the format from the bytes, or the Opus packet decoder on raw packets.
/// </summary>
internal enum Format
{
    Wav,
    Flac,
    Opus,
    Any,
    Packet,
}

/// <summary>
/// Decodes one input the way an app would, and checks what the decoders promise: they throw only
/// <see cref="InvalidDataException"/> for bad bytes, never read or write past the input or the samples
/// (the buffers end at a guard page), give the same samples from memory and from a stream that can't
/// seek, the same frames as <c>short</c> and as <c>float</c>, the same samples a second time and after a
/// seek to the start, and as many frames as <see cref="AudioDecoder.Frames"/> says.
/// </summary>
internal sealed class Target
{
    private const int Chunk = 4801;

    private readonly Format format;
    private readonly int maxFrames;
    private readonly GuardedBuffer input = new(4 << 20);
    private readonly GuardedBuffer shorts = new(Chunk * 8 * sizeof(short));
    private readonly GuardedBuffer floats = new(Chunk * 8 * sizeof(float));
    private readonly GuardedBuffer packet = new(5760 * 2 * sizeof(float));

    /// <param name="format">The decoder to run.</param>
    /// <param name="maxFrames">A file is decoded to at most this many frames, which keeps each run short.</param>
    public Target(Format format, int maxFrames)
    {
        this.format = format;
        this.maxFrames = maxFrames;
    }

    public int MaxInput => this.input.Capacity;

    /// <summary>
    /// Decodes <paramref name="data"/>, and returns null if the decoders kept their promises, or what
    /// went wrong (a short signature that tells one bug from another, then the details).
    /// </summary>
    /// <param name="data">The input.</param>
    /// <param name="hash">The decoded samples' hash, or 0 if they didn't decode.</param>
    public Failure? Run(ReadOnlySpan<byte> data, out ulong hash)
    {
        hash = 0;
        Memory<byte> bytes = this.input.EndMemory(data.Length);
        data.CopyTo(bytes.Span);
        try
        {
            if (this.format == Format.Packet)
            {
                return this.RunPacket(bytes.Span, out hash);
            }

            // From memory, as shorts, read in chunks to the end (or the limit)
            ulong first;
            long frames;
            long? declared;
            int channels;
            using (AudioDecoder? decoder = this.Open(bytes))
            {
                if (decoder is null)
                {
                    return null;
                }
                channels = decoder.Channels;
                declared = decoder.Frames;
                (frames, first) = this.ReadShorts(decoder);
                if ((frames < this.maxFrames) && (decoder.Frames is long known) && (known != frames))
                {
                    return new Failure("frames-mismatch", $"Frames said {known}, the reads gave {frames}");
                }

                // From the start again, after a seek
                if (decoder.CanSeek)
                {
                    decoder.Seek(0);
                    var (again, seekHash) = this.ReadShorts(decoder);
                    if ((again != frames) || (seekHash != first))
                    {
                        return new Failure("seek-start-differs", $"{frames} frames ({first:x16}), after Seek(0) {again} ({seekHash:x16})");
                    }
                    if ((decoder.Frames is long length) && (length > 0))
                    {
                        decoder.Seek(length / 2);
                        this.ReadShorts(decoder);
                        decoder.Seek(length);
                        Span<short> tail = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(this.shorts.End(channels * sizeof(short) * 16));
                        if (decoder.Read(tail) != 0)
                        {
                            return new Failure("read-after-end", "A read at the end returned frames");
                        }
                    }
                }
            }

            // As floats: the same number of frames
            using (AudioDecoder? decoder = this.Open(bytes))
            {
                long floatFrames = this.ReadFloats(decoder!);
                if (floatFrames != frames)
                {
                    return new Failure("float-frames-differ", $"{frames} frames as short, {floatFrames} as float");
                }
            }

            // From a stream that can't seek: the same samples
            using (var stream = new UnseekableStream(bytes.ToArray()))
            using (AudioDecoder decoder = this.Open(stream))
            {
                var (streamed, streamedHash) = this.ReadShorts(decoder);
                if ((streamed != frames) || (streamedHash != first))
                {
                    return new Failure("stream-differs", $"{frames} frames ({first:x16}) from memory, {streamed} ({streamedHash:x16}) from a stream");
                }
                if ((streamed < this.maxFrames) && (declared is long d) && (decoder.Frames != d))
                {
                    return new Failure("stream-frames-differ", $"Frames {declared} from memory, {decoder.Frames} from a stream");
                }
            }
            hash = first;
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (Exception exception)
        {
            return Failure.From(exception);
        }
    }

    public static ulong Hash(ReadOnlySpan<byte> data)
    {
        ulong hash = 14695981039346656037;
        foreach (byte b in data)
        {
            hash = (hash ^ b) * 1099511628211;
        }
        return hash;
    }

    private AudioDecoder? Open(ReadOnlyMemory<byte> bytes) => this.format switch
    {
        Format.Wav => new WavDecoder(bytes),
        Format.Flac => new FlacDecoder(bytes),
        Format.Opus => new OpusDecoder(bytes),
        _ => AudioDecoder.Open(bytes),
    };

    private AudioDecoder Open(Stream stream) => this.format switch
    {
        Format.Wav => new WavDecoder(stream, leaveOpen: true),
        Format.Flac => new FlacDecoder(stream, leaveOpen: true),
        Format.Opus => new OpusDecoder(stream, leaveOpen: true),
        _ => AudioDecoder.Open(stream, leaveOpen: true),
    };

    // Reads to the end or the limit in chunks of shorts, each into a buffer ending at a guard page
    private (long Frames, ulong Hash) ReadShorts(AudioDecoder decoder)
    {
        int channels = decoder.Channels;
        ulong hash = 14695981039346656037;
        long frames = 0;
        while (frames < this.maxFrames)
        {
            Span<short> buffer = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(this.shorts.End(Chunk * channels * sizeof(short)));
            int read = decoder.Read(buffer);
            if (read == 0)
            {
                break;
            }
            foreach (byte b in System.Runtime.InteropServices.MemoryMarshal.AsBytes(buffer[..(read * channels)]))
            {
                hash = (hash ^ b) * 1099511628211;
            }
            frames += read;
        }
        return (frames, hash);
    }

    private long ReadFloats(AudioDecoder decoder)
    {
        int channels = decoder.Channels;
        long frames = 0;

        // A float WAV file stores any float, NaN and infinities included, and is read as it is
        bool finite = decoder is not WavDecoder { SampleFormat: WavSampleFormat.Float32 or WavSampleFormat.Float64 };
        while (frames < this.maxFrames)
        {
            Span<float> buffer = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(this.floats.End(Chunk * channels * sizeof(float)));
            int read = decoder.Read(buffer);
            if (read == 0)
            {
                break;
            }
            foreach (float sample in buffer[..(read * channels)])
            {
                if (finite && !float.IsFinite(sample))
                {
                    throw new InvalidOperationException("A float sample isn't finite.");
                }
            }
            frames += read;
        }
        return frames;
    }

    // Raw Opus packets, each a length byte pair then the bytes, into a stereo and a mono decoder
    private Failure? RunPacket(ReadOnlySpan<byte> data, out ulong hash)
    {
        hash = 14695981039346656037;
        var stereo = new OpusPacketDecoder(48000, 2);
        var mono = new OpusPacketDecoder(24000, 1);
        int at = 0;
        while (at + 2 <= data.Length)
        {
            int length = Math.Min((data[at] << 8) | data[at + 1], data.Length - at - 2);
            ReadOnlySpan<byte> packet = data.Slice(at + 2, length);
            at += 2 + length;
            try
            {
                if (packet.IsEmpty)
                {
                    stereo.Conceal(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(this.packet.End(960 * 2 * sizeof(short))));
                    continue;
                }
                Span<float> output = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(this.packet.End(5760 * 2 * sizeof(float)));
                int frames = stereo.Decode(packet, output);
                foreach (byte b in System.Runtime.InteropServices.MemoryMarshal.AsBytes(output[..(frames * 2)]))
                {
                    hash = (hash ^ b) * 1099511628211;
                }
                mono.Decode(packet, System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(this.packet.End(2880 * sizeof(short))));
                stereo.DecodeFec(packet, System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(this.packet.End(960 * 2 * sizeof(short))));
            }
            catch (InvalidDataException)
            {
            }
            catch (ArgumentException)
            {
                // A packet longer than the buffer it was given, or a lost-packet length that isn't one
            }
            catch (Exception exception)
            {
                return Failure.From(exception);
            }
        }
        return null;
    }
}

/// <summary>What went wrong: a signature that tells one bug from another, and the details.</summary>
internal sealed record Failure(string Signature, string Details)
{
    public static Failure From(Exception exception)
    {
        // The method the exception came from tells one bug from another
        string method = exception.TargetSite?.Name ?? "unknown";
        return new Failure($"{exception.GetType().Name}-{method}", exception.ToString());
    }
}
