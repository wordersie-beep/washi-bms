using System;

namespace Quant.Core.Numerics;

/// <summary>
/// Exponentially weighted mean with an explicit half-life, plus an effective sample size.
/// Used wherever "recent performance" must be tracked without letting a handful of trades
/// erase a long-term estimate (spec section 21): the effective sample size is what tells
/// the caller how much the recent number is actually worth.
/// </summary>
public sealed class Ewma
{
    private readonly double _alpha;
    private double _value;
    private double _weightSum;
    private int _observations;

    /// <param name="halfLifeObservations">Number of observations after which weight halves.</param>
    public Ewma(double halfLifeObservations)
    {
        if (halfLifeObservations <= 0) throw new ArgumentOutOfRangeException(nameof(halfLifeObservations));
        HalfLife = halfLifeObservations;
        _alpha = 1.0 - Math.Pow(0.5, 1.0 / halfLifeObservations);
    }

    public double HalfLife { get; }
    public int Observations => _observations;
    public bool HasValue => _observations > 0;

    public double Value => _observations == 0 ? 0 : _value;

    /// <summary>
    /// Kish effective sample size of the exponential weights, (sum w)^2 / sum(w^2).
    /// For geometric weights this converges to (2 - alpha) / alpha, which in terms of the
    /// half-life H is approximately 2H/ln(2) - 1: a 20-observation half-life saturates
    /// near 58 effective observations, not 20. Callers use this, never the half-life, when
    /// deciding how much a "recent performance" number is actually worth.
    /// </summary>
    public double EffectiveSampleSize
    {
        get
        {
            if (_observations == 0) return 0;
            double steady = (2.0 - _alpha) / _alpha;
            // Early on, the true effective size is bounded by how many observations exist.
            return Math.Min(steady, _observations);
        }
    }

    public void Add(double x)
    {
        if (!MathUtil.IsFinite(x)) return;

        if (_observations == 0) _value = x;
        else _value += _alpha * (x - _value);

        _weightSum = (_weightSum * (1 - _alpha)) + 1;
        _observations++;
    }

    public void Reset()
    {
        _value = 0;
        _weightSum = 0;
        _observations = 0;
    }

    public double WeightSum => _weightSum;
}
