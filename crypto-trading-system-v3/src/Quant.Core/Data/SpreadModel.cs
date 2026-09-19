using System;
using Quant.Core.Config;
using Quant.Core.Numerics;

namespace Quant.Core.Data;

/// <summary>
/// Running spread statistics (spec section 56).
///
/// The absolute spread is nearly useless on its own — 5 pips is cheap on one instrument and
/// prohibitive on another, and cheap in a calm hour and prohibitive in a violent one. What
/// matters is the spread's percentile against its own recent history and its size relative
/// to ATR, which is what says whether the trade can pay for itself.
/// </summary>
public sealed class SpreadModel
{
    private readonly RollingWindow _spreads;

    public SpreadModel(DataConfig config)
    {
        if (config == null) throw new ArgumentNullException(nameof(config));
        _spreads = new RollingWindow(config.SpreadWindow);
    }

    public double Current { get; private set; }
    public int Count => _spreads.Count;
    public bool IsReady => _spreads.Count >= 30;

    public double Median => _spreads.Median();
    public double Mean => _spreads.Mean;

    /// <summary>Percentile rank of the current spread, 0..1. Returns 0.5 before ready.</summary>
    public double Percentile => _spreads.PercentileRank(Current, minSample: 30);

    public double ZScore => _spreads.ZScore(Current);

    public void Observe(double spread)
    {
        if (!MathUtil.IsFinite(spread) || spread < 0) return;
        Current = spread;
        _spreads.Add(spread);
    }

    /// <summary>Current spread as a multiple of ATR. Returns a large number when ATR is unknown.</summary>
    public double RelativeToAtr(double atr) => atr <= 0 ? double.MaxValue : Current / atr;

    /// <summary>
    /// A robust spread estimate for cost modelling: the larger of the current and median
    /// spread. Costing a trade at a momentarily tight spread is how a backtest flatters
    /// itself.
    /// </summary>
    public double CostingSpread() => IsReady ? Math.Max(Current, Median) : Current;

    public void Reset()
    {
        _spreads.Clear();
        Current = 0;
    }
}
