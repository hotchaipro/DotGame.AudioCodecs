// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;

namespace DotGame.AudioCodecs.Opus.Common;

/// <summary>
/// The C library functions the encoder's float analysis calls (analysis.c), with C's names, from
/// IEEE arithmetic alone, so an encode gives the same bytes on every build machine.
/// <see cref="Math.Log(double)"/> comes from each operating system's C library, which can differ in the
/// last bit.
/// </summary>
/// <remarks>
/// The analysis takes the log of a float and casts it back to float, so a double result within a
/// couple of units in the last place gives the float libm's does but in the rarest of cases: the
/// encoder's output was byte-identical to native libopus's (macOS's libm) on every test file.
/// </remarks>
internal static class OpusMath
{
    // ln 2, split so that e * Ln2Hi is exact for any exponent e of a double
    private const double Ln2Hi = 6.93147180369123816490e-01;
    private const double Ln2Lo = 1.90821492927058770002e-10;
    private const double InvLn10 = 0.43429448190325182765;
    private const double Sqrt2 = 1.41421356237309504880;

    /// <summary>The natural logarithm of <paramref name="x"/>.</summary>
    internal static double log(double x)
    {
        if (double.IsNaN(x) || (x < 0))
        {
            return double.NaN;
        }
        if (x == 0)
        {
            return double.NegativeInfinity;
        }
        if (double.IsPositiveInfinity(x))
        {
            return x;
        }

        // x = m * 2^e with m in [sqrt(1/2), sqrt(2))
        long bits = BitConverter.DoubleToInt64Bits(x);
        int e = (int)((bits >> 52) & 0x7ff);
        if (e == 0)
        {
            // Subnormal: scale into the normal range first
            bits = BitConverter.DoubleToInt64Bits(x * 18014398509481984.0); // 2^54
            e = (int)((bits >> 52) & 0x7ff) - 54;
        }
        e -= 1023;
        double m = BitConverter.Int64BitsToDouble((bits & 0x000fffffffffffffL) | 0x3ff0000000000000L);
        if (m >= Sqrt2)
        {
            m *= 0.5;
            e++;
        }

        // log(m) = 2 atanh(s), s = (m - 1) / (m + 1), |s| < 0.1716: the odd series to s^31
        double f = m - 1.0;
        double s = f / (m + 1.0);
        double s2 = s * s;
        double sum = 1.0 / 31;
        for (int k = 29; k >= 3; k -= 2)
        {
            sum = (sum * s2) + (1.0 / k);
        }
        // 2s = f - s*f, exactly enough: f - (f*f/(m+1))
        double r = s * s2 * sum * 2.0;
        double hf = s * f;
        return (e * Ln2Hi) + ((f - hf) + ((e * Ln2Lo) + r));
    }

    /// <summary>The base-10 logarithm of <paramref name="x"/>.</summary>
    internal static double log10(double x) => log(x) * InvLn10;
}
