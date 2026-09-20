using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Quant.Core.Config;
using Quant.Core.Decision;
using Quant.Core.Execution;
using Quant.Core.Exits;
using Quant.Core.Primitives;
using Quant.Core.Probability;
using Quant.Core.Risk;
using Quant.Core.State;
using Quant.Core.Strategies;
using Xunit;

namespace Quant.Core.Tests;

/// <summary>
/// Защита, объявленная и не подключённая, опаснее её отсутствия: она есть в коде, её видно
/// при чтении, и на неё рассчитывают — а её нет.
///
/// Этот класс ошибки встречался в проекте трижды: ModeGuard существовал и не использовался,
/// OpportunityRanker.Rank не имел вызывающих, порог корреляции сигналов был описан в
/// комментарии и не реализован. Тесты ниже фиксируют, что всё объявленное подключено.
/// </summary>
public class WiringTests
{
    // ── Причины отказа ───────────────────────────────────────────────────────────

    [Fact]
    public void ReconciliationHaltIsVisibleInTheRejectionSummary()
    {
        // Расхождение с брокером — самое важное состояние системы. Пропустить его молча
        // значит показать человеку бота, который «просто ничего не делает».
        var config = new EngineConfig();
        var gate = new TradeGate(config);

        GateOutcome outcome = GateOutcome.Reject(NoTradeReason.ReconciliationPending, "тест");
        Assert.Equal(NoTradeReason.ReconciliationPending, outcome.Reason);

        // И причина действительно используется в движке, а не только объявлена.
        string engine = System.IO.File.ReadAllText(SourcePath("src/Quant.Core/TradingEngine.cs"));
        Assert.Contains("NoTradeReason.ReconciliationPending", engine);
    }

    [Fact]
    public void EveryRejectionReasonIsActuallyUsed()
    {
        // Перечисление причин отказа — это описание поведения системы. Причина, которую
        // никто не выставляет, означает либо ненаписанную проверку, либо ложь в документации.
        string enums = System.IO.File.ReadAllText(SourcePath("src/Quant.Core/Primitives/Enums.cs"));
        Match block = Regex.Match(enums, @"enum NoTradeReason\s*\{(.*?)\n\}", RegexOptions.Singleline);
        Assert.True(block.Success);

        var reasons = Regex.Matches(block.Groups[1].Value, @"^\s*(\w+)\s*(?:=\s*\d+\s*)?,", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .Where(r => r != "None")
            .ToList();

        string source = AllSource();
        var unused = reasons.Where(r => !source.Contains("NoTradeReason." + r)).ToList();

        Assert.True(unused.Count == 0,
            "объявлены, но нигде не выставляются: " + string.Join(", ", unused));
    }

    [Fact]
    public void EveryConfigurationSettingIsActuallyRead()
    {
        // «Ни один слой не читает магическое число» — правило этого проекта. Настройка,
        // которую никто не читает, означает обратное: число зашито где-то в коде и выведено
        // из-под анализа чувствительности.
        string config = System.IO.File.ReadAllText(SourcePath("src/Quant.Core/Config/EngineConfig.cs"));
        var declared = Regex.Matches(config, @"public\s+[\w<>\[\]?.]+\s+(\w+)\s*\{\s*get;\s*set;\s*\}")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        // Ищется упоминание ВНЕ самого файла настроек, в любой форме. Проверка через
        // «точка плюс имя» была бы строже и неверна: настройка может попадать в конфиг
        // инициализатором объекта, где точки нет. А упоминание внутри EngineConfig.cs не
        // считается — именно так и выглядел дефект: значение проверялось в Validate и
        // никогда не доходило до формулы, которая вместо него использовала зашитое число.
        string elsewhere = AllSource().Replace(config, string.Empty);

        var unread = declared
            .Where(name => !Regex.IsMatch(elsewhere, @"\b" + Regex.Escape(name) + @"\b"))
            .ToList();

        Assert.True(unread.Count == 0,
            "объявлены, но нигде не читаются: " + string.Join(", ", unread));
    }

    // ── Порог корреляции сигналов ────────────────────────────────────────────────

    [Fact]
    public void StronglyOverlappingStrategiesCountAsOneVote()
    {
        // Восемь стратегий, читающих одно и то же, не должны давать почти три независимых
        // подтверждения. Выше порога перекрытия голос не засчитывается вовсе.
        //
        // Перекрытие подобрано СТРОГО МЕЖДУ порогом и единицей: при перекрытии ровно 1.0
        // обе формулы дают ноль, и тест не различал бы их вовсе.
        var config = new StrategyConfig { SignalCorrelationThreshold = 0.70 };
        var tracker = new SignalCorrelationTracker(config);

        var common = new[]
        {
            FeatureFamily.TrendDirection, FeatureFamily.Momentum,
            FeatureFamily.Volatility, FeatureFamily.Volume,
        };

        tracker.Register(new Stub("Похожая-1", common));
        tracker.Register(new Stub("Похожая-2", common.Concat(new[] { FeatureFamily.MarketStructure }).ToArray()));

        // Жаккар: 4 общих из 5 в объединении = 0.80, это выше порога 0.70.
        double overlap = tracker.Overlap("Похожая-1", "Похожая-2");
        Assert.InRange(overlap, 0.71, 0.99);

        // Вторая стратегия не добавляет свидетельства: голос остаётся один.
        // Без порога она дала бы 1 + (1 - 0.8) = 1.2.
        Assert.Equal(1.0, tracker.EffectiveVoteCount(new[] { "Похожая-1", "Похожая-2" }), 6);

        // А по-настоящему разные читают разное и считаются двумя.
        tracker.Register(new Stub("Другая", new[] { FeatureFamily.Microstructure, FeatureFamily.RangePosition }));
        Assert.True(tracker.EffectiveVoteCount(new[] { "Похожая-1", "Другая" }) > 1.5);
    }

    /// <summary>Стратегия-заглушка: нужны только имя и семейства признаков.</summary>
    private sealed class Stub : IStrategy
    {
        private readonly FeatureFamily[] _families;
        public Stub(string name, FeatureFamily[] families) { Name = name; _families = families; }

        public string Name { get; }
        public StrategyKind Kind => StrategyKind.TrendFollowing;
        public IReadOnlyDictionary<MarketRegime, double> RegimeFit => new Dictionary<MarketRegime, double>();
        public IReadOnlyCollection<FeatureFamily> Families => _families;
        public StrategySignal Evaluate(StrategyContext context) => StrategySignal.Neutral(Name, "заглушка");
    }

    // ── Пауза между повторами ────────────────────────────────────────────────────

    [Fact]
    public void TheRetryDelayIsActuallyApplied()
    {
        // Немедленный повтор почти наверняка встретит ту же причину: отсутствующая
        // котировка не появляется за микросекунду. Повтор без паузы — способ получить три
        // отказа вместо одного и испортить статистику качества исполнения.
        var config = new EngineConfig();
        var delays = new List<int>();

        var execution = new ExecutionEngine(config.Execution, new RefusingBroker(),
            new ExecutionQualityTracker(config.Execution), new IdempotencyGuard(), "test",
            delay: ms => delays.Add(ms));

        execution.Open("sig", "BTCUSD", Side.Long, 1.0,
            new ExitPlan { IsValid = true, StopPrice = 49500, StopDistance = 500 },
            Spec(), 50000, 1.0, 500, 2.0, null);

        Assert.Equal(config.Execution.MaxOrderRetries, delays.Count);
        Assert.All(delays, d => Assert.Equal(config.Execution.RetryDelayMs, d));
    }

    // ── Пороги размера выборки ───────────────────────────────────────────────────

    [Fact]
    public void SampleThresholdsComeFromConfigurationNotFromTheFormula()
    {
        // Иначе анализ чувствительности не достаёт до чисел, которые определяют, когда
        // система начинает доверять своей же статистике.
        var strict = new ProbabilityEstimate(0.6, 0.05, effectiveSample: 60,
            calibrationQuality: 1.0, basis: "test", minSample: 50, fullTrustSample: 400);

        var lenient = new ProbabilityEstimate(0.6, 0.05, effectiveSample: 60,
            calibrationQuality: 1.0, basis: "test", minSample: 5, fullTrustSample: 40);

        Assert.True(lenient.Confidence > strict.Confidence,
            $"более мягкие пороги обязаны давать больше уверенности: {lenient.Confidence:F3} против {strict.Confidence:F3}");
    }

    // ── Вспомогательное ──────────────────────────────────────────────────────────

    private static SymbolSpec Spec() =>
        new SymbolSpec("BTCUSD", 0.01, 0.01, 2, 0.001, 1000, 0.001, 35, 0.01, 0, 2.0, true);

    private static string SourcePath(string relative)
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "src")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return System.IO.Path.Combine(dir.FullName, relative);
    }

    private static string AllSource()
    {
        string root = System.IO.Path.GetDirectoryName(SourcePath("src"));
        var sb = new System.Text.StringBuilder();
        foreach (string file in System.IO.Directory.EnumerateFiles(
                     System.IO.Path.Combine(root, "src"), "*.cs", System.IO.SearchOption.AllDirectories))
        {
            if (file.Contains("/obj/") || file.Contains("/bin/")) continue;
            sb.AppendLine(System.IO.File.ReadAllText(file));
        }
        return sb.ToString();
    }

    private sealed class RefusingBroker : IBroker
    {
        public BrokerResult OpenPosition(string s, Side d, double v, double stop, double? tp, string l, string c, double m) =>
            BrokerResult.Fail("нет котировки", isTransient: true);

        public BrokerResult ClosePosition(long id, double v) => BrokerResult.Fail("нет", false);
        public BrokerResult ModifyStop(long id, double p) => BrokerResult.Fail("нет", false);
        public IReadOnlyList<BrokerPosition> GetOpenPositions(string prefix) => Array.Empty<BrokerPosition>();
        public AccountSnapshot GetAccount() => new AccountSnapshot(10000, 10000, 0, 10000, 1000, 50, false, "USD");
        public bool IsSimulated => true;
    }
}
