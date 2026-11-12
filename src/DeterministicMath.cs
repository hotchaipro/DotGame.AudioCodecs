// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;

namespace DotGame.AudioCodecs;

/// <summary>
/// Sine, cosine and the modified Bessel function I0 from additions, multiplications and divisions
/// alone, so they give the same bits on every build machine.
/// </summary>
/// <remarks>
/// <see cref="Math.Sin"/> and its kin come from each operating system's C library, which can differ in
/// the last bit. Filter and window coefficients built from them would then differ too, and so would
/// the encoded audio: the same source would give different content cache entries and web fingerprints
/// on two machines. IEEE arithmetic on doubles is exact to the bit everywhere .NET runs.
/// </remarks>
internal static class DeterministicMath
{
    private const double HalfPi = Math.PI / 2;

    /// <summary>sin(<paramref name="x"/>), to within a few units in the last place.</summary>
    public static double Sin(double x)
    {
        // Reduce to [-pi/4, pi/4] by quadrant. Arguments here are at most a few thousand radians,
        // where Math.Round and one product keep the reduction exact enough.
        double quadrant = Math.Round(x / HalfPi, MidpointRounding.ToEven);
        double r = x - (quadrant * HalfPi);
        int q = (int)(((long)quadrant) & 3);
        return q switch
        {
            0 => SinKernel(r),
            1 => CosKernel(r),
            2 => -SinKernel(r),
            _ => -CosKernel(r),
        };
    }

    /// <summary>cos(<paramref name="x"/>), to within a few units in the last place.</summary>
    public static double Cos(double x) => Sin(x + HalfPi);

    /// <summary>
    /// log2(<paramref name="x"/>) for <paramref name="x"/> &gt; 0, to about 1e-15: the exponent exactly, and
    /// the mantissa's logarithm by the series of atanh.
    /// </summary>
    public static double Log2(double x)
    {
        if (!(x > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(x));
        }

        int exponent = Math.ILogB(x);
        double m = Math.ScaleB(x, -exponent);

        // ln(m) = 2 atanh(t), t = (m - 1) / (m + 1), |t| < 1/3 for m in [1, 2)
        double t = (m - 1) / (m + 1);
        double t2 = t * t;
        double term = t;
        double sum = 0;
        for (int k = 1; k <= 31; k += 2)
        {
            sum += term / k;
            term *= t2;
        }

        return exponent + (2 * sum / Ln2);
    }

    private const double Ln2 = 0.6931471805599453;

    /// <summary>The zeroth-order modified Bessel function of the first kind, by its power series.</summary>
    public static double BesselI0(double x)
    {
        double sum = 1;
        double term = 1;
        double quarter = x * x / 4;
        for (int k = 1; k < 500; k++)
        {
            term *= quarter / ((double)k * k);
            sum += term;
            if (term < sum * 1e-17)
            {
                break;
            }
        }

        return sum;
    }

    // Taylor series on [-pi/4, pi/4], summed to a fixed number of terms (the last ones are below a
    // double's precision there)
    private static double SinKernel(double r)
    {
        double r2 = r * r;
        double term = r;
        double sum = r;
        for (int k = 1; k <= 10; k++)
        {
            term *= -r2 / ((2.0 * k) * ((2.0 * k) + 1));
            sum += term;
        }

        return sum;
    }

    private static double CosKernel(double r)
    {
        double r2 = r * r;
        double term = 1;
        double sum = 1;
        for (int k = 1; k <= 10; k++)
        {
            term *= -r2 / (((2.0 * k) - 1) * (2.0 * k));
            sum += term;
        }

        return sum;
    }
}
