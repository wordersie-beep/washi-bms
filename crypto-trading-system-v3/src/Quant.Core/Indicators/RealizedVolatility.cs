using System;
using Quant.Core.Numerics;

namespace Quant.Core.Indicators;

/// <summary>
/// Realised volatility from log returns, reported both per bar and annualised.
/// Kept separate from ATR on purpose: ATR is a range measure that includes gaps, realised
/// volatility is a close-to-close measure. They disagree exactly when the market is gapping,
/// and that disagreement is itself a useful signal.
/// </summary>
public sealed class RealizedVolatility : IIndicator
{
    private readonly RollingWindow _returns;
    private readonly double _barsPerYear;
    private double _previousClose;
    private bool _hasPrevious;

    public RealizedVolatility(int periods, double barsPerYear)
    {
        _returns = new RollingWindow(periods);
        _barsPerYear = barsPerYear > 0 ? barsPerYear : 1;
        Periods = periods;
    }

    public int Periods { get; }
    public bool IsReady => _returns.Count >= Math.Min(Periods, 10);

    /// <summary>Standard deviation of log returns per bar.</summary>
    public double Value => _returns.StdDev;

    public double Annualized => Value * Math.Sqrt(_barsPerYear);

    /// <summary>Latest log return.</summary>
    public double LastReturn => _returns.Count > 0 ? _returns[0] : 0;

    public RollingWindow Returns => _returns;

    public void Update(double close)
    {
        if (!MathUtil.IsFinite(close) || close <= 0) return;

        if (_hasPrevious && _previousClose > 0)
        {
            _returns.Add(Math.Log(close / _previousClose));
        }

        _previousClose = close;
        _hasPrevious = true;
    }

    public void Reset()
    {
        _returns.Clear();
        _previousClose = 0;
        _hasPrevious = false;
    }
}
