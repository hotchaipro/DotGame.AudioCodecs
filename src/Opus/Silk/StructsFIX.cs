/***********************************************************************
Copyright (c) 2006-2011, Skype Limited. All rights reserved.
Ported to C# from libopus 1.6.1 (silk/fixed/structs_FIX.h) by Hot Chai Games LLC
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

using static DotGame.AudioCodecs.Opus.Silk.DefineEnc;
using static DotGame.AudioCodecs.Opus.Silk.Define;

using System;

namespace DotGame.AudioCodecs.Opus.Silk;

/********************************/
/* Noise shaping analysis state */
/********************************/
internal sealed class silk_shape_state_FIX
{
    internal sbyte LastGainIndex;
    internal int HarmBoost_smth_Q16;
    internal int HarmShapeGain_smth_Q16;
    internal int Tilt_smth_Q16;

    internal void Clear()
    {
        this.LastGainIndex = 0;
        this.HarmBoost_smth_Q16 = 0;
        this.HarmShapeGain_smth_Q16 = 0;
        this.Tilt_smth_Q16 = 0;
    }
}

/************************/
/* Encoder control FIX  */
/************************/
internal sealed class silk_encoder_control_FIX
{
    /* Prediction and coding parameters */
    internal readonly int[] Gains_Q16 = new int[MAX_NB_SUBFR];
    /* C: opus_int16 PredCoef_Q12[ 2 ][ MAX_LPC_ORDER ], flattened */
    internal readonly short[] PredCoef_Q12 = new short[2 * MAX_LPC_ORDER];
    internal readonly short[] LTPCoef_Q14 = new short[LTP_ORDER * MAX_NB_SUBFR];
    internal int LTP_scale_Q14;
    internal readonly int[] pitchL = new int[MAX_NB_SUBFR];

    /* Noise shaping parameters */
    /* Testing */
    internal readonly short[] AR_Q13 = new short[MAX_NB_SUBFR * MAX_SHAPE_LPC_ORDER];
    internal readonly int[] LF_shp_Q14 = new int[MAX_NB_SUBFR];         /* Packs two int16 coefficients per int32 value         */
    internal readonly int[] Tilt_Q14 = new int[MAX_NB_SUBFR];
    internal readonly int[] HarmShapeGain_Q14 = new int[MAX_NB_SUBFR];
    internal int Lambda_Q10;
    internal int input_quality_Q14;
    internal int coding_quality_Q14;

    /* measures */
    internal int predGain_Q16;
    internal int LTPredCodGain_Q7;
    internal readonly int[] ResNrg = new int[MAX_NB_SUBFR];             /* Residual energy per subframe                         */
    internal readonly int[] ResNrgQ = new int[MAX_NB_SUBFR];            /* Q domain for the residual energy > 0                 */

    /* Parameters for CBR mode */
    internal readonly int[] GainsUnq_Q16 = new int[MAX_NB_SUBFR];
    internal sbyte lastGainIndexPrev;

    /// <summary>C's psEncCtrl->PredCoef_Q12[ k ].</summary>
    internal Span<short> PredCoef_Q12_row(int k) => this.PredCoef_Q12.AsSpan(k * MAX_LPC_ORDER, MAX_LPC_ORDER);
}

/********************************/
/* Encoder state FIX            */
/********************************/
internal sealed class silk_encoder_state_FIX
{
    internal readonly silk_encoder_state sCmn = new();                  /* Common struct, shared with floating-point code       */
    internal readonly silk_shape_state_FIX sShape = new();              /* Shape state                                          */

    /* Buffer for find pitch and noise shape analysis */
    internal readonly short[] x_buf = new short[(2 * MAX_FRAME_LENGTH) + LA_SHAPE_MAX]; /* Buffer for find pitch and noise shape analysis  */
    internal int LTPCorr_Q15;                                           /* Normalized correlation from pitch lag estimator      */
    internal int resNrgSmth;

    /* Not in C: what silk_encode_frame_FIX and silk_LBRR_encode_FIX allocate on their stacks for each
       frame (sEncCtrl, sNSQ_copy[ 2 ], sNSQ_LBRR[ 1 ]), and silk_setup_resamplers's
       temp_resampler_state, made once. */
    internal readonly silk_encoder_control_FIX sEncCtrl = new();
    internal readonly silk_nsq_state[] sNSQ_copy = [new(), new()];
    internal readonly silk_nsq_state sNSQ_LBRR = new();
    internal readonly silk_resampler_state_struct temp_resampler_state = new();

    /// <summary>silk_memset( psEnc, 0, sizeof( silk_encoder_state_FIX ) ).</summary>
    internal void Clear()
    {
        this.sCmn.Clear();
        this.sShape.Clear();
        Array.Clear(this.x_buf);
        this.LTPCorr_Q15 = 0;
        this.resNrgSmth = 0;
    }
}

/************************/
/* Encoder Super Struct */
/************************/
internal sealed class silk_encoder
{
    internal readonly stereo_enc_state sStereo = new();
    internal int nBitsUsedLBRR;
    internal int nBitsExceeded;
    internal int nChannelsAPI;
    internal int nChannelsInternal;
    internal int nPrevChannelsInternal;
    internal int timeSinceSwitchAllowed_ms;
    internal int allowBandwidthSwitch;
    internal int prev_decode_only_middle;

    /* This needs to be last so we can skip the second state for mono. */
    internal readonly silk_encoder_state_FIX[] state_Fxx = [new(), new()];

    /* Not in C: silk_Encode's local silk_LP_state save_LP, made once. */
    internal readonly silk_LP_state save_LP = new();

    /// <summary>
    /// silk_memset( psEnc, 0, sizeof( silk_encoder ) - ( channels == 1 ) * sizeof( silk_encoder_state_Fxx ) ):
    /// the second channel's state is left alone for mono.
    /// </summary>
    internal void Clear(int channels)
    {
        this.sStereo.Clear();
        this.nBitsUsedLBRR = 0;
        this.nBitsExceeded = 0;
        this.nChannelsAPI = 0;
        this.nChannelsInternal = 0;
        this.nPrevChannelsInternal = 0;
        this.timeSinceSwitchAllowed_ms = 0;
        this.allowBandwidthSwitch = 0;
        this.prev_decode_only_middle = 0;
        this.state_Fxx[0].Clear();
        if (channels != 1)
        {
            this.state_Fxx[1].Clear();
        }
    }
}
