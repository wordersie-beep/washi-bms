using System;
using System.Collections.Generic;
using Quant.Core.Config;
using Quant.Core.Numerics;
using Quant.Core.Primitives;
using Quant.Core.Stats;

namespace Quant.Core.Probability;

/// <summary>
/// The baseline probability model: hierarchical Bayesian shrinkage over the trade history
/// (spec sections 15, 19, 22).
///
/// The problem it solves is that the question worth asking is narrow — "how often does THIS
/// strategy, in THIS regime, on THIS symbol, at THIS confidence, reach its target?" — while
/// the data available to answer it is almost always thin. Taking a thin slice at face value
/// produces wild estimates; ignoring the slice and using the global rate throws away the
/// specificity that makes the estimate worth having.
///
/// The resolution is a hierarchy. Each level's posterior becomes the PRIOR for the level
/// below it, so a specific bucket starts from what its parent knows and moves away from that
/// only as far as its own evidence justifies:
///
///     global  ->  strategy  ->  strategy x regime  ->  strategy x regime x confidence
///
/// A bucket with three trades barely moves from its parent. A bucket with four hundred is
/// essentially its own estimate. Nothing has to be thresholded, and nothing is discarded.
/// </summary>
public sealed class BayesianProbabilityModel : ISignalProbabilityModel
{
    private readonly ProbabilityConfig _config;
    private readonly EvConfig _evConfig;
    private readonly PerformanceStore _performance;
    private readonly CalibrationTracker _calibration;

    /// <summary>Counts keyed by bucket, for levels the performance store does not slice on.</summary>
    private readonly Dictionary<string, (int Wins, int Losses)> _buckets =
        new Dictionary<string, (int, int)>(StringComparer.Ordinal);

    public BayesianProbabilityModel(ProbabilityConfig config, EvConfig evConfig, PerformanceStore performance, CalibrationTracker calibration)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _evConfig = evConfig ?? throw new ArgumentNullException(nameof(evConfig));
        _performance = performance ?? throw new ArgumentNullException(nameof(performance));
        _calibration = calibration ?? throw new ArgumentNullException(nameof(calibration));
    }

    public string Name => "bayesian-hierarchical-v3";

    /// <summary>
    /// Always ready. The prior is a complete answer on trade one — a deliberately pessimistic
    /// one — so the system never has to choose between "no estimate" and "a guess".
    /// </summary>
    public bool IsReady => true;

    public CalibrationTracker Calibration => _calibration;

    public ProbabilityEstimate Estimate(ProbabilityQuery q)
    {
        if (q == null) return ProbabilityEstimate.Unavailable;

        try
        {
            // --- Level 0: the prior ------------------------------------------------------
            // Anchored at the BREAK-EVEN win rate for the planned reward-to-risk, so an
            // untested setup starts with exactly no edge rather than an assumed one. This is
            // the single most important line in the model: it is what stops a brand-new
            // strategy from being handed a flattering probability it has not earned.
            double rr = q.RewardToRisk > 0 ? q.RewardToRisk : _evConfig.DefaultRewardToRisk;
            double breakEven = MathUtil.Clamp(1.0 / (1.0 + rr), 0.05, 0.95);
            double priorMean = MathUtil.Clamp((breakEven + _config.PriorWinRate) / 2.0, 0.05, 0.95);

            var posterior = new BetaBinomial(priorMean * _config.PriorStrength, (1 - priorMean) * _config.PriorStrength);
            string basis = "prior";

            // The levels are NESTED populations: every trade in a bucket is also in its
            // regime slice, its strategy slice and the global slice. A level is therefore
            // only descended into when it is strictly SMALLER than the level above it.
            //
            // Without that test the same trades are applied once per level -- four times for
            // a system whose entire history is one strategy in one regime -- and five wins
            // are read as twenty. The check costs nothing and removes the whole class of
            // double-counting error.
            double parentTotal = double.MaxValue;

            // --- Level 1: global ---------------------------------------------------------
            SegmentStats overall = _performance.Overall;
            if (overall.TotalTrades > 0)
            {
                posterior = Descend(posterior, overall.Wins, overall.Losses);
                parentTotal = overall.TotalTrades;
                basis = "global";
            }

            // --- Level 2: strategy -------------------------------------------------------
            SegmentStats byStrategy = _performance.Get(PerformanceStore.StrategyKey(q.StrategyName));
            if (byStrategy.TotalTrades > 0 && byStrategy.TotalTrades < parentTotal)
            {
                posterior = Descend(posterior, byStrategy.Wins, byStrategy.Losses);
                parentTotal = byStrategy.TotalTrades;
                basis = "strategy";
            }

            // --- Level 3: strategy x regime ----------------------------------------------
            SegmentStats byRegime = _performance.Get(PerformanceStore.StrategyRegimeKey(q.StrategyName, q.Regime));
            if (byRegime.TotalTrades > 0 && byRegime.TotalTrades < parentTotal)
            {
                posterior = Descend(posterior, byRegime.Wins, byRegime.Losses);
                parentTotal = byRegime.TotalTrades;
                basis = "strategy x regime";
            }

            // --- Level 4: strategy x regime x confidence ---------------------------------
            string bucket = BucketKey(q);
            if (_buckets.TryGetValue(bucket, out (int Wins, int Losses) counts))
            {
                int bucketTotal = counts.Wins + counts.Losses;
                if (bucketTotal > 0 && bucketTotal < parentTotal)
                {
                    posterior = Descend(posterior, counts.Wins, counts.Losses);
                    basis = "strategy x regime x confidence";
                }
            }

            // --- Calibration correction ---------------------------------------------------
            double calibrated = _calibration.Recalibrate(posterior.Mean, breakEven);

            // Recalibration moves the mean but must not be allowed to shrink the stated
            // uncertainty; if anything, a model known to be mis-calibrated is LESS certain
            // than its posterior suggests, so the correction's magnitude is added in.
            double standardError = Math.Sqrt(
                (posterior.StdDev * posterior.StdDev) +
                ((calibrated - posterior.Mean) * (calibrated - posterior.Mean)));

            return new ProbabilityEstimate(
                calibrated,
                standardError,
                posterior.Strength,
                _calibration.Quality(),
                basis,
                _config.MinSampleForBucket,
                _config.FullTrustSample);
        }
        catch (Exception)
        {
            // An estimator failure must read as "no information", whose zero confidence
            // causes the expected-value engine to refuse the trade.
            return ProbabilityEstimate.Unavailable;
        }
    }

    /// <summary>
    /// Uses the parent's posterior MEAN as the prior location for the child level, at a
    /// FIXED prior strength.
    ///
    /// The strength is fixed rather than inherited on purpose. Passing the parent's own
    /// accumulated strength down would mean a global level with thousands of trades supplied
    /// so heavy a prior that no specific bucket could ever move away from it -- the hierarchy
    /// would collapse into one global win rate wearing four different labels. A fixed
    /// strength keeps the pull toward the parent constant, so a child moves away from it in
    /// proportion to its own evidence, which is the entire point of partial pooling.
    /// </summary>
    private BetaBinomial Descend(BetaBinomial parent, int wins, int losses) =>
        BetaBinomial.FromCounts(parent.Mean, _config.PriorStrength, wins, losses);

    private static string BucketKey(ProbabilityQuery q) =>
        q.StrategyName + "|" + q.Regime + "|" + PerformanceStore.ConfidenceBucketKey(q.EnsembleConfidence);

    private static string BucketKey(TradeRecord t) =>
        t.StrategyName + "|" + t.Regime + "|" + PerformanceStore.ConfidenceBucketKey(t.EnsembleConfidence);

    public void Observe(TradeRecord trade)
    {
        if (trade == null || trade.IsVirtual) return;

        string key = BucketKey(trade);
        _buckets.TryGetValue(key, out (int Wins, int Losses) counts);
        _buckets[key] = trade.IsWin ? (counts.Wins + 1, counts.Losses) : (counts.Wins, counts.Losses + 1);

        // The calibration tracker is fed the probability the model STATED at entry, against
        // what actually happened. Feeding it a probability recomputed after the fact would
        // measure nothing.
        if (trade.EstimatedWinProbability > 0)
        {
            _calibration.Observe(trade.EstimatedWinProbability, trade.IsWin);
        }
    }

    public void Reset()
    {
        _buckets.Clear();
        _calibration.Reset();
    }
}
