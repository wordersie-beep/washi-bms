using System;
using Quant.Core.Config;
using Quant.Core.Data;
using Quant.Core.Ev;
using Quant.Core.Indicators;
using Quant.Core.Numerics;
using Quant.Core.Primitives;
using Quant.Core.Stats;
using Quant.Core.Strategies;

namespace Quant.Core.Exits;

/// <summary>
/// Builds the exit plan before entry (spec sections 41-50).
///
/// The exits are decided first, and the trade is only taken if they make sense. That
/// ordering is the point: a system that enters and then works out where to get out has
/// already committed to whatever stop the position happens to need, which is how a perfectly
/// good signal becomes an untradeable one.
///
/// Stop selection considers several candidates and takes the most DEFENSIBLE rather than the
/// tightest:
///
///   * the strategy's own invalidation level, when it has an opinion — the level whose
///     breach genuinely disproves the idea;
///   * a structural level, buffered;
///   * a volatility-based distance;
///   * a floor drawn from the historical MAE distribution (spec section 45), so the stop is
///     not sitting inside the region where winning trades routinely dip.
///
/// The tightest stop is almost never the right one. A stop inside the noise converts winners
/// into losers at exactly the rate the noise dictates, and no amount of entry quality
/// compensates for it.
/// </summary>
public sealed class ExitPlanner
{
    private readonly ExitConfig _config;
    private readonly EvConfig _evConfig;
    private readonly PerformanceStore _performance;

    public ExitPlanner(ExitConfig config, EvConfig evConfig, PerformanceStore performance)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _evConfig = evConfig ?? throw new ArgumentNullException(nameof(evConfig));
        _performance = performance ?? throw new ArgumentNullException(nameof(performance));
    }

    public ExitPlan Build(
        SymbolSpec spec,
        SymbolDataSet data,
        Side direction,
        double entryPrice,
        StrategySignal leader,
        MarketRegime regime,
        CostEstimate costAtOneAtr)
    {
        if (spec == null) return ExitPlan.Rejected(NoTradeReason.BrokerConstraint, "symbol specification unavailable");
        if (data == null || direction == Side.None) return ExitPlan.Rejected(NoTradeReason.NoSignal, "no direction");

        TimeframeSeries series = data.Signal;
        double atr = series.Atr.Value;
        if (atr <= 0 || entryPrice <= 0)
        {
            return ExitPlan.Rejected(NoTradeReason.DataQuality, "ATR or price unavailable");
        }

        double sign = direction == Side.Long ? 1 : -1;

        // --- Candidate stop distances, all in price units ---------------------------------
        double atrStop = atr * _config.AtrStopMultiple;

        double structureStop = atrStop;
        StopMethod method = StopMethod.Atr;

        SwingStructure structure = series.Structure;
        bool haveStructure = direction == Side.Long
            ? structure.TryGetSupportBelow(entryPrice, out SwingPoint level)
            : structure.TryGetResistanceAbove(entryPrice, out level);

        if (haveStructure)
        {
            double candidate = Math.Abs(entryPrice - level.Price) + (atr * _config.StructureStopBufferAtr);
            if (candidate > 0)
            {
                structureStop = candidate;
                method = StopMethod.Structure;
            }
        }

        double chosen = Math.Max(atrStop, structureStop);

        // The strategy's own invalidation level wins when it is wider than the generic
        // candidates: the strategy knows what would disprove its thesis, and a stop inside
        // that level exits a trade that is still working.
        if (leader?.InvalidationPrice != null)
        {
            double invalidationDistance = sign * (entryPrice - leader.InvalidationPrice.Value);
            if (invalidationDistance > 0 && invalidationDistance > chosen)
            {
                chosen = invalidationDistance;
                method = StopMethod.StrategyInvalidation;
            }
        }

        // --- MAE floor (spec section 45) ---------------------------------------------------
        (SegmentStats stats, double trust) = _performance.BestAvailable(
            leader?.StrategyName ?? "unknown", regime, data.SymbolName, _config.MinSampleForMaeStops);

        if (stats.MaeSampleSize >= _config.MinSampleForMaeStops && trust >= 0.5)
        {
            // MAE is recorded in R against the stop that was used at the time, so it is
            // scaled by the candidate distance to convert it back to price units.
            double maeQuantileR = stats.MaeQuantile(_config.MaeStopQuantile);
            double maeFloor = maeQuantileR * chosen;
            if (maeFloor > chosen)
            {
                chosen = maeFloor;
                method = StopMethod.MaeDistribution;
            }
        }

        // --- Broker minimum -----------------------------------------------------------------
        if (spec.MinStopLossDistancePrice > 0 && chosen < spec.MinStopLossDistancePrice)
        {
            chosen = spec.MinStopLossDistancePrice;
            method = StopMethod.BrokerMinimum;
        }

        // --- Sanity bounds (spec section 42) --------------------------------------------------
        double stopInAtr = chosen / atr;

        if (stopInAtr < _config.MinStopInAtr)
        {
            return ExitPlan.Rejected(NoTradeReason.InvalidStopPlacement,
                $"stop of {stopInAtr:F2} ATR sits inside the noise (minimum {_config.MinStopInAtr:F2} ATR)");
        }

        if (stopInAtr > _config.MaxStopInAtr)
        {
            return ExitPlan.Rejected(NoTradeReason.InvalidStopPlacement,
                $"stop of {stopInAtr:F2} ATR is too wide to be economic (maximum {_config.MaxStopInAtr:F2} ATR)");
        }

        // --- Economic viability ------------------------------------------------------------
        // Costs were estimated against one ATR of stop; rescale them to the stop actually
        // chosen. If round-trip costs eat a large share of the risk, the trade cannot pay.
        double costPrice = costAtOneAtr.TotalPrice;
        double costR = MathUtil.SafeDiv(costPrice, chosen, 1.0);
        if (costR > 0.35)
        {
            return ExitPlan.Rejected(NoTradeReason.PoorRiskReward,
                $"round-trip costs are {costR:P0} of the risk; the trade cannot pay for itself");
        }

        double stopPrice = spec.RoundToTick(entryPrice - (sign * chosen));

        // Rounding to the tick grid can nudge the stop; recompute the true distance from the
        // rounded level so every R downstream is measured against the stop that will actually
        // be placed, not the one that was intended.
        double actualDistance = sign * (entryPrice - stopPrice);
        if (actualDistance <= 0)
        {
            return ExitPlan.Rejected(NoTradeReason.InvalidStopPlacement, "stop rounded onto or past the entry price");
        }

        // --- Targets (spec sections 43-44) -----------------------------------------------------
        (double target1R, double target2R) = TargetsFor(regime);

        double target1Price = spec.RoundToTick(entryPrice + (sign * actualDistance * target1R));
        double target2Price = spec.RoundToTick(entryPrice + (sign * actualDistance * target2R));

        // --- Net break-even (spec section 47) ---------------------------------------------------
        double netBreakEven = spec.RoundToTick(
            entryPrice + (sign * (costPrice + (actualDistance * _config.BreakEvenBufferR))));

        return new ExitPlan
        {
            IsValid = true,
            RejectionReason = NoTradeReason.None,
            StopPrice = stopPrice,
            StopMethod = method,
            StopDistance = actualDistance,
            StopInAtr = actualDistance / atr,
            Target1Price = target1Price,
            Target2Price = target2Price,
            Target1R = target1R,
            Target2R = target2R,
            Target1ClosePercent = _config.Target1ClosePercent,
            Target2ClosePercent = _config.Target2ClosePercent,
            BreakEvenTriggerR = _config.BreakEvenTriggerR,
            NetBreakEvenPrice = netBreakEven,
            TrailDistanceInAtr = TrailFor(regime),
            TrailActivationR = _config.TrailActivationR,
            TimeStopBars = TimeStopFor(regime),
            InvalidationPrice = leader?.InvalidationPrice,
        };
    }

    /// <summary>
    /// Targets by regime (spec section 131).
    ///
    /// Trends are given room to run; ranges are taken off faster because price in a range is
    /// by definition going to turn around. Using one target everywhere means either leaving
    /// most of a trend on the table or handing back every range profit.
    /// </summary>
    private (double Target1R, double Target2R) TargetsFor(MarketRegime regime)
    {
        double t1 = _config.Target1R;
        double t2 = _config.Target2R;

        switch (regime)
        {
            case MarketRegime.TrendUp:
            case MarketRegime.TrendDown:
                return (t1, t2 * 1.25);

            case MarketRegime.Breakout:
                return (t1 * 1.1, t2 * 1.3);

            case MarketRegime.Range:
            case MarketRegime.Chop:
                return (t1 * 0.8, t2 * 0.75);

            case MarketRegime.HighVolatility:
            case MarketRegime.Panic:
                // Wider stops in a volatile market already mean fewer R per unit of movement;
                // demanding more R on top would put the target out of reach.
                return (t1 * 0.9, t2 * 0.9);

            default:
                return (t1, t2);
        }
    }

    private double TrailFor(MarketRegime regime)
    {
        switch (regime)
        {
            case MarketRegime.TrendUp:
            case MarketRegime.TrendDown:
            case MarketRegime.Breakout:
                return _config.TrendTrailAtr;

            case MarketRegime.HighVolatility:
            case MarketRegime.Panic:
            case MarketRegime.Euphoria:
                return _config.HighVolTrailAtr;

            default:
                return _config.RangeTrailAtr;
        }
    }

    private int TimeStopFor(MarketRegime regime)
    {
        switch (regime)
        {
            case MarketRegime.TrendUp:
            case MarketRegime.TrendDown:
            case MarketRegime.Breakout:
                return _config.TimeStopBarsTrend;
            default:
                return _config.TimeStopBarsRange;
        }
    }
}
