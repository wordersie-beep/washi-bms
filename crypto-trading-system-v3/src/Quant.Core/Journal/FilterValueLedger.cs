using System;
using System.Collections.Generic;
using System.Text;
using Quant.Core.Config;
using Quant.Core.Numerics;
using Quant.Core.Primitives;

namespace Quant.Core.Journal;

/// <summary>Отклонённый сигнал, за которым система продолжает виртуально наблюдать.</summary>
public sealed class RejectedSignal
{
    public DateTime TimeUtc { get; init; }
    public string SymbolName { get; init; }
    public Side Direction { get; init; }
    public string StrategyName { get; init; }
    public NoTradeReason Reason { get; init; }
    public double EntryPrice { get; init; }
    public double StopDistance { get; init; }
    public double TargetDistance { get; init; }

    /// <summary>Сколько баров сигнал уже отслеживается.</summary>
    public int BarsTracked { get; set; }

    public double MaxFavourableR { get; set; }
    public double MaxAdverseR { get; set; }

    /// <summary>true — цель достигнута раньше стопа; false — наоборот; null — ещё не разрешилось.</summary>
    public bool? WouldHaveWon { get; set; }

    public bool IsResolved => WouldHaveWon.HasValue;
}

/// <summary>Накопленная статистика одного фильтра.</summary>
public sealed class FilterStatistics
{
    public NoTradeReason Reason { get; init; }
    public int TotalRejections { get; set; }
    public int ResolvedRejections { get; set; }

    /// <summary>Отклонённые сигналы, которые оказались бы убыточными. Это польза фильтра.</summary>
    public int LossesPrevented { get; set; }

    /// <summary>Отклонённые сигналы, которые оказались бы прибыльными. Это цена фильтра.</summary>
    public int WinsMissed { get; set; }

    /// <summary>Суммарный виртуальный результат отклонённых сигналов, в R.</summary>
    public double ForegoneR { get; set; }

    /// <summary>Доля верных отказов.</summary>
    public double Precision => MathUtil.SafeDiv(LossesPrevented, ResolvedRejections);

    /// <summary>
    /// Чистая ценность фильтра в R: сколько он сэкономил минус сколько стоил.
    ///
    /// Положительное значение — фильтр оправдывает себя. Отрицательное — он режет больше
    /// хороших сделок, чем плохих, и его следует ослабить или убрать (раздел 120).
    /// </summary>
    public double NetValueR => -ForegoneR;

    public override string ToString() =>
        string.Format("{0,-28} n={1,5} resolved={2,5} prevented={3,5} missed={4,5} precision={5,6:P1} net={6,+8:F2}R",
            Reason, TotalRejections, ResolvedRejections, LossesPrevented, WinsMissed, Precision, NetValueR);
}

/// <summary>
/// База ложных срабатываний и учёт ценности фильтров (разделы 119–120).
///
/// Решает проблему, которую почти никто не решает: фильтр всегда ВЫГЛЯДИТ полезным, потому
/// что видно только те убытки, которых он избежал, и не видно той прибыли, которую он не дал
/// заработать. Без этого учёта любая система со временем обрастает фильтрами, каждый из
/// которых «очевидно разумен», а вместе они не дают торговать вообще.
///
/// Механика простая: каждый отклонённый сигнал продолжает отслеживаться виртуально — как если
/// бы сделка была открыта. Через N баров становится известно, дошла бы цена до цели или до
/// стопа. Это превращает вопрос «полезен ли фильтр» из вопроса вкуса в вопрос арифметики.
/// </summary>
public sealed class FilterValueLedger
{
    private readonly AdaptationConfig _config;
    private readonly List<RejectedSignal> _tracking = new List<RejectedSignal>();
    private readonly Dictionary<NoTradeReason, FilterStatistics> _statistics =
        new Dictionary<NoTradeReason, FilterStatistics>();

    public FilterValueLedger(AdaptationConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public IReadOnlyCollection<RejectedSignal> Tracking => _tracking;
    public IReadOnlyDictionary<NoTradeReason, FilterStatistics> Statistics => _statistics;

    /// <summary>
    /// Ставит отклонённый сигнал на виртуальное наблюдение.
    ///
    /// Отказы, которые не являются суждением о рынке — плохие данные, остановка по риску,
    /// лимиты портфеля — не отслеживаются. Они не утверждают, что сделка была плохой; они
    /// утверждают, что система была не в состоянии её взять. Считать их ошибками фильтрации
    /// значит измерять не то.
    /// </summary>
    public void Track(RejectedSignal signal)
    {
        if (signal == null || !IsJudgementAboutTheMarket(signal.Reason)) return;
        if (signal.StopDistance <= 0 || signal.TargetDistance <= 0) return;

        StatisticsFor(signal.Reason).TotalRejections++;

        _tracking.Add(signal);
        if (_tracking.Count > _config.FalsePositiveDatabaseSize)
        {
            _tracking.RemoveRange(0, _tracking.Count - _config.FalsePositiveDatabaseSize);
        }
    }

    /// <summary>
    /// Продвигает все наблюдаемые сигналы по одному бару данного символа.
    /// Вызывается на закрытии бара сигнального таймфрейма.
    /// </summary>
    public void OnBarClosed(string symbolName, double high, double low)
    {
        for (int i = _tracking.Count - 1; i >= 0; i--)
        {
            RejectedSignal s = _tracking[i];
            if (s.IsResolved) continue;
            if (!string.Equals(s.SymbolName, symbolName, StringComparison.Ordinal)) continue;

            s.BarsTracked++;

            double favourable = s.Direction == Side.Long ? high - s.EntryPrice : s.EntryPrice - low;
            double adverse = s.Direction == Side.Long ? s.EntryPrice - low : high - s.EntryPrice;

            double favourableR = favourable / s.StopDistance;
            double adverseR = adverse / s.StopDistance;

            if (favourableR > s.MaxFavourableR) s.MaxFavourableR = favourableR;
            if (adverseR > s.MaxAdverseR) s.MaxAdverseR = adverseR;

            double targetR = s.TargetDistance / s.StopDistance;

            // Если бар задел и цель, и стоп, разрешаем в пользу стопа. Внутрибарный порядок
            // неизвестен, и оптимистичное допущение здесь систематически завышало бы цену
            // фильтров — то есть склоняло бы систему к тому, чтобы их снимать.
            bool hitStop = adverseR >= 1.0;
            bool hitTarget = favourableR >= targetR;

            if (hitStop) Resolve(s, won: false, resultR: -1.0);
            else if (hitTarget) Resolve(s, won: true, resultR: targetR);
            else if (s.BarsTracked >= _config.RejectedSignalFollowUpBars)
            {
                // Не дошло ни туда, ни туда: результат — там, где цена оказалась в конце.
                double openR = s.Direction == Side.Long ? (high + low) / 2 - s.EntryPrice : s.EntryPrice - (high + low) / 2;
                double resultR = openR / s.StopDistance;
                Resolve(s, won: resultR > 0, resultR: resultR);
            }
        }
    }

    private void Resolve(RejectedSignal s, bool won, double resultR)
    {
        s.WouldHaveWon = won;

        FilterStatistics stats = StatisticsFor(s.Reason);
        stats.ResolvedRejections++;
        stats.ForegoneR += resultR;

        if (won) stats.WinsMissed++;
        else stats.LossesPrevented++;
    }

    private FilterStatistics StatisticsFor(NoTradeReason reason)
    {
        if (!_statistics.TryGetValue(reason, out FilterStatistics s))
        {
            s = new FilterStatistics { Reason = reason };
            _statistics[reason] = s;
        }
        return s;
    }

    /// <summary>
    /// Причины отказа, которые представляют собой СУЖДЕНИЕ О РЫНКЕ и потому подлежат проверке.
    ///
    /// Всё остальное — состояние системы, а не мнение о сделке.
    /// </summary>
    private static bool IsJudgementAboutTheMarket(NoTradeReason reason)
    {
        switch (reason)
        {
            case NoTradeReason.LowRegimeConfidence:
            case NoTradeReason.RegimeMismatch:
            case NoTradeReason.LowSignalConfidence:
            case NoTradeReason.LowProbabilityConfidence:
            case NoTradeReason.InsufficientEdge:
            case NoTradeReason.NegativeExpectedValue:
            case NoTradeReason.PoorRiskReward:
            case NoTradeReason.SpreadTooWide:
            case NoTradeReason.VolatilityTooHigh:
            case NoTradeReason.VolatilityTooLow:
            case NoTradeReason.AnomalyDetected:
            case NoTradeReason.ExtremeEvent:
            case NoTradeReason.RecoveryPeriod:
            case NoTradeReason.Chasing:
            case NoTradeReason.InvalidStopPlacement:
            case NoTradeReason.CorrelationLimit:
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Фильтры, которые по накопленным данным вредят больше, чем помогают.
    ///
    /// Возвращает только те, у которых выборка достаточна: фильтр с пятью разрешёнными
    /// отказами не сообщает ни о чём, и ослаблять его на таком основании — это тот же
    /// подгон под шум, от которого фильтры должны защищать.
    /// </summary>
    public IReadOnlyList<FilterStatistics> UnderperformingFilters(int minimumSample = 30)
    {
        var result = new List<FilterStatistics>();
        foreach (KeyValuePair<NoTradeReason, FilterStatistics> kv in _statistics)
        {
            if (kv.Value.ResolvedRejections < minimumSample) continue;
            if (kv.Value.NetValueR < 0) result.Add(kv.Value);
        }

        result.Sort((a, b) => a.NetValueR.CompareTo(b.NetValueR));
        return result;
    }

    public string Report(int minimumSample = 10)
    {
        if (_statistics.Count == 0) return "Учёт ценности фильтров: данных пока нет.";

        var ordered = new List<FilterStatistics>(_statistics.Values);
        ordered.Sort((a, b) => a.NetValueR.CompareTo(b.NetValueR));

        var sb = new StringBuilder();
        sb.Append("Ценность фильтров (положительное net = фильтр окупается):");

        for (int i = 0; i < ordered.Count; i++)
        {
            sb.AppendLine();
            sb.Append("  ").Append(ordered[i]);
            if (ordered[i].ResolvedRejections < minimumSample) sb.Append("  (выборка мала)");
        }

        return sb.ToString();
    }

    public void Reset()
    {
        _tracking.Clear();
        _statistics.Clear();
    }
}
