using System;
using System.Collections.Generic;
using Quant.Core.Config;
using Quant.Core.Exits;
using Quant.Core.Features;
using Quant.Core.Numerics;
using Quant.Core.Primitives;
using Quant.Core.Stats;
using Quant.Core.Strategies;

namespace Quant.Core.Adaptation;

/// <summary>
/// Ведёт ВИРТУАЛЬНЫЕ сделки отключённых стратегий — единственный путь, которым отключённая
/// стратегия может вернуться в работу.
///
/// Отключение стратегии создаёт замкнутый круг: без сделок нет статистики, без статистики
/// нет доказательства восстановления, без доказательства нет сделок. Разорвать его можно
/// только одним способом — продолжать вести её сигналы без денег и судить по этому теневому
/// результату (раздел 116).
///
/// Виртуальная сделка проходит тот же путь, что и настоящая: тот же план выхода, те же
/// уровни стопа и цели, тот же стоп по времени. И, что важнее всего, она ПЛАТИТ ИЗДЕРЖКИ:
/// теневая запись без спреда и комиссии выглядела бы лучше живой торговли, и стратегия
/// возвращалась бы на основании прибыли, которой в реальности не было бы.
///
/// Консервативные допущения там, где бар не даёт разрешения по времени:
/// если один бар задел и стоп, и цель — считается, что первым сработал стоп.
/// </summary>
public sealed class ShadowTracker
{
    private sealed class VirtualPosition
    {
        public string TradeId;
        public string SignalId;
        public string SymbolName;
        public string StrategyName;
        public Side Direction;
        public MarketRegime Regime;
        public double Confidence;
        public double EnsembleConfidence;
        public double EntryPrice;
        public double StopPrice;
        public double TargetPrice;
        public double StopDistance;
        public double CostInR;
        public double AtrAtEntry;
        public double SpreadAtEntry;
        public VolatilityBucket VolatilityBucket;
        public DateTime EntryTimeUtc;
        public int BarsHeld;
        public int TimeStopBars;
        public double MaeR;
        public double MfeR;
        public OperatingMode Mode;
    }

    private readonly List<VirtualPosition> _open = new List<VirtualPosition>();
    private readonly AdaptationConfig _config;
    private long _sequence;

    public ShadowTracker(AdaptationConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public int OpenCount => _open.Count;

    /// <summary>Сколько виртуальных позиций открыто по конкретной стратегии.</summary>
    public int OpenCountFor(string strategyName)
    {
        int n = 0;
        for (int i = 0; i < _open.Count; i++)
        {
            if (string.Equals(_open[i].StrategyName, strategyName, StringComparison.Ordinal)) n++;
        }
        return n;
    }

    /// <summary>
    /// Открывает виртуальную позицию по сигналу отключённой стратегии.
    ///
    /// Возвращает false, если сигнал не годится для наблюдения: нет плана выхода, уже открыта
    /// виртуальная позиция по этому символу и стратегии (одна и та же ситуация не должна
    /// засчитываться дважды), либо исчерпан лимит одновременных наблюдений.
    /// </summary>
    public bool Open(
        DateTime nowUtc, string symbolName, StrategySignal signal, ExitPlan plan,
        double entryPrice, MarketRegime regime, FeatureVector features,
        double costInR, OperatingMode mode, double? targetPrice = null)
    {
        if (signal == null || plan == null || !plan.IsValid) return false;
        if (signal.Direction == Side.None || entryPrice <= 0 || plan.StopDistance <= 0) return false;
        if (OpenCountFor(signal.StrategyName) >= _config.MaxConcurrentShadowPositions) return false;

        for (int i = 0; i < _open.Count; i++)
        {
            if (string.Equals(_open[i].SymbolName, symbolName, StringComparison.Ordinal) &&
                string.Equals(_open[i].StrategyName, signal.StrategyName, StringComparison.Ordinal))
            {
                return false;
            }
        }

        _open.Add(new VirtualPosition
        {
            TradeId = "shadow-" + (++_sequence).ToString(System.Globalization.CultureInfo.InvariantCulture),
            SignalId = signal.StrategyName + "|" + symbolName + "|" + nowUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            SymbolName = symbolName,
            StrategyName = signal.StrategyName,
            Direction = signal.Direction,
            Regime = regime,
            Confidence = signal.Confidence,
            EnsembleConfidence = signal.Confidence,
            EntryPrice = entryPrice,
            StopPrice = plan.StopPrice,
            // Цель по умолчанию — первая, как у стратегии, чей путь обратно здесь
            // наблюдается. Сбор доказательств задаёт её явно: там виртуальная сделка обязана
            // описывать ту же ставку, что и формула ожидания, а не лёгкую её часть.
            TargetPrice = targetPrice ?? plan.Target1Price,
            StopDistance = plan.StopDistance,
            CostInR = Math.Max(0, costInR),
            AtrAtEntry = features?.Atr ?? 0,
            SpreadAtEntry = features?.Spread ?? 0,
            VolatilityBucket = features?.VolatilityBucket ?? VolatilityBucket.Normal,
            EntryTimeUtc = nowUtc,
            TimeStopBars = plan.TimeStopBars > 0 ? plan.TimeStopBars : _config.ShadowFallbackTimeStopBars,
            Mode = mode,
        });

        return true;
    }

    /// <summary>
    /// Продвигает виртуальные позиции по закрывшемуся бару их символа и отдаёт закрытые
    /// сделки через <paramref name="onClosed"/>.
    /// </summary>
    public void OnBarClosed(DateTime nowUtc, string symbolName, in Candle bar, Action<TradeRecord> onClosed)
    {
        if (!bar.IsWellFormed) return;

        for (int i = _open.Count - 1; i >= 0; i--)
        {
            VirtualPosition v = _open[i];
            if (!string.Equals(v.SymbolName, symbolName, StringComparison.Ordinal)) continue;

            v.BarsHeld++;

            bool isLong = v.Direction == Side.Long;
            double adverse = isLong ? v.EntryPrice - bar.Low : bar.High - v.EntryPrice;
            double favourable = isLong ? bar.High - v.EntryPrice : v.EntryPrice - bar.Low;

            v.MaeR = Math.Max(v.MaeR, MathUtil.SafeDiv(adverse, v.StopDistance));
            v.MfeR = Math.Max(v.MfeR, MathUtil.SafeDiv(favourable, v.StopDistance));

            bool stopHit = isLong ? bar.Low <= v.StopPrice : bar.High >= v.StopPrice;
            bool targetHit = v.TargetPrice > 0 && (isLong ? bar.High >= v.TargetPrice : bar.Low <= v.TargetPrice);

            // Оба уровня в одном баре — порядок внутри бара неизвестен, и допущение берётся
            // худшее. Иначе теневая статистика систематически завышала бы результат ровно на
            // самых волатильных барах.
            if (stopHit)
            {
                Close(v, i, nowUtc, v.StopPrice, ExitReason.StopLoss, onClosed);
            }
            else if (targetHit)
            {
                Close(v, i, nowUtc, v.TargetPrice, ExitReason.TakeProfit1, onClosed);
            }
            else if (v.BarsHeld >= v.TimeStopBars)
            {
                Close(v, i, nowUtc, bar.Close, ExitReason.TimeStop, onClosed);
            }
        }
    }

    private void Close(VirtualPosition v, int index, DateTime nowUtc, double exitPrice, ExitReason reason, Action<TradeRecord> onClosed)
    {
        _open.RemoveAt(index);

        double perUnit = v.Direction == Side.Long ? exitPrice - v.EntryPrice : v.EntryPrice - exitPrice;
        double grossR = MathUtil.SafeDiv(perUnit, v.StopDistance);
        double netR = grossR - v.CostInR;

        onClosed?.Invoke(new TradeRecord
        {
            TradeId = v.TradeId,
            SignalId = v.SignalId,
            BrokerPositionId = 0,
            SymbolName = v.SymbolName,
            Direction = v.Direction,
            StrategyName = v.StrategyName,
            Regime = v.Regime,
            SignalConfidence = v.Confidence,
            EnsembleConfidence = v.EnsembleConfidence,
            EffectiveVotes = 1,
            PlannedRewardToRisk = MathUtil.SafeDiv(Math.Abs(v.TargetPrice - v.EntryPrice), v.StopDistance),
            Session = SessionClassifier.Classify(v.EntryTimeUtc),
            IsWeekend = SessionClassifier.IsWeekend(v.EntryTimeUtc),
            HourUtc = v.EntryTimeUtc.Hour,
            DayOfWeek = v.EntryTimeUtc.DayOfWeek,
            VolatilityBucket = v.VolatilityBucket,
            AtrAtEntry = v.AtrAtEntry,
            SpreadAtEntry = v.SpreadAtEntry,
            EntryTimeUtc = v.EntryTimeUtc,
            ExitTimeUtc = nowUtc,
            RequestedEntryPrice = v.EntryPrice,
            EntryPrice = v.EntryPrice,
            ExitPrice = exitPrice,
            InitialStopPrice = v.StopPrice,
            InitialTargetPrice = v.TargetPrice,

            // Размер виртуальной сделки нормирован на единицу риска: сравнивается
            // ОЖИДАНИЕ в R, а не деньги, которых здесь нет.
            VolumeInUnits = 1,
            RiskAmount = 1,
            RiskFractionOfEquity = 0,
            GrossProfit = grossR,
            Commission = v.CostInR,
            NetProfit = netR,
            R = netR,

            MaeR = v.MaeR,
            MfeR = v.MfeR,
            ExitReason = reason,
            IsVirtual = true,
            Mode = v.Mode,
        });
    }

    /// <summary>Снимает наблюдение за стратегией, которая снова допущена к торговле.</summary>
    public void Forget(string strategyName)
    {
        for (int i = _open.Count - 1; i >= 0; i--)
        {
            if (string.Equals(_open[i].StrategyName, strategyName, StringComparison.Ordinal)) _open.RemoveAt(i);
        }
    }

    public void Reset()
    {
        _open.Clear();
        _sequence = 0;
    }
}
