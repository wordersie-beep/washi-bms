using System;
using System.Collections.Generic;
using System.Linq;
using Quant.Core.Adaptation;
using Quant.Core.Config;
using Quant.Core.Features;
using Quant.Core.Primitives;
using Quant.Core.Regime;
using Quant.Core.Stats;
using Quant.Core.Strategies;
using Xunit;

namespace Quant.Core.Tests;

/// <summary>Builds a live strategy context from a warmed pipeline.</summary>
internal static class ContextBuilder
{
    public static StrategyContext Build(PipelineHarness h, RegimeAssessment regime = null)
    {
        return new StrategyContext(
            h.LastTimeUtc, h.Data, h.LastFeatures,
            regime ?? RegimeAssessment.Unknown,
            GlobalMarketContext.Unavailable, h.Config);
    }

    public static RegimeAssessment Regime(MarketRegime primary, double confidence = 0.8) => new RegimeAssessment
    {
        Primary = primary,
        Runner = MarketRegime.Unknown,
        Confidence = confidence,
        Scores = new Dictionary<MarketRegime, double> { { primary, confidence } },
        RiskMultiplier = confidence,
        BarsInRegime = 20,
    };

    /// <summary>
    /// A trending market with a REALISTIC drift-to-noise ratio.
    ///
    /// Setting drift equal to per-bar volatility produces a near-noiseless ramp -- ADX in the
    /// nineties, no pullbacks, price permanently several ATR above its own fast average.
    /// Nothing that waits for a reasonable entry will ever trade it, so it tests nothing.
    /// Real trends advance with a drift far below their noise and are full of corrections.
    /// </summary>
    public static PipelineHarness Trend(double drift, ulong seed = 31, double vol = 0.0025, int bars = 1100)
    {
        var h = new PipelineHarness();
        h.Feed(new MarketSimulator(seed: seed).Generate(bars, drift, vol));
        return h;
    }

    /// <summary>
    /// Walks a market bar by bar and collects every signal a strategy produced along the
    /// way. Testing only the final bar samples one arbitrary market state; walking the path
    /// samples hundreds, which is the difference between a test and a coincidence.
    /// </summary>
    public static List<(StrategySignal Signal, FeatureVector Features)> WalkAndCollect(
        IStrategy strategy, IEnumerable<Candle> bars, MarketRegime regime, EngineConfig config = null)
    {
        config ??= new EngineConfig();
        var h = new PipelineHarness(config);
        var results = new List<(StrategySignal, FeatureVector)>();

        foreach (Candle bar in bars)
        {
            h.Feed(new[] { bar });
            if (h.LastFeatures == null) continue;

            StrategySignal s = strategy.Evaluate(Build(h, Regime(regime)));
            if (s.IsActionable) results.Add((s, h.LastFeatures));
        }
        return results;
    }

    public static PipelineHarness Range(ulong seed = 41, int bars = 1100)
    {
        var h = new PipelineHarness();
        h.Feed(new MarketSimulator(startPrice: 50000, seed: seed).GenerateRange(bars, 50000, 400));
        return h;
    }
}

public class StrategySpecialisationTests
{
    [Fact]
    public void EveryTradeableRegimeIsCoveredAndNoStrategyClaimsCompetenceEverywhere()
    {
        Assert.Empty(new StrategyRegistry().Validate());
    }

    [Fact]
    public void MeanReversionAbstainsInEveryTrendingOrDisorderlyRegime()
    {
        var strategy = new MeanReversionStrategy();

        foreach (MarketRegime hostile in new[]
        {
            MarketRegime.TrendUp, MarketRegime.TrendDown, MarketRegime.Breakout,
            MarketRegime.Chop, MarketRegime.Panic, MarketRegime.Euphoria,
            MarketRegime.HighVolatility, MarketRegime.LiquidityStress,
        })
        {
            Assert.Equal(0.0, strategy.FitFor(hostile));
        }

        Assert.Equal(1.0, strategy.FitFor(MarketRegime.Range));
    }

    [Fact]
    public void StrategiesReturnNeutralOutsideTheirDeclaredRegimes()
    {
        PipelineHarness h = ContextBuilder.Trend(0.0012);
        StrategyContext chopContext = ContextBuilder.Build(h, ContextBuilder.Regime(MarketRegime.Chop));

        StrategySignal signal = new MeanReversionStrategy().Evaluate(chopContext);

        Assert.False(signal.IsActionable);
        Assert.Contains("unsuited", signal.Rationale);
    }

    [Fact]
    public void AStrategyThatThrowsAbstainsInsteadOfPropagating()
    {
        var thrower = new ThrowingStrategy();
        PipelineHarness h = ContextBuilder.Trend(0.001);

        StrategySignal signal = thrower.Evaluate(ContextBuilder.Build(h, ContextBuilder.Regime(MarketRegime.TrendUp)));

        Assert.False(signal.IsActionable);
        Assert.Contains("evaluation failed", signal.Rationale);
    }

    [Fact]
    public void ANullContextIsHandledWithoutThrowing()
    {
        foreach (IStrategy s in new StrategyRegistry().All)
        {
            StrategySignal signal = s.Evaluate(null);
            Assert.False(signal.IsActionable);
        }
    }

    private sealed class ThrowingStrategy : StrategyBase
    {
        public override string Name => "Thrower";
        public override StrategyKind Kind => StrategyKind.Baseline;
        public override IReadOnlyDictionary<MarketRegime, double> RegimeFit => Fit((MarketRegime.TrendUp, 1.0));
        public override IReadOnlyCollection<FeatureFamily> Families => Uses(FeatureFamily.TrendDirection);
        protected override StrategySignal EvaluateCore(StrategyContext context) => throw new InvalidOperationException("boom");
    }
}

public class StrategyDirectionTests
{
    [Fact]
    public void TrendFollowingGoesLongInAnUptrendAndShortInADowntrend()
    {
        var strategy = new TrendFollowingStrategy();

        var up = ContextBuilder.WalkAndCollect(strategy,
            new MarketSimulator(seed: 3).Generate(1400, 0.0004, 0.0025), MarketRegime.TrendUp);

        var down = ContextBuilder.WalkAndCollect(strategy,
            new MarketSimulator(seed: 4).Generate(1400, -0.0004, 0.0025), MarketRegime.TrendDown);

        Assert.NotEmpty(up);
        Assert.NotEmpty(down);

        // The direction taken must match the trend the strategy itself measured, on every
        // single signal across both paths.
        Assert.All(up, r => Assert.True(
            (r.Signal.Direction == Side.Long && r.Features.TrendStrength > 0) ||
            (r.Signal.Direction == Side.Short && r.Features.TrendStrength < 0),
            $"{r.Signal.Direction} while trend strength was {r.Features.TrendStrength:F2}"));

        Assert.All(down, r => Assert.True(
            (r.Signal.Direction == Side.Long && r.Features.TrendStrength > 0) ||
            (r.Signal.Direction == Side.Short && r.Features.TrendStrength < 0),
            $"{r.Signal.Direction} while trend strength was {r.Features.TrendStrength:F2}"));

        // And an upward drift must produce predominantly long signals, not a coin flip.
        int longs = up.Count(r => r.Signal.Direction == Side.Long);
        Assert.True(longs > up.Count * 0.7,
            $"Only {longs}/{up.Count} signals were long in an uptrend.");

        int shorts = down.Count(r => r.Signal.Direction == Side.Short);
        Assert.True(shorts > down.Count * 0.7,
            $"Only {shorts}/{down.Count} signals were short in a downtrend.");
    }

    [Fact]
    public void TrendFollowingNeverEntersFarExtendedFromTheMean()
    {
        var results = ContextBuilder.WalkAndCollect(new TrendFollowingStrategy(),
            new MarketSimulator(seed: 5).Generate(1400, 0.0004, 0.0025), MarketRegime.TrendUp);

        Assert.NotEmpty(results);
        Assert.All(results, r =>
        {
            double sign = r.Signal.Direction == Side.Long ? 1 : -1;
            double extension = sign * r.Features.DistanceToEmaFastInAtr;
            Assert.True(extension < 3.0, $"Entered {extension:F2} ATR extended from the fast EMA.");
        });
    }

    [Fact]
    public void TrendFollowingRefusesWhenPriceIsFarExtendedFromTheMean()
    {
        // Feed a violent vertical run so the close sits many ATR above the fast EMA.
        var h = new PipelineHarness();
        var sim = new MarketSimulator(seed: 55);
        h.Feed(sim.Generate(1000, 0.0008, 0.001));
        h.Feed(sim.Generate(12, 0.02, 0.0005));

        StrategySignal signal = new TrendFollowingStrategy()
            .Evaluate(ContextBuilder.Build(h, ContextBuilder.Regime(MarketRegime.TrendUp)));

        Assert.True(h.LastFeatures.DistanceToEmaFastInAtr > 2.5,
            $"Setup failed: extension was only {h.LastFeatures.DistanceToEmaFastInAtr:F2} ATR.");
        Assert.False(signal.IsActionable);
        Assert.Contains("extended", signal.Rationale);
    }

    [Fact]
    public void MeanReversionFadesTheUpperBandAndBuysTheLower()
    {
        var strategy = new MeanReversionStrategy();
        var signals = new List<StrategySignal>();

        for (ulong seed = 1; seed <= 40; seed++)
        {
            PipelineHarness h = ContextBuilder.Range(seed, bars: 900);
            StrategySignal s = strategy.Evaluate(ContextBuilder.Build(h, ContextBuilder.Regime(MarketRegime.Range)));
            if (s.IsActionable)
            {
                signals.Add(s);
                double percentB = h.LastFeatures.BollingerPercentB;
                Assert.True(
                    (s.Direction == Side.Short && percentB >= 0.9) || (s.Direction == Side.Long && percentB <= 0.1),
                    $"Mean reversion went {s.Direction} at %B {percentB:F2}.");
            }
        }

        Assert.NotEmpty(signals);
    }

    [Fact]
    public void ReversalRequiresFourIndependentConfirmations()
    {
        var strategy = new ReversalStrategy();
        int actionable = 0, abstained = 0;

        for (ulong seed = 1; seed <= 30; seed++)
        {
            PipelineHarness h = ContextBuilder.Trend(0.0006, seed, vol: 0.004, bars: 900);
            StrategySignal s = strategy.Evaluate(ContextBuilder.Build(h, ContextBuilder.Regime(MarketRegime.Euphoria)));

            if (s.IsActionable)
            {
                actionable++;
                // Counter-trend conviction is capped no matter how good the setup looks.
                Assert.True(s.Confidence <= 0.75 + 1e-9);
            }
            else
            {
                abstained++;
                Assert.Contains("confirmations", s.Rationale);
            }
        }

        Assert.True(abstained > actionable,
            $"Reversal fired {actionable} times and abstained {abstained}; it must be the most reluctant strategy.");
    }

    [Fact]
    public void BreakoutRefusesToChaseAMoveAlreadyFarPastTheLevel()
    {
        // A genuine compression phase first, so the compression guard is satisfied and this
        // test isolates the chase guard rather than tripping an earlier one.
        var config = new EngineConfig();
        config.Strategy.MaxChaseInAtr = 0.02;

        PipelineHarness h = CompressedMarket(config, seed: 78);
        Candle breakoutBar = BreakoutBar(h, overshootInAtr: 0.3);
        h.Feed(new[] { breakoutBar });

        double penetration = (h.Data.Signal.Last.Close - h.Data.Signal.Donchian.Upper) / h.LastFeatures.Atr;
        Assert.True(penetration > config.Strategy.MaxChaseInAtr,
            $"Setup failed: penetration was only {penetration:F3} ATR.");
        Assert.True(h.LastFeatures.BollingerWidthPercentile < 0.5,
            "Setup failed: the compression guard would have fired before the chase guard.");

        StrategySignal signal = new BreakoutStrategy()
            .Evaluate(ContextBuilder.Build(h, ContextBuilder.Regime(MarketRegime.Breakout)));

        Assert.False(signal.IsActionable);
        Assert.Contains("beyond the level", signal.Rationale);
    }

    [Fact]
    public void BreakoutFiresOnACleanBreakOfThePriorChannel()
    {
        // Regression guard for the Donchian lag. If the channel included the bar being
        // evaluated, its upper bound would always be at least that bar's high, "closed above
        // the channel" could never be true, and this strategy would silently never fire.
        PipelineHarness h = CompressedMarket(new EngineConfig(), seed: 78);
        double priorUpper = h.Data.Signal.Donchian.Upper;

        h.Feed(new[] { BreakoutBar(h, overshootInAtr: 0.3) });

        Assert.True(h.Data.Signal.Last.Close > priorUpper,
            "Setup failed: the bar did not close above the prior channel.");

        StrategySignal signal = new BreakoutStrategy()
            .Evaluate(ContextBuilder.Build(h, ContextBuilder.Regime(MarketRegime.Breakout)));

        Assert.True(signal.IsActionable, $"Breakout abstained: {signal.Rationale}");
        Assert.Equal(Side.Long, signal.Direction);
        Assert.Equal(priorUpper, signal.AnchorPrice, 6);
    }

    /// <summary>An active market that then compresses, so band width ranks low.</summary>
    private static PipelineHarness CompressedMarket(EngineConfig config, ulong seed)
    {
        var h = new PipelineHarness(config);
        var sim = new MarketSimulator(seed: seed);
        h.Feed(sim.Generate(1000, 0.0, 0.0018));
        h.Feed(sim.Generate(60, 0.0, 0.0004));
        return h;
    }

    /// <summary>A bar closing a chosen distance beyond the prior channel high.</summary>
    private static Candle BreakoutBar(PipelineHarness h, double overshootInAtr)
    {
        double open = h.Data.Signal.Last.Close;
        double priorUpper = h.Data.Signal.Donchian.Upper;
        double atr = h.Data.Signal.Atr.Value;
        double close = Math.Max(open, priorUpper) + (atr * overshootInAtr);
        DateTime t = h.Data.Signal.LastBarOpenTimeUtc.AddMinutes(5);
        return new Candle(t, open, close * 1.0001, Math.Min(open, close) * 0.9999, close, 5000);
    }

    [Fact]
    public void MomentumRefusesWhenMomentumIsDecelerating()
    {
        var strategy = new MomentumStrategy();
        for (ulong seed = 1; seed <= 25; seed++)
        {
            PipelineHarness h = ContextBuilder.Trend(0.001, seed, bars: 900);
            StrategySignal s = strategy.Evaluate(ContextBuilder.Build(h, ContextBuilder.Regime(MarketRegime.TrendUp)));
            if (!s.IsActionable) continue;

            double sign = s.Direction == Side.Long ? 1 : -1;
            Assert.True(sign * h.LastFeatures.MacdHistogramDeltaInAtr > 0,
                "Momentum fired while its own histogram was decelerating.");
        }
    }
}

public class SignalCorrelationTests
{
    private static SignalCorrelationTracker Tracker(out StrategyConfig config)
    {
        config = new StrategyConfig();
        var t = new SignalCorrelationTracker(config);
        foreach (IStrategy s in new StrategyRegistry().All) t.Register(s);
        return t;
    }

    [Fact]
    public void AStrategyFullyOverlapsItself()
    {
        SignalCorrelationTracker t = Tracker(out _);
        Assert.Equal(1.0, t.Overlap("TrendFollowing", "TrendFollowing"));
    }

    [Fact]
    public void StrategiesSharingFeatureFamiliesAreDetectedAsOverlappingFromTheFirstBar()
    {
        SignalCorrelationTracker t = Tracker(out _);

        // TrendFollowing uses {TrendDirection, Momentum}; Momentum uses
        // {Momentum, Volume, TrendDirection}. They share two of three families.
        double related = t.Overlap("TrendFollowing", "Momentum");

        // MeanReversion uses {RangePosition, Volatility} -- disjoint from TrendFollowing.
        double unrelated = t.Overlap("TrendFollowing", "MeanReversion");

        Assert.True(related > unrelated, $"related={related:F2} unrelated={unrelated:F2}");
        Assert.Equal(0.0, unrelated);
    }

    [Fact]
    public void EffectiveVoteCountDiscountsOverlappingAgreement()
    {
        SignalCorrelationTracker t = Tracker(out _);

        double overlapping = t.EffectiveVoteCount(new[] { "TrendFollowing", "Momentum" });
        double independent = t.EffectiveVoteCount(new[] { "TrendFollowing", "MeanReversion" });

        Assert.True(overlapping < independent,
            $"Overlapping pair scored {overlapping:F2}, independent pair {independent:F2}.");
        Assert.Equal(2.0, independent, 6);
        Assert.True(overlapping >= 1.0, "Effective votes must never fall below one.");
    }

    [Fact]
    public void EmpiricalCorrelationRaisesOverlapEvenForDisjointFeatureFamilies()
    {
        var config = new StrategyConfig();
        var t = new SignalCorrelationTracker(config);

        var a = new MeanReversionStrategy();
        var b = new VolatilityExpansionStrategy();
        t.Register(a);
        t.Register(b);

        double structural = t.Overlap(a.Name, b.Name);

        // Feed 60 bars on which the two produce identical signed confidences.
        var rng = new Quant.Core.Numerics.Pcg32(5);
        for (int i = 0; i < 60; i++)
        {
            double v = rng.NextGaussian() * 0.3;
            t.Observe(new[]
            {
                new StrategySignal { StrategyName = a.Name, Direction = v > 0 ? Side.Long : Side.Short, Confidence = Math.Abs(v) },
                new StrategySignal { StrategyName = b.Name, Direction = v > 0 ? Side.Long : Side.Short, Confidence = Math.Abs(v) },
            });
        }

        double afterEvidence = t.Overlap(a.Name, b.Name);
        Assert.True(afterEvidence > structural,
            $"Empirical duplication ({afterEvidence:F2}) should exceed the structural estimate ({structural:F2}).");
        Assert.True(afterEvidence > 0.9);
    }

    [Fact]
    public void ReliableDisagreementIsNotCountedAsOverlap()
    {
        var config = new StrategyConfig();
        var t = new SignalCorrelationTracker(config);

        // MeanReversion reads {RangePosition, Volatility}; MarketStructure reads
        // {MarketStructure, TrendDirection}. Structurally disjoint, so any overlap found
        // could only come from the empirical term.
        t.Register(new MeanReversionStrategy());
        t.Register(new MarketStructureStrategy());
        Assert.Equal(0.0, t.Overlap("MeanReversion", "MarketStructure"));

        var rng = new Quant.Core.Numerics.Pcg32(6);
        for (int i = 0; i < 60; i++)
        {
            double v = rng.NextGaussian() * 0.3;
            t.Observe(new[]
            {
                new StrategySignal { StrategyName = "MeanReversion", Direction = v > 0 ? Side.Long : Side.Short, Confidence = Math.Abs(v) },
                new StrategySignal { StrategyName = "MarketStructure", Direction = v > 0 ? Side.Short : Side.Long, Confidence = Math.Abs(v) },
            });
        }

        // Reliable disagreement is independent information, not duplicated information.
        Assert.Equal(0.0, t.Overlap("MeanReversion", "MarketStructure"));
    }
}

public class StrategyWeightEngineTests
{
    private static StrategyWeightEngine Engine(out PerformanceStore store, out AdaptationConfig config)
    {
        config = new AdaptationConfig();
        store = new PerformanceStore(config);
        var engine = new StrategyWeightEngine(config, new StrategyConfig(), store);
        foreach (IStrategy s in new StrategyRegistry().All) engine.Register(s);
        return engine;
    }

    private static TradeRecord Trade(string strategy, double r, MarketRegime regime = MarketRegime.TrendUp, DateTime? at = null) =>
        new TradeRecord
        {
            TradeId = Guid.NewGuid().ToString("N"),
            StrategyName = strategy,
            SymbolName = "BTCUSD",
            Direction = Side.Long,
            Regime = regime,
            R = r,
            MaeR = r > 0 ? 0.4 : 1.0,
            MfeR = r > 0 ? r * 1.3 : 0.2,
            EntryTimeUtc = at ?? new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ExitTimeUtc = (at ?? new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)).AddHours(2),
            ExitReason = r > 0 ? ExitReason.TakeProfit1 : ExitReason.StopLoss,
            EnsembleConfidence = 0.7,
            PlannedRewardToRisk = 2.0,
        };

    [Fact]
    public void NoSingleStrategyMayExceedTheHardWeightCap()
    {
        StrategyWeightEngine engine = Engine(out _, out _);

        var raw = new Dictionary<string, double>
        {
            { "TrendFollowing", 100.0 },
            { "Momentum", 1.0 },
            { "Breakout", 1.0 },
        };

        IReadOnlyDictionary<string, double> normalized = engine.Normalize(raw);

        Assert.True(normalized["TrendFollowing"] <= 0.40 + 1e-9,
            $"TrendFollowing took {normalized["TrendFollowing"]:P1} of the ensemble.");
        Assert.Equal(1.0, normalized.Values.Sum(), 6);
    }

    [Fact]
    public void NormalizedWeightsSumToOneAcrossAWideRangeOfInputs()
    {
        StrategyWeightEngine engine = Engine(out _, out _);
        var rng = new Quant.Core.Numerics.Pcg32(9);

        for (int trial = 0; trial < 50; trial++)
        {
            var raw = new Dictionary<string, double>();
            foreach (IStrategy s in new StrategyRegistry().Ensemble)
            {
                raw[s.Name] = rng.NextDouble() * rng.NextDouble() * 10;
            }

            IReadOnlyDictionary<string, double> normalized = engine.Normalize(raw);
            if (normalized.Count == 0) continue;

            Assert.Equal(1.0, normalized.Values.Sum(), 6);
            Assert.All(normalized.Values, w => Assert.True(w <= 0.40 + 1e-9));
        }
    }

    [Fact]
    public void ASmallSampleDoesNotMoveTheWeightMuch()
    {
        StrategyWeightEngine engine = Engine(out PerformanceStore store, out _);
        var strategy = new TrendFollowingStrategy();

        double before = engine.WeightFor(strategy, MarketRegime.TrendUp, "BTCUSD", 1.0);

        for (int i = 0; i < 5; i++) store.Record(Trade(strategy.Name, 3.0));
        double afterFiveWins = engine.WeightFor(strategy, MarketRegime.TrendUp, "BTCUSD", 1.0);

        Assert.True(Math.Abs(afterFiveWins - before) < 0.25,
            $"Five trades moved the weight from {before:F3} to {afterFiveWins:F3}; that is too much for five trades.");
    }

    [Fact]
    public void SustainedLossesDegradeThenDisableAStrategy()
    {
        StrategyWeightEngine engine = Engine(out PerformanceStore store, out AdaptationConfig config);
        var strategy = new TrendFollowingStrategy();
        DateTime t = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // A genuinely good record first, so the deterioration is a real change rather than
        // simply a strategy that never worked.
        for (int i = 0; i < 60; i++) store.Record(Trade(strategy.Name, i % 3 == 0 ? -1.0 : 1.2, at: t.AddHours(i)));
        engine.Review(t.AddHours(60));
        Assert.Equal(StrategyStatus.Active, engine.StateOf(strategy.Name).Status);

        // Then a sustained collapse.
        for (int i = 0; i < 80; i++) store.Record(Trade(strategy.Name, -1.0, at: t.AddHours(100 + i)));
        engine.Review(t.AddHours(200));

        StrategyStatus status = engine.StateOf(strategy.Name).Status;
        Assert.True(status == StrategyStatus.Degraded || status == StrategyStatus.Disabled,
            $"Status was {status} after a sustained collapse.");

        engine.Review(t.AddHours(201));
        Assert.Equal(StrategyStatus.Disabled, engine.StateOf(strategy.Name).Status);
        Assert.Equal(0.0, engine.WeightFor(strategy, MarketRegime.TrendUp, "BTCUSD", 1.0));
    }

    [Fact]
    public void ADisabledStrategyCanEarnItsWayBackThroughShadowTrading()
    {
        StrategyWeightEngine engine = Engine(out PerformanceStore store, out AdaptationConfig config);
        var strategy = new TrendFollowingStrategy();
        DateTime t = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (int i = 0; i < 60; i++) store.Record(Trade(strategy.Name, i % 3 == 0 ? -1.0 : 1.2, at: t.AddHours(i)));
        engine.Review(t.AddHours(60));
        for (int i = 0; i < 80; i++) store.Record(Trade(strategy.Name, -1.0, at: t.AddHours(100 + i)));
        engine.Review(t.AddHours(200));
        engine.Review(t.AddHours(201));
        Assert.Equal(StrategyStatus.Disabled, engine.StateOf(strategy.Name).Status);

        DateTime disabledAt = t.AddHours(201);

        // Not enough shadow evidence yet, and not enough elapsed time.
        engine.Review(disabledAt.AddHours(1));
        Assert.Equal(StrategyStatus.Disabled, engine.StateOf(strategy.Name).Status);

        // A convincing virtual record over a long enough window.
        for (int i = 0; i < config.ShadowTradesForRecovery + 10; i++)
        {
            TradeRecord virtualTrade = Trade(strategy.Name, i % 4 == 0 ? -1.0 : 1.5, at: disabledAt.AddHours(i));
            store.Record(new TradeRecord
            {
                TradeId = virtualTrade.TradeId, StrategyName = virtualTrade.StrategyName,
                SymbolName = virtualTrade.SymbolName, Direction = virtualTrade.Direction,
                Regime = virtualTrade.Regime, R = virtualTrade.R, MaeR = virtualTrade.MaeR, MfeR = virtualTrade.MfeR,
                EntryTimeUtc = virtualTrade.EntryTimeUtc, ExitTimeUtc = virtualTrade.ExitTimeUtc,
                ExitReason = virtualTrade.ExitReason, IsVirtual = true,
            });
        }

        engine.Review(disabledAt.AddHours(config.ShadowHoursBeforeRecovery + 1));
        Assert.Equal(StrategyStatus.Recovering, engine.StateOf(strategy.Name).Status);

        // Recovery must actually grant a tradeable weight. A recovering strategy weighted on
        // the record that got it disabled would sit at zero forever, never trade, and never
        // rebuild a record -- recovery would be cosmetic.
        double recoveringWeight = engine.WeightFor(strategy, MarketRegime.TrendUp, "BTCUSD", 1.0);
        Assert.True(recoveringWeight > 0,
            "A recovering strategy must receive a non-zero weight or it can never prove itself.");

        // But it is probation, not a pardon: well below a healthy strategy's weight.
        Assert.True(recoveringWeight < 0.5,
            $"Recovering weight {recoveringWeight:F3} is too generous for a strategy on probation.");
    }

    [Fact]
    public void VirtualTradesNeverContaminateTheRealPerformanceRecord()
    {
        _ = Engine(out PerformanceStore store, out _);

        store.Record(Trade("TrendFollowing", 1.0));
        for (int i = 0; i < 50; i++)
        {
            TradeRecord t = Trade("TrendFollowing", 5.0);
            store.Record(new TradeRecord
            {
                TradeId = t.TradeId, StrategyName = t.StrategyName, SymbolName = t.SymbolName,
                Direction = t.Direction, Regime = t.Regime, R = t.R, MaeR = t.MaeR, MfeR = t.MfeR,
                EntryTimeUtc = t.EntryTimeUtc, ExitTimeUtc = t.ExitTimeUtc, ExitReason = t.ExitReason,
                IsVirtual = true,
            });
        }

        Assert.Equal(1, store.Get(PerformanceStore.StrategyKey("TrendFollowing")).TotalTrades);
        Assert.Equal(50, store.Get(PerformanceStore.ShadowKey("TrendFollowing")).TotalTrades);
        Assert.Equal(1, store.Overall.TotalTrades);
    }
}

public class EnsembleVoterTests
{
    private sealed class Fixed : StrategyBase
    {
        private readonly StrategySignal _signal;
        private readonly FeatureFamily[] _families;

        public Fixed(string name, Side side, double confidence, params FeatureFamily[] families)
        {
            Name = name;
            _families = families.Length > 0 ? families : new[] { FeatureFamily.TrendDirection };
            _signal = new StrategySignal
            {
                StrategyName = name, Kind = StrategyKind.Baseline, Direction = side,
                Confidence = confidence, Rationale = "fixed", AnchorPrice = 100,
            };
        }

        public override string Name { get; }
        public override StrategyKind Kind => StrategyKind.Baseline;
        public override IReadOnlyDictionary<MarketRegime, double> RegimeFit => Fit((MarketRegime.TrendUp, 1.0));
        public override IReadOnlyCollection<FeatureFamily> Families => _families;
        protected override StrategySignal EvaluateCore(StrategyContext context) => _signal;
    }

    private static (EnsembleVoter Voter, StrategyWeightEngine Weights, SignalCorrelationTracker Corr) Build(IEnumerable<IStrategy> strategies)
    {
        var adaptation = new AdaptationConfig();
        var strategyConfig = new StrategyConfig();
        var store = new PerformanceStore(adaptation);
        var weights = new StrategyWeightEngine(adaptation, strategyConfig, store);
        var corr = new SignalCorrelationTracker(strategyConfig);

        foreach (IStrategy s in strategies) { weights.Register(s); corr.Register(s); }
        return (new EnsembleVoter(strategyConfig, corr, weights), weights, corr);
    }

    [Fact]
    public void OverlappingAgreementYieldsLessConvictionThanIndependentAgreement()
    {
        PipelineHarness h = ContextBuilder.Trend(0.001);
        StrategyContext ctx = ContextBuilder.Build(h, ContextBuilder.Regime(MarketRegime.TrendUp));

        var overlapping = new IStrategy[]
        {
            new Fixed("A", Side.Long, 0.8, FeatureFamily.TrendDirection),
            new Fixed("B", Side.Long, 0.8, FeatureFamily.TrendDirection),
            new Fixed("C", Side.Long, 0.8, FeatureFamily.TrendDirection),
        };
        var independent = new IStrategy[]
        {
            new Fixed("A", Side.Long, 0.8, FeatureFamily.TrendDirection),
            new Fixed("B", Side.Long, 0.8, FeatureFamily.RangePosition),
            new Fixed("C", Side.Long, 0.8, FeatureFamily.Microstructure),
        };

        var o = Build(overlapping);
        var i = Build(independent);

        EnsembleDecision od = o.Voter.Vote(ctx, overlapping, 1.0, false, out _);
        EnsembleDecision id = i.Voter.Vote(ctx, independent, 1.0, false, out _);

        Assert.Equal(Side.Long, od.Direction);
        Assert.Equal(Side.Long, id.Direction);
        Assert.Equal(3, od.RawVotes);
        Assert.Equal(3, id.RawVotes);

        Assert.True(od.EffectiveVotes < id.EffectiveVotes,
            $"overlapping effective={od.EffectiveVotes:F2}, independent effective={id.EffectiveVotes:F2}");
        Assert.True(od.Confidence < id.Confidence,
            $"overlapping conf={od.Confidence:F3}, independent conf={id.Confidence:F3}");
        Assert.Equal(1.0, od.EffectiveVotes, 6);
    }

    [Fact]
    public void DissentReducesConfidenceRelativeToUnanimity()
    {
        PipelineHarness h = ContextBuilder.Trend(0.001);
        StrategyContext ctx = ContextBuilder.Build(h, ContextBuilder.Regime(MarketRegime.TrendUp));

        var unanimous = new IStrategy[]
        {
            new Fixed("A", Side.Long, 0.8, FeatureFamily.TrendDirection),
            new Fixed("B", Side.Long, 0.8, FeatureFamily.RangePosition),
        };
        var split = new IStrategy[]
        {
            new Fixed("A", Side.Long, 0.8, FeatureFamily.TrendDirection),
            new Fixed("B", Side.Long, 0.8, FeatureFamily.RangePosition),
            new Fixed("C", Side.Short, 0.75, FeatureFamily.Microstructure),
        };

        EnsembleDecision u = Build(unanimous).Voter.Vote(ctx, unanimous, 1.0, false, out _);
        EnsembleDecision s = Build(split).Voter.Vote(ctx, split, 1.0, false, out _);

        Assert.Equal(Side.Long, u.Direction);
        Assert.Equal(Side.Long, s.Direction);
        Assert.True(s.Confidence < u.Confidence,
            $"split conf={s.Confidence:F3} should be below unanimous conf={u.Confidence:F3}");
        Assert.Single(s.Dissenting);
    }

    [Fact]
    public void ExactlyOffsettingStrategiesProduceNoTrade()
    {
        PipelineHarness h = ContextBuilder.Trend(0.001);
        StrategyContext ctx = ContextBuilder.Build(h, ContextBuilder.Regime(MarketRegime.TrendUp));

        var offsetting = new IStrategy[]
        {
            new Fixed("A", Side.Long, 0.8, FeatureFamily.TrendDirection),
            new Fixed("B", Side.Short, 0.8, FeatureFamily.TrendDirection),
        };

        EnsembleDecision d = Build(offsetting).Voter.Vote(ctx, offsetting, 1.0, false, out _);

        Assert.Equal(Side.None, d.Direction);
        Assert.False(d.IsActionable);
    }

    [Fact]
    public void DisabledStrategiesAreExcludedFromTheVoteButStillTrackedVirtually()
    {
        PipelineHarness h = ContextBuilder.Trend(0.001);
        StrategyContext ctx = ContextBuilder.Build(h, ContextBuilder.Regime(MarketRegime.TrendUp));

        var strategies = new IStrategy[]
        {
            new Fixed("Good", Side.Long, 0.7, FeatureFamily.TrendDirection),
            new Fixed("Banned", Side.Short, 0.95, FeatureFamily.Microstructure),
        };

        var built = Build(strategies);
        built.Weights.StateOf("Banned").Status = StrategyStatus.Disabled;

        EnsembleDecision d = built.Voter.Vote(ctx, strategies, 1.0, collectDisabled: true, out IReadOnlyList<StrategySignal> disabled);

        Assert.Equal(Side.Long, d.Direction);
        Assert.Empty(d.Dissenting);
        Assert.Single(disabled);
        Assert.Equal("Banned", disabled[0].StrategyName);
    }

    [Fact]
    public void AWeakFifthVoteCannotRaiseConvictionAboveWhatTheStrongVotesJustify()
    {
        PipelineHarness h = ContextBuilder.Trend(0.001);
        StrategyContext ctx = ContextBuilder.Build(h, ContextBuilder.Regime(MarketRegime.TrendUp));

        var strong = new IStrategy[]
        {
            new Fixed("A", Side.Long, 0.9, FeatureFamily.TrendDirection),
            new Fixed("B", Side.Long, 0.9, FeatureFamily.RangePosition),
        };
        var strongPlusWeak = new IStrategy[]
        {
            new Fixed("A", Side.Long, 0.9, FeatureFamily.TrendDirection),
            new Fixed("B", Side.Long, 0.9, FeatureFamily.RangePosition),
            new Fixed("C", Side.Long, 0.51, FeatureFamily.Volume),
        };

        EnsembleDecision s = Build(strong).Voter.Vote(ctx, strong, 1.0, false, out _);
        EnsembleDecision sw = Build(strongPlusWeak).Voter.Vote(ctx, strongPlusWeak, 1.0, false, out _);

        // The extra independent vote may add a little through the independence term, but the
        // BLENDED confidence must not exceed the strong pair's own average.
        Assert.True(sw.Confidence <= 0.9 + 1e-9);
        Assert.True(s.Confidence <= 0.9 + 1e-9);
    }
}
