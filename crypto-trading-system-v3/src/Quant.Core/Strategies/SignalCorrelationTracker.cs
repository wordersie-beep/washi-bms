using System;
using System.Collections.Generic;
using Quant.Core.Config;
using Quant.Core.Numerics;

namespace Quant.Core.Strategies;

/// <summary>
/// Measures how much strategies duplicate one another (spec sections 77-78).
///
/// The problem this solves is the central failure mode of any voting ensemble: three
/// strategies reading the same information and reaching the same conclusion feel like three
/// confirmations, so the system sizes up on what is really a single piece of evidence. That
/// is exactly backwards — the correlation means the ensemble is LESS diversified than its
/// strategy count suggests, not more confident.
///
/// Overlap is measured two ways, and the larger of the two is used:
///
///   * STRUCTURALLY, from the feature families each strategy declares. Available from the
///     first bar and immune to sampling noise.
///   * EMPIRICALLY, from the realised correlation of their signed confidences. Slower to
///     become meaningful but catches overlap the declarations missed.
/// </summary>
public sealed class SignalCorrelationTracker
{
    private readonly StrategyConfig _config;
    private readonly Dictionary<string, RollingWindow> _history = new Dictionary<string, RollingWindow>();
    private readonly Dictionary<string, IReadOnlyCollection<FeatureFamily>> _families =
        new Dictionary<string, IReadOnlyCollection<FeatureFamily>>();

    public SignalCorrelationTracker(StrategyConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public void Register(IStrategy strategy)
    {
        if (strategy == null) return;
        _families[strategy.Name] = strategy.Families;
        if (!_history.ContainsKey(strategy.Name))
        {
            _history[strategy.Name] = new RollingWindow(_config.SignalCorrelationWindow);
        }
    }

    /// <summary>Records this bar's signed confidence for every strategy that was evaluated.</summary>
    public void Observe(IReadOnlyList<StrategySignal> signals)
    {
        if (signals == null) return;
        for (int i = 0; i < signals.Count; i++)
        {
            StrategySignal s = signals[i];
            if (s == null || string.IsNullOrEmpty(s.StrategyName)) continue;
            if (!_history.TryGetValue(s.StrategyName, out RollingWindow w))
            {
                w = new RollingWindow(_config.SignalCorrelationWindow);
                _history[s.StrategyName] = w;
            }
            w.Add(s.SignedConfidence);
        }
    }

    /// <summary>
    /// Overlap between two strategies in 0..1, the larger of the structural and empirical
    /// estimates. Only same-direction empirical correlation counts: two strategies that
    /// reliably DISAGREE are genuinely independent information, not duplicated information.
    /// </summary>
    public double Overlap(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal)) return 1.0;

        double structural = StructuralOverlap(a, b);

        double empirical = 0;
        if (_history.TryGetValue(a, out RollingWindow wa) && _history.TryGetValue(b, out RollingWindow wb))
        {
            double correlation = wa.CorrelationWith(wb, _config.SignalCorrelationWindow, minSample: 30);
            empirical = Math.Max(0, correlation);
        }

        return MathUtil.Clamp01(Math.Max(structural, empirical));
    }

    /// <summary>Jaccard similarity of the two strategies' declared feature families.</summary>
    private double StructuralOverlap(string a, string b)
    {
        if (!_families.TryGetValue(a, out IReadOnlyCollection<FeatureFamily> fa) ||
            !_families.TryGetValue(b, out IReadOnlyCollection<FeatureFamily> fb))
        {
            // Unknown strategies are assumed to overlap substantially. Assuming independence
            // is the expensive mistake here; assuming duplication merely costs some size.
            return 0.5;
        }

        var union = new HashSet<FeatureFamily>(fa);
        int intersection = 0;
        foreach (FeatureFamily f in fb)
        {
            if (union.Contains(f)) intersection++;
            union.Add(f);
        }

        return union.Count == 0 ? 0 : (double)intersection / union.Count;
    }

    /// <summary>
    /// Effective independent-vote count for a set of agreeing strategies.
    ///
    /// Eight strategies that all read the same thing should count as roughly one; eight
    /// genuinely independent ones as eight. Computed by discounting each strategy by its
    /// maximum overlap with any strategy already counted, which is a deliberately
    /// conservative reading of independence.
    /// </summary>
    public double EffectiveVoteCount(IReadOnlyList<string> strategyNames)
    {
        if (strategyNames == null || strategyNames.Count == 0) return 0;
        if (strategyNames.Count == 1) return 1;

        double total = 0;
        for (int i = 0; i < strategyNames.Count; i++)
        {
            double maxOverlap = 0;
            for (int j = 0; j < i; j++)
            {
                maxOverlap = Math.Max(maxOverlap, Overlap(strategyNames[i], strategyNames[j]));
            }
            total += 1.0 - maxOverlap;
        }

        return Math.Max(1.0, total);
    }

    public void Reset()
    {
        foreach (KeyValuePair<string, RollingWindow> kv in _history) kv.Value.Clear();
    }
}
