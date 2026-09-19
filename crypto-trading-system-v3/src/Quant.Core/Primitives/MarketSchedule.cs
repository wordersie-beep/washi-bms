using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Quant.Core.Primitives;

/// <summary>Одна торговая сессия недели, в UTC.</summary>
public readonly struct TradingSessionWindow
{
    public TradingSessionWindow(DayOfWeek startDay, TimeSpan startTime, DayOfWeek endDay, TimeSpan endTime)
    {
        StartDay = startDay;
        StartTime = startTime;
        EndDay = endDay;
        EndTime = endTime;
    }

    public DayOfWeek StartDay { get; }
    public TimeSpan StartTime { get; }
    public DayOfWeek EndDay { get; }
    public TimeSpan EndTime { get; }

    /// <summary>Минут от начала недели (понедельник 00:00 UTC) до открытия сессии.</summary>
    public int StartMinuteOfWeek => MarketSchedule.MinuteOfWeek(StartDay, StartTime);

    /// <summary>
    /// Минут от начала недели до закрытия. Сессия, переходящая через границу недели,
    /// разворачивается за её пределы, чтобы длина считалась как разность, а не как
    /// отрицательное число.
    /// </summary>
    public int EndMinuteOfWeek
    {
        get
        {
            int end = MarketSchedule.MinuteOfWeek(EndDay, EndTime);
            return end <= StartMinuteOfWeek ? end + MarketSchedule.MinutesPerWeek : end;
        }
    }

    public int DurationMinutes => EndMinuteOfWeek - StartMinuteOfWeek;

    public override string ToString() =>
        $"{MarketSchedule.DayRu(StartDay)} {StartTime:hh\\:mm} – {MarketSchedule.DayRu(EndDay)} {EndTime:hh\\:mm}";
}

/// <summary>
/// Недельное расписание торгов инструмента.
///
/// Существует по одной причине: система спроектирована под круглосуточный рынок, и
/// инструмент с перерывами ломает её молча, а не громко. Ни один слой не выдаёт ошибку —
/// просто на выходных не приходят бары, разрыв читается как потеря данных, режим
/// классифицируется по рваной серии, а человек видит бота, который «не работает», без
/// единой строки о причине.
///
/// Расписание превращает это из догадки в факт, который печатается в журнал при старте
/// и виден в признаке жизни.
/// </summary>
public sealed class MarketSchedule
{
    public const int MinutesPerWeek = 7 * 24 * 60;

    /// <summary>
    /// Допуск на «почти круглосуточный» рынок, минуты в неделю.
    ///
    /// Крипто-символы у многих брокеров имеют технический перерыв в несколько минут в
    /// сутки на расчёт свопов. Считать такой инструмент непригодным было бы неверно:
    /// он торгуется 24/7 в любом практическом смысле.
    /// </summary>
    public const int ContinuousToleranceMinutes = 8 * 60;

    private readonly List<TradingSessionWindow> _sessions;

    private MarketSchedule(List<TradingSessionWindow> sessions, bool isKnown)
    {
        _sessions = sessions;
        IsKnown = isKnown;
    }

    /// <summary>Расписание неизвестно — платформа его не дала.</summary>
    public static MarketSchedule Unknown { get; } = new MarketSchedule(new List<TradingSessionWindow>(), false);

    /// <summary>Расписание непрерывной торговли, для тестов и симуляции.</summary>
    public static MarketSchedule Continuous { get; } = new MarketSchedule(
        new List<TradingSessionWindow>
        {
            new TradingSessionWindow(DayOfWeek.Sunday, TimeSpan.Zero, DayOfWeek.Sunday, TimeSpan.Zero),
        },
        true);

    public static MarketSchedule FromSessions(IEnumerable<TradingSessionWindow> sessions)
    {
        if (sessions == null) return Unknown;

        var list = new List<TradingSessionWindow>();
        foreach (TradingSessionWindow s in sessions)
        {
            if (s.DurationMinutes > 0) list.Add(s);
        }

        if (list.Count == 0) return Unknown;

        list.Sort((a, b) => a.StartMinuteOfWeek.CompareTo(b.StartMinuteOfWeek));
        return new MarketSchedule(list, true);
    }

    public bool IsKnown { get; }
    public IReadOnlyList<TradingSessionWindow> Sessions => _sessions;

    /// <summary>Открытых минут в неделю. Неизвестное расписание считается непрерывным.</summary>
    public int OpenMinutesPerWeek
    {
        get
        {
            if (!IsKnown) return MinutesPerWeek;

            // Склейка пересекающихся и смежных окон: складывать длины напрямую нельзя,
            // перекрытие дало бы больше недели.
            bool[] open = BuildOpenMask();
            int total = 0;
            for (int i = 0; i < MinutesPerWeek; i++)
            {
                if (open[i]) total++;
            }
            return total;
        }
    }

    public double OpenHoursPerWeek => OpenMinutesPerWeek / 60.0;

    /// <summary>
    /// Торгуется ли инструмент практически круглосуточно.
    ///
    /// Неизвестное расписание трактуется как круглосуточное СОЗНАТЕЛЬНО: иначе отсутствие
    /// данных у платформы превращалось бы в предупреждение о проблеме, которой может не
    /// быть. Неопределённость сообщается отдельно, через <see cref="IsKnown"/>.
    /// </summary>
    public bool IsContinuous => !IsKnown || OpenMinutesPerWeek >= MinutesPerWeek - ContinuousToleranceMinutes;

    /// <summary>Открыт ли рынок в указанный момент UTC.</summary>
    public bool IsOpenAt(DateTime utc)
    {
        if (!IsKnown) return true;
        return BuildOpenMask()[MinuteOfWeek(utc)];
    }

    /// <summary>
    /// Сколько минут до ближайшего открытия. Ноль — рынок открыт сейчас;
    /// -1 — расписание не даёт открытия вообще.
    /// </summary>
    public int MinutesUntilOpen(DateTime utc)
    {
        if (!IsKnown) return 0;

        bool[] open = BuildOpenMask();
        int start = MinuteOfWeek(utc);
        if (open[start]) return 0;

        for (int i = 1; i <= MinutesPerWeek; i++)
        {
            if (open[(start + i) % MinutesPerWeek]) return i;
        }
        return -1;
    }

    /// <summary>Перерывы в торговле длиннее указанного порога.</summary>
    public IReadOnlyList<(DateTime FromWeekStart, DateTime ToWeekStart, int Minutes)> ClosuresLongerThan(int minutes)
    {
        var result = new List<(DateTime, DateTime, int)>();
        if (!IsKnown) return result;

        bool[] open = BuildOpenMask();
        int i = 0;
        while (i < MinutesPerWeek)
        {
            if (open[i]) { i++; continue; }

            int from = i;
            while (i < MinutesPerWeek && !open[i]) i++;

            int length = i - from;
            if (length >= minutes) result.Add((WeekStart.AddMinutes(from), WeekStart.AddMinutes(i), length));
        }

        return result;
    }

    /// <summary>Сколько минут промежутка приходится на закрытый рынок.</summary>
    public int ClosedMinutesBetween(DateTime fromUtc, DateTime toUtc)
    {
        if (!IsKnown || toUtc <= fromUtc) return 0;

        int span = (int)Math.Ceiling((toUtc - fromUtc).TotalMinutes);
        if (span > MinutesPerWeek) return 0;

        bool[] open = BuildOpenMask();
        int from = MinuteOfWeek(fromUtc);

        int closed = 0;
        for (int i = 0; i < span; i++)
        {
            if (!open[(from + i) % MinutesPerWeek]) closed++;
        }
        return closed;
    }

    /// <summary>
    /// Объясняется ли разрыв в данных плановым перерывом.
    ///
    /// Нужно, чтобы отличать отсутствие баров из-за закрытого рынка от потери данных.
    /// Первое — норма и ни о чём не говорит; второе означает, что решения принимаются по
    /// неполной картине, и торговать нельзя.
    ///
    /// Требовать, чтобы ВЕСЬ промежуток был закрыт, нельзя: его границы — это последний
    /// пришедший бар и первый новый, и оба приходятся на открытый рынок. Поэтому проверка
    /// в другом: объясняет ли перерыв разрыв целиком, с точностью до
    /// <paramref name="toleranceMinutes"/>.
    /// </summary>
    public bool ExplainsGap(DateTime fromUtc, DateTime toUtc, int toleranceMinutes)
    {
        if (!IsKnown || toUtc <= fromUtc) return false;

        int span = (int)Math.Ceiling((toUtc - fromUtc).TotalMinutes);
        int closed = ClosedMinutesBetween(fromUtc, toUtc);

        return closed > 0 && span - closed <= Math.Max(0, toleranceMinutes);
    }

    /// <summary>Человекочитаемый вывод для журнала.</summary>
    public string Describe()
    {
        if (!IsKnown) return "расписание неизвестно — платформа его не сообщила";
        if (IsContinuous) return $"круглосуточно ({OpenHoursPerWeek:F0} ч/нед)";

        var sb = new StringBuilder();
        sb.Append(OpenHoursPerWeek.ToString("F1", CultureInfo.InvariantCulture));
        sb.Append(" ч/нед, перерывы: ");

        IReadOnlyList<(DateTime From, DateTime To, int Minutes)> closures = ClosuresLongerThan(30);
        for (int i = 0; i < closures.Count && i < 4; i++)
        {
            if (i > 0) sb.Append("; ");
            sb.Append($"{DayRu(closures[i].From.DayOfWeek)} {closures[i].From:HH\\:mm} – " +
                      $"{DayRu(closures[i].To.DayOfWeek)} {closures[i].To:HH\\:mm}");
        }

        if (closures.Count > 4) sb.Append($" и ещё {closures.Count - 4}");
        return sb.ToString();
    }

    private bool[] BuildOpenMask()
    {
        var open = new bool[MinutesPerWeek];
        for (int s = 0; s < _sessions.Count; s++)
        {
            TradingSessionWindow w = _sessions[s];
            int start = w.StartMinuteOfWeek;
            int length = Math.Min(w.DurationMinutes, MinutesPerWeek);

            for (int i = 0; i < length; i++) open[(start + i) % MinutesPerWeek] = true;
        }
        return open;
    }

    /// <summary>Понедельник 00:00 UTC произвольной опорной недели — только для вывода.</summary>
    private static readonly DateTime WeekStart = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static int MinuteOfWeek(DayOfWeek day, TimeSpan time)
    {
        // Неделя начинается с понедельника: воскресенье в DayOfWeek равно нулю, и без
        // сдвига воскресная сессия оказалась бы раньше понедельничной.
        int dayIndex = ((int)day + 6) % 7;
        int minutes = dayIndex * 24 * 60 + (int)time.TotalMinutes;
        return ((minutes % MinutesPerWeek) + MinutesPerWeek) % MinutesPerWeek;
    }

    public static int MinuteOfWeek(DateTime utc) => MinuteOfWeek(utc.DayOfWeek, utc.TimeOfDay);

    public static string DayRu(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "пн",
        DayOfWeek.Tuesday => "вт",
        DayOfWeek.Wednesday => "ср",
        DayOfWeek.Thursday => "чт",
        DayOfWeek.Friday => "пт",
        DayOfWeek.Saturday => "сб",
        _ => "вс",
    };

    public override string ToString() => Describe();
}
