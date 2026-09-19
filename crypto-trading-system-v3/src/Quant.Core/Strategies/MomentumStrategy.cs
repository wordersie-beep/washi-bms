using System;
using System.Collections.Generic;
using Quant.Core.Features;
using Quant.Core.Numerics;
using Quant.Core.Primitives;

namespace Quant.Core.Strategies;

/// <summary>
/// Trades confirmed ACCELERATION, not mere direction (spec section 13).
///
/// The distinction from trend following is the second derivative. Trend following asks "is
/// price going up?"; this asks "is it going up faster than it was, and is participation
/// confirming?". Without the volume requirement this degenerates into a slower, worse
/// trend follower with an extra name.
/// </summary>
public sealed class MomentumStrategy : StrategyBase
{
    public override string Name => "Momentum";
    public override StrategyKind Kind => StrategyKind.Momentum;

    public override IReadOnlyDictionary<MarketRegime, double> RegimeFit => Fit(
        (MarketRegime.TrendUp, 0.85),
        (MarketRegime.TrendDown, 0.85),
        (MarketRegime.Breakout, 0.90),
        (MarketRegime.Euphoria, 0.45),
        (MarketRegime.HighVolatility, 0.35),
        (MarketRegime.Range, 0.05),
        (MarketRegime.Chop, 0.0),
        (MarketRegime.Panic, 0.0),
        (MarketRegime.LiquidityStress, 0.0));

    public override IReadOnlyCollection<FeatureFamily> Families =>
        Uses(FeatureFamily.Momentum, FeatureFamily.Volume, FeatureFamily.TrendDirection);

    protected override StrategySignal EvaluateCore(StrategyContext ctx)
    {
        FeatureVector f = ctx.Features;

        // Direction comes from the momentum reading itself, not from the trend label.
        Side side = f.MacdHistogramInAtr > 0 ? Side.Long : f.MacdHistogramInAtr < 0 ? Side.Short : Side.None;
        if (side == Side.None) return StrategySignal.Neutral(Name, "no momentum");

        double sign = side == Side.Long ? 1 : -1;

        // Acceleration: the histogram must be growing in the signal's direction. A shrinking
        // histogram is decelerating momentum, which is a reason to exit, never to enter.
        double acceleration = sign * f.MacdHistogramDeltaInAtr;
        if (acceleration <= 0)
        {
            return StrategySignal.Neutral(Name, "momentum decelerating");
        }

        double accelerationScore = MathUtil.LinearScale(acceleration, 0.0, 0.25);
        double magnitude = MathUtil.LinearScale(Math.Abs(f.MacdHistogramInAtr), 0.05, 0.6);

        // Participation must confirm. Price moving on no volume is the signature of a thin
        // book about to snap back.
        double participation = MathUtil.LinearScale(f.VolumePercentile, 0.45, 0.85);
        if (participation < 0.10)
        {
            return StrategySignal.Neutral(Name, $"volume percentile {f.VolumePercentile:P0} does not confirm");
        }

        double recentMove = MathUtil.LinearScale(sign * f.Return5 / Math.Max(f.AtrFraction, 1e-9), 0.5, 4.0);
        double trendAgreement = MathUtil.Clamp01(0.5 + (sign * f.TrendStrength * 0.5));

        // Refuse to buy a vertical bar. By the time a 3-ATR candle has printed, the move the
        // signal is describing has already happened and what remains is the give-back.
        if (f.BarRangeInAtr > ctx.Config.Strategy.FomoBarRangeInAtr)
        {
            return StrategySignal.Neutral(Name, $"bar range {f.BarRangeInAtr:F1} ATR - waiting for a pullback");
        }

        double confidence = Combine(accelerationScore, magnitude, participation, recentMove, trendAgreement);

        return Signal(this, side, confidence, f.Price,
            $"macd {f.MacdHistogramInAtr:F2} accel {acceleration:F2} volPct {f.VolumePercentile:P0}",
            stopInAtr: ctx.Config.Exit.AtrStopMultiple);
    }
}
