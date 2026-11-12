/***********************************************************************
Copyright (c) 2006-2011, Skype Limited. All rights reserved.
Ported to C# from libopus 1.6.1 (silk/tables_other.c) by Hot Chai Games LLC
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

/// <summary>The tables of silk/tables_other.c that the decoder uses.</summary>
internal static class TablesOther
{
    internal static readonly short[] silk_stereo_pred_quant_Q13 =
    [
        -13732, -10050, -8266, -7526, -6500, -5000, -2950, -820,
        820, 2950, 5000, 6500, 7526, 8266, 10050, 13732,
    ];

    internal static readonly byte[] silk_stereo_pred_joint_iCDF =
    [
        249, 247, 246, 245, 244,
        234, 210, 202, 201, 200,
        197, 174, 82, 59, 56,
        55, 54, 46, 22, 12,
        11, 10, 9, 7, 0,
    ];

    internal static readonly byte[] silk_stereo_only_code_mid_iCDF =
    [
        64, 0,
    ];

    private static readonly byte[] silk_LBRR_flags_2_iCDF =
    [
        203, 150, 0,
    ];

    private static readonly byte[] silk_LBRR_flags_3_iCDF =
    [
        215, 195, 166, 125, 110, 82, 0,
    ];

    internal static readonly byte[] silk_lsb_iCDF =
    [
        120, 0,
    ];

    internal static readonly byte[] silk_LTPscale_iCDF =
    [
        128, 64, 0,
    ];

    internal static readonly byte[] silk_type_offset_VAD_iCDF =
    [
        232, 158, 10, 0,
    ];

    internal static readonly byte[] silk_type_offset_no_VAD_iCDF =
    [
        230, 0,
    ];

    internal static readonly byte[] silk_NLSF_interpolation_factor_iCDF =
    [
        243, 221, 192, 181, 0,
    ];

    /* C: opus_int16 silk_Quantization_Offsets_Q10[ 2 ][ 2 ], flattened row by row */
    internal static readonly short[] silk_Quantization_Offsets_Q10 =
    [
        100, 240, 32, 100,
    ];

    internal static readonly short[] silk_LTPScales_table_Q14 =
    [
        15565, 12288, 8192,
    ];

    internal static readonly byte[] silk_uniform3_iCDF =
    [
        171, 85, 0,
    ];

    internal static readonly byte[] silk_uniform4_iCDF =
    [
        192, 128, 64, 0,
    ];

    internal static readonly byte[] silk_uniform5_iCDF =
    [
        205, 154, 102, 51, 0,
    ];

    internal static readonly byte[] silk_uniform6_iCDF =
    [
        213, 171, 128, 85, 43, 0,
    ];

    internal static readonly byte[] silk_uniform8_iCDF =
    [
        224, 192, 160, 128, 96, 64, 32, 0,
    ];

    internal static readonly byte[] silk_NLSF_EXT_iCDF =
    [
        100, 40, 16, 7, 3, 1, 0,
    ];

    internal static readonly byte[][] silk_LBRR_flags_iCDF_ptr =
    [
        silk_LBRR_flags_2_iCDF,
        silk_LBRR_flags_3_iCDF,
    ];
}
