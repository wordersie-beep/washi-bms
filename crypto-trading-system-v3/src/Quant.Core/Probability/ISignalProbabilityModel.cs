using Quant.Core.Features;
using Quant.Core.Primitives;
using Quant.Core.Strategies;

namespace Quant.Core.Probability;

/// <summary>
/// Inputs to a probability query. Kept as an explicit type so that a future learned model
/// receives exactly the same information as the statistical one, from the same call site.
/// </summary>
public sealed class ProbabilityQuery
{
    public string StrategyName { get; init; }
    public string SymbolName { get; init; }
    public MarketRegime Regime { get; init; }
    public Side Direction { get; init; }
    public double EnsembleConfidence { get; init; }
    public double RewardToRisk { get; init; }
    public SessionKind Session { get; init; }
    public bool IsWeekend { get; init; }
    public VolatilityBucket VolatilityBucket { get; init; }
    public FeatureVector Features { get; init; }
    public EnsembleDecision Ensemble { get; init; }
}

/// <summary>
/// Pluggable estimator of "will this trade reach its target before its stop, after costs"
/// (spec sections 80, 83).
///
/// Note the target: not "will price go up". Direction accuracy is not what a trade is paid
/// on — a strategy can be right about direction most of the time and still lose money
/// because the stop is hit on the way. Modelling the tradeable event directly is what makes
/// the output usable by the expected-value engine without a further layer of guesswork.
/// </summary>
public interface ISignalProbabilityModel
{
    string Name { get; }
    bool IsReady { get; }

    /// <summary>
    /// Estimates the win probability. Must never throw; on failure it returns
    /// <see cref="ProbabilityEstimate.Unavailable"/>, whose zero confidence causes the
    /// expected-value engine to refuse the trade.
    /// </summary>
    ProbabilityEstimate Estimate(ProbabilityQuery query);

    /// <summary>Folds a completed trade back into the model.</summary>
    void Observe(Stats.TradeRecord trade);

    void Reset();
}
