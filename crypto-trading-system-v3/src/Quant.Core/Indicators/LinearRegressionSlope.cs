using System;
using Quant.Core.Numerics;

namespace Quant.Core.Indicators;

/// <summary>
/// Least-squares slope over a rolling window, plus R-squared.
///
/// The slope alone is not usable as a trend measure: a steep slope through noise means
/// nothing. R-squared is reported alongside it so callers can require that a trend be both
/// steep AND well fitted before treating it as a trend.
/// </summary>
public sealed class LinearRegressionSlope : IIndicator
{
    private readonly RollingWindow _window;

    public LinearRegressionSlope(int periods)
    {
        if (periods < 3) throw new ArgumentOutOfRangeException(nameof(periods), "Need at least 3 points for a slope.");
        _window = new RollingWindow(periods);
        Periods = periods;
    }

    public int Periods { get; }
    public bool IsReady => _window.IsFull;

    /// <summary>Slope in units of y per bar.</summary>
    public double Value { get; private set; }

    /// <summary>Goodness of fit, 0..1.</summary>
    public double RSquared { get; private set; }

    public void Update(double y)
    {
        _window.Add(y);
        Recompute();
    }

    private void Recompute()
    {
        int n = _window.Count;
        if (n < 3) { Value = 0; RSquared = 0; return; }

        // x runs 0..n-1 oldest to newest.
        double sumX = 0, sumY = 0, sumXy = 0, sumXx = 0;
        for (int i = 0; i < n; i++)
        {
            double x = i;
            double y = _window[n - 1 - i];
            sumX += x;
            sumY += y;
            sumXy += x * y;
            sumXx += x * x;
        }

        double denom = (n * sumXx) - (sumX * sumX);
        if (Math.Abs(denom) < MathUtil.Epsilon) { Value = 0; RSquared = 0; return; }

        double slope = ((n * sumXy) - (sumX * sumY)) / denom;
        double intercept = (sumY - (slope * sumX)) / n;

        double meanY = sumY / n;
        double ssTot = 0, ssRes = 0;
        for (int i = 0; i < n; i++)
        {
            double y = _window[n - 1 - i];
            double fit = intercept + (slope * i);
            ssTot += (y - meanY) * (y - meanY);
            ssRes += (y - fit) * (y - fit);
        }

        Value = MathUtil.Finite(slope);
        RSquared = ssTot < MathUtil.Epsilon ? 0 : MathUtil.Clamp01(1.0 - (ssRes / ssTot));
    }

    /// <summary>Slope per bar expressed as a fraction of price — comparable across instruments.</summary>
    public double NormalizedBy(double price) => MathUtil.SafeDiv(Value, price);

    public void Reset()
    {
        _window.Clear();
        Value = 0;
        RSquared = 0;
    }
}
