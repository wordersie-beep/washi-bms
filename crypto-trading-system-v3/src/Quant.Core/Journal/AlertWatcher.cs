using System;
using System.Collections.Generic;
using Quant.Core.Adaptation;
using Quant.Core.Primitives;

namespace Quant.Core.Journal;

/// <summary>
/// Снимок наблюдаемого состояния системы в один момент.
/// </summary>
public sealed class WatchedState
{
    public RiskState Risk { get; init; }
    public NoTradeReason Blocking { get; init; }
    public bool HaltedByReconciliation { get; init; }
    public int OpenPositions { get; init; }
    public long TradesClosed { get; init; }
    public double Equity { get; init; }
    public IReadOnlyDictionary<string, bool> MarketOpen { get; init; }
    public IReadOnlyDictionary<string, StrategyStatus> Strategies { get; init; }
    public IReadOnlyDictionary<string, bool> SymbolReady { get; init; }
}

/// <summary>
/// Сообщает об ИЗМЕНЕНИЯХ состояния, а не о состоянии.
///
/// Существует потому, что наблюдение раз в минуту и печать раз в минуту — разные вещи.
/// Печатать одну и ту же строку каждую минуту значит заливать журнал 1440 строками в сутки,
/// среди которых теряется единственная важная: та, где что-то изменилось. Человек,
/// попросивший следить каждую минуту, хочет узнать о событии сразу, а не читать
/// подтверждения, что ничего не произошло.
///
/// Поэтому опрос идёт ежеминутно, а в журнал попадает только разница.
/// </summary>
public sealed class AlertWatcher
{
    private WatchedState _previous;

    /// <summary>Сколько раз состояние проверялось — для отчёта о тишине.</summary>
    public long Checks { get; private set; }

    /// <summary>Когда в последний раз что-то менялось.</summary>
    public DateTime LastChangeUtc { get; private set; } = DateTime.MinValue;

    public IReadOnlyList<string> Compare(DateTime nowUtc, WatchedState current)
    {
        Checks++;

        var alerts = new List<string>();
        if (current == null) return alerts;

        WatchedState previous = _previous;
        _previous = current;

        // Первый вызов: сравнивать не с чем. Стартовое состояние печатается отдельно,
        // при запуске, и дублировать его тревогой значит начинать работу с ложного сигнала.
        if (previous == null) return alerts;

        if (previous.Risk != current.Risk)
        {
            bool worse = current.Risk > previous.Risk;
            alerts.Add($"{(worse ? "!! РИСК" : "риск")}: {previous.Risk} -> {current.Risk}" +
                       (current.Blocking != NoTradeReason.None ? $" [{current.Blocking}]" : ""));
        }
        else if (previous.Blocking != current.Blocking && current.Blocking != NoTradeReason.None)
        {
            alerts.Add($"!! БЛОКИРОВКА: {current.Blocking}");
        }

        if (!previous.HaltedByReconciliation && current.HaltedByReconciliation)
        {
            alerts.Add("!! ОСТАНОВКА ПО СВЕРКЕ: расхождение с брокером, торговля прекращена");
        }
        else if (previous.HaltedByReconciliation && !current.HaltedByReconciliation)
        {
            alerts.Add("сверка сошлась, торговля возобновлена");
        }

        if (previous.OpenPositions != current.OpenPositions)
        {
            alerts.Add($"позиций {previous.OpenPositions} -> {current.OpenPositions}");
        }

        if (previous.TradesClosed != current.TradesClosed)
        {
            long closed = current.TradesClosed - previous.TradesClosed;
            double change = current.Equity - previous.Equity;
            alerts.Add($"закрыто сделок: {closed}, капитал {previous.Equity:F2} -> {current.Equity:F2} ({change:+0.00;-0.00})");
        }

        AddMapChanges(alerts, previous.MarketOpen, current.MarketOpen,
            (symbol, was, now) => now ? $"{symbol}: рынок ОТКРЫЛСЯ" : $"{symbol}: рынок ЗАКРЫЛСЯ");

        AddMapChanges(alerts, previous.SymbolReady, current.SymbolReady,
            (symbol, was, now) => now
                ? $"{symbol}: прогрев завершён, инструмент в работе"
                : $"{symbol}: данных снова не хватает");

        if (previous.Strategies != null && current.Strategies != null)
        {
            foreach (KeyValuePair<string, StrategyStatus> kv in current.Strategies)
            {
                if (!previous.Strategies.TryGetValue(kv.Key, out StrategyStatus was)) continue;
                if (was == kv.Value) continue;

                bool worse = kv.Value == StrategyStatus.Disabled || kv.Value == StrategyStatus.Degraded;
                alerts.Add($"{(worse ? "!! " : "")}стратегия {kv.Key}: {was} -> {kv.Value}");
            }
        }

        if (alerts.Count > 0) LastChangeUtc = nowUtc;
        return alerts;
    }

    private static void AddMapChanges(
        List<string> alerts,
        IReadOnlyDictionary<string, bool> previous,
        IReadOnlyDictionary<string, bool> current,
        Func<string, bool, bool, string> describe)
    {
        if (previous == null || current == null) return;

        foreach (KeyValuePair<string, bool> kv in current)
        {
            if (!previous.TryGetValue(kv.Key, out bool was)) continue;
            if (was == kv.Value) continue;

            alerts.Add(describe(kv.Key, was, kv.Value));
        }
    }

    public void Reset()
    {
        _previous = null;
        Checks = 0;
        LastChangeUtc = DateTime.MinValue;
    }
}
