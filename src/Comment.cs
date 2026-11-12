// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DotGame.AudioCodecs;

/// <summary>
/// Vorbis comments, as FLAC's VORBIS_COMMENT block and Opus's OpusTags hold them: a vendor string, then
/// "KEY=value" strings in UTF-8, each with a 32-bit little-endian length.
/// </summary>
internal static class Comment
{
    /// <summary>The vendor string of the Opus encoder: the library and the libopus it ports.</summary>
    public const string OpusVendor = "DotGame.AudioCodecs " + LibraryVersion.Value + " (libopus 1.6.1)";

    /// <summary>The vendor string of the FLAC encoder.</summary>
    public const string FlacVendor = "DotGame.AudioCodecs " + LibraryVersion.Value;

    /// <summary>Throws unless each comment is "KEY=value" with a key of printable ASCII but '='.</summary>
    public static void Validate(IReadOnlyList<string> comments, string parameter)
    {
        foreach (string comment in comments)
        {
            int equals = comment?.IndexOf('=', StringComparison.Ordinal) ?? -1;
            if (equals <= 0)
            {
                throw new ArgumentException($"The comment \"{comment}\" isn't KEY=value.", parameter);
            }
            for (int i = 0; i < equals; i++)
            {
                if (comment![i] is < (char)0x20 or > (char)0x7D)
                {
                    throw new ArgumentException($"The comment key \"{comment[..equals]}\" isn't printable ASCII.", parameter);
                }
            }
        }
    }

    /// <summary>The length of the vendor string and comments as written.</summary>
    public static int Length(string vendor, IReadOnlyList<string> comments)
    {
        int length = 4 + Encoding.UTF8.GetByteCount(vendor) + 4;
        foreach (string comment in comments)
        {
            length += 4 + Encoding.UTF8.GetByteCount(comment);
        }
        return length;
    }

    /// <summary>Writes the vendor string and comments; returns the bytes written.</summary>
    public static int Write(Span<byte> output, string vendor, IReadOnlyList<string> comments)
    {
        int at = WriteString(output, vendor);
        BinaryPrimitives.WriteUInt32LittleEndian(output[at..], (uint)comments.Count);
        at += 4;
        foreach (string comment in comments)
        {
            at += WriteString(output[at..], comment);
        }
        return at;
    }

    /// <summary>Reads the comments (not the vendor) after a vendor string; throws if they don't fit.</summary>
    public static string[] Read(ReadOnlySpan<byte> data, string format)
    {
        int at = 0;
        Skip(data, ref at, format);
        uint count = ReadLength(data, ref at, format);
        if (count > (uint)(data.Length - at) / 4)
        {
            throw new InvalidDataException($"The {format} comments claim {count} entries, more than fit.");
        }
        var comments = new string[count];
        for (int i = 0; i < comments.Length; i++)
        {
            int start = at;
            Skip(data, ref at, format);
            comments[i] = Encoding.UTF8.GetString(data[(start + 4)..at]);
        }
        return comments;
    }

    private static int WriteString(Span<byte> output, string value)
    {
        int length = Encoding.UTF8.GetBytes(value, output[4..]);
        BinaryPrimitives.WriteUInt32LittleEndian(output, (uint)length);
        return 4 + length;
    }

    private static void Skip(ReadOnlySpan<byte> data, ref int at, string format)
    {
        uint length = ReadLength(data, ref at, format);
        if (length > (uint)(data.Length - at))
        {
            throw new InvalidDataException($"A {format} comment runs past its block.");
        }
        at += (int)length;
    }

    private static uint ReadLength(ReadOnlySpan<byte> data, ref int at, string format)
    {
        if (data.Length - at < 4)
        {
            throw new InvalidDataException($"The {format} comments are cut short.");
        }
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(data[at..]);
        at += 4;
        return length;
    }
}
