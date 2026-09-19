using System;
using System.Collections.Generic;
using System.Linq;
using Quant.Core.Config;
using Quant.Core.Primitives;
using Xunit;
using Xunit.Abstractions;

namespace Quant.Core.Tests;

/// <summary>
/// Работа на минутном сигнальном таймфрейме.
///
/// Спецификация называет M1/M3/M5 основными исполнительными таймфреймами, значит M1 обязан
/// быть рабочей конфигурацией, а не теоретически допустимой.
/// </summary>
public class MinuteTimeframeTests
{
    private readonly ITestOutputHelper _out;
    public MinuteTimeframeTests(ITestOutputHelper output) { _out = output; }

    private static EngineConfig M1Config()
    {
        var config = new EngineConfig();
        config.UseTimeframes(Tf.M1, Tf.M15);
        return config;
    }

    [Fact]
    public void AMinuteConfigurationPassesValidation()
    {
        IReadOnlyList<string> problems = M1Config().Validate();

        foreach (string p in problems) _out.WriteLine(p);
        Assert.Empty(problems);
    }

    [Fact]
    public void TheEngineReachesReadinessOnAMinuteTimeframe()
    {
        var h = new PipelineHarness(M1Config());
        h.Feed(new MarketSimulator(seed: 501).Generate(3000, 0.0002, 0.0012, minutesPerBar: 1));

        _out.WriteLine($"данные готовы: {h.Data.IsReady}");
        _out.WriteLine($"баров M1: {h.Data.Signal.BarsProcessed}, баров M15: {h.Data.Series(Tf.M15).BarsProcessed}");
        _out.WriteLine($"признаки: {(h.LastFeatures == null ? "НЕТ" : "есть")}");

        Assert.True(h.Data.IsReady, "На минутном таймфрейме система не достигает готовности.");
        Assert.NotNull(h.LastFeatures);
    }

    [Fact]
    public void TheEngineProducesDecisionsOnAMinuteTimeframe()
    {
        var config = M1Config();
        var h = new EngineHarness(config);

        h.Feed(new MarketSimulator(seed: 502).Generate(3000, 0.0002, 0.0012, minutesPerBar: 1),
               spreadFraction: 0.00005);

        long decisions = h.Engine.Journal.TotalAccepted + h.Engine.Journal.TotalRejected;
        _out.WriteLine($"решений на M1: {decisions}");
        _out.WriteLine(h.Engine.Journal.RejectionSummary());
        _out.WriteLine(h.Engine.RenderHeartbeat(h.LastTimeUtc));

        Assert.True(decisions > 0,
            "На минутном таймфрейме движок не вынес ни одного решения — цикл не запускается.");
    }

    [Fact]
    public void TheDefaultFiveMinuteConfigurationProducesDecisionsToo()
    {
        // Контрольная группа: если M5 решения выносит, а M1 нет, дело в таймфрейме.
        var h = new EngineHarness(new EngineConfig());
        h.Feed(new MarketSimulator(seed: 503).Generate(3000, 0.0002, 0.0012), spreadFraction: 0.00005);

        long decisions = h.Engine.Journal.TotalAccepted + h.Engine.Journal.TotalRejected;
        _out.WriteLine($"решений на M5: {decisions}");

        Assert.True(decisions > 0);
    }

    [Fact]
    public void AMinuteSignalWithAnHourContextIsAlsoValid()
    {
        // Более типичная связка: сигнал M1, контекст H1.
        var config = new EngineConfig();
        config.UseTimeframes(Tf.M1, Tf.H1);

        Assert.Empty(config.Validate());

        var h = new PipelineHarness(config);
        h.Feed(new MarketSimulator(seed: 504).Generate(6000, 0.0002, 0.0012, minutesPerBar: 1));

        _out.WriteLine($"баров M1: {h.Data.Signal.BarsProcessed}, баров H1: {h.Data.Series(Tf.H1).BarsProcessed}");
        _out.WriteLine($"данные готовы: {h.Data.IsReady}");

        Assert.True(h.Data.IsReady,
            "Связка «сигнал M1, контекст H1» требует 60 часов истории — это надо либо обеспечить, либо сказать явно.");
    }
}

/// <summary>
/// Набор агрегируемых таймфреймов должен оставаться согласованным при любом выборе:
/// содержать сигнальный, контекстный и корреляционный, и не тащить лишнего.
/// </summary>
public class TimeframeSetTests
{
    [Theory]
    [InlineData(Tf.M1, Tf.M15)]
    [InlineData(Tf.M1, Tf.H1)]
    [InlineData(Tf.M3, Tf.M15)]
    [InlineData(Tf.M5, Tf.H1)]
    [InlineData(Tf.M5, Tf.H4)]
    [InlineData(Tf.M15, Tf.H4)]
    public void EveryValidPairProducesAConsistentConfiguration(Tf signal, Tf context)
    {
        var config = new EngineConfig();
        config.UseTimeframes(signal, context);

        Assert.Empty(config.Validate());
        Assert.Contains(signal, config.Data.Timeframes);
        Assert.Contains(context, config.Data.Timeframes);
        Assert.Contains(config.Portfolio.CorrelationTimeframe, config.Data.Timeframes);
    }

    [Fact]
    public void TimeframesFasterThanTheSignalAreDropped()
    {
        // Такие ряды не читает ни один слой, а на минутном сигнале лишний ряд — это
        // реальная работа на каждом баре.
        var config = new EngineConfig();
        config.UseTimeframes(Tf.M15, Tf.H4);

        Assert.DoesNotContain(Tf.M1, config.Data.Timeframes);
        Assert.DoesNotContain(Tf.M3, config.Data.Timeframes);
        Assert.DoesNotContain(Tf.M5, config.Data.Timeframes);
        Assert.Contains(Tf.M15, config.Data.Timeframes);
    }

    [Fact]
    public void AMinuteSignalKeepsTheWholeLadder()
    {
        var config = new EngineConfig();
        config.UseTimeframes(Tf.M1, Tf.M15);

        foreach (Tf tf in new[] { Tf.M1, Tf.M3, Tf.M5, Tf.M15, Tf.H1, Tf.H4 })
        {
            Assert.Contains(tf, config.Data.Timeframes);
        }
    }

    [Fact]
    public void TheCorrelationTimeframeNeverRunsFasterThanTheSignal()
    {
        // На более быстром ряде корреляция измеряла бы микроструктурный шум, а не связь
        // инструментов.
        var config = new EngineConfig();
        config.Portfolio.CorrelationTimeframe = Tf.M5;
        config.UseTimeframes(Tf.M15, Tf.H4);

        Assert.True((int)config.Portfolio.CorrelationTimeframe >= (int)Tf.M15);
        Assert.Empty(config.Validate());
    }

    [Fact]
    public void AContextNoSlowerThanTheSignalIsRejectedWithAClearReason()
    {
        var config = new EngineConfig();
        config.UseTimeframes(Tf.M15, Tf.M5);

        Assert.Contains(config.Validate(), p => p.Contains("ContextTimeframe must be slower"));
    }

    [Fact]
    public void TheTimeframeSetIsSortedAndFreeOfDuplicates()
    {
        var config = new EngineConfig();
        config.UseTimeframes(Tf.M3, Tf.H1);

        Tf[] set = config.Data.Timeframes;
        Assert.Equal(set.Distinct().Count(), set.Length);
        Assert.Equal(set.OrderBy(t => (int)t).ToArray(), set);
    }
}
