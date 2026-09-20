using System;
using System.Collections.Generic;
using Quant.Core.Config;
using Quant.Core.Numerics;
using Quant.Core.Primitives;
using Quant.Core.Stats;
using Quant.Core.Strategies;

namespace Quant.Core.Adaptation;

/// <summary>
/// Computes each strategy's ensemble weight (spec sections 74-76) and manages the
/// degrade / disable / shadow / recover lifecycle (spec sections 23-24).
///
/// Weight is built from five factors, multiplied:
///
///     base x regime fit x long-term performance x recent performance x execution quality
///
/// Multiplicatively rather than additively, so that a strategy which is a poor fit for the
/// current regime cannot compensate with a good track record — a mean-reversion book with
/// superb historical numbers is still the wrong instrument in a trend, and an additive
/// model would let its history vote it into the trade anyway.
///
/// Two structural safeguards apply afterwards:
///   * a HARD CAP on any single strategy's share (spec section 76), so one lucky performer
///     cannot become the entire system;
///   * a FLOOR for active strategies, so an ordinary slump cannot silently zero a strategy
///     and remove it from the ensemble without that ever being a visible decision.
/// </summary>
public sealed class StrategyWeightEngine
{
    private readonly AdaptationConfig _config;
    private readonly StrategyConfig _strategyConfig;
    private readonly PerformanceStore _performance;
    private readonly Dictionary<string, StrategyState> _states = new Dictionary<string, StrategyState>(StringComparer.Ordinal);

    public StrategyWeightEngine(AdaptationConfig config, StrategyConfig strategyConfig, PerformanceStore performance)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _strategyConfig = strategyConfig ?? throw new ArgumentNullException(nameof(strategyConfig));
        _performance = performance ?? throw new ArgumentNullException(nameof(performance));
    }

    public IReadOnlyDictionary<string, StrategyState> States => _states;

    public StrategyState StateOf(string strategyName)
    {
        if (!_states.TryGetValue(strategyName, out StrategyState state))
        {
            state = new StrategyState { Name = strategyName };
            _states[strategyName] = state;
        }
        return state;
    }

    public void Register(IStrategy strategy)
    {
        if (strategy != null) StateOf(strategy.Name);
    }

    /// <summary>
    /// Weight for one strategy in the current regime, 0..1.
    /// Returns 0 for a disabled strategy, which is what removes it from the vote.
    /// </summary>
    public double WeightFor(IStrategy strategy, MarketRegime regime, string symbol, double executionQuality)
    {
        if (strategy == null) return 0;

        StrategyState state = StateOf(strategy.Name);
        if (!state.CanTrade) return 0;

        // A RECOVERING strategy is weighted on its declared fit and its shadow record, not
        // on the historical record that got it disabled.
        //
        // Weighting it on that history would make recovery unreachable: the strategy needs
        // to trade to rebuild its record, but the old record keeps its weight at zero so it
        // never trades. That deadlock turns "temporarily disabled" into "permanently dead"
        // and removes the recovery path the design depends on. The small weight it does get
        // is the point -- it is being given a chance to prove itself with real but minimal
        // capital at stake.
        if (state.Status == StrategyStatus.Recovering)
        {
            double declaredFit = strategy is StrategyBase sb ? sb.FitFor(regime) : 1.0;
            if (declaredFit <= 0) return 0;

            double shadowFactor = ShadowFactor(strategy.Name);
            double recovering = declaredFit * shadowFactor * _config.RecoveryWeightFraction;

            state.Weight = MathUtil.Clamp(recovering, _strategyConfig.MinActiveStrategyWeight, _strategyConfig.MaxSingleStrategyWeight);
            return state.Weight;
        }

        double regimeFit = RegimeFitFor(strategy, regime, symbol);
        if (regimeFit <= 0) return 0;

        (SegmentStats stats, double trust) = _performance.BestAvailable(
            strategy.Name, regime, symbol, _config.MinTradesForWeighting);

        double longTerm = LongTermFactor(stats, trust);
        double recent = RecentFactor(stats);
        double execution = MathUtil.Clamp(executionQuality, 0.3, 1.0);

        double weight = regimeFit * longTerm * recent * execution;

        if (state.Status == StrategyStatus.Degraded) weight *= 0.5;

        state.Weight = MathUtil.Clamp01(weight);
        return state.Weight;
    }

    /// <summary>
    /// Performance factor for a recovering strategy, drawn from the shadow record that
    /// earned it the recovery. Bounded to 0.5..1.0 so a strong shadow record shortens the
    /// probation but never skips it.
    /// </summary>
    private double ShadowFactor(string strategyName)
    {
        SegmentStats shadow = _performance.Get(PerformanceStore.ShadowKey(strategyName));
        if (shadow.TotalTrades < _config.ShadowTradesForRecovery) return 0.5;
        return MathUtil.Clamp(0.5 + (shadow.RecentExpectancyR * 1.0), 0.5, 1.0);
    }

    /// <summary>
    /// Regime suitability, blending the strategy's declared prior with its realised
    /// per-regime performance once enough trades exist (spec section 74).
    ///
    /// The declared fit is a hypothesis; the realised numbers are evidence. The blend shifts
    /// from one to the other as evidence accumulates, rather than switching at a threshold.
    /// </summary>
    private double RegimeFitFor(IStrategy strategy, MarketRegime regime, string symbol)
    {
        double declared = strategy is StrategyBase b ? b.FitFor(regime) : 1.0;
        if (declared <= 0) return 0;

        SegmentStats inRegime = _performance.Get(PerformanceStore.StrategyRegimeKey(strategy.Name, regime));
        if (inRegime.TotalTrades < _config.MinTradesForWeighting) return declared;

        // Empirical fit from expectancy: 0 R maps to 0.5, clearly positive to 1.
        double empirical = MathUtil.Clamp01(0.5 + (inRegime.ExpectancyR * 1.5));

        double evidenceWeight = MathUtil.LinearScale(
            inRegime.TotalTrades, _config.MinTradesForWeighting, _config.MinTradesForWeighting * 4);

        return MathUtil.Clamp01((declared * (1 - evidenceWeight)) + (empirical * evidenceWeight));
    }

    /// <summary>
    /// Long-term performance factor, 0.2..1.2, shrunk toward neutral by how much the slice
    /// can be trusted.
    /// </summary>
    private double LongTermFactor(SegmentStats stats, double trust) =>
        LongTermPerformanceFactor(stats, trust, _config);

    /// <summary>
    /// Чистая функция — вынесена ради проверяемости: иначе её поведение проверяется только
    /// через итоговый вес, где его перебивают соседние множители, и тест начинает измерять
    /// не то, что заявляет.
    /// </summary>
    public static double LongTermPerformanceFactor(SegmentStats stats, double trust, AdaptationConfig config)
    {
        if (stats.TotalTrades < config.MinTradesForWeighting) return 1.0;

        double expectancyScore = MathUtil.Clamp(0.5 + (stats.ExpectancyR * 2.0), 0.2, 1.2);
        double profitFactorScore = MathUtil.Clamp(stats.ProfitFactor / 1.3, 0.2, 1.2);

        // Разброс учитывается отдельно от среднего. Две стратегии с одинаковым ожиданием
        // +0.1R — это разные вещи, если одна даёт его ровно, а вторая чередует +2R и −1.8R:
        // вторая уводит счёт в просадку, из которой первая не выходила бы вовсе.
        //
        // Штрафуется ТОЛЬКО нижний разброс (мера в духе Сортино): большие выигрыши — это
        // не риск, и наказывать за них значило бы предпочитать стратегию, которая срезает
        // прибыль, той, которая её берёт.
        double stabilityScore = MathUtil.Clamp(
            0.5 + (stats.DownsideAdjustedExpectancy * 0.5), 0.2, 1.2);

        double raw = (expectancyScore * 0.45) + (profitFactorScore * 0.30) + (stabilityScore * 0.25);

        // Shrink toward 1.0 (no opinion) in proportion to how little the slice is trusted.
        return MathUtil.Clamp(1.0 + ((raw - 1.0) * MathUtil.Clamp01(trust)), 0.2, 1.2);
    }

    /// <summary>
    /// Recent performance factor, 0.3..1.15, scaled by the effective sample size behind the
    /// recent estimate. This is the guard against a handful of trades swinging the weight:
    /// the adjustment is proportional to how much evidence is actually behind it.
    /// </summary>
    private double RecentFactor(SegmentStats stats) => RecentPerformanceFactor(stats);

    /// <summary>
    /// Фактор недавней результативности, 0.3..1.15.
    ///
    /// Поправка на доказательность сохранена: горстка сделок не должна двигать вес.
    /// К ней добавлена поправка на разброс, и только к НАГРАДЕ, не к штрафу.
    ///
    /// Причина та же, что и в долгосрочном факторе: недавняя полоса +2.1R/−1.9R при том же
    /// среднем, что и +0.6R/−0.4R, — это не лучший результат, а более шумный. Повышать за
    /// неё вес значит наращивать размер позиции ровно там, где просадка вероятнее.
    /// Штраф не смягчается сознательно: плохой результат остаётся плохим независимо от того,
    /// каким ровным он был.
    /// </summary>
    public static double RecentPerformanceFactor(SegmentStats stats)
    {
        double effective = stats.RecentEffectiveSample;
        if (effective < 5) return 1.0;

        double raw = MathUtil.Clamp(0.5 + (stats.RecentExpectancyR * 2.0), 0.3, 1.15);
        double evidence = MathUtil.LinearScale(effective, 5, 30);
        double adjustment = (raw - 1.0) * evidence;

        if (adjustment > 0)
        {
            adjustment *= MathUtil.LinearScale(stats.DownsideAdjustedExpectancy, 0, 0.30);
        }

        return MathUtil.Clamp(1.0 + adjustment, 0.3, 1.15);
    }

    /// <summary>
    /// Normalises weights across the ensemble and enforces the cap and floor
    /// (spec section 76). Returns the normalised map.
    /// </summary>
    public IReadOnlyDictionary<string, double> Normalize(IReadOnlyDictionary<string, double> rawWeights)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        if (rawWeights == null || rawWeights.Count == 0) return result;

        double total = 0;
        foreach (KeyValuePair<string, double> kv in rawWeights)
        {
            if (kv.Value > 0) total += kv.Value;
        }
        if (total <= MathUtil.Epsilon) return result;

        // First pass: normalise and apply the cap, collecting the excess.
        double excess = 0;
        double uncappedTotal = 0;
        foreach (KeyValuePair<string, double> kv in rawWeights)
        {
            if (kv.Value <= 0) continue;

            double share = kv.Value / total;
            if (share > _strategyConfig.MaxSingleStrategyWeight)
            {
                excess += share - _strategyConfig.MaxSingleStrategyWeight;
                result[kv.Key] = _strategyConfig.MaxSingleStrategyWeight;
            }
            else
            {
                result[kv.Key] = share;
                uncappedTotal += share;
            }
        }

        // Second pass: redistribute the excess across the strategies that were not capped,
        // proportionally to what they already had.
        if (excess > MathUtil.Epsilon && uncappedTotal > MathUtil.Epsilon)
        {
            var keys = new List<string>(result.Keys);
            foreach (string key in keys)
            {
                if (result[key] >= _strategyConfig.MaxSingleStrategyWeight) continue;
                double bonus = excess * (result[key] / uncappedTotal);
                result[key] = Math.Min(_strategyConfig.MaxSingleStrategyWeight, result[key] + bonus);
            }
        }

        return result;
    }

    /// <summary>
    /// Runs the degradation and recovery review (spec sections 23-24).
    ///
    /// Deliberately periodic rather than per trade: reacting to every individual outcome is
    /// how a system ends up chasing noise, disabling a strategy on a bad Tuesday and
    /// re-enabling it on a good Thursday.
    /// </summary>
    public IReadOnlyList<string> Review(DateTime nowUtc)
    {
        var changes = new List<string>();

        foreach (KeyValuePair<string, StrategyState> kv in _states)
        {
            StrategyState state = kv.Value;
            SegmentStats stats = _performance.Get(PerformanceStore.StrategyKey(state.Name));

            switch (state.Status)
            {
                case StrategyStatus.Active:
                case StrategyStatus.Recovering:
                    ReviewActive(nowUtc, state, stats, changes);
                    break;

                case StrategyStatus.Degraded:
                    ReviewDegraded(nowUtc, state, stats, changes);
                    break;

                case StrategyStatus.Disabled:
                    ReviewDisabled(nowUtc, state, changes);
                    break;
            }
        }

        return changes;
    }

    private void ReviewActive(DateTime nowUtc, StrategyState state, SegmentStats stats, List<string> changes)
    {
        if (stats.TotalTrades < _config.MinTradesForWeighting) return;

        bool degraded =
            stats.RecentExpectancyR < _config.DegradedExpectancyR ||
            stats.ProfitFactor < _config.DegradedProfitFactor ||
            stats.ExpectancyDeteriorating;

        if (!degraded)
        {
            // A recovering strategy that has performed graduates back to fully active.
            if (state.Status == StrategyStatus.Recovering &&
                state.RecoveringSinceUtc.HasValue &&
                stats.RecentExpectancyR > 0 &&
                stats.RecentEffectiveSample >= _config.MinTradesForWeighting)
            {
                state.Status = StrategyStatus.Active;
                state.RecoveringSinceUtc = null;
                state.LastStatusReason = $"recovered: recent expectancy {stats.RecentExpectancyR:F3}R over {stats.RecentEffectiveSample:F0} effective trades";
                changes.Add($"{state.Name} -> Active ({state.LastStatusReason})");
            }
            return;
        }

        state.Status = StrategyStatus.Degraded;
        state.LastStatusReason =
            $"degraded: recent {stats.RecentExpectancyR:F3}R, pf {stats.ProfitFactor:F2}" +
            (stats.ExpectancyDeteriorating ? $", CUSUM severity {stats.DeteriorationSeverity:F1}" : "");
        changes.Add($"{state.Name} -> Degraded ({state.LastStatusReason})");
    }

    private void ReviewDegraded(DateTime nowUtc, StrategyState state, SegmentStats stats, List<string> changes)
    {
        // Recovered on its own: back to active.
        if (stats.RecentExpectancyR > 0 && stats.ProfitFactor >= 1.0 && !stats.ExpectancyDeteriorating)
        {
            state.Status = StrategyStatus.Active;
            state.LastStatusReason = $"degradation cleared: recent {stats.RecentExpectancyR:F3}R, pf {stats.ProfitFactor:F2}";
            changes.Add($"{state.Name} -> Active ({state.LastStatusReason})");
            return;
        }

        // Disabling requires a sample large enough for the deterioration to be meaningful,
        // plus evidence that is statistically meaningful rather than a soft patch. Disabling
        // on a small sample is how a system talks itself out of a working strategy.
        //
        // Two independent routes qualify, because they catch different failures:
        //   * the CUSUM change detector, which catches an edge that USED to exist and has
        //     stopped working;
        //   * a significantly negative expectancy, which catches an edge that never existed
        //     -- a change detector sees no change in a strategy that was always bad.
        if (stats.TotalTrades < _config.MinTradesForDisable) return;

        bool shiftDetected = stats.ExpectancyDeteriorating;
        bool significantlyNegative = stats.ExpectancyTStatistic < -2.0;
        if (!shiftDetected && !significantlyNegative) return;

        state.Status = StrategyStatus.Disabled;
        state.DisabledAtUtc = nowUtc;
        state.DisableCount++;
        state.ShadowTrades = 0;
        state.Weight = 0;
        state.LastStatusReason =
            $"disabled after {stats.TotalTrades} trades: expectancy {stats.ExpectancyR:F3}R (t={stats.ExpectancyTStatistic:F1}), " +
            $"recent {stats.RecentExpectancyR:F3}R" +
            (shiftDetected ? $", CUSUM severity {stats.DeteriorationSeverity:F1}" : "");
        changes.Add($"{state.Name} -> Disabled ({state.LastStatusReason})");
    }

    private void ReviewDisabled(DateTime nowUtc, StrategyState state, List<string> changes)
    {
        if (!state.DisabledAtUtc.HasValue) return;

        // A disabled strategy keeps generating signals, which are tracked virtually. This is
        // what allows a strategy that was disabled during an unfavourable stretch to earn
        // its place back rather than being lost permanently to one bad month.
        SegmentStats shadow = _performance.Get(PerformanceStore.ShadowKey(state.Name));

        double hoursDisabled = (nowUtc - state.DisabledAtUtc.Value).TotalHours;
        if (hoursDisabled < _config.ShadowHoursBeforeRecovery) return;
        if (shadow.TotalTrades < _config.ShadowTradesForRecovery) return;

        // Recovery demands a clearly positive shadow record, not merely a non-negative one,
        // and the bar rises with each previous failure.
        double requiredExpectancy = 0.05 * Math.Max(1, state.DisableCount);
        if (shadow.RecentExpectancyR <= requiredExpectancy || shadow.ProfitFactor < _config.ShadowRecoveryProfitFactor) return;

        state.Status = StrategyStatus.Recovering;
        state.RecoveringSinceUtc = nowUtc;
        state.LastStatusReason =
            $"shadow recovery: {shadow.TotalTrades} virtual trades at {shadow.RecentExpectancyR:F3}R (required > {requiredExpectancy:F3}R), pf {shadow.ProfitFactor:F2}";
        changes.Add($"{state.Name} -> Recovering ({state.LastStatusReason})");
    }

    /// <summary>Records that a disabled strategy produced a virtual trade.</summary>
    public void NoteShadowTrade(string strategyName)
    {
        StrategyState state = StateOf(strategyName);
        if (state.Status == StrategyStatus.Disabled) state.ShadowTrades++;
    }

    public void Reset() => _states.Clear();
}
