using System;
using System.Collections.Generic;
using Quant.Core.Primitives;

namespace Quant.Core.Features;

/// <summary>
/// The normalised feature set (spec sections 10-11).
///
/// Two invariants hold for every field here:
///
///   1. NO FUTURE INFORMATION. Every value derives from bars that have closed and quotes
///      that have arrived. This is enforced structurally upstream: features are built from
///      <c>TimeframeSeries</c>, which only ever advances on a closed bar.
///
///   2. COMPARABLE ACROSS INSTRUMENTS AND ACROSS TIME. Raw prices and raw ATRs are not
///      comparable between BTC at 60,000 and XRP at 0.5, nor between a calm week and a
///      violent one. Distances are therefore in ATR units, levels are percentile ranks or
///      z-scores, and rates are fractions. A model trained on one instrument has at least a
///      chance of meaning something on another.
/// </summary>
public sealed class FeatureVector
{
    public DateTime TimeUtc { get; init; }
    public string SymbolName { get; init; }

    // --- Price and returns ----------------------------------------------------------
    public double Price { get; init; }

    /// <summary>Log return of the last closed bar.</summary>
    public double Return1 { get; init; }

    /// <summary>Cumulative log return over the last 5 closed bars.</summary>
    public double Return5 { get; init; }

    /// <summary>Cumulative log return over the last 20 closed bars.</summary>
    public double Return20 { get; init; }

    /// <summary>Last bar's return in ATR units — the same move judged against current volatility.</summary>
    public double Return1InAtr { get; init; }

    // --- Volatility -----------------------------------------------------------------
    public double Atr { get; init; }

    /// <summary>ATR as a fraction of price.</summary>
    public double AtrFraction { get; init; }

    /// <summary>Percentile rank of ATR against its own recent history, 0..1.</summary>
    public double AtrPercentile { get; init; }

    public double AtrZScore { get; init; }

    /// <summary>Realised (close-to-close) volatility per bar.</summary>
    public double RealizedVolatility { get; init; }

    /// <summary>
    /// Ratio of ATR to realised volatility scaled to price. Above 1 means range is being
    /// made outside the closes — gaps and wicks — which is a different market than a
    /// smoothly trending one of the same nominal volatility.
    /// </summary>
    public double RangeToCloseVolRatio { get; init; }

    /// <summary>Change in ATR percentile since the previous bar. Positive is expansion.</summary>
    public double VolatilityExpansion { get; init; }

    public VolatilityBucket VolatilityBucket { get; init; }

    // --- Trend ----------------------------------------------------------------------
    public double Adx { get; init; }
    public double PlusDi { get; init; }
    public double MinusDi { get; init; }

    /// <summary>Price minus fast EMA, in ATR units.</summary>
    public double DistanceToEmaFastInAtr { get; init; }

    /// <summary>Price minus slow EMA, in ATR units.</summary>
    public double DistanceToEmaSlowInAtr { get; init; }

    /// <summary>Price minus long trend EMA, in ATR units.</summary>
    public double DistanceToEmaTrendInAtr { get; init; }

    /// <summary>Fast EMA slope per bar, in ATR units.</summary>
    public double EmaFastSlopeInAtr { get; init; }

    /// <summary>Slow EMA slope per bar, in ATR units.</summary>
    public double EmaSlowSlopeInAtr { get; init; }

    /// <summary>+1 fast above slow above trend, -1 the inverse, 0 tangled.</summary>
    public double EmaStackScore { get; init; }

    /// <summary>Regression slope per bar, in ATR units.</summary>
    public double RegressionSlopeInAtr { get; init; }

    /// <summary>Regression goodness of fit, 0..1. A steep slope with a poor fit is not a trend.</summary>
    public double RegressionFit { get; init; }

    /// <summary>
    /// Composite trend strength in -1..+1, combining ADX, the DI spread, EMA stacking and
    /// regression fit. Signed by direction.
    /// </summary>
    public double TrendStrength { get; init; }

    // --- Oscillators ----------------------------------------------------------------
    public double Rsi { get; init; }

    /// <summary>RSI re-centred to -1..+1 so it sits on the same scale as the other features.</summary>
    public double RsiNormalized { get; init; }

    /// <summary>MACD histogram normalised by ATR.</summary>
    public double MacdHistogramInAtr { get; init; }

    /// <summary>Change in the MACD histogram, in ATR units. This is the acceleration term.</summary>
    public double MacdHistogramDeltaInAtr { get; init; }

    // --- Range / bands --------------------------------------------------------------
    public double BollingerWidth { get; init; }
    public double BollingerWidthPercentile { get; init; }

    /// <summary>Position inside the Bollinger bands: 0 at the lower band, 1 at the upper.</summary>
    public double BollingerPercentB { get; init; }

    /// <summary>Position inside the Donchian channel: 0 at the low, 1 at the high.</summary>
    public double DonchianPosition { get; init; }

    /// <summary>Donchian width as a fraction of the mid price.</summary>
    public double DonchianWidth { get; init; }

    // --- Volume ---------------------------------------------------------------------
    public double VolumePercentile { get; init; }
    public double VolumeZScore { get; init; }
    public double VolumeAcceleration { get; init; }

    // --- Structure ------------------------------------------------------------------
    /// <summary>-1..+1 from the confirmed swing sequence.</summary>
    public double StructureScore { get; init; }

    /// <summary>Distance up to the nearest confirmed swing high, in ATR.</summary>
    public double DistanceToResistanceInAtr { get; init; }

    /// <summary>Distance down to the nearest confirmed swing low, in ATR.</summary>
    public double DistanceToSupportInAtr { get; init; }

    /// <summary>Bars since the last confirmed swing of either kind, capped.</summary>
    public double BarsSinceSwing { get; init; }

    /// <summary>Last bar's body as a fraction of its range.</summary>
    public double BodyFraction { get; init; }

    /// <summary>Last bar's upper wick as a fraction of its range.</summary>
    public double UpperWickFraction { get; init; }

    /// <summary>Last bar's lower wick as a fraction of its range.</summary>
    public double LowerWickFraction { get; init; }

    /// <summary>Last bar's range in ATR units. Large values are the FOMO-filter trigger.</summary>
    public double BarRangeInAtr { get; init; }

    // --- Cost / microstructure ------------------------------------------------------
    public double Spread { get; init; }
    public double SpreadPercentile { get; init; }

    /// <summary>Spread divided by ATR — the only form in which spread is decision-relevant.</summary>
    public double SpreadToAtr { get; init; }

    public double TickVelocityZScore { get; init; }
    public double TickInterArrivalZScore { get; init; }

    // --- Higher timeframe context ---------------------------------------------------
    public double ContextTrendStrength { get; init; }
    public double ContextAdx { get; init; }
    public double ContextDistanceToEmaSlowInAtr { get; init; }
    public double ContextStructureScore { get; init; }

    /// <summary>+1 when the signal and context timeframes agree on direction, -1 when they conflict.</summary>
    public double TimeframeAgreement { get; init; }

    // --- Cross-asset ----------------------------------------------------------------
    /// <summary>Benchmark (BTC) trend strength, -1..+1. 0 when unavailable.</summary>
    public double BenchmarkTrendStrength { get; init; }

    /// <summary>Benchmark ATR percentile, 0..1. 0.5 when unavailable.</summary>
    public double BenchmarkVolatilityPercentile { get; init; }

    /// <summary>Benchmark momentum over 20 bars, in ATR units.</summary>
    public double BenchmarkMomentumInAtr { get; init; }

    /// <summary>Rolling return correlation with the benchmark, -1..+1.</summary>
    public double CorrelationToBenchmark { get; init; }

    /// <summary>Mean absolute correlation to every other traded symbol, 0..1.</summary>
    public double MeanPortfolioCorrelation { get; init; }

    // --- Calendar -------------------------------------------------------------------
    public SessionKind Session { get; init; }
    public bool IsWeekend { get; init; }
    public int HourUtc { get; init; }
    public DayOfWeek DayOfWeek { get; init; }

    /// <summary>
    /// Exports the numeric features by name.
    ///
    /// This exists so a future model layer can be trained on exactly the features the live
    /// system computes, from the live system's own code path. Train-serve skew is the most
    /// common way a machine-learning trading model that backtests well fails in production;
    /// the only reliable fix is to have one implementation, and this is it.
    /// </summary>
    public IReadOnlyDictionary<string, double> ToNumericMap() => new Dictionary<string, double>
    {
        { "ret1", Return1 },
        { "ret5", Return5 },
        { "ret20", Return20 },
        { "ret1_atr", Return1InAtr },
        { "atr_frac", AtrFraction },
        { "atr_pct", AtrPercentile },
        { "atr_z", AtrZScore },
        { "realized_vol", RealizedVolatility },
        { "range_close_vol_ratio", RangeToCloseVolRatio },
        { "vol_expansion", VolatilityExpansion },
        { "adx", Adx / 100.0 },
        { "di_spread", (PlusDi - MinusDi) / 100.0 },
        { "dist_ema_fast_atr", DistanceToEmaFastInAtr },
        { "dist_ema_slow_atr", DistanceToEmaSlowInAtr },
        { "dist_ema_trend_atr", DistanceToEmaTrendInAtr },
        { "ema_fast_slope_atr", EmaFastSlopeInAtr },
        { "ema_slow_slope_atr", EmaSlowSlopeInAtr },
        { "ema_stack", EmaStackScore },
        { "reg_slope_atr", RegressionSlopeInAtr },
        { "reg_fit", RegressionFit },
        { "trend_strength", TrendStrength },
        { "rsi_norm", RsiNormalized },
        { "macd_hist_atr", MacdHistogramInAtr },
        { "macd_hist_delta_atr", MacdHistogramDeltaInAtr },
        { "bb_width", BollingerWidth },
        { "bb_width_pct", BollingerWidthPercentile },
        { "bb_percent_b", BollingerPercentB },
        { "donchian_pos", DonchianPosition },
        { "donchian_width", DonchianWidth },
        { "volume_pct", VolumePercentile },
        { "volume_z", VolumeZScore },
        { "volume_accel", VolumeAcceleration },
        { "structure", StructureScore },
        { "dist_resistance_atr", DistanceToResistanceInAtr },
        { "dist_support_atr", DistanceToSupportInAtr },
        { "bars_since_swing", BarsSinceSwing },
        { "body_frac", BodyFraction },
        { "upper_wick_frac", UpperWickFraction },
        { "lower_wick_frac", LowerWickFraction },
        { "bar_range_atr", BarRangeInAtr },
        { "spread_pct", SpreadPercentile },
        { "spread_atr", SpreadToAtr },
        { "tick_velocity_z", TickVelocityZScore },
        { "tick_interarrival_z", TickInterArrivalZScore },
        { "ctx_trend_strength", ContextTrendStrength },
        { "ctx_adx", ContextAdx / 100.0 },
        { "ctx_dist_ema_slow_atr", ContextDistanceToEmaSlowInAtr },
        { "ctx_structure", ContextStructureScore },
        { "tf_agreement", TimeframeAgreement },
        { "bench_trend", BenchmarkTrendStrength },
        { "bench_vol_pct", BenchmarkVolatilityPercentile },
        { "bench_momentum_atr", BenchmarkMomentumInAtr },
        { "corr_benchmark", CorrelationToBenchmark },
        { "corr_portfolio_mean", MeanPortfolioCorrelation },
        { "session", (double)Session / 4.0 },
        { "is_weekend", IsWeekend ? 1.0 : 0.0 },
        { "hour_sin", Math.Sin(2 * Math.PI * HourUtc / 24.0) },
        { "hour_cos", Math.Cos(2 * Math.PI * HourUtc / 24.0) },
        { "dow_sin", Math.Sin(2 * Math.PI * (int)DayOfWeek / 7.0) },
        { "dow_cos", Math.Cos(2 * Math.PI * (int)DayOfWeek / 7.0) },
    };
}
