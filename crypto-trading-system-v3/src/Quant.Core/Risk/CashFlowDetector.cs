using System;
using Quant.Core.Numerics;

namespace Quant.Core.Risk;

/// <summary>
/// Отличает движение денег по счёту от результата торговли.
///
/// Пополнение и вывод меняют капитал, ничего не говоря о качестве работы системы. Если не
/// различать их, внесённые сто тысяч читаются как заработанные сто тысяч:
///
///   * исторический пик подскакивает до уровня, которого торговля не достигала, и любая
///     последующая просадка меряется от него;
///   * дневная и недельная точки отсчёта остаются на старом уровне, и система видит
///     тысячепроцентную прибыль за день;
///   * защитная постура снимается условием восстановления капитала, которое выполнено
///     деньгами из кармана, а не результатом.
///
/// Распознаётся по расхождению: баланс изменился не на ту величину, которую объясняют
/// закрытые сделки. Мелкие расхождения — своп, финансирование — НЕ считаются движением
/// денег: это настоящие издержки, и вычитать их из отчётности значило бы прятать расходы.
/// </summary>
public sealed class CashFlowDetector
{
    private readonly double _minFractionOfEquity;
    private double _lastBalance;
    private double _lastRealisedTotal;
    private bool _initialised;

    public CashFlowDetector(double minFractionOfEquity = 0.01)
    {
        _minFractionOfEquity = Math.Max(0.0001, minFractionOfEquity);
    }

    /// <summary>Суммарная величина распознанных движений — для журнала и отчётов.</summary>
    public double TotalDetected { get; private set; }

    public int Count { get; private set; }

    /// <summary>
    /// Возвращает величину движения денег по счёту, если оно произошло, иначе ноль.
    /// </summary>
    /// <param name="balance">Текущий баланс счёта.</param>
    /// <param name="equity">Текущий капитал — только для масштаба порога.</param>
    /// <param name="realisedTotal">Суммарная чистая прибыль по ЗАКРЫТЫМ сделкам за всё время.</param>
    public double Detect(double balance, double equity, double realisedTotal)
    {
        if (!MathUtil.IsFinite(balance) || !MathUtil.IsFinite(realisedTotal)) return 0;

        if (!_initialised)
        {
            // Первое наблюдение задаёт отсчёт. Считать стартовый баланс пополнением
            // значило бы объявить движением денег сам факт запуска.
            _lastBalance = balance;
            _lastRealisedTotal = realisedTotal;
            _initialised = true;
            return 0;
        }

        double explained = realisedTotal - _lastRealisedTotal;
        double actual = balance - _lastBalance;
        double unexplained = actual - explained;

        _lastBalance = balance;
        _lastRealisedTotal = realisedTotal;

        double threshold = Math.Max(1e-6, Math.Abs(equity) * _minFractionOfEquity);
        if (Math.Abs(unexplained) < threshold) return 0;

        TotalDetected += unexplained;
        Count++;
        return unexplained;
    }

    public void Reset()
    {
        _initialised = false;
        _lastBalance = 0;
        _lastRealisedTotal = 0;
        TotalDetected = 0;
        Count = 0;
    }
}
