using System;
using System.Collections.Generic;
using System.Linq;
using Quant.Core;
using Quant.Core.Config;
using Quant.Core.Ev;
using Quant.Core.Execution;
using Quant.Core.Journal;
using Quant.Core.Primitives;
using Quant.Core.State;
using Quant.Core.Stats;
using Xunit;
using Xunit.Abstractions;

namespace Quant.Core.Tests;

/// <summary>Собирает движок поверх симулированного брокера и прогоняет через него рынок.</summary>
internal sealed class EngineHarness
{
    private readonly Dictionary<Tf, List<Candle>> _pending = new Dictionary<Tf, List<Candle>>();
    private readonly List<string> _log = new List<string>();

    public EngineHarness(EngineConfig config = null, string symbol = "BTCUSD", double startingEquity = 100000, bool simulateFailures = false)
    {
        Config = config ?? new EngineConfig();
        Config.Mode = OperatingMode.Paper;
        Symbol = symbol;

        Spec = new SymbolSpec(symbol, 0.01, 0.01, 2, 0.001, 1000, 0.001, 35, 0.01, 0, 2.0, true);
        Account = new AccountSnapshot(startingEquity, startingEquity, 0, startingEquity, 2000, 50, false, "USD");

        Broker = new SimulatedBroker(Config.Execution, new CostModel(Config.Execution), Account, simulateFailures);
        Store = new InMemoryStateStore();

        Engine = new TradingEngine(Config, Broker, Store, new ListSink(_log), "test");
        Engine.AddSymbol(symbol, Spec);

        foreach (Tf tf in Config.Data.Timeframes) _pending[tf] = new List<Candle>();
    }

    public EngineConfig Config { get; }
    public string Symbol { get; }
    public SymbolSpec Spec { get; }
    public AccountSnapshot Account { get; }
    public SimulatedBroker Broker { get; }
    public InMemoryStateStore Store { get; }
    public TradingEngine Engine { get; }
    public IReadOnlyList<string> Log => _log;
    public DateTime LastTimeUtc { get; private set; }

    /// <summary>Прогоняет поток сигнальных баров через весь движок, включая стопы брокера.</summary>
    /// <summary>Прогоняет бары в режиме ПРОГРЕВА: наполняет данные, но не принимает решений.</summary>
    public void FeedWarmUp(IEnumerable<Candle> signalBars)
    {
        int signalMinutes = (int)Config.Data.SignalTimeframe;

        foreach (Candle bar in signalBars)
        {
            LastTimeUtc = bar.OpenTimeUtc.AddMinutes(signalMinutes);

            foreach (Tf tf in Config.Data.Timeframes)
            {
                if ((int)tf < signalMinutes) continue;

                int ratio = Math.Max(1, (int)tf / signalMinutes);
                _pending[tf].Add(bar);
                if (_pending[tf].Count < ratio) continue;

                Engine.OnBarClosed(LastTimeUtc, Symbol, tf, Aggregate(_pending[tf]), isWarmUp: true);
                _pending[tf].Clear();
            }
        }
    }

    public void Feed(IEnumerable<Candle> signalBars, double spreadFraction = 0.0002)
    {
        int signalMinutes = (int)Config.Data.SignalTimeframe;

        foreach (Candle bar in signalBars)
        {
            LastTimeUtc = bar.OpenTimeUtc.AddMinutes(signalMinutes);

            double spread = bar.Close * spreadFraction;
            var quote = new Quote(LastTimeUtc, bar.Close - (spread / 2), bar.Close + (spread / 2));

            Broker.Quotes[Symbol] = quote;
            Engine.OnTick(Symbol, quote);

            // Стопы и тейки у брокера срабатывают внутри бара — до того, как движок увидит
            // его закрытие. Порядок здесь повторяет реальный.
            var closed = Broker.ProcessBar(Symbol, bar.High, bar.Low);
            for (int i = 0; i < closed.Count; i++)
            {
                (BrokerPosition p, double exitPrice, bool wasStop) = closed[i];
                double gross = (p.Direction == Side.Long ? exitPrice - p.EntryPrice : p.EntryPrice - exitPrice) * p.VolumeInUnits;
                Engine.OnBrokerPositionClosed(
                    LastTimeUtc, p.PositionId, exitPrice,
                    wasStop ? ExitReason.StopLoss : ExitReason.TakeProfit1, gross, 0, 0);
            }

            foreach (Tf tf in Config.Data.Timeframes)
            {
                if ((int)tf < signalMinutes) continue;

                int ratio = Math.Max(1, (int)tf / signalMinutes);
                _pending[tf].Add(bar);
                if (_pending[tf].Count < ratio) continue;

                Engine.OnBarClosed(LastTimeUtc, Symbol, tf, Aggregate(_pending[tf]));
                _pending[tf].Clear();
            }
        }
    }

    private static Candle Aggregate(List<Candle> bars)
    {
        double high = double.MinValue, low = double.MaxValue, volume = 0;
        for (int i = 0; i < bars.Count; i++)
        {
            if (bars[i].High > high) high = bars[i].High;
            if (bars[i].Low < low) low = bars[i].Low;
            volume += bars[i].Volume;
        }
        return new Candle(bars[0].OpenTimeUtc, bars[0].Open, high, low, bars[bars.Count - 1].Close, volume);
    }

    private sealed class ListSink : IJournalSink
    {
        private readonly List<string> _target;
        public ListSink(List<string> target) { _target = target; }
        public void Write(string message) { _target.Add(message); }
    }
}

public class EndToEndTests
{
    private readonly ITestOutputHelper _out;
    public EndToEndTests(ITestOutputHelper output) { _out = output; }

    /// <summary>
    /// Конфигурация ДЛЯ ТЕСТА, а не для торговли.
    ///
    /// Пороги опущены настолько, чтобы сделки вообще случались на короткой синтетической
    /// выборке: задача этих тестов — проверить, что путь «сигнал → решение → ордер →
    /// сопровождение → закрытие → статистика» действительно отрабатывает целиком. Проверить
    /// это на настройках по умолчанию невозможно, потому что они правильно отказываются
    /// торговать почти всё — и ровно это утверждает отдельный тест
    /// <see cref="DefaultConfigurationIsExtremelySelective"/>.
    ///
    /// Ни одно из этих значений не является рекомендацией. Априорный винрейт 80% здесь —
    /// способ гарантированно провести кандидата через экономические проверки, а не оценка
    /// чего бы то ни было.
    /// </summary>
    private static EngineConfig PermissiveConfig()
    {
        var config = new EngineConfig();
        config.Regime.MinConfidenceToTrade = 0.25;
        config.Strategy.MinEnsembleConfidence = 0.35;
        config.Strategy.MinStrategyConfidence = 0.30;
        config.Ev.BaseMinimumEdgeR = 0.0;
        config.Ev.EdgeConfidenceZ = 0.0;
        config.Ev.MinRewardToRisk = 1.0;
        config.Ev.CostEdgeMultiplier = 1.0;
        config.Ev.UncertaintyEdgeMultiplier = 0.0;
        config.Probability.PriorWinRate = 0.80;
        config.Probability.PriorStrength = 60;
        config.Risk.PostTradeCooldownMinutes = 0;
        config.Risk.MaxAtrPercentileForEntry = 0.99;
        config.Adaptation.AdaptationIntervalMinutes = 1;
        return config;
    }

    [Fact]
    public void RunsAFullMarketWithoutThrowingAndLeavesConsistentState()
    {
        var h = new EngineHarness(PermissiveConfig());
        var sim = new MarketSimulator(seed: 101);

        var bars = new List<Candle>();
        bars.AddRange(sim.Generate(1500, 0.0004, 0.0025));    // тренд вверх
        bars.AddRange(sim.GenerateRange(700, sim.Price, sim.Price * 0.02));   // боковик
        bars.AddRange(sim.Generate(700, -0.0005, 0.004));     // падение с ростом волатильности

        h.Feed(bars, spreadFraction: 0.00005);

        _out.WriteLine($"сделок закрыто: {h.Engine.TradesClosed}, открыто позиций: {h.Engine.Positions.Count}");
        _out.WriteLine(h.Engine.Journal.RejectionSummary());

        // Открытых позиций не больше лимита, и все они реальны с точки зрения брокера.
        Assert.True(h.Engine.Positions.Count <= h.Config.Portfolio.MaxOpenPositions);

        IReadOnlyList<BrokerPosition> brokerPositions = h.Broker.GetOpenPositions("QCV3");
        Assert.Equal(brokerPositions.Count, h.Engine.Positions.Count);

        Assert.False(h.Engine.IsHaltedByReconciliation,
            "Сквозной прогон не должен приводить к расхождению с брокером.");
    }

    [Fact]
    public void ActuallyTradesUnderPermissiveThresholds()
    {
        var h = new EngineHarness(PermissiveConfig());
        h.Feed(new MarketSimulator(seed: 102).Generate(2500, 0.0004, 0.0025), spreadFraction: 0.00005);

        long decisions = h.Engine.Journal.TotalAccepted + h.Engine.Journal.TotalRejected;
        Assert.True(decisions > 100, $"Движок принял всего {decisions} решений — цикл не работает.");

        Assert.True(h.Engine.Journal.TotalAccepted > 0,
            "При ослабленных порогах система должна была совершить хотя бы одну сделку. " +
            h.Engine.Journal.RejectionSummary(topN: 25));
    }

    [Fact]
    public void DefaultConfigurationIsExtremelySelective()
    {
        // Ровно тот же рынок, но с настройками по умолчанию. Это не недостаток: раздел 168
        // прямо требует, чтобы «не торговать» было полноценным и частым решением.
        var h = new EngineHarness();
        h.Feed(new MarketSimulator(seed: 103).Generate(2500, 0.0004, 0.0025));

        long total = h.Engine.Journal.TotalAccepted + h.Engine.Journal.TotalRejected;
        double acceptanceRate = total == 0 ? 0 : (double)h.Engine.Journal.TotalAccepted / total;

        _out.WriteLine($"доля принятых: {acceptanceRate:P2} из {total} решений");
        _out.WriteLine(h.Engine.Journal.RejectionSummary());

        Assert.True(acceptanceRate < 0.10,
            $"Настройки по умолчанию приняли {acceptanceRate:P1} сигналов — это слишком много для конфигурации, ориентированной на выживание.");
    }

    [Fact]
    public void NeverExceedsTheTotalRiskBudget()
    {
        var config = PermissiveConfig();
        config.Portfolio.MaxOpenPositions = 6;
        config.Portfolio.MaxPositionsPerSymbol = 3;

        var h = new EngineHarness(config);
        h.Feed(new MarketSimulator(seed: 104).Generate(2500, 0.0004, 0.003), spreadFraction: 0.00005);

        // Совокупный риск по всем открытым позициям не может превысить бюджет ни в один момент.
        double totalRisk = 0;
        for (int i = 0; i < h.Engine.Positions.Count; i++)
        {
            totalRisk += Quant.Core.Portfolio.PortfolioRiskEngine.CurrentRiskPercent(
                h.Engine.Positions[i], h.Broker.GetAccount().Equity);
        }

        Assert.True(totalRisk <= config.Risk.MaxTotalOpenRiskPercent + 1e-6,
            $"Совокупный риск {totalRisk:F3}% превысил лимит {config.Risk.MaxTotalOpenRiskPercent:F3}%.");
    }

    [Fact]
    public void EveryTradeIsRecordedWithItsFullDecisionContext()
    {
        var h = new EngineHarness(PermissiveConfig());
        h.Feed(new MarketSimulator(seed: 105).Generate(2500, 0.0004, 0.0025), spreadFraction: 0.00005);

        IReadOnlyList<TradeRecord> trades = h.Engine.Performance.Trades;
        Assert.NotEmpty(trades);

        foreach (TradeRecord t in trades)
        {
            Assert.False(string.IsNullOrEmpty(t.StrategyName), "Сделка без стратегии необъяснима задним числом.");
            Assert.NotEqual(MarketRegime.Unknown, t.Regime);
            Assert.True(t.EnsembleConfidence > 0);
            Assert.True(t.RiskAmount > 0);
            Assert.True(t.MaeR >= 0 && t.MfeR >= 0);
            Assert.NotEqual(ExitReason.Unknown, t.ExitReason);
            Assert.True(t.ExitTimeUtc >= t.EntryTimeUtc);
        }
    }

    [Fact]
    public void ProducesAStatisticalReportFromItsOwnJournal()
    {
        var h = new EngineHarness(PermissiveConfig());
        h.Feed(new MarketSimulator(seed: 106).Generate(3000, 0.0004, 0.0025), spreadFraction: 0.00005);

        IReadOnlyList<TradeRecord> trades = h.Engine.Performance.Trades;
        if (trades.Count < 5) { _out.WriteLine("сделок слишком мало для отчёта"); return; }

        PerformanceReport report = MetricsCalculator.Compute(trades, 100000);
        _out.WriteLine(report.Render());

        Assert.Equal(trades.Count, report.Trades);
        Assert.InRange(report.WinRate, 0.0, 1.0);
        Assert.True(report.MaxDrawdownR >= 0);
        Assert.InRange(report.ProfitConcentrationTop5, 0.0, 1.0);
    }
}

public class RestartRecoveryTests
{
    [Fact]
    public void StateSurvivesARestartWithItsGuardsIntact()
    {
        var config = new EngineConfig();
        var h = new EngineHarness(config);

        h.Feed(new MarketSimulator(seed: 201).Generate(1400, 0.0004, 0.0025));
        Assert.True(h.Store.SaveCount > 0, "Состояние должно сохраняться в ходе работы.");

        BotState saved = h.Store.Load();
        Assert.NotNull(saved);
        Assert.Equal(1, saved.SchemaVersion);
        Assert.True(saved.AllTimePeakEquity > 0);
        Assert.True(saved.DayStartEquity > 0);
    }

    [Fact]
    public void ARestartWithBrokerPositionsButNoSavedStateHaltsRatherThanGuesses()
    {
        var config = new EngineConfig();
        var broker = new SimulatedBroker(config.Execution, new CostModel(config.Execution),
            new AccountSnapshot(100000, 100000, 0, 100000, 2000, 50, false, "USD"), false);

        broker.Quotes["BTCUSD"] = new Quote(RiskFixtures.T0, 49999, 50001);
        broker.OpenPosition("BTCUSD", Side.Long, 1.0, 49000, null, "QCV3-test", "orphan", 0);

        var engine = new TradingEngine(config, broker, new InMemoryStateStore(), NullJournalSink.Instance, "test");
        engine.AddSymbol("BTCUSD", new SymbolSpec("BTCUSD", 0.01, 0.01, 2, 0.001, 1000, 0.001, 35, 0.01, 0, 2.0, true));

        engine.Restore(RiskFixtures.T0);

        Assert.True(engine.IsHaltedByReconciliation,
            "Позиция у брокера без сохранённого контекста не может управляться вслепую — система обязана остановиться.");
    }

    [Fact]
    public void CorruptedStateIsDiscardedRatherThanPartiallyApplied()
    {
        Assert.Null(StateSerializer.Deserialize("{ это не json"));
        Assert.Null(StateSerializer.Deserialize(""));
        Assert.Null(StateSerializer.Deserialize(null));

        // Состояние другой версии схемы отбрасывается целиком: интерпретировать его наугад
        // опаснее, чем стартовать без него.
        Assert.Null(StateSerializer.Deserialize("{\"SchemaVersion\":999}"));
    }

    [Fact]
    public void StateRoundTripsExactly()
    {
        var original = new BotState
        {
            InstanceId = "abc",
            SavedAtUtc = RiskFixtures.T0,
            RiskState = (int)RiskState.Defensive,
            ConsecutiveLosses = 3,
            CooldownUntilUtc = RiskFixtures.T0.AddMinutes(45),
            DayStartEquity = 10500,
            WeekStartEquity = 11000,
            AllTimePeakEquity = 12000,
            LastEquity = 10200,
            TotalTradesRecorded = 57,
        };
        original.ExecutedSignalIds.Add("BTCUSD|Trend|Long|20250602080000");
        original.LastSignalBarUtc["BTCUSD"] = RiskFixtures.T0;
        original.Positions.Add(new PersistedPosition
        {
            TradeId = "t1", BrokerPositionId = 42, SymbolName = "BTCUSD",
            Direction = (int)Side.Long, EntryPrice = 50000, CurrentStopPrice = 49500,
            InitialVolumeInUnits = 1, CurrentVolumeInUnits = 1, RiskPerUnit = 500,
        });

        BotState restored = StateSerializer.Deserialize(StateSerializer.Serialize(original));

        Assert.NotNull(restored);
        Assert.Equal(original.ConsecutiveLosses, restored.ConsecutiveLosses);
        Assert.Equal(original.CooldownUntilUtc, restored.CooldownUntilUtc);
        Assert.Equal(original.AllTimePeakEquity, restored.AllTimePeakEquity, 6);
        Assert.Equal(original.TotalTradesRecorded, restored.TotalTradesRecorded);
        Assert.Single(restored.ExecutedSignalIds);
        Assert.Single(restored.Positions);
        Assert.Equal(42, restored.Positions[0].BrokerPositionId);
    }

    [Fact]
    public void AStorageFailureDoesNotStopTrading()
    {
        var h = new EngineHarness(PermissiveConfigFor());
        h.Store.FailWrites = true;

        // Ни одно исключение не должно вырваться наружу: потеря снимка состояния —
        // неприятность, исключение в торговом цикле — авария.
        h.Feed(new MarketSimulator(seed: 202).Generate(1400, 0.0004, 0.0025), spreadFraction: 0.00005);

        Assert.True(h.Store.SaveCount > 0);
        Assert.Null(h.Store.Load());
    }

    private static EngineConfig PermissiveConfigFor()
    {
        var config = new EngineConfig();
        config.Regime.MinConfidenceToTrade = 0.30;
        config.Strategy.MinEnsembleConfidence = 0.35;
        config.Ev.BaseMinimumEdgeR = 0.0;
        config.Ev.EdgeConfidenceZ = 0.0;
        return config;
    }
}

public class IdempotencyTests
{
    [Fact]
    public void TheSameBarAlwaysProducesTheSameSignalId()
    {
        DateTime bar = new DateTime(2025, 6, 2, 8, 5, 0, DateTimeKind.Utc);

        string a = IdempotencyGuard.BuildSignalId("BTCUSD", "TrendFollowing", Side.Long, bar);
        string b = IdempotencyGuard.BuildSignalId("BTCUSD", "TrendFollowing", Side.Long, bar);

        Assert.Equal(a, b);
    }

    [Fact]
    public void DifferentBarsOrDirectionsProduceDifferentIds()
    {
        DateTime bar = new DateTime(2025, 6, 2, 8, 5, 0, DateTimeKind.Utc);

        string baseline = IdempotencyGuard.BuildSignalId("BTCUSD", "T", Side.Long, bar);

        Assert.NotEqual(baseline, IdempotencyGuard.BuildSignalId("BTCUSD", "T", Side.Long, bar.AddMinutes(5)));
        Assert.NotEqual(baseline, IdempotencyGuard.BuildSignalId("BTCUSD", "T", Side.Short, bar));
        Assert.NotEqual(baseline, IdempotencyGuard.BuildSignalId("ETHUSD", "T", Side.Long, bar));
        Assert.NotEqual(baseline, IdempotencyGuard.BuildSignalId("BTCUSD", "M", Side.Long, bar));
    }

    [Fact]
    public void ASignalCanOnlyBeMarkedExecutedOnce()
    {
        var guard = new IdempotencyGuard();

        Assert.True(guard.TryMarkExecuted("sig-1"));
        Assert.False(guard.TryMarkExecuted("sig-1"));
        Assert.True(guard.HasExecuted("sig-1"));
    }

    [Fact]
    public void ReleasingAFailedOrderAllowsARetry()
    {
        var guard = new IdempotencyGuard();

        guard.TryMarkExecuted("sig-1");
        guard.Release("sig-1");

        Assert.False(guard.HasExecuted("sig-1"));
        Assert.True(guard.TryMarkExecuted("sig-1"));
    }

    [Fact]
    public void TheGuardSurvivesARestart()
    {
        var before = new IdempotencyGuard();
        before.TryMarkExecuted("sig-1");
        before.TryMarkExecuted("sig-2");

        var after = new IdempotencyGuard();
        after.Restore(before.ExecutedIds);

        Assert.True(after.HasExecuted("sig-1"));
        Assert.True(after.HasExecuted("sig-2"));
        Assert.False(after.TryMarkExecuted("sig-1"));
    }

    [Fact]
    public void TheGuardStaysBoundedOverALongRun()
    {
        var guard = new IdempotencyGuard(capacity: 100);
        for (int i = 0; i < 10000; i++) guard.TryMarkExecuted("sig-" + i);

        Assert.True(guard.Count <= 100);
        Assert.True(guard.HasExecuted("sig-9999"));
        Assert.False(guard.HasExecuted("sig-0"));
    }

    [Fact]
    public void ARejectedOrderDoesNotPermanentlyBlockItsSignal()
    {
        var config = new EngineConfig();
        var quality = new Quant.Core.Risk.ExecutionQualityTracker(config.Execution);
        var guard = new IdempotencyGuard();

        // Брокер без котировок отклоняет всё как повторяемую ошибку.
        var broker = new SimulatedBroker(config.Execution, new CostModel(config.Execution),
            new AccountSnapshot(10000, 10000, 0, 10000, 1000, 50, false, "USD"), false);

        var execution = new ExecutionEngine(config.Execution, broker, quality, guard, "test");
        var spec = new SymbolSpec("BTCUSD", 0.01, 0.01, 2, 0.001, 1000, 0.001, 35, 0.01, 0, 2.0, true);
        var plan = new Quant.Core.Exits.ExitPlan { IsValid = true, StopPrice = 49500, StopDistance = 500 };

        BrokerResult result = execution.Open("sig-1", "BTCUSD", Side.Long, 1.0, plan, spec, 50000, 1.0, 500, 2.0, null);

        Assert.NotNull(result);
        Assert.False(result.IsSuccessful);
        Assert.False(guard.HasExecuted("sig-1"),
            "Отметка должна быть снята: иначе сигнал заблокирован навсегда без единой сделки.");
    }
}

public class FilterValueTests
{
    private static RejectedSignal Signal(NoTradeReason reason, double entry = 100, double stop = 5, double target = 10) =>
        new RejectedSignal
        {
            TimeUtc = RiskFixtures.T0,
            SymbolName = "BTCUSD",
            Direction = Side.Long,
            StrategyName = "T",
            Reason = reason,
            EntryPrice = entry,
            StopDistance = stop,
            TargetDistance = target,
        };

    [Fact]
    public void OnlyJudgementsAboutTheMarketAreTracked()
    {
        var ledger = new FilterValueLedger(new AdaptationConfig());

        ledger.Track(Signal(NoTradeReason.InsufficientEdge));
        ledger.Track(Signal(NoTradeReason.DailyLossLimit));
        ledger.Track(Signal(NoTradeReason.DataQuality));

        // Дневной лимит и качество данных — это состояние системы, а не суждение о сделке.
        Assert.Single(ledger.Tracking);
        Assert.Equal(NoTradeReason.InsufficientEdge, ledger.Tracking.First().Reason);
    }

    [Fact]
    public void AFilterThatAvoidedALossIsCreditedForIt()
    {
        var ledger = new FilterValueLedger(new AdaptationConfig());
        ledger.Track(Signal(NoTradeReason.SpreadTooWide));

        // Цена уходит вниз и задевает стоп: фильтр спас от убытка.
        ledger.OnBarClosed("BTCUSD", high: 101, low: 94);

        FilterStatistics stats = ledger.Statistics[NoTradeReason.SpreadTooWide];
        Assert.Equal(1, stats.ResolvedRejections);
        Assert.Equal(1, stats.LossesPrevented);
        Assert.True(stats.NetValueR > 0, "Предотвращённый убыток — это положительная ценность фильтра.");
    }

    [Fact]
    public void AFilterThatBlockedAWinnerIsChargedForIt()
    {
        var ledger = new FilterValueLedger(new AdaptationConfig());
        ledger.Track(Signal(NoTradeReason.InsufficientEdge));

        ledger.OnBarClosed("BTCUSD", high: 111, low: 99);

        FilterStatistics stats = ledger.Statistics[NoTradeReason.InsufficientEdge];
        Assert.Equal(1, stats.WinsMissed);
        Assert.True(stats.NetValueR < 0, "Упущенная прибыль — это цена фильтра.");
    }

    [Fact]
    public void ABarTouchingBothLevelsResolvesAgainstTheFilter()
    {
        // Внутрибарный порядок неизвестен. Оптимистичное допущение завышало бы цену фильтров,
        // то есть систематически склоняло бы систему к их снятию.
        var ledger = new FilterValueLedger(new AdaptationConfig());
        ledger.Track(Signal(NoTradeReason.Chasing));

        ledger.OnBarClosed("BTCUSD", high: 112, low: 94);

        Assert.Equal(1, ledger.Statistics[NoTradeReason.Chasing].LossesPrevented);
    }

    [Fact]
    public void UnderperformingFiltersAreOnlyFlaggedOnAnAdequateSample()
    {
        var ledger = new FilterValueLedger(new AdaptationConfig());

        for (int i = 0; i < 10; i++)
        {
            ledger.Track(Signal(NoTradeReason.InsufficientEdge));
            ledger.OnBarClosed("BTCUSD", 111, 99);
        }

        Assert.Empty(ledger.UnderperformingFilters(minimumSample: 30));

        for (int i = 0; i < 40; i++)
        {
            ledger.Track(Signal(NoTradeReason.InsufficientEdge));
            ledger.OnBarClosed("BTCUSD", 111, 99);
        }

        Assert.Contains(ledger.UnderperformingFilters(minimumSample: 30),
            f => f.Reason == NoTradeReason.InsufficientEdge);
    }
}
