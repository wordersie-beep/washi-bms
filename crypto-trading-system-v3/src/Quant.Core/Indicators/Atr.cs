using System;
using Quant.Core.Numerics;
using Quant.Core.Primitives;

namespace Quant.Core.Indicators;

/// <summary>Average True Range, Wilder-smoothed. The system's unit of distance.</summary>
public sealed class Atr : IIndicator
{
    private readonly WilderSmoother _smoother;
    private double _previousClose;
    private bool _hasPrevious;

    public Atr(int periods)
    {
        _smoother = new WilderSmoother(periods);
        Periods = periods;
    }

    public int Periods { get; }
    public bool IsReady => _smoother.IsReady;
    public double Value => _smoother.Value;

    public void Update(in Candle bar)
    {
        double trueRange;
        if (!_hasPrevious)
        {
            trueRange = bar.High - bar.Low;
        }
        else
        {
            double hl = bar.High - bar.Low;
            double hc = Math.Abs(bar.High - _previousClose);
            double lc = Math.Abs(bar.Low - _previousClose);
            trueRange = Math.Max(hl, Math.Max(hc, lc));
        }

        _smoother.Update(trueRange);
        _previousClose = bar.Close;
        _hasPrevious = true;
    }

    /// <summary>ATR expressed as a fraction of price — the comparable form across instruments.</summary>
    public double AsFractionOf(double price) => MathUtil.SafeDiv(Value, price);

    public void Reset()
    {
        _smoother.Reset();
        _previousClose = 0;
        _hasPrevious = false;
    }
}
