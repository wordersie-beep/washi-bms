using System;
using System.Collections.Generic;
using System.Linq;
using Quant.Core;
using Quant.Core.Config;
using Quant.Core.Primitives;
using Xunit;
using Xunit.Abstractions;

namespace Quant.Core.Tests;

/// <summary>
/// Прогрев историей должен НАПОЛНЯТЬ данные, но не ПРИНИМАТЬ решений.
///
/// При старте бот прогоняет сотни исторических баров, чтобы индикаторы и перцентили были
/// готовы к первому решению. Если этот прогон идёт через полный торговый цикл, система
/// оценивает риск, сверяется с брокером, пытается входить и сохраняет состояние — всё на
/// данных двухдневной давности и по ценам, которых уже нет.
/// </summary>
public class WarmUpTests
{
    private readonly ITestOutputHelper _out;
    public WarmUpTests(ITestOutputHelper output) { _out = output; }

    private static EngineConfig Config()
    {
        var config = new EngineConfig();
        config.Regime.MinConfidenceToTrade = 0.25;
        config.Strategy.MinEnsembleConfidence = 0.35;
        config.Ev.BaseMinimumEdgeR = 0.0;
        config.Ev.EdgeConfidenceZ = 0.0;
        config.Probability.PriorWinRate = 0.80;
        return config;
    }

    [Fact]
    public void WarmUpDoesNotFloodTheJournalWithDecisions()
    {
        var h = new EngineHarness(Config());
        var bars = new MarketSimulator(seed: 301).Generate(1500, 0.0004, 0.0025).ToList();

        h.FeedWarmUp(bars);

        long decisions = h.Engine.Journal.TotalAccepted + h.Engine.Journal.TotalRejected;

        _out.WriteLine($"решений за прогрев: {decisions}");
        _out.WriteLine(h.Engine.Journal.RejectionSummary());

        Assert.Equal(0, decisions);
    }

    [Fact]
    public void WarmUpDoesNotAttemptToTrade()
    {
        var h = new EngineHarness(Config());
        h.FeedWarmUp(new MarketSimulator(seed: 302).Generate(1500, 0.0004, 0.0025));

        Assert.Empty(h.Engine.Positions);
        Assert.Empty(h.Broker.GetOpenPositions("QCV3"));
    }

    [Fact]
    public void WarmUpDoesNotWriteStateHundredsOfTimes()
    {
        var h = new EngineHarness(Config());
        h.FeedWarmUp(new MarketSimulator(seed: 303).Generate(1500, 0.0004, 0.0025));

        _out.WriteLine($"записей состояния за прогрев: {h.Store.SaveCount}");

        // Прогрев не меняет ничего, что стоило бы сохранять.
        Assert.Equal(0, h.Store.SaveCount);
    }

    [Fact]
    public void WarmUpDoesNotAnchorTheDailyReferenceToAHistoricalDay()
    {
        // Дневная точка отсчёта, установленная во время прогрева, привязывается ко дню
        // ИСТОРИЧЕСКОГО бара. После неё дневной лимит убытка считается от капитала,
        // которого на том счёте никогда не было.
        var h = new EngineHarness(Config());
        h.FeedWarmUp(new MarketSimulator(seed: 304).Generate(1500, 0.0004, 0.0025));

        Assert.Equal(0, h.Engine.Risk.DayStartEquity);
    }

    [Fact]
    public void WarmUpStillPrimesTheIndicatorsAndRegime()
    {
        // Смысл прогрева не должен потеряться: после него система обязана быть готова
        // принимать решения немедленно, а не набирать историю ещё неделю.
        var h = new EngineHarness(Config());
        var bars = new MarketSimulator(seed: 305).Generate(1500, 0.0012, 0.0020).ToList();

        h.FeedWarmUp(bars);

        Assert.True(h.Engine.Data("BTCUSD").IsReady, "После прогрева данные обязаны быть готовы.");
        Assert.NotEqual(MarketRegime.Unknown, h.Engine.Regimes["BTCUSD"].Primary);

        _out.WriteLine($"режим после прогрева: {h.Engine.Regimes["BTCUSD"]}");
    }

    [Fact]
    public void TheFirstLiveBarAfterWarmUpProducesADecisionImmediately()
    {
        var h = new EngineHarness(Config());
        var sim = new MarketSimulator(seed: 306);

        h.FeedWarmUp(sim.Generate(1500, 0.0004, 0.0025));
        Assert.Equal(0, h.Engine.Journal.TotalAccepted + h.Engine.Journal.TotalRejected);

        // Один живой бар — и система обязана вынести суждение, а не молчать.
        h.Feed(sim.Generate(1, 0.0004, 0.0025), spreadFraction: 0.00005);

        long decisions = h.Engine.Journal.TotalAccepted + h.Engine.Journal.TotalRejected;
        _out.WriteLine($"решений после первого живого бара: {decisions}");

        Assert.True(decisions > 0,
            "После прогрева первый же живой бар обязан дать решение — иначе прогрев не выполнил свою задачу.");
    }
}

/// <summary>
/// Признак жизни существует ради одного вопроса: «он работает или сломался?».
/// Эти тесты проверяют, что на него можно ответить по одной строке.
/// </summary>
public class HeartbeatTests
{
    private readonly ITestOutputHelper _out;
    public HeartbeatTests(ITestOutputHelper output) { _out = output; }

    [Fact]
    public void DuringWarmUpItSaysHowManyBarsAreStillNeeded()
    {
        var h = new EngineHarness();
        h.FeedWarmUp(new MarketSimulator(seed: 401).Generate(50, 0.0004, 0.0025));

        string line = h.Engine.RenderHeartbeat(h.LastTimeUtc);
        _out.WriteLine(line);

        Assert.Contains("[жив]", line);
        Assert.Contains("ПРОГРЕВ", line);
        Assert.Contains("нужно ещё", line);
    }

    [Fact]
    public void OnceReadyItReportsTheRegimeAndTheDecisionCount()
    {
        var config = new EngineConfig();
        var h = new EngineHarness(config);
        h.Feed(new MarketSimulator(seed: 402).Generate(1500, 0.0012, 0.0020), spreadFraction: 0.00005);

        string line = h.Engine.RenderHeartbeat(h.LastTimeUtc);
        _out.WriteLine(line);

        Assert.DoesNotContain("ПРОГРЕВ", line);
        Assert.Contains("решений", line);
        Assert.Contains("риск", line);

        // Именно это отличает «работает и молчит» от «сломался и молчит».
        Assert.Contains("чаще всего:", line);
    }

    [Fact]
    public void ItSaysPlainlyWhenNewPositionsAreBlocked()
    {
        var config = new EngineConfig();
        config.Risk.DailyLossLimitPercent = 0.01;   // сработает немедленно

        var h = new EngineHarness(config);

        // Один и тот же симулятор: новый начал бы с той же даты, и его бары были бы
        // отвергнуты как пришедшие из прошлого.
        var sim = new MarketSimulator(seed: 403);
        h.Feed(sim.Generate(1200, 0.0004, 0.0025), spreadFraction: 0.00005);

        // Капитал падает ниже дневного лимита.
        h.Broker.SetAccount(new AccountSnapshot(90000, 90000, 0, 90000, 2000, 50, false, "USD"));
        h.Feed(sim.Generate(5, 0.0004, 0.0025), spreadFraction: 0.00005);

        string line = h.Engine.RenderHeartbeat(h.LastTimeUtc);
        _out.WriteLine(line);

        Assert.Contains("БЛОКИРОВКА", line);
    }

    [Fact]
    public void ItNeverThrowsEvenBeforeAnyDataArrives()
    {
        // Первый признак жизни печатается сразу при старте, когда ещё ничего нет.
        var h = new EngineHarness();
        string line = h.Engine.RenderHeartbeat(RiskFixtures.T0);

        _out.WriteLine(line);
        Assert.Contains("[жив]", line);
    }
}
