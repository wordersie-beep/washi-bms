using System;
using System.Collections.Generic;
using Quant.Core.Config;
using Quant.Core.Ev;
using Quant.Core.Numerics;
using Quant.Core.Primitives;

namespace Quant.Core.Execution;

/// <summary>
/// Брокер для теневого и бумажного режимов (разделы 105–106).
///
/// Ключевое: исполнение здесь НЕ бесплатное. Виртуальная сделка платит спред, комиссию и
/// смоделированное проскальзывание, потому что теневой режим, в котором сделки исполняются
/// идеально, доказывает только то, что стратегия работала бы в мире без издержек — а такой
/// вывод не имеет отношения к решению о выходе в реальную торговлю.
///
/// Разница между Shadow и Paper здесь в том, что Paper дополнительно моделирует задержку и
/// возможность отказа, то есть проверяет ещё и устойчивость логики к тому, что ордер может
/// не исполниться.
/// </summary>
public sealed class SimulatedBroker : IBroker
{
    private readonly ExecutionConfig _config;
    private readonly CostModel _costModel;
    private readonly Pcg32 _rng;
    private readonly Dictionary<long, BrokerPosition> _positions = new Dictionary<long, BrokerPosition>();
    private readonly bool _simulateFailures;

    private long _nextPositionId = 1;
    private AccountSnapshot _account;

    public SimulatedBroker(ExecutionConfig config, CostModel costModel, AccountSnapshot initialAccount, bool simulateFailures, ulong seed = 20260919UL)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _costModel = costModel ?? throw new ArgumentNullException(nameof(costModel));
        _account = initialAccount;
        _simulateFailures = simulateFailures;
        _rng = new Pcg32(seed);
    }

    /// <summary>Текущая котировка по символу; задаётся движком перед каждой операцией.</summary>
    public Dictionary<string, Quote> Quotes { get; } = new Dictionary<string, Quote>(StringComparer.Ordinal);

    /// <summary>Условия стресса по символу — влияют на моделируемое проскальзывание.</summary>
    public HashSet<string> StressedSymbols { get; } = new HashSet<string>(StringComparer.Ordinal);

    public int RejectedOrders { get; private set; }

    public BrokerResult OpenPosition(
        string symbolName, Side direction, double volumeInUnits,
        double stopPrice, double? takeProfitPrice,
        string label, string comment, double maxSlippagePrice)
    {
        if (!Quotes.TryGetValue(symbolName, out Quote quote) || !quote.IsWellFormed)
        {
            return BrokerResult.Fail("нет котировки", isTransient: true);
        }

        if (volumeInUnits <= 0) return BrokerResult.Fail("некорректный объём", isTransient: false);

        // Бумажный режим изредка отказывает — чтобы путь обработки отказа действительно
        // исполнялся, а не оставался непроверенным до первого реального сбоя.
        if (_simulateFailures && _rng.NextDouble() < 0.02)
        {
            RejectedOrders++;
            return BrokerResult.Fail("смоделированный отказ брокера", isTransient: true);
        }

        bool stressed = StressedSymbols.Contains(symbolName);
        double slippage = _costModel.OneWaySlippage(quote.Spread, stressed);

        // Проскальзывание всегда против нас: покупаем выше аска, продаём ниже бида.
        double basePrice = direction == Side.Long ? quote.Ask : quote.Bid;
        double fillPrice = direction == Side.Long ? basePrice + slippage : basePrice - slippage;

        // Ограничение проскальзывания: за его пределами ордер не исполняется, как и должен
        // вести себя market-range ордер.
        if (maxSlippagePrice > 0 && slippage > maxSlippagePrice)
        {
            RejectedOrders++;
            return BrokerResult.Fail($"проскальзывание {slippage:F4} превысило лимит {maxSlippagePrice:F4}", isTransient: true);
        }

        long id = _nextPositionId++;
        _positions[id] = new BrokerPosition
        {
            PositionId = id,
            Label = label,
            Comment = comment,
            SymbolName = symbolName,
            Direction = direction,
            VolumeInUnits = volumeInUnits,
            EntryPrice = fillPrice,
            EntryTimeUtc = quote.TimeUtc,
            StopLoss = stopPrice,
            TakeProfit = takeProfitPrice,
        };

        return BrokerResult.Ok(id, fillPrice, volumeInUnits);
    }

    public BrokerResult ClosePosition(long positionId, double volumeInUnits)
    {
        if (!_positions.TryGetValue(positionId, out BrokerPosition p))
        {
            return BrokerResult.Fail("позиция не найдена", isTransient: false);
        }

        if (!Quotes.TryGetValue(p.SymbolName, out Quote quote) || !quote.IsWellFormed)
        {
            return BrokerResult.Fail("нет котировки", isTransient: true);
        }

        double closeVolume = volumeInUnits <= 0 || volumeInUnits >= p.VolumeInUnits ? p.VolumeInUnits : volumeInUnits;

        bool stressed = StressedSymbols.Contains(p.SymbolName);
        double slippage = _costModel.OneWaySlippage(quote.Spread, stressed);

        double basePrice = p.Direction == Side.Long ? quote.Bid : quote.Ask;
        double fillPrice = p.Direction == Side.Long ? basePrice - slippage : basePrice + slippage;

        double grossPerUnit = p.Direction == Side.Long ? fillPrice - p.EntryPrice : p.EntryPrice - fillPrice;
        double gross = grossPerUnit * closeVolume;

        _account = new AccountSnapshot(
            _account.Balance + gross, _account.Equity + gross, _account.Margin, _account.FreeMargin + gross,
            _account.MarginLevelPercent, _account.StopOutLevelPercent, _account.IsLive, _account.Currency);

        if (closeVolume >= p.VolumeInUnits) _positions.Remove(positionId);
        else
        {
            _positions[positionId] = new BrokerPosition
            {
                PositionId = p.PositionId, Label = p.Label, Comment = p.Comment,
                SymbolName = p.SymbolName, Direction = p.Direction,
                VolumeInUnits = p.VolumeInUnits - closeVolume,
                EntryPrice = p.EntryPrice, EntryTimeUtc = p.EntryTimeUtc,
                StopLoss = p.StopLoss, TakeProfit = p.TakeProfit,
            };
        }

        return BrokerResult.Ok(positionId, fillPrice, closeVolume);
    }

    public BrokerResult ModifyStop(long positionId, double newStopPrice)
    {
        if (!_positions.TryGetValue(positionId, out BrokerPosition p))
        {
            return BrokerResult.Fail("позиция не найдена", isTransient: false);
        }

        _positions[positionId] = new BrokerPosition
        {
            PositionId = p.PositionId, Label = p.Label, Comment = p.Comment,
            SymbolName = p.SymbolName, Direction = p.Direction, VolumeInUnits = p.VolumeInUnits,
            EntryPrice = p.EntryPrice, EntryTimeUtc = p.EntryTimeUtc,
            StopLoss = newStopPrice, TakeProfit = p.TakeProfit,
        };

        return BrokerResult.Ok(positionId, newStopPrice, p.VolumeInUnits);
    }

    public IReadOnlyList<BrokerPosition> GetOpenPositions(string labelPrefix)
    {
        var result = new List<BrokerPosition>();
        foreach (KeyValuePair<long, BrokerPosition> kv in _positions)
        {
            if (string.IsNullOrEmpty(labelPrefix) || (kv.Value.Label != null && kv.Value.Label.StartsWith(labelPrefix, StringComparison.Ordinal)))
            {
                result.Add(kv.Value);
            }
        }
        return result;
    }

    public AccountSnapshot GetAccount() => _account;

    public void SetAccount(AccountSnapshot account) => _account = account;

    /// <summary>
    /// Прогоняет бар через открытые позиции, исполняя стопы и тейки.
    /// Возвращает закрытые позиции с ценой исполнения.
    /// </summary>
    public IReadOnlyList<(BrokerPosition Position, double ExitPrice, bool WasStop)> ProcessBar(
        string symbolName, double high, double low)
    {
        var closed = new List<(BrokerPosition, double, bool)>();
        var ids = new List<long>(_positions.Keys);

        for (int i = 0; i < ids.Count; i++)
        {
            if (!_positions.TryGetValue(ids[i], out BrokerPosition p)) continue;
            if (!string.Equals(p.SymbolName, symbolName, StringComparison.Ordinal)) continue;

            bool stopHit = p.StopLoss.HasValue &&
                (p.Direction == Side.Long ? low <= p.StopLoss.Value : high >= p.StopLoss.Value);

            bool targetHit = p.TakeProfit.HasValue &&
                (p.Direction == Side.Long ? high >= p.TakeProfit.Value : low <= p.TakeProfit.Value);

            // Если бар задел и стоп, и цель, исполняем СТОП. Внутрибарный порядок неизвестен,
            // и оптимистичное допущение здесь — прямой путь к бэктесту, который невозможно
            // повторить в реальности.
            if (stopHit)
            {
                closed.Add((p, p.StopLoss.Value, true));
                _positions.Remove(ids[i]);
            }
            else if (targetHit)
            {
                closed.Add((p, p.TakeProfit.Value, false));
                _positions.Remove(ids[i]);
            }
        }

        return closed;
    }
}
