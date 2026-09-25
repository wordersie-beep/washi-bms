using Quant.Core.Config;
using Quant.Core.Primitives;
using Quant.Core.Sizing;
using Xunit;
using Xunit.Abstractions;

namespace Quant.Core.Tests;

public class AccountViabilityTests
{
    private readonly ITestOutputHelper _out;
    public AccountViabilityTests(ITestOutputHelper output) { _out = output; }

    // BTCUSD: минимум 0.01 BTC, пересчёт один к одному (счёт в USD).
    private static SymbolSpec Btc() => new SymbolSpec("BTCUSD", 1.0, 0.01, 2, 0.01, 100, 0.01, 0, 1.0, 0, 5, true);

    [Fact]
    public void FiftyOnTheAccountCannotCarryTheSmallestBitcoinPosition()
    {
        // Ровно ситуация пользователя: на демо-счёте осталось 50.
        var config = new EngineConfig();
        Presets.CryptoSmallTimeframe(config, Tf.M5);

        AccountViability.Result r = AccountViability.Evaluate("BTCUSD", Btc(), atr: 110, equity: 50, config);
        _out.WriteLine(AccountViability.Describe(r, "USD"));

        Assert.Equal(AccountViability.Verdict.AccountTooSmall, r.Outcome);
        Assert.True(r.MinimumRiskPercent > config.Risk.HardMaxRiskPerTradePercent,
            "минимальная позиция на 50 обязана превышать даже жёсткий предел риска");
    }

    [Fact]
    public void TheReportedThresholdIsExactlyWhereTradingBecomesPossible()
    {
        var config = new EngineConfig();
        Presets.CryptoSmallTimeframe(config, Tf.M5);

        AccountViability.Result probe = AccountViability.Evaluate("BTCUSD", Btc(), 110, 1_000_000, config);

        AccountViability.Result at = AccountViability.Evaluate("BTCUSD", Btc(), 110, probe.EquityForFirstTrades * 1.001, config);
        AccountViability.Result below = AccountViability.Evaluate("BTCUSD", Btc(), 110, probe.EquityForFirstTrades * 0.99, config);

        _out.WriteLine($"для первых сделок нужно {probe.EquityForFirstTrades:F0}, для полного риска {probe.EquityForFullRisk:F0}");

        Assert.Equal(AccountViability.Verdict.Viable, at.Outcome);
        Assert.NotEqual(AccountViability.Verdict.Viable, below.Outcome);
    }

    [Fact]
    public void BetweenTheTwoThresholdsTheFirstTradeOpensAtTheBrokerMinimum()
    {
        // Раньше здесь был тупик: минимальная позиция укладывалась в риск на сделку, но
        // осторожный начальный размер выходил меньше минимума брокера, и сайзер отвергал
        // каждую первую сделку. Холодный старт заканчивается сделками — значит, не
        // заканчивался никогда.
        var config = new EngineConfig();
        Presets.CryptoSmallTimeframe(config, Tf.M5);
        var sizer = new PositionSizer(config.Sizing, config.Risk);

        double equity = 1500;
        AccountViability.Result v = AccountViability.Evaluate("BTCUSD", Btc(), 110, equity, config);
        Assert.Equal(AccountViability.Verdict.ViableOnlyAtFullRisk, v.Outcome);

        var account = new AccountSnapshot(equity, equity, 0, equity, 2000, 50, false, "USD");
        SizingResult first = sizer.Compute(Btc(), account, 100_000, 100_000 - (110 * config.Exit.AtrStopMultiple),
            Side.Long, 1.0, 0.64, 0.65, 0.10, 0.30, 0.5, 0.0, 1.0, 0.85, 1.5, isColdStart: true, trust: 0.3);

        _out.WriteLine($"счёт {equity}: {(first.IsTradeable ? $"первая сделка открыта, риск {first.RiskPercent:F3}%" : first.Detail)}");

        Assert.True(first.IsTradeable, "первая сделка снова невозможна: " + first.Detail);
        Assert.Equal(Btc().VolumeInUnitsMin, first.VolumeInUnits, 9);
        Assert.True(first.RiskPercent <= config.Sizing.RiskPerTradePercent + 1e-9,
            $"округление до минимума нарушило риск на сделку: {first.RiskPercent:F3}%");
    }

    [Fact]
    public void RoundingUpRespectsThePostureCeiling()
    {
        // В Defensive потолок ниже: минимальная позиция, не влезающая в него, не
        // открывается, даже если влезла бы в полный риск.
        var config = new EngineConfig();
        Presets.CryptoSmallTimeframe(config, Tf.M5);
        var sizer = new PositionSizer(config.Sizing, config.Risk);

        double equity = 1500;
        var account = new AccountSnapshot(equity, equity, 0, equity, 2000, 50, false, "USD");
        SizingResult defensive = sizer.Compute(Btc(), account, 100_000, 100_000 - (110 * config.Exit.AtrStopMultiple),
            Side.Long, 0.35, 0.64, 0.65, 0.10, 0.30, 0.5, 0.0, 1.0, 0.85, 1.5, isColdStart: true, trust: 0.3);

        _out.WriteLine($"Defensive на {equity}: {defensive.Detail}");
        Assert.False(defensive.IsTradeable);
    }

    [Fact]
    public void TheVerdictAgreesWithTheSizerItself()
    {
        // Отчёт, расходящийся с тем, что сделает сайзер, хуже отсутствия отчёта.
        var config = new EngineConfig();
        Presets.CryptoSmallTimeframe(config, Tf.M5);
        var sizer = new PositionSizer(config.Sizing, config.Risk);

        foreach (double equity in new[] { 50.0, 500.0, 5_000.0, 50_000.0 })
        {
            AccountViability.Result v = AccountViability.Evaluate("BTCUSD", Btc(), 110, equity, config);
            var account = new AccountSnapshot(equity, equity, 0, equity, 2000, 50, false, "USD");

            // Полный риск, без понижающих множителей: лучшее, на что способен сайзер.
            SizingResult best = sizer.Compute(Btc(), account, 100_000, 100_000 - (110 * config.Exit.AtrStopMultiple),
                Side.Long, 1.0, 1.0, 1.0, 1.0, 1.0, 0.5, 0.0, 1.0, 1.0, 10.0, isColdStart: false, trust: 1.0);

            _out.WriteLine($"капитал {equity}: отчёт {v.Outcome}, сайзер {(best.IsTradeable ? "торгует" : best.Detail)}");

            bool reportSaysPossible = v.Outcome != AccountViability.Verdict.AccountTooSmall;
            Assert.Equal(reportSaysPossible, best.IsTradeable);
        }
    }
}
