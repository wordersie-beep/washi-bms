using System;
using Quant.Core.Numerics;

namespace Quant.Core.Indicators;

/// <summary>Relative Strength Index, Wilder-smoothed.</summary>
public sealed class Rsi : IIndicator
{
    private readonly WilderSmoother _gain;
    private readonly WilderSmoother _loss;
    private double _previous;
    private bool _hasPrevious;

    public Rsi(int periods)
    {
        _gain = new WilderSmoother(periods);
        _loss = new WilderSmoother(periods);
    }

    public bool IsReady => _gain.IsReady && _loss.IsReady;

    /// <summary>0..100. Returns 50 (neutral) before the indicator is ready.</summary>
    public double Value
    {
        get
        {
            if (!IsReady) return 50;
            double avgLoss = _loss.Value;
            if (avgLoss < MathUtil.Epsilon) return _gain.Value < MathUtil.Epsilon ? 50 : 100;
            double rs = _gain.Value / avgLoss;
            return 100.0 - (100.0 / (1.0 + rs));
        }
    }

    public void Update(double close)
    {
        if (!MathUtil.IsFinite(close)) return;

        if (!_hasPrevious)
        {
            _previous = close;
            _hasPrevious = true;
            return;
        }

        double change = close - _previous;
        _gain.Update(change > 0 ? change : 0);
        _loss.Update(change < 0 ? -change : 0);
        _previous = close;
    }

    public void Reset()
    {
        _gain.Reset();
        _loss.Reset();
        _previous = 0;
        _hasPrevious = false;
    }
}
