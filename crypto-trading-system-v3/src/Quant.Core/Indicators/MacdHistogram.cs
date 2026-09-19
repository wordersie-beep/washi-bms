using Quant.Core.Numerics;

namespace Quant.Core.Indicators;

/// <summary>MACD line, signal line and histogram.</summary>
public sealed class MacdHistogram : IIndicator
{
    private readonly Ema _fast;
    private readonly Ema _slow;
    private readonly Ema _signal;

    public MacdHistogram(int fastPeriods = 12, int slowPeriods = 26, int signalPeriods = 9)
    {
        _fast = new Ema(fastPeriods);
        _slow = new Ema(slowPeriods);
        _signal = new Ema(signalPeriods);
    }

    public bool IsReady => _slow.IsReady && _signal.IsReady;

    public double Macd => _fast.Value - _slow.Value;
    public double Signal => _signal.Value;

    /// <summary>Histogram: MACD minus signal.</summary>
    public double Value => Macd - Signal;

    public double PreviousHistogram { get; private set; }

    public void Update(double close)
    {
        PreviousHistogram = Value;

        _fast.Update(close);
        _slow.Update(close);
        if (_slow.IsReady) _signal.Update(Macd);
    }

    /// <summary>Histogram normalised by price so it is comparable across instruments.</summary>
    public double NormalizedBy(double price) => MathUtil.SafeDiv(Value, price);

    public void Reset()
    {
        _fast.Reset();
        _slow.Reset();
        _signal.Reset();
        PreviousHistogram = 0;
    }
}
