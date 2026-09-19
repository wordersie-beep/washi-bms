using System;
using Quant.Core.Numerics;

namespace Quant.Core.Ev;

/// <summary>
/// The verdict on whether an idea is worth paying for (spec sections 17-19).
/// </summary>
public sealed class ExpectedValueResult
{
    public static ExpectedValueResult Reject(string reason) => new ExpectedValueResult
    {
        ExpectedValueR = double.NegativeInfinity,
        LowerBoundR = double.NegativeInfinity,
        RequiredEdgeR = 0,
        Rationale = reason,
    };

    /// <summary>Point estimate of expected value per trade, in R, net of all costs.</summary>
    public double ExpectedValueR { get; init; }

    /// <summary>
    /// Lower confidence bound on expected value. THIS is what the system trades on
    /// (spec section 19): acting on a point estimate means an edge that exists only inside
    /// the estimate's own error bars gets traded as though it were real.
    /// </summary>
    public double LowerBoundR { get; init; }

    /// <summary>Edge the trade had to clear, above zero, to be worth taking (spec section 18).</summary>
    public double RequiredEdgeR { get; init; }

    /// <summary>Expected win size in R, from the realistic target.</summary>
    public double ExpectedWinR { get; init; }

    /// <summary>Expected loss size in R, at or above 1.0 to allow for stop slippage.</summary>
    public double ExpectedLossR { get; init; }

    /// <summary>Round-trip cost in R.</summary>
    public double CostR { get; init; }

    /// <summary>Win probability used, after calibration.</summary>
    public double WinProbability { get; init; }

    /// <summary>Reward-to-risk at the target actually used, which may be below the one requested.</summary>
    public double EffectiveRewardToRisk { get; init; }

    /// <summary>True when the target was capped by the historical MFE distribution (spec section 44).</summary>
    public bool TargetCappedByHistory { get; init; }

    public string Rationale { get; init; }

    /// <summary>The single gate: the lower bound must clear the required edge.</summary>
    public bool IsAcceptable => MathUtil.IsFinite(LowerBoundR) && LowerBoundR >= RequiredEdgeR;

    /// <summary>How far past the required edge the trade sits. Feeds opportunity ranking.</summary>
    public double EdgeSurplusR => MathUtil.IsFinite(LowerBoundR) ? LowerBoundR - RequiredEdgeR : double.NegativeInfinity;

    public override string ToString() =>
        MathUtil.IsFinite(ExpectedValueR)
            ? string.Format("EV={0:F3}R (lower {1:F3}R, required {2:F3}R) p={3:P1} rr={4:F2} cost={5:F3}R -> {6}",
                ExpectedValueR, LowerBoundR, RequiredEdgeR, WinProbability, EffectiveRewardToRisk, CostR,
                IsAcceptable ? "ACCEPT" : "REJECT")
            : "REJECT: " + Rationale;
}
