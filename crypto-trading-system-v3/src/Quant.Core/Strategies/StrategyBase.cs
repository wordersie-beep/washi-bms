using System;
using System.Collections.Generic;
using Quant.Core.Numerics;
using Quant.Core.Primitives;

namespace Quant.Core.Strategies;

/// <summary>
/// Shared plumbing for strategies: the exception barrier, the regime-fit gate and the small
/// helpers that keep confidence construction consistent across the ensemble.
/// </summary>
public abstract class StrategyBase : IStrategy
{
    public abstract string Name { get; }
    public abstract StrategyKind Kind { get; }
    public abstract IReadOnlyDictionary<MarketRegime, double> RegimeFit { get; }
    public abstract IReadOnlyCollection<FeatureFamily> Families { get; }

    /// <summary>
    /// Wraps the strategy's own logic in the two guarantees the ensemble depends on:
    /// it abstains outside the regimes it claims, and it never throws.
    /// </summary>
    public StrategySignal Evaluate(StrategyContext context)
    {
        if (context == null) return StrategySignal.Neutral(Name, "no context");

        try
        {
            double fit = FitFor(context.Regime.Primary);
            if (fit < context.Config.Strategy.MinRegimeFit)
            {
                return StrategySignal.Neutral(Name, $"regime {context.Regime.Primary} unsuited (fit {fit:F2})");
            }

            StrategySignal signal = EvaluateCore(context);
            return signal ?? StrategySignal.Neutral(Name, "no setup");
        }
        catch (Exception ex)
        {
            // One broken strategy must not stop the other seven from voting.
            return StrategySignal.Neutral(Name, "evaluation failed: " + ex.GetType().Name);
        }
    }

    protected abstract StrategySignal EvaluateCore(StrategyContext context);

    public double FitFor(MarketRegime regime) =>
        RegimeFit.TryGetValue(regime, out double v) ? v : 0;

    /// <summary>
    /// Combines evidence terms into a confidence.
    ///
    /// Uses a GEOMETRIC mean rather than an arithmetic one on purpose: with an arithmetic
    /// mean, four strong terms drown one term that is near zero, so a setup missing a
    /// necessary condition still scores well. Geometrically, a near-zero term drags the
    /// whole score down, which is the correct treatment of a condition that is supposed to
    /// be necessary.
    /// </summary>
    protected static double Combine(params double[] terms)
    {
        if (terms == null || terms.Length == 0) return 0;

        double product = 1.0;
        for (int i = 0; i < terms.Length; i++)
        {
            product *= MathUtil.Clamp(terms[i], 0.01, 1.0);
        }
        return MathUtil.Clamp01(Math.Pow(product, 1.0 / terms.Length));
    }

    protected static StrategySignal Signal(
        StrategyBase strategy,
        Side direction,
        double confidence,
        double anchorPrice,
        string rationale,
        double? invalidation = null,
        double? stopInAtr = null,
        double? targetR = null) => new StrategySignal
        {
            StrategyName = strategy.Name,
            Kind = strategy.Kind,
            Direction = direction,
            Confidence = MathUtil.Clamp01(confidence),
            AnchorPrice = anchorPrice,
            Rationale = rationale,
            InvalidationPrice = invalidation,
            PreferredStopInAtr = stopInAtr,
            PreferredTargetR = targetR,
        };

    /// <summary>Builds a regime-fit map compactly, defaulting every unlisted regime to zero.</summary>
    protected static IReadOnlyDictionary<MarketRegime, double> Fit(params (MarketRegime Regime, double Score)[] entries)
    {
        var map = new Dictionary<MarketRegime, double>();
        for (int i = 0; i < entries.Length; i++) map[entries[i].Regime] = entries[i].Score;
        return map;
    }

    protected static IReadOnlyCollection<FeatureFamily> Uses(params FeatureFamily[] families) => families;
}
