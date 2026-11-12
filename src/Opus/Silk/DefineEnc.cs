/***********************************************************************
Copyright (c) 2006-2011, Skype Limited. All rights reserved.
Ported to C# from libopus 1.6.1 (silk/define.h) by Hot Chai Games LLC
Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions
are met:
- Redistributions of source code must retain the above copyright notice,
this list of conditions and the following disclaimer.
- Redistributions in binary form must reproduce the above copyright
notice, this list of conditions and the following disclaimer in the
documentation and/or other materials provided with the distribution.
- Neither the name of Internet Society, IETF or IETF Trust, nor the
names of specific contributors, may be used to endorse or promote
products derived from this software without specific prior written
permission.
THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE
LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
POSSIBILITY OF SUCH DAMAGE.
***********************************************************************/

using static DotGame.AudioCodecs.Opus.Silk.Define;
namespace DotGame.AudioCodecs.Opus.Silk;

/// <summary>The definitions of silk/define.h that only the encoder uses (the rest are in <see cref="Define"/>).</summary>
internal static class DefineEnc
{
    /* Max number of encoder channels (1/2) */
    internal const int ENCODER_NUM_CHANNELS = 2;

    /* Limits on bitrate */
    internal const int MIN_TARGET_RATE_BPS = 5000;
    internal const int MAX_TARGET_RATE_BPS = 80000;

    /* LBRR thresholds */
    internal const int LBRR_NB_MIN_RATE_BPS = 12000;
    internal const int LBRR_MB_MIN_RATE_BPS = 14000;
    internal const int LBRR_WB_MIN_RATE_BPS = 16000;

    /* DTX settings */
    internal const int NB_SPEECH_FRAMES_BEFORE_DTX = 10;      /* eq 200 ms */
    internal const int MAX_CONSECUTIVE_DTX = 20;              /* eq 400 ms */
    internal const float DTX_ACTIVITY_THRESHOLD = 0.1f;

    /* VAD decision */
    internal const int VAD_NO_DECISION = -1;
    internal const int VAD_NO_ACTIVITY = 0;
    internal const int VAD_ACTIVITY = 1;

    /* Settings for stereo processing */
    internal const double STEREO_RATIO_SMOOTH_COEF = 0.01;    /* smoothing coef for signal norms and stereo width */

    /* Milliseconds of lookahead for pitch analysis */
    internal const int LA_PITCH_MS = 2;
    internal const int LA_PITCH_MAX = LA_PITCH_MS * MAX_FS_KHZ;

    /* Order of LPC used in find pitch */
    internal const int MAX_FIND_PITCH_LPC_ORDER = 16;

    /* Length of LPC window used in find pitch */
    internal const int FIND_PITCH_LPC_WIN_MS = 20 + (LA_PITCH_MS << 1);
    internal const int FIND_PITCH_LPC_WIN_MS_2_SF = 10 + (LA_PITCH_MS << 1);
    internal const int FIND_PITCH_LPC_WIN_MAX = FIND_PITCH_LPC_WIN_MS * MAX_FS_KHZ;

    /* Milliseconds of lookahead for noise shape analysis */
    internal const int LA_SHAPE_MS = 5;
    internal const int LA_SHAPE_MAX = LA_SHAPE_MS * MAX_FS_KHZ;

    /* Maximum length of LPC window used in noise shape analysis */
    internal const int SHAPE_LPC_WIN_MAX = 15 * MAX_FS_KHZ;

    internal const float MAX_PREDICTION_POWER_GAIN_AFTER_RESET = 1e2f;

    /* Flag to use harmonic noise shaping */
    internal const int USE_HARM_SHAPING = 1;

    /* Max LPC order of noise shaping filters */
    internal const int MAX_SHAPE_LPC_ORDER = 24;

    internal const int HARM_SHAPE_FIR_TAPS = 3;

    /* Maximum number of delayed decision states */
    internal const int MAX_DEL_DEC_STATES = 4;

    internal const int LTP_BUF_LENGTH = 512;
    internal const int LTP_MASK = LTP_BUF_LENGTH - 1;
    internal const int DECISION_DELAY = 40;

    internal const int MAX_MATRIX_SIZE = MAX_LPC_ORDER; /* Max of LPC Order and LTP order */

    internal const int NSQ_LPC_BUF_LENGTH = MAX_LPC_ORDER;

    /***************************/
    /* Voice activity detector */
    /***************************/
    internal const int VAD_N_BANDS = 4;

    internal const int VAD_INTERNAL_SUBFRAMES_LOG2 = 2;
    internal const int VAD_INTERNAL_SUBFRAMES = 1 << VAD_INTERNAL_SUBFRAMES_LOG2;

    internal const int VAD_NOISE_LEVEL_SMOOTH_COEF_Q16 = 1024;    /* Must be <  4096 */
    internal const int VAD_NOISE_LEVELS_BIAS = 50;

    /* Sigmoid settings */
    internal const int VAD_NEGATIVE_OFFSET_Q5 = 128;     /* sigmoid is 0 at -128 */
    internal const int VAD_SNR_FACTOR_Q16 = 45000;

    /* smoothing for SNR measurement */
    internal const int VAD_SNR_SMOOTH_COEF_Q18 = 4096;

    /******************/
    /* NLSF quantizer */
    /******************/
    internal const int NLSF_W_Q = 2;
    internal const int NLSF_VQ_MAX_VECTORS = 32;
    internal const int NLSF_QUANT_MAX_AMPLITUDE_EXT = 10;
    internal const int NLSF_QUANT_DEL_DEC_STATES_LOG2 = 2;
    internal const int NLSF_QUANT_DEL_DEC_STATES = 1 << NLSF_QUANT_DEL_DEC_STATES_LOG2;

    /* Transition filtering for mode switching */
    internal const int TRANSITION_TIME_MS = 5120;    /* 5120 = 64 * FRAME_LENGTH_MS * ( TRANSITION_INT_NUM - 1 ) = 64*(20*4)*/
    internal const int TRANSITION_NB = 3;            /* Hardcoded in tables */
    internal const int TRANSITION_NA = 2;            /* Hardcoded in tables */
    internal const int TRANSITION_INT_NUM = 5;       /* Hardcoded in tables */
    internal const int TRANSITION_FRAMES = TRANSITION_TIME_MS / MAX_FRAME_LENGTH_MS;
    internal const int TRANSITION_INT_STEPS = TRANSITION_FRAMES / (TRANSITION_INT_NUM - 1);
}
