/* Copyright (c) 2017 Jean-Marc Valin
   Copyright (c) 1994-2013 Xiph.Org Foundation and contributors
   Ported to C# from libopusenc (src/opusenc.c: extend_signal, vorbis_lpc_from_data) by Hot Chai Games LLC */
/*
   Redistribution and use in source and binary forms, with or without
   modification, are permitted provided that the following conditions
   are met:

   - Redistributions of source code must retain the above copyright
   notice, this list of conditions and the following disclaimer.

   - Redistributions in binary form must reproduce the above copyright
   notice, this list of conditions and the following disclaimer in the
   documentation and/or other materials provided with the distribution.

   - Neither the name of the Xiph.Org Foundation nor the names of its
   contributors may be used to endorse or promote products derived from
   this software without specific prior written permission.

   THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
   ``AS IS'' AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
   LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
   A PARTICULAR PURPOSE ARE DISCLAIMED.  IN NO EVENT SHALL THE FOUNDATION
   OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
   SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
   LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
   DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
   THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
   (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
   OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
*/
/* The autocorrelator and LPC coefficient estimator are derived from code written by Jutta Degener
   and Carsten Bormann:

   Copyright 1992, 1993, 1994 by Jutta Degener and Carsten Bormann, Technische Universita"t Berlin

   Any use of this software is permitted provided that this notice is not removed and that neither
   the authors nor the Technische Universita"t Berlin are deemed to have made any representations as
   to the suitability of this software for any purpose nor are held responsible for any defects of
   this software. THERE IS ABSOLUTELY NO WARRANTY FOR THIS SOFTWARE. */

using System;

namespace DotGame.AudioCodecs.Opus;

/// <summary>
/// What libopusenc pads the end of a stream with: the last samples extended by linear prediction for
/// 120 samples, faded out, so the final frame codes the signal's tail without the click a cut to
/// silence would cost. The padding is trimmed off by the last page's granule position.
/// </summary>
internal static class EndExtension
{
    /// <summary>The samples extended: LPC_PADDING.</summary>
    public const int Length = 120;

    /// <summary>The samples the prediction is fitted to: LPC_INPUT.</summary>
    public const int History = 480;

    private const int Order = 24;
    private const float GoertzelConstant = 1.99931465f;   // 2 cos(pi / Length)

    private static readonly float[] Window = MakeWindow();

    /// <summary>
    /// extend_signal: writes <see cref="Length"/> frames of <paramref name="channels"/> interleaved
    /// channels to <paramref name="output"/>, predicted from the last <paramref name="count"/> frames of
    /// <paramref name="history"/> (at most <see cref="History"/>); zeros if there are too few to fit.
    /// </summary>
    public static void Extend(ReadOnlySpan<float> history, int count, int channels, Span<float> output)
    {
        count = Math.Min(count, History);
        if (count < 4 * Order)
        {
            output[..(Length * channels)].Clear();
            return;
        }
        ReadOnlySpan<float> before = history.Slice(history.Length - (count * channels), count * channels);
        Span<float> lpc = stackalloc float[Order];
        for (int c = 0; c < channels; c++)
        {
            LpcFromData(before, c, count, channels, lpc);
            for (int i = 0; i < Length; i++)
            {
                float sum = 0;
                for (int j = 0; j < Order; j++)
                {
                    int at = i - j - 1;
                    float x = at >= 0 ? output[(at * channels) + c] : before[before.Length + (at * channels) + c];
                    sum -= x * lpc[j];
                }
                output[(i * channels) + c] = sum;
            }
            for (int i = 0; i < Length; i++)
            {
                output[(i * channels) + c] *= Window[i];
            }
        }
    }

    // A window from a resonating IIR (Goertzel's algorithm): a raised cosine from 1 down to 0
    private static float[] MakeWindow()
    {
        var window = new float[Length];
        float m0 = 1;
        float m1 = .5f * GoertzelConstant;
        float a1 = GoertzelConstant;
        window[0] = 1;
        for (int i = 1; i < Length; i++)
        {
            window[i] = (a1 * m0) - m1;
            m1 = m0;
            m0 = window[i];
        }
        for (int i = 0; i < Length; i++)
        {
            window[i] = (float)(.5 + (.5 * window[i]));
        }
        return window;
    }

    // vorbis_lpc_from_data: autocorrelation, lag windowing, Levinson-Durbin, slight damping
    private static void LpcFromData(ReadOnlySpan<float> data, int channel, int n, int stride, Span<float> lpci)
    {
        Span<double> aut = stackalloc double[Order + 1];
        Span<double> lpc = stackalloc double[Order];
        double error;
        double epsilon;
        int i;
        int j;

        // autocorrelation, p+1 lag coefficients
        j = Order + 1;
        while (j-- > 0)
        {
            double d = 0; // double needed for accumulator depth
            for (i = j; i < n; i++)
            {
                d += (double)data[(i * stride) + channel] * data[((i - j) * stride) + channel];
            }
            aut[j] = d;
        }

        // Apply lag windowing (better than bandwidth expansion)
        for (i = 1; i <= Order; i++)
        {
            aut[i] -= aut[i] * (0.008f * 0.008f) * i * i;
        }

        // set our noise floor to about -100dB
        error = aut[0] * (1.0 + 1e-7);
        epsilon = (1e-6 * aut[0]) + 1e-7;

        lpc.Clear();
        for (i = 0; i < Order; i++)
        {
            double r = -aut[i + 1];

            if (error < epsilon)
            {
                lpc[i..].Clear();
                break;
            }

            for (j = 0; j < i; j++)
            {
                r -= lpc[j] * aut[i - j];
            }
            r /= error;

            // Update LPC coefficients and total error
            lpc[i] = r;
            for (j = 0; j < i / 2; j++)
            {
                double tmp = lpc[j];

                lpc[j] += r * lpc[i - 1 - j];
                lpc[i - 1 - j] += r * tmp;
            }
            if ((i & 1) != 0)
            {
                lpc[j] += lpc[j] * r;
            }

            error *= 1.0 - (r * r);
        }

        // slightly damp the filter
        double g = .999;
        double damp = g;
        for (j = 0; j < Order; j++)
        {
            lpc[j] *= damp;
            damp *= g;
        }

        for (j = 0; j < Order; j++)
        {
            lpci[j] = (float)lpc[j];
        }
    }
}
