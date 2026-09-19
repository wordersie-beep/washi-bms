using System;
using System.Collections.Generic;
using Quant.Core.Config;
using Quant.Core.Exits;
using Quant.Core.Numerics;
using Quant.Core.Portfolio;
using Quant.Core.Primitives;
using Quant.Core.Risk;
using Quant.Core.State;

namespace Quant.Core.Execution;

/// <summary>Результат сверки состояния бота с состоянием брокера.</summary>
public sealed class ReconciliationReport
{
    public bool IsConsistent { get; init; }

    /// <summary>Позиции у брокера, о которых бот не знает.</summary>
    public IReadOnlyList<BrokerPosition> Unknown { get; init; }

    /// <summary>Позиции, которые бот считает открытыми, а брокер — нет.</summary>
    public IReadOnlyList<OpenPosition> Missing { get; init; }

    /// <summary>Позиции, у которых расходится объём.</summary>
    public IReadOnlyList<string> VolumeMismatches { get; init; }

    public string Summary
    {
        get
        {
            if (IsConsistent) return "состояния совпадают";
            var parts = new List<string>();
            if (Unknown != null && Unknown.Count > 0) parts.Add($"{Unknown.Count} неизвестных боту позиций у брокера");
            if (Missing != null && Missing.Count > 0) parts.Add($"{Missing.Count} позиций бота отсутствуют у брокера");
            if (VolumeMismatches != null && VolumeMismatches.Count > 0) parts.Add($"{VolumeMismatches.Count} расхождений по объёму");
            return string.Join("; ", parts);
        }
    }
}

/// <summary>
/// Отправка ордеров, обработка отказов и сверка с брокером (разделы 57–60, 144–147).
///
/// Три свойства, ради которых этот слой отделён от принятия решений:
///
///   ИДЕМПОТЕНТНОСТЬ. Отметка об исполнении ставится ДО отправки ордера. Если робот упадёт
///   между отправкой и подтверждением, после перезапуска сигнал будет считаться исполненным,
///   и повторного ордера не будет. Обратный порядок — отметка после подтверждения — выглядит
///   логичнее и создаёт ровно ту дыру, в которую проваливается двойная позиция.
///
///   ОГРАНИЧЕННЫЕ ПОВТОРЫ. Повторяется только то, что имеет смысл повторять, ограниченное
///   число раз. Бесконечный повтор при отказе брокера — это способ превратить сбой связи в
///   шквал ордеров.
///
///   СВЕРКА. Состояние бота периодически сравнивается с состоянием брокера, и расхождение
///   останавливает торговлю. Торговать, не зная своих позиций, — худшее из возможных
///   состояний, и оно должно быть громким, а не тихим.
/// </summary>
public sealed class ExecutionEngine
{
    private readonly ExecutionConfig _config;
    private readonly IBroker _broker;
    private readonly ExecutionQualityTracker _quality;
    private readonly IdempotencyGuard _idempotency;
    private readonly string _instanceId;

    public ExecutionEngine(
        ExecutionConfig config,
        IBroker broker,
        ExecutionQualityTracker quality,
        IdempotencyGuard idempotency,
        string instanceId)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _quality = quality ?? throw new ArgumentNullException(nameof(quality));
        _idempotency = idempotency ?? throw new ArgumentNullException(nameof(idempotency));
        _instanceId = instanceId ?? "default";
    }

    /// <summary>Метка ордера. Префикс позволяет найти свои позиции после перезапуска.</summary>
    public string BuildLabel() => _config.OrderLabelPrefix + "-" + _instanceId;

    public string LabelPrefix => _config.OrderLabelPrefix + "-" + _instanceId;

    /// <summary>
    /// Открывает позицию по кандидату.
    ///
    /// Возвращает null, если сигнал уже исполнялся — вызывающая сторона обязана считать это
    /// успехом, а не поводом попробовать ещё раз.
    /// </summary>
    public BrokerResult Open(
        string signalId,
        string symbolName,
        Side direction,
        double volumeInUnits,
        ExitPlan plan,
        SymbolSpec spec,
        double expectedEntryPrice,
        double predictedSlippage,
        double atr,
        double currentSpread,
        Action<string> log)
    {
        if (_idempotency.HasExecuted(signalId))
        {
            log?.Invoke($"сигнал {signalId} уже исполнялся — ордер не отправляется");
            return null;
        }

        // Отметка ставится ДО отправки. См. комментарий к классу: обратный порядок открывает
        // окно, в котором перезапуск порождает дубль.
        _idempotency.TryMarkExecuted(signalId);

        double maxSlippagePrice = MaxSlippagePrice(atr, currentSpread);
        string label = BuildLabel();

        BrokerResult result = null;
        for (int attempt = 0; attempt <= _config.MaxOrderRetries; attempt++)
        {
            result = _broker.OpenPosition(
                symbolName, direction, volumeInUnits,
                plan.StopPrice, null,
                label, signalId, maxSlippagePrice);

            if (result.IsSuccessful) break;

            if (!result.IsTransient)
            {
                log?.Invoke($"ордер отклонён окончательно: {result.Error}");
                break;
            }

            log?.Invoke($"попытка {attempt + 1} не удалась ({result.Error})");
        }

        if (result == null || !result.IsSuccessful)
        {
            _quality.RecordRejection();

            // Отметка снимается ТОЛЬКО при окончательном отказе — неверный объём, нет
            // денег, торговля запрещена. В этих случаях ордера точно нет, и держать сигнал
            // заблокированным значило бы потерять его навсегда без единой сделки.
            //
            // При повторяемой ошибке — таймаут, обрыв связи, отсутствие ответа — исход
            // НЕИЗВЕСТЕН: сервер мог принять ордер и не успеть ответить. Снятие отметки
            // здесь разрешило бы второй ордер по тому же сигналу, то есть двойную позицию
            // двойного размера. Сигнал остаётся заблокированным, а расхождение, если оно
            // возникло, поймает сверка с брокером.
            bool outcomeIsKnown = result != null && !result.OutcomeUnknown;

            if (outcomeIsKnown)
            {
                _idempotency.Release(signalId);
            }
            else
            {
                log?.Invoke($"сигнал {signalId} остаётся заблокированным: исход ордера неизвестен " +
                            $"({result?.Error ?? "нет ответа"}). Расхождение, если оно есть, покажет сверка.");
            }

            return result;
        }

        _quality.RecordFill(expectedEntryPrice, result.FilledPrice, direction == Side.Long, predictedSlippage, spec.PipSize);
        return result;
    }

    /// <summary>
    /// Slippage ceiling in price units, from whichever of the two relative limits is more
    /// permissive. Falls back to the spread alone when ATR is not yet available, which is
    /// conservative without being paralysing.
    /// </summary>
    public double MaxSlippagePrice(double atr, double currentSpread)
    {
        double fromAtr = atr > 0 ? atr * _config.MaxSlippageInAtr : 0;
        double fromSpread = currentSpread > 0 ? currentSpread * _config.MaxSlippageSpreadMultiple : 0;
        double limit = Math.Max(fromAtr, fromSpread);

        // Zero would mean "reject everything", which is never the intent of a slippage cap.
        return limit > 0 ? limit : double.MaxValue;
    }

    public BrokerResult ClosePartial(long positionId, double volumeInUnits, Action<string> log)
    {
        BrokerResult result = _broker.ClosePosition(positionId, volumeInUnits);
        if (!result.IsSuccessful) log?.Invoke($"частичное закрытие #{positionId} не удалось: {result.Error}");
        return result;
    }

    public BrokerResult CloseAll(long positionId, Action<string> log)
    {
        BrokerResult result = _broker.ClosePosition(positionId, 0);
        if (!result.IsSuccessful) log?.Invoke($"закрытие #{positionId} не удалось: {result.Error}");
        return result;
    }

    public BrokerResult MoveStop(long positionId, double newStopPrice, Action<string> log)
    {
        BrokerResult result = _broker.ModifyStop(positionId, newStopPrice);
        if (!result.IsSuccessful) log?.Invoke($"перенос стопа #{positionId} не удался: {result.Error}");
        return result;
    }

    /// <summary>
    /// Сверяет внутреннее состояние с состоянием брокера (раздел 145).
    ///
    /// Позиции сопоставляются по идентификатору брокера, а не по символу и направлению:
    /// сопоставление по символу молча соединит две разные позиции в одном инструменте и
    /// объявит всё согласованным ровно тогда, когда согласованности нет.
    /// </summary>
    public ReconciliationReport Reconcile(IReadOnlyList<OpenPosition> botPositions)
    {
        IReadOnlyList<BrokerPosition> brokerPositions = _broker.GetOpenPositions(LabelPrefix);

        var byId = new Dictionary<long, BrokerPosition>();
        for (int i = 0; i < brokerPositions.Count; i++) byId[brokerPositions[i].PositionId] = brokerPositions[i];

        var known = new HashSet<long>();
        var missing = new List<OpenPosition>();
        var mismatches = new List<string>();

        if (botPositions != null)
        {
            for (int i = 0; i < botPositions.Count; i++)
            {
                OpenPosition p = botPositions[i];
                if (p == null || p.IsVirtual) continue;

                if (!byId.TryGetValue(p.BrokerPositionId, out BrokerPosition bp))
                {
                    missing.Add(p);
                    continue;
                }

                known.Add(p.BrokerPositionId);

                // Допуск в один шаг объёма: частичное закрытие могло округлиться.
                double tolerance = Math.Max(1e-6, p.InitialVolumeInUnits * 1e-4);
                if (Math.Abs(bp.VolumeInUnits - p.CurrentVolumeInUnits) > tolerance)
                {
                    mismatches.Add($"#{p.BrokerPositionId} {p.SymbolName}: бот {p.CurrentVolumeInUnits:F6}, брокер {bp.VolumeInUnits:F6}");
                }
            }
        }

        var unknown = new List<BrokerPosition>();
        for (int i = 0; i < brokerPositions.Count; i++)
        {
            if (!known.Contains(brokerPositions[i].PositionId)) unknown.Add(brokerPositions[i]);
        }

        return new ReconciliationReport
        {
            IsConsistent = unknown.Count == 0 && missing.Count == 0 && mismatches.Count == 0,
            Unknown = unknown,
            Missing = missing,
            VolumeMismatches = mismatches,
        };
    }

    public AccountSnapshot GetAccount() => _broker.GetAccount();

    public IReadOnlyList<BrokerPosition> GetOpenPositions() => _broker.GetOpenPositions(LabelPrefix);
}
