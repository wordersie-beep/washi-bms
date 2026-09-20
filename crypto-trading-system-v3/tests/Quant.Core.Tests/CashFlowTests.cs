using System;
using Quant.Core.Config;
using Quant.Core.Primitives;
using Quant.Core.Risk;
using Quant.Core.Stats;
using Xunit;

namespace Quant.Core.Tests;

/// <summary>
/// Внесённые деньги — не заработанные деньги.
///
/// Не различать их значит позволить пополнению счёта снять защитную постуру, поднять
/// исторический пик до уровня, которого торговля не достигала, и показать
/// тысячепроцентную дневную прибыль.
/// </summary>
public class CashFlowTests
{
    [Fact]
    public void TheFirstObservationIsNotADeposit()
    {
        // Иначе сам факт запуска объявлялся бы движением денег.
        var detector = new CashFlowDetector();
        Assert.Equal(0, detector.Detect(balance: 10000, equity: 10000, realisedTotal: 0));
        Assert.Equal(0, detector.Count);
    }

    [Fact]
    public void ADepositIsDetected()
    {
        var detector = new CashFlowDetector();
        detector.Detect(10000, 10000, 0);

        double delta = detector.Detect(balance: 110000, equity: 110000, realisedTotal: 0);

        Assert.Equal(100000, delta, 6);
        Assert.Equal(1, detector.Count);
    }

    [Fact]
    public void AWithdrawalIsDetected()
    {
        var detector = new CashFlowDetector();
        detector.Detect(10000, 10000, 0);

        Assert.Equal(-4000, detector.Detect(6000, 6000, 0), 6);
    }

    [Fact]
    public void ProfitFromTradingIsNotMistakenForADeposit()
    {
        // Баланс вырос ровно на то, что объясняют закрытые сделки. Это результат, а не
        // движение денег, и сдвигать отсчёты здесь значило бы стереть заработанное.
        var detector = new CashFlowDetector();
        detector.Detect(10000, 10000, 0);

        Assert.Equal(0, detector.Detect(balance: 10850, equity: 10850, realisedTotal: 850));
    }

    [Fact]
    public void SmallUnexplainedDriftIsTreatedAsACostNotACashFlow()
    {
        // Своп и финансирование двигают баланс, не будучи сделками. Это НАСТОЯЩИЕ издержки,
        // и вычитать их из отчётности значило бы прятать расходы.
        var detector = new CashFlowDetector();
        detector.Detect(10000, 10000, 0);

        Assert.Equal(0, detector.Detect(balance: 9997.50, equity: 9997.50, realisedTotal: 0));
        Assert.Equal(0, detector.Count);
    }

    [Fact]
    public void ADepositDoesNotRaiseTheDrawdownPeak()
    {
        // Иначе просадка навсегда меряется от уровня, которого торговля не достигала, и
        // лимит срабатывает от первого же отката к капиталу, который был до пополнения.
        var tracker = new DrawdownTracker();
        tracker.Observe(RiskFixtures.T0, 10000);
        tracker.Observe(RiskFixtures.T0.AddHours(1), 10500);

        Assert.Equal(10500, tracker.AllTimePeak, 2);

        // Пополнение на 100 000: пик сдвигается, просадка остаётся нулевой.
        tracker.NoteCashFlow(100000);
        tracker.Observe(RiskFixtures.T0.AddHours(2), 110500);

        Assert.Equal(110500, tracker.AllTimePeak, 2);
        Assert.Equal(0, tracker.AllTimeDrawdownPercent, 4);

        // А настоящая потеря 500 после пополнения — это по-прежнему потеря 500.
        tracker.Observe(RiskFixtures.T0.AddHours(3), 110000);
        Assert.Equal(100.0 * 500 / 110500, tracker.AllTimeDrawdownPercent, 4);
    }

    [Fact]
    public void ADepositDoesNotEraseTheDayLossAlreadyIncurred()
    {
        // Обнулить дневной убыток внесением денег значило бы дать способ снимать дневной
        // лимит переводом со сберегательного счёта.
        var config = new EngineConfig();
        var risk = new RiskEngine(config.Risk, config.Sizing, new DrawdownTracker(),
            new ExecutionQualityTracker(config.Execution), new PerformanceStore(config.Adaptation),
            new RiskOfRuinEstimator(seed: 3));

        risk.Restore(RiskState.Normal, 0, 0, null,
            RiskFixtures.T0, dayStartEquity: 10000,
            RiskFixtures.T0, weekStartEquity: 10000, RiskFixtures.T0);

        Assert.Equal(10000, risk.DayStartEquity, 2);

        risk.NoteCashFlow(100000);

        // Точка отсчёта поднялась ровно на внесённую сумму: капитал 110 000 после
        // пополнения — это ноль прибыли за день, а не плюс тысяча процентов.
        Assert.Equal(110000, risk.DayStartEquity, 2);
        Assert.Equal(110000, risk.WeekStartEquity, 2);
    }
}
