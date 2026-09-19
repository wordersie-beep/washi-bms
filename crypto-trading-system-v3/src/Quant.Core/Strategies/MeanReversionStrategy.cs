using System;
using System.Collections.Generic;
using Quant.Core.Features;
using Quant.Core.Numerics;
using Quant.Core.Primitives;

namespace Quant.Core.Strategies;

/// <summary>
/// Fades stretched price back toward the mean — in RANGES ONLY (spec section 13).
///
/// The regime fit is the entire risk control here. Mean reversion has a high win rate and a
/// catastrophic loss distribution: it is right most of the time and ruinous the once it is
/// not, and the once it is not is always a trend. Allowing it to run outside a range is not
/// a tuning choice, it is the standard way this strategy family destroys an account, so
/// every trending and disorderly regime is scored at zero.
/// </summary>
public sealed class MeanReversionStrategy : StrategyBase
{
    public override string Name => "MeanReversion";
    public override StrategyKind Kind => StrategyKind.MeanReversion;

    public override IReadOnlyDictionary<MarketRegime, double> RegimeFit => Fit(
        (MarketRegime.Range, 1.00),
        (MarketRegime.LowVolatility, 0.60),
        // Zero, not "small". Chop looks like a range and is not one; trends are where this
        // strategy goes to die.
        (MarketRegime.Chop, 0.0),
        (MarketRegime.TrendUp, 0.0),
        (MarketRegime.TrendDown, 0.0),
        (MarketRegime.Breakout, 0.0),
        (MarketRegime.HighVolatility, 0.0),
        (MarketRegime.Panic, 0.0),
        (MarketRegime.Euphoria, 0.0),
        (MarketRegime.LiquidityStress, 0.0));

    public override IReadOnlyCollection<FeatureFamily> Families =>
        Uses(FeatureFamily.RangePosition, FeatureFamily.Volatility);

    protected override StrategySignal EvaluateCore(StrategyContext ctx)
    {
        FeatureVector f = ctx.Features;

        // Fade the band the price is pressing against.
        Side side = f.BollingerPercentB >= 0.95 ? Side.Short
                  : f.BollingerPercentB <= 0.05 ? Side.Long
                  : Side.None;
        if (side == Side.None) return StrategySignal.Neutral(Name, "price not at a band extreme");

        double sign = side == Side.Long ? 1 : -1;

        // Refuse to fade a move that is breaking the range rather than testing it. This is
        // the difference between selling the top of a range and standing in front of a
        // breakout.
        double edgePenetration = side == Side.Short
            ? MathUtil.SafeDiv(ctx.Bar.Close - ctx.Signal.Donchian.Upper, f.Atr)
            : MathUtil.SafeDiv(ctx.Signal.Donchian.Lower - ctx.Bar.Close, f.Atr);
        if (edgePenetration > 0.10)
        {
            return StrategySignal.Neutral(Name, $"price {edgePenetration:F2} ATR outside the range - this is a break, not a test");
        }

        // Volume expansion at the edge of a range is what a genuine breakout looks like.
        if (f.VolumePercentile > 0.90 && f.VolatilityExpansion > 0.15)
        {
            return StrategySignal.Neutral(Name, "volume and volatility expanding at the edge - breakout risk");
        }

        double stretch = Math.Abs(f.BollingerPercentB - 0.5) * 2.0;
        // Зеркальные пороги. Асимметричная формула здесь означала бы, что одна сторона
        // проверяет перекупленность, а другая не проверяет ничего: выражение вида
        // LinearScale(70 - Rsi, 0, 14) равно единице при любом RSI ниже 56, то есть
        // практически всегда.
        double oscillatorExtreme = side == Side.Short
            ? MathUtil.LinearScale(f.Rsi, 68, 82)
            : MathUtil.LinearScale(32 - f.Rsi, -14, 14);

        // Вето, а не множитель. Combine — геометрическое среднее шести членов, и обнуление
        // одного из них снижает уверенность лишь вдвое: возврат к средней от RSI 50 остался
        // бы возможным, просто с меньшей уверенностью. Для стратегии, которая торгует ИМЕННО
        // крайность, это не «сигнал послабее», а отсутствие причины входить.
        if (oscillatorExtreme < 0.15)
        {
            return StrategySignal.Neutral(Name, $"осциллятор не в крайности (rsi {f.Rsi:F0})");
        }

        // A range needs two walls. Room back toward the middle is what the trade is paid for.
        double roomToMean = side == Side.Short
            ? MathUtil.SafeDiv(ctx.Bar.Close - ctx.Signal.Bollinger.Middle, f.Atr)
            : MathUtil.SafeDiv(ctx.Signal.Bollinger.Middle - ctx.Bar.Close, f.Atr);
        double roomScore = MathUtil.LinearScale(roomToMean, 0.5, 2.5);
        if (roomScore < 0.15)
        {
            return StrategySignal.Neutral(Name, "insufficient room back to the mean");
        }

        // Rejection at the extreme: a wick pointing the way price came from.
        double rejection = side == Side.Short
            ? MathUtil.LinearScale(f.UpperWickFraction, 0.15, 0.55)
            : MathUtil.LinearScale(f.LowerWickFraction, 0.15, 0.55);

        double trendlessness = 1.0 - MathUtil.Clamp01(Math.Abs(f.TrendStrength));
        double contextCalm = 1.0 - MathUtil.Clamp01(Math.Abs(f.ContextTrendStrength));

        double confidence = Combine(stretch, oscillatorExtreme, roomScore, rejection, trendlessness, contextCalm);

        double invalidation = side == Side.Short
            ? Math.Max(ctx.Bar.High, ctx.Signal.Donchian.Upper) + (ctx.Config.Exit.StructureStopBufferAtr * f.Atr)
            : Math.Min(ctx.Bar.Low, ctx.Signal.Donchian.Lower) - (ctx.Config.Exit.StructureStopBufferAtr * f.Atr);

        // Ranges pay less than trends do, and pretending otherwise turns a high-win-rate
        // strategy into a low-win-rate one by placing the target where price never reaches.
        return Signal(this, side, confidence, f.Price,
            $"fading %B {f.BollingerPercentB:F2}, rsi {f.Rsi:F0}, {roomToMean:F1} ATR to the mean",
            invalidation: invalidation,
            targetR: 1.2);
    }
}
