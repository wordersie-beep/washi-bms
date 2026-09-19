using System;
using System.Collections.Generic;
using Quant.Core.Features;
using Quant.Core.Numerics;
using Quant.Core.Primitives;

namespace Quant.Core.Strategies;

/// <summary>
/// Trades the transition from compression to expansion (spec section 13).
///
/// Distinct from the breakout strategy: that one needs a CHANNEL LEVEL to be taken out,
/// this one needs only that volatility has begun expanding out of a quiet base, and it
/// takes its direction from where the expansion is pointing. It is the earlier, lower-
/// confidence expression of the same market event, and it is sized accordingly.
/// </summary>
public sealed class VolatilityExpansionStrategy : StrategyBase
{
    public override string Name => "VolatilityExpansion";
    public override StrategyKind Kind => StrategyKind.VolatilityExpansion;

    public override IReadOnlyDictionary<MarketRegime, double> RegimeFit => Fit(
        (MarketRegime.LowVolatility, 1.00),
        (MarketRegime.Breakout, 0.80),
        (MarketRegime.Range, 0.45),
        (MarketRegime.TrendUp, 0.40),
        (MarketRegime.TrendDown, 0.40),
        (MarketRegime.Chop, 0.10),
        (MarketRegime.HighVolatility, 0.0),
        (MarketRegime.Panic, 0.0),
        (MarketRegime.LiquidityStress, 0.0));

    public override IReadOnlyCollection<FeatureFamily> Families =>
        Uses(FeatureFamily.Volatility, FeatureFamily.Volume);

    protected override StrategySignal EvaluateCore(StrategyContext ctx)
    {
        FeatureVector f = ctx.Features;

        // The base must have been genuinely quiet. Expansion from an already-elevated level
        // is not a new move, it is an existing one getting messier.
        double priorCompression = 1.0 - MathUtil.Clamp01(f.BollingerWidthPercentile);
        if (priorCompression < 0.35)
        {
            return StrategySignal.Neutral(Name, $"no quiet base (band width percentile {f.BollingerWidthPercentile:P0})");
        }

        double expansion = MathUtil.LinearScale(f.VolatilityExpansion, 0.05, 0.30);
        if (expansion < 0.15)
        {
            return StrategySignal.Neutral(Name, "volatility not yet expanding");
        }

        // Direction: the expansion bar itself. A large bar with a decisive body is the move
        // announcing which way it intends to go.
        Side side = ctx.Bar.Close > ctx.Bar.Open ? Side.Long
                  : ctx.Bar.Close < ctx.Bar.Open ? Side.Short
                  : Side.None;
        if (side == Side.None) return StrategySignal.Neutral(Name, "expansion bar has no direction");

        double sign = side == Side.Long ? 1 : -1;

        double bodyQuality = MathUtil.LinearScale(f.BodyFraction, 0.40, 0.80);
        double impulse = MathUtil.LinearScale(Math.Abs(f.Return1InAtr), 0.5, 2.0);
        double participation = MathUtil.LinearScale(f.VolumePercentile, 0.50, 0.88);

        // Do not fight the higher timeframe outright, but do not require its blessing either
        // -- the whole point is to be early.
        double contextPenalty = MathUtil.Clamp01(-sign * f.ContextTrendStrength);
        double contextScore = 1.0 - (0.6 * contextPenalty);

        // An expansion bar that has already gone vertical has priced in the news.
        if (f.BarRangeInAtr > ctx.Config.Strategy.FomoBarRangeInAtr)
        {
            return StrategySignal.Neutral(Name, $"expansion bar already {f.BarRangeInAtr:F1} ATR - too late");
        }

        double confidence = Combine(priorCompression, expansion, bodyQuality, impulse, participation, contextScore);

        // Being early means being wrong more often. The cap is explicit rather than emergent.
        confidence = Math.Min(confidence, 0.80);

        double invalidation = side == Side.Long
            ? ctx.Bar.Low - (ctx.Config.Exit.StructureStopBufferAtr * f.Atr)
            : ctx.Bar.High + (ctx.Config.Exit.StructureStopBufferAtr * f.Atr);

        return Signal(this, side, confidence, ctx.Bar.Open,
            $"expansion {f.VolatilityExpansion:F2} from compression {priorCompression:F2}, body {f.BodyFraction:F2}",
            invalidation: invalidation);
    }
}
