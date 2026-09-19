using System.Collections.Generic;
using Quant.Core.Primitives;

namespace Quant.Core.Strategies;

/// <summary>
/// Families of information a strategy reads.
///
/// This exists so that signal overlap can be detected STRUCTURALLY, on day one, before any
/// realised correlation history has accumulated (spec sections 77-78). Three strategies all
/// reading momentum are not three independent confirmations no matter how the empirical
/// correlation happens to have come out over the last fifty trades.
/// </summary>
public enum FeatureFamily
{
    TrendDirection,
    Momentum,
    Volatility,
    RangePosition,
    MarketStructure,
    Volume,
    Microstructure,
}

/// <summary>
/// A single trading strategy (spec sections 12-13).
///
/// Every strategy is independent and specialised. None of them is expected to work in all
/// conditions, and a strategy that claims to is treated as a bug rather than a feature:
/// <see cref="RegimeFit"/> forces each one to declare where it believes it has an edge, and
/// the ensemble holds it to that declaration.
/// </summary>
public interface IStrategy
{
    string Name { get; }
    StrategyKind Kind { get; }

    /// <summary>
    /// Prior suitability per regime, 0..1. This is only the STARTING point: once enough
    /// trades exist, realised per-regime performance takes over (spec section 74).
    /// </summary>
    IReadOnlyDictionary<MarketRegime, double> RegimeFit { get; }

    /// <summary>Information families this strategy reads, for structural overlap detection.</summary>
    IReadOnlyCollection<FeatureFamily> Families { get; }

    /// <summary>
    /// Evaluates the current market state. Must never throw and must never return null;
    /// on any internal failure it returns a neutral signal, because a strategy that fails
    /// must abstain rather than take the ensemble down with it.
    /// </summary>
    StrategySignal Evaluate(StrategyContext context);
}
