using System;
using System.Collections.Generic;
using Quant.Core.Features;
using Quant.Core.Indicators;
using Quant.Core.Numerics;
using Quant.Core.Primitives;

namespace Quant.Core.Strategies;

/// <summary>
/// Trades confirmed swing structure: a break of structure followed by a retest
/// (spec section 13).
///
/// Everything here is built on CONFIRMED swing points, which by construction lag by the
/// confirmation window. That lag is the price of not fooling yourself — a swing identified
/// from a bar that is still forming is not a swing, and a structure model built on those is
/// a look-ahead engine that will look superb in backtest and lose money live.
/// </summary>
public sealed class MarketStructureStrategy : StrategyBase
{
    public override string Name => "MarketStructure";
    public override StrategyKind Kind => StrategyKind.MarketStructure;

    public override IReadOnlyDictionary<MarketRegime, double> RegimeFit => Fit(
        (MarketRegime.TrendUp, 0.90),
        (MarketRegime.TrendDown, 0.90),
        (MarketRegime.Breakout, 0.75),
        (MarketRegime.Range, 0.50),
        (MarketRegime.HighVolatility, 0.30),
        (MarketRegime.Chop, 0.05),
        (MarketRegime.Panic, 0.0),
        (MarketRegime.Euphoria, 0.20),
        (MarketRegime.LiquidityStress, 0.0));

    public override IReadOnlyCollection<FeatureFamily> Families =>
        Uses(FeatureFamily.MarketStructure, FeatureFamily.TrendDirection);

    protected override StrategySignal EvaluateCore(StrategyContext ctx)
    {
        FeatureVector f = ctx.Features;
        SwingStructure structure = ctx.Signal.Structure;

        if (!structure.IsReady) return StrategySignal.Neutral(Name, "structure not established");

        double score = f.StructureScore;
        Side side = score >= 0.5 ? Side.Long : score <= -0.5 ? Side.Short : Side.None;
        if (side == Side.None) return StrategySignal.Neutral(Name, $"structure inconclusive ({score:F2})");

        double sign = side == Side.Long ? 1 : -1;

        // The entry is the RETEST of the level that was broken, not the break itself. Chasing
        // the break is the breakout strategy's job and is priced differently.
        double distanceToLevel = side == Side.Long
            ? f.DistanceToSupportInAtr
            : f.DistanceToResistanceInAtr;

        // Far from the level means there is no retest to trade; on top of it means the level
        // has not held yet.
        double proximity = 1.0 - MathUtil.LinearScale(distanceToLevel, 0.4, 2.5);
        if (proximity < 0.20)
        {
            return StrategySignal.Neutral(Name, $"no level nearby to trade against ({distanceToLevel:F1} ATR)");
        }

        // Structure must be fresh. A swing forty bars old describes a market that no longer
        // exists.
        long age = structure.BarsSinceLastSwing();
        double freshness = 1.0 - MathUtil.LinearScale(age, 5, 40);
        if (freshness < 0.15)
        {
            return StrategySignal.Neutral(Name, $"structure stale ({age} bars since the last swing)");
        }

        double structureStrength = MathUtil.Clamp01(Math.Abs(score));
        double contextAgreement = MathUtil.Clamp01(0.5 + (sign * f.ContextStructureScore * 0.5));

        // A rejection wick off the level is the confirmation that it held.
        double rejection = side == Side.Long
            ? MathUtil.LinearScale(f.LowerWickFraction, 0.15, 0.50)
            : MathUtil.LinearScale(f.UpperWickFraction, 0.15, 0.50);

        double confidence = Combine(structureStrength, proximity, freshness, contextAgreement, rejection);

        bool haveLevel = side == Side.Long
            ? structure.TryGetSupportBelow(f.Price, out SwingPoint level)
            : structure.TryGetResistanceAbove(f.Price, out level);

        double? invalidation = haveLevel
            ? level.Price - (sign * ctx.Config.Exit.StructureStopBufferAtr * f.Atr)
            : (double?)null;

        return Signal(this, side, confidence, haveLevel ? level.Price : f.Price,
            $"structure {score:F2}, {distanceToLevel:F1} ATR from the level, {age} bars old",
            invalidation: invalidation);
    }
}
