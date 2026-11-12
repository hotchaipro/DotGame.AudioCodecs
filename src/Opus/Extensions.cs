/* Copyright (c) 2022 Amazon
   Ported to C# from libopus 1.6.1 (src/extensions.c) by Hot Chai Games LLC */
/*
   Redistribution and use in source and binary forms, with or without
   modification, are permitted provided that the following conditions
   are met:

   - Redistributions of source code must retain the above copyright
   notice, this list of conditions and the following disclaimer.

   - Redistributions in binary form must reproduce the above copyright
   notice, this list of conditions and the following disclaimer in the
   documentation and/or other materials provided with the distribution.

   THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
   ``AS IS'' AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
   LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
   A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER
   OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL,
   EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO,
   PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR
   PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF
   LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
   NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
   SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
*/

using System;
using System.Diagnostics;
using static DotGame.AudioCodecs.Opus.Celt.Arch;
using static DotGame.AudioCodecs.Opus.OpusDefines;

namespace DotGame.AudioCodecs.Opus;

/// <summary>
/// opus_private.h's opus_extension_data: an extension in a packet's padding. C's data pointer is
/// a slice of the packet here.
/// </summary>
internal struct opus_extension_data
{
    internal int id;
    internal int frame;
    internal ReadOnlyMemory<byte> data;
    internal int len;
}

/// <summary>
/// opus_private.h's OpusExtensionIterator. C's pointers into the padding (data, curr_data,
/// repeat_data, last_long, src_data) are offsets into <see cref="data"/> here, -1 for NULL.
/// </summary>
internal struct OpusExtensionIterator
{
    internal ReadOnlyMemory<byte> data;
    internal int curr_data;
    internal int repeat_data;
    internal int last_long;
    internal int src_data;
    internal int len;
    internal int curr_len;
    internal int repeat_len;
    internal int src_len;
    internal int trailing_short_len;
    internal int nb_frames;
    internal int frame_max;
    internal int curr_frame;
    internal int repeat_frame;
    internal byte repeat_l;
}

/// <summary>extensions.c: parsing and generating the extensions in Opus padding.</summary>
internal static class Extensions
{
    /* Given an extension payload (i.e., excluding the initial ID byte), advance
        data to the next extension and return the length of the remaining
        extensions.
       N.B., a "Repeat These Extensions" extension (ID==2) does not advance past
        the repeated extension payloads.
       That requires higher-level logic. */
    private static int skip_extension_payload(ReadOnlySpan<byte> buf, ref int pdata,
        int len, ref int pheader_size, int id_byte,
        int trailing_short_len)
    {
        int data;
        int header_size;
        int id, L;
        data = pdata;
        header_size = 0;
        id = id_byte >> 1;
        L = id_byte & 1;
        if ((id == 0 && L == 1) || id == 2)
        {
            /* Nothing to do. */
        }
        else if (id > 0 && id < 32)
        {
            if (len < L)
            {
                return -1;
            }
            data += L;
            len -= L;
        }
        else
        {
            if (L == 0)
            {
                if (len < trailing_short_len)
                {
                    return -1;
                }
                data += len - trailing_short_len;
                len = trailing_short_len;
            }
            else
            {
                int bytes = 0;
                int lacing;
                do
                {
                    if (len < 1)
                    {
                        return -1;
                    }
                    lacing = buf[data++];
                    bytes += lacing;
                    header_size++;
                    len -= lacing + 1;
                }
                while (lacing == 255);
                if (len < 0)
                {
                    return -1;
                }
                data += bytes;
            }
        }
        pdata = data;
        pheader_size = header_size;
        return len;
    }

    /* Given an extension, advance data to the next extension and return the
       length of the remaining extensions.
       N.B., a "Repeat These Extensions" extension (ID==2) only advances past the
        extension ID byte.
       Higher-level logic is required to skip the extension payloads that come
        after it.*/
    private static int skip_extension(ReadOnlySpan<byte> buf, ref int pdata, int len,
        ref int pheader_size)
    {
        int data;
        int id_byte;
        if (len == 0)
        {
            pheader_size = 0;
            return 0;
        }
        if (len < 1)
        {
            return -1;
        }
        data = pdata;
        id_byte = buf[data++];
        len--;
        len = skip_extension_payload(buf, ref data, len, ref pheader_size, id_byte, 0);
        if (len >= 0)
        {
            pdata = data;
            pheader_size++;
        }
        return len;
    }

    internal static void opus_extension_iterator_init(ref OpusExtensionIterator iter,
        ReadOnlyMemory<byte> data, int len, int nb_frames)
    {
        Debug.Assert(len >= 0);
        Debug.Assert(nb_frames >= 0 && nb_frames <= 48);
        iter.data = data;
        iter.repeat_data = iter.curr_data = 0;
        iter.last_long = iter.src_data = -1;
        iter.curr_len = iter.len = len;
        iter.repeat_len = iter.src_len = 0;
        iter.trailing_short_len = 0;
        iter.frame_max = iter.nb_frames = nb_frames;
        iter.repeat_frame = iter.curr_frame = 0;
        iter.repeat_l = 0;
    }

    /* Reset the iterator so it can start iterating again from the first
        extension. */
    internal static void opus_extension_iterator_reset(ref OpusExtensionIterator iter)
    {
        iter.repeat_data = iter.curr_data = 0;
        iter.last_long = -1;
        iter.curr_len = iter.len;
        iter.repeat_frame = iter.curr_frame = 0;
        iter.trailing_short_len = 0;
    }

    /* Tell the iterator not to return any extensions for frames of index
        frame_max or larger.
       This can allow it to stop iterating early if these extensions are not
        needed. */
    internal static void opus_extension_iterator_set_frame_max(ref OpusExtensionIterator iter,
        int frame_max)
    {
        iter.frame_max = frame_max;
    }

    /* Return the next repeated extension.
       The return value is non-zero if one is found, negative on error, or 0 if we
        have finished repeating extensions. */
    private static int opus_extension_iterator_next_repeat(ref OpusExtensionIterator iter,
        ref opus_extension_data ext, bool haveExt)
    {
        int header_size = 0;
        ReadOnlySpan<byte> buf = iter.data.Span;
        Debug.Assert(iter.repeat_frame > 0);
        for (; iter.repeat_frame < iter.nb_frames; iter.repeat_frame++)
        {
            while (iter.src_len > 0)
            {
                int curr_data0;
                int repeat_id_byte;
                repeat_id_byte = buf[iter.src_data];
                iter.src_len = skip_extension(buf, ref iter.src_data, iter.src_len,
                    ref header_size);
                /* We skipped this extension earlier, so it should not fail now. */
                Debug.Assert(iter.src_len >= 0);
                /* Don't repeat padding or frame separators with a 0 increment. */
                if (repeat_id_byte <= 3)
                {
                    continue;
                }
                /* If the "Repeat These Extensions" extension had L == 0 and this
                    is the last repeated long extension, then force decoding the
                    payload with L = 0. */
                if (iter.repeat_l == 0
                    && iter.repeat_frame + 1 >= iter.nb_frames
                    && iter.src_data == iter.last_long)
                {
                    repeat_id_byte &= ~1;
                }
                curr_data0 = iter.curr_data;
                iter.curr_len = skip_extension_payload(buf, ref iter.curr_data,
                    iter.curr_len, ref header_size, repeat_id_byte,
                    iter.trailing_short_len);
                if (iter.curr_len < 0)
                {
                    return OPUS_INVALID_PACKET;
                }
                Debug.Assert(iter.curr_data == iter.len - iter.curr_len);
                /* If we were asked to stop at frame_max, skip extensions for later
                    frames. */
                if (iter.repeat_frame >= iter.frame_max)
                {
                    continue;
                }
                if (haveExt)
                {
                    ext.id = repeat_id_byte >> 1;
                    ext.frame = iter.repeat_frame;
                    ext.len = iter.curr_data - curr_data0 - header_size;
                    ext.data = iter.data.Slice(curr_data0 + header_size, ext.len);
                }
                return 1;
            }
            /* We finished repeating the extensions for this frame. */
            iter.src_data = iter.repeat_data;
            iter.src_len = iter.repeat_len;
        }
        /* We finished repeating extensions. */
        iter.repeat_data = iter.curr_data;
        iter.last_long = -1;
        /* If L == 0, advance the frame number to handle the case where we did
            not consume all of the data with an L == 0 long extension. */
        if (iter.repeat_l == 0)
        {
            iter.curr_frame++;
            /* Ignore additional padding if this was already the last frame. */
            if (iter.curr_frame >= iter.nb_frames)
            {
                iter.curr_len = 0;
            }
        }
        iter.repeat_frame = 0;
        return 0;
    }

    /* Return the next extension (excluding real padding, separators, and repeat
        indicators, but including the repeated extensions) in bitstream order.
       Due to the extension repetition mechanism, extensions are not necessarily
        returned in frame order. haveExt false is C's ext == NULL. */
    internal static int opus_extension_iterator_next(ref OpusExtensionIterator iter,
        ref opus_extension_data ext, bool haveExt)
    {
        int header_size = 0;
        if (iter.curr_len < 0)
        {
            return OPUS_INVALID_PACKET;
        }
        if (iter.repeat_frame > 0)
        {
            int ret;
            /* We are in the process of repeating some extensions. */
            ret = opus_extension_iterator_next_repeat(ref iter, ref ext, haveExt);
            if (ret != 0)
            {
                return ret;
            }
        }
        /* Checking this here allows opus_extension_iterator_set_frame_max() to be
            called at any point. */
        if (iter.curr_frame >= iter.frame_max)
        {
            return 0;
        }
        ReadOnlySpan<byte> buf = iter.data.Span;
        while (iter.curr_len > 0)
        {
            int curr_data0;
            int id;
            int L;
            curr_data0 = iter.curr_data;
            id = buf[curr_data0] >> 1;
            L = buf[curr_data0] & 1;
            iter.curr_len = skip_extension(buf, ref iter.curr_data, iter.curr_len,
                ref header_size);
            if (iter.curr_len < 0)
            {
                return OPUS_INVALID_PACKET;
            }
            Debug.Assert(iter.curr_data == iter.len - iter.curr_len);
            if (id == 1)
            {
                if (L == 0)
                {
                    iter.curr_frame++;
                }
                else
                {
                    /* A frame increment of 0 is a no-op. */
                    if (buf[curr_data0 + 1] == 0)
                    {
                        continue;
                    }
                    iter.curr_frame += buf[curr_data0 + 1];
                }
                if (iter.curr_frame >= iter.nb_frames)
                {
                    iter.curr_len = -1;
                    return OPUS_INVALID_PACKET;
                }
                /* If we were asked to stop at frame_max, skip extensions for later
                    frames. */
                if (iter.curr_frame >= iter.frame_max)
                {
                    iter.curr_len = 0;
                }
                iter.repeat_data = iter.curr_data;
                iter.last_long = -1;
                iter.trailing_short_len = 0;
            }
            else if (id == 2)
            {
                int ret;
                iter.repeat_l = (byte)L;
                iter.repeat_frame = iter.curr_frame + 1;
                iter.repeat_len = curr_data0 - iter.repeat_data;
                iter.src_data = iter.repeat_data;
                iter.src_len = iter.repeat_len;
                ret = opus_extension_iterator_next_repeat(ref iter, ref ext, haveExt);
                if (ret != 0)
                {
                    return ret;
                }
            }
            else if (id > 2)
            {
                /* Update the location of the last long extension.
                   This lets us know when we need to modify the last L flag if we
                    repeat these extensions with L=0. */
                if (id >= 32)
                {
                    iter.last_long = iter.curr_data;
                    iter.trailing_short_len = 0;
                }
                /* Otherwise, keep track of how many payload bytes follow the last
                    long extension. */
                else
                {
                    iter.trailing_short_len += L;
                }
                if (haveExt)
                {
                    ext.id = id;
                    ext.frame = iter.curr_frame;
                    ext.len = iter.curr_data - curr_data0 - header_size;
                    ext.data = iter.data.Slice(curr_data0 + header_size, ext.len);
                }
                return 1;
            }
        }
        return 0;
    }

    internal static int opus_extension_iterator_find(ref OpusExtensionIterator iter,
        ref opus_extension_data ext, int id)
    {
        opus_extension_data curr_ext = default;
        int ret;
        for (; ; )
        {
            ret = opus_extension_iterator_next(ref iter, ref curr_ext, true);
            if (ret <= 0)
            {
                return ret;
            }
            if (curr_ext.id == id)
            {
                ext = curr_ext;
                return ret;
            }
        }
    }

    /* Count the number of extensions, excluding real padding, separators, and
        repeat indicators, but including the repeated extensions. */
    internal static int opus_packet_extensions_count(ReadOnlyMemory<byte> data,
        int len, int nb_frames)
    {
        OpusExtensionIterator iter = default;
        opus_extension_data none = default;
        int count;
        opus_extension_iterator_init(ref iter, data, len, nb_frames);
        for (count = 0; opus_extension_iterator_next(ref iter, ref none, false) > 0; count++)
        {
        }
        return count;
    }

    /* Count the number of extensions for each frame, excluding real padding and
        separators and repeat indicators, but including the repeated extensions. */
    internal static int opus_packet_extensions_count_ext(ReadOnlyMemory<byte> data,
        int len, Span<int> nb_frame_exts, int nb_frames)
    {
        OpusExtensionIterator iter = default;
        opus_extension_data ext = default;
        int count;
        opus_extension_iterator_init(ref iter, data, len, nb_frames);
        nb_frame_exts.Slice(0, nb_frames).Clear();
        for (count = 0; opus_extension_iterator_next(ref iter, ref ext, true) > 0; count++)
        {
            nb_frame_exts[ext.frame]++;
        }
        return count;
    }

    /* Extract extensions from Opus padding (excluding real padding, separators,
        and repeat indicators, but including the repeated extensions) in bitstream
        order.
       Due to the extension repetition mechanism, extensions are not necessarily
        returned in frame order. */
    internal static int opus_packet_extensions_parse(ReadOnlyMemory<byte> data,
        int len, Span<opus_extension_data> extensions, ref int nb_extensions,
        int nb_frames)
    {
        OpusExtensionIterator iter = default;
        int count;
        int ret;
        opus_extension_iterator_init(ref iter, data, len, nb_frames);
        for (count = 0; ; count++)
        {
            opus_extension_data ext = default;
            ret = opus_extension_iterator_next(ref iter, ref ext, true);
            if (ret <= 0)
            {
                break;
            }
            if (count == nb_extensions)
            {
                return OPUS_BUFFER_TOO_SMALL;
            }
            extensions[count] = ext;
        }
        nb_extensions = count;
        return ret;
    }

    /* Extract extensions from Opus padding (excluding real padding, separators,
        and repeat indicators, but including the repeated extensions) in frame
        order.
       nb_frame_exts must be filled with the output of
        opus_packet_extensions_count_ext(). */
    internal static int opus_packet_extensions_parse_ext(ReadOnlyMemory<byte> data,
        int len, Span<opus_extension_data> extensions, ref int nb_extensions,
        ReadOnlySpan<int> nb_frame_exts, int nb_frames)
    {
        OpusExtensionIterator iter = default;
        opus_extension_data ext = default;
        Span<int> nb_frames_cum = stackalloc int[49];
        int count;
        int prev_total;
        int ret;
        Debug.Assert(nb_frames <= 48);
        /* Convert the frame extension count array to a cumulative sum. */
        prev_total = 0;
        for (count = 0; count < nb_frames; count++)
        {
            int total;
            total = nb_frame_exts[count] + prev_total;
            nb_frames_cum[count] = prev_total;
            prev_total = total;
        }
        nb_frames_cum[count] = prev_total;
        opus_extension_iterator_init(ref iter, data, len, nb_frames);
        for (count = 0; ; count++)
        {
            int idx;
            ret = opus_extension_iterator_next(ref iter, ref ext, true);
            if (ret <= 0)
            {
                break;
            }
            idx = nb_frames_cum[ext.frame]++;
            if (idx >= nb_extensions)
            {
                return OPUS_BUFFER_TOO_SMALL;
            }
            Debug.Assert(idx < nb_frames_cum[ext.frame + 1]);
            extensions[idx] = ext;
        }
        nb_extensions = count;
        return ret;
    }

    // haveData false is C's data == NULL (measuring only)
    private static int write_extension_payload(Span<byte> data, bool haveData, int len,
        int pos, in opus_extension_data ext, int last)
    {
        Debug.Assert(ext.id >= 3 && ext.id <= 127);
        if (ext.id < 32)
        {
            if (ext.len < 0 || ext.len > 1)
            {
                return OPUS_BAD_ARG;
            }
            if (ext.len > 0)
            {
                if (len - pos < ext.len)
                {
                    return OPUS_BUFFER_TOO_SMALL;
                }
                if (haveData)
                {
                    data[pos] = ext.data.Span[0];
                }
                pos++;
            }
        }
        else
        {
            int length_bytes;
            if (ext.len < 0)
            {
                return OPUS_BAD_ARG;
            }
            length_bytes = 1 + (ext.len / 255);
            if (last != 0)
            {
                length_bytes = 0;
            }
            if (len - pos < length_bytes + ext.len)
            {
                return OPUS_BUFFER_TOO_SMALL;
            }
            if (last == 0)
            {
                int j;
                for (j = 0; j < ext.len / 255; j++)
                {
                    if (haveData)
                    {
                        data[pos] = 255;
                    }
                    pos++;
                }
                if (haveData)
                {
                    data[pos] = (byte)(ext.len % 255);
                }
                pos++;
            }
            if (haveData)
            {
                ext.data.Span.Slice(0, ext.len).CopyTo(data.Slice(pos));
            }
            pos += ext.len;
        }
        return pos;
    }

    private static int write_extension(Span<byte> data, bool haveData, int len, int pos,
        in opus_extension_data ext, int last)
    {
        if (len - pos < 1)
        {
            return OPUS_BUFFER_TOO_SMALL;
        }
        Debug.Assert(ext.id >= 3 && ext.id <= 127);
        if (haveData)
        {
            data[pos] = (byte)((ext.id << 1) + (ext.id < 32 ? ext.len : (last == 0 ? 1 : 0)));
        }
        pos++;
        return write_extension_payload(data, haveData, len, pos, in ext, last);
    }

    // haveData false is C's data == NULL (measuring only)
    internal static int opus_packet_extensions_generate(Span<byte> data, bool haveData, int len,
        ReadOnlySpan<opus_extension_data> extensions, int nb_extensions,
        int nb_frames, int pad)
    {
        Span<int> frame_min_idx = stackalloc int[48];
        Span<int> frame_max_idx = stackalloc int[48];
        Span<int> frame_repeat_idx = stackalloc int[48];
        int i;
        int f;
        int curr_frame = 0;
        int pos = 0;
        int written = 0;

        Debug.Assert(len >= 0);
        if (nb_frames > 48)
        {
            return OPUS_BAD_ARG;
        }

        /* Do a little work up-front to make this O(nb_extensions) instead of
            O(nb_extensions*nb_frames) so long as the extensions are in frame
            order (without requiring that they be in frame order). */
        for (f = 0; f < nb_frames; f++)
        {
            frame_min_idx[f] = nb_extensions;
        }
        frame_max_idx.Slice(0, nb_frames).Clear();
        for (i = 0; i < nb_extensions; i++)
        {
            f = extensions[i].frame;
            if (f < 0 || f >= nb_frames)
            {
                return OPUS_BAD_ARG;
            }
            if (extensions[i].id < 3 || extensions[i].id > 127)
            {
                return OPUS_BAD_ARG;
            }
            frame_min_idx[f] = IMIN(frame_min_idx[f], i);
            frame_max_idx[f] = IMAX(frame_max_idx[f], i + 1);
        }
        for (f = 0; f < nb_frames; f++)
        {
            frame_repeat_idx[f] = frame_min_idx[f];
        }
        for (f = 0; f < nb_frames; f++)
        {
            int last_long_idx;
            int repeat_count;
            repeat_count = 0;
            last_long_idx = -1;
            if (f + 1 < nb_frames)
            {
                for (i = frame_min_idx[f]; i < frame_max_idx[f]; i++)
                {
                    if (extensions[i].frame == f)
                    {
                        int g;
                        /* Test if we can repeat this extension in future frames. */
                        for (g = f + 1; g < nb_frames; g++)
                        {
                            if (frame_repeat_idx[g] >= frame_max_idx[g])
                            {
                                break;
                            }
                            Debug.Assert(extensions[frame_repeat_idx[g]].frame == g);
                            if (extensions[frame_repeat_idx[g]].id != extensions[i].id)
                            {
                                break;
                            }
                            if (extensions[frame_repeat_idx[g]].id < 32
                                && extensions[frame_repeat_idx[g]].len
                                != extensions[i].len)
                            {
                                break;
                            }
                        }
                        if (g < nb_frames)
                        {
                            break;
                        }
                        /* We can! */
                        /* If this is a long extension, save the index of the last
                            instance, so we can modify its L flag. */
                        if (extensions[i].id >= 32)
                        {
                            last_long_idx = frame_repeat_idx[nb_frames - 1];
                        }
                        /* Using the repeat mechanism almost always makes the
                            encoding smaller (or at least no larger).
                           However, there's one case where that might not be true: if
                            the last repeated long extension in the last frame was
                            previously the last extension, but using the repeat
                            mechanism makes that no longer true (because there are other
                            non-repeated extensions in earlier frames that must now be
                            coded after it), and coding its length requires more bytes
                            than the repeat mechanism saves.
                           This can only be true if its length is at least 255 bytes
                            (although sometimes it requires even more).
                           Currently we do not check for that, and just always use the
                            repeat mechanism if we can.
                           See git history for code that does the check. */
                        /* Advance the repeat pointers. */
                        for (g = f + 1; g < nb_frames; g++)
                        {
                            int j;
                            for (j = frame_repeat_idx[g] + 1; j < frame_max_idx[g]
                                && extensions[j].frame != g; j++)
                            {
                            }
                            frame_repeat_idx[g] = j;
                        }
                        repeat_count++;
                        /* Point the repeat pointer for this frame to the current
                            extension, so we know when to trigger the repeats. */
                        frame_repeat_idx[f] = i;
                    }
                }
            }
            for (i = frame_min_idx[f]; i < frame_max_idx[f]; i++)
            {
                if (extensions[i].frame == f)
                {
                    /* Insert separator when needed. */
                    if (f != curr_frame)
                    {
                        int diff = f - curr_frame;
                        if (len - pos < 2)
                        {
                            return OPUS_BUFFER_TOO_SMALL;
                        }
                        if (diff == 1)
                        {
                            if (haveData)
                            {
                                data[pos] = 0x02;
                            }
                            pos++;
                        }
                        else
                        {
                            if (haveData)
                            {
                                data[pos] = 0x03;
                            }
                            pos++;
                            if (haveData)
                            {
                                data[pos] = (byte)diff;
                            }
                            pos++;
                        }
                        curr_frame = f;
                    }

                    pos = write_extension(data, haveData, len, pos, in extensions[i],
                        written == nb_extensions - 1 ? 1 : 0);
                    if (pos < 0)
                    {
                        return pos;
                    }
                    written++;

                    if (repeat_count > 0 && frame_repeat_idx[f] == i)
                    {
                        int nb_repeated;
                        int last;
                        int g;
                        /* Add the repeat indicator. */
                        nb_repeated = repeat_count * (nb_frames - (f + 1));
                        last = (written + nb_repeated == nb_extensions
                            || (last_long_idx < 0 && i + 1 >= frame_max_idx[f])) ? 1 : 0;
                        if (len - pos < 1)
                        {
                            return OPUS_BUFFER_TOO_SMALL;
                        }
                        if (haveData)
                        {
                            data[pos] = (byte)(0x04 + (last == 0 ? 1 : 0));
                        }
                        pos++;
                        for (g = f + 1; g < nb_frames; g++)
                        {
                            int j;
                            for (j = frame_min_idx[g]; j < frame_repeat_idx[g]; j++)
                            {
                                if (extensions[j].frame == g)
                                {
                                    pos = write_extension_payload(data, haveData, len, pos,
                                        in extensions[j], (last != 0 && j == last_long_idx) ? 1 : 0);
                                    if (pos < 0)
                                    {
                                        return pos;
                                    }
                                    written++;
                                }
                            }
                            frame_min_idx[g] = j;
                        }
                        if (last != 0)
                        {
                            curr_frame++;
                        }
                    }
                }
            }
        }
        Debug.Assert(written == nb_extensions);
        /* If we need to pad, just prepend 0x01 bytes. Even better would be to fill the
           end with zeros, but that requires checking that turning the last extension into
           an L=1 case still fits. */
        if (pad != 0 && pos < len)
        {
            int padding = len - pos;
            if (haveData)
            {
                data.Slice(0, pos).CopyTo(data.Slice(padding));
                for (i = 0; i < padding; i++)
                {
                    data[i] = 0x01;
                }
            }
            pos += padding;
        }
        return pos;
    }
}
