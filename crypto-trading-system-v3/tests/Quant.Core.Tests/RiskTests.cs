using System;
using System.Collections.Generic;
using System.Linq;
using Quant.Core.Config;
using Quant.Core.Numerics;
using Quant.Core.Primitives;
using Quant.Core.Risk;
using Quant.Core.Stats;
using Xunit;

namespace Quant.Core.Tests;

internal static class RiskFixtures
{
    public static readonly DateTime T0 = new DateTime(2025, 6, 2, 8, 0, 0, DateTimeKind.Utc);

    public static AccountSnapshot Account(double equity, double balance = 0, double? marginLevel = null, bool isLive = false) =>
        new AccountSnapshot(balance > 0 ? balance : equity, equity, 0, equity, marginLevel, 50, isLive, "USD");

    public static RiskEngine Engine(out DrawdownTracker dd, out ExecutionQualityTracker exec, out PerformanceStore perf, EngineConfig config = null)
    {
        config ??= new EngineConfig();
        dd = new DrawdownTracker();
        exec = new ExecutionQualityTracker(config.Execution);
        perf = new PerformanceStore(config.Adaptation);
        return new RiskEngine(config.Risk, config.Sizing, dd, exec, perf);
    }
}

public class DrawdownTrackerTests
{
    [Fact]
    public void TracksAllTimeDrawdownFromThePeak()
    {
        var t = new DrawdownTracker();
        DateTime now = RiskFixtures.T0;

        t.Observe(now, 10000);
        t.Observe(now.AddMinutes(5), 11000);
        t.Observe(now.AddMinutes(10), 9900);

        Assert.Equal(11000, t.AllTimePeak, 6);
        Assert.Equal(10.0, t.AllTimeDrawdownPercent, 6);
    }

    [Fact]
    public void ShortAndLongHorizonsCanDisagreeAndThatIsThePoint()
    {
        var t = new DrawdownTracker();
        DateTime now = RiskFixtures.T0;

        // A long, slow climb, then a sharp fall inside the last day.
        for (int day = 0; day < 20; day++) t.Observe(now.AddDays(day), 10000 + (day * 200));

        DateTime today = now.AddDays(20);
        t.Observe(today, 14000);
        t.Observe(today.AddHours(6), 13300);

        Assert.True(t.Drawdown24hPercent >= 4.9,
            $"24h drawdown was {t.Drawdown24hPercent:F2}%, which understates today's fall.");
        Assert.True(t.AllTimeDrawdownPercent >= 4.9);
        Assert.True(t.Drawdown30dPercent >= t.Drawdown24hPercent - 1e-9);
    }

    [Fact]
    public void HistoryStaysBoundedOverALongRun()
    {
        var t = new DrawdownTracker(maxPoints: 2000);
        DateTime now = RiskFixtures.T0;

        for (int i = 0; i < 200000; i++) t.Observe(now.AddMinutes(i), 10000 + (i % 500));

        // No assertion on internals; the contract is simply that this completes and the
        // tracker still answers. An unbounded history would exhaust memory in a 24/7 process.
        Assert.True(t.HasData);
        Assert.InRange(t.AllTimeDrawdownPercent, 0, 100);
    }

    [Fact]
    public void RejectsNonsensicalEquityValues()
    {
        var t = new DrawdownTracker();
        t.Observe(RiskFixtures.T0, 10000);
        t.Observe(RiskFixtures.T0.AddMinutes(1), double.NaN);
        t.Observe(RiskFixtures.T0.AddMinutes(2), -5);

        Assert.Equal(10000, t.CurrentEquity, 6);
    }

    [Fact]
    public void SurvivesARestartWithItsPeakIntact()
    {
        var t = new DrawdownTracker();
        t.Restore(allTimePeak: 12000, currentEquity: 11000, RiskFixtures.T0);

        Assert.Equal(12000, t.AllTimePeak, 6);
        Assert.InRange(t.AllTimeDrawdownPercent, 8.3, 8.4);
    }
}

public class RiskOfRuinTests
{
    [Fact]
    public void ANegativeEdgeIsEssentiallyCertainRuin()
    {
        var estimator = new RiskOfRuinEstimator(paths: 2000, horizonTrades: 400);
        RuinEstimate r = estimator.Estimate(winRate: 0.30, payoffRatio: 1.0, riskFraction: 0.02, ruinThresholdFraction: 0.35);

        Assert.True(r.Probability > 0.9, $"Ruin probability was only {r.Probability:P1} for a clearly losing system.");
    }

    [Fact]
    public void AStrongEdgeAtSmallSizeIsSafe()
    {
        var estimator = new RiskOfRuinEstimator(paths: 2000, horizonTrades: 400);
        RuinEstimate r = estimator.Estimate(0.55, 2.0, riskFraction: 0.005, ruinThresholdFraction: 0.35);

        Assert.True(r.Probability < 0.01, $"Ruin probability was {r.Probability:P2} for a strong edge at half a percent risk.");
    }

    [Fact]
    public void LargerPositionsRaiseRuinProbabilityAtTheSameEdge()
    {
        var estimator = new RiskOfRuinEstimator(paths: 3000, horizonTrades: 300);

        double small = estimator.Estimate(0.45, 1.6, 0.005, 0.35).Probability;
        double large = estimator.Estimate(0.45, 1.6, 0.05, 0.35).Probability;

        Assert.True(large > small, $"risk 0.5% -> {small:P2}, risk 5% -> {large:P2}");
    }

    [Fact]
    public void LossClusteringMakesRuinMoreLikelyThanIndependenceSuggests()
    {
        var estimator = new RiskOfRuinEstimator(paths: 4000, horizonTrades: 300);

        double independent = estimator.Estimate(0.45, 1.6, 0.02, 0.35, lossClusteringFactor: 0.0).Probability;
        double clustered = estimator.Estimate(0.45, 1.6, 0.02, 0.35, lossClusteringFactor: 0.35).Probability;

        Assert.True(clustered > independent,
            $"Clustered losses ({clustered:P2}) must not be safer than independent ones ({independent:P2}).");
    }

    [Fact]
    public void TheEstimateIsReproducibleForAGivenSeed()
    {
        var a = new RiskOfRuinEstimator(1000, 200, seed: 7);
        var b = new RiskOfRuinEstimator(1000, 200, seed: 7);

        Assert.Equal(a.Estimate(0.5, 1.5, 0.01, 0.35).Probability, b.Estimate(0.5, 1.5, 0.01, 0.35).Probability, 10);
    }

    [Fact]
    public void TheClosedFormAgreesInDirectionWithTheSimulation()
    {
        var estimator = new RiskOfRuinEstimator(3000, 400);

        double simulatedWeak = estimator.Estimate(0.40, 1.2, 0.03, 0.35).Probability;
        double simulatedStrong = estimator.Estimate(0.60, 2.0, 0.005, 0.35).Probability;

        double closedWeak = RiskOfRuinEstimator.ClassicalApproximation(0.40, 1.2, 0.03, 0.35);
        double closedStrong = RiskOfRuinEstimator.ClassicalApproximation(0.60, 2.0, 0.005, 0.35);

        Assert.True(simulatedWeak > simulatedStrong);
        Assert.True(closedWeak > closedStrong);
    }
}

public class ExecutionQualityTests
{
    [Fact]
    public void StartsNeutralRatherThanPerfect()
    {
        var t = new ExecutionQualityTracker(new ExecutionConfig());
        Assert.Equal(0.85, t.Quality, 6);
        Assert.Equal(1.0, t.SlippageMultiplier, 6);
    }

    [Fact]
    public void FillsMatchingPredictionScoreWell()
    {
        var t = new ExecutionQualityTracker(new ExecutionConfig());
        for (int i = 0; i < 50; i++) t.RecordFill(100.0, 100.10, isBuy: true, predictedSlippage: 0.10, pipSize: 0.01);

        Assert.InRange(t.SlippageMultiplier, 0.95, 1.05);
        Assert.True(t.Quality > 0.85, $"Quality was {t.Quality:P0} for fills exactly matching prediction.");
    }

    [Fact]
    public void PersistentlyWorseFillsDegradeQuality()
    {
        var t = new ExecutionQualityTracker(new ExecutionConfig());
        for (int i = 0; i < 50; i++) t.RecordFill(100.0, 100.40, isBuy: true, predictedSlippage: 0.10, pipSize: 0.01);

        Assert.True(t.SlippageMultiplier > 3.0);
        Assert.True(t.Quality < 0.45, $"Quality was {t.Quality:P0} for fills four times worse than predicted.");
    }

    [Fact]
    public void SlippageSignIsDirectionAware()
    {
        var t = new ExecutionQualityTracker(new ExecutionConfig());

        // Selling BELOW the requested price is adverse for a short seller.
        for (int i = 0; i < 20; i++) t.RecordFill(100.0, 99.80, isBuy: false, predictedSlippage: 0.10, pipSize: 0.01);
        Assert.True(t.SlippageMultiplier > 1.5);

        var favourable = new ExecutionQualityTracker(new ExecutionConfig());
        for (int i = 0; i < 20; i++) favourable.RecordFill(100.0, 100.20, isBuy: false, predictedSlippage: 0.10, pipSize: 0.01);
        Assert.True(favourable.SlippageMultiplier < 0.5, "Selling above the requested price is favourable, not adverse.");
    }

    [Fact]
    public void RejectionsCountAgainstQuality()
    {
        var t = new ExecutionQualityTracker(new ExecutionConfig());
        for (int i = 0; i < 40; i++) t.RecordFill(100.0, 100.10, true, 0.10, 0.01);
        double before = t.Quality;

        for (int i = 0; i < 20; i++) t.RecordRejection();

        Assert.True(t.RejectionRate > 0.3);
        Assert.True(t.Quality < before);
    }
}

public class AnomalyDetectorTests
{
    private static Quant.Core.Features.FeatureVector Features(
        double return1InAtr = 0.2, double volumeZ = 0, double atrZ = 0, double spreadPercentile = 0.5,
        double spreadToAtr = 0.02, double atrPercentile = 0.5, double volExpansion = 0, double tickVelocityZ = 0,
        double tickInterArrivalZ = 0) => new Quant.Core.Features.FeatureVector
        {
            SymbolName = "BTCUSD",
            Return1InAtr = return1InAtr,
            VolumeZScore = volumeZ,
            AtrZScore = atrZ,
            SpreadPercentile = spreadPercentile,
            SpreadToAtr = spreadToAtr,
            AtrPercentile = atrPercentile,
            VolatilityExpansion = volExpansion,
            TickVelocityZScore = tickVelocityZ,
            TickInterArrivalZScore = tickInterArrivalZ,
        };

    [Fact]
    public void EveryAnomalyThresholdIsReadFromTheConfigurationAndActuallyBinds()
    {
        // Проверка появилась потому, что мутации прошли незамеченными: пороги детектора
        // можно было сдвинуть на два порядка, и ни один тест не падал. Порог, который
        // ничего не держит, отличается от отсутствующего только тем, что его видно.
        var config = new RiskConfig();
        var d = new AnomalyDetector(config);

        // Спред относительно ATR: по умолчанию 0.30.
        Assert.False(d.Evaluate(Features(spreadToAtr: config.AnomalousSpreadToAtr * 0.5)).IsExtremeEvent);
        AnomalyReport wide = d.Evaluate(Features(spreadToAtr: config.AnomalousSpreadToAtr * 1.5));
        Assert.True(wide.IsExtremeEvent);
        Assert.True(wide.Severity >= config.AnomalousSpreadToAtrSeverity);

        // Предельная волатильность, которая всё ещё расширяется.
        var stillExpanding = new AnomalyDetector(config);
        AnomalyReport blowing = stillExpanding.Evaluate(Features(
            atrPercentile: config.ExtremeVolatilityPercentile + 0.005,
            volExpansion: config.ExpandingVolatilityFraction * 1.5));
        Assert.True(blowing.Severity >= config.ExtremeVolatilitySeverity);

        // Исчезнувшая ликвидность.
        AnomalyReport illiquid = new AnomalyDetector(config).Evaluate(
            Features(spreadPercentile: config.ExtremeSpreadPercentile + 0.01));
        Assert.True(illiquid.Severity >= config.ExtremeSpreadSeverity);

        // И каждый из них двигается вместе с конфигурацией, а не живёт своей жизнью.
        var relaxed = new RiskConfig { AnomalousSpreadToAtr = 0.90 };
        Assert.False(new AnomalyDetector(relaxed)
            .Evaluate(Features(spreadToAtr: 0.45)).IsExtremeEvent);
    }

    [Fact]
    public void TheAnomalyEscalationLadderUsesItsConfiguredRungs()
    {
        // Ступени постуры по тяжести аномалии тоже были вписаны в код, и их тоже никто
        // не проверял: мутация порога до недостижимого значения проходила молча.
        var config = new EngineConfig();
        var risk = new RiskEngine(config.Risk, config.Sizing, new DrawdownTracker(),
            new ExecutionQualityTracker(config.Execution), new PerformanceStore(config.Adaptation),
            new RiskOfRuinEstimator(seed: 11));

        var account = new AccountSnapshot(10000, 10000, 0, 10000, 2000, 50, false, "USD");
        double below = config.Risk.AnomalyCautionSeverity * 0.5;
        double middle = (config.Risk.AnomalyCautionSeverity + config.Risk.AnomalyDefensiveSeverity) / 2.0;
        double above = Math.Min(1.0, config.Risk.AnomalyDefensiveSeverity + 0.1);

        Assert.Equal(RiskState.Normal, risk.Evaluate(RiskFixtures.T0, account, below, false).State);

        var second = new RiskEngine(config.Risk, config.Sizing, new DrawdownTracker(),
            new ExecutionQualityTracker(config.Execution), new PerformanceStore(config.Adaptation),
            new RiskOfRuinEstimator(seed: 12));
        Assert.Equal(RiskState.Caution, second.Evaluate(RiskFixtures.T0, account, middle, false).State);

        var third = new RiskEngine(config.Risk, config.Sizing, new DrawdownTracker(),
            new ExecutionQualityTracker(config.Execution), new PerformanceStore(config.Adaptation),
            new RiskOfRuinEstimator(seed: 13));
        Assert.Equal(RiskState.Defensive, third.Evaluate(RiskFixtures.T0, account, above, false).State);
    }

    [Fact]
    public void AnEscalationRungAboveTheReachableMaximumIsRefusedByValidation()
    {
        // Тяжесть аномалии ограничена единицей по построению. Ступень выше неё
        // недостижима, сколько бы рынок ни ломался, — те же ворота, что и всегда.
        var config = new EngineConfig();
        config.Risk.AnomalyDefensiveSeverity = 1.9;

        Assert.Contains(config.Validate(), p => p.Contains("AnomalyDefensiveSeverity"));
    }

    [Fact]
    public void AQuietMarketProducesNoFindings()
    {
        var d = new AnomalyDetector(new RiskConfig());
        AnomalyReport r = d.Evaluate(Features());

        Assert.Equal(0, r.Severity);
        Assert.False(r.IsExtremeEvent);
        Assert.Equal("normal", r.Summary);
    }

    [Fact]
    public void AViolentBarIsFlaggedAsExtreme()
    {
        var d = new AnomalyDetector(new RiskConfig());
        AnomalyReport r = d.Evaluate(Features(return1InAtr: -6.0));

        Assert.True(r.IsExtremeEvent);
        Assert.True(r.Severity > 0.8);
    }

    [Fact]
    public void SimultaneousPriceVolumeAndSpreadStressIsADislocationEvenWhenNoneAloneWouldBe()
    {
        var d = new AnomalyDetector(new RiskConfig());

        // Individually tolerable readings.
        Assert.False(d.Evaluate(Features(return1InAtr: 2.5)).IsExtremeEvent);
        d.Reset();
        Assert.False(d.Evaluate(Features(volumeZ: 2.5)).IsExtremeEvent);
        d.Reset();
        Assert.False(d.Evaluate(Features(spreadPercentile: 0.93)).IsExtremeEvent);
        d.Reset();

        // All three at once.
        AnomalyReport combined = d.Evaluate(Features(return1InAtr: 2.5, volumeZ: 2.5, spreadPercentile: 0.93));
        Assert.True(combined.IsExtremeEvent);
        Assert.Contains(combined.Findings, f => f.Contains("dislocation"));
    }

    [Fact]
    public void TheRecoveryHoldRunsForTheConfiguredNumberOfBars()
    {
        var config = new RiskConfig { RecoveryBarsAfterExtremeEvent = 5 };
        var d = new AnomalyDetector(config);

        Assert.False(d.InRecoveryPeriod);

        d.Evaluate(Features(return1InAtr: -6.0));
        Assert.True(d.InRecoveryPeriod);

        for (int i = 0; i < 4; i++) { d.OnBarClosed(); Assert.True(d.InRecoveryPeriod, $"Left the hold after {i + 1} bars."); }

        d.OnBarClosed();
        Assert.False(d.InRecoveryPeriod);
    }

    [Fact]
    public void MissingFeaturesAreTreatedAsMaximallyAnomalous()
    {
        var d = new AnomalyDetector(new RiskConfig());
        AnomalyReport r = d.Evaluate(null);

        Assert.Equal(1.0, r.Severity);
        Assert.True(r.IsExtremeEvent);
    }
}

public class RiskEngineTests
{
    [Fact]
    public void StartsNormalAndAllowsFullRiskOnAHealthyAccount()
    {
        RiskEngine engine = RiskFixtures.Engine(out _, out _, out _);
        RiskAssessment a = engine.Evaluate(RiskFixtures.T0, RiskFixtures.Account(10000), 0, false);

        Assert.Equal(RiskState.Normal, a.State);
        Assert.True(a.AllowsNewPositions);
        Assert.True(a.RiskMultiplier > 0.7, $"Risk multiplier was {a.RiskMultiplier:F2} on a healthy account.");
    }

    [Fact]
    public void HaltsOnTheDailyLossLimit()
    {
        var config = new EngineConfig();
        RiskEngine engine = RiskFixtures.Engine(out _, out _, out _, config);

        engine.Evaluate(RiskFixtures.T0, RiskFixtures.Account(10000), 0, false);
        RiskAssessment a = engine.Evaluate(RiskFixtures.T0.AddHours(3), RiskFixtures.Account(9700), 0, false);

        Assert.Equal(RiskState.Halt, a.State);
        Assert.Equal(NoTradeReason.DailyLossLimit, a.BlockingReason);
        Assert.Equal(0, a.RiskMultiplier);
        Assert.False(a.AllowsNewPositions);
    }

    [Fact]
    public void TrimsRiskProgressivelyAsTheDailyLimitIsApproached()
    {
        RiskEngine engine = RiskFixtures.Engine(out _, out _, out _);
        engine.Evaluate(RiskFixtures.T0, RiskFixtures.Account(10000), 0, false);

        double atFlat = engine.Evaluate(RiskFixtures.T0.AddMinutes(30), RiskFixtures.Account(10000), 0, false).RiskMultiplier;
        double atHalfPercent = engine.Evaluate(RiskFixtures.T0.AddHours(1), RiskFixtures.Account(9950), 0, false).RiskMultiplier;
        double atOnePercent = engine.Evaluate(RiskFixtures.T0.AddHours(2), RiskFixtures.Account(9900), 0, false).RiskMultiplier;

        Assert.True(atHalfPercent < atFlat, "Risk should start trimming before a limit is reached, not at the cliff edge.");
        Assert.True(atOnePercent < atHalfPercent);
    }

    [Fact]
    public void ConsecutiveLossesEscalateThePostureInOrder()
    {
        var config = new EngineConfig();
        RiskEngine engine = RiskFixtures.Engine(out _, out _, out _, config);

        DateTime t = RiskFixtures.T0;
        engine.Evaluate(t, RiskFixtures.Account(10000), 0, false);

        var observed = new List<RiskState>();
        for (int i = 1; i <= config.Risk.LossesBeforeHalt; i++)
        {
            engine.OnTradeClosed(t, isWin: false);
            t = t.AddMinutes(10);
            observed.Add(engine.Evaluate(t, RiskFixtures.Account(10000), 0, false).State);
        }

        Assert.Equal(RiskState.Normal, observed[0]);
        Assert.Equal(RiskState.Caution, observed[config.Risk.LossesBeforeRiskReduction - 1]);
        Assert.Equal(RiskState.Defensive, observed[config.Risk.LossesBeforeDefensive - 1]);
        Assert.Equal(RiskState.Halt, observed[config.Risk.LossesBeforeHalt - 1]);
    }

    [Fact]
    public void RiskIsNeverIncreasedByALoss()
    {
        // The anti-martingale invariant, checked exhaustively rather than argued.
        RiskEngine engine = RiskFixtures.Engine(out _, out _, out _);
        DateTime t = RiskFixtures.T0;

        double previous = engine.Evaluate(t, RiskFixtures.Account(10000), 0, false).RiskMultiplier;
        double equity = 10000;

        for (int i = 0; i < 20; i++)
        {
            engine.OnTradeClosed(t, isWin: false);
            equity *= 0.999;
            t = t.AddMinutes(20);

            double now = engine.Evaluate(t, RiskFixtures.Account(equity), 0, false).RiskMultiplier;
            Assert.True(now <= previous + 1e-9,
                $"Risk multiplier rose from {previous:F4} to {now:F4} after loss {i + 1}. That is a martingale.");
            previous = now;
        }
    }

    [Fact]
    public void WinningStreaksDoNotScaleRiskByDefault()
    {
        RiskEngine engine = RiskFixtures.Engine(out _, out _, out _);
        DateTime t = RiskFixtures.T0;

        double baseline = engine.Evaluate(t, RiskFixtures.Account(10000), 0, false).RiskMultiplier;

        for (int i = 0; i < 12; i++)
        {
            engine.OnTradeClosed(t, isWin: true);
            t = t.AddMinutes(20);
        }

        double afterStreak = engine.Evaluate(t, RiskFixtures.Account(10000), 0, false).RiskMultiplier;
        Assert.Equal(baseline, afterStreak, 6);
    }

    [Fact]
    public void ConfigurationForbidsAggressiveWinStreakScaling()
    {
        var config = new EngineConfig();
        config.Risk.MaxWinStreakRiskMultiplier = 3.0;

        Assert.Contains(config.Validate(), p => p.Contains("aggressive scaling"));
    }

    [Fact]
    public void ConfigurationForbidsANonMonotonicRiskLadder()
    {
        var config = new EngineConfig();
        config.Risk.DefensiveRiskMultiplier = 0.9;
        config.Risk.CautionRiskMultiplier = 0.5;

        Assert.Contains(config.Validate(), p => p.Contains("monotonic"));
    }

    [Fact]
    public void APostureDoesNotRelaxImmediatelyWhenConditionsImprove()
    {
        var config = new EngineConfig();
        RiskEngine engine = RiskFixtures.Engine(out _, out _, out _, config);

        DateTime t = RiskFixtures.T0;
        engine.Evaluate(t, RiskFixtures.Account(10000), 0, false);

        for (int i = 0; i < config.Risk.LossesBeforeDefensive; i++) engine.OnTradeClosed(t, false);
        t = t.AddMinutes(5);
        Assert.Equal(RiskState.Defensive, engine.Evaluate(t, RiskFixtures.Account(10000), 0, false).State);

        // Conditions look fine again immediately, but the dwell time has not elapsed.
        engine.OnTradeClosed(t, isWin: true);
        t = t.AddMinutes(1);
        Assert.Equal(RiskState.Defensive, engine.Evaluate(t, RiskFixtures.Account(10000), 0, false).State);
    }

    [Fact]
    public void DeEscalationProceedsOneStepAtATime()
    {
        var config = new EngineConfig();
        config.Risk.MinMinutesInRiskState = 1;
        RiskEngine engine = RiskFixtures.Engine(out _, out _, out _, config);

        DateTime t = RiskFixtures.T0;
        engine.Evaluate(t, RiskFixtures.Account(10000), 0, false);

        for (int i = 0; i < config.Risk.LossesBeforeHalt; i++) engine.OnTradeClosed(t, false);
        t = t.AddMinutes(5);
        Assert.Equal(RiskState.Halt, engine.Evaluate(t, RiskFixtures.Account(10000), 0, false).State);

        engine.OnTradeClosed(t, isWin: true);

        // Equity climbing well past the recovery threshold, one evaluation at a time.
        var seen = new List<RiskState>();
        double equity = 10000;
        for (int i = 0; i < 4; i++)
        {
            t = t.AddMinutes(10);
            equity *= 1.01;
            seen.Add(engine.Evaluate(t, RiskFixtures.Account(equity), 0, false).State);
        }

        Assert.Equal(new[] { RiskState.Defensive, RiskState.Caution, RiskState.Normal, RiskState.Normal }, seen);
    }

    [Fact]
    public void AMarginLevelNearStopOutHaltsTrading()
    {
        RiskEngine engine = RiskFixtures.Engine(out _, out _, out _);

        RiskAssessment a = engine.Evaluate(RiskFixtures.T0, RiskFixtures.Account(10000, marginLevel: 120), 0, false);

        Assert.Equal(RiskState.Halt, a.State);
        Assert.Equal(NoTradeReason.MarginGuard, a.BlockingReason);
    }

    [Fact]
    public void AnExtremeEventForcesDefensiveAndCutsSize()
    {
        RiskEngine engine = RiskFixtures.Engine(out _, out _, out _);
        engine.Evaluate(RiskFixtures.T0, RiskFixtures.Account(10000), 0, false);

        RiskAssessment a = engine.Evaluate(RiskFixtures.T0.AddMinutes(5), RiskFixtures.Account(10000), anomalySeverity: 0.95, inRecoveryPeriod: true);

        Assert.Equal(RiskState.Defensive, a.State);
        Assert.True(a.RiskMultiplier < 0.25, $"Risk multiplier was {a.RiskMultiplier:F3} during an extreme event.");
    }

    [Fact]
    public void ForceHaltStopsEverythingImmediately()
    {
        RiskEngine engine = RiskFixtures.Engine(out _, out _, out _);
        engine.Evaluate(RiskFixtures.T0, RiskFixtures.Account(10000), 0, false);

        engine.ForceHalt(RiskFixtures.T0, "position reconciliation mismatch");
        RiskAssessment a = engine.Evaluate(RiskFixtures.T0.AddMinutes(1), RiskFixtures.Account(10000), 0, false);

        Assert.Equal(RiskState.Halt, a.State);
        Assert.False(a.AllowsNewPositions);
        Assert.Equal("position reconciliation mismatch", engine.LastForcedHaltReason);
    }

    [Fact]
    public void AnUnusableAccountSnapshotHaltsRatherThanGuesses()
    {
        RiskEngine engine = RiskFixtures.Engine(out _, out _, out _);
        RiskAssessment a = engine.Evaluate(RiskFixtures.T0, new AccountSnapshot(0, double.NaN, 0, 0, null, 50, false, "USD"), 0, false);

        Assert.Equal(RiskState.Halt, a.State);
        Assert.False(a.AllowsNewPositions);
    }

    [Fact]
    public void DailyAndWeeklyReferencesRollAtTheirBoundaries()
    {
        RiskEngine engine = RiskFixtures.Engine(out _, out _, out _);

        // Monday.
        DateTime monday = new DateTime(2025, 6, 2, 1, 0, 0, DateTimeKind.Utc);
        engine.Evaluate(monday, RiskFixtures.Account(10000), 0, false);
        Assert.Equal(10000, engine.DayStartEquity, 6);
        Assert.Equal(10000, engine.WeekStartEquity, 6);

        // Tuesday: the daily reference resets, the weekly one does not.
        DateTime tuesday = monday.AddDays(1);
        engine.Evaluate(tuesday, RiskFixtures.Account(9800), 0, false);
        Assert.Equal(9800, engine.DayStartEquity, 6);
        Assert.Equal(10000, engine.WeekStartEquity, 6);

        // Next Monday: both reset.
        DateTime nextMonday = monday.AddDays(7);
        engine.Evaluate(nextMonday, RiskFixtures.Account(9600), 0, false);
        Assert.Equal(9600, engine.DayStartEquity, 6);
        Assert.Equal(9600, engine.WeekStartEquity, 6);
    }

    [Fact]
    public void StateSurvivesARestart()
    {
        var config = new EngineConfig();
        RiskEngine engine = RiskFixtures.Engine(out _, out _, out _, config);

        DateTime cooldown = RiskFixtures.T0.AddMinutes(45);
        engine.Restore(RiskState.Defensive, consecutiveLosses: 4, consecutiveWins: 0, cooldown,
            RiskFixtures.T0.Date, 10300, RiskFixtures.T0.Date, 10400, RiskFixtures.T0);

        RiskAssessment a = engine.Evaluate(RiskFixtures.T0.AddMinutes(1), RiskFixtures.Account(10200), 0, false);

        // The restored loss counter, the restored cooldown and the restored daily reference
        // all survive: without them a restart would silently reset every guard that a bad
        // session had just triggered.
        Assert.Equal(4, a.ConsecutiveLosses);
        Assert.Equal(0, a.RiskMultiplier);
        Assert.Equal(NoTradeReason.ConsecutiveLossCooldown, a.BlockingReason);
        Assert.Equal(cooldown, a.CooldownUntilUtc);
        Assert.Contains(a.Reasons, r => r.Contains("cooldown until"));
        Assert.InRange(a.DailyLossPercent, 0.9, 1.1);
        Assert.InRange(a.WeeklyLossPercent, 1.9, 2.0);
    }
}
