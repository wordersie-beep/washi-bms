using System;
using Quant.Core.Config;
using Quant.Core.Primitives;
using Quant.Core.Risk;
using Quant.Core.Sizing;
using Xunit;
using Xunit.Abstractions;

namespace Quant.Core.Tests;

/// <summary>
/// Политика уменьшает размер, но не имеет права превращать «меньше» в «никогда».
///
/// Зонд на счёте в миллион евро показал:
///
///     пограничный, холодный старт:  ОТКАЗ  0.0115% &lt; 0.05%
///     типичный,    холодный старт:  ОТКАЗ  0.0283% &lt; 0.05%
///     сильный,     холодный старт:  ОТКАЗ  0.0442% &lt; 0.05%
///     типичный,    Defensive:       ОТКАЗ  0.0283% &lt; 0.05%
///
/// Холодный старт заканчивается после двадцати сделок. Сделок не могло быть. Круг, который
/// считался разорванным в оценке преимущества, просто переехал на слой ниже.
/// </summary>
public class SizingPolicyFloorTests
{
    private readonly ITestOutputHelper _out;
    public SizingPolicyFloorTests(ITestOutputHelper output) { _out = output; }

    private static SizingResult Size(double confidence, double surplus, double weight, bool cold, double posture)
    {
        var config = new EngineConfig();
        Presets.CryptoSmallTimeframe(config, Tf.M5);
        var sizer = new PositionSizer(config.Sizing, config.Risk);

        // Большой счёт нарочно: размер счёта здесь не должен быть причиной отказа.
        var spec = new SymbolSpec("BTCUSD", 1.0, 0.01, 2, 0.01, 100, 0.01, 0, 1.0, 0, 5, true);
        var account = new AccountSnapshot(1_000_000, 1_000_000, 0, 1_000_000, 2000, 50, false, "USD");

        return sizer.Compute(spec, account, 100_000, 100_000 - 264, Side.Long,
            riskStateMultiplier: posture, regimeMultiplier: 0.64, ensembleConfidence: confidence,
            edgeSurplusR: surplus, strategyWeight: weight, atrPercentile: 0.5, correlationPenalty: 0.0,
            dataQualityScore: 1.0, executionQuality: 0.85, remainingRiskBudgetPercent: 1.5, isColdStart: cold);
    }

    [Theory]
    [InlineData(0.65, 0.10, 0.30)]   // типичный
    [InlineData(0.80, 0.30, 0.40)]   // сильный
    public void TheColdStartCanActuallyOpenItsFirstTrade(double confidence, double surplus, double weight)
    {
        SizingResult cold = Size(confidence, surplus, weight, cold: true, posture: 1.0);
        _out.WriteLine($"холодный старт: {cold.RiskPercent:F4}% {cold.Detail}");

        Assert.True(cold.IsTradeable, "холодный старт снова не может открыть первую сделку: " + cold.Detail);
    }

    [Fact]
    public void DefensiveShrinksTheTradeButDoesNotHaltIt()
    {
        // Лестница постуры — Normal, Caution, Defensive, Halt. Если Defensive ведёт себя как
        // Halt, ступеней на деле три, и выход из убыточной постуры требует сделок, которых
        // она сама не допускает.
        SizingResult normal = Size(0.65, 0.10, 0.30, cold: false, posture: 1.0);
        SizingResult defensive = Size(0.65, 0.10, 0.30, cold: false, posture: 0.35);

        _out.WriteLine($"Normal {normal.RiskPercent:F4}%, Defensive {defensive.RiskPercent:F4}% {defensive.Detail}");

        Assert.True(defensive.IsTradeable, "Defensive превратился в Halt: " + defensive.Detail);
        Assert.True(defensive.RiskPercent < normal.RiskPercent, "Defensive обязан быть МЕНЬШЕ, а не таким же");
    }

    [Fact]
    public void ACandidateWeakOnEveryAxisIsStillRefused()
    {
        // Пол не отменён — он перестал путать слабость кандидата с осторожностью системы.
        // Кандидат, пограничный по каждой оси, по-прежнему не стоит сделки.
        SizingResult marginal = Size(0.55, 0.00, 0.15, cold: false, posture: 1.0);
        _out.WriteLine($"пограничный: {marginal.Detail}");

        Assert.False(marginal.IsTradeable);
        Assert.Equal(NoTradeReason.SizeBelowMinimum, marginal.RejectionReason);
    }

    [Fact]
    public void PolicyNeverIncreasesRisk()
    {
        // Ни одно из исправлений не может увеличить размер — только разрешить меньший.
        SizingResult normal = Size(0.80, 0.30, 0.40, cold: false, posture: 1.0);
        SizingResult cold = Size(0.80, 0.30, 0.40, cold: true, posture: 1.0);
        SizingResult both = Size(0.80, 0.30, 0.40, cold: true, posture: 0.35);

        Assert.True(cold.RiskPercent < normal.RiskPercent);
        Assert.True(both.RiskPercent < cold.RiskPercent);
    }

    [Fact]
    public void AHaltedPostureSizesNothing()
    {
        SizingResult halted = Size(0.80, 0.30, 0.40, cold: false, posture: 0.0);
        Assert.False(halted.IsTradeable);
    }
}
