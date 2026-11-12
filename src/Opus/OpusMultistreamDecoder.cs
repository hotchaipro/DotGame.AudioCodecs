/* Copyright (c) 2011 Xiph.Org Foundation
   Written by Jean-Marc Valin
   Ported to C# from libopus 1.6.1 (src/opus_multistream_decoder.c, src/opus_multistream.c) by Hot Chai Games LLC */
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
using System.IO;
using static DotGame.AudioCodecs.Opus.OpusDefines;

namespace DotGame.AudioCodecs.Opus;

/// <summary>
/// The multistream decoder (OpusMSDecoder): <c>nb_streams</c> Opus streams in one packet, the first
/// <c>nb_coupled_streams</c> of them stereo, each mapped to output channels by <c>mapping</c> (255 is a
/// silent channel). Output is opus_res, interleaved, as opus_multistream_decode24 gives it.
/// </summary>
internal sealed class OpusMultistreamDecoder
{
    private readonly int nb_channels;
    private readonly int nb_streams;
    private readonly int nb_coupled_streams;
    private readonly byte[] mapping;
    private readonly OpusDecoderState[] decoders;
    private readonly int Fs;
    private readonly int[] buf;

    /// <summary>opus_multistream_decoder_create.</summary>
    /// <exception cref="ArgumentException">The layout isn't valid (C's OPUS_BAD_ARG).</exception>
    internal OpusMultistreamDecoder(int Fs, int channels, int streams, int coupled_streams, ReadOnlySpan<byte> mapping)
    {
        if ((channels > 255) || (channels < 1) || (coupled_streams > streams)
            || (streams < 1) || (coupled_streams < 0) || (streams > 255 - coupled_streams) || (mapping.Length < channels))
        {
            throw new ArgumentException("The Opus multistream layout isn't valid.");
        }
        this.nb_channels = channels;
        this.nb_streams = streams;
        this.nb_coupled_streams = coupled_streams;
        this.mapping = mapping[..channels].ToArray();
        if (!this.validate_layout())
        {
            throw new ArgumentException("The Opus channel mapping names a stream that isn't there.");
        }
        this.Fs = Fs;
        this.decoders = new OpusDecoderState[streams];
        for (int i = 0; i < streams; i++)
        {
            this.decoders[i] = new OpusDecoderState(Fs, i < coupled_streams ? 2 : 1);
        }
        this.buf = new int[2 * (Fs / 25 * 3)];
    }

    /// <summary>OPUS_GET_FINAL_RANGE: the streams' final ranges, XORed.</summary>
    internal uint GetFinalRange()
    {
        uint value = 0;
        foreach (OpusDecoderState decoder in this.decoders)
        {
            value ^= decoder.GetFinalRange();
        }
        return value;
    }

    /// <summary>OPUS_RESET_STATE, on every stream.</summary>
    internal void ResetState()
    {
        foreach (OpusDecoderState decoder in this.decoders)
        {
            decoder.ResetState();
        }
    }

    /// <summary>
    /// opus_multistream_decode24: decodes a packet (empty to conceal a lost one) to opus_res,
    /// interleaved, and returns the frames decoded.
    /// </summary>
    /// <exception cref="InvalidDataException">The packet is damaged.</exception>
    internal int Decode(ReadOnlySpan<byte> data, Span<int> pcm, int frame_size, bool decode_fec = false)
    {
        int ret = this.opus_multistream_decode_native(data, data.Length, pcm, frame_size, decode_fec ? 1 : 0);
        if (ret < 0)
        {
            throw new InvalidDataException("The Opus packet can't be decoded: " + opus_strerror(ret) + ".");
        }
        return ret;
    }

    private bool validate_layout()
    {
        int i, max_channel;

        max_channel = this.nb_streams + this.nb_coupled_streams;
        if (max_channel > 255)
        {
            return false;
        }
        for (i = 0; i < this.nb_channels; i++)
        {
            if (this.mapping[i] >= max_channel && this.mapping[i] != 255)
            {
                return false;
            }
        }
        return true;
    }

    private int get_left_channel(int stream_id, int prev)
    {
        int i;
        i = (prev < 0) ? 0 : prev + 1;
        for (; i < this.nb_channels; i++)
        {
            if (this.mapping[i] == stream_id * 2)
            {
                return i;
            }
        }
        return -1;
    }

    private int get_right_channel(int stream_id, int prev)
    {
        int i;
        i = (prev < 0) ? 0 : prev + 1;
        for (; i < this.nb_channels; i++)
        {
            if (this.mapping[i] == (stream_id * 2) + 1)
            {
                return i;
            }
        }
        return -1;
    }

    private int get_mono_channel(int stream_id, int prev)
    {
        int i;
        i = (prev < 0) ? 0 : prev + 1;
        for (; i < this.nb_channels; i++)
        {
            if (this.mapping[i] == stream_id + this.nb_coupled_streams)
            {
                return i;
            }
        }
        return -1;
    }

    private static int opus_multistream_packet_validate(ReadOnlySpan<byte> data, int len, int nb_streams, int Fs)
    {
        int s;
        int count;
        Span<short> size = stackalloc short[48];
        int samples = 0;
        int packet_offset;

        for (s = 0; s < nb_streams; s++)
        {
            int tmp_samples;
            if (len <= 0)
            {
                return OPUS_INVALID_PACKET;
            }
            count = OpusDecoderState.opus_packet_parse_impl(data, len, s != nb_streams - 1 ? 1 : 0, out _, size, out _, out packet_offset, out _, out _);
            if (count < 0)
            {
                return count;
            }
            tmp_samples = OpusDecoderState.opus_packet_get_nb_samples(data, packet_offset, Fs);
            if (s != 0 && samples != tmp_samples)
            {
                return OPUS_INVALID_PACKET;
            }
            samples = tmp_samples;
            data = data[packet_offset..];
            len -= packet_offset;
        }
        return samples;
    }

    // copy_channel_out for opus_res: one source channel (stride apart) into an output channel, or
    // silence for a null source
    private static void copy_channel_out(Span<int> dst, int dst_stride, int dst_channel, ReadOnlySpan<int> src, int src_stride, int frame_size)
    {
        int i;
        if (!src.IsEmpty)
        {
            for (i = 0; i < frame_size; i++)
            {
                dst[(i * dst_stride) + dst_channel] = src[i * src_stride];
            }
        }
        else
        {
            for (i = 0; i < frame_size; i++)
            {
                dst[(i * dst_stride) + dst_channel] = 0;
            }
        }
    }

    private int opus_multistream_decode_native(ReadOnlySpan<byte> data, int len, Span<int> pcm, int frame_size, int decode_fec)
    {
        int s, c;
        bool do_plc = false;

        if (frame_size <= 0)
        {
            return OPUS_BAD_ARG;
        }
        /* Limit frame_size to avoid excessive stack allocations. */
        frame_size = Math.Min(frame_size, this.Fs / 25 * 3);
        Span<int> buf = this.buf.AsSpan(0, 2 * frame_size);

        if (len == 0)
        {
            do_plc = true;
        }
        if (len < 0)
        {
            return OPUS_BAD_ARG;
        }
        if (!do_plc && len < (2 * this.nb_streams) - 1)
        {
            return OPUS_INVALID_PACKET;
        }
        if (!do_plc)
        {
            int ret = opus_multistream_packet_validate(data, len, this.nb_streams, this.Fs);
            if (ret < 0)
            {
                return ret;
            }
            else if (ret > frame_size)
            {
                return OPUS_BUFFER_TOO_SMALL;
            }
        }
        for (s = 0; s < this.nb_streams; s++)
        {
            OpusDecoderState dec;
            int packet_offset;
            int ret;

            dec = this.decoders[s];

            if (!do_plc && len <= 0)
            {
                return OPUS_INTERNAL_ERROR;
            }
            ret = dec.opus_decode_native(data, len, buf, frame_size, decode_fec, s != this.nb_streams - 1 ? 1 : 0, out packet_offset);
            if (!do_plc)
            {
                data = data[packet_offset..];
                len -= packet_offset;
            }
            if (ret <= 0)
            {
                return ret;
            }
            frame_size = ret;
            if (s < this.nb_coupled_streams)
            {
                int chan, prev;
                prev = -1;
                /* Copy "left" audio to the channel(s) where it belongs */
                while ((chan = this.get_left_channel(s, prev)) != -1)
                {
                    copy_channel_out(pcm, this.nb_channels, chan, buf, 2, frame_size);
                    prev = chan;
                }
                prev = -1;
                /* Copy "right" audio to the channel(s) where it belongs */
                while ((chan = this.get_right_channel(s, prev)) != -1)
                {
                    copy_channel_out(pcm, this.nb_channels, chan, buf[1..], 2, frame_size);
                    prev = chan;
                }
            }
            else
            {
                int chan, prev;
                prev = -1;
                /* Copy audio to the channel(s) where it belongs */
                while ((chan = this.get_mono_channel(s, prev)) != -1)
                {
                    copy_channel_out(pcm, this.nb_channels, chan, buf, 1, frame_size);
                    prev = chan;
                }
            }
        }
        /* Handle muted channels */
        for (c = 0; c < this.nb_channels; c++)
        {
            if (this.mapping[c] == 255)
            {
                copy_channel_out(pcm, this.nb_channels, c, default, 0, frame_size);
            }
        }
        return frame_size;
    }
}
