/* Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License. */

/* The native reference for eng/reference/Check: libopus 1.6.1, built fixed point, generic C, with no
   floating-point contraction (build-libopus.sh). libopus's ctl functions are variadic, which .NET can't
   call on arm64, so these wrap the calls the checks make. One or two channels are a plain encoder
   and decoder, as DotGame.AudioCodecs's Ogg Opus encoder uses for mapping family 0; more are the multistream
   surround encoder (mapping family 1), as opusenc creates it, and the multistream decoder. */

#include <opus.h>
#include <opus_multistream.h>

typedef struct
{
    int bitrate;            /* bits a second; -1000 (OPUS_AUTO) for libopus's own */
    int vbr;
    int vbr_constraint;
    int complexity;
    int loss;
    int fec;
    int dtx;
    int signal;             /* OPUS_AUTO, OPUS_SIGNAL_VOICE or OPUS_SIGNAL_MUSIC */
    int max_bandwidth;      /* OPUS_BANDWIDTH_*; fullband by default */
    int phase_inversion_disabled;
    int application;
} shim_settings;

typedef struct
{
    OpusEncoder *single;
    OpusMSEncoder *multi;
} shim_encoder;

#define CTL(e, request) ((e)->single ? opus_encoder_ctl((e)->single, request) : opus_multistream_encoder_ctl((e)->multi, request))

static shim_encoder encoders[64];

/* Creates an encoder; for more than two channels, writes its streams, coupled streams and mapping */
shim_encoder *shim_encoder_create(int rate, int channels, const shim_settings *s, int *streams, int *coupled, unsigned char *mapping)
{
    int error;
    shim_encoder *e = 0;
    for (int i = 0; i < 64; i++)
    {
        if (!encoders[i].single && !encoders[i].multi)
        {
            e = &encoders[i];
            break;
        }
    }
    if (!e)
    {
        return 0;
    }
    if (channels <= 2)
    {
        e->single = opus_encoder_create(rate, channels, s->application, &error);
        *streams = 1;
        *coupled = channels - 1;
        mapping[0] = 0;
        mapping[1] = 1;
    }
    else
    {
        e->multi = opus_multistream_surround_encoder_create(rate, channels, 1, streams, coupled, mapping, s->application, &error);
    }
    if (error != OPUS_OK)
    {
        e->single = 0;
        e->multi = 0;
        return 0;
    }
    CTL(e, OPUS_SET_BITRATE(s->bitrate));
    CTL(e, OPUS_SET_VBR(s->vbr));
    CTL(e, OPUS_SET_VBR_CONSTRAINT(s->vbr_constraint));
    if (s->signal != OPUS_AUTO)
    {
        CTL(e, OPUS_SET_SIGNAL(s->signal));
    }
    CTL(e, OPUS_SET_COMPLEXITY(s->complexity));
    CTL(e, OPUS_SET_PACKET_LOSS_PERC(s->loss));
    if (s->fec)
    {
        CTL(e, OPUS_SET_INBAND_FEC(1));
    }
    if (s->dtx)
    {
        CTL(e, OPUS_SET_DTX(1));
    }
    if (s->max_bandwidth != OPUS_BANDWIDTH_FULLBAND)
    {
        CTL(e, OPUS_SET_MAX_BANDWIDTH(s->max_bandwidth));
    }
    if (s->phase_inversion_disabled)
    {
        CTL(e, OPUS_SET_PHASE_INVERSION_DISABLED(1));
    }
    return e;
}

int shim_encode_float(shim_encoder *e, const float *pcm, int frame_size, unsigned char *data, int max_bytes)
{
    return e->single ? opus_encode_float(e->single, pcm, frame_size, data, max_bytes)
        : opus_multistream_encode_float(e->multi, pcm, frame_size, data, max_bytes);
}

int shim_encode_short(shim_encoder *e, const opus_int16 *pcm, int frame_size, unsigned char *data, int max_bytes)
{
    return e->single ? opus_encode(e->single, pcm, frame_size, data, max_bytes)
        : opus_multistream_encode(e->multi, pcm, frame_size, data, max_bytes);
}

unsigned int shim_encoder_final_range(shim_encoder *e)
{
    /* The multistream encoder XORs its streams' final ranges */
    opus_uint32 range;
    CTL(e, OPUS_GET_FINAL_RANGE(&range));
    return range;
}

int shim_encoder_lookahead(shim_encoder *e)
{
    opus_int32 lookahead;
    CTL(e, OPUS_GET_LOOKAHEAD(&lookahead));
    return lookahead;
}

void shim_encoder_destroy(shim_encoder *e)
{
    if (e->single)
    {
        opus_encoder_destroy(e->single);
    }
    if (e->multi)
    {
        opus_multistream_encoder_destroy(e->multi);
    }
    e->single = 0;
    e->multi = 0;
}

/* A multistream decoder; with one stream it decodes as a plain decoder does */
OpusMSDecoder *shim_decoder_create(int rate, int channels, int streams, int coupled, const unsigned char *mapping)
{
    int error;
    OpusMSDecoder *dec = opus_multistream_decoder_create(rate, channels, streams, coupled, mapping, &error);
    return error == OPUS_OK ? dec : 0;
}

int shim_decode_short(OpusMSDecoder *dec, const unsigned char *data, int length, opus_int16 *pcm, int frame_size)
{
    return opus_multistream_decode(dec, data, length, pcm, frame_size, 0);
}

int shim_decode_float(OpusMSDecoder *dec, const unsigned char *data, int length, float *pcm, int frame_size)
{
    return opus_multistream_decode_float(dec, data, length, pcm, frame_size, 0);
}

unsigned int shim_decoder_final_range(OpusMSDecoder *dec)
{
    opus_uint32 range;
    opus_multistream_decoder_ctl(dec, OPUS_GET_FINAL_RANGE(&range));
    return range;
}

void shim_decoder_destroy(OpusMSDecoder *dec)
{
    opus_multistream_decoder_destroy(dec);
}
