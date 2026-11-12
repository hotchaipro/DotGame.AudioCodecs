/* Copyright (c) 2010-2011 Xiph.Org Foundation, Skype Limited
   Written by Jean-Marc Valin and Koen Vos
   Ported to C# from libopus 1.6.1 (include/opus_defines.h, src/opus_private.h) by Hot Chai Games LLC */
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

namespace DotGame.AudioCodecs.Opus;

/// <summary>The error codes, bandwidths and modes the decoder uses.</summary>
internal static class OpusDefines
{
    internal const int OPUS_OK = 0;
    internal const int OPUS_BAD_ARG = -1;
    internal const int OPUS_BUFFER_TOO_SMALL = -2;
    internal const int OPUS_INTERNAL_ERROR = -3;
    internal const int OPUS_INVALID_PACKET = -4;
    internal const int OPUS_UNIMPLEMENTED = -5;
    internal const int OPUS_INVALID_STATE = -6;
    internal const int OPUS_ALLOC_FAIL = -7;

    internal const int OPUS_BANDWIDTH_NARROWBAND = 1101;
    internal const int OPUS_BANDWIDTH_MEDIUMBAND = 1102;
    internal const int OPUS_BANDWIDTH_WIDEBAND = 1103;
    internal const int OPUS_BANDWIDTH_SUPERWIDEBAND = 1104;
    internal const int OPUS_BANDWIDTH_FULLBAND = 1105;

    internal const int MODE_SILK_ONLY = 1000;
    internal const int MODE_HYBRID = 1001;
    internal const int MODE_CELT_ONLY = 1002;

    internal static string opus_strerror(int error) => error switch
    {
        OPUS_OK => "success",
        OPUS_BAD_ARG => "invalid argument",
        OPUS_BUFFER_TOO_SMALL => "buffer too small",
        OPUS_INTERNAL_ERROR => "internal error",
        OPUS_INVALID_PACKET => "corrupted stream",
        OPUS_UNIMPLEMENTED => "request not implemented",
        OPUS_INVALID_STATE => "invalid state",
        OPUS_ALLOC_FAIL => "memory allocation failed",
        _ => "unknown error",
    };
}
