using System;
using System.Collections.Generic;
using Quant.Core.Primitives;

namespace Quant.Core.Strategies;

/// <summary>
/// The strategy roster.
///
/// The baseline is registered but kept OUT of the live ensemble by default: its job is to be
/// the comparison arm (spec sections 155-156), and a baseline that is also a participant
/// cannot serve as a control.
/// </summary>
public sealed class StrategyRegistry
{
    private readonly List<IStrategy> _ensemble = new List<IStrategy>();

    public StrategyRegistry(bool includeBaselineInEnsemble = false)
    {
        _ensemble.Add(new TrendFollowingStrategy());
        _ensemble.Add(new MomentumStrategy());
        _ensemble.Add(new BreakoutStrategy());
        _ensemble.Add(new PullbackStrategy());
        _ensemble.Add(new MeanReversionStrategy());
        _ensemble.Add(new VolatilityExpansionStrategy());
        _ensemble.Add(new MarketStructureStrategy());
        _ensemble.Add(new ReversalStrategy());

        Baseline = new BaselineTrendStrategy();
        if (includeBaselineInEnsemble) _ensemble.Add(Baseline);
    }

    /// <summary>The strategies that vote.</summary>
    public IReadOnlyList<IStrategy> Ensemble => _ensemble;

    /// <summary>The comparison arm. Evaluated alongside but, by default, not part of the vote.</summary>
    public IStrategy Baseline { get; }

    /// <summary>Every strategy including the baseline, for registration and reporting.</summary>
    public IEnumerable<IStrategy> All
    {
        get
        {
            for (int i = 0; i < _ensemble.Count; i++) yield return _ensemble[i];
            if (!_ensemble.Contains(Baseline)) yield return Baseline;
        }
    }

    public IStrategy Find(string name)
    {
        for (int i = 0; i < _ensemble.Count; i++)
        {
            if (string.Equals(_ensemble[i].Name, name, StringComparison.Ordinal)) return _ensemble[i];
        }
        return string.Equals(Baseline.Name, name, StringComparison.Ordinal) ? Baseline : null;
    }

    /// <summary>
    /// Sanity check run at startup: every regime must have at least one strategy claiming
    /// competence in it, and no strategy may claim competence everywhere. A strategy with a
    /// non-zero fit in every regime has not been specialised, which the spec forbids and
    /// which in practice means it has no thesis at all.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        var tradeableRegimes = new[]
        {
            MarketRegime.TrendUp, MarketRegime.TrendDown, MarketRegime.Range,
            MarketRegime.Breakout, MarketRegime.HighVolatility, MarketRegime.LowVolatility,
        };

        foreach (MarketRegime regime in tradeableRegimes)
        {
            bool covered = false;
            for (int i = 0; i < _ensemble.Count; i++)
            {
                if (_ensemble[i] is StrategyBase b && b.FitFor(regime) >= 0.5) { covered = true; break; }
            }
            if (!covered) problems.Add($"No strategy claims competence in regime {regime}.");
        }

        for (int i = 0; i < _ensemble.Count; i++)
        {
            if (_ensemble[i] is not StrategyBase b) continue;

            int nonZero = 0;
            foreach (MarketRegime regime in Enum.GetValues(typeof(MarketRegime)))
            {
                if (b.FitFor(regime) > 0) nonZero++;
            }

            if (nonZero >= Enum.GetValues(typeof(MarketRegime)).Length)
            {
                problems.Add($"Strategy '{b.Name}' claims a non-zero fit in every regime; it is not specialised.");
            }
        }

        return problems;
    }
}
