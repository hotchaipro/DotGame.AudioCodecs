/* Copyright (c) 2008-2011 Octasic Inc.
                 2012-2017 Jean-Marc Valin
   Ported to C# from libopus 1.6.1 (src/mlp.c, src/mlp.h) by Hot Chai Games LLC */
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

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace DotGame.AudioCodecs.Opus;

/// <summary>mlp.h's AnalysisDenseLayer: a dense layer of the tonality analysis's network.</summary>
internal sealed class AnalysisDenseLayer
{
    internal readonly sbyte[] bias;
    internal readonly sbyte[] input_weights;
    internal readonly int nb_inputs;
    internal readonly int nb_neurons;
    internal readonly int sigmoid;

    internal AnalysisDenseLayer(sbyte[] bias, sbyte[] input_weights, int nb_inputs, int nb_neurons, int sigmoid)
    {
        this.bias = bias;
        this.input_weights = input_weights;
        this.nb_inputs = nb_inputs;
        this.nb_neurons = nb_neurons;
        this.sigmoid = sigmoid;
    }
}

/// <summary>mlp.h's AnalysisGRULayer: the tonality analysis's recurrent layer.</summary>
internal sealed class AnalysisGRULayer
{
    internal readonly sbyte[] bias;
    internal readonly sbyte[] input_weights;
    internal readonly sbyte[] recurrent_weights;
    internal readonly int nb_inputs;
    internal readonly int nb_neurons;

    internal AnalysisGRULayer(sbyte[] bias, sbyte[] input_weights, sbyte[] recurrent_weights, int nb_inputs, int nb_neurons)
    {
        this.bias = bias;
        this.input_weights = input_weights;
        this.recurrent_weights = recurrent_weights;
        this.nb_inputs = nb_inputs;
        this.nb_neurons = nb_neurons;
    }
}

/// <summary>mlp.c: the tonality analysis's network layers, in float as C computes them.</summary>
internal static class Mlp
{
    internal const float WEIGHTS_SCALE = 1.0f / 128;

    internal const int MAX_NEURONS = 32;

    // fmadd(a, b, c) is ((a)*(b)+(c)): two roundings, no fused multiply-add
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float fmadd(float a, float b, float c) => (a * b) + c;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float tansig_approx(float x)
    {
        const float N0 = 952.52801514f;
        const float N1 = 96.39235687f;
        const float N2 = 0.60863042f;
        const float D0 = 952.72399902f;
        const float D1 = 413.36801147f;
        const float D2 = 11.88600922f;
        float X2, num, den;
        X2 = x * x;
        num = fmadd(fmadd(N2, X2, N1), X2, N0);
        den = fmadd(fmadd(D2, X2, D1), X2, D0);
        num = num * x / den;
        /* MAX32(-1.f, MIN32(1.f, num)) */
        float m = 1.0f < num ? 1.0f : num;
        return -1.0f > m ? -1.0f : m;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float sigmoid_approx(float x)
    {
        return .5f + (.5f * tansig_approx(.5f * x));
    }

    // Each row's sum adds its columns' products in order, as the C's loop does; the columns go
    // outside so four rows at a time share a vector. Multiplies and adds stay separate (no fused
    // multiply-add), as the reference compiles them, so the sums are the C's to the bit.
    private static void gemm_accum(Span<float> @out, ReadOnlySpan<sbyte> weights, int rows, int cols, int col_stride, ReadOnlySpan<float> x)
    {
        if ((rows > @out.Length) || (cols > x.Length) || (rows > col_stride) || ((cols > 0) && (((cols - 1) * col_stride) + rows > weights.Length)))
        {
            throw new ArgumentOutOfRangeException(nameof(rows));
        }

        ref float output = ref MemoryMarshal.GetReference(@out);
        ref sbyte weight = ref MemoryMarshal.GetReference(weights);
        ref float input = ref MemoryMarshal.GetReference(x);
        int i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            // Sixteen outputs at a time held in registers through every column: each output still adds
            // its column products in order, as the C does
            for (; i + 16 <= rows; i += 16)
            {
                var s0 = Vector128.LoadUnsafe(ref output, (nuint)i);
                var s1 = Vector128.LoadUnsafe(ref output, (nuint)(i + 4));
                var s2 = Vector128.LoadUnsafe(ref output, (nuint)(i + 8));
                var s3 = Vector128.LoadUnsafe(ref output, (nuint)(i + 12));
                for (int j = 0; j < cols; j++)
                {
                    var xv = Vector128.Create(Unsafe.Add(ref input, j));
                    var w = Vector128.LoadUnsafe(ref Unsafe.Add(ref weight, (j * col_stride) + i));
                    var (w01, w23) = Vector128.Widen(w);
                    var (w0, w1) = Vector128.Widen(w01);
                    var (w2, w3) = Vector128.Widen(w23);
                    s0 += Vector128.ConvertToSingle(w0) * xv;
                    s1 += Vector128.ConvertToSingle(w1) * xv;
                    s2 += Vector128.ConvertToSingle(w2) * xv;
                    s3 += Vector128.ConvertToSingle(w3) * xv;
                }
                s0.StoreUnsafe(ref output, (nuint)i);
                s1.StoreUnsafe(ref output, (nuint)(i + 4));
                s2.StoreUnsafe(ref output, (nuint)(i + 8));
                s3.StoreUnsafe(ref output, (nuint)(i + 12));
            }
            for (; i + 4 <= rows; i += 4)
            {
                var sum = Vector128.LoadUnsafe(ref output, (nuint)i);
                for (int j = 0; j < cols; j++)
                {
                    var w = Vector128.WidenLower(Vector128.WidenLower(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<int>(ref Unsafe.As<sbyte, byte>(ref Unsafe.Add(ref weight, (j * col_stride) + i)))).AsSByte()));
                    sum += Vector128.ConvertToSingle(w) * Vector128.Create(Unsafe.Add(ref input, j));
                }
                sum.StoreUnsafe(ref output, (nuint)i);
            }
        }

        for (; i < rows; i++)
        {
            float sum = Unsafe.Add(ref output, i);
            for (int j = 0; j < cols; j++)
            {
                sum += Unsafe.Add(ref weight, (j * col_stride) + i) * Unsafe.Add(ref input, j);
            }
            Unsafe.Add(ref output, i) = sum;
        }
    }

    internal static void analysis_compute_dense(AnalysisDenseLayer layer, Span<float> output, ReadOnlySpan<float> input)
    {
        int i;
        int N, M;
        int stride;
        M = layer.nb_inputs;
        N = layer.nb_neurons;
        stride = N;
        for (i = 0; i < N; i++)
        {
            output[i] = layer.bias[i];
        }
        gemm_accum(output, layer.input_weights, N, M, stride, input);
        for (i = 0; i < N; i++)
        {
            output[i] *= WEIGHTS_SCALE;
        }
        if (layer.sigmoid != 0)
        {
            for (i = 0; i < N; i++)
            {
                output[i] = sigmoid_approx(output[i]);
            }
        }
        else
        {
            for (i = 0; i < N; i++)
            {
                output[i] = tansig_approx(output[i]);
            }
        }
    }

    internal static void analysis_compute_gru(AnalysisGRULayer gru, Span<float> state, ReadOnlySpan<float> input)
    {
        int i;
        int N, M;
        int stride;
        Span<float> tmp = stackalloc float[MAX_NEURONS];
        Span<float> z = stackalloc float[MAX_NEURONS];
        Span<float> r = stackalloc float[MAX_NEURONS];
        Span<float> h = stackalloc float[MAX_NEURONS];
        M = gru.nb_inputs;
        N = gru.nb_neurons;
        stride = 3 * N;
        /* Compute update gate. */
        for (i = 0; i < N; i++)
        {
            z[i] = gru.bias[i];
        }
        gemm_accum(z, gru.input_weights, N, M, stride, input);
        gemm_accum(z, gru.recurrent_weights, N, N, stride, state);
        for (i = 0; i < N; i++)
        {
            z[i] = sigmoid_approx(WEIGHTS_SCALE * z[i]);
        }

        /* Compute reset gate. */
        for (i = 0; i < N; i++)
        {
            r[i] = gru.bias[N + i];
        }
        gemm_accum(r, gru.input_weights.AsSpan(N), N, M, stride, input);
        gemm_accum(r, gru.recurrent_weights.AsSpan(N), N, N, stride, state);
        for (i = 0; i < N; i++)
        {
            r[i] = sigmoid_approx(WEIGHTS_SCALE * r[i]);
        }

        /* Compute output. */
        for (i = 0; i < N; i++)
        {
            h[i] = gru.bias[(2 * N) + i];
        }
        for (i = 0; i < N; i++)
        {
            tmp[i] = state[i] * r[i];
        }
        gemm_accum(h, gru.input_weights.AsSpan(2 * N), N, M, stride, input);
        gemm_accum(h, gru.recurrent_weights.AsSpan(2 * N), N, N, stride, tmp);
        for (i = 0; i < N; i++)
        {
            h[i] = (z[i] * state[i]) + ((1 - z[i]) * tansig_approx(WEIGHTS_SCALE * h[i]));
        }
        for (i = 0; i < N; i++)
        {
            state[i] = h[i];
        }
    }
}
