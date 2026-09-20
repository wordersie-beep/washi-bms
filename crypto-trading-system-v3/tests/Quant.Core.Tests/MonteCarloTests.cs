using System;
using System.Collections.Generic;
using System.Linq;
using Quant.Core;
using Quant.Core.Config;
using Quant.Core.Ev;
using Quant.Core.Execution;
using Quant.Core.Features;
using Quant.Core.Journal;
using Quant.Core.Numerics;
using Quant.Core.Primitives;
using Quant.Core.State;
using Quant.Core.Stats;
using Xunit;
using Xunit.Abstractions;

namespace Quant.Core.Tests;

/// <summary>
/// Историческая максимальная просадка — это ОДНА реализация из распределения. Судить по
/// ней о том, чего ждать дальше, значит принимать удачную последовательность за свойство
/// системы.
/// </summary>
public class MonteCarloTests
{
    private readonly ITestOutputHelper _out;
    public MonteCarloTests(ITestOutputHelper output) { _out = output; }

    private static List<double> Sample(int n, double winRate, double win, double loss, ulong seed)
    {
        var rng = new Pcg32(seed);
        var r = new List<double>(n);
        for (int i = 0; i < n; i++) r.Add(rng.NextDouble() < winRate ? win : -loss);
        return r;
    }

    [Fact]
    public void TooFewTradesProduceNoDistributionRatherThanAConfidentOne()
    {
        // Распределение по девяти наблюдениям — это не распределение, и выдавать его за
        // оценку риска хуже, чем не выдавать ничего.
        MonteCarloResult result = new MonteCarloSimulator(1000, 7).Run(Sample(9, 0.5, 1, 1, 1));
        Assert.Equal(0, result.Paths);
    }

    [Fact]
    public void TheWorstSimulatedDrawdownExceedsTheObservedOne()
    {
        // Это и есть смысл упражнения: тот же набор сделок в другом порядке даёт просадку
        // хуже пережитой. Система, рассчитанная на историческую просадку, рассчитана на
        // везение.
        List<double> sample = Sample(200, 0.45, 1.3, 1.0, 11);

        double observedWorst = 0, running = 0, peak = 0;
        foreach (double r in sample)
        {
            running += r;
            peak = Math.Max(peak, running);
            observedWorst = Math.Max(observedWorst, peak - running);
        }

        MonteCarloResult mc = new MonteCarloSimulator(2000, 11).Run(sample);

        _out.WriteLine($"пережитая просадка {observedWorst:F2}R, медиана {mc.DrawdownMedian:F2}R, P95 {mc.DrawdownP95:F2}R, худшая {mc.DrawdownWorst:F2}R");

        Assert.True(mc.Paths > 0);
        Assert.True(mc.DrawdownWorst > observedWorst,
            $"худшая смоделированная {mc.DrawdownWorst:F2}R должна превышать пережитую {observedWorst:F2}R");
        Assert.True(mc.DrawdownP95 >= mc.DrawdownMedian);
        Assert.True(mc.DrawdownMedian >= mc.DrawdownP25);
    }

    [Fact]
    public void LossClusteringMakesDrawdownsWorse()
    {
        // Убытки приходят сериями, а не вразброс. Независимая перетасовка — оптимистичное
        // допущение, и считать по ней значит занижать то единственное, ради чего это
        // считается.
        List<double> sample = Sample(300, 0.45, 1.3, 1.0, 23);

        MonteCarloResult independent = new MonteCarloSimulator(3000, 23).Run(sample, lossClustering: 0.0);
        MonteCarloResult clustered = new MonteCarloSimulator(3000, 23).Run(sample, lossClustering: 0.40);

        _out.WriteLine($"без кластеризации P95 {independent.DrawdownP95:F2}R, с кластеризацией {clustered.DrawdownP95:F2}R");

        Assert.True(clustered.DrawdownP95 > independent.DrawdownP95,
            "кластеризация убытков обязана ухудшать хвост просадки");
    }

    [Fact]
    public void ExtraCostPerTradeShiftsTheWholeDistributionDown()
    {
        // Стресс по издержкам (раздел 90): преимущество, которое исчезает от лишних 0.05R
        // на сделку, — это не преимущество, а зазор до брокерской комиссии.
        List<double> sample = Sample(300, 0.50, 1.2, 1.0, 31);

        MonteCarloResult clean = new MonteCarloSimulator(2000, 31).Run(sample);
        MonteCarloResult stressed = new MonteCarloSimulator(2000, 31).Run(sample, slippagePerTradeR: 0.05);

        Assert.True(stressed.FinalMedian < clean.FinalMedian);
        Assert.True(stressed.ProbabilityOfLoss > clean.ProbabilityOfLoss);
    }

    [Fact]
    public void TheEngineRunsMonteCarloOverItsOwnRecordAndVirtualTradesAreExcluded()
    {
        // Теневые сделки не платят настоящих издержек. Пустить их в оценку риска значило
        // бы улучшить распределение просадок сделками, которых не было.
        var config = new EngineConfig();
        var broker = new SimulatedBroker(config.Execution, new CostModel(config.Execution),
            new AccountSnapshot(100000, 100000, 0, 100000, 5000, 50, false, "USD"), false);

        var engine = new TradingEngine(config, broker, new InMemoryStateStore(), NullJournalSink.Instance, "test");
        engine.AddSymbol("BTCUSD", new SymbolSpec("BTCUSD", 0.01, 0.01, 2, 0.001, 1000, 0.001, 35, 0.01, 0, 2.0, true));

        Assert.Equal(0, engine.RunMonteCarlo(paths: 500).Paths);

        var rng = new Pcg32(41);
        for (int i = 0; i < 80; i++)
        {
            bool win = rng.NextDouble() < 0.5;
            engine.Performance.Record(new TradeRecord
            {
                TradeId = "t" + i, SymbolName = "BTCUSD", StrategyName = "Breakout",
                Direction = Side.Long, EntryTimeUtc = RiskFixtures.T0.AddHours(i),
                ExitTimeUtc = RiskFixtures.T0.AddHours(i).AddMinutes(30),
                R = win ? 1.2 : -1.0, ExitReason = ExitReason.TakeProfit1, Mode = OperatingMode.Paper,
            });
        }

        MonteCarloResult withReal = engine.RunMonteCarlo(paths: 1000);
        Assert.True(withReal.Paths > 0);
        Assert.Equal(80, withReal.TradesPerPath);

        // Ещё сорок ТЕНЕВЫХ — длина пути не должна измениться.
        for (int i = 0; i < 40; i++)
        {
            engine.Performance.Record(new TradeRecord
            {
                TradeId = "v" + i, SymbolName = "BTCUSD", StrategyName = "Momentum",
                Direction = Side.Long, EntryTimeUtc = RiskFixtures.T0.AddHours(i),
                ExitTimeUtc = RiskFixtures.T0.AddHours(i).AddMinutes(30),
                R = 2.0, ExitReason = ExitReason.TakeProfit1, Mode = OperatingMode.Paper, IsVirtual = true,
            });
        }

        Assert.Equal(80, engine.RunMonteCarlo(paths: 1000).TradesPerPath);
    }

    [Fact]
    public void TheDashboardShowsTheDistributionOnceThereIsEnoughHistory()
    {
        var config = new EngineConfig();
        var broker = new SimulatedBroker(config.Execution, new CostModel(config.Execution),
            new AccountSnapshot(100000, 100000, 0, 100000, 5000, 50, false, "USD"), false);

        var engine = new TradingEngine(config, broker, new InMemoryStateStore(), NullJournalSink.Instance, "test");
        engine.AddSymbol("BTCUSD", new SymbolSpec("BTCUSD", 0.01, 0.01, 2, 0.001, 1000, 0.001, 35, 0.01, 0, 2.0, true));

        Assert.DoesNotContain("MONTE CARLO", engine.RenderDashboard(RiskFixtures.T0));

        for (int i = 0; i < 40; i++)
        {
            engine.Performance.Record(new TradeRecord
            {
                TradeId = "t" + i, SymbolName = "BTCUSD", StrategyName = "Breakout",
                Direction = Side.Long, EntryTimeUtc = RiskFixtures.T0.AddHours(i),
                ExitTimeUtc = RiskFixtures.T0.AddHours(i).AddMinutes(30),
                R = i % 2 == 0 ? 1.1 : -1.0, ExitReason = ExitReason.TakeProfit1, Mode = OperatingMode.Paper,
            });
        }

        Assert.Contains("MONTE CARLO", engine.RenderDashboard(RiskFixtures.T0));
    }
}
