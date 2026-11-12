# DotGame.AudioCodecs

Opus, FLAC and WAV audio codecs for .NET, with no dependencies: a decoder and an encoder for each.
Trim and AOT safe.

| Format | Based on | Matches |
|---|---|---|
| Ogg Opus | libopus 1.6.1 (fixed point), ported to C#, and libopusenc's file layout | libopus's packets and decoded samples, exactly |
| FLAC | RFC 9639, with flac 1.5's compression levels | flac's and ffmpeg's decoded samples; files flac decodes to exactly the input |
| WAV | The WAVE format: 8 to 32-bit PCM, 32 and 64-bit float | ffmpeg's reading of every sample format |

## Installing

For .NET 11 and later:

```bash
dotnet add package DotGame.AudioCodecs
```

## Using it

The [guide](https://github.com/hotchaipro/DotGame.AudioCodecs/blob/main/docs/guide.md) shows how to
decode and encode a file, and the
[API reference](https://github.com/hotchaipro/DotGame.AudioCodecs/blob/main/docs/README.md) has a page
for every public type and member, with every option the encoders take.

## Performance

Times relative to each format's reference C library (lower is faster): libopus and libFLAC, with
dr_flac as a second FLAC decoder (the one SDL_sound and SDL_mixer can bundle). The first column is
the reference library's time. DotGame.AudioCodecs ran on .NET 11's JIT; published with NativeAOT it's
within a few percent of these. DotGame.AudioCodecs's Opus codec is a port of libopus's fixed-point build,
so that build's column is the like-for-like one; the first is libopus's default, floating-point build,
which decodes faster and encodes slower.

Ogg Opus decode:

| Audio | libopus | libopus, fixed point | DotGame.AudioCodecs |
|---|---:|---:|---:|
| Music, 126 s, stereo | 150 ms | 1.01x | 1.04x |
| Sound effects, 56 s, stereo | 57.6 ms | 1.03x | 1.06x |

Ogg Opus encode, opusenc's defaults (96 kbit/s for stereo, unconstrained VBR, complexity 10, 20 ms
frames), with the file's size:

| Audio | libopus | libopus, fixed point | DotGame.AudioCodecs |
|---|---:|---:|---:|
| Music, 126 s, stereo | 576 ms, 1,840 KB | 0.93x, 1,837 KB | 1.04x, 1,829 KB |
| Sound effects, 56 s, stereo | 204 ms, 690 KB | 0.88x, 706 KB | 1.01x, 703 KB |

FLAC decode:

| Audio | libFLAC | dr_flac | DotGame.AudioCodecs |
|---|---:|---:|---:|
| Music, 126 s, stereo | 80.6 ms | 0.58x | 0.53x |
| Sound effects, 56 s, stereo | 29.1 ms | 0.64x | 0.55x |

FLAC encode, level 5 (flac's default), with the file's size:

| Audio | libFLAC | DotGame.AudioCodecs |
|---|---:|---:|
| Music, 126 s, stereo | 124 ms, 14,080 KB | 1.16x, 14,088 KB |
| Sound effects, 56 s, stereo | 51.7 ms, 3,065 KB | 1.07x, 3,074 KB |

How these were measured (`bench/`, run with `bench/run.sh`):

- Apple M6 Mac mini (Arm64), macOS 27.2, one thread, everything in memory.
- Each time is the median of at least 15 runs over at least 2 seconds, after a warm-up.
- The C libraries are Homebrew's builds, called from C compiled with `-O2`: libopus 1.6.1, libogg
  1.3.6 and libFLAC 1.5.0. The fixed-point libopus is 1.6.1 built with `--enable-fixed-point` and
  its default flags (`-O2`, NEON). dr_flac is mackron/dr_libs at 84f04159 (v0.13.5 in progress),
  one header compiled into the same harness.
- .NET 11.0.0 (SDK 11.0.100).
- The audio is 48 kHz, 16-bit. Decoders write 16-bit interleaved samples, DotGame.AudioCodecs's into a
  buffer it's given; libFLAC's MD5 check is off, as DotGame.AudioCodecs doesn't check MD5s. Every
  FLAC decoder checks the frames' CRCs. The files
  decoded are the C libraries' encodes of the same audio.
- Encoders read 16-bit interleaved samples. The C side writes Ogg pages with libogg.

## License

MIT (`LICENSE`). The Opus codec is a port of libopus, and the Opus encoder's end-of-stream padding
follows libopusenc's; both are under their own licenses: see `THIRD-PARTY-NOTICES.md`.

## Building and testing

`src/` has the library, a folder per format, and `test/` its tests. Building needs the .NET 11 SDK
or later.

```bash
dotnet build
dotnet test
```

`eng/reference/` holds the checks against native libopus, flac and ffmpeg, `fuzz/` the decoders'
fuzzer, and `bench/` the benchmarks. The API reference, a page for every public type and member, is in
[docs/](https://github.com/hotchaipro/DotGame.AudioCodecs/blob/main/docs/README.md). To contribute,
see `CONTRIBUTING.md`.
