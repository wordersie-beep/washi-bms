using System;
using System.Collections.Generic;
using Quant.Core.Config;
using Quant.Core.Numerics;
using Quant.Core.Portfolio;

namespace Quant.Core.Decision;

/// <summary>
/// Ranks surviving candidates so the best risk-adjusted opportunities get the budget
/// (spec sections 30-31).
///
/// This exists because "five good signals" is not a reason to open five positions. Risk
/// budget is finite, and spending it on the first signals that happen to arrive rather than
/// on the best ones available is a pure, avoidable loss of expectancy. Sorting first and
/// allocating second costs nothing and is strictly better.
///
/// The score is intentionally not just expected value. A marginally higher edge on an
/// instrument that is expensive to trade, already correlated with the book, and read in a
/// murky regime is worse than a slightly smaller edge that is clean on all three counts.
/// </summary>
public sealed class OpportunityRanker
{
    private readonly PortfolioConfig _config;
    private readonly CorrelationEngine _correlation;

    public OpportunityRanker(PortfolioConfig config, CorrelationEngine correlation)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _correlation = correlation ?? throw new ArgumentNullException(nameof(correlation));
    }

    /// <summary>
    /// Scores a candidate in 0..1. Requires the expected-value result and the exit plan to
    /// have been computed.
    /// </summary>
    public double Score(TradeCandidate candidate, IReadOnlyList<OpenPosition> openPositions)
    {
        if (candidate?.ExpectedValue == null || candidate.Exit == null) return 0;

        // Edge, on the lower bound rather than the point estimate, saturating at 0.5R.
        double edge = MathUtil.LinearScale(candidate.ExpectedValue.LowerBoundR, 0, 0.5);

        // Statistical confidence in that edge.
        double confidence = MathUtil.Clamp01(candidate.Probability.Confidence);

        // Signal conviction, discounted for strategy overlap by the ensemble.
        double conviction = MathUtil.Clamp01(candidate.Ensemble.Confidence);

        // Independent corroboration.
        double independence = MathUtil.LinearScale(candidate.Ensemble.EffectiveVotes, 1.0, 3.0);

        // Reward-to-risk.
        double rewardToRisk = MathUtil.LinearScale(candidate.Exit.RewardToRisk, 1.0, 3.0);

        // Cost efficiency: what fraction of the risk is eaten before the trade does anything.
        double costEfficiency = 1.0 - MathUtil.LinearScale(candidate.ExpectedValue.CostR, 0.02, 0.30);

        // Regime clarity.
        double regimeQuality = MathUtil.Clamp01(candidate.Regime.Confidence);

        // Diversification: a candidate uncorrelated with what is already open is worth more
        // than an identical one that doubles an existing bet.
        double diversification = DiversificationScore(candidate, openPositions);

        // Data quality.
        double dataQuality = MathUtil.Clamp01(candidate.DataQuality.Score);

        // Weighted geometric mean, so a candidate that is very weak on any one dimension
        // cannot be rescued by being excellent on the others.
        return MathUtil.Clamp01(WeightedGeometricMean(
            (edge, 0.22),
            (confidence, 0.13),
            (conviction, 0.13),
            (independence, 0.08),
            (rewardToRisk, 0.12),
            (costEfficiency, 0.12),
            (regimeQuality, 0.10),
            (diversification, 0.06),
            (dataQuality, 0.04)));
    }

    /// <summary>
    /// 1 when the candidate is uncorrelated with everything open, falling toward 0 as it
    /// duplicates existing exposure. Same-direction correlation counts against; opposite
    /// direction is treated as neutral rather than as a bonus, because deliberately hedging
    /// for its own sake is not a source of edge (spec section 117).
    /// </summary>
    private double DiversificationScore(TradeCandidate candidate, IReadOnlyList<OpenPosition> openPositions)
    {
        if (openPositions == null || openPositions.Count == 0) return 1.0;

        double worst = 0;
        for (int i = 0; i < openPositions.Count; i++)
        {
            OpenPosition p = openPositions[i];
            if (p == null || p.IsVirtual) continue;

            double correlation = _correlation.Correlation(candidate.SymbolName, p.SymbolName);
            bool sameDirection = p.Direction == candidate.Direction;

            // Correlated and same way, or anti-correlated and opposite ways: both stack the
            // same underlying bet.
            double effective = sameDirection ? correlation : -correlation;
            if (effective > worst) worst = effective;
        }

        return MathUtil.Clamp01(1.0 - worst);
    }

    private static double WeightedGeometricMean(params (double Value, double Weight)[] terms)
    {
        double sumLog = 0;
        double sumWeight = 0;

        for (int i = 0; i < terms.Length; i++)
        {
            double v = MathUtil.Clamp(terms[i].Value, 0.01, 1.0);
            sumLog += terms[i].Weight * Math.Log(v);
            sumWeight += terms[i].Weight;
        }

        return sumWeight <= 0 ? 0 : Math.Exp(sumLog / sumWeight);
    }

    /// <summary>
    /// Scores and orders candidates, best first, keeping at most
    /// <see cref="PortfolioConfig.MaxCandidatesPerCycle"/>.
    /// </summary>
    public IReadOnlyList<TradeCandidate> Rank(IReadOnlyList<TradeCandidate> candidates, IReadOnlyList<OpenPosition> openPositions)
    {
        var scored = new List<TradeCandidate>(candidates?.Count ?? 0);
        if (candidates == null) return scored;

        for (int i = 0; i < candidates.Count; i++)
        {
            candidates[i].OpportunityScore = Score(candidates[i], openPositions);
            scored.Add(candidates[i]);
        }

        // Ties are broken by symbol name so the ordering is deterministic. Without that, two
        // equally-scored candidates could be picked differently between a backtest and a
        // live run, and the backtest would stop being a description of the live system.
        scored.Sort((a, b) =>
        {
            int byScore = b.OpportunityScore.CompareTo(a.OpportunityScore);
            return byScore != 0 ? byScore : string.CompareOrdinal(a.SymbolName, b.SymbolName);
        });

        if (scored.Count > _config.MaxCandidatesPerCycle)
        {
            scored.RemoveRange(_config.MaxCandidatesPerCycle, scored.Count - _config.MaxCandidatesPerCycle);
        }

        return scored;
    }
}
