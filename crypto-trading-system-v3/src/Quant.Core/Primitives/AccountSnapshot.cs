using System;

namespace Quant.Core.Primitives;

/// <summary>Immutable view of account state at one instant.</summary>
public readonly struct AccountSnapshot
{
    public AccountSnapshot(double balance, double equity, double margin, double freeMargin, double? marginLevelPercent, double stopOutLevelPercent, bool isLive, string currency)
    {
        Balance = balance;
        Equity = equity;
        Margin = margin;
        FreeMargin = freeMargin;
        MarginLevelPercent = marginLevelPercent;
        StopOutLevelPercent = NormaliseStopOutLevel(stopOutLevelPercent);
        IsLive = isLive;
        Currency = currency;
    }

    public double Balance { get; }
    public double Equity { get; }
    public double Margin { get; }
    public double FreeMargin { get; }

    /// <summary>Equity/Margin as a percentage. Null when no position is open.</summary>
    public double? MarginLevelPercent { get; }

    /// <summary>
    /// Уровень стоп-аута брокера, В ПРОЦЕНТАХ.
    ///
    /// Платформы сообщают его по-разному: одни долей (0.5), другие процентом (50). Ошибка
    /// в сто раз здесь не безобидна — она уводит порог опасности либо в ноль, и защита
    /// молча выключается, либо в пять тысяч процентов, и торговля останавливается навсегда
    /// без единой сделки. Нормализация происходит на входе, один раз.
    /// </summary>
    public double StopOutLevelPercent { get; }

    /// <summary>
    /// Приводит уровень стоп-аута к процентам.
    ///
    /// Значения не больше единицы трактуются как доля. Это безопасно: стоп-аут ниже одного
    /// процента не встречается — типичные значения от 20 до 100, — поэтому «0.5» означает
    /// половину, а не полпроцента.
    /// </summary>
    public static double NormaliseStopOutLevel(double raw)
    {
        if (double.IsNaN(raw) || double.IsInfinity(raw) || raw <= 0) return 0;
        return raw <= 1.0 ? raw * 100.0 : raw;
    }
    public bool IsLive { get; }
    public string Currency { get; }

    public bool IsUsable => Equity > 0 && !double.IsNaN(Equity) && !double.IsInfinity(Equity);
}
