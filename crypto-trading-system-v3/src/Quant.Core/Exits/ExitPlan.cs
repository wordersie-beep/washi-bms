using System;
using Quant.Core.Numerics;
using Quant.Core.Primitives;

namespace Quant.Core.Exits;

/// <summary>How a stop level was chosen, recorded so exit quality can be attributed later.</summary>
public enum StopMethod
{
    None = 0,
    Atr,
    Structure,
    StrategyInvalidation,
    MaeDistribution,
    BrokerMinimum,
}

/// <summary>The complete exit plan for a trade, decided BEFORE entry.</summary>
public sealed class ExitPlan
{
    public static ExitPlan Rejected(NoTradeReason reason, string detail) => new ExitPlan
    {
        IsValid = false,
        RejectionReason = reason,
        Detail = detail,
    };

    public bool IsValid { get; init; }
    public NoTradeReason RejectionReason { get; init; }
    public string Detail { get; init; }

    public double StopPrice { get; init; }
    public StopMethod StopMethod { get; init; }

    /// <summary>Entry-to-stop distance in price units. The definition of 1R.</summary>
    public double StopDistance { get; init; }

    /// <summary>Stop distance expressed in ATR, for the sanity bounds.</summary>
    public double StopInAtr { get; init; }

    public double Target1Price { get; init; }
    public double Target2Price { get; init; }
    public double Target1R { get; init; }
    public double Target2R { get; init; }

    /// <summary>Fraction of the position closed at each target; the remainder is the runner.</summary>
    public double Target1ClosePercent { get; init; }
    public double Target2ClosePercent { get; init; }

    /// <summary>Open profit in R at which the stop moves to NET break-even.</summary>
    public double BreakEvenTriggerR { get; init; }

    /// <summary>
    /// Price at which the trade breaks even AFTER costs, not at entry.
    ///
    /// Moving a stop to the entry price is not break-even: the spread has been crossed, the
    /// commission has been paid, and a "scratch" there is a small, reliable loss. Repeated a
    /// few hundred times that is a material drag, and it is entirely avoidable.
    /// </summary>
    public double NetBreakEvenPrice { get; init; }

    /// <summary>Trailing distance in ATR, chosen by regime.</summary>
    public double TrailDistanceInAtr { get; init; }

    /// <summary>Open profit in R before trailing engages.</summary>
    public double TrailActivationR { get; init; }

    /// <summary>Maximum holding time in signal-timeframe bars.</summary>
    public int TimeStopBars { get; init; }

    /// <summary>Price whose breach means the entry reason no longer holds.</summary>
    public double? InvalidationPrice { get; init; }

    /// <summary>
    /// Отношение прибыли к риску ДЛЯ ВСЕГО ПЛАНА, а не для первой цели.
    ///
    /// Позиция закрывается по частям: доля на первой цели, доля на второй, остаток ведётся
    /// трейлингом. Мерить план одной лишь первой целью — значит систематически занижать его
    /// и не замечать вторую половину собственной конструкции.
    ///
    /// Ошибка была не безобидной: при настройках по умолчанию первая цель равна 1.2R, а
    /// минимально допустимое отношение — 1.3. Ворота отвергали КАЖДОГО кандидата, в любом
    /// рынке, всегда, и система не совершала ни одной сделки — при том что каждый её слой
    /// по отдельности работал правильно.
    ///
    /// Остаток засчитывается по ВТОРОЙ цели, а не по трейлингу: чтобы стать остатком, цена
    /// обязана была до второй цели дойти, а что даст трейлинг дальше — неизвестно, и
    /// приписывать ему прибыль значило бы обещать.
    ///
    /// Это геометрия плана, а не ожидание: вероятность достижения целей оценивает
    /// отдельный слой, и смешивать две вещи в одном числе значило бы считать вероятность
    /// дважды.
    /// </summary>
    public double RewardToRisk
    {
        get
        {
            double atFirst = MathUtil.Clamp01(Target1ClosePercent);
            double atSecond = MathUtil.Clamp01(Target2ClosePercent);
            double runner = Math.Max(0, 1.0 - atFirst - atSecond);

            double total = atFirst + atSecond + runner;
            if (total <= 0 || Target1R <= 0) return Target1R;

            double second = Target2R > Target1R ? Target2R : Target1R;
            return ((atFirst * Target1R) + ((atSecond + runner) * second)) / total;
        }
    }

    /// <summary>Отношение по первой цели — для отчётов и сравнения с планом.</summary>
    public double RewardToRiskAtFirstTarget => Target1R;

    public override string ToString() =>
        IsValid
            ? string.Format("stop {0:F4} ({1}, {2:F2} ATR), targets {3:F4}/{4:F4} at {5:F2}R/{6:F2}R, BE at {7:F2}R",
                StopPrice, StopMethod, StopInAtr, Target1Price, Target2Price, Target1R, Target2R, BreakEvenTriggerR)
            : string.Format("no plan: {0} ({1})", RejectionReason, Detail);
}
