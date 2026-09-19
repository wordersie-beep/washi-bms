using System;
using System.Collections.Generic;
using Quant.Core.Numerics;
using Quant.Core.Primitives;

namespace Quant.Core.Regime;

/// <summary>
/// The regime read for one symbol at one bar (spec sections 7-9).
///
/// Deliberately NOT a bare label. A label alone invites the system to act with equal
/// conviction on an obvious trend and on a coin-flip, which is precisely the mistake that
/// makes regime-switching systems worse than the strategies they switch between.
/// </summary>
public sealed class RegimeAssessment
{
    public static readonly RegimeAssessment Unknown = new RegimeAssessment
    {
        Primary = MarketRegime.Unknown,
        Runner = MarketRegime.Unknown,
        Confidence = 0,
        Scores = new Dictionary<MarketRegime, double>(),
        RiskMultiplier = 0,
        BarsInRegime = 0,
    };

    public MarketRegime Primary { get; init; }

    /// <summary>Second-placed regime. The gap to it is what makes the confidence meaningful.</summary>
    public MarketRegime Runner { get; init; }

    /// <summary>Previous confirmed regime, for transition detection.</summary>
    public MarketRegime Previous { get; init; }

    /// <summary>
    /// 0..1. Built from the margin between the top two regime scores and how long the
    /// current read has held, not from the winning score alone.
    /// </summary>
    public double Confidence { get; init; }

    public IReadOnlyDictionary<MarketRegime, double> Scores { get; init; }

    /// <summary>True while a regime change is still within its confirmation window.</summary>
    public bool IsTransitioning { get; init; }

    /// <summary>Bars the current regime has held.</summary>
    public int BarsInRegime { get; init; }

    /// <summary>
    /// Risk multiplier the regime read implies, 0..1. Combines confidence with the
    /// transition penalty (spec section 9).
    /// </summary>
    public double RiskMultiplier { get; init; }

    /// <summary>
    /// Volatility state on its own axis, -1 (very quiet) to +1 (extreme), reported
    /// independently of <see cref="Primary"/>.
    ///
    /// Structure and volatility are not alternatives. A market can be trending strongly AND
    /// be highly volatile; forcing a single label to carry both means one of the two facts
    /// is always lost, and it is usually the one position sizing needed.
    /// </summary>
    public double VolatilityState { get; init; }

    /// <summary>True when volatility is elevated, whatever the structural label says.</summary>
    public bool IsHighVolatility => VolatilityState > 0.5;

    /// <summary>True when volatility is compressed, whatever the structural label says.</summary>
    public bool IsLowVolatility => VolatilityState < -0.5;

    /// <summary>Directional bias implied by the regime; None for non-directional regimes.</summary>
    public Side DirectionalBias =>
        Primary == MarketRegime.TrendUp ? Side.Long :
        Primary == MarketRegime.TrendDown ? Side.Short : Side.None;

    /// <summary>
    /// Regimes in which the system should not be opening anything at all, whatever any
    /// individual strategy thinks.
    /// </summary>
    public bool IsHostile =>
        Primary == MarketRegime.Panic ||
        Primary == MarketRegime.LiquidityStress;

    public double ScoreOf(MarketRegime regime) =>
        Scores != null && Scores.TryGetValue(regime, out double v) ? v : 0;

    /// <summary>Score of the closest trend regime, whichever direction. Useful for trend-family strategies.</summary>
    public double TrendScore => Math.Max(ScoreOf(MarketRegime.TrendUp), ScoreOf(MarketRegime.TrendDown));

    public override string ToString() =>
        string.Format("{0} conf={1:P0} (runner {2}) bars={3}{4} riskX={5:F2}",
            Primary, Confidence, Runner, BarsInRegime, IsTransitioning ? " TRANSITION" : "", RiskMultiplier);
}
