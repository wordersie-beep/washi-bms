using System;
using Quant.Core.Numerics;

namespace Quant.Core.Indicators;

/// <summary>
/// Bollinger bands. The system uses the WIDTH far more than the bands themselves, because
/// width normalised by the middle band is a clean, scale-free volatility-state measure.
/// </summary>
public sealed class BollingerBands : IIndicator
{
    private readonly RollingWindow _window;
    private readonly double _deviations;

    public BollingerBands(int periods, double deviations = 2.0)
    {
        _window = new RollingWindow(periods);
        _deviations = deviations;
        Periods = periods;
    }

    public int Periods { get; }
    public bool IsReady => _window.IsFull;

    public double Middle => _window.Mean;
    public double Upper => Middle + (_deviations * _window.StdDev);
    public double Lower => Middle - (_deviations * _window.StdDev);

    /// <summary>Band width as a fraction of the middle band. This is what <see cref="Value"/> reports.</summary>
    public double Value => MathUtil.SafeDiv(Upper - Lower, Middle);

    /// <summary>Where price sits inside the bands: 0 at the lower band, 1 at the upper, clamped.</summary>
    public double PercentB(double price)
    {
        double span = Upper - Lower;
        if (span < MathUtil.Epsilon) return 0.5;
        return MathUtil.Clamp((price - Lower) / span, -0.5, 1.5);
    }

    public void Update(double close) => _window.Add(close);

    public void Reset() => _window.Clear();
}
