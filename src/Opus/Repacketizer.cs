/* Copyright (c) 2011 Xiph.Org Foundation
   Written by Jean-Marc Valin
   Ported to C# from libopus 1.6.1 (src/repacketizer.c, and src/opus.c's encode_size) by Hot Chai Games LLC */
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

using static DotGame.AudioCodecs.Opus.OpusDefines;

using System;

namespace DotGame.AudioCodecs.Opus;

/// <summary>
/// opus_private.h's struct OpusRepacketizer. C's frame and padding pointers are slices of the
/// packets given to <see cref="Repacketizer.opus_repacketizer_cat"/> (default for NULL). It also
/// owns the scratch C allocates on the stack in opus_repacketizer_out_range_impl (all_extensions)
/// and opus_packet_pad_impl (copy), so repacketizing allocates nothing.
/// </summary>
internal sealed class OpusRepacketizer
{
    internal byte toc;
    internal int nb_frames;
    internal readonly ReadOnlyMemory<byte>[] frames = new ReadOnlyMemory<byte>[48];
    internal readonly short[] len = new short[48];
    internal int framesize;
    internal readonly ReadOnlyMemory<byte>[] paddings = new ReadOnlyMemory<byte>[48];
    internal readonly int[] padding_len = new int[48];
    internal readonly byte[] padding_nb_frames = new byte[48];

    // opus_repacketizer_out_range_impl's all_extensions (grown only if a packet has more)
    internal opus_extension_data[] all_extensions = new opus_extension_data[16];

    // opus_packet_pad_impl's copy of the packet (grown only for packets longer than the encoder makes)
    internal byte[] copy = new byte[1276 * 6];
}

/// <summary>repacketizer.c: what the encoder uses to pad packets and join frames.</summary>
internal static class Repacketizer
{
    // src/opus.c
    internal static int encode_size(int size, Span<byte> data)
    {
        if (size < 252)
        {
            data[0] = (byte)size;
            return 1;
        }
        else
        {
            data[0] = (byte)(252 + (size & 0x3));
            data[1] = (byte)((size - (int)data[0]) >> 2);
            return 2;
        }
    }

    internal static OpusRepacketizer opus_repacketizer_init(OpusRepacketizer rp)
    {
        rp.nb_frames = 0;
        return rp;
    }

    // opus_packet_parse_impl with frame and padding pointers, over the decoder's port (which
    // returns offsets)
    private static int opus_packet_parse_impl(ReadOnlyMemory<byte> data, int len,
        int self_delimited, out byte out_toc, Span<ReadOnlyMemory<byte>> frames, Span<short> size,
        out ReadOnlyMemory<byte> padding, out int padding_len)
    {
        int i;
        int count = OpusDecoderState.opus_packet_parse_impl(data.Span, len, self_delimited, out out_toc, size,
            out int payload_offset, out _, out int padding_offset, out padding_len);
        padding = default;
        if (count < 0)
        {
            return count;
        }
        int offset = payload_offset;
        for (i = 0; i < count; i++)
        {
            frames[i] = data.Slice(offset, size[i]);
            offset += size[i];
        }
        padding = data.Slice(padding_offset, padding_len);
        return count;
    }

    private static int opus_repacketizer_cat_impl(OpusRepacketizer rp, ReadOnlyMemory<byte> data, int len, int self_delimited)
    {
        byte tmp_toc;
        int curr_nb_frames, ret;
        /* Set of check ToC */
        if (len < 1)
        {
            return OPUS_INVALID_PACKET;
        }
        ReadOnlySpan<byte> d = data.Span;
        if (rp.nb_frames == 0)
        {
            rp.toc = d[0];
            rp.framesize = OpusDecoderState.opus_packet_get_samples_per_frame(d, 8000);
        }
        else if ((rp.toc & 0xFC) != (d[0] & 0xFC))
        {
            return OPUS_INVALID_PACKET;
        }
        curr_nb_frames = OpusDecoderState.opus_packet_get_nb_frames(d, len);
        if (curr_nb_frames < 1)
        {
            return OPUS_INVALID_PACKET;
        }

        /* Check the 120 ms maximum packet size */
        if ((curr_nb_frames + rp.nb_frames) * rp.framesize > 960)
        {
            return OPUS_INVALID_PACKET;
        }

        ret = opus_packet_parse_impl(data, len, self_delimited, out tmp_toc, rp.frames.AsSpan(rp.nb_frames), rp.len.AsSpan(rp.nb_frames),
            out rp.paddings[rp.nb_frames], out rp.padding_len[rp.nb_frames]);
        if (ret < 1)
        {
            return ret;
        }
        rp.padding_nb_frames[rp.nb_frames] = (byte)ret;

        /* set padding length to zero for all but the first frame */
        while (curr_nb_frames > 1)
        {
            rp.nb_frames++;
            rp.padding_len[rp.nb_frames] = 0;
            rp.padding_nb_frames[rp.nb_frames] = 0;
            rp.paddings[rp.nb_frames] = default;
            curr_nb_frames--;
        }
        rp.nb_frames++;
        return OPUS_OK;
    }

    internal static int opus_repacketizer_cat(OpusRepacketizer rp, ReadOnlyMemory<byte> data, int len)
    {
        return opus_repacketizer_cat_impl(rp, data, len, 0);
    }

    internal static int opus_repacketizer_get_nb_frames(OpusRepacketizer rp)
    {
        return rp.nb_frames;
    }

    internal static int opus_repacketizer_out_range_impl(OpusRepacketizer rp, int begin, int end,
        Span<byte> data, int maxlen, int self_delimited, int pad, ReadOnlySpan<opus_extension_data> extensions, int nb_extensions)
    {
        int i, count;
        int tot_size;
        Span<short> len;
        Span<ReadOnlyMemory<byte>> frames;
        int ptr;
        int ones_begin = 0, ones_end = 0;
        int ext_begin = 0, ext_len = 0;
        int ext_count, total_ext_count;

        if (begin < 0 || begin >= end || end > rp.nb_frames)
        {
            return OPUS_BAD_ARG;
        }
        count = end - begin;

        len = rp.len.AsSpan(begin);
        frames = rp.frames.AsSpan(begin);
        if (self_delimited != 0)
        {
            tot_size = 1 + (len[count - 1] >= 252 ? 1 : 0);
        }
        else
        {
            tot_size = 0;
        }

        /* figure out total number of extensions */
        total_ext_count = nb_extensions;
        for (i = begin; i < end; i++)
        {
            int n = Extensions.opus_packet_extensions_count(rp.paddings[i], rp.padding_len[i],
                rp.padding_nb_frames[i]);
            if (n > 0)
            {
                total_ext_count += n;
            }
        }
        if (rp.all_extensions.Length < total_ext_count)
        {
            rp.all_extensions = new opus_extension_data[total_ext_count];
        }
        Span<opus_extension_data> all_extensions = rp.all_extensions.AsSpan(0, total_ext_count);
        /* copy over any extensions that were passed in */
        for (ext_count = 0; ext_count < nb_extensions; ext_count++)
        {
            all_extensions[ext_count] = extensions[ext_count];
        }

        /* incorporate any extensions from the repacketizer padding */
        for (i = begin; i < end; i++)
        {
            int j, ret;
            int frame_ext_count;
            frame_ext_count = total_ext_count - ext_count;
            ret = Extensions.opus_packet_extensions_parse(rp.paddings[i], rp.padding_len[i],
                all_extensions.Slice(ext_count), ref frame_ext_count, rp.padding_nb_frames[i]);
            if (ret < 0)
            {
                return OPUS_INTERNAL_ERROR;
            }
            /* renumber the extension frame numbers */
            for (j = 0; j < frame_ext_count; j++)
            {
                all_extensions[ext_count + j].frame += i - begin;
            }
            ext_count += frame_ext_count;
        }

        ptr = 0;
        if (count == 1)
        {
            /* Code 0 */
            tot_size += len[0] + 1;
            if (tot_size > maxlen)
            {
                return OPUS_BUFFER_TOO_SMALL;
            }
            data[ptr++] = (byte)(rp.toc & 0xFC);
        }
        else if (count == 2)
        {
            if (len[1] == len[0])
            {
                /* Code 1 */
                tot_size += (2 * len[0]) + 1;
                if (tot_size > maxlen)
                {
                    return OPUS_BUFFER_TOO_SMALL;
                }
                data[ptr++] = (byte)((rp.toc & 0xFC) | 0x1);
            }
            else
            {
                /* Code 2 */
                tot_size += len[0] + len[1] + 2 + (len[0] >= 252 ? 1 : 0);
                if (tot_size > maxlen)
                {
                    return OPUS_BUFFER_TOO_SMALL;
                }
                data[ptr++] = (byte)((rp.toc & 0xFC) | 0x2);
                ptr += encode_size(len[0], data.Slice(ptr));
            }
        }
        if (count > 2 || (pad != 0 && tot_size < maxlen) || ext_count > 0)
        {
            /* Code 3 */
            int vbr;
            int pad_amount = 0;

            /* Restart the process for the padding case */
            ptr = 0;
            if (self_delimited != 0)
            {
                tot_size = 1 + (len[count - 1] >= 252 ? 1 : 0);
            }
            else
            {
                tot_size = 0;
            }
            vbr = 0;
            for (i = 1; i < count; i++)
            {
                if (len[i] != len[0])
                {
                    vbr = 1;
                    break;
                }
            }
            if (vbr != 0)
            {
                tot_size += 2;
                for (i = 0; i < count - 1; i++)
                {
                    tot_size += 1 + (len[i] >= 252 ? 1 : 0) + len[i];
                }
                tot_size += len[count - 1];

                if (tot_size > maxlen)
                {
                    return OPUS_BUFFER_TOO_SMALL;
                }
                data[ptr++] = (byte)((rp.toc & 0xFC) | 0x3);
                data[ptr++] = (byte)(count | 0x80);
            }
            else
            {
                tot_size += (count * len[0]) + 2;
                if (tot_size > maxlen)
                {
                    return OPUS_BUFFER_TOO_SMALL;
                }
                data[ptr++] = (byte)((rp.toc & 0xFC) | 0x3);
                data[ptr++] = (byte)count;
            }
            pad_amount = pad != 0 ? (maxlen - tot_size) : 0;
            if (ext_count > 0)
            {
                /* figure out how much space we need for the extensions */
                ext_len = Extensions.opus_packet_extensions_generate(Span<byte>.Empty, false, maxlen - tot_size,
                    all_extensions, ext_count, count, 0);
                if (ext_len < 0)
                {
                    return ext_len;
                }
                if (pad == 0)
                {
                    pad_amount = ext_len + (ext_len != 0 ? (ext_len + 253) / 254 : 1);
                }
            }
            if (pad_amount != 0)
            {
                int nb_255s;
                data[1] |= 0x40;
                nb_255s = (pad_amount - 1) / 255;
                if (tot_size + ext_len + nb_255s + 1 > maxlen)
                {
                    return OPUS_BUFFER_TOO_SMALL;
                }
                ext_begin = tot_size + pad_amount - ext_len;
                /* Prepend 0x01 padding */
                ones_begin = tot_size + nb_255s + 1;
                ones_end = tot_size + pad_amount - ext_len;
                for (i = 0; i < nb_255s; i++)
                {
                    data[ptr++] = 255;
                }
                data[ptr++] = (byte)(pad_amount - (255 * nb_255s) - 1);
                tot_size += pad_amount;
            }
            if (vbr != 0)
            {
                for (i = 0; i < count - 1; i++)
                {
                    ptr += encode_size(len[i], data.Slice(ptr));
                }
            }
        }
        if (self_delimited != 0)
        {
            int sdlen = encode_size(len[count - 1], data.Slice(ptr));
            ptr += sdlen;
        }
        /* Copy the actual data */
        for (i = 0; i < count; i++)
        {
            /* OPUS_MOVE() rather than OPUS_COPY() in case we're doing in-place
               padding from opus_packet_pad or opus_packet_unpad(). */
            frames[i].Span.Slice(0, len[i]).CopyTo(data.Slice(ptr));
            ptr += len[i];
        }
        if (ext_len > 0)
        {
            int ret = Extensions.opus_packet_extensions_generate(data.Slice(ext_begin), true, ext_len,
                all_extensions, ext_count, count, 0);
            System.Diagnostics.Debug.Assert(ret == ext_len);
        }
        for (i = ones_begin; i < ones_end; i++)
        {
            data[i] = 0x01;
        }
        if (pad != 0 && ext_count == 0)
        {
            /* Fill padding with zeros. */
            data.Slice(ptr, maxlen - ptr).Clear();
        }
        return tot_size;
    }

    internal static int opus_repacketizer_out_range(OpusRepacketizer rp, int begin, int end, Span<byte> data, int maxlen)
    {
        return opus_repacketizer_out_range_impl(rp, begin, end, data, maxlen, 0, 0, ReadOnlySpan<opus_extension_data>.Empty, 0);
    }

    internal static int opus_repacketizer_out(OpusRepacketizer rp, Span<byte> data, int maxlen)
    {
        return opus_repacketizer_out_range_impl(rp, 0, rp.nb_frames, data, maxlen, 0, 0, ReadOnlySpan<opus_extension_data>.Empty, 0);
    }

    // rp is the OpusRepacketizer C has on the stack here (with its copy buffer)
    internal static int opus_packet_pad_impl(Span<byte> data, int len, int new_len, int pad, ReadOnlySpan<opus_extension_data> extensions, int nb_extensions,
        OpusRepacketizer rp)
    {
        int ret;
        if (len < 1)
        {
            return OPUS_BAD_ARG;
        }
        if (len == new_len)
        {
            return OPUS_OK;
        }
        else if (len > new_len)
        {
            return OPUS_BAD_ARG;
        }
        if (rp.copy.Length < len)
        {
            rp.copy = new byte[len];
        }
        byte[] copy = rp.copy;
        opus_repacketizer_init(rp);
        /* Moving payload to the end of the packet so we can do in-place padding */
        data.Slice(0, len).CopyTo(copy);
        ret = opus_repacketizer_cat(rp, copy.AsMemory(0, len), len);
        if (ret != OPUS_OK)
        {
            return ret;
        }
        ret = opus_repacketizer_out_range_impl(rp, 0, rp.nb_frames, data, new_len, 0, pad, extensions, nb_extensions);
        return ret;
    }

    // rp is the OpusRepacketizer opus_packet_pad_impl has on the stack in C
    internal static int opus_packet_pad(Span<byte> data, int len, int new_len, OpusRepacketizer rp)
    {
        int ret;
        ret = opus_packet_pad_impl(data, len, new_len, 1, ReadOnlySpan<opus_extension_data>.Empty, 0, rp);
        if (ret > 0)
        {
            return OPUS_OK;
        }
        else
        {
            return ret;
        }
    }

    // rp is the OpusRepacketizer C has on the stack here. The frames stay in data (C's in-place
    // unpadding): the repacketizer copies them forward, never over bytes it has yet to read.
    internal static int opus_packet_unpad(Memory<byte> data, int len, OpusRepacketizer rp)
    {
        int ret;
        int i;
        if (len < 1)
        {
            return OPUS_BAD_ARG;
        }
        opus_repacketizer_init(rp);
        ret = opus_repacketizer_cat(rp, data, len);
        if (ret < 0)
        {
            return ret;
        }
        /* Discard all padding and extensions. */
        for (i = 0; i < rp.nb_frames; i++)
        {
            rp.padding_len[i] = 0;
            rp.paddings[i] = default;
        }
        ret = opus_repacketizer_out_range_impl(rp, 0, rp.nb_frames, data.Span, len, 0, 0, ReadOnlySpan<opus_extension_data>.Empty, 0);
        System.Diagnostics.Debug.Assert(ret > 0 && ret <= len);
        return ret;
    }
}
