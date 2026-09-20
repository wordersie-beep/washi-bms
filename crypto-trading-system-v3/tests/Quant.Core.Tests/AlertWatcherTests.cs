using System;
using System.Collections.Generic;
using System.Linq;
using Quant.Core;
using Quant.Core.Adaptation;
using Quant.Core.Config;
using Quant.Core.Ev;
using Quant.Core.Execution;
using Quant.Core.Journal;
using Quant.Core.Primitives;
using Quant.Core.State;
using Xunit;

namespace Quant.Core.Tests;

/// <summary>
/// Наблюдение раз в минуту и печать раз в минуту — разные вещи.
///
/// Печатать одну и ту же строку ежеминутно значит заливать журнал 1440 строками в сутки,
/// среди которых теряется единственная важная: та, где что-то изменилось. Человек,
/// попросивший следить каждую минуту, хочет узнать о событии сразу, а не читать
/// подтверждения, что ничего не произошло.
/// </summary>
public class AlertWatcherTests
{
    private static WatchedState State(
        RiskState risk = RiskState.Normal,
        NoTradeReason blocking = NoTradeReason.None,
        bool halted = false,
        int positions = 0,
        long closed = 0,
        double equity = 10000,
        bool marketOpen = true,
        bool ready = true,
        StrategyStatus strategy = StrategyStatus.Active) => new WatchedState
    {
        Risk = risk,
        Blocking = blocking,
        HaltedByReconciliation = halted,
        OpenPositions = positions,
        TradesClosed = closed,
        Equity = equity,
        MarketOpen = new Dictionary<string, bool> { ["BTCUSD"] = marketOpen },
        SymbolReady = new Dictionary<string, bool> { ["BTCUSD"] = ready },
        Strategies = new Dictionary<string, StrategyStatus> { ["Breakout"] = strategy },
    };

    [Fact]
    public void TheFirstCheckRaisesNothing()
    {
        // Сравнивать не с чем. Стартовое состояние печатается при запуске, и дублировать
        // его тревогой значит начинать работу с ложного сигнала.
        var watcher = new AlertWatcher();
        Assert.Empty(watcher.Compare(RiskFixtures.T0, State()));
    }

    [Fact]
    public void AnUnchangedSystemStaysSilent()
    {
        // Это и есть ответ на «следи каждую минуту»: проверка идёт, а журнал молчит.
        var watcher = new AlertWatcher();
        watcher.Compare(RiskFixtures.T0, State());

        for (int minute = 1; minute <= 60; minute++)
        {
            Assert.Empty(watcher.Compare(RiskFixtures.T0.AddMinutes(minute), State()));
        }

        Assert.Equal(61, watcher.Checks);
        Assert.Equal(DateTime.MinValue, watcher.LastChangeUtc);
    }

    [Fact]
    public void AWorseningRiskPostureIsReportedLoudly()
    {
        var watcher = new AlertWatcher();
        watcher.Compare(RiskFixtures.T0, State());

        IReadOnlyList<string> alerts = watcher.Compare(
            RiskFixtures.T0.AddMinutes(1),
            State(risk: RiskState.Halt, blocking: NoTradeReason.DailyLossLimit));

        Assert.Single(alerts);
        Assert.Contains("!!", alerts[0]);
        Assert.Contains("Halt", alerts[0]);
        Assert.Contains("DailyLossLimit", alerts[0]);
    }

    [Fact]
    public void RecoveryIsReportedQuietly()
    {
        // Возврат к норме — это тоже событие, но не тревога. Ставить восклицательные знаки
        // на хорошие новости значит обесценить их на плохих.
        var watcher = new AlertWatcher();
        watcher.Compare(RiskFixtures.T0, State(risk: RiskState.Defensive));

        IReadOnlyList<string> alerts = watcher.Compare(RiskFixtures.T0.AddMinutes(1), State());

        Assert.Single(alerts);
        Assert.DoesNotContain("!!", alerts[0]);
    }

    [Fact]
    public void ReconciliationHaltAndItsClearingAreBothReported()
    {
        var watcher = new AlertWatcher();
        watcher.Compare(RiskFixtures.T0, State());

        Assert.Contains("ОСТАНОВКА ПО СВЕРКЕ",
            string.Join(" ", watcher.Compare(RiskFixtures.T0.AddMinutes(1), State(halted: true))));

        Assert.Contains("сверка сошлась",
            string.Join(" ", watcher.Compare(RiskFixtures.T0.AddMinutes(2), State(halted: false))));
    }

    [Fact]
    public void AClosedTradeIsReportedWithTheEquityChange()
    {
        var watcher = new AlertWatcher();
        watcher.Compare(RiskFixtures.T0, State(positions: 1, closed: 4, equity: 10000));

        IReadOnlyList<string> alerts = watcher.Compare(
            RiskFixtures.T0.AddMinutes(1), State(positions: 0, closed: 5, equity: 10120));

        string joined = string.Join(" | ", alerts);
        Assert.Contains("позиций 1 -> 0", joined);
        Assert.Contains("закрыто сделок: 1", joined);
        Assert.Contains("+120", joined);
    }

    [Fact]
    public void MarketOpeningAndWarmUpCompletionAreReported()
    {
        // Две самые частые причины, по которым исправный бот ничего не делает. Момент,
        // когда причина исчезает, — именно то, чего человек ждёт.
        var watcher = new AlertWatcher();
        watcher.Compare(RiskFixtures.T0, State(marketOpen: false, ready: false));

        string joined = string.Join(" | ",
            watcher.Compare(RiskFixtures.T0.AddMinutes(1), State(marketOpen: true, ready: true)));

        Assert.Contains("рынок ОТКРЫЛСЯ", joined);
        Assert.Contains("прогрев завершён", joined);
    }

    [Fact]
    public void AStrategyBeingDisabledIsReportedLoudly()
    {
        var watcher = new AlertWatcher();
        watcher.Compare(RiskFixtures.T0, State());

        IReadOnlyList<string> alerts = watcher.Compare(
            RiskFixtures.T0.AddMinutes(1), State(strategy: StrategyStatus.Disabled));

        Assert.Single(alerts);
        Assert.Contains("!!", alerts[0]);
        Assert.Contains("Breakout", alerts[0]);
    }

    [Fact]
    public void TheEngineReportsItsOwnChanges()
    {
        // Сквозная проверка: наблюдатель подключён к движку, а не просто существует.
        var config = new EngineConfig();
        var broker = new SimulatedBroker(config.Execution, new CostModel(config.Execution),
            new AccountSnapshot(100000, 100000, 0, 100000, 5000, 50, false, "USD"), false);

        var engine = new TradingEngine(config, broker, new InMemoryStateStore(), NullJournalSink.Instance, "test");
        engine.AddSymbol("XAUEUR", new SymbolSpec("XAUEUR", 0.01, 0.01, 2, 0.001, 1000, 0.001, 35, 0.01, 0, 2.0, true),
            MarketSchedule.FromSessions(new[]
            {
                new TradingSessionWindow(DayOfWeek.Sunday, TimeSpan.FromHours(22), DayOfWeek.Friday, TimeSpan.FromHours(22)),
            }));

        // Суббота: рынок закрыт.
        var saturday = new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);
        Assert.Empty(engine.CollectAlerts(saturday));
        Assert.Empty(engine.CollectAlerts(saturday.AddMinutes(1)));

        // Понедельник: открыт. Об этом должно быть сказано.
        var monday = new DateTime(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);
        Assert.Contains("рынок ОТКРЫЛСЯ", string.Join(" ", engine.CollectAlerts(monday)));
    }
}
