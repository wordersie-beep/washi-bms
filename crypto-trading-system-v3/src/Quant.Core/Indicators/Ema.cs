using System;
using Quant.Core.Numerics;

namespace Quant.Core.Indicators;

/// <summary>Exponential moving average, seeded with a simple average of the first N samples.</summary>
public sealed class Ema : IIndicator
{
    private readonly int _periods;
    private readonly double _alpha;
    private double _seedSum;
    private int _seedCount;
    private double _value;

    public Ema(int periods)
    {
        if (periods < 1) throw new ArgumentOutOfRangeException(nameof(periods));
        _periods = periods;
        _alpha = 2.0 / (periods + 1.0);
    }

    public int Periods => _periods;
    public bool IsReady => _seedCount >= _periods;
    public double Value => _value;

    /// <summary>Previous value, for slope measurement without a second buffer.</summary>
    public double Previous { get; private set; }

    public void Update(double sample)
    {
        if (!MathUtil.IsFinite(sample)) return;

        Previous = _value;

        if (_seedCount < _periods)
        {
            _seedSum += sample;
            _seedCount++;
            _value = _seedSum / _seedCount;
            return;
        }

        _value += _alpha * (sample - _value);
    }

    public void Reset()
    {
        _seedSum = 0;
        _seedCount = 0;
        _value = 0;
        Previous = 0;
    }
}
