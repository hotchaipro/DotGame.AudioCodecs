// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Security.Cryptography;
using DotGame.AudioCodecs.Opus;

namespace DotGame.AudioCodecs.Tests;

/// <summary>
/// The port of libopus 1.6.1's encoder. Each hash is of native libopus's packets for the case (fixed
/// point, generic C, no FMA: each packet as a 2-byte length and its bytes, then the final range); the
/// checks in eng/reference compare the two packet by packet. The signals are made with arithmetic alone, so they're the same on every machine.
/// </summary>
public sealed class OpusEncoderTests
{
    private const int MaxPacket = (1275 * 6) + 16;

    [Theory]
    [InlineData("music", 2, 112000, 960, 10, 1, 0, 0, "d1afbe1f8b3136cf9fbf5bc78edc06abe9dfd071200198ed2df34f64c1fe9f2b")]
    [InlineData("music", 2, 48000, 960, 10, 1, 0, 0, "0b8ab6973e97e56066237204d8be6366a00d40f4d1e282dcef893a7b1d6bf830")]
    [InlineData("music", 2, 256000, 960, 10, 1, 0, 0, "018758432b1d562c5479d96263633e82d497a60e257912d2a87888b9d17e8f62")]
    [InlineData("music", 1, 64000, 960, 10, 1, 0, 0, "5a43c2e0c752eb28371829fed1533de07c8bd84b2452e3480f7d3414b2dab991")]
    [InlineData("speech", 1, 12000, 960, 10, 1, 0, 0, "fb5c370d82b07280e77869c3efa927e6c8e6ac0eb928f9247acbfcead23337ba")]
    [InlineData("speech", 1, 24000, 960, 10, 1, 0, 0, "a8d141ff9c046ece829b047ed3bbb11c419010569e0750d5747a578f001aef3f")]
    [InlineData("speech", 2, 32000, 960, 10, 1, 0, 0, "5f0d16d64960b08689dd3ade4c2ef13266c715256a765907a35558b2b298b0dd")]
    [InlineData("speech", 1, 16000, 2880, 10, 1, 0, 0, "3584441033b9a19f8631a040bd723a2344761fa51ba4f754b3bfcbc0b84ab041")]
    [InlineData("music", 2, 96000, 120, 10, 1, 0, 0, "fa16373fcd533534097004d9896fa4ecf9f0f12564e0a1fb98ba231625c65979")]
    [InlineData("music", 2, 64000, 960, 0, 1, 0, 0, "cd95ace9f41c809c55e1332295f79f583056189bb85920bf5947b383ce6c0873")]
    [InlineData("music", 2, 64000, 960, 10, 0, 0, 0, "0697fca17c8c3b47bee9e395720abddacb7954a2aef88ae8426f771dd691b348")]
    [InlineData("music", 2, 64000, 960, 10, 1, 1, 0, "b948e8f401d668c88d47b33003b409d610a9b0b01b662e18631ead816f548d04")]
    [InlineData("speech", 1, 20000, 960, 10, 1, 0, 15, "14ab305b94420dc4758d94dc04267b52ab84bd7bf9d0a8c8161d72a25566fca0")]
    [InlineData("transients", 2, 112000, 960, 10, 1, 0, 0, "5702c1ac17f3b6e903105b95f6d56155b61078e3f7ec86ae12ceb226deef1c22")]
    [InlineData("silence", 2, 112000, 960, 10, 1, 0, 0, "be0bd78d75c2c3bb5ae15fc37b299e3c735645dc486e49aa8db841a70378deb7")]
    public void Encode_GivesLibopussPackets(string signal, int channels, int bitrate, int frame, int complexity, int vbr, int constrained, int loss, string sha256)
    {
        float[] pcm = OpusTestSignal.Make(signal, channels, 3);
        var encoder = new OpusEncoderState(48000, channels, OpusEncoderState.OPUS_APPLICATION_AUDIO);
        encoder.SetBitrate(bitrate);
        encoder.SetComplexity(complexity);
        encoder.SetVBR(vbr);
        encoder.SetVBRConstraint(constrained);
        encoder.SetPacketLossPerc(loss);
        encoder.SetInbandFEC(loss > 0 ? 1 : 0);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var packet = new byte[MaxPacket];
        for (int at = 0; at + (frame * channels) <= pcm.Length; at += frame * channels)
        {
            int length = encoder.Encode(pcm.AsSpan(at, frame * channels), frame, packet);
            Assert.True(length > 0, $"packet at {at / channels}: {length}");
            hash.AppendData([(byte)(length & 255), (byte)(length >> 8)]);
            hash.AppendData(packet, 0, length);
            hash.AppendData(BitConverter.GetBytes(encoder.GetFinalRange()));
        }

        Assert.Equal(sha256, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    [Fact]
    public void Encode_AllocatesNothing_OnceMade()
    {
        float[] pcm = OpusTestSignal.Make("music", 2, 1);
        var packet = new byte[MaxPacket];

        // The whole signal once first, so every path's one-time setup (static tables the first frame
        // doesn't reach, which another test may or may not have touched already) is done
        var warm = new OpusEncoderState(48000, 2, OpusEncoderState.OPUS_APPLICATION_AUDIO);
        for (int at = 0; at + 1920 <= pcm.Length; at += 1920)
        {
            warm.Encode(pcm.AsSpan(at, 1920), 960, packet);
        }

        var encoder = new OpusEncoderState(48000, 2, OpusEncoderState.OPUS_APPLICATION_AUDIO);
        encoder.Encode(pcm.AsSpan(0, 1920), 960, packet);

        Allocations.AssertNone(() =>
        {
            for (int at = 1920; at + 1920 <= pcm.Length; at += 1920)
            {
                encoder.Encode(pcm.AsSpan(at, 1920), 960, packet);
            }
        });
    }
}
