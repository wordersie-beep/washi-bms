using System;
using System.Collections.Generic;
using cAlgo.API;
using cAlgo.API.Internals;
using Quant.Core.Execution;
using Quant.Core.Primitives;
using Side = Quant.Core.Primitives.Side;

namespace Quant.Bot;

/// <summary>
/// Адаптер cAlgo.API к интерфейсу <see cref="IBroker"/>.
///
/// Единственное место во всей системе, где встречаются типы cAlgo. Всё, что выше, работает с
/// нейтральными типами, и именно поэтому весь торговый цикл покрывается тестами без запуска
/// платформы.
///
/// Здесь же находится и преобразование ошибок платформы в «повторяемые» и «окончательные».
/// Разделение не косметическое: повторять BadVolume бессмысленно и шумно, а не повторять
/// Timeout — значит терять сделки на ровном месте.
/// </summary>
public sealed class CTraderBroker : IBroker
{
    private readonly Robot _robot;

    public CTraderBroker(Robot robot)
    {
        _robot = robot ?? throw new ArgumentNullException(nameof(robot));
    }

    public BrokerResult OpenPosition(
        string symbolName, Side direction, double volumeInUnits,
        double stopPrice, double? takeProfitPrice,
        string label, string comment, double maxSlippagePrice)
    {
        try
        {
            Symbol symbol = _robot.Symbols.GetSymbol(symbolName);
            if (symbol == null) return BrokerResult.Fail("символ недоступен", isTransient: false);

            double normalized = symbol.NormalizeVolumeInUnits(volumeInUnits, RoundingMode.Down);
            if (normalized <= 0) return BrokerResult.Fail("объём ниже минимального", isTransient: false);

            TradeType tradeType = direction == Side.Long ? TradeType.Buy : TradeType.Sell;

            // Стоп передаётся в пипсах от текущей цены: это форма, которую принимает
            // ExecuteMarketOrder, и она же гарантирует, что защита появится вместе с позицией,
            // а не отдельным вызовом после неё. Позиция, пожившая хотя бы мгновение без стопа,
            // — это позиция без стопа ровно в тот момент, когда рынок может дёрнуться.
            double referencePrice = direction == Side.Long ? symbol.Ask : symbol.Bid;
            double stopDistance = Math.Abs(referencePrice - stopPrice);
            double stopLossPips = symbol.PipSize > 0 ? stopDistance / symbol.PipSize : 0;
            if (stopLossPips <= 0) return BrokerResult.Fail("некорректное расстояние до стопа", isTransient: false);

            double? takeProfitPips = null;
            if (takeProfitPrice.HasValue && symbol.PipSize > 0)
            {
                takeProfitPips = Math.Abs(takeProfitPrice.Value - referencePrice) / symbol.PipSize;
            }

            // Market-range ордер ограничивает проскальзывание. Для криптовалют на малых
            // таймфреймах это существенно: обычный рыночный ордер в момент всплеска
            // исполняется там, где решение уже не имеет силы.
            //
            // Лимит приходит сверху в единицах ЦЕНЫ и переводится в пипсы здесь, потому что
            // именно такую форму принимает API. Задавать его в пипсах на уровне
            // конфигурации нельзя: пипс криптовалютного CFD ничего не говорит о масштабе
            // инструмента.
            double marketRangePips = symbol.PipSize > 0 && maxSlippagePrice < double.MaxValue
                ? maxSlippagePrice / symbol.PipSize
                : 1000.0;

            TradeResult result = _robot.ExecuteMarketRangeOrder(
                tradeType, symbolName, normalized, marketRangePips, referencePrice,
                label, stopLossPips, takeProfitPips, comment);

            if (result == null || !result.IsSuccessful)
            {
                return OutcomeIsUnknown(result)
                    ? BrokerResult.Unknown(DescribeError(result))
                    : OutcomeIsUnknown(result)
                    ? BrokerResult.Unknown(DescribeError(result))
                    : BrokerResult.Fail(DescribeError(result), IsTransient(result));
            }

            Position position = result.Position;
            return BrokerResult.Ok(position.Id, position.EntryPrice, position.VolumeInUnits);
        }
        catch (Exception ex)
        {
            // Исключение в момент отправки: приказ мог дойти до биржи, а мог и нет.
            return BrokerResult.Unknown(ex.GetType().Name + ": " + ex.Message);
        }
    }

    public BrokerResult ClosePosition(long positionId, double volumeInUnits)
    {
        try
        {
            Position position = _robot.Positions.FindById((int)positionId);
            if (position == null) return BrokerResult.Fail("позиция не найдена", isTransient: false);

            double closingPrice = position.CurrentPrice;

            // Объём снимается ДО отправки приказа. После закрытия объект Position уже
            // изменён платформой — при полном закрытии он показывает нуль, при частичном
            // остаток. Читать объём оттуда значило бы сообщить движку, что закрыто меньше,
            // чем закрыто на самом деле: учёт разошёлся бы с брокером, сверка объявила бы
            // расхождение и принудительно остановила торговлю.
            double volumeBefore = position.VolumeInUnits;
            bool fullClose = volumeInUnits <= 0 || volumeInUnits >= volumeBefore;

            TradeResult result = fullClose
                ? _robot.ClosePosition(position)
                : _robot.ClosePosition(position, volumeInUnits);

            if (result == null || !result.IsSuccessful)
            {
                return OutcomeIsUnknown(result)
                    ? BrokerResult.Unknown(DescribeError(result))
                    : OutcomeIsUnknown(result)
                    ? BrokerResult.Unknown(DescribeError(result))
                    : BrokerResult.Fail(DescribeError(result), IsTransient(result));
            }

            double closed = fullClose ? volumeBefore : Math.Min(volumeInUnits, volumeBefore);
            return BrokerResult.Ok(positionId, closingPrice, closed);
        }
        catch (Exception ex)
        {
            // Исключение в момент отправки: приказ мог дойти до биржи, а мог и нет.
            return BrokerResult.Unknown(ex.GetType().Name + ": " + ex.Message);
        }
    }

    public BrokerResult ModifyStop(long positionId, double newStopPrice)
    {
        try
        {
            Position position = _robot.Positions.FindById((int)positionId);
            if (position == null) return BrokerResult.Fail("позиция не найдена", isTransient: false);

            // ProtectionType.Absolute — уровни задаются ценой, а не смещением. Перегрузка без
            // этого параметра объявлена устаревшей именно из-за неоднозначности трактовки.
            TradeResult result = _robot.ModifyPosition(
                position, newStopPrice, position.TakeProfit, ProtectionType.Absolute);

            return result != null && result.IsSuccessful
                ? BrokerResult.Ok(positionId, newStopPrice, position.VolumeInUnits)
                : OutcomeIsUnknown(result)
                    ? BrokerResult.Unknown(DescribeError(result))
                    : BrokerResult.Fail(DescribeError(result), IsTransient(result));
        }
        catch (Exception ex)
        {
            // Исключение в момент отправки: приказ мог дойти до биржи, а мог и нет.
            return BrokerResult.Unknown(ex.GetType().Name + ": " + ex.Message);
        }
    }

    public IReadOnlyList<BrokerPosition> GetOpenPositions(string labelPrefix)
    {
        var result = new List<BrokerPosition>();

        try
        {
            foreach (Position p in _robot.Positions)
            {
                if (!string.IsNullOrEmpty(labelPrefix) &&
                    (p.Label == null || !p.Label.StartsWith(labelPrefix, StringComparison.Ordinal)))
                {
                    continue;
                }

                result.Add(new BrokerPosition
                {
                    PositionId = p.Id,
                    Label = p.Label,
                    Comment = p.Comment,
                    SymbolName = p.SymbolName,
                    Direction = p.TradeType == TradeType.Buy ? Side.Long : Side.Short,
                    VolumeInUnits = p.VolumeInUnits,
                    EntryPrice = p.EntryPrice,
                    EntryTimeUtc = DateTime.SpecifyKind(p.EntryTime, DateTimeKind.Utc),
                    StopLoss = p.StopLoss,
                    TakeProfit = p.TakeProfit,
                    NetProfit = p.NetProfit,
                    Commissions = p.Commissions,
                    Swap = p.Swap,
                });
            }
        }
        catch (Exception)
        {
            // Пустой список честнее исключения: вызывающая сторона трактует расхождение как
            // повод остановиться, а это именно то поведение, которое здесь нужно.
        }

        return result;
    }

    public AccountSnapshot GetAccount()
    {
        IAccount a = _robot.Account;
        return new AccountSnapshot(
            a.Balance, a.Equity, a.Margin, a.FreeMargin,
            a.MarginLevel, a.StopOutLevel * 100.0, a.IsLive, a.Asset.Name);
    }

    private static string DescribeError(TradeResult result)
    {
        if (result == null) return "нет ответа от сервера";
        return result.Error.HasValue ? result.Error.Value.ToString() : "неизвестная ошибка";
    }

    /// <summary>
    /// Различает ошибки, которые имеет смысл повторить, и окончательные.
    ///
    /// Повторять BadVolume или NoMoney бессмысленно — условия не изменятся от повтора.
    /// Не повторять Timeout или Disconnected — значит терять сделки из-за секундного сбоя связи.
    /// </summary>
    /// <summary>
    /// Таймаут и обрыв связи означают, что ответа не получено, — а не что приказ не дошёл.
    /// Остальные ошибки платформа возвращает, зная, что приказ отвергнут.
    /// </summary>
    private static bool OutcomeIsUnknown(TradeResult result)
    {
        if (result == null) return true;
        if (!result.Error.HasValue) return true;

        return result.Error.Value == ErrorCode.Timeout ||
               result.Error.Value == ErrorCode.Disconnected ||
               result.Error.Value == ErrorCode.TechnicalError;
    }

    private static bool IsTransient(TradeResult result)
    {
        if (result == null) return true;
        if (!result.Error.HasValue) return true;

        switch (result.Error.Value)
        {
            case ErrorCode.Timeout:
            case ErrorCode.Disconnected:
            case ErrorCode.TechnicalError:
                return true;

            case ErrorCode.BadVolume:
            case ErrorCode.NoMoney:
            case ErrorCode.MarketClosed:
            case ErrorCode.EntityNotFound:
            case ErrorCode.UnknownSymbol:
            case ErrorCode.InvalidStopLossTakeProfit:
            case ErrorCode.InvalidRequest:
            case ErrorCode.NoTradingPermission:
            default:
                return false;
        }
    }
}
