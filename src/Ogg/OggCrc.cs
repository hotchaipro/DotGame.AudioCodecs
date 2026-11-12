// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;

namespace DotGame.AudioCodecs.Ogg;

/// <summary>The Ogg page CRC (RFC 3533): polynomial 0x04C11DB7, unreflected, from 0 (<see cref="Crc.Ogg"/>).</summary>
internal static class OggCrc
{
    /// <summary>The CRC of <paramref name="page"/> with its CRC field (bytes 22 to 25) taken as zero.</summary>
    public static uint OfPage(ReadOnlySpan<byte> page)
    {
        uint crc = Crc.Ogg.Update(0, page[..22]);
        crc = Crc.Ogg.Update(crc, [0, 0, 0, 0]);
        return Crc.Ogg.Update(crc, page[26..]);
    }
}
