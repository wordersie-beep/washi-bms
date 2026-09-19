using System;
using Quant.Core.Numerics;
using Quant.Core.Primitives;

namespace Quant.Core.Strategies;

/// <summary>
/// One strategy's opinion (spec section 14).
///
/// The vote is NOT binary. A strategy that can only say "buy" forces the ensemble to treat
/// a marginal setup and a textbook one identically, which throws away most of the
/// information the strategy actually has.
/// </summary>
public sealed class StrategySignal
{
    public static StrategySignal Neutral(string strategyName, string reason) => new StrategySignal
    {
        StrategyName = strategyName,
        Direction = Side.None,
        Confidence = 0,
        Rationale = reason,
    };

    public string StrategyName { get; init; }
    public StrategyKind Kind { get; init; }
    public Side Direction { get; init; }

    /// <summary>
    /// Strength of this strategy's own conviction, 0..1, BEFORE any ensemble weighting,
    /// regime fit or historical performance is applied. Those adjustments belong to the
    /// ensemble, not to the strategy: a strategy that grades itself on its own track record
    /// double-counts that record once the ensemble applies it too.
    /// </summary>
    public double Confidence { get; init; }

    /// <summary>
    /// The price level whose breach would prove the idea wrong, if the strategy has an
    /// opinion. Null hands stop placement entirely to the exit engine.
    /// </summary>
    public double? InvalidationPrice { get; init; }

    /// <summary>
    /// Preferred stop distance in ATR, if the strategy has an opinion. The exit engine
    /// treats this as advice and may widen or reject it.
    /// </summary>
    public double? PreferredStopInAtr { get; init; }

    /// <summary>Preferred first target in R, if the strategy has an opinion.</summary>
    public double? PreferredTargetR { get; init; }

    /// <summary>
    /// The price the signal is anchored to — a breakout level, a pullback level, the close.
    /// Used by the chase filter to measure how far price has already run (spec section 51).
    /// </summary>
    public double AnchorPrice { get; init; }

    /// <summary>Human-readable explanation, carried into the decision journal.</summary>
    public string Rationale { get; init; }

    public bool IsActionable => Direction != Side.None && Confidence > 0;

    /// <summary>Signed confidence: positive long, negative short. The form the ensemble sums.</summary>
    public double SignedConfidence => Direction == Side.Long ? Confidence : Direction == Side.Short ? -Confidence : 0;

    public override string ToString() =>
        IsActionable
            ? string.Format("{0}: {1} conf={2:P0} ({3})", StrategyName, Direction, Confidence, Rationale)
            : string.Format("{0}: neutral ({1})", StrategyName, Rationale);
}
