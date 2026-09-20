using System;
using System.Collections.Generic;
using Quant.Core.Numerics;

namespace Quant.Core.Stats;

/// <summary>Разбор итогового значения фитнеса — чтобы результат оптимизации можно было объяснить.</summary>
public sealed class FitnessBreakdown
{
    public double Fitness { get; init; }
    public double QualityScore { get; init; }
    public IReadOnlyDictionary<string, double> Components { get; init; }
    public IReadOnlyList<string> Penalties { get; init; }

    public override string ToString()
    {
        var parts = new List<string>();
        if (Components != null)
        {
            foreach (KeyValuePair<string, double> kv in Components) parts.Add($"{kv.Key}={kv.Value:F3}");
        }

        string penalties = Penalties == null || Penalties.Count == 0 ? "нет" : string.Join(", ", Penalties);
        return $"fitness={Fitness:F4} ({string.Join(" ", parts)}) штрафы: {penalties}";
    }
}

/// <summary>
/// Функция приспособленности для оптимизации (разделы 133–135).
///
/// Оптимизация по чистой прибыли — самый надёжный способ получить переподогнанный набор
/// параметров. Максимум прибыли почти всегда лежит на узком пике, достигается высокой
/// просадкой и держится на нескольких сделках; всё это невоспроизводимо вне выборки.
///
/// Здесь фитнес — композит, в котором прибыль лишь одна из составляющих, а ШТРАФЫ способны
/// обнулить результат полностью. Штрафуется то, что систематически не переносится
/// out-of-sample:
///
///   * слишком мало сделок — результат статистически пуст;
///   * высокая просадка — риск несовместим с выживанием капитала;
///   * концентрация прибыли — преимущество держится на выбросах;
///   * зависимость от одного символа — это не система, а одна ставка;
///   * чувствительность к издержкам — работает только при идеальном исполнении;
///   * длинные серии убытков — путь, который невозможно высидеть.
/// </summary>
public static class FitnessCalculator
{
    /// <summary>Минимум сделок, ниже которого результат не рассматривается вообще.</summary>
    public const int MinimumTradesForFitness = 30;

    /// <summary>
    /// Считает фитнес.
    /// </summary>
    /// <param name="report">Отчёт по основному прогону.</param>
    /// <param name="stressedReport">
    /// Тот же прогон с увеличенными издержками (раздел 90). Если null, штраф за
    /// чувствительность к издержкам не применяется — но и подтверждения устойчивости нет.
    /// </param>
    /// <param name="symbolContributions">Вклад каждого символа в прибыль, для проверки на зависимость от одного инструмента.</param>
    public static FitnessBreakdown Compute(
        PerformanceReport report,
        double concentrationWarning,
        PerformanceReport stressedReport = null,
        IReadOnlyDictionary<string, double> symbolContributions = null)
    {
        var components = new Dictionary<string, double>(StringComparer.Ordinal);
        var penalties = new List<string>();

        if (report == null || report.Trades < MinimumTradesForFitness)
        {
            return new FitnessBreakdown
            {
                Fitness = 0,
                QualityScore = 0,
                Components = components,
                Penalties = new List<string> { $"сделок {report?.Trades ?? 0}, минимум {MinimumTradesForFitness}" },
            };
        }

        // --- Составляющие качества, каждая в 0..1 ----------------------------------------
        double expectancy = MathUtil.LinearScale(report.ExpectancyR, 0, 0.35);
        double profitFactor = MathUtil.LinearScale(report.ProfitFactor, 1.0, 2.0);
        double sharpe = MathUtil.LinearScale(report.SharpeLike, 0, 0.35);
        double sortino = MathUtil.LinearScale(report.SortinoLike, 0, 0.50);
        double recovery = MathUtil.LinearScale(report.RecoveryFactor, 0.5, 5.0);

        // Просадка входит как обратная величина: чем глубже, тем ниже оценка.
        double drawdownQuality = 1.0 - MathUtil.LinearScale(report.MaxDrawdownR, 5, 40);

        // Достаточность выборки: 30 сделок — минимум, 300 — уверенная выборка.
        double sampleQuality = MathUtil.LinearScale(report.Trades, MinimumTradesForFitness, 300);

        components["expectancy"] = expectancy;
        components["profitFactor"] = profitFactor;
        components["sharpe"] = sharpe;
        components["sortino"] = sortino;
        components["recovery"] = recovery;
        components["drawdown"] = drawdownQuality;
        components["sample"] = sampleQuality;

        // Средневзвешенное геометрическое: слабость в одном измерении не компенсируется
        // силой в другом. Набор параметров с прекрасной прибылью и катастрофической
        // просадкой не должен обгонять сбалансированный.
        double quality = WeightedGeometricMean(
            (expectancy, 0.25),
            (profitFactor, 0.15),
            (sharpe, 0.12),
            (sortino, 0.12),
            (recovery, 0.10),
            (drawdownQuality, 0.16),
            (sampleQuality, 0.10));

        // --- Штрафы --------------------------------------------------------------------------
        double multiplier = 1.0;

        if (report.ExpectancyR <= 0)
        {
            penalties.Add("матожидание не положительно");
            multiplier = 0;
        }

        if (report.ExpectancyExcludingTailWinnersR <= 0 && report.ExpectancyR > 0)
        {
            penalties.Add("без лучших 5% сделок матожидание отрицательно");
            multiplier *= 0.25;
        }

        if (report.ProfitConcentrationTop5 > concentrationWarning)
        {
            penalties.Add($"{report.ProfitConcentrationTop5:P0} прибыли создано 5% сделок");
            multiplier *= 1.0 - MathUtil.Clamp01((report.ProfitConcentrationTop5 - concentrationWarning) / (1.0 - concentrationWarning)) * 0.7;
        }

        if (report.LongestLosingStreak > 12)
        {
            penalties.Add($"серия из {report.LongestLosingStreak} убытков подряд");
            multiplier *= 1.0 - MathUtil.Clamp01((report.LongestLosingStreak - 12) / 12.0) * 0.5;
        }

        if (report.MaxDrawdownPercent > 25)
        {
            penalties.Add($"просадка {report.MaxDrawdownPercent:F1}%");
            multiplier *= 1.0 - MathUtil.Clamp01((report.MaxDrawdownPercent - 25) / 25.0) * 0.8;
        }

        // Чувствительность к издержкам (раздел 90): стратегия, ломающаяся от умеренного
        // роста издержек, работает только в бэктесте.
        if (stressedReport != null && stressedReport.Trades > 0)
        {
            double retention = MathUtil.SafeDiv(stressedReport.ExpectancyR, report.ExpectancyR);
            components["costRobustness"] = MathUtil.Clamp01(retention);

            if (retention < 0.5)
            {
                penalties.Add($"при росте издержек сохраняется лишь {retention:P0} матожидания");
                multiplier *= MathUtil.Clamp(retention * 2, 0.1, 1.0);
            }
        }

        // Зависимость от одного символа (раздел 134).
        if (symbolContributions != null && symbolContributions.Count > 1)
        {
            double total = 0, largest = 0;
            foreach (KeyValuePair<string, double> kv in symbolContributions)
            {
                if (kv.Value <= 0) continue;
                total += kv.Value;
                if (kv.Value > largest) largest = kv.Value;
            }

            double share = MathUtil.SafeDiv(largest, total);
            components["symbolDiversity"] = 1.0 - share;

            if (share > 0.70)
            {
                penalties.Add($"{share:P0} прибыли из одного инструмента");
                multiplier *= 1.0 - MathUtil.Clamp01((share - 0.70) / 0.30) * 0.6;
            }
        }

        double fitness = MathUtil.Clamp(quality * multiplier, 0, 1) * 1000.0;

        return new FitnessBreakdown
        {
            Fitness = fitness,
            QualityScore = quality,
            Components = components,
            Penalties = penalties,
        };
    }

    private static double WeightedGeometricMean(params (double Value, double Weight)[] terms)
    {
        double sumLog = 0, sumWeight = 0;
        for (int i = 0; i < terms.Length; i++)
        {
            double v = MathUtil.Clamp(terms[i].Value, 0.01, 1.0);
            sumLog += terms[i].Weight * Math.Log(v);
            sumWeight += terms[i].Weight;
        }
        return sumWeight <= 0 ? 0 : Math.Exp(sumLog / sumWeight);
    }
}
