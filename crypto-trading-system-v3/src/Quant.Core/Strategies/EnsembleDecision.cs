using System;
using System.Collections.Generic;
using Quant.Core.Primitives;

namespace Quant.Core.Strategies;

/// <summary>The ensemble's combined view for one symbol on one bar.</summary>
public sealed class EnsembleDecision
{
    public static readonly EnsembleDecision Neutral = new EnsembleDecision
    {
        Direction = Side.None,
        Confidence = 0,
        Signals = Array.Empty<StrategySignal>(),
        Agreeing = Array.Empty<StrategySignal>(),
        EffectiveVotes = 0,
        Rationale = "no actionable signal",
    };

    public Side Direction { get; init; }

    /// <summary>Blended confidence, 0..1, after weighting and the correlation discount.</summary>
    public double Confidence { get; init; }

    /// <summary>Every signal collected this bar, including the neutral ones.</summary>
    public IReadOnlyList<StrategySignal> Signals { get; init; }

    /// <summary>Signals agreeing with <see cref="Direction"/>, strongest first.</summary>
    public IReadOnlyList<StrategySignal> Agreeing { get; init; }

    /// <summary>Signals that argued the other way. Dissent is recorded, never hidden.</summary>
    public IReadOnlyList<StrategySignal> Dissenting { get; init; }

    /// <summary>
    /// Number of genuinely independent votes behind the decision, after discounting overlap.
    /// Always at most the raw agreeing count, and usually well below it.
    /// </summary>
    public double EffectiveVotes { get; init; }

    /// <summary>Raw agreeing count, kept alongside so the discount is visible in the journal.</summary>
    public int RawVotes => Agreeing?.Count ?? 0;

    /// <summary>The strategy contributing most weight, which owns the exit plan.</summary>
    public StrategySignal Leader { get; init; }

    /// <summary>Per-strategy weights used, for the journal.</summary>
    public IReadOnlyDictionary<string, double> Weights { get; init; }

    public string Rationale { get; init; }

    public bool IsActionable => Direction != Side.None && Confidence > 0 && Leader != null;

    public override string ToString() =>
        IsActionable
            ? string.Format("{0} conf={1:P0} votes={2}/{3} leader={4}", Direction, Confidence, EffectiveVotes.ToString("F1"), RawVotes, Leader.StrategyName)
            : "neutral: " + Rationale;
}
