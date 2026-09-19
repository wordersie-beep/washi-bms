using System;
using System.Text;
using Quant.Core.Primitives;

namespace Quant.Core.Journal;

/// <summary>
/// One decision, accepted or refused, rendered so a human can follow the reasoning
/// (spec sections 121-122).
///
/// The requirement this meets is that EVERY decision be explainable after the fact. A system
/// that cannot say why it took a trade cannot be debugged, cannot be trusted, and cannot be
/// improved — the only available response to a losing month is to change something at random
/// and hope.
/// </summary>
public sealed class DecisionRecord
{
    public DateTime TimeUtc { get; init; }
    public string SignalId { get; init; }
    public string SymbolName { get; init; }
    public Side Direction { get; init; }
    public bool Accepted { get; init; }

    public MarketRegime Regime { get; init; }
    public double RegimeConfidence { get; init; }
    public string StrategyName { get; init; }
    public double StrategyConfidence { get; init; }
    public double EnsembleConfidence { get; init; }
    public double EffectiveVotes { get; init; }
    public int RawVotes { get; init; }

    public double WinProbability { get; init; }
    public double ProbabilityConfidence { get; init; }
    public double RewardToRisk { get; init; }
    public double ExpectedValueR { get; init; }
    public double EdgeLowerBoundR { get; init; }
    public double RequiredEdgeR { get; init; }
    public double CostR { get; init; }

    public double StopInAtr { get; init; }
    public double SpreadPercentile { get; init; }
    public double AtrPercentile { get; init; }

    public double RiskPercent { get; init; }
    public double PortfolioHeatPercent { get; init; }
    public RiskState RiskState { get; init; }
    public double OpportunityScore { get; init; }

    // --- Rejection only ------------------------------------------------------------------
    public NoTradeReason RejectionReason { get; init; }
    public string RejectionDetail { get; init; }

    /// <summary>Renders the record in the block form the spec's examples use.</summary>
    public string Render()
    {
        var sb = new StringBuilder();

        sb.AppendLine($"{TimeUtc:yyyy-MM-dd HH:mm:ss}Z  {SymbolName}  {(Accepted ? Direction.ToString().ToUpperInvariant() : "NO TRADE")}");
        sb.AppendLine($"  Regime            = {Regime} (confidence {RegimeConfidence:P0})");
        sb.AppendLine($"  Strategy          = {StrategyName ?? "-"} (confidence {StrategyConfidence:P0})");
        sb.AppendLine($"  Ensemble          = {EnsembleConfidence:P0} from {EffectiveVotes:F1} independent of {RawVotes} votes");
        sb.AppendLine($"  P(win)            = {WinProbability:P1} (estimate confidence {ProbabilityConfidence:P0})");
        sb.AppendLine($"  Reward:risk       = {RewardToRisk:F2}   stop {StopInAtr:F2} ATR");
        sb.AppendLine($"  Expected value    = {ExpectedValueR:+0.000;-0.000;0.000}R   lower bound {EdgeLowerBoundR:+0.000;-0.000;0.000}R   required {RequiredEdgeR:F3}R");
        sb.AppendLine($"  Cost              = {CostR:F3}R   spread pct {SpreadPercentile:P0}   volatility pct {AtrPercentile:P0}");
        sb.AppendLine($"  Portfolio         = risk {RiskPercent:F3}%   heat {PortfolioHeatPercent:F2}%   posture {RiskState}");

        if (Accepted)
        {
            sb.AppendLine($"  Opportunity score = {OpportunityScore:F3}");
            sb.Append("  Decision          = ACCEPT");
        }
        else
        {
            sb.AppendLine($"  Reason            = {RejectionReason}");
            sb.Append($"  Detail            = {RejectionDetail}");
        }

        return sb.ToString();
    }

    /// <summary>A single-line form for high-frequency logging.</summary>
    public string RenderCompact() =>
        Accepted
            ? $"{TimeUtc:HH:mm:ss}Z ACCEPT {SymbolName} {Direction} {StrategyName} conf={EnsembleConfidence:P0} p={WinProbability:P0} ev={ExpectedValueR:+0.00;-0.00}R risk={RiskPercent:F2}%"
            : $"{TimeUtc:HH:mm:ss}Z SKIP   {SymbolName} {Direction} {RejectionReason} ({RejectionDetail})";

    public override string ToString() => RenderCompact();
}
