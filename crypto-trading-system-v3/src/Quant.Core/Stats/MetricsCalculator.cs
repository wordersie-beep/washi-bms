using System;
using System.Collections.Generic;
using Quant.Core.Numerics;
using Quant.Core.Risk;

namespace Quant.Core.Stats;

/// <summary>
/// Считает полный статистический отчёт по журналу сделок (раздел 165).
///
/// Отдельного упоминания заслуживает КОНЦЕНТРАЦИЯ ПРИБЫЛИ (разделы 99–100). Стратегия, вся
/// прибыль которой создана тремя сделками из четырёхсот, статистически неотличима от
/// стратегии без преимущества, которой повезло. Без этой метрики такой результат выглядит как
/// красивая кривая капитала, и именно так системы попадают в реальную торговлю на основании
/// удачи. Поэтому отчёт не только считает концентрацию, но и пересчитывает матожидание БЕЗ
/// хвостовых прибылей: если без них оно отрицательное, преимущества нет.
/// </summary>
public static class MetricsCalculator
{
    public static PerformanceReport Compute(
        IReadOnlyList<TradeRecord> trades,
        double startingEquity,
        double riskOfRuin = 0)
    {
        if (trades == null || trades.Count == 0) return new PerformanceReport();

        int n = trades.Count;
        var rValues = new double[n];
        double sumR = 0, sumRSquared = 0;
        double grossWinR = 0, grossLossR = 0;
        int wins = 0;
        double netProfit = 0;
        double sumHolding = 0, sumMae = 0, sumMfe = 0, sumCapture = 0;

        int longestWin = 0, longestLoss = 0, currentStreak = 0;
        DateTime first = trades[0].EntryTimeUtc, last = trades[0].ExitTimeUtc;

        for (int i = 0; i < n; i++)
        {
            TradeRecord t = trades[i];
            double r = MathUtil.Finite(t.R);

            rValues[i] = r;
            sumR += r;
            sumRSquared += r * r;
            netProfit += MathUtil.Finite(t.NetProfit);

            if (r > 0)
            {
                wins++;
                grossWinR += r;
                currentStreak = currentStreak > 0 ? currentStreak + 1 : 1;
                if (currentStreak > longestWin) longestWin = currentStreak;
            }
            else
            {
                grossLossR += -r;
                currentStreak = currentStreak < 0 ? currentStreak - 1 : -1;
                if (-currentStreak > longestLoss) longestLoss = -currentStreak;
            }

            sumHolding += t.DurationMinutes;
            sumMae += Math.Abs(MathUtil.Finite(t.MaeR));
            sumMfe += Math.Abs(MathUtil.Finite(t.MfeR));
            sumCapture += t.CaptureRatio;

            if (t.EntryTimeUtc < first) first = t.EntryTimeUtc;
            if (t.ExitTimeUtc > last) last = t.ExitTimeUtc;
        }

        double mean = sumR / n;
        double variance = n < 2 ? 0 : Math.Max(0, (sumRSquared - (n * mean * mean)) / (n - 1));
        double stdDev = Math.Sqrt(variance);

        // --- Кривая капитала в R, просадки и Ulcer index --------------------------------
        double cumulative = 0, peak = 0, maxDrawdown = 0, sumDrawdown = 0, sumDrawdownSquared = 0;
        for (int i = 0; i < n; i++)
        {
            cumulative += rValues[i];
            if (cumulative > peak) peak = cumulative;

            double drawdown = peak - cumulative;
            if (drawdown > maxDrawdown) maxDrawdown = drawdown;

            sumDrawdown += drawdown;

            // Ulcer index считается по ОТНОСИТЕЛЬНОЙ просадке; при нулевом пике относительной
            // просадки ещё не существует.
            double relative = peak > MathUtil.Epsilon ? 100.0 * drawdown / peak : 0;
            sumDrawdownSquared += relative * relative;
        }

        // --- Downside deviation для Sortino ----------------------------------------------
        double downsideSquared = 0;
        int downsideCount = 0;
        for (int i = 0; i < n; i++)
        {
            if (rValues[i] < 0) { downsideSquared += rValues[i] * rValues[i]; downsideCount++; }
        }
        double downsideDeviation = downsideCount == 0 ? 0 : Math.Sqrt(downsideSquared / downsideCount);

        // --- Концентрация прибыли и убытка ------------------------------------------------
        var sorted = new double[n];
        Array.Copy(rValues, sorted, n);
        Array.Sort(sorted);   // по возрастанию

        double totalPositive = 0, totalNegative = 0;
        for (int i = 0; i < n; i++)
        {
            if (sorted[i] > 0) totalPositive += sorted[i];
            else totalNegative += -sorted[i];
        }

        double top1 = TopShare(sorted, n, 0.01, totalPositive, fromTop: true);
        double top5 = TopShare(sorted, n, 0.05, totalPositive, fromTop: true);
        double top10 = TopShare(sorted, n, 0.10, totalPositive, fromTop: true);
        double worst5 = TopShare(sorted, n, 0.05, totalNegative, fromTop: false);

        // Матожидание без хвостовых прибылей: проверка на то, что преимущество не держится
        // на горстке выбросов.
        int excludeCount = Math.Max(1, (int)Math.Ceiling(n * 0.05));
        double sumExcludingTail = 0;
        for (int i = 0; i < n - excludeCount; i++) sumExcludingTail += sorted[i];
        double expectancyExcludingTail = n - excludeCount > 0 ? sumExcludingTail / (n - excludeCount) : 0;

        double days = Math.Max((last - first).TotalDays, 1.0 / 24.0);
        double netProfitPercent = startingEquity > 0 ? 100.0 * netProfit / startingEquity : 0;

        return new PerformanceReport
        {
            Trades = n,
            TradesPerDay = n / days,
            NetProfit = netProfit,
            NetProfitPercent = netProfitPercent,
            ProfitFactor = grossLossR <= MathUtil.Epsilon ? (grossWinR > 0 ? 10.0 : 0.0) : MathUtil.Clamp(grossWinR / grossLossR, 0, 10),
            ExpectancyR = mean,
            AverageR = mean,
            MedianR = MathUtil.QuantileSorted(sorted, n, 0.5),
            StdDevR = stdDev,
            WinRate = (double)wins / n,
            AverageWinR = wins == 0 ? 0 : grossWinR / wins,
            AverageLossR = n - wins == 0 ? 0 : grossLossR / (n - wins),
            PayoffRatio = MathUtil.SafeDiv(wins == 0 ? 0 : grossWinR / wins, n - wins == 0 ? 0 : grossLossR / (n - wins)),
            MaxDrawdownR = maxDrawdown,
            MaxDrawdownPercent = startingEquity > 0 && peak > 0 ? 100.0 * maxDrawdown / peak : 0,
            AverageDrawdownR = sumDrawdown / n,
            RecoveryFactor = MathUtil.SafeDiv(cumulative, maxDrawdown),
            SharpeLike = MathUtil.SafeDiv(mean, stdDev),
            SortinoLike = MathUtil.SafeDiv(mean, downsideDeviation),
            CalmarLike = MathUtil.SafeDiv(cumulative, maxDrawdown),
            UlcerIndex = Math.Sqrt(sumDrawdownSquared / n),
            RiskOfRuin = riskOfRuin,
            LongestLosingStreak = longestLoss,
            LongestWinningStreak = longestWin,
            AverageHoldingMinutes = sumHolding / n,
            AverageMaeR = sumMae / n,
            AverageMfeR = sumMfe / n,
            AverageCaptureRatio = sumCapture / n,
            ProfitConcentrationTop1 = top1,
            ProfitConcentrationTop5 = top5,
            ProfitConcentrationTop10 = top10,
            LossConcentrationTop5 = worst5,
            ExpectancyExcludingTailWinnersR = expectancyExcludingTail,
        };
    }

    /// <summary>Доля <paramref name="total"/>, приходящаяся на верхний или нижний хвост.</summary>
    private static double TopShare(double[] sortedAscending, int n, double fraction, double total, bool fromTop)
    {
        if (total <= MathUtil.Epsilon) return 0;

        int count = Math.Max(1, (int)Math.Ceiling(n * fraction));
        double sum = 0;

        for (int i = 0; i < count; i++)
        {
            double v = fromTop ? sortedAscending[n - 1 - i] : sortedAscending[i];
            if (fromTop && v > 0) sum += v;
            else if (!fromTop && v < 0) sum += -v;
        }

        return MathUtil.Clamp01(sum / total);
    }
}
