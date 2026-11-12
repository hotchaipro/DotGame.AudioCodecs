// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;

namespace DotGame.AudioCodecs;

/// <summary>
/// Changes the sample rate of interleaved audio as it streams, with a Kaiser-windowed sinc filter, the
/// same bits on every machine: input goes in with <see cref="Write"/> in pieces of any size, and output
/// comes out as soon as the filter has the input it needs.
/// </summary>
/// <remarks>
/// <para>
/// The filter has 16 zero crossings either side, a cutoff at 97% of the lower rate's Nyquist frequency,
/// and a Kaiser window with beta 9; when downsampling it widens with the ratio, so it still stops what
/// the lower rate can't hold. Output frame <c>n</c> is at exactly <c>n × from / to</c> in the input, so
/// there's no drift and no delay: output frame 0 is input frame 0, and the input is taken as silent
/// before its start and, once <see cref="Flush"/> says it has ended, after its end.
/// </para>
/// <para>
/// The ratio is reduced to <c>L / M</c>, and a table holds the filter for each of the <c>L</c>
/// positions between input frames (160 for 44.1 to 48 kHz); above 4,096, an unusual pair of rates, each
/// filter is worked out as it's needed. Each filter sums to 1, so a constant stays constant.
/// Coefficients come from <see cref="DeterministicMath"/> and samples are summed in a fixed order.
/// </para>
/// </remarks>
internal sealed class Resampler
{
    private const int ZeroCrossings = 16;
    private const double Cutoff = 0.97;
    private const double KaiserBeta = 9;
    private const int MaxTablePhases = 4096;

    private readonly int _channels;
    private readonly long _up;
    private readonly long _down;
    private readonly double _scale;
    private readonly int _half;
    private readonly int _taps;
    private readonly double[]? _table;
    private readonly double[] _kernel;
    private float[] _input;            // the input frames still needed, from _base
    private long _base;                // the input frame _input starts at
    private long _received;            // input frames written
    private long _next;                // the next output frame
    private long _end = -1;            // the output frames there are, once the input has ended

    public Resampler(int channels, int fromRate, int toRate)
    {
        long divisor = Gcd(fromRate, toRate);
        this._channels = channels;
        this._up = toRate / divisor;
        this._down = fromRate / divisor;
        this._scale = Math.Min(1.0, (double)toRate / fromRate);
        this._half = (int)Math.Ceiling(ZeroCrossings / this._scale);
        this._taps = 2 * this._half;
        this._table = this._up <= MaxTablePhases ? BuildTable((int)this._up, this._half, this._scale) : null;
        this._kernel = new double[this._taps];
        this._input = new float[(this._taps + 4096) * channels];
    }

    /// <summary>
    /// Takes <paramref name="input"/> (whole frames, interleaved) and writes the output frames it
    /// completes to <paramref name="output"/>, calling it with each run of them.
    /// </summary>
    public void Write(ReadOnlySpan<float> input, OutputHandler output)
    {
        int frames = input.Length / this._channels;
        int done = 0;
        while (done < frames)
        {
            // Room after what's kept: grown if a piece is larger than the buffer
            int kept = (int)(this._received - this._base);
            int room = (this._input.Length / this._channels) - kept;
            if (room == 0)
            {
                Array.Resize(ref this._input, this._input.Length * 2);
                continue;
            }
            int take = Math.Min(room, frames - done);
            input.Slice(done * this._channels, take * this._channels).CopyTo(this._input.AsSpan(kept * this._channels));
            this._received += take;
            done += take;
            this.Produce(output);
        }
    }

    /// <summary>Ends the input: writes the output frames left, the input taken as silent past its end.</summary>
    public void Flush(OutputHandler output)
    {
        this._end = ((this._received * this._up) + (this._down / 2)) / this._down;
        this.Produce(output);
    }

    /// <summary>Called with each run of output frames, interleaved; the span is valid during the call.</summary>
    public delegate void OutputHandler(ReadOnlySpan<float> frames);

    // Writes every output frame whose taps the input reaches (or all, once it's ended), then drops
    // the input no later output needs
    private void Produce(OutputHandler output)
    {
        int channels = this._channels;
        Span<float> chunk = stackalloc float[256 * channels];
        int count = 0;
        while (true)
        {
            long position = this._next * this._down;
            long whole = position / this._up;
            long first = whole - this._half + 1;
            if (this._end >= 0 ? this._next >= this._end : first + this._taps > this._received)
            {
                break;
            }

            int phase = (int)(position % this._up);
            ReadOnlySpan<double> filter;
            if (this._table is not null)
            {
                filter = this._table.AsSpan(phase * this._taps, this._taps);
            }
            else
            {
                Kernel(this._kernel, (double)phase / this._up, this._half, this._scale);
                filter = this._kernel;
            }
            for (int c = 0; c < channels; c++)
            {
                double sum = 0;
                for (int k = 0; k < filter.Length; k++)
                {
                    long index = first + k;
                    if ((index >= 0) && (index < this._received))
                    {
                        sum += filter[k] * this._input[((index - this._base) * channels) + c];
                    }
                }
                chunk[(count * channels) + c] = (float)sum;
            }
            this._next++;
            if (++count == 256)
            {
                output(chunk);
                count = 0;
            }
        }
        if (count > 0)
        {
            output(chunk[..(count * channels)]);
        }

        // The next output's first tap is the oldest input still needed
        long keepFrom = Math.Clamp(((this._next * this._down) / this._up) - this._half + 1, this._base, this._received);
        if (keepFrom > this._base)
        {
            int drop = (int)(keepFrom - this._base);
            int kept = (int)(this._received - keepFrom);
            this._input.AsSpan(drop * channels, kept * channels).CopyTo(this._input);
            this._base = keepFrom;
        }
    }

    private static double[] BuildTable(int phases, int half, double scale)
    {
        int taps = 2 * half;
        var table = new double[phases * taps];
        for (int p = 0; p < phases; p++)
        {
            Kernel(table.AsSpan(p * taps, taps), (double)p / phases, half, scale);
        }
        return table;
    }

    // The filter for an output frac (0 <= frac < 1) after an input frame: taps at offsets
    // -half + 1 .. half from that frame
    private static void Kernel(Span<double> kernel, double frac, int half, double scale)
    {
        double cutoff = Cutoff * scale;
        double window = DeterministicMath.BesselI0(KaiserBeta);
        double sum = 0;
        for (int k = 0; k < kernel.Length; k++)
        {
            double x = (k - half + 1) - frac;
            double t = x / half;
            double w = (t is >= -1 and <= 1) ? DeterministicMath.BesselI0(KaiserBeta * Math.Sqrt(1 - (t * t))) / window : 0;
            double arg = Math.PI * cutoff * x;
            double sinc = x == 0 ? 1 : DeterministicMath.Sin(arg) / arg;
            kernel[k] = cutoff * sinc * w;
            sum += kernel[k];
        }
        for (int k = 0; k < kernel.Length; k++)
        {
            kernel[k] /= sum;
        }
    }

    private static long Gcd(long a, long b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }
        return a;
    }
}
