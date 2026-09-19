using System;
using Quant.Core.Config;
using Quant.Core.Data;
using Quant.Core.Indicators;
using Quant.Core.Numerics;
using Quant.Core.Primitives;

namespace Quant.Core.Features;

/// <summary>
/// Turns a <see cref="SymbolDataSet"/> into a normalised <see cref="FeatureVector"/>.
///
/// The engine is stateless apart from one previous-bar memory used for the volatility
/// expansion delta, which keeps it trivially testable and free of hidden coupling.
/// </summary>
public sealed class FeatureEngine
{
    private readonly DataConfig _config;
    private double _previousAtrPercentile = double.NaN;

    public FeatureEngine(DataConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// Builds the feature vector for the most recently closed signal bar.
    /// Returns null when the symbol does not yet have enough history — callers treat a null
    /// feature vector as an unconditional no-trade, never as neutral features.
    /// </summary>
    public FeatureVector Build(
        DateTime nowUtc,
        SymbolDataSet data,
        GlobalMarketContext global,
        double correlationToBenchmark,
        double meanPortfolioCorrelation)
    {
        if (data == null || !data.IsReady) return null;

        TimeframeSeries s = data.Signal;
        TimeframeSeries ctx = data.Context;
        if (s.Bars.Count < 21) return null;

        Candle last = s.Last;
        double price = last.Close;
        double atr = s.Atr.Value;
        if (atr <= 0 || price <= 0) return null;

        double atrPercentile = s.AtrPercentile;
        double volExpansion = double.IsNaN(_previousAtrPercentile) ? 0 : atrPercentile - _previousAtrPercentile;
        _previousAtrPercentile = atrPercentile;

        double trendStrength = ComputeTrendStrength(s);
        double contextTrend = ComputeTrendStrength(ctx);

        double realizedVol = s.RealizedVol.Value;
        // Realised volatility is a per-bar log-return sigma; ATR is a price range. Scaling
        // ATR to a fraction of price puts them on the same footing.
        double rangeToCloseRatio = MathUtil.SafeDiv(atr / price, realizedVol, 1.0);

        global = global ?? GlobalMarketContext.Unavailable;

        return new FeatureVector
        {
            TimeUtc = nowUtc,
            SymbolName = data.SymbolName,
            Price = price,

            Return1 = LogReturn(s, 1),
            Return5 = LogReturn(s, 5),
            Return20 = LogReturn(s, 20),
            Return1InAtr = MathUtil.Clamp((last.Close - last.Open) / atr, -20, 20),

            Atr = atr,
            AtrFraction = atr / price,
            AtrPercentile = atrPercentile,
            AtrZScore = s.AtrZScore,
            RealizedVolatility = realizedVol,
            RangeToCloseVolRatio = MathUtil.Clamp(rangeToCloseRatio, 0, 20),
            VolatilityExpansion = volExpansion,
            VolatilityBucket = Bucket(atrPercentile),

            Adx = s.Adx.Value,
            PlusDi = s.Adx.PlusDi,
            MinusDi = s.Adx.MinusDi,

            DistanceToEmaFastInAtr = s.DistanceInAtr(price, s.EmaFast.Value),
            DistanceToEmaSlowInAtr = s.DistanceInAtr(price, s.EmaSlow.Value),
            DistanceToEmaTrendInAtr = s.EmaTrend.IsReady ? s.DistanceInAtr(price, s.EmaTrend.Value) : 0,
            EmaFastSlopeInAtr = s.EmaSlopeInAtr(s.EmaFast),
            EmaSlowSlopeInAtr = s.EmaSlopeInAtr(s.EmaSlow),
            EmaStackScore = EmaStack(s),

            RegressionSlopeInAtr = MathUtil.Clamp(MathUtil.SafeDiv(s.Slope.Value, atr), -10, 10),
            RegressionFit = s.Slope.RSquared,
            TrendStrength = trendStrength,

            Rsi = s.Rsi.Value,
            RsiNormalized = MathUtil.Clamp((s.Rsi.Value - 50.0) / 50.0, -1, 1),
            MacdHistogramInAtr = MathUtil.Clamp(MathUtil.SafeDiv(s.Macd.Value, atr), -10, 10),
            MacdHistogramDeltaInAtr = MathUtil.Clamp(MathUtil.SafeDiv(s.Macd.Value - s.Macd.PreviousHistogram, atr), -10, 10),

            BollingerWidth = s.Bollinger.Value,
            BollingerWidthPercentile = s.BollingerWidthPercentile,
            BollingerPercentB = s.Bollinger.PercentB(price),
            DonchianPosition = s.Donchian.PositionOf(price),
            DonchianWidth = s.Donchian.Value,

            VolumePercentile = s.VolumePercentile,
            VolumeZScore = s.VolumeZScore,
            VolumeAcceleration = s.VolumeAcceleration,

            StructureScore = s.Structure.StructureScore(),
            DistanceToResistanceInAtr = s.Structure.DistanceToResistanceInAtr(price, atr),
            DistanceToSupportInAtr = s.Structure.DistanceToSupportInAtr(price, atr),
            BarsSinceSwing = MathUtil.Clamp(s.Structure.BarsSinceLastSwing(), 0, 200),
            BodyFraction = last.BodyFraction,
            UpperWickFraction = last.UpperWickFraction,
            LowerWickFraction = last.LowerWickFraction,
            BarRangeInAtr = MathUtil.Clamp(last.Range / atr, 0, 50),

            Spread = data.Spread.Current,
            SpreadPercentile = data.Spread.Percentile,
            SpreadToAtr = MathUtil.Clamp(data.Spread.RelativeToAtr(atr), 0, 10),
            TickVelocityZScore = data.Ticks.VelocityZScore,
            TickInterArrivalZScore = data.Ticks.InterArrivalZScore,

            ContextTrendStrength = contextTrend,
            ContextAdx = ctx.Adx.Value,
            ContextDistanceToEmaSlowInAtr = ctx.Atr.Value > 0 ? ctx.DistanceInAtr(ctx.Last.Close, ctx.EmaSlow.Value) : 0,
            ContextStructureScore = ctx.Structure.StructureScore(),
            TimeframeAgreement = Agreement(trendStrength, contextTrend),

            BenchmarkTrendStrength = global.TrendStrength,
            BenchmarkVolatilityPercentile = global.VolatilityPercentile,
            BenchmarkMomentumInAtr = global.MomentumInAtr,
            CorrelationToBenchmark = MathUtil.Clamp(correlationToBenchmark, -1, 1),
            MeanPortfolioCorrelation = MathUtil.Clamp01(meanPortfolioCorrelation),

            Session = SessionClassifier.Classify(nowUtc),
            IsWeekend = SessionClassifier.IsWeekend(nowUtc),
            HourUtc = nowUtc.Hour,
            DayOfWeek = nowUtc.DayOfWeek,
        };
    }

    /// <summary>Cumulative log return over the last <paramref name="bars"/> closed bars.</summary>
    private static double LogReturn(TimeframeSeries s, int bars)
    {
        if (s.Bars.Count <= bars) return 0;
        double now = s.Bars[0].Close;
        double then = s.Bars[bars].Close;
        if (now <= 0 || then <= 0) return 0;
        return MathUtil.Clamp(Math.Log(now / then), -2, 2);
    }

    /// <summary>+1 for a bullish EMA stack, -1 for a bearish one, 0 when tangled.</summary>
    private static double EmaStack(TimeframeSeries s)
    {
        if (!s.EmaSlow.IsReady) return 0;

        double fast = s.EmaFast.Value;
        double slow = s.EmaSlow.Value;
        double trend = s.EmaTrend.IsReady ? s.EmaTrend.Value : slow;

        if (fast > slow && slow > trend) return 1.0;
        if (fast < slow && slow < trend) return -1.0;
        if (fast > slow) return 0.4;
        if (fast < slow) return -0.4;
        return 0;
    }

    /// <summary>
    /// Signed composite trend strength in -1..+1.
    ///
    /// Direction comes from the EMA stack and the DI spread; magnitude from ADX, regression
    /// fit and the DI spread together. Requiring FIT as well as SLOPE is the point: a steep
    /// line through noise is not a trend, and treating it as one is how trend-following
    /// systems lose money in chop.
    /// </summary>
    internal static double ComputeTrendStrength(TimeframeSeries s)
    {
        if (s == null || !s.Adx.IsReady || !s.EmaSlow.IsReady) return 0;

        double adxComponent = MathUtil.LinearScale(s.Adx.Value, 15, 45);
        double diSpread = MathUtil.Clamp((s.Adx.PlusDi - s.Adx.MinusDi) / 40.0, -1, 1);
        double fit = s.Slope.RSquared;
        double stack = EmaStack(s);

        double magnitude = MathUtil.Clamp01((adxComponent * 0.45) + (Math.Abs(diSpread) * 0.30) + (fit * 0.25));

        // Direction is a vote between the DI spread and the EMA stack. When they disagree
        // the magnitude is damped, because a market whose structure and momentum point
        // different ways is by definition not trending cleanly.
        double direction = (Math.Sign(diSpread) * 0.5) + (Math.Sign(stack) * 0.5);
        if (Math.Abs(direction) < 0.25) return 0;

        double agreementDamping = Math.Abs(direction);
        return MathUtil.Clamp(Math.Sign(direction) * magnitude * agreementDamping, -1, 1);
    }

    /// <summary>+1 when two trend readings agree in sign, -1 when they oppose, scaled by strength.</summary>
    private static double Agreement(double a, double b)
    {
        if (Math.Abs(a) < 0.05 || Math.Abs(b) < 0.05) return 0;
        double magnitude = Math.Min(Math.Abs(a), Math.Abs(b));
        return Math.Sign(a) == Math.Sign(b) ? magnitude : -magnitude;
    }

    private static VolatilityBucket Bucket(double atrPercentile)
    {
        if (atrPercentile >= 0.95) return VolatilityBucket.Extreme;
        if (atrPercentile >= 0.75) return VolatilityBucket.High;
        if (atrPercentile >= 0.25) return VolatilityBucket.Normal;
        if (atrPercentile >= 0.05) return VolatilityBucket.Low;
        return VolatilityBucket.VeryLow;
    }

    public void Reset() => _previousAtrPercentile = double.NaN;
}
