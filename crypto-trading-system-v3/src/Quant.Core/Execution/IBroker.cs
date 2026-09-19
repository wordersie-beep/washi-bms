using System;
using System.Collections.Generic;
using Quant.Core.Primitives;

namespace Quant.Core.Execution;

/// <summary>Результат одной операции с брокером.</summary>
public sealed class BrokerResult
{
    public static BrokerResult Ok(long positionId, double filledPrice, double filledVolume) => new BrokerResult
    {
        IsSuccessful = true,
        PositionId = positionId,
        FilledPrice = filledPrice,
        FilledVolume = filledVolume,
    };

    public static BrokerResult Fail(string error, bool isTransient) => new BrokerResult
    {
        IsSuccessful = false,
        Error = error,
        IsTransient = isTransient,
    };

    public bool IsSuccessful { get; init; }
    public long PositionId { get; init; }
    public double FilledPrice { get; init; }
    public double FilledVolume { get; init; }
    public string Error { get; init; }

    /// <summary>
    /// Ошибка, которую имеет смысл повторить (таймаут, обрыв связи), в отличие от
    /// окончательной (неверный объём, нет денег, торговля запрещена).
    /// Повторять окончательную ошибку — значит генерировать шум и, возможно, дубли.
    /// </summary>
    public bool IsTransient { get; init; }

    public override string ToString() =>
        IsSuccessful ? $"ok #{PositionId} @{FilledPrice} x{FilledVolume}" : $"fail: {Error}{(IsTransient ? " (повторяемая)" : "")}";
}

/// <summary>Позиция, как её видит брокер. Нужна для сверки (раздел 145).</summary>
public sealed class BrokerPosition
{
    public long PositionId { get; init; }
    public string Label { get; init; }
    public string Comment { get; init; }
    public string SymbolName { get; init; }
    public Side Direction { get; init; }
    public double VolumeInUnits { get; init; }
    public double EntryPrice { get; init; }
    public DateTime EntryTimeUtc { get; init; }
    public double? StopLoss { get; init; }
    public double? TakeProfit { get; init; }
    public double NetProfit { get; init; }
    public double Commissions { get; init; }
    public double Swap { get; init; }
}

/// <summary>
/// Интерфейс к брокеру.
///
/// Существует ровно затем, чтобы вся торговая логика могла быть протестирована без cTrader и
/// чтобы теневой и бумажный режимы были не «особым случаем внутри исполнения», а другой
/// реализацией одного и того же контракта. Режим, реализованный как ветвление внутри боевого
/// кода, неизбежно расходится с ним — и расходится именно там, где это дороже всего.
/// </summary>
public interface IBroker
{
    /// <summary>Открывает рыночную позицию. maxSlippagePrice ограничивает проскальзывание.</summary>
    BrokerResult OpenPosition(
        string symbolName, Side direction, double volumeInUnits,
        double stopPrice, double? takeProfitPrice,
        string label, string comment, double maxSlippagePrice);

    BrokerResult ClosePosition(long positionId, double volumeInUnits);

    BrokerResult ModifyStop(long positionId, double newStopPrice);

    /// <summary>Все открытые позиции, принадлежащие данному экземпляру (по префиксу метки).</summary>
    IReadOnlyList<BrokerPosition> GetOpenPositions(string labelPrefix);

    AccountSnapshot GetAccount();
}
