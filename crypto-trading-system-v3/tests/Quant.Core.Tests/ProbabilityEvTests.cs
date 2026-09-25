using System;
using Quant.Core.Config;
using Quant.Core.Ev;
using Quant.Core.Numerics;
using Quant.Core.Primitives;
using Quant.Core.Probability;
using Quant.Core.Stats;
using Xunit;

namespace Quant.Core.Tests;

internal static class Fixtures
{
    public static TradeRecord Trade(
        string strategy, bool win, double r,
        MarketRegime regime = MarketRegime.TrendUp,
        double ensembleConfidence = 0.7,
        double statedProbability = 0.6,
        double mfeR = 0,
        string symbol = "BTCUSD") => new TradeRecord
        {
            TradeId = Guid.NewGuid().ToString("N"),
            StrategyName = strategy,
            SymbolName = symbol,
            Direction = Side.Long,
            Regime = regime,
            R = win ? Math.Abs(r) : -Math.Abs(r),
            MaeR = win ? 0.4 : 1.0,
            MfeR = mfeR > 0 ? mfeR : (win ? Math.Abs(r) * 1.2 : 0.3),
            EnsembleConfidence = ensembleConfidence,
            EstimatedWinProbability = statedProbability,
            PlannedRewardToRisk = 2.0,
            EntryTimeUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ExitTimeUtc = new DateTime(2025, 1, 1, 2, 0, 0, DateTimeKind.Utc),
            ExitReason = win ? ExitReason.TakeProfit1 : ExitReason.StopLoss,
        };

    public static SymbolSpec Spec(double commissionPerMillion = 35) =>
        new SymbolSpec("BTCUSD", 0.01, 0.01, 2, 0.01, 100, 0.01, commissionPerMillion, 0.01, 0, 2.0, true);

    public static (BayesianProbabilityModel Model, PerformanceStore Store, CalibrationTracker Cal) BuildModel(EngineConfig config = null)
    {
        config ??= new EngineConfig();
        var store = new PerformanceStore(config.Adaptation);
        var cal = new CalibrationTracker(config.Probability);
        var model = new BayesianProbabilityModel(config.Probability, config.Ev, store, cal);
        return (model, store, cal);
    }

    public static ProbabilityQuery Query(string strategy = "TrendFollowing", double rr = 2.0, double confidence = 0.7,
        MarketRegime regime = MarketRegime.TrendUp) => new ProbabilityQuery
        {
            StrategyName = strategy,
            SymbolName = "BTCUSD",
            Regime = regime,
            Direction = Side.Long,
            EnsembleConfidence = confidence,
            RewardToRisk = rr,
            Session = SessionKind.Europe,
            VolatilityBucket = VolatilityBucket.Normal,
        };
}

public class BayesianProbabilityModelTests
{
    [Fact]
    public void WithNoHistoryTheEstimateSitsAtBreakEvenNotAtAFlatteringGuess()
    {
        (BayesianProbabilityModel model, _, _) = Fixtures.BuildModel();

        ProbabilityEstimate e = model.Estimate(Fixtures.Query(rr: 2.0));

        // Break-even at 2:1 is 33%. The prior blends that with the configured base rate of
        // 40%, giving roughly 37% -- an untested setup must start with no edge, not an
        // assumed one.
        Assert.InRange(e.PWin, 0.30, 0.42);
        Assert.True(e.Confidence < 0.4, "An estimate with no trade history must not be confident.");
    }

    [Fact]
    public void TheBreakEvenAnchorTracksTheRequestedRewardToRisk()
    {
        (BayesianProbabilityModel model, _, _) = Fixtures.BuildModel();

        double atOneToOne = model.Estimate(Fixtures.Query(rr: 1.0)).PWin;
        double atFourToOne = model.Estimate(Fixtures.Query(rr: 4.0)).PWin;

        // A 1:1 trade needs a much higher win rate to break even than a 4:1 trade, so the
        // uninformed prior must sit higher for the former.
        Assert.True(atOneToOne > atFourToOne,
            $"1:1 prior {atOneToOne:P1} should exceed 4:1 prior {atFourToOne:P1}.");
    }

    [Fact]
    public void ASmallWinningSampleBarelyMovesTheEstimate()
    {
        (BayesianProbabilityModel model, PerformanceStore store, _) = Fixtures.BuildModel();
        double before = model.Estimate(Fixtures.Query()).PWin;

        for (int i = 0; i < 5; i++)
        {
            TradeRecord t = Fixtures.Trade("TrendFollowing", win: true, 2.0);
            store.Record(t); model.Observe(t);
        }

        double after = model.Estimate(Fixtures.Query()).PWin;
        Assert.True(after - before < 0.15,
            $"Five wins moved the estimate from {before:P1} to {after:P1}; that is too much for five trades.");
        Assert.True(after > before, "Five wins should still move it somewhat.");
    }

    [Fact]
    public void ALargeSampleIsAllowedToDominateThePrior()
    {
        (BayesianProbabilityModel model, PerformanceStore store, _) = Fixtures.BuildModel();

        // The stated probability matches what is actually delivered, so the model is
        // well calibrated and its posterior is allowed to show through.
        for (int i = 0; i < 600; i++)
        {
            TradeRecord t = Fixtures.Trade("TrendFollowing", win: i % 10 < 7, 2.0, statedProbability: 0.70);
            store.Record(t); model.Observe(t);
        }

        ProbabilityEstimate e = model.Estimate(Fixtures.Query());
        Assert.InRange(e.PWin, 0.62, 0.76);
        Assert.True(e.Confidence > 0.55, $"Confidence was {e.Confidence:F2} after 600 trades.");
        Assert.True(e.StandardError < 0.05);
    }

    [Fact]
    public void UncertaintyAndConfidenceImproveAsEvidenceAccumulates()
    {
        (BayesianProbabilityModel model, PerformanceStore store, _) = Fixtures.BuildModel();

        ProbabilityEstimate cold = model.Estimate(Fixtures.Query());

        while (store.Overall.TotalTrades < 500)
        {
            TradeRecord t = Fixtures.Trade("TrendFollowing",
                win: store.Overall.TotalTrades % 3 != 0, 2.0, statedProbability: 0.667);
            store.Record(t); model.Observe(t);
        }

        ProbabilityEstimate warm = model.Estimate(Fixtures.Query());

        Assert.True(warm.StandardError < cold.StandardError,
            $"Standard error did not fall: {cold.StandardError:F4} -> {warm.StandardError:F4}.");
        Assert.True(warm.Confidence > cold.Confidence,
            $"Confidence did not rise: {cold.Confidence:F3} -> {warm.Confidence:F3}.");
        Assert.True(warm.EffectiveSample > cold.EffectiveSample);
    }

    [Fact]
    public void ADemonstrablyOverconfidentHistoryPullsEstimatesBackTowardBreakEven()
    {
        // The safety behaviour: if the model has been stating 80% and delivering 45%, its
        // future estimates must be marked down rather than taken at face value.
        (BayesianProbabilityModel model, PerformanceStore store, _) = Fixtures.BuildModel();

        for (int i = 0; i < 600; i++)
        {
            TradeRecord t = Fixtures.Trade("TrendFollowing", win: i % 20 < 9, 2.0, statedProbability: 0.80);
            store.Record(t); model.Observe(t);
        }

        ProbabilityEstimate e = model.Estimate(Fixtures.Query());

        Assert.True(e.PWin < 0.55,
            $"An overconfident record still produced {e.PWin:P1}.");
        Assert.True(e.CalibrationQuality < 0.4,
            $"Calibration quality was {e.CalibrationQuality:F2} despite a 35-point error.");

        // And the marked-down estimate must carry WIDER error bars than an equally large
        // but well-calibrated history would produce: knowing a model is biased is not the
        // same as knowing the true value.
        (BayesianProbabilityModel honest, PerformanceStore honestStore, _) = Fixtures.BuildModel();
        for (int i = 0; i < 600; i++)
        {
            TradeRecord t = Fixtures.Trade("TrendFollowing", win: i % 20 < 9, 2.0, statedProbability: 0.45);
            honestStore.Record(t); honest.Observe(t);
        }
        ProbabilityEstimate honestEstimate = honest.Estimate(Fixtures.Query());

        Assert.True(e.StandardError > honestEstimate.StandardError,
            $"A biased model reported tighter error bars ({e.StandardError:F4}) than an honest one ({honestEstimate.StandardError:F4}) on the same sample size.");
        Assert.True(honestEstimate.CalibrationQuality > e.CalibrationQuality);
    }

    [Fact]
    public void NestedHistorySlicesAreNotCountedOncePerHierarchyLevel()
    {
        // Every trade in a confidence bucket is also in its regime slice, its strategy slice
        // and the global slice. Applying each level's counts unconditionally would read the
        // same ten trades as forty.
        (BayesianProbabilityModel model, PerformanceStore store, _) = Fixtures.BuildModel();

        double prior = model.Estimate(Fixtures.Query()).PWin;

        // Ten wins, all from one strategy, one regime, one confidence bucket -- so all four
        // hierarchy levels describe the identical ten trades.
        for (int i = 0; i < 10; i++)
        {
            TradeRecord t = Fixtures.Trade("TrendFollowing", win: true, 2.0, statedProbability: 0.55);
            store.Record(t); model.Observe(t);
        }

        ProbabilityEstimate e = model.Estimate(Fixtures.Query());

        // Against a prior of strength 25, ten wins should move the estimate by roughly
        // 10/(25+10) of the distance to 1.0 -- a clear but bounded move, not a landslide.
        Assert.True(e.PWin > prior, "Ten wins should move the estimate upward.");
        Assert.True(e.PWin < 0.62,
            $"Ten wins moved the estimate from {prior:P1} to {e.PWin:P1}; the hierarchy is double-counting.");
        Assert.True(e.EffectiveSample < 45,
            $"Effective sample was {e.EffectiveSample:F0} after ten trades against a prior of 25.");
    }

    [Fact]
    public void ARegimeSpecificRecordSeparatesTheEstimatesPerRegime()
    {
        (BayesianProbabilityModel model, PerformanceStore store, _) = Fixtures.BuildModel();

        for (int i = 0; i < 200; i++)
        {
            TradeRecord good = Fixtures.Trade("TrendFollowing", win: i % 10 < 8, 2.0, MarketRegime.TrendUp, statedProbability: 0.80);
            store.Record(good); model.Observe(good);

            TradeRecord bad = Fixtures.Trade("TrendFollowing", win: i % 10 < 2, 2.0, MarketRegime.Chop, statedProbability: 0.20);
            store.Record(bad); model.Observe(bad);
        }

        double trend = model.Estimate(Fixtures.Query(regime: MarketRegime.TrendUp)).PWin;
        double chop = model.Estimate(Fixtures.Query(regime: MarketRegime.Chop)).PWin;

        Assert.True(trend > chop + 0.20,
            $"Regime slices failed to separate: trend {trend:P1} vs chop {chop:P1}.");
    }

    [Fact]
    public void AGlobalRecordCannotDrownASpecificBucket()
    {
        // The hierarchy must let a specific bucket move away from its parent. If the global
        // prior were handed down at full strength, every bucket would collapse onto the
        // global rate and the hierarchy would be decorative.
        (BayesianProbabilityModel model, PerformanceStore store, _) = Fixtures.BuildModel();

        for (int i = 0; i < 2000; i++)
        {
            TradeRecord t = Fixtures.Trade("Other", win: i % 10 < 2, 2.0, MarketRegime.Range, statedProbability: 0.20);
            store.Record(t); model.Observe(t);
        }
        for (int i = 0; i < 200; i++)
        {
            TradeRecord t = Fixtures.Trade("TrendFollowing", win: i % 10 < 8, 2.0, MarketRegime.TrendUp, statedProbability: 0.80);
            store.Record(t); model.Observe(t);
        }

        double specific = model.Estimate(Fixtures.Query("TrendFollowing", regime: MarketRegime.TrendUp)).PWin;
        Assert.True(specific > 0.55,
            $"The specific bucket was dragged to {specific:P1} by an unrelated global record.");
    }

    [Fact]
    public void AFailingEstimateDegradesToNoInformationRatherThanThrowing()
    {
        (BayesianProbabilityModel model, _, _) = Fixtures.BuildModel();
        ProbabilityEstimate e = model.Estimate(null);

        Assert.Equal(0.5, e.PWin);
        Assert.Equal(0, e.Confidence);
        Assert.Equal(0, e.EffectiveSample);
    }
}

public class CalibrationTests
{
    [Fact]
    public void QualityStartsMediocreRatherThanPerfect()
    {
        var tracker = new CalibrationTracker(new ProbabilityConfig());

        // An unvalidated model is the one that most needs its influence limited; returning
        // 1.0 here would let it be trusted completely on zero evidence.
        Assert.Equal(0.5, tracker.Quality());
    }

    [Fact]
    public void AWellCalibratedModelScoresHighly()
    {
        var tracker = new CalibrationTracker(new ProbabilityConfig());
        var rng = new Pcg32(17);

        foreach (double p in new[] { 0.55, 0.62, 0.68, 0.72, 0.78, 0.83, 0.88 })
        {
            for (int i = 0; i < 300; i++) tracker.Observe(p, rng.NextDouble() < p);
        }

        Assert.True(tracker.ExpectedCalibrationError() < 0.05,
            $"ECE was {tracker.ExpectedCalibrationError():P1} for a well-calibrated model.");
        Assert.True(tracker.Quality() > 0.75);
    }

    [Fact]
    public void AnOverconfidentModelIsDetectedAndItsPredictionsAreShrunk()
    {
        var tracker = new CalibrationTracker(new ProbabilityConfig());
        var rng = new Pcg32(18);

        // Claims 80%, delivers 50%.
        for (int i = 0; i < 600; i++) tracker.Observe(0.80, rng.NextDouble() < 0.50);

        Assert.True(tracker.ExpectedCalibrationError() > 0.20,
            $"ECE was only {tracker.ExpectedCalibrationError():P1} for a badly overconfident model.");
        Assert.True(tracker.Quality() < 0.35);

        double shrunk = tracker.Recalibrate(0.80, baseRate: 0.40);
        Assert.True(shrunk < 0.60,
            $"An overconfident 80% call was only shrunk to {shrunk:P1}.");
    }

    [Fact]
    public void BrierScoreRewardsDecisivenessThatIsActuallyCorrect()
    {
        var confident = new CalibrationTracker(new ProbabilityConfig());
        var hedging = new CalibrationTracker(new ProbabilityConfig());
        var rng = new Pcg32(19);

        for (int i = 0; i < 1000; i++)
        {
            bool outcome = rng.NextDouble() < 0.9;
            confident.Observe(0.9, outcome);
            hedging.Observe(0.5, outcome);
        }

        Assert.True(confident.BrierScore < hedging.BrierScore,
            $"confident={confident.BrierScore:F4} hedging={hedging.BrierScore:F4}");
    }

    [Fact]
    public void ThinBinsAreExcludedFromTheErrorEstimate()
    {
        var tracker = new CalibrationTracker(new ProbabilityConfig());

        // Five observations in one bin, all losing, at a claimed 90%.
        for (int i = 0; i < 5; i++) tracker.Observe(0.92, false);

        Assert.Equal(0, tracker.ExpectedCalibrationError());
        Assert.Equal(0.5, tracker.Quality());
    }
}

public class CostModelTests
{
    [Fact]
    public void ChargesAFullRoundTripSpreadNotHalfOfOne()
    {
        var model = new CostModel(new ExecutionConfig());
        CostEstimate cost = model.Estimate(Fixtures.Spec(commissionPerMillion: 0), costingSpread: 10, stopDistance: 100, price: 50000, isStressed: false);

        Assert.Equal(10, cost.SpreadCost, 8);
    }

    [Fact]
    public void ExpressesCostInRSoItIsComparableAcrossStopSizes()
    {
        var model = new CostModel(new ExecutionConfig());

        CostEstimate wide = model.Estimate(Fixtures.Spec(), 10, stopDistance: 500, price: 50000, isStressed: false);
        CostEstimate tight = model.Estimate(Fixtures.Spec(), 10, stopDistance: 50, price: 50000, isStressed: false);

        Assert.Equal(wide.TotalPrice, tight.TotalPrice, 8);
        Assert.True(tight.TotalR > wide.TotalR * 9,
            "The identical cost must be a much larger fraction of a tight stop.");
    }

    [Fact]
    public void StressRaisesTheSlippageAssumption()
    {
        var model = new CostModel(new ExecutionConfig());

        CostEstimate calm = model.Estimate(Fixtures.Spec(), 10, 100, 50000, isStressed: false);
        CostEstimate stressed = model.Estimate(Fixtures.Spec(), 10, 100, 50000, isStressed: true);

        Assert.True(stressed.SlippageCost > calm.SlippageCost);
        Assert.True(stressed.TotalR > calm.TotalR);
    }

    [Fact]
    public void MeasuredSlippageOverridesTheAssumption()
    {
        var model = new CostModel(new ExecutionConfig());

        CostEstimate asModelled = model.Estimate(Fixtures.Spec(), 10, 100, 50000, false, measuredSlippageMultiplier: 1.0);
        CostEstimate asMeasured = model.Estimate(Fixtures.Spec(), 10, 100, 50000, false, measuredSlippageMultiplier: 3.0);

        Assert.True(asMeasured.SlippageCost > asModelled.SlippageCost * 2.5);
    }

    [Fact]
    public void AnUncomputableCostIsTreatedAsProhibitiveRatherThanFree()
    {
        var model = new CostModel(new ExecutionConfig());
        CostEstimate cost = model.Estimate(null, 10, 100, 50000, false);

        // Спецификации нет — издержки НЕИЗВЕСТНЫ, и единственная безопасная оценка
        // неизвестного здесь — запретительная. Ноль означал бы «бесплатно», и любой слой,
        // который не проверил спецификацию сам, увидел бы идеальную сделку.
        Assert.Equal(100, cost.StopDistance, 8);
        Assert.Equal(1.0, cost.TotalR, 8);
        Assert.True(cost.TotalPrice > 0, "Неизвестные издержки не могут считаться нулевыми.");
    }

    [Fact]
    public void CommissionScalesWithNotionalValue()
    {
        var model = new CostModel(new ExecutionConfig());

        CostEstimate cheap = model.Estimate(Fixtures.Spec(35), 1, 10, price: 1.0, isStressed: false);
        CostEstimate expensive = model.Estimate(Fixtures.Spec(35), 1, 10, price: 60000.0, isStressed: false);

        Assert.True(expensive.CommissionCost > cheap.CommissionCost * 1000);
    }
}

public class ExpectedValueEngineTests
{
    /// <param name="coldStart">
    /// По умолчанию хранилище наполняется историей, чтобы движок НЕ был в холодном старте.
    ///
    /// Иначе большинство этих тестов проверяло бы не то, что заявляет: в холодном старте
    /// штрафы за отсутствие данных сняты сознательно — они наказывают за состояние,
    /// выйти из которого можно только сделкой. Свойства «неопределённость поднимает
    /// планку» и «плохая калибровка поднимает планку» относятся к системе, у которой
    /// история уже есть.
    /// </param>
    private static ExpectedValueEngine Engine(out PerformanceStore store, EngineConfig config = null,
        bool coldStart = false)
    {
        config ??= new EngineConfig();
        store = new PerformanceStore(config.Adaptation);

        if (!coldStart)
        {
            for (int i = 0; i < config.Ev.ColdStartTrades + 5; i++)
            {
                store.Record(new TradeRecord
                {
                    TradeId = "seed-" + i,
                    SymbolName = "SEED",
                    StrategyName = "Seed",
                    Direction = Side.Long,
                    Regime = MarketRegime.Unknown,
                    EntryTimeUtc = RiskFixtures.T0.AddHours(i),
                    ExitTimeUtc = RiskFixtures.T0.AddHours(i).AddMinutes(30),
                    R = i % 2 == 0 ? 1.0 : -1.0,
                    ExitReason = ExitReason.StopLoss,
                    Mode = OperatingMode.Paper,
                });
            }
        }

        return new ExpectedValueEngine(config.Ev, store);
    }

    private static CostEstimate Cost(double totalR, double stop = 100) =>
        new CostEstimate(totalR * stop, 0, 0, stop);

    private static ProbabilityEstimate Prob(double p, double se, double n = 200, double cal = 0.9) =>
        new ProbabilityEstimate(p, se, n, cal, "test");

    [Fact]
    public void ARealEdgeIsAccepted()
    {
        ExpectedValueEngine engine = Engine(out _);

        ExpectedValueResult r = engine.Evaluate(
            Prob(0.55, 0.03), plannedRewardToRisk: 2.0, Cost(0.05),
            "TrendFollowing", MarketRegime.TrendUp, "BTCUSD", regimeConfidence: 0.85, volatilityStress: 0.1);

        Assert.True(r.IsAcceptable, r.ToString());
        Assert.True(r.ExpectedValueR > 0);
    }

    [Fact]
    public void AMarginallyPositiveEdgeIsRefusedBecauseItIsNoise()
    {
        ExpectedValueEngine engine = Engine(out _);

        // Break-even at 2:1 with a 1.05R assumed loss is about 34.5%. 35% is barely above it.
        ExpectedValueResult r = engine.Evaluate(
            Prob(0.35, 0.04), 2.0, Cost(0.02),
            "TrendFollowing", MarketRegime.TrendUp, "BTCUSD", 0.85, 0.1);

        Assert.False(r.IsAcceptable, r.ToString());
        Assert.True(r.RequiredEdgeR > 0);
    }

    [Fact]
    public void CostsAloneCanTurnAWinningSetupIntoARejection()
    {
        ExpectedValueEngine engine = Engine(out _);

        ExpectedValueResult cheap = engine.Evaluate(
            Prob(0.50, 0.03), 2.0, Cost(0.03), "TrendFollowing", MarketRegime.TrendUp, "BTCUSD", 0.85, 0.05);

        ExpectedValueResult expensive = engine.Evaluate(
            Prob(0.50, 0.03), 2.0, Cost(0.60), "TrendFollowing", MarketRegime.TrendUp, "BTCUSD", 0.85, 0.05);

        Assert.True(cheap.IsAcceptable, cheap.ToString());
        Assert.False(expensive.IsAcceptable, expensive.ToString());
        Assert.True(expensive.ExpectedValueR < cheap.ExpectedValueR - 0.5);
    }

    [Fact]
    public void UncertaintyShrinksTheSizeNotTheVerdict()
    {
        // Раньше этот тест утверждал обратное: одинаковая оценка с широкими границами
        // обязана быть отвергнута. Именно это свойство и запирало систему — ширина
        // границ сужается только сделками, а сделок при широких границах не было. На
        // устойчивом тренде: двадцать прибыльных сделок и ни одной за следующие восемь
        // тысяч баров.
        //
        // Осторожность не отменена, она переехала: неуверенная оценка торгуется малым
        // объёмом, уверенная — полным.
        ExpectedValueEngine engine = Engine(out _);

        ExpectedValueResult certain = engine.Evaluate(
            Prob(0.50, 0.01, n: 500), 2.0, Cost(0.05), "TrendFollowing", MarketRegime.TrendUp, "BTCUSD", 0.85, 0.05);

        ExpectedValueResult uncertain = engine.Evaluate(
            Prob(0.50, 0.18, n: 12), 2.0, Cost(0.05), "TrendFollowing", MarketRegime.TrendUp, "BTCUSD", 0.85, 0.05);

        Assert.Equal(certain.ExpectedValueR, uncertain.ExpectedValueR, 6);

        // Один и тот же вердикт: решает оценка преимущества после издержек, а не её ширина.
        Assert.Equal(certain.IsAcceptable, uncertain.IsAcceptable);
        Assert.Equal(certain.RequiredEdgeR, uncertain.RequiredEdgeR, 9);

        // Но доверие к широкой оценке заметно ниже — и размер режется именно им.
        Assert.True(uncertain.Trust < certain.Trust * 0.6,
            $"доверие к широкой оценке {uncertain.Trust:P0} почти не отличается от узкой {certain.Trust:P0}");
        Assert.True(uncertain.EvidencePenaltyR > certain.EvidencePenaltyR);
    }

    [Fact]
    public void LowRegimeConfidenceRaisesTheBar()
    {
        ExpectedValueEngine engine = Engine(out _);

        ExpectedValueResult clear = engine.Evaluate(
            Prob(0.50, 0.03), 2.0, Cost(0.05), "TrendFollowing", MarketRegime.TrendUp, "BTCUSD", regimeConfidence: 0.95, volatilityStress: 0);

        ExpectedValueResult murky = engine.Evaluate(
            Prob(0.50, 0.03), 2.0, Cost(0.05), "TrendFollowing", MarketRegime.TrendUp, "BTCUSD", regimeConfidence: 0.20, volatilityStress: 0);

        Assert.True(murky.RequiredEdgeR > clear.RequiredEdgeR);
    }

    [Fact]
    public void PoorCalibrationLowersTrustWithoutMovingTheBar()
    {
        // Модель, чьи вероятности не совпадали с реальностью, заслуживает меньшего
        // размера. Запрещать ей торговать значило бы запретить и сверку: калибровка
        // измеряется только на исходах сделок.
        ExpectedValueEngine engine = Engine(out _);

        ExpectedValueResult trusted = engine.Evaluate(
            Prob(0.50, 0.03, cal: 1.0), 2.0, Cost(0.05), "TrendFollowing", MarketRegime.TrendUp, "BTCUSD", 0.9, 0);

        ExpectedValueResult suspect = engine.Evaluate(
            Prob(0.50, 0.03, cal: 0.1), 2.0, Cost(0.05), "TrendFollowing", MarketRegime.TrendUp, "BTCUSD", 0.9, 0);

        Assert.Equal(trusted.RequiredEdgeR, suspect.RequiredEdgeR, 9);
        Assert.True(suspect.Trust < trusted.Trust);
    }

    [Fact]
    public void TheTargetIsCappedByWhatTheSetupHistoricallyReaches()
    {
        EngineConfig config = new EngineConfig();
        ExpectedValueEngine engine = Engine(out PerformanceStore store, config);

        // A setup whose favourable excursion rarely exceeds 1.2R.
        for (int i = 0; i < 120; i++)
        {
            store.Record(Fixtures.Trade("TrendFollowing", win: i % 2 == 0, 1.0, MarketRegime.TrendUp, mfeR: 1.1));
        }

        ExpectedValueResult r = engine.Evaluate(
            Prob(0.60, 0.03), plannedRewardToRisk: 4.0, Cost(0.05),
            "TrendFollowing", MarketRegime.TrendUp, "BTCUSD", 0.9, 0);

        Assert.True(r.TargetCappedByHistory, r.ToString());
        Assert.True(r.EffectiveRewardToRisk < 2.0,
            $"A 4R target was allowed on a setup that historically reaches {r.EffectiveRewardToRisk:F2}R.");
    }

    [Fact]
    public void NoCapIsAppliedWhileTheHistoryIsTooThinToJustifyOne()
    {
        ExpectedValueEngine engine = Engine(out PerformanceStore store);
        for (int i = 0; i < 5; i++) store.Record(Fixtures.Trade("TrendFollowing", true, 1.0, mfeR: 1.1));

        ExpectedValueResult r = engine.Evaluate(
            Prob(0.60, 0.03), 4.0, Cost(0.05), "TrendFollowing", MarketRegime.TrendUp, "BTCUSD", 0.9, 0);

        Assert.False(r.TargetCappedByHistory);
        Assert.Equal(4.0, r.EffectiveRewardToRisk, 6);
    }

    [Fact]
    public void ExpectedLossExceedsOneRToAllowForStopSlippage()
    {
        ExpectedValueEngine engine = Engine(out _);

        ExpectedValueResult r = engine.Evaluate(
            Prob(0.50, 0.03), 2.0, Cost(0.05), "TrendFollowing", MarketRegime.TrendUp, "BTCUSD", 0.9, 0);

        Assert.True(r.ExpectedLossR >= 1.0,
            "A stop never fills better than its level; assuming exactly 1R is optimistic.");
    }

    [Fact]
    public void AnUnusableProbabilityEstimateIsRefusedOutright()
    {
        ExpectedValueEngine engine = Engine(out _);

        ExpectedValueResult r = engine.Evaluate(
            ProbabilityEstimate.Unavailable, 2.0, Cost(0.05), "TrendFollowing", MarketRegime.TrendUp, "BTCUSD", 0.9, 0);

        Assert.False(r.IsAcceptable);
        Assert.Contains("no usable probability", r.Rationale);
    }
}
