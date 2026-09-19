using System;
using Quant.Core.Primitives;
using Quant.Core.Stats;

namespace Quant.Core.State;

/// <summary>
/// Перевод закрытой сделки в сохраняемый вид и обратно.
///
/// Отдельный тип, потому что это граница между внутренней моделью и форматом хранения.
/// Пока перевод явный, добавление поля в <see cref="TradeRecord"/> не ломает чтение
/// состояния, записанного предыдущей версией: неизвестное поле просто отсутствует и
/// получает значение по умолчанию.
/// </summary>
public static class TradeHistoryMapper
{
    public static PersistedTrade ToPersisted(TradeRecord t) => t == null ? null : new PersistedTrade
    {
        TradeId = t.TradeId,
        SignalId = t.SignalId,
        SymbolName = t.SymbolName,
        StrategyName = t.StrategyName,
        Direction = (int)t.Direction,
        StrategyKind = (int)t.StrategyKind,
        Regime = (int)t.Regime,
        Session = (int)t.Session,
        VolatilityBucket = (int)t.VolatilityBucket,
        ExitReason = (int)t.ExitReason,
        Mode = (int)t.Mode,
        HourUtc = t.HourUtc,
        DayOfWeek = (int)t.DayOfWeek,
        IsWeekend = t.IsWeekend,
        IsVirtual = t.IsVirtual,
        RegimeConfidence = t.RegimeConfidence,
        SignalConfidence = t.SignalConfidence,
        EnsembleConfidence = t.EnsembleConfidence,
        EffectiveVotes = t.EffectiveVotes,
        EstimatedWinProbability = t.EstimatedWinProbability,
        ExpectedValueR = t.ExpectedValueR,
        PlannedRewardToRisk = t.PlannedRewardToRisk,
        AtrAtEntry = t.AtrAtEntry,
        SpreadAtEntry = t.SpreadAtEntry,
        EntryTimeUtc = t.EntryTimeUtc,
        ExitTimeUtc = t.ExitTimeUtc,
        EntryPrice = t.EntryPrice,
        RequestedEntryPrice = t.RequestedEntryPrice,
        ExitPrice = t.ExitPrice,
        InitialStopPrice = t.InitialStopPrice,
        InitialTargetPrice = t.InitialTargetPrice,
        VolumeInUnits = t.VolumeInUnits,
        RiskAmount = t.RiskAmount,
        RiskFractionOfEquity = t.RiskFractionOfEquity,
        GrossProfit = t.GrossProfit,
        Commission = t.Commission,
        Swap = t.Swap,
        NetProfit = t.NetProfit,
        EntrySlippage = t.EntrySlippage,
        R = t.R,
        MaeR = t.MaeR,
        MfeR = t.MfeR,
    };

    public static TradeRecord FromPersisted(PersistedTrade p) => p == null ? null : new TradeRecord
    {
        TradeId = p.TradeId,
        SignalId = p.SignalId,
        SymbolName = p.SymbolName,
        StrategyName = p.StrategyName,
        Direction = (Side)p.Direction,
        StrategyKind = (StrategyKind)p.StrategyKind,
        Regime = (MarketRegime)p.Regime,
        Session = (SessionKind)p.Session,
        VolatilityBucket = (VolatilityBucket)p.VolatilityBucket,
        ExitReason = (ExitReason)p.ExitReason,
        Mode = (OperatingMode)p.Mode,
        HourUtc = p.HourUtc,
        DayOfWeek = (DayOfWeek)p.DayOfWeek,
        IsWeekend = p.IsWeekend,
        IsVirtual = p.IsVirtual,
        RegimeConfidence = p.RegimeConfidence,
        SignalConfidence = p.SignalConfidence,
        EnsembleConfidence = p.EnsembleConfidence,
        EffectiveVotes = p.EffectiveVotes,
        EstimatedWinProbability = p.EstimatedWinProbability,
        ExpectedValueR = p.ExpectedValueR,
        PlannedRewardToRisk = p.PlannedRewardToRisk,
        AtrAtEntry = p.AtrAtEntry,
        SpreadAtEntry = p.SpreadAtEntry,
        EntryTimeUtc = p.EntryTimeUtc,
        ExitTimeUtc = p.ExitTimeUtc,
        EntryPrice = p.EntryPrice,
        RequestedEntryPrice = p.RequestedEntryPrice,
        ExitPrice = p.ExitPrice,
        InitialStopPrice = p.InitialStopPrice,
        InitialTargetPrice = p.InitialTargetPrice,
        VolumeInUnits = p.VolumeInUnits,
        RiskAmount = p.RiskAmount,
        RiskFractionOfEquity = p.RiskFractionOfEquity,
        GrossProfit = p.GrossProfit,
        Commission = p.Commission,
        Swap = p.Swap,
        NetProfit = p.NetProfit,
        EntrySlippage = p.EntrySlippage,
        R = p.R,
        MaeR = p.MaeR,
        MfeR = p.MfeR,
    };
}
