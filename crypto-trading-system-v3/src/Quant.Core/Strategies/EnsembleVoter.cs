using System;
using System.Collections.Generic;
using Quant.Core.Adaptation;
using Quant.Core.Config;
using Quant.Core.Numerics;
using Quant.Core.Primitives;

namespace Quant.Core.Strategies;

/// <summary>
/// Combines strategy votes into one ensemble decision (spec sections 14, 75-78).
///
/// The combination is a weighted sum of SIGNED confidences, which handles disagreement
/// correctly: two strategies pulling opposite ways cancel instead of both counting as
/// evidence. Two adjustments then apply before the result is usable:
///
///   1. The CORRELATION DISCOUNT. Agreement between strategies reading the same information
///      is not corroboration. The blended confidence is scaled by how many genuinely
///      independent votes are behind it, so three overlapping strategies do not produce the
///      conviction of three independent ones.
///
///   2. The DISSENT PENALTY. A decision carried 60/40 is materially less reliable than one
///      carried unanimously, and the system should size the two differently rather than
///      recording both as simply "long".
/// </summary>
public sealed class EnsembleVoter
{
    private readonly StrategyConfig _config;
    private readonly SignalCorrelationTracker _correlation;
    private readonly StrategyWeightEngine _weights;

    public EnsembleVoter(StrategyConfig config, SignalCorrelationTracker correlation, StrategyWeightEngine weights)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _correlation = correlation ?? throw new ArgumentNullException(nameof(correlation));
        _weights = weights ?? throw new ArgumentNullException(nameof(weights));
    }

    /// <summary>
    /// Evaluates every strategy and blends the result.
    ///
    /// <paramref name="collectDisabled"/> makes disabled strategies evaluate anyway so their
    /// signals can be tracked virtually for recovery (spec section 24). Those signals are
    /// excluded from the vote itself — a disabled strategy must not influence a live trade.
    /// </summary>
    public EnsembleDecision Vote(
        StrategyContext ctx,
        IReadOnlyList<IStrategy> strategies,
        double executionQuality,
        bool collectDisabled,
        out IReadOnlyList<StrategySignal> disabledSignals)
    {
        var collectedDisabled = new List<StrategySignal>();
        disabledSignals = collectedDisabled;

        if (ctx == null || strategies == null || strategies.Count == 0) return EnsembleDecision.Neutral;

        var allSignals = new List<StrategySignal>(strategies.Count);
        var rawWeights = new Dictionary<string, double>(StringComparer.Ordinal);
        var signalsByName = new Dictionary<string, StrategySignal>(StringComparer.Ordinal);

        foreach (IStrategy strategy in strategies)
        {
            StrategySignal signal = strategy.Evaluate(ctx);
            allSignals.Add(signal);
            signalsByName[strategy.Name] = signal;

            StrategyState state = _weights.StateOf(strategy.Name);
            if (!state.CanTrade)
            {
                if (collectDisabled && signal.IsActionable) collectedDisabled.Add(signal);
                continue;
            }

            if (!signal.IsActionable) continue;
            if (signal.Confidence < _config.MinStrategyConfidence) continue;

            double weight = _weights.WeightFor(strategy, ctx.Regime.Primary, ctx.SymbolName, executionQuality);
            if (weight > 0) rawWeights[strategy.Name] = weight;
        }

        // The correlation tracker sees every signal, including the neutral ones: a strategy
        // that is reliably silent when another fires is information about their overlap too.
        _correlation.Observe(allSignals);

        if (rawWeights.Count == 0)
        {
            return new EnsembleDecision
            {
                Direction = Side.None,
                Confidence = 0,
                Signals = allSignals,
                Agreeing = Array.Empty<StrategySignal>(),
                Dissenting = Array.Empty<StrategySignal>(),
                EffectiveVotes = 0,
                Rationale = "no strategy produced an actionable, sufficiently confident signal",
            };
        }

        IReadOnlyDictionary<string, double> normalized = _weights.Normalize(rawWeights);

        // --- Weighted signed sum --------------------------------------------------------
        double net = 0;
        double grossLong = 0;
        double grossShort = 0;

        foreach (KeyValuePair<string, double> kv in normalized)
        {
            StrategySignal s = signalsByName[kv.Key];
            double contribution = s.SignedConfidence * kv.Value;
            net += contribution;
            if (contribution > 0) grossLong += contribution;
            else grossShort += -contribution;
        }

        Side direction = net > 0 ? Side.Long : net < 0 ? Side.Short : Side.None;
        if (direction == Side.None)
        {
            return new EnsembleDecision
            {
                Direction = Side.None,
                Confidence = 0,
                Signals = allSignals,
                Agreeing = Array.Empty<StrategySignal>(),
                Dissenting = Array.Empty<StrategySignal>(),
                EffectiveVotes = 0,
                Weights = normalized,
                Rationale = "strategies exactly offset one another",
            };
        }

        // --- Split into agreement and dissent -------------------------------------------
        var agreeing = new List<StrategySignal>();
        var dissenting = new List<StrategySignal>();
        var agreeingNames = new List<string>();
        double agreeingWeight = 0;

        foreach (KeyValuePair<string, double> kv in normalized)
        {
            StrategySignal s = signalsByName[kv.Key];
            if (s.Direction == direction)
            {
                agreeing.Add(s);
                agreeingNames.Add(kv.Key);
                agreeingWeight += kv.Value;
            }
            else
            {
                dissenting.Add(s);
            }
        }

        agreeing.Sort((x, y) => y.Confidence.CompareTo(x.Confidence));

        // --- Blended confidence ----------------------------------------------------------
        // Weighted mean confidence across the agreeing strategies, so that adding a weak
        // fifth vote cannot raise conviction above what the strong four already justified.
        double weightedConfidence = 0;
        for (int i = 0; i < agreeing.Count; i++)
        {
            double w = normalized[agreeing[i].StrategyName];
            weightedConfidence += agreeing[i].Confidence * w;
        }
        weightedConfidence = MathUtil.SafeDiv(weightedConfidence, agreeingWeight);

        // --- Correlation discount ---------------------------------------------------------
        double effectiveVotes = _correlation.EffectiveVoteCount(agreeingNames);
        double independenceBonus = MathUtil.LinearScale(effectiveVotes, 1.0, 3.0);

        // One independent vote keeps 80% of its confidence; three or more keep all of it.
        // Overlapping votes converge on the single-vote case, which is what they are.
        double correlationFactor = 0.80 + (0.20 * independenceBonus);

        // --- Dissent penalty --------------------------------------------------------------
        double totalGross = grossLong + grossShort;
        double consensus = MathUtil.SafeDiv(Math.Abs(net), totalGross, 1.0);
        double dissentFactor = 0.55 + (0.45 * MathUtil.Clamp01(consensus));

        double confidence = MathUtil.Clamp01(weightedConfidence * correlationFactor * dissentFactor);

        // The leader owns the exit plan: it is the strategy whose reasoning the trade is
        // actually expressing, so its invalidation level is the one that means something.
        StrategySignal leader = null;
        double bestWeightedConfidence = -1;
        for (int i = 0; i < agreeing.Count; i++)
        {
            double score = agreeing[i].Confidence * normalized[agreeing[i].StrategyName];
            if (score > bestWeightedConfidence) { bestWeightedConfidence = score; leader = agreeing[i]; }
        }

        return new EnsembleDecision
        {
            Direction = direction,
            Confidence = confidence,
            Signals = allSignals,
            Agreeing = agreeing,
            Dissenting = dissenting,
            EffectiveVotes = effectiveVotes,
            Leader = leader,
            Weights = normalized,
            Rationale = string.Format(
                "{0} agreeing ({1:F1} effective), consensus {2:P0}, blended {3:P0} -> {4:P0}",
                agreeing.Count, effectiveVotes, consensus, weightedConfidence, confidence),
        };
    }
}
