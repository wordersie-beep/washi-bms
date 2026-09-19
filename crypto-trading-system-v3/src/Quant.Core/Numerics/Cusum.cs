using System;

namespace Quant.Core.Numerics;

/// <summary>
/// Two-sided CUSUM change detector (spec section 104).
/// Detects a sustained shift in the mean of a standardised stream faster than a rolling
/// average does, which is what "the edge quietly stopped working" looks like in practice.
///
/// Inputs are expected in standard-deviation units. <paramref name="slack"/> (k) is the
/// smallest shift worth reacting to, conventionally half the shift you care about;
/// <paramref name="threshold"/> (h) trades detection delay against false alarms.
/// </summary>
public sealed class Cusum
{
    private readonly double _slack;
    private readonly double _threshold;

    public Cusum(double slack = 0.5, double threshold = 5.0)
    {
        _slack = Math.Max(0, slack);
        _threshold = Math.Max(0.1, threshold);
    }

    public double High { get; private set; }
    public double Low { get; private set; }
    public int Observations { get; private set; }

    public bool UpwardShiftDetected => High > _threshold;
    public bool DownwardShiftDetected => Low < -_threshold;

    /// <summary>How far past the alarm threshold the downward statistic has travelled, 0..1+.</summary>
    public double DownwardSeverity => _threshold <= 0 ? 0 : MathUtil.Clamp(-Low / _threshold, 0, 4);

    public void Add(double standardisedValue)
    {
        if (!MathUtil.IsFinite(standardisedValue)) return;

        High = Math.Max(0, High + standardisedValue - _slack);
        Low = Math.Min(0, Low + standardisedValue + _slack);

        // Keep the statistics bounded; an unbounded CUSUM takes a very long time to reset
        // after a large excursion, which would freeze a strategy long past its recovery.
        High = Math.Min(High, _threshold * 4);
        Low = Math.Max(Low, -_threshold * 4);

        Observations++;
    }

    public void Reset()
    {
        High = 0;
        Low = 0;
        Observations = 0;
    }
}
