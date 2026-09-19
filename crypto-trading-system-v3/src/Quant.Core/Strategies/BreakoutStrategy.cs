using System;
using System.Collections.Generic;
using Quant.Core.Features;
using Quant.Core.Numerics;
using Quant.Core.Primitives;

namespace Quant.Core.Strategies;

/// <summary>
/// Trades range expansion out of compression, with confirmation (spec section 13).
///
/// Three conditions are all treated as necessary, which is why they are combined
/// geometrically: prior COMPRESSION, a decisive close BEYOND the channel, and PARTICIPATION.
/// A breakout without compression before it is just a trend continuation; one without volume
/// is the setup that produces the failed-breakout reversal that this system trades from the
/// other side.
/// </summary>
public sealed class BreakoutStrategy : StrategyBase
{
    public override string Name => "Breakout";
    public override StrategyKind Kind => StrategyKind.Breakout;

    public override IReadOnlyDictionary<MarketRegime, double> RegimeFit => Fit(
        (MarketRegime.Breakout, 1.00),
        (MarketRegime.LowVolatility, 0.70),
        (MarketRegime.TrendUp, 0.60),
        (MarketRegime.TrendDown, 0.60),
        (MarketRegime.Range, 0.35),
        (MarketRegime.HighVolatility, 0.25),
        (MarketRegime.Chop, 0.0),
        (MarketRegime.Panic, 0.0),
        (MarketRegime.LiquidityStress, 0.0));

    public override IReadOnlyCollection<FeatureFamily> Families =>
        Uses(FeatureFamily.Volatility, FeatureFamily.RangePosition, FeatureFamily.Volume);

    protected override StrategySignal EvaluateCore(StrategyContext ctx)
    {
        FeatureVector f = ctx.Features;
        double upper = ctx.Signal.Donchian.Upper;
        double lower = ctx.Signal.Donchian.Lower;
        double close = ctx.Bar.Close;

        // The Donchian channel excludes the current bar by construction, so "closed beyond
        // the channel" is a genuine statement about the past rather than about itself.
        Side side;
        double level;
        if (close > upper) { side = Side.Long; level = upper; }
        else if (close < lower) { side = Side.Short; level = lower; }
        else return StrategySignal.Neutral(Name, "price inside the channel");

        double sign = side == Side.Long ? 1 : -1;

        // Compression BEFORE the move. Without it there was no coiled energy to release.
        double compression = 1.0 - MathUtil.Clamp01(f.BollingerWidthPercentile);
        if (compression < 0.15)
        {
            return StrategySignal.Neutral(Name, $"no prior compression (band width percentile {f.BollingerWidthPercentile:P0})");
        }

        // The close must clear the level decisively relative to volatility, and the bar must
        // close near its extreme -- a long upper wick on an upside break is rejection, not
        // a breakout.
        double penetration = MathUtil.SafeDiv(sign * (close - level), f.Atr);
        double penetrationScore = MathUtil.LinearScale(penetration, 0.05, 0.60);

        double closeStrength = side == Side.Long
            ? 1.0 - MathUtil.Clamp01(f.UpperWickFraction * 2.0)
            : 1.0 - MathUtil.Clamp01(f.LowerWickFraction * 2.0);
        if (closeStrength < 0.25)
        {
            return StrategySignal.Neutral(Name, "breakout bar rejected from its extreme");
        }

        double participation = MathUtil.LinearScale(f.VolumePercentile, 0.55, 0.90);
        if (participation < 0.15)
        {
            return StrategySignal.Neutral(Name, $"volume percentile {f.VolumePercentile:P0} does not confirm the break");
        }

        double expansion = MathUtil.LinearScale(f.VolatilityExpansion, -0.05, 0.25);
        double bodyQuality = MathUtil.LinearScale(f.BodyFraction, 0.35, 0.75);

        // Already far past the level: the trade is available only to whoever took it earlier.
        if (penetration > ctx.Config.Strategy.MaxChaseInAtr)
        {
            return StrategySignal.Neutral(Name, $"already {penetration:F1} ATR beyond the level");
        }

        double confidence = Combine(compression, penetrationScore, closeStrength, participation, expansion, bodyQuality);

        return Signal(this, side, confidence, level,
            $"broke {level:F2} by {penetration:F2} ATR, compression {compression:F2}, volPct {f.VolumePercentile:P0}",
            invalidation: level - (sign * ctx.Config.Exit.StructureStopBufferAtr * f.Atr));
    }
}
