using System;
using System.Text;

namespace Quant.Core.Stats;

/// <summary>Полный статистический отчёт (раздел 165).</summary>
public sealed class PerformanceReport
{
    public int Trades { get; init; }
    public double TradesPerDay { get; init; }
    public double NetProfit { get; init; }
    public double NetProfitPercent { get; init; }
    public double ProfitFactor { get; init; }
    public double ExpectancyR { get; init; }
    public double AverageR { get; init; }
    public double MedianR { get; init; }
    public double StdDevR { get; init; }
    public double WinRate { get; init; }
    public double AverageWinR { get; init; }
    public double AverageLossR { get; init; }
    public double PayoffRatio { get; init; }
    public double MaxDrawdownR { get; init; }
    public double MaxDrawdownPercent { get; init; }
    public double AverageDrawdownR { get; init; }
    public double RecoveryFactor { get; init; }
    public double SharpeLike { get; init; }
    public double SortinoLike { get; init; }
    public double CalmarLike { get; init; }
    public double UlcerIndex { get; init; }
    public double RiskOfRuin { get; init; }
    public int LongestLosingStreak { get; init; }
    public int LongestWinningStreak { get; init; }
    public double AverageHoldingMinutes { get; init; }
    public double AverageMaeR { get; init; }
    public double AverageMfeR { get; init; }
    public double AverageCaptureRatio { get; init; }

    /// <summary>Доля всей прибыли, созданная лучшим 1% сделок (раздел 100).</summary>
    public double ProfitConcentrationTop1 { get; init; }
    public double ProfitConcentrationTop5 { get; init; }
    public double ProfitConcentrationTop10 { get; init; }

    /// <summary>Доля всего убытка, созданная худшим 5% сделок (раздел 101).</summary>
    public double LossConcentrationTop5 { get; init; }

    /// <summary>Матожидание, пересчитанное без лучшего 5% сделок (раздел 99).</summary>
    public double ExpectancyExcludingTailWinnersR { get; init; }

    public string Render()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=============== СТАТИСТИЧЕСКИЙ ОТЧЁТ ===============");
        sb.AppendFormat("  Сделок                    {0,12}   ({1:F2} в день)", Trades, TradesPerDay).AppendLine();
        sb.AppendFormat("  Чистая прибыль            {0,12:F2}   ({1:F2}%)", NetProfit, NetProfitPercent).AppendLine();
        sb.AppendFormat("  Profit factor             {0,12:F3}", ProfitFactor).AppendLine();
        sb.AppendFormat("  Матожидание               {0,12:F4} R", ExpectancyR).AppendLine();
        sb.AppendFormat("  Средний / медианный R     {0,12:F4} / {1:F4}", AverageR, MedianR).AppendLine();
        sb.AppendFormat("  Ст. отклонение R          {0,12:F4}", StdDevR).AppendLine();
        sb.AppendLine("  ---");
        sb.AppendFormat("  Винрейт                   {0,12:P2}", WinRate).AppendLine();
        sb.AppendFormat("  Средняя прибыль / убыток  {0,12:F3} / {1:F3} R", AverageWinR, AverageLossR).AppendLine();
        sb.AppendFormat("  Payoff ratio              {0,12:F3}", PayoffRatio).AppendLine();
        sb.AppendLine("  ---");
        sb.AppendFormat("  Макс. просадка            {0,12:F2} R   ({1:F2}%)", MaxDrawdownR, MaxDrawdownPercent).AppendLine();
        sb.AppendFormat("  Средняя просадка          {0,12:F2} R", AverageDrawdownR).AppendLine();
        sb.AppendFormat("  Recovery factor           {0,12:F3}", RecoveryFactor).AppendLine();
        sb.AppendFormat("  Ulcer index               {0,12:F3}", UlcerIndex).AppendLine();
        sb.AppendLine("  ---");
        sb.AppendFormat("  Sharpe-подобный           {0,12:F3}", SharpeLike).AppendLine();
        sb.AppendFormat("  Sortino-подобный          {0,12:F3}", SortinoLike).AppendLine();
        sb.AppendFormat("  Calmar-подобный           {0,12:F3}", CalmarLike).AppendLine();
        sb.AppendFormat("  Риск разорения            {0,12:P2}", RiskOfRuin).AppendLine();
        sb.AppendLine("  ---");
        sb.AppendFormat("  Серия убытков / побед     {0,12} / {1}", LongestLosingStreak, LongestWinningStreak).AppendLine();
        sb.AppendFormat("  Среднее удержание         {0,12:F1} мин", AverageHoldingMinutes).AppendLine();
        sb.AppendFormat("  Средний MAE / MFE         {0,12:F3} / {1:F3} R", AverageMaeR, AverageMfeR).AppendLine();
        sb.AppendFormat("  Захват движения           {0,12:P1}", AverageCaptureRatio).AppendLine();
        sb.AppendLine("  ---");
        sb.AppendFormat("  Концентрация прибыли      топ 1%: {0:P1}   топ 5%: {1:P1}   топ 10%: {2:P1}",
            ProfitConcentrationTop1, ProfitConcentrationTop5, ProfitConcentrationTop10).AppendLine();
        sb.AppendFormat("  Концентрация убытка       худшие 5%: {0:P1}", LossConcentrationTop5).AppendLine();
        sb.AppendFormat("  Матожидание без хвоста    {0,12:F4} R", ExpectancyExcludingTailWinnersR).AppendLine();

        if (ProfitConcentrationTop5 > 0.60)
        {
            sb.AppendLine("  >>> ВНИМАНИЕ: большая часть прибыли создана несколькими сделками.");
            sb.AppendLine("      Такой результат почти не отличим от удачи и не должен считаться доказательством преимущества.");
        }

        sb.Append("====================================================");
        return sb.ToString();
    }

    public override string ToString() => Render();
}
