using System;
using System.Collections.Generic;
using Quant.Core.Primitives;

namespace Quant.Core.State;

/// <summary>Сохранённое состояние одной позиции, достаточное для её восстановления.</summary>
public sealed class PersistedPosition
{
    public string TradeId { get; set; }
    public string SignalId { get; set; }
    public long BrokerPositionId { get; set; }
    public string Label { get; set; }
    public string SymbolName { get; set; }
    public int Direction { get; set; }
    public string StrategyName { get; set; }
    public int Regime { get; set; }
    public DateTime EntryTimeUtc { get; set; }
    public double EntryPrice { get; set; }
    public double InitialVolumeInUnits { get; set; }
    public double CurrentVolumeInUnits { get; set; }
    public double InitialStopPrice { get; set; }
    public double CurrentStopPrice { get; set; }
    public double Target1Price { get; set; }
    public double Target2Price { get; set; }
    public double RiskPerUnit { get; set; }
    public double RiskAmount { get; set; }
    public double RiskFractionOfEquity { get; set; }
    public double EnsembleConfidence { get; set; }
    public double EstimatedWinProbability { get; set; }
    public double ExpectedValueR { get; set; }
    public double PlannedRewardToRisk { get; set; }
    public double MaxFavourableExcursionR { get; set; }
    public double MaxAdverseExcursionR { get; set; }
    public double PeakOpenProfitR { get; set; }
    public bool Target1Filled { get; set; }
    public bool Target2Filled { get; set; }
    public bool BreakEvenApplied { get; set; }
    public int BarsHeld { get; set; }
    public double AtrAtEntry { get; set; }
    public double SpreadAtEntry { get; set; }
    public double InvalidationPrice { get; set; }
    public bool HasInvalidation { get; set; }
}

/// <summary>Сохранённое состояние одной стратегии.</summary>
public sealed class PersistedStrategy
{
    public string Name { get; set; }
    public int Status { get; set; }
    public double Weight { get; set; }
    public DateTime? DisabledAtUtc { get; set; }
    public DateTime? RecoveringSinceUtc { get; set; }
    public int ShadowTrades { get; set; }
    public int DisableCount { get; set; }
    public string LastStatusReason { get; set; }
}

/// <summary>
/// Полный снимок состояния бота, переживающий рестарт (раздел 146).
///
/// Что именно здесь лежит, определяется одним вопросом: что будет неверно, если это
/// потерять? Потеря счётчика серии убытков означает, что после перезапуска система снова
/// считает себя свежей и торгует полным размером сразу после четырёх убытков подряд. Потеря
/// дневной точки отсчёта означает, что дневной лимит убытка молча обнуляется. Потеря пика
/// капитала означает, что просадка считается от нового, более низкого уровня — и лимит
/// просадки перестаёт что-либо ограничивать.
///
/// Каждое из этих полей существует потому, что его потеря снимает один из защитных барьеров
/// ровно в тот момент, когда он нужнее всего — после сбоя.
/// </summary>
public sealed class BotState
{
    /// <summary>Версия схемы. Несовпадение — состояние отбрасывается, а не интерпретируется наугад.</summary>
    public int SchemaVersion { get; set; } = 1;

    public string InstanceId { get; set; }
    public DateTime SavedAtUtc { get; set; }
    public int Mode { get; set; }

    // --- Риск ---------------------------------------------------------------------------
    public int RiskState { get; set; }
    public int ConsecutiveLosses { get; set; }
    public int ConsecutiveWins { get; set; }
    public DateTime? CooldownUntilUtc { get; set; }
    public DateTime DayStartUtc { get; set; }
    public double DayStartEquity { get; set; }
    public DateTime WeekStartUtc { get; set; }
    public double WeekStartEquity { get; set; }
    public double AllTimePeakEquity { get; set; }
    public double LastEquity { get; set; }

    // --- Позиции и идемпотентность ----------------------------------------------------
    public List<PersistedPosition> Positions { get; set; } = new List<PersistedPosition>();

    /// <summary>
    /// Идентификаторы сигналов, по которым уже отправлялся ордер.
    ///
    /// Это единственное, что защищает от повторного открытия той же сделки после
    /// перезапуска, случившегося между отправкой ордера и его подтверждением
    /// (разделы 54, 147).
    /// </summary>
    public List<string> ExecutedSignalIds { get; set; } = new List<string>();

    public List<PersistedStrategy> Strategies { get; set; } = new List<PersistedStrategy>();

    /// <summary>Время последнего обработанного сигнального бара по каждому символу.</summary>
    public Dictionary<string, DateTime> LastSignalBarUtc { get; set; } = new Dictionary<string, DateTime>();

    public long TotalTradesRecorded { get; set; }
}
