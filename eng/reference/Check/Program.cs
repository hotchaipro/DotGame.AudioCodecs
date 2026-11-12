// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

// The checks against native libopus 1.6.1, fixed point (build-libopus.sh): DotGame.AudioCodecs's public API
// must give its packets, final ranges and samples exactly. Usage:
//   Check [--vectors <folder of RFC 8251 .bit files>] [--tools] [--quick]
// --tools also checks FLAC, WAV and Ogg Opus files against flac and ffmpeg, which must be on the PATH.
// Exits 1 if anything differs.

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using DotGame.AudioCodecs.Opus;

int failures = 0;
int checks = 0;
bool quick = args.Contains("--quick");
string? vectors = args.SkipWhile(a => a != "--vectors").Skip(1).FirstOrDefault();

// 1. Packets, one and two channels: settings opusenc and libopus users set, both input types
var settingsList = new List<(string Name, OpusEncoderOptions Options)>
{
    ("default", new()),
    ("vbr 32k", new() { Bitrate = 32000 }),
    ("vbr 160k", new() { Bitrate = 160000 }),
    ("cvbr 64k", new() { Bitrate = 64000, BitrateMode = OpusBitrateMode.ConstrainedVbr }),
    ("cbr 96k", new() { Bitrate = 96000, BitrateMode = OpusBitrateMode.Cbr }),
    ("comp 0", new() { Complexity = 0 }),
    ("comp 5", new() { Complexity = 5 }),
    ("voip 16k", new() { Bitrate = 16000, Application = OpusApplication.Voip }),
    ("voip fec", new() { Bitrate = 24000, Application = OpusApplication.Voip, InbandFec = true, ExpectedPacketLoss = 10 }),
    ("dtx", new() { Bitrate = 20000, Application = OpusApplication.Voip, Dtx = true }),
    ("lowdelay 5ms", new() { Application = OpusApplication.RestrictedLowDelay, FrameDuration = OpusFrameDuration.Ms5 }),
    ("celt 2.5ms", new() { Application = OpusApplication.RestrictedCelt, FrameDuration = OpusFrameDuration.Ms2_5, Bitrate = 128000 }),
    ("silk 60ms", new() { Application = OpusApplication.RestrictedSilk, FrameDuration = OpusFrameDuration.Ms60, Bitrate = 12000 }),
    ("10ms", new() { FrameDuration = OpusFrameDuration.Ms10 }),
    ("40ms", new() { FrameDuration = OpusFrameDuration.Ms40 }),
    ("120ms", new() { FrameDuration = OpusFrameDuration.Ms120, Bitrate = 64000 }),
    ("music", new() { Signal = OpusSignal.Music, Bitrate = 48000 }),
    ("speech", new() { Signal = OpusSignal.Speech, Bitrate = 24000 }),
    ("wideband", new() { MaxBandwidth = OpusBandwidth.Wideband }),
    ("no phase inversion", new() { PhaseInversion = false }),
};
if (quick)
{
    settingsList = settingsList.Take(5).ToList();
}
foreach (int rate in new[] { 48000, 16000 })
{
    foreach (int channels in new[] { 1, 2 })
    {
        foreach (var (name, options) in settingsList)
        {
            if ((rate != 48000) && (options.FrameDuration < OpusFrameDuration.Ms10))
            {
                continue;
            }
            foreach (bool floats in new[] { true, false })
            {
                ComparePackets($"{rate} Hz, {channels} ch, {name}, {(floats ? "float" : "short")}", rate, channels, options, floats);
            }
        }
    }
}

// 2. Ogg Opus files of 3 to 8 channels: the packets of libopus's surround encoder, and its decode
foreach (int channels in new[] { 3, 4, 5, 6, 7, 8 })
{
    foreach (bool floats in new[] { true, false })
    {
        CompareSurround(channels, new OpusEncoderOptions(), floats);
    }
    if (!quick)
    {
        CompareSurround(channels, new OpusEncoderOptions { Bitrate = 128000 * channels / 6 + 32000, BitrateMode = OpusBitrateMode.Cbr }, floats: true);
    }
}

// 3. The RFC 8251 vectors, decoded by the packet decoder in stereo and mono: the samples and final ranges of libopus
if (vectors is not null)
{
    foreach (string bit in Directory.GetFiles(vectors, "testvector*.bit").Order())
    {
        foreach (int channels in new[] { 2, 1 })
        {
            CompareVector(bit, channels);
        }
    }
}

Console.WriteLine($"{checks - failures} of {checks} the same as libopus");

// 4. flac and ffmpeg, from the PATH
if (args.Contains("--tools"))
{
    var (toolChecks, toolFailures) = Tools.Run(Console.WriteLine);
    Console.WriteLine($"{toolChecks - toolFailures} of {toolChecks} as flac and ffmpeg have them");
    failures += toolFailures;
}
return failures == 0 ? 0 : 1;

void ComparePackets(string what, int rate, int channels, OpusEncoderOptions options, bool floats)
{
    float[] signal = Signal.Make(channels, rate * 3, rate);
    short[] shorts = Signal.ToShorts(signal);
    var ours = new OpusPacketEncoder(rate, channels, options);
    using var native = new Native.Encoder(rate, channels, options);
    int frame = ours.FrameSize;
    var a = new byte[4000];
    var b = new byte[4000];
    var decoder = new OpusPacketDecoder(rate, channels);
    var floatDecoder = new OpusPacketDecoder(rate, channels);
    using var nativeDecoder = new Native.Decoder(rate, channels, 1, channels - 1, [0, 1]);
    using var nativeFloatDecoder = new Native.Decoder(rate, channels, 1, channels - 1, [0, 1]);
    var oursShort = new short[frame * channels];
    var nativeShort = new short[frame * channels];
    var oursFloat = new float[frame * channels];
    var nativeFloat = new float[frame * channels];
    int packets = 0;
    for (int at = 0; at + frame <= signal.Length / channels; at += frame)
    {
        int la = floats ? ours.Encode(signal.AsSpan(at * channels, frame * channels), a) : ours.Encode(shorts.AsSpan(at * channels, frame * channels), a);
        int lb = floats ? native.Encode(signal.AsSpan(at * channels, frame * channels), frame, b) : native.Encode(shorts.AsSpan(at * channels, frame * channels), frame, b);
        if (!a.AsSpan(0, la).SequenceEqual(b.AsSpan(0, lb)) || (ours.FinalRange != native.FinalRange))
        {
            Fail($"{what}: packet {packets} differs ({la} vs {lb} bytes, range {ours.FinalRange:x8} vs {native.FinalRange:x8})");
            return;
        }

        // Both decoders, in both sample types
        int fa = decoder.Decode(a.AsSpan(0, la), oursShort);
        int fb = nativeDecoder.Decode(b.AsSpan(0, lb), nativeShort);
        int ga = floatDecoder.Decode(a.AsSpan(0, la), oursFloat);
        int gb = nativeFloatDecoder.Decode(b.AsSpan(0, lb), nativeFloat);
        packets++;
        if ((fa != fb) || !oursShort.AsSpan(0, fa * channels).SequenceEqual(nativeShort.AsSpan(0, fb * channels))
            || (ga != gb) || !oursFloat.AsSpan(0, ga * channels).SequenceEqual(nativeFloat.AsSpan(0, gb * channels))
            || (decoder.FinalRange != ours.FinalRange))
        {
            Fail($"{what}: the decode of packet {packets} differs");
            return;
        }
    }
    Pass();
}

void CompareSurround(int channels, OpusEncoderOptions options, bool floats)
{
    string what = $"surround {channels} ch, {(options.Bitrate is int r ? $"{r / 1000}k" : "default")} {options.BitrateMode}, {(floats ? "float" : "short")}";
    int frames = 48000 * 2 + 345;
    float[] wave = Signal.Make(channels, frames, 48000);
    var file = new MemoryStream();
    using (var encoder = new OpusEncoder(file, 48000, channels, options, leaveOpen: true))
    {
        if (floats)
        {
            encoder.Write(wave);
        }
        else
        {
            encoder.Write(Signal.ToShorts(wave));
        }
        encoder.Complete();
    }
    byte[] ogg = file.ToArray();
    var (head, packets) = Ogg.Read(ogg);
    int streams = head[19];
    int coupled = head[20];
    byte[] mapping = head.AsSpan(21, channels).ToArray();
    int preSkip = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(10));

    // The same frames, in Opus's order, through libopus, padded as the file's encoder pads
    byte[] order = Signal.OpusOrder(channels);
    float[] opusOrder = new float[wave.Length];
    for (int f = 0; f < frames; f++)
    {
        for (int c = 0; c < channels; c++)
        {
            opusOrder[(f * channels) + order[c]] = wave[(f * channels) + c];
        }
    }
    using var native = new Native.Encoder(48000, channels, options);
    if ((native.Streams != streams) || (native.Coupled != coupled) || !native.Mapping.AsSpan(0, channels).SequenceEqual(mapping) || (native.Lookahead != preSkip))
    {
        Fail($"{what}: the layout or pre-skip differs");
        return;
    }
    var frame = new float[960 * channels];
    var shortFrame = new short[960 * channels];
    var packet = new byte[((1277 * 6) + 2) * streams];

    // The end padded as the encoder pads it: the last 480 frames (as the encoder holds them: 16-bit
    // for short input) extended by LPC for 120, then silence
    if (!floats)
    {
        short[] quantized = Signal.ToShorts(opusOrder);
        for (int i = 0; i < quantized.Length; i++)
        {
            opusOrder[i] = quantized[i] / 32768f;
        }
    }
    float[] padded = new float[opusOrder.Length + (packets.Count * 960 * channels)];
    opusOrder.CopyTo(padded, 0);
    EndExtension.Extend(opusOrder, Math.Min(frames, EndExtension.History), channels, padded.AsSpan(opusOrder.Length, EndExtension.Length * channels));
    opusOrder = padded;
    short[] opusShorts = Signal.ToShorts(opusOrder);
    for (int i = 0; i < packets.Count; i++)
    {
        int from = i * 960 * channels;
        int count = frame.Length;
        Array.Clear(frame);
        Array.Clear(shortFrame);
        opusOrder.AsSpan(from, count).CopyTo(frame);
        opusShorts.AsSpan(from, count).CopyTo(shortFrame);
        int length = floats ? native.Encode(frame, 960, packet) : native.Encode(shortFrame, 960, packet);
        if (!packets[i].AsSpan().SequenceEqual(packet.AsSpan(0, length)))
        {
            Fail($"{what}: packet {i} differs ({packets[i].Length} vs {length} bytes)");
            return;
        }
    }

    // Our file decoder against libopus's multistream decoder, in WAVE order, pre-skip and end dropped
    using var nativeDecoder = new Native.Decoder(48000, channels, streams, coupled, mapping);
    var all = new List<float>();
    var pcm = new float[5760 * channels];
    foreach (byte[] p in packets)
    {
        int n = nativeDecoder.Decode(p, pcm);
        all.AddRange(pcm.AsSpan(0, n * channels));
    }
    using var decoder = new OpusDecoder(ogg);
    var ours = new float[frames * channels];
    if (decoder.Read(ours) != frames)
    {
        Fail($"{what}: the file decodes to the wrong length");
        return;
    }
    for (int f = 0; f < frames; f++)
    {
        for (int c = 0; c < channels; c++)
        {
            if (ours[(f * channels) + c] != all[((f + preSkip) * channels) + order[c]])
            {
                Fail($"{what}: the decode differs at frame {f}, channel {c}");
                return;
            }
        }
    }
    Pass();
}

void CompareVector(string bit, int channels)
{
    byte[] data = File.ReadAllBytes(bit);
    var decoder = new OpusPacketDecoder(48000, channels);
    using var native = new Native.Decoder(48000, channels, 1, channels - 1, [0, 1]);
    var a = new short[5760 * channels];
    var b = new short[5760 * channels];
    var fa = new float[5760 * channels];
    var floatDecoder = new OpusPacketDecoder(48000, channels);
    using var nativeFloat = new Native.Decoder(48000, channels, 1, channels - 1, [0, 1]);
    var fb = new float[5760 * channels];
    int at = 0;
    int packet = 0;
    while (at + 8 <= data.Length)
    {
        int length = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(at));
        uint range = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at + 4));
        ReadOnlySpan<byte> bytes = data.AsSpan(at + 8, length);
        at += 8 + length;
        if (bytes.IsEmpty)
        {
            continue;
        }
        int na = decoder.Decode(bytes, a);
        int nb = native.Decode(bytes, b);
        int ma = floatDecoder.Decode(bytes, fa);
        int mb = nativeFloat.Decode(bytes, fb);
        if ((na != nb) || !a.AsSpan(0, na * channels).SequenceEqual(b.AsSpan(0, nb * channels)) || (decoder.FinalRange != range)
            || (ma != mb) || !fa.AsSpan(0, ma * channels).SequenceEqual(fb.AsSpan(0, mb * channels)))
        {
            Fail($"{Path.GetFileName(bit)} ({channels} ch): packet {packet} differs");
            return;
        }
        packet++;
    }
    Pass();
}

void Pass() => checks++;

void Fail(string message)
{
    checks++;
    failures++;
    Console.WriteLine("DIFFERS " + message);
}
