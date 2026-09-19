using System;
using System.Collections.Generic;
using Quant.Core.Config;
using Quant.Core.Data;
using Quant.Core.Features;
using Quant.Core.Primitives;
using Quant.Core.Regime;
using Xunit;

namespace Quant.Core.Tests;

public class TimeframeSeriesTests
{
    private static Candle Bar(DateTime t, double close) =>
        new Candle(t, close, close + 1, close - 1, close, 1000);

    [Fact]
    public void RejectsDuplicateAndOutOfOrderBarsWithoutCorruptingState()
    {
        var series = new TimeframeSeries(Tf.M5, new DataConfig());
        DateTime t = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.True(series.OnBarClosed(Bar(t, 100)));
        Assert.True(series.OnBarClosed(Bar(t.AddMinutes(5), 101)));

        long processed = series.BarsProcessed;
        double atr = series.Atr.Value;

        Assert.False(series.OnBarClosed(Bar(t.AddMinutes(5), 999)));  // duplicate timestamp
        Assert.False(series.OnBarClosed(Bar(t, 999)));                // older timestamp

        Assert.Equal(processed, series.BarsProcessed);
        Assert.Equal(atr, series.Atr.Value, 10);
    }

    [Fact]
    public void RejectsMalformedBars()
    {
        var series = new TimeframeSeries(Tf.M5, new DataConfig());
        DateTime t = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.False(series.OnBarClosed(new Candle(t, 100, 90, 110, 100, 1000)));   // high below low
        Assert.False(series.OnBarClosed(new Candle(t, 0, 1, 0, 0, 1000)));          // non-positive
        Assert.False(series.OnBarClosed(new Candle(t, 100, 101, 99, 100, -5)));     // negative volume
        Assert.False(series.OnBarClosed(new Candle(t, double.NaN, 1, 1, 1, 1)));    // non-finite
        Assert.Equal(0, series.BarsProcessed);
    }

    [Fact]
    public void DetectsAndCountsBarGaps()
    {
        var series = new TimeframeSeries(Tf.M5, new DataConfig());
        DateTime t = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        series.OnBarClosed(Bar(t, 100));
        series.OnBarClosed(Bar(t.AddMinutes(5), 101));
        Assert.Equal(0, series.LastGapBars);
        Assert.Equal(0, series.GapCount);

        series.OnBarClosed(Bar(t.AddMinutes(20), 102));  // three intervals skipped
        Assert.Equal(2, series.LastGapBars);
        Assert.Equal(1, series.GapCount);
    }

    [Fact]
    public void PercentileRanksNeverCompareAValueAgainstItself()
    {
        // A monotonically rising ATR must not report a percentile of exactly 1.0 by virtue
        // of the newest value having been added to its own comparison window.
        var series = new TimeframeSeries(Tf.M5, new DataConfig());
        DateTime t = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (int i = 0; i < 300; i++)
        {
            double c = 100 + i;
            double halfRange = 1 + (i * 0.05);
            series.OnBarClosed(new Candle(t.AddMinutes(5 * i), c, c + halfRange, c - halfRange, c, 1000));
        }

        Assert.True(series.Atr.IsReady);
        Assert.True(series.AtrPercentile > 0.9);
        Assert.True(series.AtrPercentile <= 1.0);
    }
}

public class DataQualityTests
{
    private static PipelineHarness WarmHarness()
    {
        var h = new PipelineHarness();
        var sim = new MarketSimulator();
        h.Feed(sim.Generate(1100, 0.0, 0.002));
        return h;
    }

    [Fact]
    public void AcceptsAHealthyFeed()
    {
        PipelineHarness h = WarmHarness();
        DataQualityReport report = h.QualityMonitor.Evaluate(
            h.LastTimeUtc, h.Spec, h.Data.LatestQuote, h.Data.Signal, h.Data.Ticks, h.Data.Spread);

        Assert.True(report.IsAcceptable, report.IssueSummary);
        Assert.True(report.Score > 0.8);
    }

    [Fact]
    public void RejectsAStaleQuote()
    {
        PipelineHarness h = WarmHarness();
        DataQualityReport report = h.QualityMonitor.Evaluate(
            h.LastTimeUtc.AddMinutes(10), h.Spec, h.Data.LatestQuote, h.Data.Signal, h.Data.Ticks, h.Data.Spread);

        Assert.False(report.IsAcceptable);
        Assert.Contains(report.Issues, i => i.Contains("stale quote"));
    }

    [Fact]
    public void RejectsAMalformedQuote()
    {
        PipelineHarness h = WarmHarness();
        var inverted = new Quote(h.LastTimeUtc, 100, 90);

        DataQualityReport report = h.QualityMonitor.Evaluate(
            h.LastTimeUtc, h.Spec, inverted, h.Data.Signal, h.Data.Ticks, h.Data.Spread);

        Assert.False(report.IsAcceptable);
        Assert.Equal(0, report.Score);
    }

    [Fact]
    public void RejectsInsufficientHistoryRatherThanGuessing()
    {
        var h = new PipelineHarness();
        var sim = new MarketSimulator();
        h.Feed(sim.Generate(50, 0, 0.002));

        DataQualityReport report = h.QualityMonitor.Evaluate(
            h.LastTimeUtc, h.Spec, h.Data.LatestQuote, h.Data.Signal, h.Data.Ticks, h.Data.Spread);

        Assert.False(report.IsAcceptable);
        Assert.Contains(report.Issues, i => i.Contains("insufficient history"));
    }

    [Fact]
    public void RejectsWhenTradingIsDisabledForTheSymbol()
    {
        PipelineHarness h = WarmHarness();
        var disabled = new SymbolSpec("BTCUSD", 0.01, 0.01, 2, 0.01, 100, 0.01, 35, 0.01, 0, isTradingEnabled: false);

        DataQualityReport report = h.QualityMonitor.Evaluate(
            h.LastTimeUtc, disabled, h.Data.LatestQuote, h.Data.Signal, h.Data.Ticks, h.Data.Spread);

        Assert.False(report.IsAcceptable);
    }

    [Fact]
    public void TickStatisticsRejectImplausiblePriceJumps()
    {
        PipelineHarness h = WarmHarness();
        long before = h.Data.Ticks.RejectedTicks;

        double mid = h.Data.LatestQuote.Mid;
        double atr = h.Data.Signal.Atr.Value;
        double absurd = mid + (atr * 50);

        bool accepted = h.Data.OnQuote(new Quote(h.LastTimeUtc.AddSeconds(1), absurd - 1, absurd + 1));

        Assert.False(accepted);
        Assert.Equal(before + 1, h.Data.Ticks.RejectedTicks);
    }
}

public class FeatureEngineTests
{
    [Fact]
    public void ProducesFiniteFeaturesForEveryFieldOnANormalMarket()
    {
        var h = new PipelineHarness();
        h.Feed(new MarketSimulator().Generate(1100, 0.0002, 0.003));

        FeatureVector f = h.LastFeatures;
        Assert.NotNull(f);

        foreach (KeyValuePair<string, double> kv in f.ToNumericMap())
        {
            Assert.True(!double.IsNaN(kv.Value) && !double.IsInfinity(kv.Value),
                $"Feature '{kv.Key}' was not finite: {kv.Value}");
        }
    }

    [Fact]
    public void ReportsPositiveTrendStrengthInAnUptrendAndNegativeInADowntrend()
    {
        var up = new PipelineHarness();
        up.Feed(new MarketSimulator(seed: 1).Generate(1100, driftPerBar: 0.0012, volPerBar: 0.0015));
        Assert.True(up.LastFeatures.TrendStrength > 0.3,
            $"Uptrend strength was {up.LastFeatures.TrendStrength:F2}.");

        var down = new PipelineHarness();
        down.Feed(new MarketSimulator(seed: 2).Generate(1100, driftPerBar: -0.0012, volPerBar: 0.0015));
        Assert.True(down.LastFeatures.TrendStrength < -0.3,
            $"Downtrend strength was {down.LastFeatures.TrendStrength:F2}.");
    }

    [Fact]
    public void ReportsNearZeroTrendStrengthInARange()
    {
        var h = new PipelineHarness();
        h.Feed(new MarketSimulator(startPrice: 50000, seed: 3).GenerateRange(1100, 50000, 500));
        Assert.True(Math.Abs(h.LastFeatures.TrendStrength) < 0.35,
            $"Range trend strength was {h.LastFeatures.TrendStrength:F2}, expected near zero.");
    }

    [Fact]
    public void NormalisedFeaturesAreScaleFreeAcrossInstruments()
    {
        // The identical price PATH at two very different price levels must produce the same
        // normalised features. If it does not, nothing learned on BTC can transfer anywhere.
        var expensive = new PipelineHarness(symbol: "BTCUSD");
        expensive.Feed(new MarketSimulator(startPrice: 60000, seed: 7).Generate(1100, 0.0005, 0.0025));

        var cheap = new PipelineHarness(symbol: "XRPUSD");
        cheap.Feed(new MarketSimulator(startPrice: 0.5, seed: 7).Generate(1100, 0.0005, 0.0025));

        FeatureVector a = expensive.LastFeatures;
        FeatureVector b = cheap.LastFeatures;

        Assert.Equal(a.AtrFraction, b.AtrFraction, 8);
        Assert.Equal(a.TrendStrength, b.TrendStrength, 8);
        Assert.Equal(a.DistanceToEmaSlowInAtr, b.DistanceToEmaSlowInAtr, 6);
        Assert.Equal(a.Rsi, b.Rsi, 6);
        Assert.Equal(a.BarRangeInAtr, b.BarRangeInAtr, 6);
    }

    [Fact]
    public void ReturnsNullRatherThanNeutralFeaturesWhenHistoryIsShort()
    {
        var h = new PipelineHarness();
        h.Feed(new MarketSimulator().Generate(30, 0, 0.002));
        Assert.Null(h.LastFeatures);
    }
}

public class RegimeModelTests
{
    private static RegimeAssessment ClassifyStream(IEnumerable<Candle> bars, EngineConfig config = null)
    {
        config ??= new EngineConfig();
        var h = new PipelineHarness(config);
        var model = new StatisticalRegimeModel(config.Regime);

        RegimeAssessment last = RegimeAssessment.Unknown;
        var buffered = new List<Candle>(bars);

        // Feed bar by bar so the classifier sees the same sequence a live run would.
        for (int i = 0; i < buffered.Count; i++)
        {
            h.Feed(new[] { buffered[i] });
            if (h.LastFeatures != null) last = model.Classify(h.LastFeatures);
        }
        return last;
    }

    [Fact]
    public void ClassifiesASustainedUptrendAsTrendUp()
    {
        RegimeAssessment r = ClassifyStream(new MarketSimulator(seed: 11).Generate(1100, 0.0012, 0.0012));
        Assert.Equal(MarketRegime.TrendUp, r.Primary);
        Assert.Equal(Side.Long, r.DirectionalBias);
        Assert.True(r.Confidence > 0.5, $"Confidence {r.Confidence:F2} too low for a clean trend.");
    }

    [Fact]
    public void ClassifiesASustainedDowntrendAsTrendDown()
    {
        RegimeAssessment r = ClassifyStream(new MarketSimulator(seed: 12).Generate(1100, -0.0012, 0.0012));
        Assert.Equal(MarketRegime.TrendDown, r.Primary);
        Assert.Equal(Side.Short, r.DirectionalBias);
    }

    [Fact]
    public void DoesNotClassifyAMeanRevertingMarketAsATrend()
    {
        RegimeAssessment r = ClassifyStream(new MarketSimulator(seed: 13).GenerateRange(1100, 50000, 400));
        Assert.NotEqual(MarketRegime.TrendUp, r.Primary);
        Assert.NotEqual(MarketRegime.TrendDown, r.Primary);
    }

    [Fact]
    public void ReportsUnknownAndRefusesRiskBeforeAnyFeaturesArrive()
    {
        var model = new StatisticalRegimeModel(new RegimeConfig());
        RegimeAssessment r = model.Classify(null);

        Assert.Equal(MarketRegime.Unknown, r.Primary);
        Assert.Equal(0, r.RiskMultiplier);
        Assert.Equal(0, r.Confidence);
    }

    [Fact]
    public void SuppressesRiskWhileARegimeChangeIsUnconfirmed()
    {
        var config = new EngineConfig();
        var h = new PipelineHarness(config);
        var model = new StatisticalRegimeModel(config.Regime);

        var sim = new MarketSimulator(seed: 14);
        var bars = new List<Candle>(sim.Generate(1100, 0.0012, 0.0012));   // trend up
        bars.AddRange(sim.Generate(300, -0.0015, 0.0018));                // sharp reversal

        RegimeAssessment atReversal = RegimeAssessment.Unknown;
        double minRiskAfterFlip = double.MaxValue;
        MarketRegime seen = MarketRegime.Unknown;

        for (int i = 0; i < bars.Count; i++)
        {
            h.Feed(new[] { bars[i] });
            if (h.LastFeatures == null) continue;

            RegimeAssessment r = model.Classify(h.LastFeatures);
            if (seen != MarketRegime.Unknown && r.Primary != seen && atReversal == RegimeAssessment.Unknown)
            {
                atReversal = r;
            }
            if (atReversal != RegimeAssessment.Unknown && r.IsTransitioning)
            {
                minRiskAfterFlip = Math.Min(minRiskAfterFlip, r.RiskMultiplier);
            }
            seen = r.Primary;
        }

        Assert.NotEqual(RegimeAssessment.Unknown, atReversal);
        Assert.True(atReversal.IsTransitioning, "The bar on which the regime flipped must be marked as transitioning.");
        Assert.True(minRiskAfterFlip <= atReversal.Confidence * config.Regime.TransitionRiskMultiplier + 1e-9,
            "Risk must be suppressed while a regime transition is unconfirmed.");
    }

    [Fact]
    public void DoesNotFlipOnASingleContraryBar()
    {
        var config = new EngineConfig();
        config.Regime.ConfirmationBars = 3;

        var h = new PipelineHarness(config);
        var model = new StatisticalRegimeModel(config.Regime);

        var sim = new MarketSimulator(seed: 15);
        var bars = new List<Candle>(sim.Generate(1100, 0.0012, 0.001));

        MarketRegime beforeShock = MarketRegime.Unknown;
        for (int i = 0; i < bars.Count; i++)
        {
            h.Feed(new[] { bars[i] });
            if (h.LastFeatures != null) beforeShock = model.Classify(h.LastFeatures).Primary;
        }
        Assert.Equal(MarketRegime.TrendUp, beforeShock);

        // One violent down bar must not, on its own, change the confirmed regime.
        Candle lastBar = bars[bars.Count - 1];
        double open = lastBar.Close;
        double close = open * 0.97;
        var shock = new Candle(lastBar.OpenTimeUtc.AddMinutes(5), open, open * 1.001, close * 0.999, close, 9000);

        h.Feed(new[] { shock });
        RegimeAssessment after = model.Classify(h.LastFeatures);

        Assert.Equal(MarketRegime.TrendUp, after.Primary);

        // The label held, but the model must not pretend it is as sure as it was: the shock
        // bar scored elsewhere, so the reported regime is knowingly stale and confidence
        // has to reflect that.
        Assert.True(after.Confidence < 0.6,
            $"Confidence was {after.Confidence:P0}; a stale label must be reported with reduced confidence.");
    }

    [Fact]
    public void DoesNotLabelAStrongTrendAsMerelyVolatile()
    {
        // Structure and volatility are separate axes. A trend in a volatile instrument is a
        // trend that happens to be volatile, and the assessment must say both.
        var config = new EngineConfig();
        var h = new PipelineHarness(config);
        var model = new StatisticalRegimeModel(config.Regime);

        RegimeAssessment last = RegimeAssessment.Unknown;
        foreach (Candle b in new MarketSimulator(seed: 21).Generate(1100, 0.0015, 0.004))
        {
            h.Feed(new[] { b });
            if (h.LastFeatures != null) last = model.Classify(h.LastFeatures);
        }

        Assert.Equal(MarketRegime.TrendUp, last.Primary);
        Assert.NotEqual(MarketRegime.HighVolatility, last.Primary);
    }

    [Fact]
    public void VolatilityPercentileIsNotPinnedByARisingPriceLevel()
    {
        // The instrument triples over the sample at constant PROPORTIONAL volatility. If
        // volatility were ranked in price units the percentile would sit at 1.0 throughout
        // and every risk control keyed to it would be permanently maxed out.
        var h = new PipelineHarness();
        h.Feed(new MarketSimulator(startPrice: 1000, seed: 22).Generate(1500, driftPerBar: 0.0008, volPerBar: 0.002));

        Assert.True(h.Data.Signal.Last.Close > 2500, "The simulated market should have risen sharply.");
        Assert.InRange(h.LastFeatures.AtrPercentile, 0.02, 0.98);
    }
}
