using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
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
/// Закрытая сделка в сохранённом виде.
///
/// Компактная проекция <see cref="Quant.Core.Stats.TradeRecord"/>: только те поля, из
/// которых восстанавливаются все производные структуры — срезы статистики, калибровка,
/// иерархия вероятностей, распределения MAE и MFE. Вычисляемые величины не хранятся, они
/// выводятся заново.
///
/// Отдельный тип, а не сам TradeRecord, потому что формат хранения обязан меняться
/// медленнее внутренней модели: добавление поля в запись сделки не должно ломать чтение
/// состояния, записанного вчерашней версией.
/// </summary>
public sealed class PersistedTrade
{
    [JsonPropertyName("ti")]
    public string TradeId { get; set; }
    [JsonPropertyName("si")]
    public string SignalId { get; set; }
    [JsonPropertyName("sy")]
    public string SymbolName { get; set; }
    [JsonPropertyName("st")]
    public string StrategyName { get; set; }
    [JsonPropertyName("d")]
    public int Direction { get; set; }
    [JsonPropertyName("sk")]
    public int StrategyKind { get; set; }
    [JsonPropertyName("rg")]
    public int Regime { get; set; }
    [JsonPropertyName("se")]
    public int Session { get; set; }
    [JsonPropertyName("vb")]
    public int VolatilityBucket { get; set; }
    [JsonPropertyName("xr")]
    public int ExitReason { get; set; }
    [JsonPropertyName("m")]
    public int Mode { get; set; }
    [JsonPropertyName("h")]
    public int HourUtc { get; set; }
    [JsonPropertyName("dw")]
    public int DayOfWeek { get; set; }
    [JsonPropertyName("we")]
    public bool IsWeekend { get; set; }
    [JsonPropertyName("v")]
    public bool IsVirtual { get; set; }
    [JsonPropertyName("rc")]
    public double RegimeConfidence { get; set; }
    [JsonPropertyName("sc")]
    public double SignalConfidence { get; set; }
    [JsonPropertyName("ec")]
    public double EnsembleConfidence { get; set; }
    [JsonPropertyName("ev")]
    public double EffectiveVotes { get; set; }
    [JsonPropertyName("pw")]
    public double EstimatedWinProbability { get; set; }
    [JsonPropertyName("eR")]
    public double ExpectedValueR { get; set; }
    [JsonPropertyName("rr")]
    public double PlannedRewardToRisk { get; set; }
    [JsonPropertyName("at")]
    public double AtrAtEntry { get; set; }
    [JsonPropertyName("sp")]
    public double SpreadAtEntry { get; set; }
    [JsonPropertyName("t0")]
    public DateTime EntryTimeUtc { get; set; }
    [JsonPropertyName("t1")]
    public DateTime ExitTimeUtc { get; set; }
    [JsonPropertyName("p0")]
    public double EntryPrice { get; set; }
    [JsonPropertyName("pq")]
    public double RequestedEntryPrice { get; set; }
    [JsonPropertyName("p1")]
    public double ExitPrice { get; set; }
    [JsonPropertyName("ps")]
    public double InitialStopPrice { get; set; }
    [JsonPropertyName("pt")]
    public double InitialTargetPrice { get; set; }
    [JsonPropertyName("vo")]
    public double VolumeInUnits { get; set; }
    [JsonPropertyName("ra")]
    public double RiskAmount { get; set; }
    [JsonPropertyName("rf")]
    public double RiskFractionOfEquity { get; set; }
    [JsonPropertyName("gp")]
    public double GrossProfit { get; set; }
    [JsonPropertyName("cm")]
    public double Commission { get; set; }
    [JsonPropertyName("sw")]
    public double Swap { get; set; }
    [JsonPropertyName("np")]
    public double NetProfit { get; set; }
    [JsonPropertyName("sl")]
    public double EntrySlippage { get; set; }
    [JsonPropertyName("r")]
    public double R { get; set; }
    [JsonPropertyName("ma")]
    public double MaeR { get; set; }
    public double MfeR { get; set; }
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
    public int SchemaVersion { get; set; } = 2;

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

    /// <summary>
    /// История закрытых сделок.
    ///
    /// Без неё каждый перезапуск обнулял всё, чему система научилась: срезы статистики,
    /// калибровку вероятностей, распределения MAE и MFE, обнаружение деградации стратегий.
    /// В облаке, где процесс перезапускается регулярно, адаптивный слой не накапливал бы
    /// ничего и вечно работал по априорным значениям — то есть существовал бы формально.
    ///
    /// Хранится ограниченное число последних сделок: состояние должно оставаться
    /// компактным, а статистика и так взвешена в пользу недавнего.
    /// </summary>
    public List<PersistedTrade> Trades { get; set; } = new List<PersistedTrade>();
}
