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

namespace DotGame.AudioCodecs.Opus.Silk;

/// <summary>The defines of silk/define.h that the decoder uses.</summary>
internal static class Define
{
    /* Number of decoder channels (1/2) */
    internal const int DECODER_NUM_CHANNELS = 2;

    internal const int MAX_FRAMES_PER_PACKET = 3;

    /* Maximum sampling frequency */
    internal const int MAX_FS_KHZ = 16;
    internal const int MAX_API_FS_KHZ = 48;

    /* Signal types */
    internal const int TYPE_NO_VOICE_ACTIVITY = 0;
    internal const int TYPE_UNVOICED = 1;
    internal const int TYPE_VOICED = 2;

    /* Conditional coding types */
    internal const int CODE_INDEPENDENTLY = 0;
    internal const int CODE_INDEPENDENTLY_NO_LTP_SCALING = 1;
    internal const int CODE_CONDITIONALLY = 2;

    /* Settings for stereo processing */
    internal const int STEREO_QUANT_TAB_SIZE = 16;
    internal const int STEREO_QUANT_SUB_STEPS = 5;
    internal const int STEREO_INTERP_LEN_MS = 8;       /* must be even */

    /* Range of pitch lag estimates */
    internal const int PITCH_EST_MIN_LAG_MS = 2;       /* 2 ms -> 500 Hz */
    internal const int PITCH_EST_MAX_LAG_MS = 18;      /* 18 ms -> 56 Hz */

    /* Maximum number of subframes */
    internal const int MAX_NB_SUBFR = 4;

    /* Number of samples per frame */
    internal const int LTP_MEM_LENGTH_MS = 20;
    internal const int SUB_FRAME_LENGTH_MS = 5;
    internal const int MAX_SUB_FRAME_LENGTH = SUB_FRAME_LENGTH_MS * MAX_FS_KHZ;
    internal const int MAX_FRAME_LENGTH_MS = SUB_FRAME_LENGTH_MS * MAX_NB_SUBFR;
    internal const int MAX_FRAME_LENGTH = MAX_FRAME_LENGTH_MS * MAX_FS_KHZ;

    /* dB level of lowest gain quantization level */
    internal const int MIN_QGAIN_DB = 2;
    /* dB level of highest gain quantization level */
    internal const int MAX_QGAIN_DB = 88;
    /* Number of gain quantization levels */
    internal const int N_LEVELS_QGAIN = 64;
    /* Max increase in gain quantization index */
    internal const int MAX_DELTA_GAIN_QUANT = 36;
    /* Max decrease in gain quantization index */
    internal const int MIN_DELTA_GAIN_QUANT = -4;

    /* Quantization offsets (multiples of 4) */
    internal const int OFFSET_VL_Q10 = 32;
    internal const int OFFSET_VH_Q10 = 100;
    internal const int OFFSET_UVL_Q10 = 100;
    internal const int OFFSET_UVH_Q10 = 240;

    internal const int QUANT_LEVEL_ADJUST_Q10 = 80;

    /* Maximum numbers of iterations used to stabilize an LPC vector */
    internal const int MAX_LPC_STABILIZE_ITERATIONS = 16;
    internal const float MAX_PREDICTION_POWER_GAIN = 1e4f;

    internal const int MAX_LPC_ORDER = 16;
    internal const int MIN_LPC_ORDER = 10;

    /* Find Pred Coef defines */
    internal const int LTP_ORDER = 5;

    /* LTP quantization settings */
    internal const int NB_LTP_CBKS = 3;

    /* Number of subframes for excitation entropy coding */
    internal const int SHELL_CODEC_FRAME_LENGTH = 16;
    internal const int LOG2_SHELL_CODEC_FRAME_LENGTH = 4;
    internal const int MAX_NB_SHELL_BLOCKS = MAX_FRAME_LENGTH / SHELL_CODEC_FRAME_LENGTH;

    /* Number of rate levels, for entropy coding of excitation */
    internal const int N_RATE_LEVELS = 10;

    /* Maximum sum of pulses per shell coding frame */
    internal const int SILK_MAX_PULSES = 16;

    /* Size of the piecewise linear cosine approximation table for the LSFs */
    internal const int LSF_COS_TAB_SZ_FIX = 128;

    /******************/
    /* NLSF quantizer */
    /******************/
    internal const int NLSF_QUANT_MAX_AMPLITUDE = 4;
    internal const double NLSF_QUANT_LEVEL_ADJ = 0.1;

    /* BWE factors to apply after packet loss */
    internal const int BWE_AFTER_LOSS_Q16 = 63570;

    /* Defines for CN generation */
    internal const int CNG_BUF_MASK_MAX = 255;             /* 2^floor(log2(MAX_FRAME_LENGTH))-1    */
    internal const int CNG_GAIN_SMTH_Q16 = 4634;           /* 0.25^(1/4)                           */
    internal const int CNG_GAIN_SMTH_THRESHOLD_Q16 = 46396; /* -3 dB                               */
    internal const int CNG_NLSF_SMTH_Q16 = 16348;          /* 0.25                                 */
}
