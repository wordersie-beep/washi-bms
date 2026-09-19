using System;
using Quant.Core.Numerics;
using Quant.Core.Primitives;

namespace Quant.Core.Indicators;

/// <summary>
/// Wilder's Directional Movement System: +DI, -DI and ADX.
/// ADX measures trend STRENGTH without direction; the DI pair supplies direction.
/// </summary>
public sealed class Adx : IIndicator
{
    private readonly WilderSmoother _trSmooth;
    private readonly WilderSmoother _plusDmSmooth;
    private readonly WilderSmoother _minusDmSmooth;
    private readonly WilderSmoother _dxSmooth;
    private Candle _previous;
    private bool _hasPrevious;

    public Adx(int periods)
    {
        _trSmooth = new WilderSmoother(periods);
        _plusDmSmooth = new WilderSmoother(periods);
        _minusDmSmooth = new WilderSmoother(periods);
        _dxSmooth = new WilderSmoother(periods);
        Periods = periods;
    }

    public int Periods { get; }
    public bool IsReady => _dxSmooth.IsReady;

    /// <summary>ADX, 0..100. Returns 0 before the indicator is ready.</summary>
    public double Value => IsReady ? _dxSmooth.Value : 0;

    public double PlusDi => MathUtil.SafeDiv(100.0 * _plusDmSmooth.Value, _trSmooth.Value);
    public double MinusDi => MathUtil.SafeDiv(100.0 * _minusDmSmooth.Value, _trSmooth.Value);

    /// <summary>+1 when +DI leads, -1 when -DI leads, 0 when they are level.</summary>
    public int DirectionalBias
    {
        get
        {
            double p = PlusDi, m = MinusDi;
            if (Math.Abs(p - m) < MathUtil.Epsilon) return 0;
            return p > m ? 1 : -1;
        }
    }

    public void Update(in Candle bar)
    {
        if (!_hasPrevious)
        {
            _previous = bar;
            _hasPrevious = true;
            return;
        }

        double upMove = bar.High - _previous.High;
        double downMove = _previous.Low - bar.Low;

        double plusDm = (upMove > downMove && upMove > 0) ? upMove : 0;
        double minusDm = (downMove > upMove && downMove > 0) ? downMove : 0;

        double tr = Math.Max(bar.High - bar.Low,
                    Math.Max(Math.Abs(bar.High - _previous.Close), Math.Abs(bar.Low - _previous.Close)));

        _trSmooth.Update(tr);
        _plusDmSmooth.Update(plusDm);
        _minusDmSmooth.Update(minusDm);

        if (_trSmooth.IsReady)
        {
            double pdi = PlusDi;
            double mdi = MinusDi;
            double sum = pdi + mdi;
            double dx = sum < MathUtil.Epsilon ? 0 : 100.0 * Math.Abs(pdi - mdi) / sum;
            _dxSmooth.Update(dx);
        }

        _previous = bar;
    }

    public void Reset()
    {
        _trSmooth.Reset();
        _plusDmSmooth.Reset();
        _minusDmSmooth.Reset();
        _dxSmooth.Reset();
        _hasPrevious = false;
    }
}
