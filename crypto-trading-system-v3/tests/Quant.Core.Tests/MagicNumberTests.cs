using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace Quant.Core.Tests;

/// <summary>
/// «Ни один слой не читает магическое число».
///
/// Требование не стилистическое. Порог, вписанный в код, недостижим для анализа
/// чувствительности: его нельзя ни продуть по сетке, ни опровергнуть, ни даже увидеть,
/// не читая исходник. Он просто принимается на веру — а система, которая распоряжается
/// деньгами, не имеет права опираться на числа, которых никто не проверял.
///
/// Второй ущерб — дублирование. Порог напряжённости рынка 0.85 жил тремя копиями в трёх
/// местах движка. Поменять одну и не заметить двух — вопрос времени, а не внимательности.
///
/// Сторож охватывает СЛОИ, ПРИНИМАЮЩИЕ РЕШЕНИЯ: что торговать, каким размером, когда
/// остановиться. Границу нужно назвать вслух, иначе она расползётся: внутренние отсечки
/// отдельных стратегий и форма метрики качества сюда не входят — они описывают не
/// решение о капитале, а определение самой стратегии и самой метрики.
/// </summary>
public class MagicNumberTests
{
    private readonly ITestOutputHelper _out;
    public MagicNumberTests(ITestOutputHelper output) { _out = output; }

    /// <summary>Слои, чьи пороги решают судьбу капитала.</summary>
    private static readonly string[] DecisionLayers =
    {
        "Decision", "Risk", "Ev", "Sizing", "Exits", "Portfolio", "Data", "Regime", "Probability",
    };

    /// <summary>
    /// Сравнение с дробным литералом. Три исключения, и каждое — не послабление:
    ///
    /// «=>» — стрелка лямбды, а не знак сравнения. Без этого сторож считал порогом
    /// пересчёт баров в год и требовал вынести в конфигурацию число дней в году.
    ///
    /// 0.0 и 1.0 — границы диапазона, а не пороги: «доля больше нуля» и «доля не больше
    /// единицы» не подлежат настройке, потому что настраивать в них нечего.
    /// </summary>
    private static readonly Regex Threshold =
        new Regex(@"(?<!=)[<>]=?\s*(?!0\.0(?![0-9])|1\.0(?![0-9]))[0-9]+\.[0-9]+", RegexOptions.Compiled);

    [Fact]
    public void NoDecisionLayerComparesAgainstAHardCodedThreshold()
    {
        var offenders = new List<string>();

        foreach (string file in DecisionSources())
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue;

                if (Threshold.IsMatch(line))
                {
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}  {trimmed}");
                }
            }
        }

        foreach (string o in offenders) _out.WriteLine(o);

        Assert.True(offenders.Count == 0,
            $"порогов в коде: {offenders.Count}. Каждый недостижим для анализа чувствительности. " +
            "Место константы — EngineConfig:" + Environment.NewLine +
            string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void TheGuardActuallyLooksAtSomething()
    {
        // Сторож, не нашедший ни одного файла, зелен всегда и не значит ничего.
        List<string> files = DecisionSources().ToList();
        Assert.True(files.Count >= 20, $"сторож осматривает лишь {files.Count} файлов — он проверяет не то");

        // И он обязан УМЕТЬ находить: строка с порогом должна распознаваться.
        Assert.Matches(Threshold, "if (costR > 0.35)");
        Assert.Matches(Threshold, "if (x >= 0.99 && y > 0.30)");

        // А границы диапазона — нет.
        Assert.DoesNotMatch(Threshold, "if (share > 0.0)");
        Assert.DoesNotMatch(Threshold, "if (fraction <= 1.0)");

        // И стрелка лямбды — не сравнение.
        Assert.DoesNotMatch(Threshold, "private static double BarsPerYear(Tf tf) => 365.0 * 24.0;");
    }

    private static IEnumerable<string> DecisionSources()
    {
        string root = Path.Combine(RepoRoot(), "src", "Quant.Core");

        foreach (string layer in DecisionLayers)
        {
            string dir = Path.Combine(root, layer);
            if (!Directory.Exists(dir)) continue;

            foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                yield return file;
            }
        }

        yield return Path.Combine(root, "TradingEngine.cs");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir.FullName;
    }
}
