# Reference checks

DotGame.AudioCodecs's Opus codec is a port of libopus 1.6.1, and these checks hold it to that: it must give
native libopus's packets, final ranges and samples exactly. The per-packet checks use the library's
internal packet encoder and decoder (the checker is a friend assembly); the file checks use the public API. The FLAC, WAV and Ogg code is our
own, so it's checked against the reference tools instead.

```bash
eng/reference/build-libopus.sh
dotnet run -c Release --project eng/reference/Check -- --vectors <folder> --tools
```

`build-libopus.sh` downloads libopus 1.6.1's release tarball, checks its SHA-256, and builds it fixed
point, generic C only, with `-ffp-contract=off`, so its float analysis rounds as C# does (C# never fuses
a multiply and an add). It then builds `shim.c`, which wraps libopus's variadic ctl calls for .NET.
Everything goes in `eng/reference/.native`, which git ignores.

`Check` compares, on test signals made with arithmetic alone:

- the packet encoder (the libopus encoder `OpusEncoder` codes with) against `opus_encode` and `opus_encode_float`: 20 settings (bitrates, VBR, CVBR,
  CBR, complexity, every application, FEC, DTX, 2.5 to 120 ms frames, signal and bandwidth hints, phase
  inversion), mono and stereo, at 48 and 16 kHz; every packet and final range;
- the packet decoder (the libopus decoder `OpusDecoder` decodes with) against `opus_decode` and `opus_decode_float` on those packets;
- `OpusEncoder` files of 3 to 8 channels against libopus's surround multistream encoder, packet for
  packet, and `OpusDecoder` against its multistream decoder, sample for sample;
- with `--vectors`, the twelve RFC 8251 test vectors (https://opus-codec.org/testvectors/) through
  the packet decoder, in stereo and mono, against libopus's decode and the vectors' final ranges;
- with `--tools`, our FLAC files through `flac -t` and `flac -d` (exactly the input, at every level),
  our sizes against `flac -0` to `-8`, every WAV sample format read by ffmpeg as we read it and the
  other way round, and our Ogg Opus files of 1 to 8 channels decoded by ffmpeg.

`-p:PortableVectors=true` builds the library with `OPUS_PORTABLE_VECTORS`, so the portable SIMD forms
WebAssembly's Mono runs are checked on machines that have the intrinsics.

Checked so far on CoreCLR (.NET 11.0), Arm64 macOS, both SIMD configurations. Mono (WebAssembly) isn't
checked for the encoder's bit-exactness.
