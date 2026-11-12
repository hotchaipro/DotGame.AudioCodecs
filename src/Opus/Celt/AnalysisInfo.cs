/* Copyright (c) 2007-2008 CSIRO
   Copyright (c) 2007-2009 Xiph.Org Foundation
   Copyright (c) 2008 Gregory Maxwell
   Written by Jean-Marc Valin and Gregory Maxwell
   Ported to C# from libopus 1.6.1 (celt/celt.h) by Hot Chai Games LLC */
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

using System.Runtime.CompilerServices;

namespace DotGame.AudioCodecs.Opus.Celt;

/// <summary>celt.h's LEAK_BANDS bytes of AnalysisInfo.leak_boost, by value as in C.</summary>
[InlineArray(AnalysisInfo.LEAK_BANDS)]
internal struct LeakBoost
{
    private byte _element0;
}

/// <summary>celt.h's AnalysisInfo: the tonality analysis's result for a frame, copied by value as in C.</summary>
internal struct AnalysisInfo
{
    internal const int LEAK_BANDS = 19;

    internal int valid;
    internal float tonality;
    internal float tonality_slope;
    internal float noisiness;
    internal float activity;
    internal float music_prob;
    internal float music_prob_min;
    internal float music_prob_max;
    internal int bandwidth;
    internal float activity_probability;
    internal float max_pitch_ratio;
    /* Store as Q6 char to save space. */
    internal LeakBoost leak_boost;
}

/// <summary>celt.h's SILKInfo: what SILK tells CELT of a hybrid frame.</summary>
internal struct SILKInfo
{
    internal int signalType;
    internal int offset;
}
