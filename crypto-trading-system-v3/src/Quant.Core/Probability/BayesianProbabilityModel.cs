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

    /// <summary>
    /// Теневые доказательства по стратегиям: дробные выигрыши и проигрыши виртуальных
    /// сделок. Хранятся ОТДЕЛЬНО от реальной статистики и не попадают ни в риск-движок,
    /// ни в счётчик холодного старта, ни в веса стратегий.
    /// </summary>
    private readonly Dictionary<string, (double Wins, double Losses)> _evidence =
        new Dictionary<string, (double Wins, double Losses)>(StringComparer.Ordinal);
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

            // --- Level 0.5: теневые доказательства --------------------------------------
            //
            // Без этого уровня модель без истории возвращала ОДНО И ТО ЖЕ число при любом
            // сигнале, любом рынке и любой уверенности: (безубыточность + априор) / 2. Для
            // плана с R:R 1.88 это 0.368, ожидание после издержек −0.10R, требуемое +0.23R.
            // Ни один кандидат не мог пройти никогда, а история берётся только из сделок.
            //
            // Доказательство берётся из виртуальных сделок по тем же кандидатам, на том же
            // потоке, с теми же издержками. Вес ниже единицы: виртуальная сделка не знает
            // реального исполнения. Популяция отдельна от реальных сделок, поэтому правило
            // вложенности уровней её не касается — двойного счёта здесь нет.
            if (_evidence.TryGetValue(EvidenceKey(q.StrategyName), out (double Wins, double Losses) shadow) &&
                shadow.Wins + shadow.Losses > 0)
            {
                double weight = MathUtil.Clamp01(_config.ShadowEvidenceWeight);
                posterior = BetaBinomial.FromCounts(posterior.Mean, _config.PriorStrength,
                    shadow.Wins * weight, shadow.Losses * weight);
                basis = "evidence";
            }

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

    /// <summary>
    /// Принимает исход ВИРТУАЛЬНОЙ сделки как доказательство.
    ///
    /// Исход переводится в эквивалентную долю выигрыша q — такую, что бинарная ставка
    /// «+RR с вероятностью q, −L с вероятностью 1−q» имеет то же матожидание, что и
    /// фактический результат:
    ///
    ///     q = (R + L) / (RR + L)
    ///
    /// Цель — q = 1, стоп — q = 0, выход по времени — ровно столько, сколько он стоил.
    /// Так доказательство описывает ту же самую ставку, что и формула ожидания, которая
    /// потом его прочтёт. Считать выход по времени с +0.3R «выигрышем» значило бы платить
    /// за него в формуле как за +1.88R.
    ///
    /// R берётся ВАЛОВЫЙ, по ценам: издержки вычитает движок ожидания, и вычесть их здесь
    /// значило бы вычесть дважды.
    /// </summary>
    public void ObserveEvidence(TradeRecord virtualTrade)
    {
        if (virtualTrade == null || !virtualTrade.IsVirtual) return;

        double stop = Math.Abs(virtualTrade.EntryPrice - virtualTrade.InitialStopPrice);
        double reward = Math.Abs(virtualTrade.InitialTargetPrice - virtualTrade.EntryPrice);
        if (stop <= 0 || reward <= 0) return;

        double rr = reward / stop;
        double loss = Math.Max(1.0, _evConfig.AssumedLossR);

        double q;
        if (virtualTrade.ExitReason == ExitReason.StopLoss)
        {
            // Стоп — это проигрыш целиком, даже если виртуальная цена исполнения вышла
            // чуть лучше допущения о проскальзывании.
            q = 0;
        }
        else
        {
            double move = virtualTrade.Direction == Side.Long
                ? virtualTrade.ExitPrice - virtualTrade.EntryPrice
                : virtualTrade.EntryPrice - virtualTrade.ExitPrice;
            q = MathUtil.Clamp01(((move / stop) + loss) / (rr + loss));
        }

        // Затухание: рынок, сменивший характер, должен переубеждать модель за сотни
        // сделок, а не за годы.
        double keep = 1.0 - (1.0 / Math.Max(1.0, _config.EvidenceMemoryTrades));

        string key = EvidenceKey(virtualTrade.StrategyName);
        _evidence.TryGetValue(key, out (double Wins, double Losses) counts);
        _evidence[key] = ((counts.Wins * keep) + q, (counts.Losses * keep) + (1 - q));
    }

    /// <summary>Сколько виртуальных сделок накоплено как доказательство по стратегии.</summary>
    public double EvidenceTrades(string strategyName) =>
        _evidence.TryGetValue(EvidenceKey(strategyName), out (double Wins, double Losses) c) ? c.Wins + c.Losses : 0;

    /// <summary>Всего виртуальных сделок-доказательств по всем стратегиям.</summary>
    public double TotalEvidenceTrades
    {
        get
        {
            double total = 0;
            foreach (KeyValuePair<string, (double Wins, double Losses)> kv in _evidence) total += kv.Value.Wins + kv.Value.Losses;
            return total;
        }
    }

    private static string EvidenceKey(string strategyName) => "evidence|" + (strategyName ?? "unknown");

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
