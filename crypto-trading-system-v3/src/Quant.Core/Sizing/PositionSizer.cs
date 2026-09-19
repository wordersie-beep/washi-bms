using System;
using System.Collections.Generic;
using Quant.Core.Config;
using Quant.Core.Numerics;
using Quant.Core.Primitives;

namespace Quant.Core.Sizing;

/// <summary>The sizing decision, with every factor that produced it.</summary>
public sealed class SizingResult
{
    public static SizingResult Rejected(NoTradeReason reason, string detail) => new SizingResult
    {
        VolumeInUnits = 0,
        RejectionReason = reason,
        Detail = detail,
    };

    public double VolumeInUnits { get; init; }
    public double RiskPercent { get; init; }
    public double RiskAmount { get; init; }
    public double StopDistance { get; init; }

    /// <summary>Every multiplier applied, in order, so a size is always explainable.</summary>
    public IReadOnlyDictionary<string, double> Factors { get; init; }

    public NoTradeReason RejectionReason { get; init; }
    public string Detail { get; init; }

    public bool IsTradeable => VolumeInUnits > 0 && RejectionReason == NoTradeReason.None;

    public override string ToString() =>
        IsTradeable
            ? string.Format("{0:F4} units, risking {1:F3}% ({2:F2})", VolumeInUnits, RiskPercent, RiskAmount)
            : string.Format("no size: {0} ({1})", RejectionReason, Detail);
}

/// <summary>
/// Turns a decision into a number of units (spec section 32).
///
/// Sizing is multiplicative from a base risk percentage, and every factor is bounded at or
/// below 1.0 with one narrowly capped exception. That is a deliberate structural property:
/// it means no combination of inputs, and no parameter set a user or an optimiser can
/// produce, is capable of sizing ABOVE the base risk by more than the single explicitly
/// capped win-streak term. A system that can only scale down cannot martingale
/// (spec section 40), and enforcing that in the shape of the calculation is far more
/// reliable than enforcing it in review.
///
/// The final size is then clamped by the hard per-trade cap, checked against free margin,
/// and snapped DOWN onto the broker's volume grid — never up, so rounding can never breach
/// a risk limit.
/// </summary>
public sealed class PositionSizer
{
    private readonly SizingConfig _config;
    private readonly RiskConfig _riskConfig;

    public PositionSizer(SizingConfig config, RiskConfig riskConfig)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _riskConfig = riskConfig ?? throw new ArgumentNullException(nameof(riskConfig));
    }

    /// <summary>
    /// Computes position size.
    /// </summary>
    /// <param name="spec">Instrument terms, for volume normalisation and margin.</param>
    /// <param name="account">Account state.</param>
    /// <param name="entryPrice">Intended entry.</param>
    /// <param name="stopPrice">Intended stop.</param>
    /// <param name="direction">Trade direction.</param>
    /// <param name="riskStateMultiplier">From the risk engine, 0..1.</param>
    /// <param name="regimeMultiplier">From the regime assessment, 0..1.</param>
    /// <param name="ensembleConfidence">Blended signal confidence, 0..1.</param>
    /// <param name="edgeSurplusR">How far past the required edge the trade sits, in R.</param>
    /// <param name="strategyWeight">The leading strategy's ensemble weight, 0..1.</param>
    /// <param name="atrPercentile">Current volatility percentile, for volatility targeting.</param>
    /// <param name="correlationPenalty">0..1, where 1 means fully correlated with the existing book.</param>
    /// <param name="dataQualityScore">0..1 from the data quality monitor.</param>
    /// <param name="executionQuality">0..1 from the execution quality tracker.</param>
    /// <param name="remainingRiskBudgetPercent">Portfolio headroom still available, in percent of equity.</param>
    public SizingResult Compute(
        SymbolSpec spec,
        AccountSnapshot account,
        double entryPrice,
        double stopPrice,
        Side direction,
        double riskStateMultiplier,
        double regimeMultiplier,
        double ensembleConfidence,
        double edgeSurplusR,
        double strategyWeight,
        double atrPercentile,
        double correlationPenalty,
        double dataQualityScore,
        double executionQuality,
        double remainingRiskBudgetPercent)
    {
        if (spec == null) return SizingResult.Rejected(NoTradeReason.BrokerConstraint, "symbol specification unavailable");
        if (!account.IsUsable) return SizingResult.Rejected(NoTradeReason.DataQuality, "account snapshot unusable");
        if (direction == Side.None) return SizingResult.Rejected(NoTradeReason.NoSignal, "no direction");

        double stopDistance = direction == Side.Long ? entryPrice - stopPrice : stopPrice - entryPrice;
        if (stopDistance <= 0)
        {
            return SizingResult.Rejected(NoTradeReason.InvalidStopPlacement, $"stop is on the wrong side of entry ({stopDistance:F6})");
        }

        var factors = new Dictionary<string, double>(StringComparer.Ordinal);

        // --- Multiplicative factors, every one of them at most 1.0 -----------------------
        double riskState = MathUtil.Clamp01(riskStateMultiplier);
        double regime = MathUtil.Clamp01(regimeMultiplier);

        // Confidence: at the minimum usable confidence the trade is sized small; at full
        // conviction it is sized at the base. ConfidenceWeight sets how much this matters.
        double confidence = Blend(MathUtil.Clamp01(ensembleConfidence), _config.ConfidenceWeight);

        // Edge: surplus above the required threshold, saturating at 0.5R so an outlier
        // estimate cannot buy an outsized position.
        double edge = Blend(MathUtil.LinearScale(edgeSurplusR, 0, 0.5), _config.EdgeWeight);

        double strategy = MathUtil.Clamp(strategyWeight <= 0 ? 1.0 : MathUtil.LinearScale(strategyWeight, 0.05, 0.35), 0.4, 1.0);
        double volatility = VolatilityTargetFactor(atrPercentile);
        double correlation = MathUtil.Clamp(1.0 - (0.5 * MathUtil.Clamp01(correlationPenalty)), 0.5, 1.0);
        double dataQuality = MathUtil.Clamp(dataQualityScore, 0.3, 1.0);
        double execution = MathUtil.Clamp(executionQuality, 0.3, 1.0);

        factors["riskState"] = riskState;
        factors["regime"] = regime;
        factors["confidence"] = confidence;
        factors["edge"] = edge;
        factors["strategy"] = strategy;
        factors["volatility"] = volatility;
        factors["correlation"] = correlation;
        factors["dataQuality"] = dataQuality;
        factors["execution"] = execution;

        double riskPercent = _config.RiskPerTradePercent
            * riskState * regime * confidence * edge * strategy * volatility * correlation * dataQuality * execution;

        // --- Hard caps -------------------------------------------------------------------
        // Applied last and unconditionally. Whatever the factors produced, this is the line
        // that cannot be crossed.
        riskPercent = Math.Min(riskPercent, _riskConfig.HardMaxRiskPerTradePercent);
        riskPercent = Math.Min(riskPercent, Math.Max(0, remainingRiskBudgetPercent));

        if (riskPercent < _config.MinRiskPerTradePercent)
        {
            return SizingResult.Rejected(NoTradeReason.SizeBelowMinimum,
                $"risk shrank to {riskPercent:F4}%, below the {_config.MinRiskPerTradePercent:F4}% floor");
        }

        // --- Units --------------------------------------------------------------------------
        double riskAmount = account.Equity * riskPercent / 100.0;
        double rawUnits = riskAmount / stopDistance;

        double units = spec.NormalizeVolumeDown(rawUnits);
        if (units <= 0)
        {
            return SizingResult.Rejected(NoTradeReason.SizeBelowMinimum,
                $"{rawUnits:F6} units is below the broker minimum of {spec.VolumeInUnitsMin:F6}");
        }

        // --- Margin guard (spec sections 114-115) ----------------------------------------------
        double estimatedMargin = spec.EstimateMargin(units, entryPrice);
        double marginBudget = account.FreeMargin * _config.MaxMarginUtilization;
        if (estimatedMargin > marginBudget && marginBudget > 0)
        {
            double scaled = units * (marginBudget / estimatedMargin);
            units = spec.NormalizeVolumeDown(scaled);
            if (units <= 0)
            {
                return SizingResult.Rejected(NoTradeReason.MarginGuard,
                    $"estimated margin {estimatedMargin:F2} exceeds the {marginBudget:F2} budget and cannot be scaled down");
            }
            factors["marginScaled"] = marginBudget / estimatedMargin;
        }

        // Recompute the REALISED risk from the size actually obtainable. Rounding down means
        // the true risk is at or below the requested figure, and the record must say so.
        double actualRiskAmount = units * stopDistance;
        double actualRiskPercent = 100.0 * actualRiskAmount / account.Equity;

        return new SizingResult
        {
            VolumeInUnits = units,
            RiskPercent = actualRiskPercent,
            RiskAmount = actualRiskAmount,
            StopDistance = stopDistance,
            Factors = factors,
            RejectionReason = NoTradeReason.None,
        };
    }

    /// <summary>
    /// Blends a 0..1 score toward 1.0 by a weight, so that a weight of 0 disables the factor
    /// entirely and a weight of 1 applies it in full. The result is always at most 1.0, which
    /// is what keeps the whole calculation incapable of scaling up.
    /// </summary>
    private static double Blend(double score, double weight)
    {
        double w = MathUtil.Clamp01(weight);
        return MathUtil.Clamp(1.0 - (w * (1.0 - MathUtil.Clamp01(score))), 0.05, 1.0);
    }

    /// <summary>
    /// Volatility targeting (spec section 64).
    ///
    /// Scales size DOWN as volatility rises above its neutral percentile, and never up when
    /// it falls below. Sizing up into quiet markets is how a system arranges to be at its
    /// largest exactly when volatility mean-reverts back up, which it does.
    /// </summary>
    private double VolatilityTargetFactor(double atrPercentile)
    {
        double excess = MathUtil.Clamp01(atrPercentile) - _config.VolatilityTargetPercentile;
        if (excess <= 0) return _config.MaxVolatilityMultiplier;

        double reduction = MathUtil.Clamp01(excess / Math.Max(1.0 - _config.VolatilityTargetPercentile, 1e-6));
        double factor = 1.0 - (reduction * MathUtil.Clamp01(_config.VolatilityTargetStrength));

        return MathUtil.Clamp(factor, _config.MinVolatilityMultiplier, _config.MaxVolatilityMultiplier);
    }
}
