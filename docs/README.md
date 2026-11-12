# DotGame.AudioCodecs documentation

DotGame.AudioCodecs is a set of Opus, FLAC and WAV audio codecs for .NET 11, with no dependencies: a
decoder and an encoder for each. The [README](../README.md) shows how to install it and how fast it
is.

## Guide

[Using DotGame.AudioCodecs](guide.md) shows how to decode and encode a file: reading in chunks,
damaged data and checksums, encoder options and sample rates.

## API reference

[The API reference](api/README.md) has a page for every public type and member, generated from the
source code's doc comments:

| Namespace | What's in it |
|---|---|
| `DotGame.AudioCodecs` | [`AudioDecoder`](api/AudioDecoder.md), which reads any of the formats, telling which from the bytes, and [`AudioDecoderOptions`](api/AudioDecoderOptions.md); [`AudioEncoder`](api/AudioEncoder.md), the base of the encoders |
| `DotGame.AudioCodecs.Opus` | [`OpusDecoder`](api/OpusDecoder.md) and [`OpusEncoder`](api/OpusEncoder.md) for Ogg Opus files, with [`OpusEncoderOptions`](api/OpusEncoderOptions.md) (opusenc's settings) and [`OpusFileOptions`](api/OpusFileOptions.md) |
| `DotGame.AudioCodecs.Flac` | [`FlacDecoder`](api/FlacDecoder.md) and [`FlacEncoder`](api/FlacEncoder.md), with [`FlacEncoderOptions`](api/FlacEncoderOptions.md) (flac's settings) |
| `DotGame.AudioCodecs.Wav` | [`WavDecoder`](api/WavDecoder.md) and [`WavEncoder`](api/WavEncoder.md), with [`WavEncoderOptions`](api/WavEncoderOptions.md) and [`WavSampleFormat`](api/WavSampleFormat.md) |

To find something fast, press `t` on the repository's GitHub page and type the type's name:
`OpusEncoder.md` takes you straight to it.

## Contributing

How to build, test and change the library is in [CONTRIBUTING.md](../CONTRIBUTING.md). The API
reference follows the doc comments: after changing one, regenerate it (see "The docs follow the code"
there).
