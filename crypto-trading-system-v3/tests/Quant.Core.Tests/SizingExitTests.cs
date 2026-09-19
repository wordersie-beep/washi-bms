using System;
using System.Collections.Generic;
using System.Linq;
using Quant.Core.Config;
using Quant.Core.Data;
using Quant.Core.Ev;
using Quant.Core.Exits;
using Quant.Core.Numerics;
using Quant.Core.Portfolio;
using Quant.Core.Primitives;
using Quant.Core.Sizing;
using Quant.Core.Stats;
using Quant.Core.Strategies;
using Xunit;

namespace Quant.Core.Tests;

public class PositionSizerTests
{
    private static SymbolSpec Spec(double minVolume = 0.01, double step = 0.01, double maxVolume = 1000, double leverage = 2.0) =>
        new SymbolSpec("BTCUSD", 0.01, 0.01, 2, minVolume, maxVolume, step, 35, 0.01, 0, leverage, true);

    private static AccountSnapshot Account(double equity = 10000, double freeMargin = 10000) =>
        new AccountSnapshot(equity, equity, 0, freeMargin, 1000, 50, false, "USD");

    private static PositionSizer Sizer(EngineConfig config = null)
    {
        config ??= new EngineConfig();
        return new PositionSizer(config.Sizing, config.Risk);
    }

    private static SizingResult Size(
        PositionSizer sizer, EngineConfig config, double riskState = 1.0, double regime = 1.0,
        double confidence = 1.0, double edgeSurplus = 1.0, double strategyWeight = 0.35,
        double atrPercentile = 0.5, double correlation = 0, double dataQuality = 1.0,
        double execution = 1.0, double budget = 100, double equity = 10000, double freeMargin = 10000) =>
        sizer.Compute(Spec(), Account(equity, freeMargin), entryPrice: 50000, stopPrice: 49500, Side.Long,
            riskState, regime, confidence, edgeSurplus, strategyWeight, atrPercentile, correlation, dataQuality, execution, budget);

    [Fact]
    public void RiskNeverExceedsTheHardCapUnderAnyCombinationOfInputs()
    {
        var config = new EngineConfig();
        config.Sizing.RiskPerTradePercent = 5.0;    // deliberately above the cap
        PositionSizer sizer = Sizer(config);

        var rng = new Pcg32(23);
        for (int i = 0; i < 3000; i++)
        {
            SizingResult r = sizer.Compute(Spec(), Account(), 50000, 49500, Side.Long,
                riskStateMultiplier: rng.NextDouble() * 2,
                regimeMultiplier: rng.NextDouble() * 2,
                ensembleConfidence: rng.NextDouble() * 2,
                edgeSurplusR: rng.NextDouble() * 10,
                strategyWeight: rng.NextDouble() * 2,
                atrPercentile: rng.NextDouble(),
                correlationPenalty: rng.NextDouble(),
                dataQualityScore: rng.NextDouble() * 2,
                executionQuality: rng.NextDouble() * 2,
                remainingRiskBudgetPercent: 100);

            if (!r.IsTradeable) continue;

            Assert.True(r.RiskPercent <= config.Risk.HardMaxRiskPerTradePercent + 1e-9,
                $"Risk reached {r.RiskPercent:F4}% against a hard cap of {config.Risk.HardMaxRiskPerTradePercent:F4}%.");
        }
    }

    [Fact]
    public void EveryAdaptiveFactorCanOnlyReduceSizeNeverIncreaseIt()
    {
        // The anti-martingale property, enforced by the shape of the calculation rather than
        // by review. If any factor could exceed 1.0, some input combination would size above
        // the base risk.
        var config = new EngineConfig();
        PositionSizer sizer = Sizer(config);

        SizingResult best = Size(sizer, config);
        double baseRisk = config.Sizing.RiskPerTradePercent;

        Assert.True(best.RiskPercent <= baseRisk + 1e-9,
            $"With every factor at its best, risk was {best.RiskPercent:F4}% against a base of {baseRisk:F4}%.");

        var rng = new Pcg32(24);
        for (int i = 0; i < 2000; i++)
        {
            SizingResult r = Size(sizer, config,
                riskState: rng.NextDouble(), regime: rng.NextDouble(), confidence: rng.NextDouble(),
                edgeSurplus: rng.NextDouble() * 5, strategyWeight: rng.NextDouble(),
                atrPercentile: rng.NextDouble(), correlation: rng.NextDouble(),
                dataQuality: rng.NextDouble(), execution: rng.NextDouble());

            if (r.IsTradeable)
            {
                Assert.True(r.RiskPercent <= baseRisk + 1e-9,
                    $"An adaptive factor scaled risk UP to {r.RiskPercent:F4}%.");
            }
        }
    }

    [Fact]
    public void EachFactorIndividuallyReducesSize()
    {
        var config = new EngineConfig();
        PositionSizer sizer = Sizer(config);
        double full = Size(sizer, config).VolumeInUnits;

        Assert.True(Size(sizer, config, riskState: 0.5).VolumeInUnits < full);
        Assert.True(Size(sizer, config, regime: 0.5).VolumeInUnits < full);
        Assert.True(Size(sizer, config, confidence: 0.5).VolumeInUnits < full);
        Assert.True(Size(sizer, config, edgeSurplus: 0.0).VolumeInUnits < full);
        Assert.True(Size(sizer, config, strategyWeight: 0.05).VolumeInUnits < full);
        Assert.True(Size(sizer, config, atrPercentile: 0.95).VolumeInUnits < full);
        Assert.True(Size(sizer, config, correlation: 1.0).VolumeInUnits < full);
        Assert.True(Size(sizer, config, dataQuality: 0.5).VolumeInUnits < full);
        Assert.True(Size(sizer, config, execution: 0.5).VolumeInUnits < full);
    }

    [Fact]
    public void VolatilityTargetingSizesDownInVolatilityAndNeverUpInCalm()
    {
        var config = new EngineConfig();
        PositionSizer sizer = Sizer(config);

        double calm = Size(sizer, config, atrPercentile: 0.05).VolumeInUnits;
        double neutral = Size(sizer, config, atrPercentile: 0.50).VolumeInUnits;
        double wild = Size(sizer, config, atrPercentile: 0.99).VolumeInUnits;

        Assert.Equal(neutral, calm);   // no scaling up into quiet markets
        Assert.True(wild < neutral * 0.6, $"wild={wild:F4} neutral={neutral:F4}");
    }

    [Fact]
    public void VolumeIsAlwaysRoundedDownOntoTheBrokerGrid()
    {
        var config = new EngineConfig();
        var sizer = new PositionSizer(config.Sizing, config.Risk);
        var spec = new SymbolSpec("BTCUSD", 0.01, 0.01, 2, 0.1, 1000, 0.1, 35, 0.01, 0, 2.0, true);

        // Equity large enough that the requested size clears the broker's 0.1-unit grid.
        SizingResult r = sizer.Compute(spec, Account(equity: 200000, freeMargin: 200000), 50000, 49500, Side.Long,
            1, 1, 1, 1, 0.35, 0.5, 0, 1, 1, 100);

        Assert.True(r.IsTradeable, r.Detail);

        double steps = r.VolumeInUnits / 0.1;
        Assert.True(Math.Abs(steps - Math.Round(steps)) < 1e-6,
            $"{r.VolumeInUnits} units is not a whole multiple of the 0.1 volume step.");
        Assert.True(r.RiskPercent <= config.Sizing.RiskPerTradePercent + 1e-9,
            "Rounding volume must never push realised risk above the requested figure.");
    }

    [Fact]
    public void RejectsWhenTheStopIsOnTheWrongSideOfEntry()
    {
        var config = new EngineConfig();
        var sizer = new PositionSizer(config.Sizing, config.Risk);

        SizingResult r = sizer.Compute(Spec(), Account(), entryPrice: 50000, stopPrice: 50500, Side.Long,
            1, 1, 1, 1, 0.35, 0.5, 0, 1, 1, 100);

        Assert.False(r.IsTradeable);
        Assert.Equal(NoTradeReason.InvalidStopPlacement, r.RejectionReason);
    }

    [Fact]
    public void RejectsRatherThanShrinkingBelowTheBrokerMinimum()
    {
        var config = new EngineConfig();
        var sizer = new PositionSizer(config.Sizing, config.Risk);
        var chunky = new SymbolSpec("BTCUSD", 0.01, 0.01, 2, 10, 1000, 10, 35, 0.01, 0, 2.0, true);

        SizingResult r = sizer.Compute(chunky, Account(equity: 1000), 50000, 49500, Side.Long,
            1, 1, 1, 1, 0.35, 0.5, 0, 1, 1, 100);

        Assert.False(r.IsTradeable);
        Assert.Equal(NoTradeReason.SizeBelowMinimum, r.RejectionReason);
    }

    [Fact]
    public void ThePortfolioBudgetCapsTheTrade()
    {
        var config = new EngineConfig();
        PositionSizer sizer = Sizer(config);

        SizingResult generous = Size(sizer, config, budget: 100);
        SizingResult constrained = Size(sizer, config, budget: 0.10);

        Assert.True(constrained.RiskPercent < generous.RiskPercent);
        Assert.True(constrained.RiskPercent <= 0.10 + 1e-9);
    }

    [Fact]
    public void MarginPressureScalesTheTradeDownRatherThanRiskingAMarginCall()
    {
        var config = new EngineConfig();
        PositionSizer sizer = Sizer(config);

        SizingResult roomy = Size(sizer, config, freeMargin: 1000000);
        SizingResult tight = Size(sizer, config, freeMargin: 500);

        Assert.True(roomy.IsTradeable);
        Assert.True(!tight.IsTradeable || tight.VolumeInUnits < roomy.VolumeInUnits);
    }

    [Fact]
    public void EverySizeCarriesTheFactorsThatProducedIt()
    {
        var config = new EngineConfig();
        SizingResult r = Size(Sizer(config), config);

        Assert.True(r.IsTradeable);
        foreach (string key in new[] { "riskState", "regime", "confidence", "edge", "strategy", "volatility", "correlation", "dataQuality", "execution" })
        {
            Assert.True(r.Factors.ContainsKey(key), $"Sizing factor '{key}' was not recorded.");
        }
    }
}

public class CorrelationEngineTests
{
    private static CorrelationEngine Engine(out PortfolioConfig config)
    {
        config = new PortfolioConfig();
        return new CorrelationEngine(config);
    }

    [Fact]
    public void IdenticalSeriesCorrelatePerfectlyAndMirroredOnesInversely()
    {
        CorrelationEngine e = Engine(out _);
        var rng = new Pcg32(31);

        double a = 100, b = 100, c = 100;
        for (int i = 0; i < 150; i++)
        {
            double shock = rng.NextGaussian() * 0.01;
            a *= Math.Exp(shock);
            b *= Math.Exp(shock);
            c *= Math.Exp(-shock);
            e.Observe("A", a); e.Observe("B", b); e.Observe("C", c);
        }

        Assert.Equal(1.0, e.Correlation("A", "B"), 6);
        Assert.Equal(-1.0, e.Correlation("A", "C"), 6);
    }

    [Fact]
    public void IndependentSeriesShowLittleCorrelationOnAnAdequateSample()
    {
        // Over 600 observations the sampling error of a correlation estimate is about 0.04,
        // so a genuinely independent pair has nowhere to hide.
        CorrelationEngine e = Engine(out _);
        var rng = new Pcg32(32);

        double a = 100, b = 100;
        for (int i = 0; i < 600; i++)
        {
            a *= Math.Exp(rng.NextGaussian() * 0.01);
            b *= Math.Exp(rng.NextGaussian() * 0.01);
            e.Observe("A", a); e.Observe("B", b);
        }

        Assert.True(Math.Abs(e.Correlation("A", "B")) < 0.25,
            $"Independent series reported a correlation of {e.Correlation("A", "B"):F3}.");
    }

    [Fact]
    public void ShortWindowNoiseIsNotMistakenForARelationship()
    {
        // Across many independent seeds, the reported correlation must stay small. This is
        // the regression guard for taking a maximum across windows of differing reliability.
        var config = new PortfolioConfig();
        double worst = 0;

        for (ulong seed = 30; seed < 60; seed++)
        {
            var e = new CorrelationEngine(config);
            var rng = new Pcg32(seed);
            double a = 100, b = 100;
            for (int i = 0; i < 150; i++)
            {
                a *= Math.Exp(rng.NextGaussian() * 0.01);
                b *= Math.Exp(rng.NextGaussian() * 0.01);
                e.Observe("A", a); e.Observe("B", b);
            }
            worst = Math.Max(worst, Math.Abs(e.Correlation("A", "B")));
        }

        Assert.True(worst < 0.45, $"Worst spurious correlation across 30 independent seeds was {worst:F3}.");
    }

    [Fact]
    public void CorrelatedSymbolsAreGroupedIntoOneCluster()
    {
        CorrelationEngine e = Engine(out _);
        var rng = new Pcg32(33);

        double btc = 50000, eth = 3000, sol = 150, gold = 2000;
        for (int i = 0; i < 200; i++)
        {
            double market = rng.NextGaussian() * 0.01;
            btc *= Math.Exp(market + (rng.NextGaussian() * 0.001));
            eth *= Math.Exp(market + (rng.NextGaussian() * 0.001));
            sol *= Math.Exp(market + (rng.NextGaussian() * 0.001));
            gold *= Math.Exp(rng.NextGaussian() * 0.01);

            e.Observe("BTCUSD", btc); e.Observe("ETHUSD", eth);
            e.Observe("SOLUSD", sol); e.Observe("XAUUSD", gold);
        }

        IReadOnlyDictionary<string, int> clusters = e.BuildClusters();

        Assert.Equal(clusters["BTCUSD"], clusters["ETHUSD"]);
        Assert.Equal(clusters["BTCUSD"], clusters["SOLUSD"]);
        Assert.NotEqual(clusters["BTCUSD"], clusters["XAUUSD"]);
    }

    [Fact]
    public void ClusteringIsTransitiveThroughAnIntermediary()
    {
        // A correlates with B, B with C, A and C only weakly. Single linkage must still put
        // all three in one bucket, because B transmits a shock between them.
        CorrelationEngine e = Engine(out _);
        var rng = new Pcg32(34);

        double a = 100, b = 100, c = 100;
        for (int i = 0; i < 250; i++)
        {
            double x = rng.NextGaussian() * 0.01;
            double y = rng.NextGaussian() * 0.01;
            a *= Math.Exp(x);
            b *= Math.Exp((0.75 * x) + (0.75 * y));
            c *= Math.Exp(y);
            e.Observe("A", a); e.Observe("B", b); e.Observe("C", c);
        }

        IReadOnlyDictionary<string, int> clusters = e.BuildClusters();
        Assert.Equal(clusters["A"], clusters["C"]);
    }

    [Fact]
    public void ClusterNumberingIsDeterministic()
    {
        CorrelationEngine a = Engine(out _);
        CorrelationEngine b = Engine(out _);
        var rng = new Pcg32(35);

        double p = 100, q = 100;
        for (int i = 0; i < 150; i++)
        {
            p *= Math.Exp(rng.NextGaussian() * 0.01);
            q *= Math.Exp(rng.NextGaussian() * 0.01);
            a.Observe("ZZZ", p); a.Observe("AAA", q);
            b.Observe("AAA", q); b.Observe("ZZZ", p);
        }

        Assert.Equal(a.BuildClusters()["AAA"], b.BuildClusters()["AAA"]);
        Assert.Equal(a.BuildClusters()["ZZZ"], b.BuildClusters()["ZZZ"]);
    }

    [Fact]
    public void ShortSeriesReportNoCorrelationRatherThanANoisyOne()
    {
        CorrelationEngine e = Engine(out _);
        for (int i = 0; i < 5; i++) { e.Observe("A", 100 + i); e.Observe("B", 100 + i); }

        Assert.Equal(0, e.Correlation("A", "B"));
    }
}

public class PortfolioRiskTests
{
    private static OpenPosition Position(string symbol, Side side, double entry, double stop, double units) =>
        new OpenPosition
        {
            TradeId = Guid.NewGuid().ToString("N"),
            SymbolName = symbol,
            Direction = side,
            EntryPrice = entry,
            InitialStopPrice = stop,
            CurrentStopPrice = stop,
            InitialVolumeInUnits = units,
            CurrentVolumeInUnits = units,
            RiskPerUnit = Math.Abs(entry - stop),
        };

    [Fact]
    public void PortfolioHeatIsTheSumOfWhatEveryStopWouldCost()
    {
        var config = new EngineConfig();
        var correlation = new CorrelationEngine(config.Portfolio);
        var engine = new PortfolioRiskEngine(config.Portfolio, config.Risk, correlation);

        var positions = new List<OpenPosition>
        {
            Position("BTCUSD", Side.Long, 50000, 49500, 0.10),   // 50 at risk
            Position("ETHUSD", Side.Long, 3000, 2940, 1.00),     // 60 at risk
        };

        PortfolioExposure exposure = engine.Compute(positions, equity: 10000, correlation.BuildClusters());

        Assert.Equal(1.10, exposure.TotalOpenRiskPercent, 6);
        Assert.Equal(0.50, exposure.RiskInSymbol("BTCUSD"), 6);
        Assert.Equal(2, exposure.OpenPositionCount);
    }

    [Fact]
    public void APositionStoppedAtBreakEvenCarriesNoRisk()
    {
        var config = new EngineConfig();
        var correlation = new CorrelationEngine(config.Portfolio);
        var engine = new PortfolioRiskEngine(config.Portfolio, config.Risk, correlation);

        OpenPosition p = Position("BTCUSD", Side.Long, 50000, 49500, 0.10);
        p.CurrentStopPrice = 50000;   // moved to break-even

        PortfolioExposure exposure = engine.Compute(new[] { p }, 10000, correlation.BuildClusters());

        Assert.Equal(0, exposure.TotalOpenRiskPercent, 8);
    }

    [Fact]
    public void CorrelatedPositionsCarryMoreRealDirectionalRiskThanUncorrelatedOnes()
    {
        var config = new EngineConfig();
        var rng = new Pcg32(41);

        var correlated = new CorrelationEngine(config.Portfolio);
        double a = 100, b = 100;
        for (int i = 0; i < 200; i++)
        {
            double shock = rng.NextGaussian() * 0.01;
            a *= Math.Exp(shock); b *= Math.Exp(shock);
            correlated.Observe("AAA", a); correlated.Observe("BBB", b);
        }

        var independent = new CorrelationEngine(config.Portfolio);
        double c = 100, d = 100;
        for (int i = 0; i < 200; i++)
        {
            c *= Math.Exp(rng.NextGaussian() * 0.01);
            d *= Math.Exp(rng.NextGaussian() * 0.01);
            independent.Observe("AAA", c); independent.Observe("BBB", d);
        }

        var positions = new List<OpenPosition>
        {
            Position("AAA", Side.Long, 100, 99, 5),
            Position("BBB", Side.Long, 100, 99, 5),
        };

        double correlatedRisk = new PortfolioRiskEngine(config.Portfolio, config.Risk, correlated)
            .Compute(positions, 10000, correlated.BuildClusters()).CorrelationAdjustedDirectionalRiskPercent;

        double independentRisk = new PortfolioRiskEngine(config.Portfolio, config.Risk, independent)
            .Compute(positions, 10000, independent.BuildClusters()).CorrelationAdjustedDirectionalRiskPercent;

        Assert.True(correlatedRisk > independentRisk * 1.2,
            $"Correlated book measured {correlatedRisk:F3}% against an independent book at {independentRisk:F3}%; " +
            "apparent diversification is being taken at face value.");
    }

    [Fact]
    public void OppositeDirectionsPartiallyOffsetInTheDirectionalMeasure()
    {
        var config = new EngineConfig();
        var correlation = new CorrelationEngine(config.Portfolio);
        var rng = new Pcg32(42);

        double a = 100, b = 100;
        for (int i = 0; i < 200; i++)
        {
            double shock = rng.NextGaussian() * 0.01;
            a *= Math.Exp(shock); b *= Math.Exp(shock);
            correlation.Observe("AAA", a); correlation.Observe("BBB", b);
        }

        var engine = new PortfolioRiskEngine(config.Portfolio, config.Risk, correlation);

        double sameWay = engine.Compute(new[]
        {
            Position("AAA", Side.Long, 100, 99, 5),
            Position("BBB", Side.Long, 100, 99, 5),
        }, 10000, correlation.BuildClusters()).CorrelationAdjustedDirectionalRiskPercent;

        double opposed = engine.Compute(new[]
        {
            Position("AAA", Side.Long, 100, 99, 5),
            Position("BBB", Side.Short, 100, 101, 5),
        }, 10000, correlation.BuildClusters()).CorrelationAdjustedDirectionalRiskPercent;

        Assert.True(opposed < sameWay);
    }

    [Fact]
    public void LimitsBlockTheTradesTheyAreMeantTo()
    {
        var config = new EngineConfig();
        var correlation = new CorrelationEngine(config.Portfolio);
        var engine = new PortfolioRiskEngine(config.Portfolio, config.Risk, correlation);
        IReadOnlyDictionary<string, int> clusters = correlation.BuildClusters();

        PortfolioExposure empty = engine.Compute(Array.Empty<OpenPosition>(), 10000, clusters);
        Assert.Equal(NoTradeReason.None, engine.CheckLimits(empty, "BTCUSD", Side.Long, 0.3, clusters, 0, out _));

        PortfolioExposure heavy = engine.Compute(new[]
        {
            Position("BTCUSD", Side.Long, 50000, 49500, 0.10),
            Position("ETHUSD", Side.Long, 3000, 2940, 1.00),
        }, 10000, clusters);

        Assert.Equal(NoTradeReason.PortfolioRiskLimit,
            engine.CheckLimits(heavy, "SOLUSD", Side.Long, 1.5, clusters, 0, out _));

        Assert.Equal(NoTradeReason.PositionAlreadyOpen,
            engine.CheckLimits(heavy, "BTCUSD", Side.Long, 0.1, clusters, 1, out _));

        Assert.Equal(NoTradeReason.SymbolExposureLimit,
            engine.CheckLimits(heavy, "BTCUSD", Side.Long, 0.5, clusters, 0, out _));
    }

    [Fact]
    public void TheMaximumPositionCountIsEnforced()
    {
        var config = new EngineConfig();
        config.Portfolio.MaxOpenPositions = 2;

        var correlation = new CorrelationEngine(config.Portfolio);
        var engine = new PortfolioRiskEngine(config.Portfolio, config.Risk, correlation);
        IReadOnlyDictionary<string, int> clusters = correlation.BuildClusters();

        PortfolioExposure exposure = engine.Compute(new[]
        {
            Position("AAA", Side.Long, 100, 99.9, 1),
            Position("BBB", Side.Long, 100, 99.9, 1),
        }, 100000, clusters);

        Assert.Equal(NoTradeReason.MaxPositionsReached,
            engine.CheckLimits(exposure, "CCC", Side.Long, 0.01, clusters, 0, out _));
    }
}

public class ExitPlannerTests
{
    private static PipelineHarness WarmMarket(EngineConfig config = null, ulong seed = 51)
    {
        var h = new PipelineHarness(config ?? new EngineConfig());
        h.Feed(new MarketSimulator(seed: seed).Generate(1200, 0.0003, 0.0025));
        return h;
    }

    private static ExitPlanner Planner(EngineConfig config, out PerformanceStore store)
    {
        store = new PerformanceStore(config.Adaptation);
        return new ExitPlanner(config.Exit, config.Ev, store);
    }

    private static CostEstimate SmallCost(double atr) => new CostEstimate(atr * 0.01, 0, atr * 0.005, atr);

    [Fact]
    public void ProducesACoherentPlanWithTheStopBelowEntryForALong()
    {
        var config = new EngineConfig();
        PipelineHarness h = WarmMarket(config);
        ExitPlanner planner = Planner(config, out _);

        double entry = h.Data.Signal.Last.Close;
        ExitPlan plan = planner.Build(h.Spec, h.Data, Side.Long, entry, null, MarketRegime.TrendUp, SmallCost(h.Data.Signal.Atr.Value));

        Assert.True(plan.IsValid, plan.Detail);
        Assert.True(plan.StopPrice < entry);
        Assert.True(plan.Target1Price > entry);
        Assert.True(plan.Target2Price > plan.Target1Price);
        Assert.True(plan.StopDistance > 0);
        Assert.InRange(plan.StopInAtr, config.Exit.MinStopInAtr, config.Exit.MaxStopInAtr);
    }

    [Fact]
    public void MirrorsCorrectlyForAShort()
    {
        var config = new EngineConfig();
        PipelineHarness h = WarmMarket(config);
        ExitPlanner planner = Planner(config, out _);

        double entry = h.Data.Signal.Last.Close;
        ExitPlan plan = planner.Build(h.Spec, h.Data, Side.Short, entry, null, MarketRegime.TrendDown, SmallCost(h.Data.Signal.Atr.Value));

        Assert.True(plan.IsValid, plan.Detail);
        Assert.True(plan.StopPrice > entry);
        Assert.True(plan.Target1Price < entry);
        Assert.True(plan.Target2Price < plan.Target1Price);
    }

    [Fact]
    public void BreakEvenIsNetOfCostsNotTheEntryPrice()
    {
        var config = new EngineConfig();
        PipelineHarness h = WarmMarket(config);
        ExitPlanner planner = Planner(config, out _);

        double atr = h.Data.Signal.Atr.Value;
        double entry = h.Data.Signal.Last.Close;
        ExitPlan plan = planner.Build(h.Spec, h.Data, Side.Long, entry, null, MarketRegime.TrendUp, SmallCost(atr));

        Assert.True(plan.NetBreakEvenPrice > entry,
            "Break-even for a long must sit ABOVE entry, or the 'scratch' is a reliable small loss.");
    }

    [Fact]
    public void RefusesATradeWhoseCostsWouldEatTheRisk()
    {
        var config = new EngineConfig();
        PipelineHarness h = WarmMarket(config);
        ExitPlanner planner = Planner(config, out _);

        double atr = h.Data.Signal.Atr.Value;
        var ruinousCost = new CostEstimate(atr * 1.2, atr * 0.3, atr * 0.3, atr);

        ExitPlan plan = planner.Build(h.Spec, h.Data, Side.Long, h.Data.Signal.Last.Close, null, MarketRegime.TrendUp, ruinousCost);

        Assert.False(plan.IsValid);
        Assert.Equal(NoTradeReason.PoorRiskReward, plan.RejectionReason);
    }

    [Fact]
    public void AStrategyInvalidationLevelWidensButNeverTightensTheStop()
    {
        var config = new EngineConfig();
        PipelineHarness h = WarmMarket(config);
        ExitPlanner planner = Planner(config, out _);

        double atr = h.Data.Signal.Atr.Value;
        double entry = h.Data.Signal.Last.Close;

        ExitPlan generic = planner.Build(h.Spec, h.Data, Side.Long, entry, null, MarketRegime.TrendUp, SmallCost(atr));

        var wide = new StrategySignal
        {
            StrategyName = "T", Direction = Side.Long, Confidence = 0.8,
            InvalidationPrice = entry - (atr * 3.0),
        };
        ExitPlan widened = planner.Build(h.Spec, h.Data, Side.Long, entry, wide, MarketRegime.TrendUp, SmallCost(atr));
        Assert.True(widened.StopDistance > generic.StopDistance);
        Assert.Equal(StopMethod.StrategyInvalidation, widened.StopMethod);

        var tight = new StrategySignal
        {
            StrategyName = "T", Direction = Side.Long, Confidence = 0.8,
            InvalidationPrice = entry - (atr * 0.1),
        };
        ExitPlan notTightened = planner.Build(h.Spec, h.Data, Side.Long, entry, tight, MarketRegime.TrendUp, SmallCost(atr));
        Assert.Equal(generic.StopDistance, notTightened.StopDistance, 8);
    }

    [Fact]
    public void TheMaeDistributionWidensAStopThatWouldSitInsideTheNoise()
    {
        var config = new EngineConfig();
        PipelineHarness h = WarmMarket(config);
        ExitPlanner planner = Planner(config, out PerformanceStore store);

        double atr = h.Data.Signal.Atr.Value;
        double entry = h.Data.Signal.Last.Close;
        ExitPlan before = planner.Build(h.Spec, h.Data, Side.Long, entry, null, MarketRegime.TrendUp, SmallCost(atr));

        // A history in which winners routinely dip 1.4R before working.
        for (int i = 0; i < 80; i++)
        {
            store.Record(new TradeRecord
            {
                TradeId = Guid.NewGuid().ToString("N"),
                StrategyName = "TrendFollowing", SymbolName = "BTCUSD", Direction = Side.Long,
                Regime = MarketRegime.TrendUp, R = 1.5, MaeR = 1.4, MfeR = 2.0,
                EntryTimeUtc = RiskFixtures.T0, ExitTimeUtc = RiskFixtures.T0.AddHours(1),
                ExitReason = ExitReason.TakeProfit1,
            });
        }

        var leader = new StrategySignal { StrategyName = "TrendFollowing", Direction = Side.Long, Confidence = 0.8 };
        ExitPlan after = planner.Build(h.Spec, h.Data, Side.Long, entry, leader, MarketRegime.TrendUp, SmallCost(atr));

        Assert.True(after.StopDistance > before.StopDistance,
            "A stop inside the region where winners routinely dip converts winners into losers.");
        Assert.Equal(StopMethod.MaeDistribution, after.StopMethod);
    }

    [Fact]
    public void AnOverlyWideInvalidationFallsBackToTheAtrStopInsteadOfRefusingTheTrade()
    {
        // Предпочитать более защищённый стоп правильно: стоп внутри шума превращает
        // победителей в проигравших. Но когда структурный уровень или точка инвалидации
        // оказываются за границей допустимого — а в волатильном рынке это норма —
        // отказываться от сделки нельзя, если более узкий кандидат вполне приемлем.
        var config = new EngineConfig();
        PipelineHarness h = WarmMarket(config);
        ExitPlanner planner = Planner(config, out _);

        double atr = h.Data.Signal.Atr.Value;
        double entry = h.Data.Signal.Last.Close;

        var absurdlyWide = new StrategySignal
        {
            StrategyName = "T", Direction = Side.Long, Confidence = 0.8,
            InvalidationPrice = entry - (atr * 12.0),   // далеко за MaxStopInAtr
        };

        ExitPlan plan = planner.Build(h.Spec, h.Data, Side.Long, entry, absurdlyWide, MarketRegime.TrendUp, SmallCost(atr));

        Assert.True(plan.IsValid,
            $"Сделка отклонена вместо отката к ATR-стопу: {plan.RejectionReason} ({plan.Detail})");
        Assert.InRange(plan.StopInAtr, config.Exit.MinStopInAtr, config.Exit.MaxStopInAtr);
        Assert.NotEqual(StopMethod.StrategyInvalidation, plan.StopMethod);
    }

    [Fact]
    public void TheWidestAcceptableCandidateIsStillPreferred()
    {
        // Откат к более узкому кандидату не должен превратиться в «всегда самый узкий».
        var config = new EngineConfig();
        PipelineHarness h = WarmMarket(config);
        ExitPlanner planner = Planner(config, out _);

        double atr = h.Data.Signal.Atr.Value;
        double entry = h.Data.Signal.Last.Close;

        ExitPlan generic = planner.Build(h.Spec, h.Data, Side.Long, entry, null, MarketRegime.TrendUp, SmallCost(atr));

        var withinBounds = new StrategySignal
        {
            StrategyName = "T", Direction = Side.Long, Confidence = 0.8,
            InvalidationPrice = entry - (atr * 3.0),    // шире базового, но внутри границ
        };

        ExitPlan preferred = planner.Build(h.Spec, h.Data, Side.Long, entry, withinBounds, MarketRegime.TrendUp, SmallCost(atr));

        Assert.True(preferred.IsValid, preferred.Detail);
        Assert.True(preferred.StopDistance > generic.StopDistance);
        Assert.Equal(StopMethod.StrategyInvalidation, preferred.StopMethod);
    }

    [Fact]
    public void ATradeIsStillRefusedWhenEvenTheNarrowestCandidateIsUnacceptable()
    {
        // Отказ обязан остаться возможным: если даже базовый ATR-кандидат выходит за
        // границы, торговать нечем.
        var config = new EngineConfig();
        config.Exit.MaxStopInAtr = 1.0;
        config.Exit.AtrStopMultiple = 1.6;          // базовый кандидат уже за границей

        PipelineHarness h = WarmMarket(config);
        ExitPlanner planner = Planner(config, out _);

        double atr = h.Data.Signal.Atr.Value;
        ExitPlan plan = planner.Build(h.Spec, h.Data, Side.Long, h.Data.Signal.Last.Close, null, MarketRegime.TrendUp, SmallCost(atr));

        Assert.False(plan.IsValid);
        Assert.Equal(NoTradeReason.InvalidStopPlacement, plan.RejectionReason);
    }

    [Fact]
    public void TargetsAreWiderInTrendsAndTighterInRanges()
    {
        var config = new EngineConfig();
        PipelineHarness h = WarmMarket(config);
        ExitPlanner planner = Planner(config, out _);

        double atr = h.Data.Signal.Atr.Value;
        double entry = h.Data.Signal.Last.Close;

        ExitPlan trend = planner.Build(h.Spec, h.Data, Side.Long, entry, null, MarketRegime.TrendUp, SmallCost(atr));
        ExitPlan range = planner.Build(h.Spec, h.Data, Side.Long, entry, null, MarketRegime.Range, SmallCost(atr));

        Assert.True(trend.Target2R > range.Target2R);
        Assert.True(trend.TrailDistanceInAtr > range.TrailDistanceInAtr,
            "A trend runner needs a looser trail than a range trade.");
        Assert.True(trend.TimeStopBars > range.TimeStopBars);
    }

    [Fact]
    public void TheTargetsAndStopSurviveTickRounding()
    {
        var config = new EngineConfig();
        var coarse = new SymbolSpec("BTCUSD", 1.0, 5.0, 0, 0.01, 1000, 0.01, 35, 1.0, 0, 2.0, true);
        PipelineHarness h = WarmMarket(config);
        ExitPlanner planner = Planner(config, out _);

        double atr = h.Data.Signal.Atr.Value;
        double entry = h.Data.Signal.Last.Close;
        ExitPlan plan = planner.Build(coarse, h.Data, Side.Long, entry, null, MarketRegime.TrendUp, SmallCost(atr));

        Assert.True(plan.IsValid, plan.Detail);
        Assert.Equal(0.0, plan.StopPrice % 5.0, 6);

        // R is measured against the stop that will ACTUALLY be placed, after rounding.
        Assert.Equal(entry - plan.StopPrice, plan.StopDistance, 6);
    }
}

public class PositionManagerTests
{
    private static readonly SymbolSpec Spec = new SymbolSpec("BTCUSD", 0.01, 0.01, 2, 0.001, 1000, 0.001, 35, 0.01, 0, 2.0, true);

    private static OpenPosition Position(Side side = Side.Long, double entry = 50000, double stop = 49500, double units = 1.0) =>
        new OpenPosition
        {
            TradeId = "t1",
            SymbolName = "BTCUSD",
            Direction = side,
            EntryPrice = entry,
            InitialStopPrice = stop,
            CurrentStopPrice = stop,
            Target1Price = side == Side.Long ? entry + 600 : entry - 600,
            Target2Price = side == Side.Long ? entry + 1100 : entry - 1100,
            InitialVolumeInUnits = units,
            CurrentVolumeInUnits = units,
            RiskPerUnit = Math.Abs(entry - stop),
            EntryTimeUtc = RiskFixtures.T0,
        };

    private static ExitPlan Plan(Side side = Side.Long, double entry = 50000, double stop = 49500, int timeStopBars = 100) =>
        new ExitPlan
        {
            IsValid = true,
            StopPrice = stop,
            StopDistance = Math.Abs(entry - stop),
            StopInAtr = 1.5,
            Target1Price = side == Side.Long ? entry + 600 : entry - 600,
            Target2Price = side == Side.Long ? entry + 1100 : entry - 1100,
            Target1R = 1.2, Target2R = 2.2,
            Target1ClosePercent = 0.40, Target2ClosePercent = 0.35,
            BreakEvenTriggerR = 0.9,
            NetBreakEvenPrice = side == Side.Long ? entry + 50 : entry - 50,
            TrailDistanceInAtr = 2.2,
            TrailActivationR = 1.0,
            TimeStopBars = timeStopBars,
        };

    private static PipelineHarness Market()
    {
        var h = new PipelineHarness();
        h.Feed(new MarketSimulator(seed: 61).Generate(1200, 0.0003, 0.002));
        return h;
    }

    [Fact]
    public void DoesNothingWhileTheTradeIsStillDeveloping()
    {
        var manager = new PositionManager(new ExitConfig());
        IReadOnlyList<ExitAction> actions = manager.Evaluate(Position(), Plan(), 50100, Spec, Market().Data, null, onClosedBar: false);

        Assert.Empty(actions);
    }

    [Fact]
    public void MovesToNetBreakEvenOnceTheTriggerIsReached()
    {
        var manager = new PositionManager(new ExitConfig());
        OpenPosition p = Position();
        ExitPlan plan = Plan();

        // 0.9R on a 500-point risk is 450 points.
        IReadOnlyList<ExitAction> actions = manager.Evaluate(p, plan, 50460, Spec, Market().Data, null, false);

        ExitAction move = actions.FirstOrDefault(a => a.Kind == ExitActionKind.MoveStop);
        Assert.NotNull(move);
        Assert.True(move.NewStopPrice >= plan.NetBreakEvenPrice - 1e-6);
        Assert.True(move.NewStopPrice > p.EntryPrice,
            "Break-even must sit above entry for a long, so the scratch is genuinely flat.");
    }

    [Fact]
    public void AStopOnlyEverMovesInTheDirectionThatReducesRisk()
    {
        var manager = new PositionManager(new ExitConfig());
        SymbolDataSet data = Market().Data;

        OpenPosition p = Position();
        p.CurrentStopPrice = 50200;   // already well past break-even

        // Price falls back. Nothing may widen the stop.
        foreach (double price in new[] { 50150.0, 50000.0, 49800.0, 49600.0 })
        {
            IReadOnlyList<ExitAction> actions = manager.Evaluate(p, Plan(), price, Spec, data, null, false);
            foreach (ExitAction a in actions.Where(a => a.Kind == ExitActionKind.MoveStop))
            {
                Assert.True(a.NewStopPrice > p.CurrentStopPrice,
                    $"Stop would have been widened from {p.CurrentStopPrice} to {a.NewStopPrice}.");
            }
        }
    }

    [Fact]
    public void TakesAPartialAtTheFirstTargetAndLeavesARunner()
    {
        var manager = new PositionManager(new ExitConfig());
        OpenPosition p = Position(units: 1.0);
        ExitPlan plan = Plan();

        IReadOnlyList<ExitAction> actions = manager.Evaluate(p, plan, 50650, Spec, Market().Data, null, false);

        ExitAction partial = actions.FirstOrDefault(a => a.Kind == ExitActionKind.PartialClose);
        Assert.NotNull(partial);
        Assert.Equal(ExitReason.TakeProfit1, partial.Reason);
        Assert.Equal(0.40, partial.CloseVolumeInUnits, 6);
        Assert.True(partial.CloseVolumeInUnits < p.CurrentVolumeInUnits, "A partial must leave something running.");
    }

    [Fact]
    public void TheSecondTargetOnlyFiresAfterTheFirst()
    {
        var manager = new PositionManager(new ExitConfig());
        OpenPosition p = Position();

        IReadOnlyList<ExitAction> straightToTwo = manager.Evaluate(p, Plan(), 51200, Spec, Market().Data, null, false);
        Assert.DoesNotContain(straightToTwo, a => a.Reason == ExitReason.TakeProfit2);

        p.Target1Filled = true;
        IReadOnlyList<ExitAction> afterOne = manager.Evaluate(p, Plan(), 51200, Spec, Market().Data, null, false);
        Assert.Contains(afterOne, a => a.Reason == ExitReason.TakeProfit2);
    }

    [Fact]
    public void ATimeStopFiresOnlyWhenTheTradeHasGoneNowhere()
    {
        var manager = new PositionManager(new ExitConfig());
        SymbolDataSet data = Market().Data;

        OpenPosition stagnant = Position();
        stagnant.BarsHeld = 200;
        IReadOnlyList<ExitAction> a = manager.Evaluate(stagnant, Plan(timeStopBars: 100), 50020, Spec, data, null, onClosedBar: true);
        Assert.Contains(a, x => x.Reason == ExitReason.TimeStop);

        OpenPosition working = Position();
        working.BarsHeld = 200;
        IReadOnlyList<ExitAction> b = manager.Evaluate(working, Plan(timeStopBars: 100), 51000, Spec, data, null, onClosedBar: true);
        Assert.DoesNotContain(b, x => x.Reason == ExitReason.TimeStop);
    }

    [Fact]
    public void ProfitProtectionFiresOnlyAfterASubstantialMoveAndALargeGiveBack()
    {
        var manager = new PositionManager(new ExitConfig());
        SymbolDataSet data = Market().Data;

        // Small peak, large give-back: must NOT fire, or every trend is strangled early.
        OpenPosition small = Position();
        small.PeakOpenProfitR = 1.0;
        IReadOnlyList<ExitAction> a = manager.Evaluate(small, Plan(), 50050, Spec, data, null, false);
        Assert.DoesNotContain(a, x => x.Reason == ExitReason.ProfitProtection);

        // Large peak, large give-back: must fire.
        OpenPosition large = Position();
        large.PeakOpenProfitR = 3.0;
        IReadOnlyList<ExitAction> b = manager.Evaluate(large, Plan(), 50500, Spec, data, null, false);
        Assert.Contains(b, x => x.Reason == ExitReason.ProfitProtection);
    }

    [Fact]
    public void TracksTheJourneyExtremes()
    {
        var manager = new PositionManager(new ExitConfig());
        SymbolDataSet data = Market().Data;
        OpenPosition p = Position();

        manager.Evaluate(p, Plan(), 49700, Spec, data, null, false);   // -0.6R
        manager.Evaluate(p, Plan(), 51000, Spec, data, null, false);   // +2.0R
        manager.Evaluate(p, Plan(), 50200, Spec, data, null, false);   // +0.4R

        Assert.Equal(0.6, p.MaxAdverseExcursionR, 6);
        Assert.Equal(2.0, p.MaxFavourableExcursionR, 6);
        Assert.Equal(2.0, p.PeakOpenProfitR, 6);
    }

    [Fact]
    public void AShortPositionIsManagedAsTheMirrorOfALong()
    {
        var manager = new PositionManager(new ExitConfig());
        SymbolDataSet data = Market().Data;

        OpenPosition p = Position(Side.Short, 50000, 50500);
        ExitPlan plan = Plan(Side.Short);

        IReadOnlyList<ExitAction> actions = manager.Evaluate(p, plan, 49540, Spec, data, null, false);

        ExitAction move = actions.FirstOrDefault(a => a.Kind == ExitActionKind.MoveStop);
        Assert.NotNull(move);
        Assert.True(move.NewStopPrice < p.EntryPrice, "Break-even for a short must sit BELOW entry.");
        Assert.True(move.NewStopPrice < p.CurrentStopPrice);
    }

    [Fact]
    public void ADegenerateOrClosedPositionIsIgnoredSafely()
    {
        var manager = new PositionManager(new ExitConfig());
        SymbolDataSet data = Market().Data;

        Assert.Empty(manager.Evaluate(null, Plan(), 50000, Spec, data, null, false));

        OpenPosition closed = Position();
        closed.CurrentVolumeInUnits = 0;
        Assert.Empty(manager.Evaluate(closed, Plan(), 50000, Spec, data, null, false));
    }
}
