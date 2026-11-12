# Using DotGame.AudioCodecs

How to decode and encode Opus, FLAC and WAV files. The [API reference](api/README.md) has every type,
member and option.

## Decoding

A decoder reads in chunks: each [`Read`](api/AudioDecoder.md#read) fills the buffer it's given with the
next frames, interleaved, as 16-bit `short` or -1 to 1 `float` samples, and
[`Seek`](api/AudioDecoder.md#seek) moves to any frame. [`AudioDecoder.Open`](api/AudioDecoder.md#open)
tells the format from the bytes; [`WavDecoder`](api/WavDecoder.md), [`FlacDecoder`](api/FlacDecoder.md)
and [`OpusDecoder`](api/OpusDecoder.md) (in `DotGame.AudioCodecs.Wav`, `.Flac` and `.Opus`) open one
format each. A decoder allocates its buffers when it's created, so reading allocates nothing. Opus is
always decoded at 48 kHz.

```csharp
using DotGame.AudioCodecs;

using AudioDecoder decoder = AudioDecoder.Open(File.ReadAllBytes("music.opus"));
short[] samples = new short[decoder.Frames!.Value * decoder.Channels];
decoder.Read(samples);
```

Damaged data is an `InvalidDataException`. Ogg page CRCs and FLAC frame CRC-16s are checked;
`new AudioDecoderOptions { SkipChecksums = true }` ([`AudioDecoderOptions`](api/AudioDecoderOptions.md))
skips them for files that are trusted.

## Encoding

An encoder writes to a stream. [`Complete`](api/AudioEncoder.md#complete) finishes the file (its
headers, totals and checksums); an encoder disposed without it leaves the file visibly unfinished.
Each format's options class names the reference tool's settings:
[`OpusEncoderOptions`](api/OpusEncoderOptions.md) opusenc's, [`FlacEncoderOptions`](api/FlacEncoderOptions.md)
flac's. The defaults encode as those tools do with no options.

```csharp
using DotGame.AudioCodecs.Opus;

using var file = File.Create("music.opus");
using var encoder = new OpusEncoder(file, sampleRate: 44100, channels: 2,
    new OpusEncoderOptions { Bitrate = 128_000 });
encoder.Write(samples);
encoder.Complete();
```

The Opus encoder takes any sample rate up to 768 kHz: libopus's own rates (8, 12, 16, 24 and 48 kHz)
are coded directly, and others are resampled to 48 kHz, as opusenc does.
