using System;
using System.Collections.Generic;
using System.Text;
using Quant.Core;
using Quant.Core.Config;
using Quant.Core.Ev;
using Quant.Core.Execution;
using Quant.Core.Journal;
using Quant.Core.Numerics;
using Quant.Core.Primitives;
using Quant.Core.State;
using Quant.Core.Stats;
using Xunit;
using Xunit.Abstractions;

namespace Quant.Core.Tests;

/// <summary>
/// Память системы обязана переживать перезапуск.
///
/// Без этого адаптивный слой существует только формально: в облаке процесс
/// перезапускается регулярно, и каждый раз статистика, калибровка и распределения выходов
/// начинаются с нуля. Система вечно работает по априорным значениям и никогда ничему не
/// учится — при том что весь смысл этих слоёв в накоплении.
/// </summary>
public class PersistentPerformanceTests
{
    private readonly ITestOutputHelper _out;
    public PersistentPerformanceTests(ITestOutputHelper output) { _out = output; }

    private static readonly SymbolSpec Spec =
        new SymbolSpec("BTCUSD", 0.01, 0.01, 2, 0.001, 1000, 0.001, 35, 0.01, 0, 2.0, true);

    private static TradingEngine NewEngine(EngineConfig config, IStateStore store)
    {
        var broker = new SimulatedBroker(config.Execution, new CostModel(config.Execution),
            new AccountSnapshot(100000, 100000, 0, 100000, 5000, 50, false, "USD"), false);

        var engine = new TradingEngine(config, broker, store, NullJournalSink.Instance, "test");
        engine.AddSymbol("BTCUSD", Spec, MarketSchedule.Continuous);
        return engine;
    }

    private static TradeRecord Trade(int i, bool win)
    {
        DateTime entry = RiskFixtures.T0.AddHours(i);
        return new TradeRecord
        {
            TradeId = "t-" + i,
            SignalId = "s-" + i,
            SymbolName = "BTCUSD",
            StrategyName = i % 2 == 0 ? "Breakout" : "Momentum",
            Direction = Side.Long,
            Regime = MarketRegime.TrendUp,
            Session = SessionClassifier.Classify(entry),
            IsWeekend = SessionClassifier.IsWeekend(entry),
            HourUtc = entry.Hour,
            DayOfWeek = entry.DayOfWeek,
            VolatilityBucket = VolatilityBucket.Normal,
            EnsembleConfidence = 0.7,
            EstimatedWinProbability = 0.6,
            PlannedRewardToRisk = 1.5,
            EntryTimeUtc = entry,
            ExitTimeUtc = entry.AddMinutes(45),
            EntryPrice = 50000,
            ExitPrice = win ? 50600 : 49500,
            InitialStopPrice = 49500,
            InitialTargetPrice = 50600,
            VolumeInUnits = 1,
            RiskAmount = 500,
            GrossProfit = win ? 600 : -500,
            NetProfit = win ? 580 : -520,
            R = win ? 1.16 : -1.04,
            MaeR = win ? 0.3 : 1.0,
            MfeR = win ? 1.4 : 0.2,
            ExitReason = win ? ExitReason.TakeProfit1 : ExitReason.StopLoss,
            Mode = OperatingMode.Paper,
        };
    }

    [Fact]
    public void StatisticsSurviveARestart()
    {
        var config = new EngineConfig();
        var store = new InMemoryStateStore();

        TradingEngine first = NewEngine(config, store);
        var rng = new Pcg32(4242);
        for (int i = 0; i < 60; i++)
        {
            first.Performance.Record(Trade(i, rng.NextDouble() < 0.55));
        }

        SegmentStats before = first.Performance.Get("all");
        first.SaveStateForTest(RiskFixtures.T0.AddDays(3));

        // Новый экземпляр — как после перезапуска процесса.
        TradingEngine second = NewEngine(config, store);
        second.Restore(RiskFixtures.T0.AddDays(3));

        SegmentStats after = second.Performance.Get("all");

        Assert.Equal(before.TotalTrades, after.TotalTrades);
        Assert.Equal(before.ExpectancyR, after.ExpectancyR, 9);
        Assert.Equal(before.WinRate, after.WinRate, 9);

        _out.WriteLine($"до: {before.TotalTrades} сделок, ожидание {before.ExpectancyR:F4}R");
        _out.WriteLine($"после: {after.TotalTrades} сделок, ожидание {after.ExpectancyR:F4}R");
    }

    [Fact]
    public void PerStrategyAndCalibrationSlicesSurviveToo()
    {
        // Общий срез мало что значит: решения принимаются по срезам стратегии и режима,
        // а размер позиции — по калибровке.
        var config = new EngineConfig();
        var store = new InMemoryStateStore();

        TradingEngine first = NewEngine(config, store);
        for (int i = 0; i < 40; i++) first.Performance.Record(Trade(i, i % 3 != 0));

        int breakoutBefore = first.Performance.Get(PerformanceStore.StrategyKey("Breakout")).TotalTrades;
        first.SaveStateForTest(RiskFixtures.T0.AddDays(2));

        TradingEngine second = NewEngine(config, store);
        second.Restore(RiskFixtures.T0.AddDays(2));

        Assert.Equal(breakoutBefore, second.Performance.Get(PerformanceStore.StrategyKey("Breakout")).TotalTrades);
        Assert.True(breakoutBefore > 0);

        // Калибровка питается заявленной при входе вероятностью против факта.
        Assert.True(second.Calibration.Observations > 0,
            "калибровка обязана восстановиться вместе с историей — иначе оценки снова считаются несмещёнными");
    }

    [Fact]
    public void VirtualTradesStayOutOfTheRealSlicesAfterARestart()
    {
        // Теневые сделки не платят настоящих издержек, и пропустить их в реальные срезы
        // при восстановлении значило бы улучшить статистику перезапуском.
        var config = new EngineConfig();
        var store = new InMemoryStateStore();

        TradingEngine first = NewEngine(config, store);
        for (int i = 0; i < 10; i++)
        {
            TradeRecord t = Trade(i, true);
            first.Performance.Record(new TradeRecord
            {
                TradeId = t.TradeId, SignalId = t.SignalId, SymbolName = t.SymbolName,
                StrategyName = t.StrategyName, Direction = t.Direction, Regime = t.Regime,
                Session = t.Session, HourUtc = t.HourUtc, DayOfWeek = t.DayOfWeek,
                EntryTimeUtc = t.EntryTimeUtc, ExitTimeUtc = t.ExitTimeUtc, R = t.R,
                ExitReason = t.ExitReason, Mode = t.Mode, IsVirtual = true,
            });
        }

        first.SaveStateForTest(RiskFixtures.T0.AddDays(1));

        TradingEngine second = NewEngine(config, store);
        second.Restore(RiskFixtures.T0.AddDays(1));

        Assert.Equal(0, second.Performance.Get("all").TotalTrades);
        Assert.Equal(10, second.Performance.Get(PerformanceStore.ShadowKey("Breakout")).TotalTrades
                       + second.Performance.Get(PerformanceStore.ShadowKey("Momentum")).TotalTrades);
    }

    [Fact]
    public void TheSnapshotStaysSmallEnoughForPlatformStorage()
    {
        // Снимок пишется в LocalStorage платформы. Неограниченная история рано или поздно
        // перестала бы туда помещаться, и система молча потеряла бы ВСЁ состояние, а не
        // только историю.
        var config = new EngineConfig();
        var store = new InMemoryStateStore();

        TradingEngine engine = NewEngine(config, store);

        // Худший случай: и настоящих, и теневых сделок по полному лимиту.
        for (int i = 0; i < config.Adaptation.PersistedTradeHistory * 2; i++)
        {
            engine.Performance.Record(Trade(i, i % 2 == 0));

            TradeRecord shadow = Trade(i, i % 3 == 0);
            engine.Performance.Record(new TradeRecord
            {
                TradeId = shadow.TradeId, SignalId = shadow.SignalId, SymbolName = shadow.SymbolName,
                StrategyName = shadow.StrategyName, Direction = shadow.Direction, Regime = shadow.Regime,
                Session = shadow.Session, HourUtc = shadow.HourUtc, DayOfWeek = shadow.DayOfWeek,
                EntryTimeUtc = shadow.EntryTimeUtc, ExitTimeUtc = shadow.ExitTimeUtc, R = shadow.R,
                MaeR = shadow.MaeR, MfeR = shadow.MfeR, ExitReason = shadow.ExitReason,
                Mode = shadow.Mode, IsVirtual = true,
            });
        }

        engine.SaveStateForTest(RiskFixtures.T0.AddDays(30));
        BotState saved = store.Load();

        Assert.Equal(config.Adaptation.PersistedTradeHistory * 2, saved.Trades.Count);

        int bytes = Encoding.UTF8.GetByteCount(StateSerializer.Serialize(saved));
        _out.WriteLine($"снимок: {saved.Trades.Count} сделок, {bytes / 1024.0:F1} КБ");

        Assert.True(bytes < 256 * 1024,
            $"снимок {bytes / 1024.0:F0} КБ — слишком много для хранилища платформы");
    }

    [Fact]
    public void HistoryCanBeTurnedOffForBacktests()
    {
        var config = new EngineConfig();
        config.Adaptation.PersistedTradeHistory = 0;

        var store = new InMemoryStateStore();
        TradingEngine engine = NewEngine(config, store);
        for (int i = 0; i < 20; i++) engine.Performance.Record(Trade(i, true));

        engine.SaveStateForTest(RiskFixtures.T0.AddDays(1));
        Assert.Empty(store.Load().Trades);
    }
}
