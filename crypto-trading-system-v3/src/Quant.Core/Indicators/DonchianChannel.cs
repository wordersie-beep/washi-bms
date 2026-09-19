using System;
using Quant.Core.Numerics;
using Quant.Core.Primitives;

namespace Quant.Core.Indicators;

/// <summary>
/// Highest high and lowest low over N CLOSED bars.
///
/// The current bar is deliberately excluded from the channel: including it means the
/// breakout level moves with the bar that is supposed to break it, which is the classic way
/// a breakout backtest ends up trading its own look-ahead.
/// </summary>
public sealed class DonchianChannel : IIndicator
{
    private readonly RollingWindow _highs;
    private readonly RollingWindow _lows;

    public DonchianChannel(int periods)
    {
        _highs = new RollingWindow(periods);
        _lows = new RollingWindow(periods);
        Periods = periods;
    }

    public int Periods { get; }
    public bool IsReady => _highs.IsFull;

    public double Upper => _highs.Count == 0 ? 0 : _highs.Max();
    public double Lower => _lows.Count == 0 ? 0 : _lows.Min();
    public double Middle => (Upper + Lower) / 2.0;
    public double Width => Upper - Lower;

    /// <summary>Channel width as a fraction of the mid price.</summary>
    public double Value => MathUtil.SafeDiv(Width, Middle);

    /// <summary>Position of a price inside the channel: 0 at the low, 1 at the high.</summary>
    public double PositionOf(double price)
    {
        double w = Width;
        if (w < MathUtil.Epsilon) return 0.5;
        return MathUtil.Clamp((price - Lower) / w, -0.5, 1.5);
    }

    public void Update(in Candle closedBar)
    {
        _highs.Add(closedBar.High);
        _lows.Add(closedBar.Low);
    }

    public void Reset()
    {
        _highs.Clear();
        _lows.Clear();
    }
}
