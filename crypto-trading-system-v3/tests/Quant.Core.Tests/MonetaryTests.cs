using System;
using System.Collections.Generic;
using Quant.Core;
using Quant.Core.Config;
using Quant.Core.Ev;
using Quant.Core.Execution;
using Quant.Core.Journal;
using Quant.Core.Portfolio;
using Quant.Core.Primitives;
using Quant.Core.Sizing;
using Quant.Core.State;
using Xunit;

namespace Quant.Core.Tests;

/// <summary>
/// Деньги и цена — разные величины, и весь риск считается в деньгах СЧЁТА.
///
/// Эти тесты существуют потому, что подмена одного другим не проявляется ни в одной
/// проверке: система работает, сделки открываются, числа выглядят разумно — и просто
/// рискует не тем, чем собиралась. На счёте в евро и инструменте, котируемом в долларах,
/// ошибка равна курсу пары, то есть десяткам процентов размера позиции.
/// </summary>
public class MonetaryTests
{
    /// <summary>
    /// Инструмент, котируемый НЕ в валюте счёта. PipValue от платформы уже переведён в
    /// валюту счёта: 0.92 означает, что движение на один пункт стоит 0.92 евро.
    /// </summary>
    private static SymbolSpec ForeignQuote(double accountPerQuote = 0.92) =>
        new SymbolSpec("ETHUSD", pipSize: 0.01, tickSize: 0.01, digits: 2,
            volumeInUnitsMin: 0.001, volumeInUnitsMax: 1000, volumeInUnitsStep: 0.001,
            commissionPerMillionQuote: 35, pipValuePerUnit: 0.01 * accountPerQuote,
            minStopLossDistancePrice: 0, leverage: 2.0, isTradingEnabled: true);

    private static SymbolSpec SameCurrency() =>
        new SymbolSpec("BTCUSD", 0.01, 0.01, 2, 0.001, 1000, 0.001, 35, 0.01, 0, 2.0, true);

    [Fact]
    public void ThePriceToMoneyRateComesFromThePlatformPipValue()
    {
        Assert.Equal(0.92, ForeignQuote().MoneyPerPricePerUnit, 6);
        Assert.Equal(1.00, SameCurrency().MoneyPerPricePerUnit, 6);
        Assert.False(ForeignQuote().MoneyConversionIsAssumed);
    }

    [Fact]
    public void PositionSizeIsComputedInAccountCurrencyNotQuoteCurrency()
    {
        // Счёт в евро, инструмент в долларах. Стоп в 50 долларов — это 46 евро, и объём
        // должен быть меньше, а не больше.
        var sizer = new PositionSizer(new SizingConfig(), new RiskConfig());
        var account = new AccountSnapshot(100000, 100000, 0, 100000, 5000, 50, false, "EUR");

        SizingResult foreign = Size(sizer, ForeignQuote(), account);
        SizingResult same = Size(sizer, SameCurrency(), account);

        Assert.True(foreign.IsTradeable && same.IsTradeable);

        // Движение стоит дороже в валюте счёта? Нет: 0.92 евро за доллар — дешевле,
        // значит на тот же риск приходится БОЛЬШЕ единиц.
        Assert.True(foreign.VolumeInUnits > same.VolumeInUnits,
            $"при курсе 0.92 объём должен быть больше: {foreign.VolumeInUnits:F4} против {same.VolumeInUnits:F4}");

        // И ровно во столько раз, во сколько отличается курс.
        Assert.Equal(same.VolumeInUnits / 0.92, foreign.VolumeInUnits, 2);
    }

    [Fact]
    public void RealisedRiskIsReportedInAccountCurrency()
    {
        var sizer = new PositionSizer(new SizingConfig(), new RiskConfig());
        var account = new AccountSnapshot(100000, 100000, 0, 100000, 5000, 50, false, "EUR");

        SizingResult r = Size(sizer, ForeignQuote(), account);

        // Записанный риск обязан совпадать с тем, что реально потеряет счёт.
        double expected = r.VolumeInUnits * r.StopDistance * 0.92;
        Assert.Equal(expected, r.RiskAmount, 4);
        Assert.Equal(100.0 * expected / account.Equity, r.RiskPercent, 4);
    }

    [Fact]
    public void MarginIsEstimatedInAccountCurrency()
    {
        // Маржа сравнивается со свободной маржой счёта. Считать её в котируемой валюте
        // значит ошибаться на курс ровно в той проверке, которая нужна, чтобы не остаться
        // без денег.
        SymbolSpec foreign = ForeignQuote();
        double margin = foreign.EstimateMargin(units: 10, price: 2500);

        // Номинал 25 000 долларов = 23 000 евро, при плече 2 — 11 500 евро.
        Assert.Equal(11500, margin, 2);
    }

    [Fact]
    public void PortfolioRiskIsMeasuredInAccountCurrency()
    {
        // Иначе портфельный риск считается в котируемой валюте и сравнивается с лимитами,
        // заданными в процентах от счёта: лимиты смещены на курс.
        var position = new OpenPosition
        {
            SymbolName = "ETHUSD", Direction = Side.Long,
            EntryPrice = 2500, CurrentStopPrice = 2450,
            InitialVolumeInUnits = 10, CurrentVolumeInUnits = 10,
            MoneyPerPricePerUnit = 0.92,
        };

        // Ход 50 на 10 единицах = 500 в котируемой, 460 в валюте счёта.
        double percent = PortfolioRiskEngine.CurrentRiskPercent(position, equity: 46000);
        Assert.Equal(1.0, percent, 4);
    }

    private static SizingResult Size(PositionSizer sizer, SymbolSpec spec, AccountSnapshot account) =>
        sizer.Compute(spec, account, entryPrice: 2500, stopPrice: 2450, direction: Side.Long,
            riskStateMultiplier: 1.0, regimeMultiplier: 1.0, ensembleConfidence: 0.8,
            edgeSurplusR: 0.3, strategyWeight: 0.3, atrPercentile: 0.5,
            correlationPenalty: 0.0, dataQualityScore: 1.0, executionQuality: 1.0,
            remainingRiskBudgetPercent: 10.0);
}
