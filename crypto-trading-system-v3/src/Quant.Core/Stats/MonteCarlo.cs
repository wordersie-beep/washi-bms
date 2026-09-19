using System;
using System.Collections.Generic;
using System.Text;
using Quant.Core.Numerics;

namespace Quant.Core.Stats;

/// <summary>Распределение исходов, полученное симуляцией.</summary>
public sealed class MonteCarloResult
{
    public int Paths { get; init; }
    public int TradesPerPath { get; init; }

    public double DrawdownP5 { get; init; }
    public double DrawdownP25 { get; init; }
    public double DrawdownMedian { get; init; }
    public double DrawdownP75 { get; init; }
    public double DrawdownP95 { get; init; }
    public double DrawdownWorst { get; init; }

    public double FinalP5 { get; init; }
    public double FinalMedian { get; init; }
    public double FinalP95 { get; init; }

    /// <summary>Доля путей, закончившихся убытком.</summary>
    public double ProbabilityOfLoss { get; init; }

    /// <summary>Доля путей, просевших более чем на заданный порог.</summary>
    public double ProbabilityOfSevereDrawdown { get; init; }

    public string Render()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=============== MONTE CARLO ===============");
        sb.AppendFormat("  Путей {0}, сделок в пути {1}", Paths, TradesPerPath).AppendLine();
        sb.AppendLine("  Просадка (в R):");
        sb.AppendFormat("    P5 {0,7:F2}   P25 {1,7:F2}   медиана {2,7:F2}   P75 {3,7:F2}   P95 {4,7:F2}   худшая {5,7:F2}",
            DrawdownP5, DrawdownP25, DrawdownMedian, DrawdownP75, DrawdownP95, DrawdownWorst).AppendLine();
        sb.AppendLine("  Итог (в R):");
        sb.AppendFormat("    P5 {0,7:F2}   медиана {1,7:F2}   P95 {2,7:F2}", FinalP5, FinalMedian, FinalP95).AppendLine();
        sb.AppendFormat("  Вероятность убыточного пути      {0:P1}", ProbabilityOfLoss).AppendLine();
        sb.AppendFormat("  Вероятность тяжёлой просадки     {0:P1}", ProbabilityOfSevereDrawdown).AppendLine();
        sb.Append("==========================================");
        return sb.ToString();
    }

    public override string ToString() => Render();
}

/// <summary>
/// Монте-Карло по результатам сделок (разделы 96–97).
///
/// Отвечает на вопрос, на который исторический бэктест ответить не может: историческая
/// максимальная просадка — это ОДНА реализация из распределения, а торговать предстоит в
/// другой. Система, показавшая 8% просадки на истории, вполне может иметь 20% в
/// девяносто пятом процентиле — и именно этот, а не исторический, показатель должен
/// определять допустимый размер позиции.
///
/// Перемешивание порядка сделок с КЛАСТЕРИЗАЦИЕЙ УБЫТКОВ существенно: простая случайная
/// перестановка разрушает серии, а серии — главный источник глубоких просадок. Симуляция без
/// кластеризации систематически недооценивает хвост, то есть ошибается в единственную
/// сторону, которая имеет значение.
/// </summary>
public sealed class MonteCarloSimulator
{
    private readonly int _paths;
    private readonly ulong _seed;

    public MonteCarloSimulator(int paths = 2000, ulong seed = 20260919UL)
    {
        _paths = Math.Max(100, paths);
        _seed = seed;
    }

    /// <summary>
    /// Прогоняет распределение исходов.
    /// </summary>
    /// <param name="observedR">Результаты реальных сделок, в R.</param>
    /// <param name="tradesPerPath">Длина симулируемого пути; по умолчанию — как в выборке.</param>
    /// <param name="lossClustering">
    /// Вероятность, что после убытка следующая сделка тоже будет выбрана из убыточных.
    /// 0 — независимость (оптимистично), 0.3 — выраженные серии.
    /// </param>
    /// <param name="severeDrawdownR">Порог «тяжёлой» просадки, в R.</param>
    /// <param name="slippagePerTradeR">
    /// Дополнительные издержки на сделку, в R, для стресс-теста по издержкам (раздел 90).
    /// </param>
    public MonteCarloResult Run(
        IReadOnlyList<double> observedR,
        int tradesPerPath = 0,
        double lossClustering = 0.20,
        double severeDrawdownR = 15.0,
        double slippagePerTradeR = 0)
    {
        if (observedR == null || observedR.Count < 10)
        {
            return new MonteCarloResult { Paths = 0, TradesPerPath = 0 };
        }

        int pathLength = tradesPerPath > 0 ? tradesPerPath : observedR.Count;

        // Разделяем выборку, чтобы кластеризация могла тянуть именно из убытков.
        var winners = new List<double>();
        var losers = new List<double>();
        for (int i = 0; i < observedR.Count; i++)
        {
            if (observedR[i] > 0) winners.Add(observedR[i]); else losers.Add(observedR[i]);
        }

        if (winners.Count == 0 || losers.Count == 0)
        {
            // Без обеих сторон кластеризация бессмысленна; работаем простой перестановкой.
            lossClustering = 0;
        }

        double baseWinRate = (double)winners.Count / observedR.Count;

        var rng = new Pcg32(_seed);
        var drawdowns = new double[_paths];
        var finals = new double[_paths];
        int lossyPaths = 0, severePaths = 0;

        for (int path = 0; path < _paths; path++)
        {
            double cumulative = 0, peak = 0, maxDrawdown = 0;
            bool previousWasLoss = false;

            for (int t = 0; t < pathLength; t++)
            {
                double r;

                if (lossClustering > 0 && previousWasLoss && rng.NextDouble() < lossClustering)
                {
                    r = losers[rng.NextInt(0, losers.Count)];
                }
                else if (lossClustering > 0)
                {
                    bool win = rng.NextDouble() < baseWinRate;
                    r = win ? winners[rng.NextInt(0, winners.Count)] : losers[rng.NextInt(0, losers.Count)];
                }
                else
                {
                    r = observedR[rng.NextInt(0, observedR.Count)];
                }

                r -= slippagePerTradeR;
                previousWasLoss = r <= 0;

                cumulative += r;
                if (cumulative > peak) peak = cumulative;
                double drawdown = peak - cumulative;
                if (drawdown > maxDrawdown) maxDrawdown = drawdown;
            }

            drawdowns[path] = maxDrawdown;
            finals[path] = cumulative;
            if (cumulative < 0) lossyPaths++;
            if (maxDrawdown > severeDrawdownR) severePaths++;
        }

        Array.Sort(drawdowns);
        Array.Sort(finals);

        return new MonteCarloResult
        {
            Paths = _paths,
            TradesPerPath = pathLength,
            DrawdownP5 = MathUtil.QuantileSorted(drawdowns, _paths, 0.05),
            DrawdownP25 = MathUtil.QuantileSorted(drawdowns, _paths, 0.25),
            DrawdownMedian = MathUtil.QuantileSorted(drawdowns, _paths, 0.50),
            DrawdownP75 = MathUtil.QuantileSorted(drawdowns, _paths, 0.75),
            DrawdownP95 = MathUtil.QuantileSorted(drawdowns, _paths, 0.95),
            DrawdownWorst = drawdowns[_paths - 1],
            FinalP5 = MathUtil.QuantileSorted(finals, _paths, 0.05),
            FinalMedian = MathUtil.QuantileSorted(finals, _paths, 0.50),
            FinalP95 = MathUtil.QuantileSorted(finals, _paths, 0.95),
            ProbabilityOfLoss = (double)lossyPaths / _paths,
            ProbabilityOfSevereDrawdown = (double)severePaths / _paths,
        };
    }
}
