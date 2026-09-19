using System;
using System.Collections.Generic;
using Quant.Core.Features;
using Quant.Core.Numerics;
using Quant.Core.Primitives;


namespace Quant.Core.Strategies;

/// <summary>
/// Buys corrections inside an established trend (spec section 13).
///
/// The hard part is telling a pullback from the start of a reversal, and no indicator
/// answers that. What this strategy does instead is insist the correction be SHALLOW in
/// structural terms: the higher timeframe must still be trending, the correction must not
/// have broken the trend's own structure, and it must be losing momentum rather than
/// gaining it. A correction that is accelerating is a reversal until proven otherwise.
/// </summary>
public sealed class PullbackStrategy : StrategyBase
{
    public override string Name => "Pullback";
    public override StrategyKind Kind => StrategyKind.Pullback;

    public override IReadOnlyDictionary<MarketRegime, double> RegimeFit => Fit(
        (MarketRegime.TrendUp, 1.00),
        (MarketRegime.TrendDown, 1.00),
        (MarketRegime.Breakout, 0.45),
        (MarketRegime.HighVolatility, 0.30),
        (MarketRegime.Range, 0.15),
        (MarketRegime.Euphoria, 0.25),
        (MarketRegime.Chop, 0.0),
        (MarketRegime.Panic, 0.0),
        (MarketRegime.LiquidityStress, 0.0));

    public override IReadOnlyCollection<FeatureFamily> Families =>
        Uses(FeatureFamily.TrendDirection, FeatureFamily.RangePosition, FeatureFamily.MarketStructure);

    protected override StrategySignal EvaluateCore(StrategyContext ctx)
    {
        FeatureVector f = ctx.Features;

        // Direction is the CONTEXT trend's direction. A pullback is defined relative to the
        // larger move, never relative to the fast timeframe it is currently dominating.
        Side side = f.ContextTrendStrength > 0.15 ? Side.Long
                  : f.ContextTrendStrength < -0.15 ? Side.Short
                  : Side.None;
        if (side == Side.None) return StrategySignal.Neutral(Name, "no higher-timeframe trend to pull back within");

        double sign = side == Side.Long ? 1 : -1;

        // There must actually BE a correction: price has to have moved against the trend.
        double retracement = -sign * f.DistanceToEmaFastInAtr;
        if (retracement <= 0.15)
        {
            return StrategySignal.Neutral(Name, "no meaningful correction yet");
        }

        // ...but not so deep that the trend itself is in question.
        double depthPenalty = MathUtil.LinearScale(retracement, 1.5, 3.5);
        if (depthPenalty > 0.85)
        {
            return StrategySignal.Neutral(Name, $"correction {retracement:F1} ATR deep - trend integrity in doubt");
        }
        double depthQuality = 1.0 - depthPenalty;

        // The correction must not have broken the trend's structure.
        double structureIntact = MathUtil.Clamp01(0.5 + (sign * f.StructureScore * 0.5));
        if (structureIntact < 0.35)
        {
            return StrategySignal.Neutral(Name, "correction has broken the trend structure");
        }

        // A correction that is still accelerating is not a pullback. This is the single most
        // important condition here and it is why counter-trend momentum is required to be
        // fading, not merely present.
        double counterMomentum = -sign * f.MacdHistogramDeltaInAtr;
        if (counterMomentum > 0.05)
        {
            return StrategySignal.Neutral(Name, "correction still accelerating - treating as a reversal");
        }
        double fading = MathUtil.LinearScale(-counterMomentum, 0.0, 0.15);

        // Oscillator should be reset but not capitulating.
        double rsi = f.Rsi;
        double oscillatorReset = side == Side.Long
            ? MathUtil.LinearScale(60 - rsi, 0, 25) * (rsi < 25 ? 0.4 : 1.0)
            : MathUtil.LinearScale(rsi - 40, 0, 25) * (rsi > 75 ? 0.4 : 1.0);

        double trendQuality = MathUtil.Clamp01(Math.Abs(f.ContextTrendStrength));

        double confidence = Combine(trendQuality, depthQuality, structureIntact, fading, oscillatorReset);

        // The correction's own extreme is what must hold.
        double invalidation = side == Side.Long
            ? ctx.Bar.Low - (ctx.Config.Exit.StructureStopBufferAtr * f.Atr)
            : ctx.Bar.High + (ctx.Config.Exit.StructureStopBufferAtr * f.Atr);

        return Signal(this, side, confidence, f.Price,
            $"pullback {retracement:F1} ATR in a {f.ContextTrendStrength:F2} context trend, momentum fading",
            invalidation: invalidation);
    }
}
