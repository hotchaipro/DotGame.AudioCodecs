/* Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

   The C libraries' side of the benchmarks (bench/README.md): times one operation on a file in memory
   and prints "<median ms> <output bytes>".

     audiobench opus-decode file.opus     Ogg Opus to 16-bit interleaved samples (libogg, libopus)
     audiobench opus-encode file.wav      16-bit WAV to Ogg Opus as opusenc's defaults: 20 ms,
                                          unconstrained VBR, complexity 10, 64 kbit/s a stream and 32 more a coupled one
     audiobench flac-decode file.flac     FLAC to 16-bit interleaved samples (libFLAC, MD5 not checked)
     audiobench flac-encode file.wav      16-bit WAV to FLAC, level 5 (flac's default)
     audiobench drflac-decode file.flac   FLAC to 16-bit interleaved samples with dr_flac (built with
                                          -DWITH_DR_FLAC and dr_flac.h on the include path)

   An encode writes its file to a third argument, if there is one. The median of at least 15 runs and 2 seconds, after two to warm up. */
#include <FLAC/stream_decoder.h>
#include <FLAC/stream_encoder.h>
#include <ogg/ogg.h>
#include <opus_multistream.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#ifdef WITH_DR_FLAC
#define DR_FLAC_IMPLEMENTATION
#include "dr_flac.h"
#endif

static unsigned char *data;
static size_t size;
static size_t output;      /* bytes of the last run's output */

static double now(void) { struct timespec t; clock_gettime(CLOCK_MONOTONIC, &t); return t.tv_sec + t.tv_nsec / 1e9; }

static void fail(const char *what) { fprintf(stderr, "audiobench: %s\n", what); exit(1); }

/* ---- Ogg Opus decode ---- */

static short *decoded;
static size_t decodedCapacity;

static void opus_decode_file(void)
{
    ogg_sync_state sync;
    ogg_stream_state stream;
    ogg_page page;
    ogg_packet packet;
    OpusMSDecoder *decoder = NULL;
    int channels = 0, packets = 0, started = 0;
    size_t frames = 0;
    ogg_sync_init(&sync);
    char *buffer = ogg_sync_buffer(&sync, (long)size);
    memcpy(buffer, data, size);
    ogg_sync_wrote(&sync, (long)size);
    while (ogg_sync_pageout(&sync, &page) == 1)
    {
        if (!started)
        {
            ogg_stream_init(&stream, ogg_page_serialno(&page));
            started = 1;
        }
        if (ogg_page_serialno(&page) != stream.serialno || ogg_stream_pagein(&stream, &page) != 0)
            continue;
        while (ogg_stream_packetout(&stream, &packet) == 1)
        {
            if (packets++ == 0)
            {
                /* OpusHead: channels, then the mapping */
                const unsigned char *h = packet.packet;
                channels = h[9];
                int family = h[18], streams = 1, coupled = channels - 1, error;
                unsigned char mapping[255] = { 0, 1 };
                if (family != 0)
                {
                    streams = h[19];
                    coupled = h[20];
                    memcpy(mapping, h + 21, channels);
                }
                decoder = opus_multistream_decoder_create(48000, channels, streams, coupled, mapping, &error);
                if (error != OPUS_OK) fail("decoder");
                continue;
            }
            if (packets == 2)
                continue;   /* OpusTags */
            if ((frames + 5760) * channels > decodedCapacity)
            {
                decodedCapacity = (frames + 5760) * channels * 2;
                decoded = realloc(decoded, decodedCapacity * sizeof(short));
            }
            int n = opus_multistream_decode(decoder, packet.packet, (opus_int32)packet.bytes, decoded + frames * channels, 5760, 0);
            if (n < 0) fail("decode");
            frames += n;
        }
    }
    opus_multistream_decoder_destroy(decoder);
    ogg_stream_clear(&stream);
    ogg_sync_clear(&sync);
    output = frames * channels * sizeof(short);
}

/* ---- WAV input ---- */

static const short *pcm;
static int pcmChannels, pcmRate;
static size_t pcmFrames;

static void read_wav(void)
{
    size_t at = 12;
    while (at + 8 <= size)
    {
        unsigned length = data[at + 4] | (data[at + 5] << 8) | (data[at + 6] << 16) | ((unsigned)data[at + 7] << 24);
        if (!memcmp(data + at, "fmt ", 4))
        {
            pcmChannels = data[at + 10] | (data[at + 11] << 8);
            pcmRate = data[at + 12] | (data[at + 13] << 8) | (data[at + 14] << 16) | (data[at + 15] << 24);
            if ((data[at + 22] | (data[at + 23] << 8)) != 16) fail("16-bit WAV only");
        }
        else if (!memcmp(data + at, "data", 4))
        {
            pcm = (const short *)(data + at + 8);
            pcmFrames = length / 2 / pcmChannels;
            return;
        }
        at += 8 + length + (length & 1);
    }
    fail("no data chunk");
}

/* ---- Ogg Opus encode ---- */

static unsigned char *encoded;
static size_t encodedLength, encodedCapacity;

static void append(const unsigned char *bytes, long n)
{
    if (encodedLength + n > encodedCapacity)
    {
        encodedCapacity = (encodedLength + n) * 2;
        encoded = realloc(encoded, encodedCapacity);
    }
    memcpy(encoded + encodedLength, bytes, n);
    encodedLength += n;
}

static void write_pages(ogg_stream_state *stream, int flush)
{
    ogg_page page;
    while (flush ? ogg_stream_flush(stream, &page) : ogg_stream_pageout(stream, &page))
    {
        append(page.header, page.header_len);
        append(page.body, page.body_len);
    }
}

static void opus_encode_file(void)
{
    int streams, coupled, error;
    unsigned char mapping[255];
    OpusMSEncoder *encoder = opus_multistream_surround_encoder_create(48000, pcmChannels, pcmChannels > 2 ? 1 : 0,
        &streams, &coupled, mapping, OPUS_APPLICATION_AUDIO, &error);
    if (error != OPUS_OK) fail("encoder");
    opus_multistream_encoder_ctl(encoder, OPUS_SET_BITRATE(64000 * streams + 32000 * coupled));
    opus_multistream_encoder_ctl(encoder, OPUS_SET_VBR(1));
    opus_multistream_encoder_ctl(encoder, OPUS_SET_VBR_CONSTRAINT(0));   /* opusenc's unconstrained VBR */
    opus_multistream_encoder_ctl(encoder, OPUS_SET_COMPLEXITY(10));
    opus_multistream_encoder_ctl(encoder, OPUS_SET_LSB_DEPTH(16));
    opus_int32 lookahead;
    opus_multistream_encoder_ctl(encoder, OPUS_GET_LOOKAHEAD(&lookahead));

    ogg_stream_state stream;
    ogg_stream_init(&stream, 0);
    encodedLength = 0;
    unsigned char head[19] = { 'O', 'p', 'u', 's', 'H', 'e', 'a', 'd', 1, (unsigned char)pcmChannels,
        (unsigned char)lookahead, (unsigned char)(lookahead >> 8), 0x80, 0xBB, 0, 0, 0, 0, 0 };
    ogg_packet packet = { head, sizeof head, 1, 0, 0, 0 };
    ogg_stream_packetin(&stream, &packet);
    write_pages(&stream, 1);
    unsigned char tags[] = { 'O', 'p', 'u', 's', 'T', 'a', 'g', 's', 0, 0, 0, 0, 0, 0, 0, 0 };
    packet = (ogg_packet){ tags, sizeof tags, 0, 0, 0, 1 };
    ogg_stream_packetin(&stream, &packet);
    write_pages(&stream, 1);

    static short frame[960 * 8];
    static unsigned char bytes[(1277 * 6 + 2) * 8];
    size_t total = pcmFrames + lookahead;
    ogg_int64_t sequence = 2;
    for (size_t at = 0; at < total; at += 960)
    {
        for (int i = 0; i < 960 * pcmChannels; i++)
        {
            size_t sample = at * pcmChannels + i;
            frame[i] = sample < pcmFrames * pcmChannels ? pcm[sample] : 0;
        }
        int n = opus_multistream_encode(encoder, frame, 960, bytes, sizeof bytes);
        if (n < 0) fail("encode");
        int last = at + 960 >= total;
        packet = (ogg_packet){ bytes, n, 0, last, (ogg_int64_t)(at + 960 < total ? at + 960 : total), sequence++ };
        ogg_stream_packetin(&stream, &packet);
        write_pages(&stream, last);
    }
    ogg_stream_clear(&stream);
    opus_multistream_encoder_destroy(encoder);
    output = encodedLength;
}

/* ---- FLAC decode ---- */

typedef struct { size_t pos, frames; unsigned channels; } FlacState;

static FLAC__StreamDecoderReadStatus flac_read(const FLAC__StreamDecoder *d, FLAC__byte buffer[], size_t *bytes, void *c)
{
    FlacState *s = c;
    size_t n = size - s->pos < *bytes ? size - s->pos : *bytes;
    if (n == 0) { *bytes = 0; return FLAC__STREAM_DECODER_READ_STATUS_END_OF_STREAM; }
    memcpy(buffer, data + s->pos, n);
    s->pos += n;
    *bytes = n;
    return FLAC__STREAM_DECODER_READ_STATUS_CONTINUE;
}

static FLAC__StreamDecoderWriteStatus flac_write(const FLAC__StreamDecoder *d, const FLAC__Frame *f, const FLAC__int32 *const b[], void *c)
{
    FlacState *s = c;
    unsigned ch = f->header.channels, n = f->header.blocksize;
    if ((s->frames + n) * ch > decodedCapacity)
    {
        decodedCapacity = (s->frames + n) * ch * 2;
        decoded = realloc(decoded, decodedCapacity * sizeof(short));
    }
    short *o = decoded + s->frames * ch;
    for (unsigned i = 0; i < n; i++)
        for (unsigned k = 0; k < ch; k++)
            *o++ = (short)b[k][i];
    s->frames += n;
    s->channels = ch;
    return FLAC__STREAM_DECODER_WRITE_STATUS_CONTINUE;
}

static void flac_error(const FLAC__StreamDecoder *d, FLAC__StreamDecoderErrorStatus e, void *c) { fail("FLAC decode"); }

static void flac_decode_file(void)
{
    FlacState s = { 0, 0, 0 };
    FLAC__StreamDecoder *d = FLAC__stream_decoder_new();
    FLAC__stream_decoder_set_md5_checking(d, false);
    FLAC__stream_decoder_init_stream(d, flac_read, NULL, NULL, NULL, NULL, flac_write, NULL, flac_error, &s);
    FLAC__stream_decoder_process_until_end_of_stream(d);
    FLAC__stream_decoder_delete(d);
    output = s.frames * s.channels * sizeof(short);
}

#ifdef WITH_DR_FLAC
/* ---- FLAC decode with dr_flac (its frame CRCs checked, as by default) ---- */

static void drflac_decode_file(void)
{
    drflac *d = drflac_open_memory(data, size, NULL);
    if (!d) fail("dr_flac open");
    size_t needed = (size_t)d->totalPCMFrameCount * d->channels;
    if (needed > decodedCapacity)
    {
        decodedCapacity = needed;
        decoded = realloc(decoded, decodedCapacity * sizeof(short));
    }
    drflac_uint64 frames = drflac_read_pcm_frames_s16(d, d->totalPCMFrameCount, decoded);
    output = (size_t)frames * d->channels * sizeof(short);
    drflac_close(d);
}
#endif

/* ---- FLAC encode ---- */

static FLAC__StreamEncoderWriteStatus flac_written(const FLAC__StreamEncoder *e, const FLAC__byte buffer[], size_t bytes, uint32_t samples, uint32_t frame, void *c)
{
    append(buffer, (long)bytes);
    return FLAC__STREAM_ENCODER_WRITE_STATUS_OK;
}

static void flac_encode_file(void)
{
    static FLAC__int32 block[4096 * 8];
    encodedLength = 0;
    FLAC__StreamEncoder *e = FLAC__stream_encoder_new();
    FLAC__stream_encoder_set_channels(e, pcmChannels);
    FLAC__stream_encoder_set_bits_per_sample(e, 16);
    FLAC__stream_encoder_set_sample_rate(e, pcmRate);
    FLAC__stream_encoder_set_compression_level(e, 5);
    FLAC__stream_encoder_set_total_samples_estimate(e, pcmFrames);
    if (FLAC__stream_encoder_init_stream(e, flac_written, NULL, NULL, NULL, NULL) != FLAC__STREAM_ENCODER_INIT_STATUS_OK) fail("FLAC encoder");
    for (size_t at = 0; at < pcmFrames; at += 4096)
    {
        size_t n = pcmFrames - at < 4096 ? pcmFrames - at : 4096;
        for (size_t i = 0; i < n * pcmChannels; i++)
            block[i] = pcm[at * pcmChannels + i];
        if (!FLAC__stream_encoder_process_interleaved(e, block, (uint32_t)n)) fail("FLAC encode");
    }
    FLAC__stream_encoder_finish(e);
    FLAC__stream_encoder_delete(e);
    output = encodedLength;
}

static int compare(const void *a, const void *b) { double x = *(const double *)a, y = *(const double *)b; return x < y ? -1 : x > y; }

int main(int argc, char **argv)
{
    if (argc < 3) fail("usage: audiobench opus-decode|opus-encode|flac-decode|flac-encode file");
    FILE *f = fopen(argv[2], "rb");
    if (!f) fail("can't open the file");
    fseek(f, 0, SEEK_END);
    size = (size_t)ftell(f);
    fseek(f, 0, SEEK_SET);
    data = malloc(size);
    if (fread(data, 1, size, f) != size) fail("can't read the file");
    fclose(f);

    void (*run)(void);
    if (!strcmp(argv[1], "opus-decode")) run = opus_decode_file;
    else if (!strcmp(argv[1], "flac-decode")) run = flac_decode_file;
#ifdef WITH_DR_FLAC
    else if (!strcmp(argv[1], "drflac-decode")) run = drflac_decode_file;
#endif
    else
    {
        read_wav();
        run = !strcmp(argv[1], "opus-encode") ? opus_encode_file : flac_encode_file;
    }

    run();
    run();
    static double times[10000];
    int count = 0;
    double start = now();
    while ((count < 15 || now() - start < 2) && count < 10000)
    {
        double t0 = now();
        run();
        times[count++] = now() - t0;
    }
    qsort(times, count, sizeof(double), compare);
    if (argc > 3 && encodedLength > 0)
    {
        FILE *out = fopen(argv[3], "wb");
        if (!out || fwrite(encoded, 1, encodedLength, out) != encodedLength) fail("can't write the output");
        fclose(out);
    }
    printf("%.3f %zu\n", times[count / 2] * 1000, output);
    return 0;
}
