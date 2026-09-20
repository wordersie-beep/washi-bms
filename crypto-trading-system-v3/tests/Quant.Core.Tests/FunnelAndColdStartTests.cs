using System;
using System.Collections.Generic;
using System.Linq;
using Quant.Core.Config;
using Quant.Core.Ev;
using Quant.Core.Journal;
using Quant.Core.Primitives;
using Quant.Core.Probability;
using Quant.Core.Stats;
using Xunit;
using Xunit.Abstractions;

namespace Quant.Core.Tests;

/// <summary>
/// Воронка решений и холодный старт — два ответа на один вопрос: почему система,
/// у которой каждый слой работает правильно, не совершает ни одной сделки.
/// </summary>
public class FunnelAndColdStartTests
{
    private readonly ITestOutputHelper _out;
    public FunnelAndColdStartTests(ITestOutputHelper output) { _out = output; }

    // ── Воронка ──────────────────────────────────────────────────────────────────

    [Fact]
    public void TheFunnelShowsHowManyReachedEachGateAndHowManyPassed()
    {
        // Сводка причин отказа не отвечает на главный вопрос: фильтр осторожен или сломан.
        // Отличает их одно число — доля прошедших.
        var counts = new Dictionary<NoTradeReason, int>
        {
            [NoTradeReason.NoSignal] = 600,
            [NoTradeReason.LowRegimeConfidence] = 250,
            [NoTradeReason.PoorRiskReward] = 100,
        };

        IReadOnlyList<DecisionFunnel.Stage> funnel = DecisionFunnel.Build(counts, accepted: 50);
        _out.WriteLine(DecisionFunnel.Render(funnel, 50));

        DecisionFunnel.Stage noSignal = funnel.First(s => s.Reason == NoTradeReason.NoSignal);
        Assert.Equal(1000, noSignal.Reached);
        Assert.Equal(400, noSignal.Passed);

        DecisionFunnel.Stage rr = funnel.First(s => s.Reason == NoTradeReason.PoorRiskReward);
        Assert.Equal(150, rr.Reached);
        Assert.Equal(50, rr.Passed);
    }

    [Fact]
    public void AStructuralGateThatPassesNobodyIsFlagged()
    {
        // Ровно тот дефект, ради которого воронка и написана: отношение прибыли к риску
        // считалось по первой цели (1.2R) при минимуме 1.3 и отвергало каждого кандидата.
        var counts = new Dictionary<NoTradeReason, int>
        {
            [NoTradeReason.NoSignal] = 500,
            [NoTradeReason.PoorRiskReward] = 300,
        };

        IReadOnlyList<DecisionFunnel.Stage> funnel = DecisionFunnel.Build(counts, accepted: 0);
        IReadOnlyList<DecisionFunnel.Stage> blocked = DecisionFunnel.Impassable(funnel);

        Assert.Single(blocked);
        Assert.Equal(NoTradeReason.PoorRiskReward, blocked[0].Reason);
    }

    [Fact]
    public void AnEconomicGateThatPassesNobodyIsNotFlagged()
    {
        // На рынке без преимущества система ОБЯЗАНА отказывать всем. Объявлять это поломкой
        // значило бы кричать «волки» там, где всё правильно, — и приучить не смотреть.
        var counts = new Dictionary<NoTradeReason, int>
        {
            [NoTradeReason.NoSignal] = 500,
            [NoTradeReason.InsufficientEdge] = 300,
        };

        IReadOnlyList<DecisionFunnel.Stage> funnel = DecisionFunnel.Build(counts, accepted: 0);

        Assert.Empty(DecisionFunnel.Impassable(funnel));
        Assert.False(DecisionFunnel.IsStructural(NoTradeReason.InsufficientEdge));
        Assert.True(DecisionFunnel.IsStructural(NoTradeReason.PoorRiskReward));
    }

    [Fact]
    public void ASmallSampleIsNotEnoughToCallAGateBroken()
    {
        var counts = new Dictionary<NoTradeReason, int> { [NoTradeReason.PoorRiskReward] = 10 };
        Assert.Empty(DecisionFunnel.Impassable(DecisionFunnel.Build(counts, accepted: 0)));
    }

    // ── Холодный старт ───────────────────────────────────────────────────────────

    [Fact]
    public void WithoutHistoryTheSystemDoesNotDemandEvidenceItCannotHave()
    {
        // Три штрафа наказывали за отсутствие данных: тонкая выборка, отсутствие калибровки
        // и ширина оценки. Все три снимаются только сделками — а сделок нет, пока штрафы
        // действуют. Требуемое преимущество доходило до 0.8R при базовом пороге 0.10R.
        var config = new EngineConfig();
        var empty = new PerformanceStore(config.Adaptation);
        var engine = new ExpectedValueEngine(config.Ev, empty);

        // Оценка без истории: широкая, некалиброванная.
        var green = new ProbabilityEstimate(0.55, standardError: 0.18, effectiveSample: 8,
            calibrationQuality: 0, basis: "prior",
            minSample: config.Probability.MinSampleForBucket,
            fullTrustSample: config.Probability.FullTrustSample);

        var cost = new CostEstimate(spreadCost: 10, commissionCost: 5, slippageCost: 5, stopDistance: 500);

        ExpectedValueResult cold = engine.Evaluate(green, 2.0, cost, "Breakout",
            MarketRegime.TrendUp, "BTCUSD", regimeConfidence: 0.7, volatilityStress: 0.1);

        Assert.True(cold.IsColdStart, "без истории система обязана быть в холодном старте");

        // Решение по точечной оценке, а не по нижней границе: ширина границы тоже измеряет
        // незнание, а не риск сделки.
        Assert.Equal(cold.ExpectedValueR, cold.DecisionEdgeR, 9);

        _out.WriteLine($"холодный старт: требуется {cold.RequiredEdgeR:F3}R, ожидание {cold.ExpectedValueR:F3}R");

        // Та же оценка при наполненной истории требует заметно большего.
        var seasoned = new PerformanceStore(config.Adaptation);
        for (int i = 0; i < config.Ev.ColdStartTrades + 5; i++)
        {
            seasoned.Record(new TradeRecord
            {
                TradeId = "t" + i, SymbolName = "BTCUSD", StrategyName = "Breakout",
                Direction = Side.Long, Regime = MarketRegime.TrendUp,
                EntryTimeUtc = RiskFixtures.T0.AddHours(i), ExitTimeUtc = RiskFixtures.T0.AddHours(i).AddMinutes(30),
                R = i % 2 == 0 ? 1.0 : -1.0, ExitReason = ExitReason.StopLoss, Mode = OperatingMode.Paper,
            });
        }

        ExpectedValueResult warm = new ExpectedValueEngine(config.Ev, seasoned).Evaluate(
            green, 2.0, cost, "Breakout", MarketRegime.TrendUp, "BTCUSD", 0.7, 0.1);

        Assert.False(warm.IsColdStart);
        Assert.Equal(warm.LowerBoundR, warm.DecisionEdgeR, 9);
        Assert.True(warm.RequiredEdgeR > cold.RequiredEdgeR,
            $"с историей планка обязана быть выше: {warm.RequiredEdgeR:F3}R против {cold.RequiredEdgeR:F3}R");

        _out.WriteLine($"с историей:     требуется {warm.RequiredEdgeR:F3}R");
    }

    [Fact]
    public void ColdStartCanBeTurnedOff()
    {
        // Для бэктеста на готовой истории холодный старт не нужен и только исказил бы
        // первые сделки.
        var config = new EngineConfig();
        config.Ev.ColdStartTrades = 0;

        var engine = new ExpectedValueEngine(config.Ev, new PerformanceStore(config.Adaptation));
        var estimate = new ProbabilityEstimate(0.55, 0.18, 8, 0, "prior");
        var cost = new CostEstimate(10, 5, 5, 500);

        Assert.False(engine.Evaluate(estimate, 2.0, cost, "Breakout",
            MarketRegime.TrendUp, "BTCUSD", 0.7, 0.1).IsColdStart);
    }
}
