using System;
using System.Collections.Generic;
using Quant.Core.Config;
using Quant.Core.Data;
using Quant.Core.Primitives;
using Xunit;

namespace Quant.Core.Tests;

/// <summary>
/// Расписание торгов существует ради одного вопроса: почему исправный бот ничего не
/// делает. Закрытый рынок и сбой подачи данных выглядят одинаково — пустой журнал, — и
/// только расписание их различает.
/// </summary>
public class MarketScheduleTests
{
    /// <summary>Круглосуточная неделя: одна сессия, замкнутая сама на себя.</summary>
    private static MarketSchedule Crypto() => MarketSchedule.FromSessions(new[]
    {
        new TradingSessionWindow(DayOfWeek.Monday, TimeSpan.Zero, DayOfWeek.Monday, TimeSpan.Zero),
    });

    /// <summary>Типичный CFD: с вечера воскресенья до вечера пятницы.</summary>
    private static MarketSchedule Cfd() => MarketSchedule.FromSessions(new[]
    {
        new TradingSessionWindow(DayOfWeek.Sunday, TimeSpan.FromHours(22), DayOfWeek.Friday, TimeSpan.FromHours(22)),
    });

    [Fact]
    public void ACryptoSymbolIsRecognisedAsContinuous()
    {
        MarketSchedule schedule = Crypto();

        Assert.True(schedule.IsKnown);
        Assert.True(schedule.IsContinuous);
        Assert.Equal(168.0, schedule.OpenHoursPerWeek, 1);
    }

    [Fact]
    public void AWeekdayOnlySymbolIsNotContinuous()
    {
        MarketSchedule schedule = Cfd();

        Assert.True(schedule.IsKnown);
        Assert.False(schedule.IsContinuous);
        Assert.Equal(120.0, schedule.OpenHoursPerWeek, 1);
    }

    [Fact]
    public void ASmallDailyMaintenanceBreakStillCountsAsContinuous()
    {
        // У многих брокеров крипта закрывается на несколько минут в сутки на расчёт
        // свопов. Считать такой инструмент непригодным для 24/7 было бы неверно.
        var sessions = new List<TradingSessionWindow>();
        for (int d = 0; d < 7; d++)
        {
            var day = (DayOfWeek)(((d + 1) % 7));
            sessions.Add(new TradingSessionWindow(
                day, TimeSpan.FromMinutes(5), day, TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59)));
        }

        MarketSchedule schedule = MarketSchedule.FromSessions(sessions);

        Assert.True(schedule.IsContinuous,
            $"Перерыв в несколько минут в сутки — это 24/7 в любом практическом смысле ({schedule.OpenHoursPerWeek:F1} ч/нед).");
    }

    [Fact]
    public void TheWeekendIsReportedAsClosed()
    {
        MarketSchedule schedule = Cfd();

        // Суббота, 19 сентября 2026 года, полдень UTC.
        var saturday = new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(schedule.IsOpenAt(saturday));

        int untilOpen = schedule.MinutesUntilOpen(saturday);
        Assert.True(untilOpen > 0);

        // Открытие — в воскресенье 22:00, то есть примерно через 34 часа.
        Assert.Equal(34 * 60, untilOpen);
    }

    [Fact]
    public void AnOpenMarketReportsZeroMinutesUntilOpen()
    {
        var wednesday = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(Cfd().IsOpenAt(wednesday));
        Assert.Equal(0, Cfd().MinutesUntilOpen(wednesday));
        Assert.True(Crypto().IsOpenAt(wednesday));
    }

    [Fact]
    public void AGapAcrossTheWeekendIsScheduledRatherThanMissingData()
    {
        // Иначе инструмент с перерывом отвергается после КАЖДОГО открытия сессии: разрыв
        // в десятки интервалов читается как сбой подачи данных.
        MarketSchedule schedule = Cfd();

        var fridayClose = new DateTime(2026, 9, 18, 21, 55, 0, DateTimeKind.Utc);
        var sundayOpen = new DateTime(2026, 9, 20, 22, 5, 0, DateTimeKind.Utc);

        // Перерыв — 48 часов; открытыми остаются только пять минут до пятничного закрытия
        // и пять после воскресного открытия, то есть границы самого разрыва.
        Assert.Equal(48 * 60, schedule.ClosedMinutesBetween(fridayClose, sundayOpen));
        Assert.True(schedule.ExplainsGap(fridayClose, sundayOpen, toleranceMinutes: 10));
    }

    [Fact]
    public void AGapDuringOpenHoursIsNotExcused()
    {
        // Пропущенные бары в среду — это настоящая потеря данных, и расписание не должно
        // служить ей оправданием.
        MarketSchedule schedule = Cfd();

        var wednesday = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(0, schedule.ClosedMinutesBetween(wednesday, wednesday.AddHours(3)));
        Assert.False(schedule.ExplainsGap(wednesday, wednesday.AddHours(3), toleranceMinutes: 10));
    }

    [Fact]
    public void AnUnknownScheduleNeverBlocksAndNeverExcuses()
    {
        // Отсутствие данных у платформы не должно ни останавливать торговлю, ни оправдывать
        // разрывы: неизвестность — это не факт ни в одну сторону.
        MarketSchedule schedule = MarketSchedule.Unknown;

        Assert.False(schedule.IsKnown);
        Assert.True(schedule.IsContinuous);
        Assert.True(schedule.IsOpenAt(new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc)));
        Assert.False(schedule.ExplainsGap(
            new DateTime(2026, 9, 18, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc),
            toleranceMinutes: 10));
    }

    [Fact]
    public void SessionsAreOrderedFromMondayNotFromSunday()
    {
        // DayOfWeek.Sunday равен нулю. Без сдвига воскресная сессия оказалась бы раньше
        // понедельничной, и подсчёт открытых минут развалился бы.
        Assert.Equal(0, MarketSchedule.MinuteOfWeek(DayOfWeek.Monday, TimeSpan.Zero));
        Assert.Equal(6 * 24 * 60, MarketSchedule.MinuteOfWeek(DayOfWeek.Sunday, TimeSpan.Zero));
    }

    [Fact]
    public void AClosedMarketIsReportedAsClosedNotAsBadData()
    {
        // «Закрытый рынок» и «данные испорчены» — разные вещи. Назвать первое вторым значит
        // спрятать самую частую причину бездействия за формулировкой, которая звучит как
        // поломка, и отправить человека искать несуществующий сбой.
        var config = new EngineConfig();
        var monitor = new DataQualityMonitor(config.Data);
        var harness = new PipelineHarness(config);
        harness.Feed(new MarketSimulator(seed: 701).Generate(600, 0.0002, 0.002));

        var saturday = new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);

        DataQualityReport report = monitor.Evaluate(
            saturday, harness.Spec, new Quote(saturday, 49999, 50001),
            harness.Data.Signal, harness.Data.Ticks, harness.Data.Spread, Cfd());

        Assert.False(report.IsAcceptable);
        Assert.Equal(NoTradeReason.MarketClosed, report.Reason);
        Assert.Contains("рынок закрыт", report.IssueSummary);
    }

    [Fact]
    public void TheDashboardNamesWhyASymbolIsIdle()
    {
        // «bars=0» само по себе не отвечает на вопрос, который человек задаёт, глядя на
        // дашборд: это прогрев, закрытый рынок или неисправность. Три состояния выглядят
        // одинаково, а требуют совершенно разных действий.
        var config = new EngineConfig();
        var broker = new Quant.Core.Execution.SimulatedBroker(
            config.Execution, new Quant.Core.Ev.CostModel(config.Execution),
            new AccountSnapshot(100000, 100000, 0, 100000, 2000, 50, false, "USD"), false);

        var engine = new TradingEngine(config, broker, new Quant.Core.State.InMemoryStateStore(),
            Quant.Core.Journal.NullJournalSink.Instance, "test");

        var spec = new SymbolSpec("BTCUSD", 0.01, 0.01, 2, 0.001, 1000, 0.001, 35, 0.01, 0, 2.0, true);
        engine.AddSymbol("BTCUSD", spec, MarketSchedule.Continuous);

        string dashboard = engine.RenderDashboard(new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc));

        Assert.Contains("ПРОГРЕВ", dashboard);
        Assert.Contains("нужно ещё", dashboard);
    }

    [Fact]
    public void TheDashboardNamesAClosedMarketRatherThanWarmUp()
    {
        var config = new EngineConfig();
        var broker = new Quant.Core.Execution.SimulatedBroker(
            config.Execution, new Quant.Core.Ev.CostModel(config.Execution),
            new AccountSnapshot(100000, 100000, 0, 100000, 2000, 50, false, "USD"), false);

        var engine = new TradingEngine(config, broker, new Quant.Core.State.InMemoryStateStore(),
            Quant.Core.Journal.NullJournalSink.Instance, "test");

        var spec = new SymbolSpec("XAUEUR", 0.01, 0.01, 2, 0.001, 1000, 0.001, 35, 0.01, 0, 2.0, true);
        engine.AddSymbol("XAUEUR", spec, Cfd());

        // Суббота: рынок закрыт, и это, а не прогрев, — причина бездействия.
        string dashboard = engine.RenderDashboard(new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc));

        Assert.Contains("РЫНОК ЗАКРЫТ", dashboard);
        Assert.DoesNotContain("ПРОГРЕВ", dashboard);
    }

    [Fact]
    public void TheDescriptionNamesTheClosure()
    {
        string description = Cfd().Describe();

        Assert.Contains("ч/нед", description);
        Assert.Contains("перерыв", description);
        Assert.Contains("пт", description);
    }
}
