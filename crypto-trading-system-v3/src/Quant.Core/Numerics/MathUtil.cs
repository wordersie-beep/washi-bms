using System;

namespace Quant.Core.Numerics;

/// <summary>Small numeric helpers used across the stack. All total functions: no throws, no NaN out.</summary>
public static class MathUtil
{
    public const double Epsilon = 1e-12;

    public static double Clamp(double value, double min, double max)
    {
        if (double.IsNaN(value)) return min;
        return value < min ? min : (value > max ? max : value);
    }

    public static int ClampInt(int value, int min, int max) => value < min ? min : (value > max ? max : value);

    public static double Clamp01(double value) => Clamp(value, 0.0, 1.0);

    /// <summary>Division that yields <paramref name="fallback"/> instead of infinity or NaN.</summary>
    public static double SafeDiv(double numerator, double denominator, double fallback = 0.0)
    {
        if (Math.Abs(denominator) < Epsilon) return fallback;
        double r = numerator / denominator;
        return IsFinite(r) ? r : fallback;
    }

    public static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

    /// <summary>Returns <paramref name="fallback"/> when the value is not a usable number.</summary>
    public static double Finite(double v, double fallback = 0.0) => IsFinite(v) ? v : fallback;

    public static double Logistic(double x) => 1.0 / (1.0 + Math.Exp(-Clamp(x, -40, 40)));

    /// <summary>Maps a value to 0..1 linearly between two bounds, clamped at both ends.</summary>
    public static double LinearScale(double value, double atZero, double atOne)
    {
        if (Math.Abs(atOne - atZero) < Epsilon) return value >= atOne ? 1.0 : 0.0;
        return Clamp01((value - atZero) / (atOne - atZero));
    }

    /// <summary>Standard normal CDF via Abramowitz &amp; Stegun 7.1.26. Max abs error ~1.5e-7.</summary>
    public static double NormalCdf(double x)
    {
        if (!IsFinite(x)) return x > 0 ? 1.0 : 0.0;

        double sign = x < 0 ? -1.0 : 1.0;
        double ax = Math.Abs(x) / Math.Sqrt(2.0);

        const double a1 = 0.254829592, a2 = -0.284496736, a3 = 1.421413741, a4 = -1.453152027, a5 = 1.061405429, p = 0.3275911;
        double t = 1.0 / (1.0 + (p * ax));
        double y = 1.0 - ((((((((a5 * t) + a4) * t) + a3) * t) + a2) * t) + a1) * t * Math.Exp(-ax * ax);

        return 0.5 * (1.0 + (sign * y));
    }

    /// <summary>Geometric mean of positive weights; used to combine multiplicative factors without one term dominating.</summary>
    public static double GeometricMean(params double[] values)
    {
        if (values == null || values.Length == 0) return 1.0;
        double sumLog = 0;
        int n = 0;
        for (int i = 0; i < values.Length; i++)
        {
            double v = values[i];
            if (!IsFinite(v) || v <= 0) continue;
            sumLog += Math.Log(v);
            n++;
        }
        return n == 0 ? 1.0 : Math.Exp(sumLog / n);
    }

    /// <summary>Quantile of an already-sorted ascending array, using linear interpolation.</summary>
    public static double QuantileSorted(double[] sortedAscending, int count, double q)
    {
        if (sortedAscending == null || count <= 0) return 0;
        if (count == 1) return sortedAscending[0];

        q = Clamp01(q);
        double pos = q * (count - 1);
        int lo = (int)Math.Floor(pos);
        int hi = (int)Math.Ceiling(pos);
        if (lo == hi) return sortedAscending[lo];
        double frac = pos - lo;
        return (sortedAscending[lo] * (1 - frac)) + (sortedAscending[hi] * frac);
    }
}
