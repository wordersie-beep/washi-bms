using System;
using System.Collections.Generic;
using Quant.Core.Config;
using Quant.Core.Ev;
using Quant.Core.Primitives;
using Xunit;
using Xunit.Abstractions;

namespace Quant.Core.Tests;

/// <summary>
/// Может ли сделка окупить собственные издержки — вопрос арифметики, а не стратегии.
///
/// Эти тесты закрепляют числами то, что иначе осталось бы советом. Рекомендация «бери
/// M5, а не M1» ничего не стоит, пока её нельзя проверить; здесь она проверяется.
/// </summary>
public class TradeabilityTests
{
    private readonly ITestOutputHelper _out;
    public TradeabilityTests(ITestOutputHelper output) { _out = output; }

    private static SymbolSpec Forex() =>
        new SymbolSpec("EURUSD", 0.0001, 0.00001, 5, 1000, 1e9, 1000, 0, 0.0001, 0, 30, true);

    private static SymbolSpec Crypto() =>
        new SymbolSpec("BTCUSD", 1.0, 0.01, 2, 0.001, 1000, 0.001, 0, 1.0, 0, 5, true);

    [Fact]
    public void OnTheMinuteChartRetailSpreadsMakeTheTradeImpossibleBeforeAnyStrategyRuns()
    {
        // Типичные величины розничного счёта: EURUSD, ATR(M1) около полутора пипсов,
        // спред один пипс. Числа не подогнаны — это обычный день обычного брокера.
        var config = new EngineConfig();

        TradeabilityReport.Line m1 = TradeabilityReport.Evaluate(
            Tf.M1, atr: 0.00015, spread: 0.00010, price: 1.09, Forex(), config);

        _out.WriteLine($"EURUSD M1: спред/ATR {m1.SpreadToAtr:P0}, издержки {m1.CostR:P0} риска, " +
                       $"нужно EV {m1.RequiredEdgeR:F2}R — {m1.Outcome}");

        Assert.False(m1.IsTradeable);

        // И дело не в осторожности настроек: издержки съедают больше половины риска.
        Assert.True(m1.CostR > 0.5,
            $"издержки {m1.CostR:P0} — если бы они были малы, вывод был бы о настройках, а не об арифметике");
    }

    [Fact]
    public void TheSameInstrumentBecomesTradeableOnceTheBarIsLongEnoughToPayForTheSpread()
    {
        // Тот же спред, та же стратегия, тот же брокер. Меняется только длина бара —
        // и вместе с ней волатильность, на которую делятся неизменные издержки.
        var config = new EngineConfig();

        var lines = new List<TradeabilityReport.Line>
        {
            TradeabilityReport.Evaluate(Tf.M1, 0.00015, 0.00010, 1.09, Forex(), config),
            TradeabilityReport.Evaluate(Tf.M5, 0.00035, 0.00010, 1.09, Forex(), config),
            TradeabilityReport.Evaluate(Tf.M15, 0.00065, 0.00010, 1.09, Forex(), config),
            TradeabilityReport.Evaluate(Tf.H1, 0.00150, 0.00010, 1.09, Forex(), config),
        };

        _out.WriteLine(TradeabilityReport.Render("EURUSD", lines,
            TradeabilityReport.AtrNeededFor(0.00010, config)));

        // Издержки обязаны убывать с ростом таймфрейма — иначе расчёт считает не то.
        for (int i = 1; i < lines.Count; i++)
        {
            Assert.True(lines[i].CostR < lines[i - 1].CostR,
                $"{lines[i].Timeframe} дороже {lines[i - 1].Timeframe} — расчёт неверен");
        }

        Assert.False(lines[0].IsTradeable);
        Assert.True(lines[lines.Count - 1].IsTradeable);
    }

    [Fact]
    public void CryptoBuysItsTradeabilityWithVolatilityNotWithACheaperSpread()
    {
        // Спред на BTCUSD в абсолютных числах огромен — двадцать долларов против одного
        // пипса. Торгуемым инструмент делает не дешевизна, а то, что ATR ещё больше.
        var config = new EngineConfig();

        TradeabilityReport.Line btc = TradeabilityReport.Evaluate(
            Tf.M5, atr: 140.0, spread: 20.0, price: 100_000, Crypto(), config);

        _out.WriteLine($"BTCUSD M5: спред/ATR {btc.SpreadToAtr:P0}, издержки {btc.CostR:P0} риска, " +
                       $"нужно EV {btc.RequiredEdgeR:F2}R — {btc.Outcome}");

        Assert.True(btc.IsTradeable);

        // А на минутном графике исчезает и это преимущество.
        TradeabilityReport.Line btcM1 = TradeabilityReport.Evaluate(
            Tf.M1, atr: 45.0, spread: 20.0, price: 100_000, Crypto(), config);

        _out.WriteLine($"BTCUSD M1: спред/ATR {btcM1.SpreadToAtr:P0}, издержки {btcM1.CostR:P0} риска — {btcM1.Outcome}");
        Assert.False(btcM1.IsTradeable);
    }

    [Fact]
    public void AWiderStopLowersTheCostShareBecauseTheSameCostBuysMoreRisk()
    {
        // Рычаг, который есть у настроек: издержки делятся на стоп, а стоп задаётся
        // множителем ATR. Это не приближает прибыль — это удешевляет вход.
        var narrow = new EngineConfig();
        narrow.Exit.AtrStopMultiple = 1.6;

        var wide = new EngineConfig();
        wide.Exit.AtrStopMultiple = 2.6;

        TradeabilityReport.Line a = TradeabilityReport.Evaluate(Tf.M5, 100.0, 22.0, 100_000, Crypto(), narrow);
        TradeabilityReport.Line b = TradeabilityReport.Evaluate(Tf.M5, 100.0, 22.0, 100_000, Crypto(), wide);

        Assert.True(b.CostR < a.CostR);

        // Но фильтр спреда шире стопа не становится: он сравнивает спред с ATR, а не со
        // стопом, и потому не обходится подкруткой множителя.
        Assert.Equal(a.SpreadToAtr, b.SpreadToAtr, 9);
    }

    [Fact]
    public void TheRequiredAtrIsTheThresholdBothGatesAgreeOn()
    {
        var config = new EngineConfig();
        double spread = 15.0;
        double needed = TradeabilityReport.AtrNeededFor(spread, config);

        // Ровно на границе — проходит.
        TradeabilityReport.Line at = TradeabilityReport.Evaluate(Tf.M5, needed, spread, 100_000, Crypto(), config);
        Assert.True(at.IsTradeable, $"на границе ATR {needed:F4} вердикт {at.Outcome}");

        // Чуть ниже — нет.
        TradeabilityReport.Line below = TradeabilityReport.Evaluate(Tf.M5, needed * 0.9, spread, 100_000, Crypto(), config);
        Assert.False(below.IsTradeable);
    }

    [Fact]
    public void AnUnmeasurableInstrumentIsRefusedRatherThanAssumedCheap()
    {
        var config = new EngineConfig();
        TradeabilityReport.Line blind = TradeabilityReport.Evaluate(Tf.M5, atr: 0, spread: 20, price: 100_000, Crypto(), config);
        Assert.False(blind.IsTradeable);
    }

    [Theory]
    [InlineData(110.0)]
    [InlineData(160.0)]
    public void TheCryptoPresetPassesItsOwnEconomicsAtTypicalVolatility(double atr)
    {
        // Пресет — это обещание пользователю: «на этом наборе бот будет торговать».
        // Непроверенное обещание ничем не отличается от совета, а советов в этой задаче
        // уже достаточно. Числа спреда и ATR — типичные для BTCUSD у розничного брокера.
        var config = new EngineConfig();
        Presets.CryptoSmallTimeframe(config, Tf.M5);

        TradeabilityReport.Line line = TradeabilityReport.Evaluate(
            Tf.M5, atr, spread: 20.0, price: 100_000, Crypto(), config);

        _out.WriteLine($"ATR {atr}: спред/ATR {line.SpreadToAtr:P1}, издержки {line.CostR:P1}, " +
                       $"нужно EV {line.RequiredEdgeR:F2}R — {line.Outcome}");

        Assert.True(line.IsTradeable,
            $"пресет не проходит собственную экономику при ATR {atr}: {line.Outcome}, " +
            $"спред {line.SpreadToAtr:P1} от ATR, издержки {line.CostR:P1} риска");
    }

    [Fact]
    public void InAQuietHourThePresetRefusesAndThatIsTheCorrectAnswer()
    {
        // Этот тест написан после того, как предыдущий упал на тихом часе, и он
        // закрепляет ОТКАЗ, а не обходит его.
        //
        // Соблазн был расширить порог спреда так, чтобы «проходило всегда». Это и значило
        // бы торговать там, где издержки съедают почти пятую часть риска, а требуемое
        // матожидание уходит за 0.35R — то есть покупать сделки, которые по арифметике
        // не окупаются. Бот, стоящий в стороне в тихие часы, не сломан; бот, торгующий в
        // них, сломан молча и за деньги.
        var config = new EngineConfig();
        Presets.CryptoSmallTimeframe(config, Tf.M5);

        TradeabilityReport.Line quiet = TradeabilityReport.Evaluate(
            Tf.M5, atr: 80.0, spread: 20.0, price: 100_000, Crypto(), config);

        _out.WriteLine($"тихий час, ATR 80: спред/ATR {quiet.SpreadToAtr:P1}, издержки {quiet.CostR:P1}, " +
                       $"нужно EV {quiet.RequiredEdgeR:F2}R — {quiet.Outcome}");

        Assert.False(quiet.IsTradeable);
        Assert.True(quiet.RequiredEdgeR > TradeabilityReport.UnrealisticEdgeR,
            "если бы требуемое преимущество было реалистичным, отказ был бы перестраховкой");
    }

    [Fact]
    public void ThePresetIsCheaperThanTheDefaultsItReplaces()
    {
        // Смысл пресета — не в других числах, а в более дешёвом входе. Если издержки не
        // упали, менять было нечего.
        var defaults = new EngineConfig();
        var preset = new EngineConfig();
        Presets.CryptoSmallTimeframe(preset, Tf.M5);

        TradeabilityReport.Line a = TradeabilityReport.Evaluate(Tf.M5, 110, 20, 100_000, Crypto(), defaults);
        TradeabilityReport.Line b = TradeabilityReport.Evaluate(Tf.M5, 110, 20, 100_000, Crypto(), preset);

        _out.WriteLine($"по умолчанию: издержки {a.CostR:P1}, нужно {a.RequiredEdgeR:F2}R — {a.Outcome}");
        _out.WriteLine($"пресет:       издержки {b.CostR:P1}, нужно {b.RequiredEdgeR:F2}R — {b.Outcome}");

        Assert.True(b.CostR < a.CostR);
        Assert.True(b.RequiredEdgeR < a.RequiredEdgeR);
    }

    [Fact]
    public void ThePresetIsInternallyConsistent()
    {
        // Пресет двигает поля из разных секций сразу — ровно тот случай, в котором эта
        // система уже трижды ловила недостижимые ворота.
        foreach (Tf tf in new[] { Tf.M5, Tf.M15 })
        {
            var config = new EngineConfig { Mode = OperatingMode.Paper };
            Presets.CryptoSmallTimeframe(config, tf);

            Assert.True(config.Validate().Count == 0,
                $"{tf}: " + string.Join(" | ", config.Validate()));
        }
    }
}
