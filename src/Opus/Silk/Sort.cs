/***********************************************************************
Copyright (c) 2006-2011, Skype Limited. All rights reserved.
Ported to C# from libopus 1.6.1 (silk/sort.c) by Hot Chai Games LLC
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

using System;
using System.Diagnostics;

namespace DotGame.AudioCodecs.Opus.Silk;

/* Insertion sort (fast for already almost sorted arrays): the decoder only uses
   silk_insertion_sort_increasing_all_values_int16 (from silk_NLSF_stabilize). */
internal static class Sort
{
    internal static void silk_insertion_sort_increasing_all_values_int16(
        Span<short> a,                  /* I/O   Unsorted / Sorted vector                                   */
        int L)                          /* I     Vector length                                              */
    {
        int value;
        int i, j;

        /* Safety checks */
        Debug.Assert(L > 0);

        /* Sort vector elements by value, increasing order */
        for (i = 1; i < L; i++)
        {
            value = a[i];
            for (j = i - 1; (j >= 0) && (value < a[j]); j--)
            {
                a[j + 1] = a[j]; /* Shift value */
            }
            a[j + 1] = (short)value; /* Write value */
        }
    }
}
