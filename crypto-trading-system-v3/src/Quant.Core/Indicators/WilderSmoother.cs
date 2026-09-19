using System;
using Quant.Core.Numerics;

namespace Quant.Core.Indicators;

/// <summary>
/// Wilder's smoothing (equivalent to an EMA with alpha = 1/N), seeded with the simple
/// average of the first N samples. ATR, RSI and ADX are all defined in these terms, so
/// they share one implementation rather than three subtly different ones.
/// </summary>
public sealed class WilderSmoother : IIndicator
{
    private readonly int _periods;
    private double _seedSum;
    private int _seedCount;
    private double _value;

    public WilderSmoother(int periods)
    {
        if (periods < 1) throw new ArgumentOutOfRangeException(nameof(periods));
        _periods = periods;
    }

    public bool IsReady => _seedCount >= _periods;
    public double Value => _value;

    public void Update(double sample)
    {
        if (!MathUtil.IsFinite(sample)) return;

        if (_seedCount < _periods)
        {
            _seedSum += sample;
            _seedCount++;
            _value = _seedSum / _seedCount;
            return;
        }

        _value = _value + ((sample - _value) / _periods);
    }

    public void Reset()
    {
        _seedSum = 0;
        _seedCount = 0;
        _value = 0;
    }
}
