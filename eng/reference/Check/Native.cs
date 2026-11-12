// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Runtime.InteropServices;
using DotGame.AudioCodecs.Opus;

/// <summary>Native libopus through the shim (shim.c): encoders and decoders with the checks' settings.</summary>
internal static unsafe partial class Native
{
    private const string Library = "shim";

    static Native()
    {
        NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, (name, assembly, path) =>
        {
            if (name != Library)
            {
                return 0;
            }
            // eng/reference/.native, above the output folder (or LIBSHIM, a path to the library)
            string file = OperatingSystem.IsMacOS() ? "libshim.dylib" : "libshim.so";
            if (Environment.GetEnvironmentVariable("LIBSHIM") is { Length: > 0 } explicitPath)
            {
                return NativeLibrary.Load(explicitPath);
            }
            for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            {
                string candidate = Path.Combine(folder.FullName, ".native", file);
                if (File.Exists(candidate))
                {
                    return NativeLibrary.Load(candidate);
                }
            }
            throw new DllNotFoundException($"{file} isn't built: run eng/reference/build-libopus.sh.");
        });
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Settings
    {
        public int Bitrate;
        public int Vbr;
        public int VbrConstraint;
        public int Complexity;
        public int Loss;
        public int Fec;
        public int Dtx;
        public int Signal;
        public int MaxBandwidth;
        public int PhaseInversionDisabled;
        public int Application;
    }

    [LibraryImport(Library, EntryPoint = "shim_encoder_create")]
    private static partial nint EncoderCreate(int rate, int channels, Settings* settings, int* streams, int* coupled, byte* mapping);

    [LibraryImport(Library, EntryPoint = "shim_encode_float")]
    private static partial int EncodeFloat(nint encoder, float* pcm, int frameSize, byte* data, int maxBytes);

    [LibraryImport(Library, EntryPoint = "shim_encode_short")]
    private static partial int EncodeShort(nint encoder, short* pcm, int frameSize, byte* data, int maxBytes);

    [LibraryImport(Library, EntryPoint = "shim_encoder_final_range")]
    private static partial uint EncoderFinalRange(nint encoder);

    [LibraryImport(Library, EntryPoint = "shim_encoder_lookahead")]
    private static partial int EncoderLookahead(nint encoder);

    [LibraryImport(Library, EntryPoint = "shim_encoder_destroy")]
    private static partial void EncoderDestroy(nint encoder);

    [LibraryImport(Library, EntryPoint = "shim_decoder_create")]
    private static partial nint DecoderCreate(int rate, int channels, int streams, int coupled, byte* mapping);

    [LibraryImport(Library, EntryPoint = "shim_decode_short")]
    private static partial int DecodeShort(nint decoder, byte* data, int length, short* pcm, int frameSize);

    [LibraryImport(Library, EntryPoint = "shim_decode_float")]
    private static partial int DecodeFloat(nint decoder, byte* data, int length, float* pcm, int frameSize);

    [LibraryImport(Library, EntryPoint = "shim_decoder_destroy")]
    private static partial void DecoderDestroy(nint decoder);

    /// <summary>libopus's encoder with what the options set, as the public encoders set them.</summary>
    internal sealed class Encoder : IDisposable
    {
        private readonly nint _handle;

        public Encoder(int rate, int channels, OpusEncoderOptions options)
        {
            int streams;
            int coupled;
            byte* mapping = stackalloc byte[8];
            var settings = new Settings
            {
                Bitrate = Bitrate(options, rate, channels),
                Vbr = options.BitrateMode == OpusBitrateMode.Cbr ? 0 : 1,
                VbrConstraint = options.BitrateMode == OpusBitrateMode.ConstrainedVbr ? 1 : 0,
                Complexity = options.Complexity,
                Loss = options.ExpectedPacketLoss,
                Fec = options.InbandFec ? 1 : 0,
                Dtx = options.Dtx ? 1 : 0,
                Signal = options.Signal switch { OpusSignal.Music => 3002, OpusSignal.Speech => 3001, _ => -1000 },
                MaxBandwidth = 1101 + (int)options.MaxBandwidth,
                PhaseInversionDisabled = options.PhaseInversion ? 0 : 1,
                Application = options.Application switch
                {
                    OpusApplication.Audio => 2049,
                    OpusApplication.Voip => 2048,
                    OpusApplication.RestrictedLowDelay => 2051,
                    OpusApplication.RestrictedSilk => 2052,
                    _ => 2053,
                },
            };
            this._handle = EncoderCreate(rate, channels, &settings, &streams, &coupled, mapping);
            if (this._handle == 0)
            {
                throw new InvalidOperationException("libopus didn't create the encoder.");
            }
            this.Streams = streams;
            this.Coupled = coupled;
            this.Mapping = new ReadOnlySpan<byte>(mapping, 8).ToArray();
        }

        public int Streams { get; }

        public int Coupled { get; }

        public byte[] Mapping { get; }

        public uint FinalRange => EncoderFinalRange(this._handle);

        public int Lookahead => EncoderLookahead(this._handle);

        public int Encode(ReadOnlySpan<float> pcm, int frameSize, Span<byte> packet)
        {
            fixed (float* p = pcm)
            fixed (byte* d = packet)
            {
                return EncodeFloat(this._handle, p, frameSize, d, packet.Length);
            }
        }

        public int Encode(ReadOnlySpan<short> pcm, int frameSize, Span<byte> packet)
        {
            fixed (short* p = pcm)
            fixed (byte* d = packet)
            {
                return EncodeShort(this._handle, p, frameSize, d, packet.Length);
            }
        }

        public void Dispose() => EncoderDestroy(this._handle);

        // opusenc's default for the layout, as OpusEncoderOptions documents it
        private static int Bitrate(OpusEncoderOptions options, int rate, int channels)
        {
            if (options.Bitrate is int bitrate)
            {
                return bitrate;
            }
            (int streams, int coupled) = channels switch { 1 => (1, 0), 2 => (1, 1), 3 => (2, 1), 4 => (2, 2), 5 => (3, 2), 6 => (4, 2), 7 => (4, 3), _ => (5, 3) };
            int r = rate < 44100 ? rate : 48000;
            int khz = Math.Min(48, Math.Max(8, (r + 1000) / 1000));
            return (int)Math.Min(((((64000L * streams) + (32000L * coupled)) * (khz + 16)) + 32) >> 6, 750_000L * channels);
        }
    }

    /// <summary>libopus's multistream decoder (with one stream, a plain decoder's output).</summary>
    internal sealed class Decoder : IDisposable
    {
        private readonly nint _handle;
        private readonly int _channels;

        public Decoder(int rate, int channels, int streams, int coupled, ReadOnlySpan<byte> mapping)
        {
            byte* map = stackalloc byte[8];
            for (int i = 0; i < channels; i++)
            {
                map[i] = mapping.Length > i ? mapping[i] : (byte)i;
            }
            this._handle = DecoderCreate(rate, channels, streams, coupled, map);
            this._channels = channels;
            if (this._handle == 0)
            {
                throw new InvalidOperationException("libopus didn't create the decoder.");
            }
        }

        public int Decode(ReadOnlySpan<byte> packet, Span<short> pcm)
        {
            fixed (byte* d = packet)
            fixed (short* p = pcm)
            {
                return DecodeShort(this._handle, d, packet.Length, p, pcm.Length / this._channels);
            }
        }

        public int Decode(ReadOnlySpan<byte> packet, Span<float> pcm)
        {
            fixed (byte* d = packet)
            fixed (float* p = pcm)
            {
                return DecodeFloat(this._handle, d, packet.Length, p, pcm.Length / this._channels);
            }
        }

        public void Dispose() => DecoderDestroy(this._handle);
    }
}
