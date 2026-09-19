using System;
using System.Collections.Generic;
using Quant.Core.Primitives;

namespace Quant.Core.Risk;

/// <summary>The current risk posture and everything that produced it.</summary>
public sealed class RiskAssessment
{
    public RiskState State { get; init; }

    /// <summary>Multiplier applied to base risk, 0..1. Zero means no new positions.</summary>
    public double RiskMultiplier { get; init; }

    /// <summary>Reasons for the current posture, for the journal and the dashboard.</summary>
    public IReadOnlyList<string> Reasons { get; init; }

    /// <summary>The binding constraint, when the posture is not Normal.</summary>
    public NoTradeReason BlockingReason { get; init; }

    public double Drawdown24hPercent { get; init; }
    public double Drawdown7dPercent { get; init; }
    public double Drawdown30dPercent { get; init; }
    public double DrawdownAllTimePercent { get; init; }
    public double DailyLossPercent { get; init; }
    public double WeeklyLossPercent { get; init; }
    public int ConsecutiveLosses { get; init; }
    public double RuinProbability { get; init; }
    public double ExecutionQuality { get; init; }
    public DateTime? CooldownUntilUtc { get; init; }

    /// <summary>True when the system may open new positions. Existing ones are always managed.</summary>
    public bool AllowsNewPositions => State != RiskState.Halt && RiskMultiplier > 0;

    public string ReasonSummary => Reasons == null || Reasons.Count == 0 ? "normal" : string.Join("; ", Reasons);

    public override string ToString() =>
        string.Format("{0} riskX={1:F2} dd24h={2:F2}% daily={3:F2}% losses={4} ruin={5:P2} [{6}]",
            State, RiskMultiplier, Drawdown24hPercent, DailyLossPercent, ConsecutiveLosses, RuinProbability, ReasonSummary);
}
