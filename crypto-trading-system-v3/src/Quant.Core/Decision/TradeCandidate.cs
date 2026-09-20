using System;
using Quant.Core.Data;
using Quant.Core.Ev;
using Quant.Core.Exits;
using Quant.Core.Features;
using Quant.Core.Primitives;
using Quant.Core.Probability;
using Quant.Core.Regime;
using Quant.Core.Sizing;
using Quant.Core.Strategies;

namespace Quant.Core.Decision;

/// <summary>
/// Everything known about one potential trade, assembled as it passes through the gates.
///
/// Carried as a single object so that the rejection journal can record the FULL state at the
/// moment of refusal. A rejection reason without its context is almost useless later: what
/// makes the false-positive analysis possible is knowing not just that a trade was refused
/// for low edge, but what the edge was, what was required, and what every other reading said
/// at the time.
/// </summary>
public sealed class TradeCandidate
{
    public string SignalId { get; init; }
    public DateTime TimeUtc { get; init; }
    public string SymbolName { get; init; }
    public SymbolDataSet Data { get; init; }
    public FeatureVector Features { get; init; }
    public RegimeAssessment Regime { get; init; }
    public EnsembleDecision Ensemble { get; init; }
    public DataQualityReport DataQuality { get; init; }

    public Side Direction => Ensemble?.Direction ?? Side.None;
    public StrategySignal Leader => Ensemble?.Leader;

    // Filled in as the candidate progresses.
    public ExitPlan Exit { get; set; }
    public CostEstimate Cost { get; set; }
    public ProbabilityEstimate Probability { get; set; }
    public ExpectedValueResult ExpectedValue { get; set; }
    public SizingResult Sizing { get; set; }

    /// <summary>Composite ranking score; only meaningful once the candidate has cleared the gates.</summary>
    public double OpportunityScore { get; set; }

    /// <summary>
    /// Порог ясности режима, действующий В ЭТОТ МОМЕНТ на этом инструменте.
    ///
    /// Приходит от кандидата, а не из настроек, потому что он не константа: это перцентиль
    /// от собственного распределения классификатора, и у каждого инструмента оно своё.
    /// </summary>
    public double RegimeClarityThreshold { get; set; }

    public override string ToString() =>
        string.Format("{0} {1} {2} conf={3:P0}", SymbolName, Direction, Leader?.StrategyName ?? "-", Ensemble?.Confidence ?? 0);
}

/// <summary>The outcome of running a candidate through the gates.</summary>
public sealed class GateOutcome
{
    public static readonly GateOutcome Pass = new GateOutcome { Passed = true, Reason = NoTradeReason.None };

    public static GateOutcome Reject(NoTradeReason reason, string detail) =>
        new GateOutcome { Passed = false, Reason = reason, Detail = detail };

    public bool Passed { get; init; }
    public NoTradeReason Reason { get; init; }
    public string Detail { get; init; }

    public override string ToString() => Passed ? "pass" : $"{Reason}: {Detail}";
}
