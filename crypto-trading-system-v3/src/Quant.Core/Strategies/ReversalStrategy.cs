using System;
using System.Collections.Generic;
using Quant.Core.Features;
using Quant.Core.Numerics;
using Quant.Core.Primitives;

namespace Quant.Core.Strategies;

/// <summary>
/// Trades exhaustion reversals — and demands several independent confirmations before it
/// will (spec section 13).
///
/// This is the most dangerous strategy in the ensemble, because it is the only one that
/// deliberately trades AGAINST the prevailing move, and because being early and being wrong
/// look identical at the moment of entry. Four conditions are therefore all required rather
/// than scored: extreme extension, an exhausted oscillator, decelerating momentum, and a
/// rejection bar. Any one of them alone is a coin flip with a good story attached.
/// </summary>
public sealed class ReversalStrategy : StrategyBase
{
    /// <summary>Confirmations required before the strategy will produce a signal at all.</summary>
    private const int RequiredConfirmations = 4;

    public override string Name => "Reversal";
    public override StrategyKind Kind => StrategyKind.Reversal;

    public override IReadOnlyDictionary<MarketRegime, double> RegimeFit => Fit(
        (MarketRegime.Euphoria, 0.95),
        (MarketRegime.Panic, 0.55),
        (MarketRegime.HighVolatility, 0.55),
        (MarketRegime.Range, 0.45),
        (MarketRegime.TrendUp, 0.25),
        (MarketRegime.TrendDown, 0.25),
        (MarketRegime.Breakout, 0.15),
        (MarketRegime.Chop, 0.0),
        (MarketRegime.LiquidityStress, 0.0));

    public override IReadOnlyCollection<FeatureFamily> Families =>
        Uses(FeatureFamily.Momentum, FeatureFamily.RangePosition, FeatureFamily.MarketStructure, FeatureFamily.Volume);

    protected override StrategySignal EvaluateCore(StrategyContext ctx)
    {
        FeatureVector f = ctx.Features;

        // Fade the direction of the extension.
        Side side = f.DistanceToEmaFastInAtr > 0 ? Side.Short
                  : f.DistanceToEmaFastInAtr < 0 ? Side.Long
                  : Side.None;
        if (side == Side.None) return StrategySignal.Neutral(Name, "price not extended");

        double sign = side == Side.Long ? 1 : -1;
        double extension = Math.Abs(f.DistanceToEmaFastInAtr);

        int confirmations = 0;
        var evidence = new List<string>();

        // 1. Extreme extension from the mean.
        double extensionScore = MathUtil.LinearScale(extension, 2.0, 5.0);
        if (extensionScore > 0.25) { confirmations++; evidence.Add($"extended {extension:F1} ATR"); }

        // 2. Oscillator exhaustion.
        double oscillator = side == Side.Short
            ? MathUtil.LinearScale(f.Rsi, 72, 88)
            : MathUtil.LinearScale(28 - f.Rsi, 0, 16);
        if (oscillator > 0.25) { confirmations++; evidence.Add($"rsi {f.Rsi:F0}"); }

        // 3. Momentum decelerating in the direction of the extension.
        double deceleration = sign * f.MacdHistogramDeltaInAtr;
        double decelerationScore = MathUtil.LinearScale(deceleration, 0.0, 0.15);
        if (decelerationScore > 0.20) { confirmations++; evidence.Add("momentum fading"); }

        // 4. A rejection bar at the extreme.
        double rejection = side == Side.Short
            ? MathUtil.LinearScale(f.UpperWickFraction, 0.25, 0.60)
            : MathUtil.LinearScale(f.LowerWickFraction, 0.25, 0.60);
        if (rejection > 0.25) { confirmations++; evidence.Add("rejection wick"); }

        // 5. Climactic volume, treated as supporting rather than required.
        double climax = MathUtil.LinearScale(f.VolumeZScore, 1.5, 4.0);
        if (climax > 0.30) { evidence.Add($"volume z {f.VolumeZScore:F1}"); }

        if (confirmations < RequiredConfirmations)
        {
            return StrategySignal.Neutral(Name, $"only {confirmations}/{RequiredConfirmations} confirmations ({string.Join(", ", evidence)})");
        }

        double confidence = Combine(extensionScore, oscillator, decelerationScore, rejection, 0.5 + (0.5 * climax));

        // Counter-trend trades are capped below the ensemble's trend-following strategies no
        // matter how good the setup looks. Conviction against a live trend is not a virtue.
        confidence = Math.Min(confidence, 0.75);

        double invalidation = side == Side.Long
            ? ctx.Bar.Low - (ctx.Config.Exit.StructureStopBufferAtr * f.Atr)
            : ctx.Bar.High + (ctx.Config.Exit.StructureStopBufferAtr * f.Atr);

        return Signal(this, side, confidence, f.Price,
            $"reversal: {string.Join(", ", evidence)}",
            invalidation: invalidation,
            targetR: 1.5);
    }
}
