using System;
using System.Collections.Generic;
using System.Linq;
using Quant.Core;
using Quant.Core.Adaptation;
using Quant.Core.Config;
using Quant.Core.Decision;
using Quant.Core.Ev;
using Quant.Core.Execution;
using Quant.Core.Exits;
using Quant.Core.Journal;
using Quant.Core.State;
using Quant.Core.Numerics;
using Quant.Core.Portfolio;
using Quant.Core.Probability;
using Quant.Core.Regime;
using Quant.Core.Strategies;
using Quant.Core.Data;
using Quant.Core.Features;
using Quant.Core.Primitives;
using Quant.Core.Sizing;
using Quant.Core.Stats;
using Xunit;
using Xunit.Abstractions;

namespace Quant.Core.Tests;

/// <summary>
/// Регрессии на дефекты, найденные состязательным разбором кода.
/// Каждый тест назван так, чтобы из падения было видно, ЧТО именно сломано.
/// </summary>
public class DefectRegressionTests
{
    private readonly ITestOutputHelper _out;
    public DefectRegressionTests(ITestOutputHelper output) { _out = output; }

    private static readonly SymbolSpec Spec =
        new SymbolSpec("BTCUSD", 0.01, 0.01, 2, 0.001, 1000, 0.001, 35, 0.01, 0, 2.0, true);

    // ── 1. Счётчик удержания не должен расти от чужих баров ───────────────────────

    [Fact]
    public void HoldingTimeCountsOnlyThisSymbolsBars()
    {
        // Время-стоп задан в барах СИГНАЛЬНОГО таймфрейма этого инструмента. Если счётчик
        // растёт на закрытии бара любого символа, то в портфеле из N инструментов — а
        // эталонный добавляется всегда, значит N ≥ 2 — стоп по времени срабатывает в N раз
        // раньше, чем задумано.
        var manager = new PositionManager(new ExitConfig());
        var market = new PipelineHarness();
        market.Feed(new MarketSimulator(seed: 601).Generate(1200, 0.0003, 0.002));

        OpenPosition position = Position();
        ExitPlan plan = Plan();

        // Десять закрытий бара ЭТОГО символа.
        for (int i = 0; i < 10; i++)
        {
            manager.Evaluate(position, plan, 50100, Spec, market.Data, null, onClosedBar: true);
        }

        Assert.Equal(10, position.BarsHeld);
    }

    // ── 3. Направленный лимит должен связывать книгу из шортов ────────────────────

    [Fact]
    public void TheDirectionalLimitBindsOnAShortHeavyBook()
    {
        // Корреляционно-скорректированный риск — это величина sqrt(w'Cw), она всегда
        // неотрицательна. Прибавление к ней ЗНАКОВОГО риска кандидата означает, что каждый
        // следующий шорт УМЕНЬШАЕТ проекцию, и лимит на книге из шортов не срабатывает
        // никогда.
        var config = new EngineConfig();
        config.Portfolio.MaxDirectionalRiskPercent = 1.0;

        var correlation = new CorrelationEngine(config.Portfolio);
        // Инструменты НЕЗАВИСИМЫ: так они попадают в разные кластеры, и сработать может
        // только направленный лимит — иначе тест доказывал бы работу кластерного.
        var rng = new Pcg32(602);
        double a = 100, b = 100, c = 100;
        for (int i = 0; i < 200; i++)
        {
            a *= Math.Exp(rng.NextGaussian() * 0.01);
            b *= Math.Exp(rng.NextGaussian() * 0.01);
            c *= Math.Exp(rng.NextGaussian() * 0.01);
            correlation.Observe("AAA", a); correlation.Observe("BBB", b); correlation.Observe("CCC", c);
        }

        var engine = new PortfolioRiskEngine(config.Portfolio, config.Risk, correlation);
        IReadOnlyDictionary<string, int> clusters = correlation.BuildClusters();

        // Книга уже сильно в шорт.
        // По 0.5% риска на позицию: суммарно книга в шорт на 1.0% капитала.
        var shorts = new List<OpenPosition>
        {
            Position("AAA", Side.Short, 100, 101, 50),
            Position("BBB", Side.Short, 100, 101, 50),
        };

        PortfolioExposure exposure = engine.Compute(shorts, 10000, clusters);
        _out.WriteLine($"направленный риск: {exposure.NetDirectionalRiskPercent:F2}%, " +
                       $"с учётом корреляции: {exposure.CorrelationAdjustedDirectionalRiskPercent:F2}%");

        // Третий инструмент — чтобы сработал именно НАПРАВЛЕННЫЙ лимит, а не лимит на символ.
        NoTradeReason reason = engine.CheckLimits(
            exposure, "CCC", Side.Short, proposedRiskPercent: 0.7, clusters, 0, out string detail);

        _out.WriteLine($"ещё один шорт: {reason} {detail}");

        Assert.Equal(NoTradeReason.DirectionalExposureLimit, reason);
    }

    // ── 5. Защита по марже не должна отключаться при нулевой свободной марже ──────

    [Fact]
    public void TheMarginGuardRefusesWhenThereIsNoFreeMargin()
    {
        var config = new EngineConfig();
        var sizer = new PositionSizer(config.Sizing, config.Risk);

        var brokeAccount = new AccountSnapshot(10000, 10000, 10000, freeMargin: 0, 100, 50, false, "USD");

        SizingResult result = sizer.Compute(
            Spec, brokeAccount, 50000, 49500, Side.Long,
            1, 1, 1, 1, 0.35, 0.5, 0, 1, 1, 100);

        _out.WriteLine(result.ToString());

        Assert.False(result.IsTradeable,
            "При нулевой свободной марже сделка обязана быть отклонена, а не пропущена мимо проверки.");
        Assert.Equal(NoTradeReason.MarginGuard, result.RejectionReason);
    }

    [Fact]
    public void TheMarginGuardAlsoRefusesOnNegativeFreeMargin()
    {
        var config = new EngineConfig();
        var sizer = new PositionSizer(config.Sizing, config.Risk);

        var underwater = new AccountSnapshot(10000, 8000, 12000, freeMargin: -4000, 60, 50, false, "USD");

        SizingResult result = sizer.Compute(
            Spec, underwater, 50000, 49500, Side.Long,
            1, 1, 1, 1, 0.35, 0.5, 0, 1, 1, 100);

        Assert.False(result.IsTradeable);
        Assert.Equal(NoTradeReason.MarginGuard, result.RejectionReason);
    }

    // ── 6. Неопределённый исход ордера не должен разблокировать сигнал ────────────

    [Fact]
    public void ATransientFailureKeepsTheSignalLockedBecauseAcceptanceIsUnknown()
    {
        // Таймаут и обрыв связи — единственные случаи, когда НЕИЗВЕСТНО, принят ордер или
        // нет. Снять отметку об исполнении здесь значит разрешить второй ордер по тому же
        // сигналу, то есть двойную позицию двойного размера.
        var config = new EngineConfig();
        var guard = new State.IdempotencyGuard();
        var quality = new Risk.ExecutionQualityTracker(config.Execution);

        var broker = new AlwaysFailsBroker(outcomeUnknown: true);
        var execution = new ExecutionEngine(config.Execution, broker, quality, guard, "test");

        execution.Open("sig-1", "BTCUSD", Side.Long, 1.0, Plan(), Spec, 50000, 1.0, 500, 2.0, null);

        Assert.True(guard.HasExecuted("sig-1"),
            "После неопределённого исхода сигнал обязан остаться заблокированным.");
    }

    [Fact]
    public void ADefinitiveRejectionStillReleasesTheSignal()
    {
        // Окончательный отказ — неверный объём, нет денег, торговля запрещена — означает,
        // что ордера точно нет. Держать сигнал заблокированным здесь значит потерять его
        // навсегда без единой сделки.
        var config = new EngineConfig();
        var guard = new State.IdempotencyGuard();
        var quality = new Risk.ExecutionQualityTracker(config.Execution);

        var broker = new AlwaysFailsBroker(outcomeUnknown: false);
        var execution = new ExecutionEngine(config.Execution, broker, quality, guard, "test");

        execution.Open("sig-2", "BTCUSD", Side.Long, 1.0, Plan(), Spec, 50000, 1.0, 500, 2.0, null);

        Assert.False(guard.HasExecuted("sig-2"));
    }

    // ── 8. Условие перекупленности/перепроданности должно быть симметричным ───────

    [Fact]
    public void MeanReversionRequiresAnOversoldOscillatorToGoLong()
    {
        // Короткая сторона требует RSI 68..82. Длинная должна требовать зеркального
        // условия, иначе член всегда равен единице и перестаёт что-либо проверять.
        //
        // Одного лишь множителя мало: геометрическое среднее шести членов обнуляет вклад
        // слабого члена лишь частично (0.01 в степени 1/6 — это всё ещё 0.46). Поэтому
        // крайность осциллятора проверяется отдельным вето, как и запас хода до средней.
        var strategy = new Quant.Core.Strategies.MeanReversionStrategy();

        var results = ContextBuilder.WalkAndCollect(strategy,
            new MarketSimulator(seed: 603).GenerateRange(1500, 50000, 500), MarketRegime.Range);

        var longs = results.Where(r => r.Signal.Direction == Side.Long).ToList();
        _out.WriteLine($"лонгов: {longs.Count}, шортов: {results.Count - longs.Count}");

        foreach (var r in longs)
        {
            Assert.True(r.Features.Rsi < 45,
                $"Лонг от средней при RSI {r.Features.Rsi:F1} — осциллятор не проверяется.");
        }
    }

    // ── 9. Виртуальное исполнение не бесплатно ───────────────────────────────────

    [Fact]
    public void SimulatedFillsPayCommissionAsWellAsSpread()
    {
        // Теневой режим, в котором сделки исполняются без комиссии, доказывает лишь то, что
        // стратегия работала бы у брокера, который не берёт комиссию.
        var config = new EngineConfig();
        var broker = new SimulatedBroker(config.Execution, new CostModel(config.Execution),
            new AccountSnapshot(100000, 100000, 0, 100000, 2000, 50, false, "USD"), false);

        broker.RegisterSymbol(Spec);
        broker.Quotes["BTCUSD"] = new Quote(RiskFixtures.T0, 49999, 50001);

        BrokerResult opened = broker.OpenPosition("BTCUSD", Side.Long, 1.0, 49000, null, "QCV3-t", "c", 0);
        Assert.True(opened.IsSuccessful);

        double equityBefore = broker.GetAccount().Equity;

        // Закрываем по той же цене: без издержек результат был бы нулевым.
        BrokerResult closed = broker.ClosePosition(opened.PositionId, 0);
        Assert.True(closed.IsSuccessful);

        double equityAfter = broker.GetAccount().Equity;
        _out.WriteLine($"капитал {equityBefore:F2} → {equityAfter:F2}");

        Assert.True(equityAfter < equityBefore,
            "Круговая сделка по одной цене обязана стоить денег: спред и комиссия.");
    }

    // ── 10. Невычислимые издержки запретительны, а не нулевые ────────────────────

    [Fact]
    public void AnUncomputableCostIsProhibitiveRatherThanFree()
    {
        var model = new CostModel(new ExecutionConfig());

        CostEstimate noSpec = model.Estimate(null, 10, 100, 50000, false);
        CostEstimate noStop = model.Estimate(Spec, 10, 0, 50000, false);
        CostEstimate noPrice = model.Estimate(Spec, 10, 100, 0, false);

        foreach (CostEstimate cost in new[] { noSpec, noStop, noPrice })
        {
            Assert.True(cost.TotalR >= 1.0,
                $"Невычислимые издержки вернули {cost.TotalR:F3}R — это льстит сделке, о которой ничего не известно.");
        }
    }

    // ── 11. Позиция, исчезнувшая у брокера, не должна пропадать из памяти ────────

    [Fact]
    public void APositionClosedWhileOfflineStillReachesTheStatistics()
    {
        // Стоп мог сработать, пока робот был выключен. Если такая позиция просто исчезает,
        // её исход не попадает ни в статистику, ни в счётчик серии убытков — то есть
        // рестарт бесшумно стирает проигранную сделку.
        var config = new EngineConfig();
        var broker = new SimulatedBroker(config.Execution, new CostModel(config.Execution),
            new AccountSnapshot(100000, 100000, 0, 100000, 2000, 50, false, "USD"), false);
        broker.RegisterSymbol(Spec);
        broker.Quotes["BTCUSD"] = new Quote(RiskFixtures.T0, 49999, 50001);

        var store = new State.InMemoryStateStore();
        var state = new State.BotState
        {
            InstanceId = "test",
            SavedAtUtc = RiskFixtures.T0,
            DayStartEquity = 100000,
            WeekStartEquity = 100000,
            AllTimePeakEquity = 100000,
        };
        state.Positions.Add(new State.PersistedPosition
        {
            TradeId = "gone", SignalId = "sig", BrokerPositionId = 999,
            SymbolName = "BTCUSD", Direction = (int)Side.Long, StrategyName = "TrendFollowing",
            EntryTimeUtc = RiskFixtures.T0.AddHours(-2), EntryPrice = 50000,
            InitialVolumeInUnits = 1, CurrentVolumeInUnits = 1,
            InitialStopPrice = 49500, CurrentStopPrice = 49500, RiskPerUnit = 500,
            RiskAmount = 500, PlannedRewardToRisk = 2.0, EnsembleConfidence = 0.7,
        });
        store.Save(state);

        var engine = new TradingEngine(config, broker, store, NullJournalSink.Instance, "test");
        engine.AddSymbol("BTCUSD", Spec);
        engine.Restore(RiskFixtures.T0);

        _out.WriteLine($"сделок в памяти: {engine.Performance.TotalTrades}");
        _out.WriteLine($"серия убытков: {engine.Risk.ConsecutiveLosses}");

        Assert.Equal(1, engine.Performance.TotalTrades);
    }

    // ── вспомогательное ──────────────────────────────────────────────────────────


    // ── 1b. Чужой бар не должен продвигать счётчик удержания ──────────────────────

    [Fact]
    public void AnotherSymbolsBarDoesNotAdvanceHoldingTime()
    {
        var manager = new PositionManager(new ExitConfig());
        var market = new PipelineHarness();
        market.Feed(new MarketSimulator(seed: 611).Generate(1200, 0.0003, 0.002));

        OpenPosition position = Position();
        ExitPlan plan = Plan();

        // Двадцать закрытий баров ДРУГИХ инструментов — движок передаёт onClosedBar: false.
        for (int i = 0; i < 20; i++)
        {
            manager.Evaluate(position, plan, 50100, Spec, market.Data, null, onClosedBar: false);
        }

        Assert.Equal(0, position.BarsHeld);
    }

    // ── 4. Отключённая стратегия должна накапливать теневую запись ────────────────

    [Fact]
    public void DisabledStrategyAccumulatesShadowRecordAndCanRecover()
    {
        // Без записей ИСХОДОВ отключение необратимо: порог восстановления требует
        // ShadowTradesForRecovery виртуальных сделок, а счётчик сигналов их не создаёт.
        var config = new AdaptationConfig();
        var tracker = new ShadowTracker(config);
        var store = new PerformanceStore(config);

        var signal = new StrategySignal
        {
            StrategyName = "Breakout",
            Kind = StrategyKind.Breakout,
            Direction = Side.Long,
            Confidence = 0.7,
            AnchorPrice = 50000,
        };

        bool opened = tracker.Open(
            RiskFixtures.T0, "BTCUSD", signal, Plan(), 50000, MarketRegime.TrendUp,
            null, costInR: 0.05, mode: OperatingMode.Paper);

        Assert.True(opened, "Сигнал отключённой стратегии обязан встать под виртуальное наблюдение.");

        // Бар, достигающий цели: виртуальная сделка закрывается в плюс.
        var closed = new List<TradeRecord>();
        tracker.OnBarClosed(RiskFixtures.T0.AddMinutes(5), "BTCUSD",
            new Candle(RiskFixtures.T0, 50000, 50700, 49900, 50650, 100), closed.Add);

        Assert.Single(closed);
        Assert.True(closed[0].IsVirtual, "Теневая сделка не имеет права попасть в реальную статистику.");

        store.Record(closed[0]);
        Assert.Equal(1, store.Get(PerformanceStore.ShadowKey("Breakout")).TotalTrades);
        Assert.Equal(0, store.Get(PerformanceStore.StrategyKey("Breakout")).TotalTrades);

        // Издержки списаны: результат меньше валового хода.
        double grossR = (50600.0 - 50000.0) / 500.0;
        Assert.True(closed[0].R < grossR,
            $"Теневая сделка обязана платить издержки: R={closed[0].R:F3} должно быть меньше валового {grossR:F3}.");
    }

    [Fact]
    public void ShadowPositionPrefersTheStopWhenOneBarTouchesBoth()
    {
        // Порядок внутри бара неизвестен. Оптимистичное допущение систематически завышало бы
        // теневой результат на самых волатильных барах — ровно там, где цена ошибки выше всего.
        var tracker = new ShadowTracker(new AdaptationConfig());
        var signal = new StrategySignal { StrategyName = "MeanReversion", Direction = Side.Long, Confidence = 0.6 };

        tracker.Open(RiskFixtures.T0, "BTCUSD", signal, Plan(), 50000, MarketRegime.Range,
            null, costInR: 0.0, mode: OperatingMode.Paper);

        var closed = new List<TradeRecord>();
        tracker.OnBarClosed(RiskFixtures.T0.AddMinutes(5), "BTCUSD",
            new Candle(RiskFixtures.T0, 50000, 51000, 49000, 50500, 100), closed.Add);

        Assert.Single(closed);
        Assert.Equal(ExitReason.StopLoss, closed[0].ExitReason);
    }

    [Fact]
    public void ShadowTrackerLimitsConcurrentObservationsPerStrategy()
    {
        // Десяток виртуальных сделок на одном движении — это одно наблюдение, посчитанное
        // десять раз, и порог восстановления был бы взят фиктивной статистикой.
        var config = new AdaptationConfig { MaxConcurrentShadowPositions = 2 };
        var tracker = new ShadowTracker(config);
        var signal = new StrategySignal { StrategyName = "Momentum", Direction = Side.Long, Confidence = 0.6 };

        Assert.True(tracker.Open(RiskFixtures.T0, "BTCUSD", signal, Plan(), 50000, MarketRegime.TrendUp, null, 0, OperatingMode.Paper));
        Assert.True(tracker.Open(RiskFixtures.T0, "ETHUSD", signal, Plan(), 50000, MarketRegime.TrendUp, null, 0, OperatingMode.Paper));
        Assert.False(tracker.Open(RiskFixtures.T0, "SOLUSD", signal, Plan(), 50000, MarketRegime.TrendUp, null, 0, OperatingMode.Paper));

        // Тот же инструмент и та же стратегия не наблюдаются дважды.
        Assert.False(tracker.Open(RiskFixtures.T0, "BTCUSD", signal, Plan(), 50000, MarketRegime.TrendUp, null, 0, OperatingMode.Paper));
    }

    // ── 7. Одна сделка — одна запись, каким бы путём ни пришло закрытие ───────────

    [Fact]
    public void ATradeIsRecordedOnceEvenIfClosedByBothPaths()
    {
        // Платформа может поднять событие закрытия СИНХРОННО внутри вызова ClosePosition.
        // Тогда исход приходит дважды: от события и от собственного кода закрытия.
        var harness = new EngineHarness();
        OpenPosition position = Position();
        AccountSnapshot account = harness.Broker.GetAccount();

        harness.Engine.CloseTrade(RiskFixtures.T0.AddHours(1), position, 50600, ExitReason.TakeProfit1, account);
        long after = harness.Engine.TradesClosed;

        harness.Engine.CloseTrade(RiskFixtures.T0.AddHours(1), position, 50600, ExitReason.TakeProfit1, account);

        Assert.Equal(after, harness.Engine.TradesClosed);
        Assert.Equal(1, harness.Engine.Performance.Get(PerformanceStore.StrategyKey(position.StrategyName)).TotalTrades);
    }

    // ── 11. Позиция, закрывшаяся во время простоя, не должна исчезать бесследно ───

    [Fact]
    public void PositionsClosedWhileTheBotWasOfflineReachTheStatistics()
    {
        // Пока робот выключен, у брокера срабатывают стопы. Если при восстановлении просто
        // забыть такие позиции, то перезапуск бесшумно стирает проигранные сделки — а именно
        // они должны были бы ужесточить постуру риска.
        var config = new EngineConfig();
        var broker = new SimulatedBroker(config.Execution, new CostModel(config.Execution),
            new AccountSnapshot(100000, 100000, 0, 100000, 2000, 50, false, "USD"), false);

        var store = new InMemoryStateStore();
        store.Save(new BotState
        {
            InstanceId = "test",
            SavedAtUtc = RiskFixtures.T0,
            Mode = (int)OperatingMode.Paper,
            DayStartUtc = RiskFixtures.T0,
            DayStartEquity = 100000,
            WeekStartUtc = RiskFixtures.T0,
            WeekStartEquity = 100000,
            AllTimePeakEquity = 100000,
            Positions =
            {
                new PersistedPosition
                {
                    TradeId = "t-1",
                    SignalId = "s-1",
                    BrokerPositionId = 4242,
                    SymbolName = "BTCUSD",
                    Direction = (int)Side.Long,
                    StrategyName = "Breakout",
                    Regime = (int)MarketRegime.TrendUp,
                    EntryTimeUtc = RiskFixtures.T0,
                    EntryPrice = 50000,
                    InitialVolumeInUnits = 1,
                    CurrentVolumeInUnits = 1,
                    InitialStopPrice = 49500,
                    CurrentStopPrice = 49500,
                    Target1Price = 51000,
                    RiskPerUnit = 500,
                    RiskAmount = 500,
                    RiskFractionOfEquity = 0.005,
                },
            },
        });

        var engine = new TradingEngine(config, broker, store, NullJournalSink.Instance, "test");
        engine.AddSymbol("BTCUSD", Spec);

        // У брокера этой позиции уже нет: стоп сработал, пока робот был выключен.
        engine.Restore(RiskFixtures.T0.AddHours(6));

        Assert.Equal(1, engine.Performance.Get(PerformanceStore.StrategyKey("Breakout")).TotalTrades);
        Assert.Equal(1, engine.Risk.ConsecutiveLosses);
        Assert.False(engine.IsHaltedByReconciliation,
            "Позиция, которой у брокера больше нет, — это закрытая сделка, а не расхождение.");
    }

    // ── 12. Бюджет риска достаётся лучшей возможности, а не первой ────────────────

    [Fact]
    public void CandidatesAreRankedRatherThanServedInArrivalOrder()
    {
        var config = new PortfolioConfig { MaxCandidatesPerCycle = 2 };
        var ranker = new OpportunityRanker(config, new CorrelationEngine(config));

        TradeCandidate weak = Candidate("AAAUSD", edge: 0.02, confidence: 0.30, rewardToRisk: 1.1, cost: 0.25);
        TradeCandidate strong = Candidate("ZZZUSD", edge: 0.40, confidence: 0.85, rewardToRisk: 2.8, cost: 0.03);
        TradeCandidate middling = Candidate("MMMUSD", edge: 0.18, confidence: 0.60, rewardToRisk: 1.8, cost: 0.10);

        // Слабый приходит ПЕРВЫМ — именно так ошибка и проявлялась бы.
        IReadOnlyList<TradeCandidate> ranked = ranker.Rank(
            new List<TradeCandidate> { weak, strong, middling }, Array.Empty<OpenPosition>());

        Assert.Equal(2, ranked.Count);
        Assert.Equal("ZZZUSD", ranked[0].SymbolName);
        Assert.Equal("MMMUSD", ranked[1].SymbolName);
        Assert.DoesNotContain(ranked, c => c.SymbolName == "AAAUSD");
    }

    private static TradeCandidate Candidate(string symbol, double edge, double confidence, double rewardToRisk, double cost)
    {
        return new TradeCandidate
        {
            SymbolName = symbol,
            TimeUtc = RiskFixtures.T0,
            Regime = new RegimeAssessment { Primary = MarketRegime.TrendUp, Confidence = 0.7 },
            Ensemble = new EnsembleDecision { Direction = Side.Long, Confidence = confidence, EffectiveVotes = 2 },
            Probability = new ProbabilityEstimate(0.5, 0.05, 100, confidence, "test"),
            Exit = new ExitPlan { IsValid = true, StopPrice = 49500, StopDistance = 500, Target1R = rewardToRisk },
            ExpectedValue = new ExpectedValueResult
            {
                LowerBoundR = edge,
                CostR = cost,
            },
            DataQuality = new DataQualityReport(true, 0.9, Array.Empty<string>()),
        };
    }

    private static OpenPosition Position(
        string symbol = "BTCUSD", Side side = Side.Long,
        double entry = 50000, double stop = 49500, double units = 1.0) => new OpenPosition
        {
            TradeId = Guid.NewGuid().ToString("N"),
            SymbolName = symbol,
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

    private static ExitPlan Plan() => new ExitPlan
    {
        IsValid = true,
        StopPrice = 49500,
        StopDistance = 500,
        StopInAtr = 1.5,
        Target1Price = 50600,
        Target2Price = 51100,
        Target1R = 1.2, Target2R = 2.2,
        Target1ClosePercent = 0.40, Target2ClosePercent = 0.35,
        BreakEvenTriggerR = 0.9,
        NetBreakEvenPrice = 50050,
        TrailDistanceInAtr = 2.2,
        TrailActivationR = 1.0,
        TimeStopBars = 100,
    };

    /// <summary>Брокер, который всегда отказывает — с выбранным типом ошибки.</summary>
    private sealed class AlwaysFailsBroker : IBroker
    {
        private readonly bool _transient;
        public AlwaysFailsBroker(bool outcomeUnknown) { _transient = outcomeUnknown; }

        public BrokerResult OpenPosition(string s, Side d, double v, double stop, double? tp, string l, string c, double m) =>
            _transient ? BrokerResult.Unknown("таймаут") : BrokerResult.Fail("неверный объём", isTransient: false);

        public BrokerResult ClosePosition(long id, double v) => BrokerResult.Fail("нет", false);
        public BrokerResult ModifyStop(long id, double p) => BrokerResult.Fail("нет", false);
        public IReadOnlyList<BrokerPosition> GetOpenPositions(string prefix) => Array.Empty<BrokerPosition>();
        public AccountSnapshot GetAccount() => new AccountSnapshot(10000, 10000, 0, 10000, 1000, 50, false, "USD");
    }
}