using System;
using System.Collections.Generic;
using Quant.Core.Config;
using Quant.Core.Ev;
using Quant.Core.Features;
using Quant.Core.Numerics;
using Quant.Core.Portfolio;
using Quant.Core.Primitives;
using Quant.Core.Regime;
using Quant.Core.Risk;

namespace Quant.Core.Decision;

/// <summary>
/// The no-trade engine (spec sections 118, 167).
///
/// Every check is a named, ordered, independently testable predicate, and the FIRST one to
/// fail stops the evaluation and is recorded as the binding reason. Ordering matters and is
/// deliberate: the cheapest and most fundamental checks run first, so an expensive
/// probability estimate is never computed for a candidate that was already dead on data
/// quality.
///
/// The design principle behind this whole class is the spec's most important rule: NO TRADE
/// is a complete and frequently correct decision. There is no fallback path, no "trade it
/// smaller instead", and no way for a caller to bypass a gate. If nothing passes, nothing
/// trades, and the system waits — which is what it should spend most of its time doing.
/// </summary>
public sealed class TradeGate
{
    private readonly EngineConfig _config;

    public TradeGate(EngineConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// Checks that depend only on the market and the signal, run before any expensive
    /// modelling. Returns the first failure.
    /// </summary>
    public GateOutcome CheckPreTrade(
        TradeCandidate candidate,
        RiskAssessment risk,
        AnomalyReport anomaly,
        IReadOnlyDictionary<string, DateTime> lastSignalPerSymbol,
        DateTime nowUtc)
    {
        if (candidate == null) return GateOutcome.Reject(NoTradeReason.NoSignal, "no candidate");

        // 1. Data quality -- nothing downstream means anything if the inputs are wrong.
        if (!candidate.DataQuality.IsAcceptable)
        {
            return GateOutcome.Reject(candidate.DataQuality.Reason, candidate.DataQuality.IssueSummary);
        }

        // 2. Risk posture.
        if (!risk.AllowsNewPositions)
        {
            return GateOutcome.Reject(
                risk.BlockingReason == NoTradeReason.None ? NoTradeReason.RiskStateHalt : risk.BlockingReason,
                risk.ReasonSummary);
        }

        // 3. Signal exists and is directional.
        if (candidate.Ensemble == null || !candidate.Ensemble.IsActionable)
        {
            return GateOutcome.Reject(NoTradeReason.NoSignal, candidate.Ensemble?.Rationale ?? "no ensemble decision");
        }

        // 4. Regime is intelligible and not actively hostile.
        RegimeAssessment regime = candidate.Regime;
        if (regime == null || regime.Primary == MarketRegime.Unknown)
        {
            return GateOutcome.Reject(NoTradeReason.LowRegimeConfidence, "regime unknown");
        }

        if (regime.IsHostile)
        {
            return GateOutcome.Reject(NoTradeReason.RegimeMismatch, $"regime {regime.Primary} is hostile to new risk");
        }

        if (regime.Confidence < candidate.RegimeClarityThreshold)
        {
            return GateOutcome.Reject(NoTradeReason.LowRegimeConfidence,
                $"regime confidence {regime.Confidence:P0} below the {candidate.RegimeClarityThreshold:P0} minimum " +
                $"(верхние {1 - _config.Regime.RegimeClarityPercentile:P0} чтений этого классификатора)");
        }

        // 5. Ensemble conviction.
        if (candidate.Ensemble.Confidence < _config.Strategy.MinEnsembleConfidence)
        {
            return GateOutcome.Reject(NoTradeReason.LowSignalConfidence,
                $"ensemble confidence {candidate.Ensemble.Confidence:P0} below the {_config.Strategy.MinEnsembleConfidence:P0} minimum");
        }

        FeatureVector f = candidate.Features;

        // 6. Anomalies and post-event recovery.
        if (anomaly.IsExtremeEvent)
        {
            return GateOutcome.Reject(NoTradeReason.ExtremeEvent, anomaly.Summary);
        }

        // 7. Spread.
        //
        // The ABSOLUTE test comes first and is unconditional: a spread that is a large
        // fraction of ATR makes the trade unaffordable regardless of how it ranks against
        // its own history.
        if (f.SpreadToAtr > _config.Risk.MaxSpreadToAtr)
        {
            return GateOutcome.Reject(NoTradeReason.SpreadTooWide,
                $"spread is {f.SpreadToAtr:P1} of ATR, limit {_config.Risk.MaxSpreadToAtr:P1}");
        }

        // The RELATIVE test then catches a spread that is unusually wide for this instrument
        // even though it is not yet expensive in absolute terms -- which is the early warning
        // that liquidity is deteriorating.
        //
        // It only applies once the spread is actually costing something. Without that
        // qualifier, an instrument whose spread is a trivial fraction of ATR would still be
        // refused for ranking high against its own very narrow distribution, which blocks
        // trades on exactly the cheapest instruments to trade.
        bool spreadIsMaterial = f.SpreadToAtr > _config.Risk.MaxSpreadToAtr * 0.25;
        if (spreadIsMaterial && f.SpreadPercentile > _config.Risk.MaxSpreadPercentile)
        {
            return GateOutcome.Reject(NoTradeReason.SpreadTooWide,
                $"spread at the {f.SpreadPercentile:P0} percentile ({f.SpreadToAtr:P1} of ATR), limit {_config.Risk.MaxSpreadPercentile:P0}");
        }

        // 8. Volatility.
        if (f.AtrPercentile > _config.Risk.MaxAtrPercentileForEntry)
        {
            return GateOutcome.Reject(NoTradeReason.VolatilityTooHigh,
                $"volatility at the {f.AtrPercentile:P0} percentile, limit {_config.Risk.MaxAtrPercentileForEntry:P0}");
        }

        // 9. FOMO filter (spec section 52): never enter on the bar that already made the move.
        if (f.BarRangeInAtr > _config.Strategy.FomoBarRangeInAtr)
        {
            return GateOutcome.Reject(NoTradeReason.Chasing,
                $"last bar spanned {f.BarRangeInAtr:F1} ATR; waiting for a pullback or retest");
        }

        // 10. Chasing filter (spec section 51): price has already left the level behind.
        if (candidate.Leader != null && candidate.Leader.AnchorPrice > 0 && f.Atr > 0)
        {
            double sign = candidate.Direction == Side.Long ? 1 : -1;
            double travelled = sign * (f.Price - candidate.Leader.AnchorPrice) / f.Atr;
            if (travelled > _config.Strategy.MaxChaseInAtr)
            {
                return GateOutcome.Reject(NoTradeReason.Chasing,
                    $"price is already {travelled:F2} ATR beyond the signal level, limit {_config.Strategy.MaxChaseInAtr:F2}");
            }
        }

        // 11. Signal age (spec section 53).
        double barMinutes = (int)_config.Data.SignalTimeframe;
        double signalAgeMinutes = (nowUtc - candidate.Features.TimeUtc).TotalMinutes;
        if (signalAgeMinutes > barMinutes * _config.Strategy.SignalExpiryBars)
        {
            return GateOutcome.Reject(NoTradeReason.SignalExpired,
                $"signal is {signalAgeMinutes:F0} minutes old, expiry {barMinutes * _config.Strategy.SignalExpiryBars:F0} minutes");
        }

        // 12. Duplicate suppression (spec section 54).
        if (lastSignalPerSymbol != null &&
            lastSignalPerSymbol.TryGetValue(candidate.SymbolName, out DateTime lastSignal) &&
            candidate.Features.TimeUtc <= lastSignal)
        {
            return GateOutcome.Reject(NoTradeReason.DuplicateSignal,
                $"a signal for this bar ({candidate.Features.TimeUtc:HH:mm}Z) has already been acted on");
        }

        return GateOutcome.Pass;
    }

    /// <summary>
    /// Checks that require the exit plan, the probability estimate and the expected value.
    /// Run only for candidates that survived <see cref="CheckPreTrade"/>.
    /// </summary>
    public GateOutcome CheckEconomics(TradeCandidate candidate)
    {
        if (candidate.Exit == null || !candidate.Exit.IsValid)
        {
            return GateOutcome.Reject(
                candidate.Exit?.RejectionReason ?? NoTradeReason.InvalidStopPlacement,
                candidate.Exit?.Detail ?? "no exit plan");
        }

        if (candidate.Exit.RewardToRisk < _config.Ev.MinRewardToRisk)
        {
            return GateOutcome.Reject(NoTradeReason.PoorRiskReward,
                $"reward-to-risk {candidate.Exit.RewardToRisk:F2} below the {_config.Ev.MinRewardToRisk:F2} minimum");
        }

        if (candidate.Probability.Confidence <= 0)
        {
            return GateOutcome.Reject(NoTradeReason.LowProbabilityConfidence, "no usable probability estimate");
        }

        ExpectedValueResult ev = candidate.ExpectedValue;
        if (ev == null)
        {
            return GateOutcome.Reject(NoTradeReason.NegativeExpectedValue, "expected value not computed");
        }

        if (!MathUtil.IsFinite(ev.ExpectedValueR) || ev.ExpectedValueR <= 0)
        {
            return GateOutcome.Reject(NoTradeReason.NegativeExpectedValue,
                $"expected value {ev.ExpectedValueR:F3}R is not positive");
        }

        if (!ev.IsAcceptable)
        {
            return GateOutcome.Reject(NoTradeReason.InsufficientEdge,
                $"edge {ev.DecisionEdgeR:F3}R below the {ev.RequiredEdgeR:F3}R required" +
                $" (доверие к оценке {ev.Trust:P0})");
        }

        return GateOutcome.Pass;
    }

    /// <summary>
    /// Portfolio and sizing checks, run last because they depend on what else is open and on
    /// how the other candidates ranked.
    /// </summary>
    public GateOutcome CheckPortfolio(
        TradeCandidate candidate,
        PortfolioRiskEngine portfolio,
        PortfolioExposure exposure,
        IReadOnlyDictionary<string, int> clusters,
        int positionsInSymbol)
    {
        if (candidate.Sizing == null || !candidate.Sizing.IsTradeable)
        {
            return GateOutcome.Reject(
                candidate.Sizing?.RejectionReason ?? NoTradeReason.SizeBelowMinimum,
                candidate.Sizing?.Detail ?? "no size");
        }

        NoTradeReason limit = portfolio.CheckLimits(
            exposure, candidate.SymbolName, candidate.Direction, candidate.Sizing.RiskPercent,
            clusters, positionsInSymbol, out string detail);

        return limit == NoTradeReason.None ? GateOutcome.Pass : GateOutcome.Reject(limit, detail);
    }
}
