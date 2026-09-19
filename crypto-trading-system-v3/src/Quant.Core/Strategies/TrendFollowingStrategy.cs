using System;
using System.Collections.Generic;
using Quant.Core.Features;
using Quant.Core.Numerics;
using Quant.Core.Primitives;

namespace Quant.Core.Strategies;

/// <summary>
/// Trades in the direction of an established trend (spec section 13).
///
/// Deliberately does NOT trade the start of a trend — that is the breakout strategy's job,
/// and the two would otherwise be the same trade wearing different labels. This one wants a
/// trend that already exists, on both the signal and the context timeframe, and it wants to
/// enter when price is near the trend's mean rather than extended from it.
/// </summary>
public sealed class TrendFollowingStrategy : StrategyBase
{
    public override string Name => "TrendFollowing";
    public override StrategyKind Kind => StrategyKind.TrendFollowing;

    public override IReadOnlyDictionary<MarketRegime, double> RegimeFit => Fit(
        (MarketRegime.TrendUp, 1.00),
        (MarketRegime.TrendDown, 1.00),
        (MarketRegime.Breakout, 0.55),
        (MarketRegime.HighVolatility, 0.30),
        (MarketRegime.Euphoria, 0.30),
        (MarketRegime.Range, 0.05),
        (MarketRegime.Chop, 0.0),
        (MarketRegime.Panic, 0.0),
        (MarketRegime.LiquidityStress, 0.0));

    public override IReadOnlyCollection<FeatureFamily> Families =>
        Uses(FeatureFamily.TrendDirection, FeatureFamily.Momentum);

    protected override StrategySignal EvaluateCore(StrategyContext ctx)
    {
        FeatureVector f = ctx.Features;

        Side side = f.TrendStrength > 0 ? Side.Long : f.TrendStrength < 0 ? Side.Short : Side.None;
        if (side == Side.None) return StrategySignal.Neutral(Name, "no directional trend");

        double sign = side == Side.Long ? 1 : -1;

        // The trend must exist on the decision timeframe AND on the one above it. A trend
        // visible only on the fast timeframe is usually a pullback inside a larger move in
        // the other direction, which is the worst thing to be trading with the trend.
        double signalTrend = MathUtil.Clamp01(Math.Abs(f.TrendStrength));
        double contextAgreement = MathUtil.Clamp01(sign * f.ContextTrendStrength);
        if (contextAgreement < 0.10)
        {
            return StrategySignal.Neutral(Name, "higher timeframe does not confirm the trend");
        }

        double adxStrength = MathUtil.LinearScale(f.Adx, ctx.Config.Regime.RangeAdxThreshold, ctx.Config.Regime.TrendAdxThreshold + 12);
        double fitQuality = MathUtil.LinearScale(f.RegressionFit, 0.30, 0.75);
        double structureAgreement = MathUtil.Clamp01(0.5 + (sign * f.StructureScore * 0.5));

        // Entering a long 4 ATR above the fast EMA is buying the top of a leg. Proximity is
        // scored so the strategy prefers the part of the trend where the stop can be tight
        // enough for the trade to be worth taking.
        double extension = sign * f.DistanceToEmaFastInAtr;
        double proximity = extension <= 0
            ? 1.0                                              // at or below the mean: ideal
            : 1.0 - MathUtil.LinearScale(extension, 0.5, 3.0); // increasingly extended

        if (proximity < 0.15)
        {
            return StrategySignal.Neutral(Name, $"price extended {extension:F1} ATR from the fast EMA");
        }

        double confidence = Combine(signalTrend, contextAgreement, adxStrength, fitQuality, structureAgreement, proximity);

        // The slow EMA is the line whose loss means the trend read was wrong.
        double invalidation = ctx.Signal.EmaSlow.Value - (sign * ctx.Config.Exit.StructureStopBufferAtr * f.Atr);

        return Signal(this, side, confidence, f.Price,
            $"trend {f.TrendStrength:F2} adx {f.Adx:F0} fit {f.RegressionFit:F2} ext {extension:F1}ATR",
            invalidation: invalidation);
    }
}
