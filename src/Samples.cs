// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Runtime.CompilerServices;

namespace DotGame.AudioCodecs;

/// <summary>
/// Sample conversions shared by the formats. Integers of <c>bits</c> bits are signed and
/// right-aligned. Fewer bits to more is exact; more to fewer keeps the top bits, as libsndfile, dr_flac
/// and ffmpeg give; float to integer rounds to the nearest (ties to even, as C's <c>lrintf</c>) and
/// clips at ±1.
/// </summary>
internal static class Samples
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static short IntToShort(int value, int bits) =>
        (short)(bits >= 16 ? value >> (bits - 16) : value << (16 - bits));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float IntToFloat(int value, int bits) => value * Scale(bits);

    /// <summary>1 / 2^(bits - 1): an integer of <paramref name="bits"/> bits to -1..1.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Scale(int bits) => 1f / (1L << (bits - 1));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ShortToInt(short value, int bits)
    {
        if (bits >= 16)
        {
            return value << (bits - 16);
        }
        int shift = 16 - bits;
        int max = (1 << (bits - 1)) - 1;
        return Math.Min((value + (1 << (shift - 1))) >> shift, max);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int FloatToInt(float value, int bits)
    {
        if (float.IsNaN(value))
        {
            return 0;
        }
        double scaled = Math.Round(value * (double)(1L << (bits - 1)), MidpointRounding.ToEven);
        double max = (1L << (bits - 1)) - 1;
        return (int)Math.Clamp(scaled, -max - 1, max);
    }

    /// <summary>A float sample to 16 bits: libopus's <c>FLOAT2INT16</c> (scale, round, saturate).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static short FloatToShort(float value) => (short)FloatToInt(value, 16);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ShortToFloat(short value) => value * (1f / 32768f);
}
