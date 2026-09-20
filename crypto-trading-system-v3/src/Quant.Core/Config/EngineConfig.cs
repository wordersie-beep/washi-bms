using System;
using System.Collections.Generic;
using Quant.Core.Primitives;

namespace Quant.Core.Config;

/// <summary>
/// Every tunable in the system, in one validated place.
///
/// Two rules govern this file:
///   1. Defaults are conservative. A freshly constructed config should be survivable, not
///      profitable; profitability is earned through validation, not through defaults.
///   2. Nothing downstream reads a magic number. If a constant matters, it lives here, so
///      that sensitivity analysis (spec section 137) can actually reach it.
/// </summary>
public sealed class EngineConfig
{
    public DataConfig Data { get; set; } = new DataConfig();
    public RegimeConfig Regime { get; set; } = new RegimeConfig();
    public StrategyConfig Strategy { get; set; } = new StrategyConfig();
    public ProbabilityConfig Probability { get; set; } = new ProbabilityConfig();
    public EvConfig Ev { get; set; } = new EvConfig();
    public PortfolioConfig Portfolio { get; set; } = new PortfolioConfig();
    public RiskConfig Risk { get; set; } = new RiskConfig();
    public SizingConfig Sizing { get; set; } = new SizingConfig();
    public ExitConfig Exit { get; set; } = new ExitConfig();
    public ExecutionConfig Execution { get; set; } = new ExecutionConfig();
    public AdaptationConfig Adaptation { get; set; } = new AdaptationConfig();

    public OperatingMode Mode { get; set; } = OperatingMode.Shadow;

    /// <summary>
    /// Explicit acknowledgement required before the system will place orders on a live
    /// account. Mode alone is not enough: a mis-set parameter must not be able to move real
    /// money (see <see cref="Validate"/> and the mode guard in the decision engine).
    /// </summary>
    public bool LiveTradingAcknowledged { get; set; }

    /// <summary>Deterministic seed for every stochastic component.</summary>
    public ulong RandomSeed { get; set; } = 20260919UL;

    /// <summary>
    /// Sets the signal and context timeframes together, rebuilding the aggregated set and
    /// keeping the correlation timeframe consistent with them.
    ///
    /// Exists so the three cannot drift apart: setting SignalTimeframe alone used to leave
    /// a timeframe set that might not contain it, and a correlation timeframe that might be
    /// faster than it.
    /// </summary>
    public void UseTimeframes(Tf signal, Tf context)
    {
        Data.UseTimeframes(signal, context, Portfolio.CorrelationTimeframe);
        if ((int)Portfolio.CorrelationTimeframe < (int)signal) Portfolio.CorrelationTimeframe = signal;
    }

    /// <summary>
    /// Validates the configuration and returns the problems found. An empty list means the
    /// configuration is internally consistent; it says nothing about whether it is profitable.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        Data.Validate(problems);
        Regime.Validate(problems);
        Strategy.Validate(problems);
        Probability.Validate(problems);
        Ev.Validate(problems);
        Portfolio.Validate(problems);
        Risk.Validate(problems);
        Sizing.Validate(problems);
        Exit.Validate(problems);
        Execution.Validate(problems);
        Adaptation.Validate(problems);

        if (Mode == OperatingMode.Live && !LiveTradingAcknowledged)
        {
            problems.Add("Mode is Live but LiveTradingAcknowledged is false. Live trading is refused.");
        }

        if (Sizing.RiskPerTradePercent > Risk.HardMaxRiskPerTradePercent)
        {
            problems.Add($"RiskPerTradePercent ({Sizing.RiskPerTradePercent:F3}) exceeds the hard cap ({Risk.HardMaxRiskPerTradePercent:F3}).");
        }

        if (Risk.MaxTotalOpenRiskPercent < Sizing.RiskPerTradePercent)
        {
            problems.Add("MaxTotalOpenRiskPercent is below RiskPerTradePercent; no trade could ever be opened.");
        }

        // Достижимость ворот по отношению прибыли к риску.
        //
        // Проверка межсекционная, и именно поэтому её не было: обе величины по отдельности
        // разумны, противоречие возникает только вместе. Ценой была система, которая не
        // могла совершить ни одной сделки ни при каких условиях.
        double runnerShare = Math.Max(0, 1.0 - Exit.Target1ClosePercent - Exit.Target2ClosePercent);
        double achievableRr = (Exit.Target1ClosePercent * Exit.Target1R) +
                              ((Exit.Target2ClosePercent + runnerShare) * Math.Max(Exit.Target1R, Exit.Target2R));

        if (achievableRr < Ev.MinRewardToRisk)
        {
            problems.Add(
                $"Планируемое отношение прибыли к риску ({achievableRr:F2}) ниже минимально " +
                $"допустимого ({Ev.MinRewardToRisk:F2}): ни один кандидат не пройдёт этот фильтр никогда.");
        }

        if (Risk.DailyLossLimitPercent >= Risk.WeeklyLossLimitPercent)
        {
            problems.Add("WeeklyLossLimitPercent must exceed DailyLossLimitPercent.");
        }

        return problems;
    }
}

public sealed class DataConfig
{
    /// <summary>Timeframes aggregated for every traded symbol. Decisions never rest on M1 alone.</summary>
    public Tf[] Timeframes { get; set; } = { Tf.M1, Tf.M3, Tf.M5, Tf.M15, Tf.H1, Tf.H4 };

    /// <summary>Timeframe whose bar close drives the decision cycle.</summary>
    public Tf SignalTimeframe { get; set; } = Tf.M5;

    /// <summary>Timeframe supplying trend context to the signal timeframe.</summary>
    public Tf ContextTimeframe { get; set; } = Tf.H1;

    /// <summary>Bars retained per timeframe. Bounded so a 24/7 process cannot grow without limit.</summary>
    public int BarHistory { get; set; } = 500;

    /// <summary>Ticks retained for microstructure statistics.</summary>
    public int TickHistory { get; set; } = 600;

    /// <summary>Window for spread percentile and median statistics, in ticks.</summary>
    public int SpreadWindow { get; set; } = 400;

    /// <summary>Window for percentile-ranking volatility and volume, in bars.</summary>
    public int PercentileWindow { get; set; } = 250;

    /// <summary>Minimum closed bars on the signal timeframe before the symbol may be traded.</summary>
    public int MinBarsBeforeTrading { get; set; } = 200;

    /// <summary>
    /// Minimum closed bars on ANY timeframe before that timeframe is considered usable.
    /// Applies to the context and enrichment timeframes, which must warm up but must not be
    /// held to the signal timeframe's much larger bar requirement.
    /// </summary>
    public int MinBarsPerTimeframe { get; set; } = 60;

    /// <summary>
    /// A gap larger than this many multiples of the bar interval is treated as missing data
    /// rather than as a quiet market.
    /// </summary>
    public double MaxBarGapMultiple { get; set; } = 2.5;

    /// <summary>Quotes older than this are stale; the symbol stops accepting new entries.</summary>
    public double MaxQuoteAgeSeconds { get; set; } = 30;

    /// <summary>A single-tick price jump beyond this many ATR is rejected as a bad print.</summary>
    public double MaxTickJumpInAtr { get; set; } = 3.0;

    public int AtrPeriods { get; set; } = 14;
    public int AdxPeriods { get; set; } = 14;
    public int RsiPeriods { get; set; } = 14;
    public int BollingerPeriods { get; set; } = 20;
    public double BollingerDeviations { get; set; } = 2.0;
    public int EmaFastPeriods { get; set; } = 21;
    public int EmaSlowPeriods { get; set; } = 55;
    public int EmaTrendPeriods { get; set; } = 200;
    public int SlopePeriods { get; set; } = 20;
    public int DonchianPeriods { get; set; } = 20;
    public int SwingConfirmationBars { get; set; } = 2;
    public int RealizedVolPeriods { get; set; } = 60;
    public int VolumeWindow { get; set; } = 100;

    /// <summary>
    /// Builds the set of timeframes to aggregate, given the three that must be present.
    ///
    /// Anything FASTER than the signal timeframe is dropped: no layer reads such a series,
    /// and on a one-minute signal an extra series is real work on every bar. The signal,
    /// context and correlation timeframes are always included, in ascending order.
    /// </summary>
    public static Tf[] BuildTimeframeSet(Tf signal, Tf context, Tf correlation)
    {
        var all = new[] { Tf.M1, Tf.M3, Tf.M5, Tf.M15, Tf.H1, Tf.H4 };
        var result = new List<Tf>();

        for (int i = 0; i < all.Length; i++)
        {
            if ((int)all[i] >= (int)signal) result.Add(all[i]);
        }

        foreach (Tf required in new[] { signal, context, correlation })
        {
            if (!result.Contains(required)) result.Add(required);
        }

        result.Sort((a, b) => ((int)a).CompareTo((int)b));
        return result.ToArray();
    }

    /// <summary>
    /// Applies a signal/context pair, rebuilding the timeframe set and keeping the
    /// correlation timeframe from running faster than the signal — on a faster series it
    /// would measure microstructure noise rather than the relationship between instruments.
    /// </summary>
    public void UseTimeframes(Tf signal, Tf context, Tf correlation)
    {
        SignalTimeframe = signal;
        ContextTimeframe = context;

        Tf effectiveCorrelation = (int)correlation < (int)signal ? signal : correlation;
        Timeframes = BuildTimeframeSet(signal, context, effectiveCorrelation);
    }

    internal void Validate(List<string> problems)
    {
        if (Timeframes == null || Timeframes.Length == 0) problems.Add("At least one timeframe is required.");
        if (BarHistory < 250) problems.Add("BarHistory must be at least 250 to support the longest indicator window.");
        if (MinBarsBeforeTrading < EmaTrendPeriods) problems.Add("MinBarsBeforeTrading must cover the slowest EMA.");
        if (MinBarsPerTimeframe < EmaSlowPeriods) problems.Add("MinBarsPerTimeframe must cover EmaSlowPeriods.");
        if (MinBarsPerTimeframe > MinBarsBeforeTrading) problems.Add("MinBarsPerTimeframe must not exceed MinBarsBeforeTrading.");
        if (PercentileWindow < 50) problems.Add("PercentileWindow below 50 makes percentile ranks meaningless.");
        if (EmaFastPeriods >= EmaSlowPeriods) problems.Add("EmaFastPeriods must be shorter than EmaSlowPeriods.");
        if (MaxQuoteAgeSeconds <= 0) problems.Add("MaxQuoteAgeSeconds must be positive.");
        if (Array.IndexOf(Timeframes, SignalTimeframe) < 0) problems.Add("SignalTimeframe must be one of the aggregated timeframes.");
        if (Array.IndexOf(Timeframes, ContextTimeframe) < 0) problems.Add("ContextTimeframe must be one of the aggregated timeframes.");
        if ((int)ContextTimeframe <= (int)SignalTimeframe) problems.Add("ContextTimeframe must be slower than SignalTimeframe.");
    }
}

public sealed class RegimeConfig
{
    /// <summary>ADX above this counts as a trending market.</summary>
    public double TrendAdxThreshold { get; set; } = 25;

    /// <summary>ADX below this counts as a non-trending market.</summary>
    public double RangeAdxThreshold { get; set; } = 18;

    /// <summary>Regression R-squared required before a slope is called a trend.</summary>
    public double TrendFitThreshold { get; set; } = 0.55;

    /// <summary>ATR percentile above which volatility counts as high.</summary>
    public double HighVolPercentile { get; set; } = 0.80;

    /// <summary>ATR percentile below which volatility counts as low.</summary>
    public double LowVolPercentile { get; set; } = 0.20;

    /// <summary>ATR percentile above which conditions are treated as panic, not merely volatile.</summary>
    public double PanicVolPercentile { get; set; } = 0.97;

    /// <summary>Spread percentile above which liquidity is treated as stressed.</summary>
    public double LiquidityStressSpreadPercentile { get; set; } = 0.95;

    /// <summary>Consecutive confirmations required before the reported regime flips.</summary>
    public int ConfirmationBars { get; set; } = 2;

    /// <summary>Bars after a regime change during which risk stays suppressed.</summary>
    public int TransitionBars { get; set; } = 6;

    /// <summary>Risk multiplier applied while a regime transition is unconfirmed.</summary>
    public double TransitionRiskMultiplier { get; set; } = 0.5;

    /// <summary>
    /// Абсолютный ПОЛ уверенности режима. Ниже него не торгуем никогда, каким бы ясным
    /// чтение ни казалось на фоне остальных: «лучшее из плохого» — всё ещё плохое.
    /// </summary>
    public double MinConfidenceToTrade { get; set; } = 0.30;

    /// <summary>
    /// Доля собственных чтений классификатора, которые считаются НЕДОСТАТОЧНО ясными.
    ///
    /// 0.70 означает: торговать в верхних тридцати процентах по ясности режима.
    ///
    /// Заменяет абсолютный порог, потому что уверенность классификатора — это мера отрыва
    /// победителя от второго места, и её шкала зависит от числа режимов, инструмента и
    /// таймфрейма. Круглое число на такой шкале задаёт разное намерение на разных
    /// инструментах: там, где медиана 0.35, порог 0.55 пропускает верхнюю десятую часть и
    /// в сочетании с прочими фильтрами даёт НОЛЬ сделок; там, где медиана 0.70, тот же
    /// порог не ограничивает ничего.
    /// </summary>
    public double RegimeClarityPercentile { get; set; } = 0.70;

    /// <summary>Порог до набора выборки, пока о распределении говорить рано.</summary>
    public double UncalibratedConfidenceThreshold { get; set; } = 0.45;

    /// <summary>Чтений классификатора, после которых распределению можно доверять.</summary>
    public int ClaritySampleMinimum { get; set; } = 100;

    internal void Validate(List<string> problems)
    {
        if (RangeAdxThreshold >= TrendAdxThreshold) problems.Add("RangeAdxThreshold must be below TrendAdxThreshold.");
        if (LowVolPercentile >= HighVolPercentile) problems.Add("LowVolPercentile must be below HighVolPercentile.");
        if (PanicVolPercentile <= HighVolPercentile) problems.Add("PanicVolPercentile must exceed HighVolPercentile.");
        if (ConfirmationBars < 1) problems.Add("ConfirmationBars must be at least 1.");
        if (TransitionRiskMultiplier <= 0 || TransitionRiskMultiplier > 1) problems.Add("TransitionRiskMultiplier must be in (0, 1].");
        if (MinConfidenceToTrade < 0 || MinConfidenceToTrade > 1) problems.Add("MinConfidenceToTrade must be in [0, 1].");
        if (RegimeClarityPercentile < 0 || RegimeClarityPercentile >= 1) problems.Add("RegimeClarityPercentile must be in [0, 1).");
        if (UncalibratedConfidenceThreshold < MinConfidenceToTrade) problems.Add("UncalibratedConfidenceThreshold must not be below the absolute floor.");
        if (ClaritySampleMinimum < 20) problems.Add("ClaritySampleMinimum below 20 turns noise into a calibration.");
    }
}

public sealed class StrategyConfig
{
    /// <summary>Minimum raw strategy confidence (0..1) before a vote is considered at all.</summary>
    public double MinStrategyConfidence { get; set; } = 0.50;

    /// <summary>Minimum blended ensemble confidence (0..1) required to proceed.</summary>
    public double MinEnsembleConfidence { get; set; } = 0.55;

    /// <summary>
    /// Hardest cap on any single strategy's share of ensemble influence (spec section 76).
    /// Prevents one lucky strategy from becoming the whole system.
    /// </summary>
    public double MaxSingleStrategyWeight { get; set; } = 0.40;

    /// <summary>Floor on a live strategy's weight, so a temporary slump cannot silently zero it.</summary>
    public double MinActiveStrategyWeight { get; set; } = 0.05;

    /// <summary>
    /// Correlation above which two strategies' votes are treated as one piece of evidence
    /// rather than two (spec sections 77-78).
    /// </summary>
    public double SignalCorrelationThreshold { get; set; } = 0.70;

    /// <summary>Observations used for the rolling strategy-signal correlation estimate.</summary>
    public int SignalCorrelationWindow { get; set; } = 100;

    /// <summary>
    /// Наблюдений, ниже которых эмпирическая корреляция сигналов не считается известной.
    ///
    /// До этого порога действует только структурная оценка — по объявленным семействам
    /// признаков. Корреляция по десятку наблюдений это шум, и принимать её за знание
    /// значит то завышать, то занижать число независимых голосов случайным образом.
    /// </summary>
    public int MinSamplesForSignalCorrelation { get; set; } = 30;

    /// <summary>Regime-fit score below which a strategy abstains rather than votes weakly.</summary>
    public double MinRegimeFit { get; set; } = 0.25;

    /// <summary>Bars after which an unacted signal is stale (spec section 53).</summary>
    public int SignalExpiryBars { get; set; } = 2;

    /// <summary>
    /// A candidate entry is "chasing" once price has already travelled this many ATR from
    /// the level that triggered the signal (spec section 51).
    /// </summary>
    public double MaxChaseInAtr { get; set; } = 1.0;

    /// <summary>A bar whose range exceeds this many ATR triggers the FOMO filter (spec section 52).</summary>
    public double FomoBarRangeInAtr { get; set; } = 2.5;

    internal void Validate(List<string> problems)
    {
        if (MaxSingleStrategyWeight <= 0 || MaxSingleStrategyWeight > 1) problems.Add("MaxSingleStrategyWeight must be in (0, 1].");
        if (MinActiveStrategyWeight < 0 || MinActiveStrategyWeight >= MaxSingleStrategyWeight) problems.Add("MinActiveStrategyWeight must be below MaxSingleStrategyWeight.");
        if (MinSamplesForSignalCorrelation < 10) problems.Add("MinSamplesForSignalCorrelation below 10 turns noise into a correlation estimate.");
        if (SignalCorrelationWindow < 20) problems.Add("SignalCorrelationWindow below 20 is not estimable.");
        if (SignalExpiryBars < 1) problems.Add("SignalExpiryBars must be at least 1.");
        if (MaxChaseInAtr <= 0) problems.Add("MaxChaseInAtr must be positive.");
    }
}

public sealed class ProbabilityConfig
{
    /// <summary>
    /// Strength of the Bayesian prior, in pseudo-trades (spec section 22). A larger value
    /// demands more evidence before a bucket's own win rate is believed.
    /// </summary>
    public double PriorStrength { get; set; } = 25;

    /// <summary>
    /// Prior win rate used when nothing better is known. Deliberately set at the
    /// break-even rate for the default reward-to-risk, so an untested bucket starts with
    /// exactly no edge rather than an assumed one.
    /// </summary>
    public double PriorWinRate { get; set; } = 0.40;

    /// <summary>Trades below which a bucket is considered to carry no independent information.</summary>
    public int MinSampleForBucket { get; set; } = 20;

    /// <summary>Trades at which a bucket's own estimate is trusted without hierarchical backing.</summary>
    public int FullTrustSample { get; set; } = 100;

    /// <summary>Confidence buckets used for calibration tracking (spec section 125).</summary>
    public double[] CalibrationBucketEdges { get; set; } = { 0.50, 0.55, 0.60, 0.65, 0.70, 0.75, 0.80, 0.85, 0.90, 1.00 };

    /// <summary>Trades per calibration bin before its reliability estimate is used.</summary>
    public int MinSamplePerCalibrationBin { get; set; } = 20;

    /// <summary>
    /// Expected calibration error above which the model's probabilities are shrunk toward
    /// the base rate (spec section 16). 0.10 means predictions are off by ten points on average.
    /// </summary>
    public double MaxAcceptableCalibrationError { get; set; } = 0.10;

    internal void Validate(List<string> problems)
    {
        if (PriorStrength <= 0) problems.Add("PriorStrength must be positive.");
        if (PriorWinRate <= 0 || PriorWinRate >= 1) problems.Add("PriorWinRate must be in (0, 1).");
        if (MinSampleForBucket < 1) problems.Add("MinSampleForBucket must be at least 1.");
        if (FullTrustSample <= MinSampleForBucket) problems.Add("FullTrustSample must exceed MinSampleForBucket.");
        if (CalibrationBucketEdges == null || CalibrationBucketEdges.Length < 2) problems.Add("At least two calibration bucket edges are required.");
    }
}

public sealed class EvConfig
{
    /// <summary>
    /// Base edge required beyond break-even, expressed in R (spec section 18). Trading at
    /// EV marginally above zero is trading noise.
    /// </summary>
    public double BaseMinimumEdgeR { get; set; } = 0.10;

    /// <summary>Multiplier on round-trip cost added to the required edge.</summary>
    public double CostEdgeMultiplier { get; set; } = 1.5;

    /// <summary>Multiplier on estimated uncertainty added to the required edge.</summary>
    public double UncertaintyEdgeMultiplier { get; set; } = 1.0;

    /// <summary>
    /// Sigma multiple used for the lower confidence bound on expected value
    /// (spec section 19). The system trades the bound, not the point estimate.
    /// </summary>
    public double EdgeConfidenceZ { get; set; } = 1.0;

    /// <summary>
    /// Число реальных сделок, до которого система считается НЕ ИМЕЮЩЕЙ ИСТОРИИ.
    ///
    /// Существует потому, что три штрафа в требуемом преимуществе наказывают за отсутствие
    /// данных: тонкая выборка, плохая калибровка и ширина оценки. Все три снимаются только
    /// сделками — а сделок нет, пока штрафы действуют. Требуемое преимущество доходило до
    /// 0.8R при базовом пороге 0.10R, и первая сделка была невозможна никогда.
    ///
    /// Замкнутый круг разрывается не ослаблением требований к сделке, а переносом защиты в
    /// РАЗМЕР: пока истории нет, решение принимается по точечной оценке с полными
    /// структурными требованиями, а позиция открывается пробным, минимальным объёмом.
    /// Ноль отключает холодный старт полностью.
    /// </summary>
    public int ColdStartTrades { get; set; } = 20;

    /// <summary>Minimum reward-to-risk at the first target.</summary>
    public double MinRewardToRisk { get; set; } = 1.3;

    /// <summary>
    /// Reward-to-risk used for the break-even probability reference when no history exists.
    /// </summary>
    public double DefaultRewardToRisk { get; set; } = 1.8;

    /// <summary>
    /// Quantile of the historical MFE distribution used as a realistic target ceiling
    /// (spec section 44). Targets beyond what the setup historically reaches are fantasy.
    /// </summary>
    public double MfeTargetQuantile { get; set; } = 0.60;

    /// <summary>Trades required before the MFE distribution overrides the configured target.</summary>
    public int MinSampleForMfeTargeting { get; set; } = 30;

    /// <summary>Assumed loss, in R, when a stop is hit — above 1.0 to account for slippage.</summary>
    public double AssumedLossR { get; set; } = 1.05;

    internal void Validate(List<string> problems)
    {
        if (BaseMinimumEdgeR < 0) problems.Add("BaseMinimumEdgeR cannot be negative.");
        if (MinRewardToRisk <= 0) problems.Add("MinRewardToRisk must be positive.");
        if (MfeTargetQuantile <= 0 || MfeTargetQuantile >= 1) problems.Add("MfeTargetQuantile must be in (0, 1).");
        if (AssumedLossR < 1.0) problems.Add("AssumedLossR below 1.0 assumes stops fill better than requested.");
        if (ColdStartTrades < 0) problems.Add("ColdStartTrades cannot be negative.");
        if (EdgeConfidenceZ < 0) problems.Add("EdgeConfidenceZ cannot be negative.");
    }
}

public sealed class PortfolioConfig
{
    /// <summary>Observation counts for the rolling correlation estimates (spec section 28).</summary>
    public int[] CorrelationWindows { get; set; } = { 20, 50, 100 };

    /// <summary>Correlation at or above which two symbols join the same risk cluster.</summary>
    public double ClusterThreshold { get; set; } = 0.70;

    /// <summary>Timeframe on which returns are sampled for correlation.</summary>
    public Tf CorrelationTimeframe { get; set; } = Tf.M15;

    /// <summary>Maximum simultaneously open positions across the portfolio.</summary>
    public int MaxOpenPositions { get; set; } = 4;

    /// <summary>Maximum simultaneously open positions in one symbol.</summary>
    public int MaxPositionsPerSymbol { get; set; } = 1;

    /// <summary>Maximum open risk in one correlation cluster, as a percentage of equity.</summary>
    public double MaxClusterRiskPercent { get; set; } = 1.0;

    /// <summary>Maximum open risk in one symbol, as a percentage of equity.</summary>
    public double MaxSymbolRiskPercent { get; set; } = 0.75;

    /// <summary>
    /// Maximum correlation-adjusted net directional exposure, as a percentage of equity.
    /// This is what stops "four different trades" from being one leveraged bet on BTC.
    /// </summary>
    public double MaxDirectionalRiskPercent { get; set; } = 1.25;

    /// <summary>Candidates considered per decision cycle before ranking picks the best.</summary>
    public int MaxCandidatesPerCycle { get; set; } = 8;

    /// <summary>
    /// Окно сбора кандидатов, секунды.
    ///
    /// Бары разных инструментов закрываются отдельными событиями платформы, приходящими
    /// одно за другим. Исполнять каждое немедленно — значит отдавать бюджет риска тому,
    /// чьё событие пришло первым, а не лучшей возможности: очерёдность событий не имеет
    /// никакого отношения к качеству сигналов. Поэтому кандидаты, прошедшие все фильтры,
    /// собираются в пачку, ранжируются и исполняются по убыванию оценки.
    ///
    /// Окно намеренно короткое. Оно закрывается досрочно, как только отчитались все
    /// инструменты с этим временем бара — то есть в обычной работе задержки нет вовсе, а
    /// окно служит лишь страховкой на случай молчащего инструмента.
    /// </summary>
    public double CandidateBatchWindowSeconds { get; set; } = 2.0;

    /// <summary>Symbol whose regime defines the market-wide crypto context (spec sections 25-26).</summary>
    public string BenchmarkSymbol { get; set; } = "BTCUSD";

    /// <summary>
    /// Weight of the benchmark-regime agreement adjustment. The benchmark shifts
    /// probability; it is never an absolute veto (spec section 26).
    /// </summary>
    public double BenchmarkInfluence { get; set; } = 0.15;

    internal void Validate(List<string> problems)
    {
        if (CorrelationWindows == null || CorrelationWindows.Length == 0) problems.Add("At least one correlation window is required.");
        if (ClusterThreshold <= 0 || ClusterThreshold >= 1) problems.Add("ClusterThreshold must be in (0, 1).");
        if (MaxOpenPositions < 1) problems.Add("MaxOpenPositions must be at least 1.");
        if (MaxPositionsPerSymbol < 1) problems.Add("MaxPositionsPerSymbol must be at least 1.");
        if (MaxCandidatesPerCycle < 1) problems.Add("MaxCandidatesPerCycle must be at least 1 or no candidate could ever be executed.");
        if (CandidateBatchWindowSeconds < 0) problems.Add("CandidateBatchWindowSeconds cannot be negative.");
        if (CandidateBatchWindowSeconds > 30) problems.Add("CandidateBatchWindowSeconds above 30 delays entries far past the signal that produced them.");
        if (BenchmarkInfluence < 0 || BenchmarkInfluence > 0.5) problems.Add("BenchmarkInfluence must be in [0, 0.5]; the benchmark adjusts, it does not decide.");
    }
}

public sealed class RiskConfig
{
    /// <summary>Absolute ceiling on risk per trade. Nothing may exceed this, ever.</summary>
    public double HardMaxRiskPerTradePercent { get; set; } = 1.0;

    /// <summary>Maximum total open risk across all positions, as a percentage of equity.</summary>
    public double MaxTotalOpenRiskPercent { get; set; } = 2.0;

    public double DailyLossLimitPercent { get; set; } = 2.0;
    public double WeeklyLossLimitPercent { get; set; } = 5.0;

    /// <summary>Rolling drawdown limits by horizon (spec section 37).</summary>
    public double MaxDrawdown24hPercent { get; set; } = 4.0;
    public double MaxDrawdown7dPercent { get; set; } = 8.0;
    public double MaxDrawdown30dPercent { get; set; } = 12.0;
    public double MaxDrawdownAllTimePercent { get; set; } = 18.0;

    /// <summary>Consecutive-loss ladder (spec section 38).</summary>
    public int LossesBeforeRiskReduction { get; set; } = 2;
    public int LossesBeforeCooldown { get; set; } = 3;
    public int LossesBeforeDefensive { get; set; } = 4;
    public int LossesBeforeHalt { get; set; } = 6;
    public int ConsecutiveLossCooldownMinutes { get; set; } = 120;

    /// <summary>Risk multipliers for each posture. Strictly non-increasing.</summary>
    public double CautionRiskMultiplier { get; set; } = 0.65;
    public double DefensiveRiskMultiplier { get; set; } = 0.35;

    /// <summary>Equity must recover this fraction of the breach before the posture relaxes.</summary>
    public double RecoveryHysteresisFraction { get; set; } = 0.5;

    /// <summary>Minutes a posture must hold before it may improve, to stop state flapping.</summary>
    public int MinMinutesInRiskState { get; set; } = 30;

    /// <summary>Estimated probability of ruin above which the system refuses new risk.</summary>
    public double MaxAcceptableRiskOfRuin { get; set; } = 0.01;

    /// <summary>Ruin defined as this fractional loss of starting equity.</summary>
    public double RuinThresholdFraction { get; set; } = 0.35;

    /// <summary>Margin level (%) below which no new position may be opened.</summary>
    public double MinMarginLevelPercent { get; set; } = 400;

    /// <summary>Multiple of the stop-out level treated as the danger zone.</summary>
    public double StopOutSafetyMultiple { get; set; } = 3.0;

    /// <summary>
    /// Потолок порога опасного уровня маржи, %.
    ///
    /// Существует как защита от чужой ошибки: брокер, сообщивший стоп-аут в непривычных
    /// единицах, иначе получил бы возможность остановить торговлю навсегда. Значение
    /// заметно выше любого разумного стоп-аута и при этом конечно.
    /// </summary>
    public double MaxMarginDangerLevelPercent { get; set; } = 1000.0;

    /// <summary>Cooldown after any position closes, in minutes.</summary>
    public int PostTradeCooldownMinutes { get; set; } = 5;

    /// <summary>Spread percentile above which entries are refused (spec section 56).</summary>
    public double MaxSpreadPercentile { get; set; } = 0.90;

    /// <summary>Spread-to-ATR ratio above which the trade cannot pay for itself.</summary>
    public double MaxSpreadToAtr { get; set; } = 0.15;

    /// <summary>ATR percentile above which new entries are refused outright.</summary>
    public double MaxAtrPercentileForEntry { get; set; } = 0.95;

    /// <summary>Z-score at which a price, volume, spread or velocity reading is an anomaly.</summary>
    public double AnomalyZThreshold { get; set; } = 4.0;

    /// <summary>Bars of normal conditions required after an extreme event (spec section 66).</summary>
    public int RecoveryBarsAfterExtremeEvent { get; set; } = 12;

    /// <summary>Execution quality (0..1) below which risk is cut (spec section 59).</summary>
    public double MinExecutionQuality { get; set; } = 0.60;

    /// <summary>
    /// Winning-streak scaling cap (spec section 39). Risk may never be scaled above this
    /// multiple no matter how good recent results look.
    /// </summary>
    public double MaxWinStreakRiskMultiplier { get; set; } = 1.0;

    internal void Validate(List<string> problems)
    {
        if (HardMaxRiskPerTradePercent <= 0 || HardMaxRiskPerTradePercent > 5) problems.Add("HardMaxRiskPerTradePercent must be in (0, 5].");
        if (CautionRiskMultiplier > 1 || CautionRiskMultiplier <= 0) problems.Add("CautionRiskMultiplier must be in (0, 1].");
        if (DefensiveRiskMultiplier > CautionRiskMultiplier) problems.Add("DefensiveRiskMultiplier must not exceed CautionRiskMultiplier; the ladder must be monotonic.");
        if (MaxWinStreakRiskMultiplier < 1.0) problems.Add("MaxWinStreakRiskMultiplier below 1.0 would penalise winning.");
        if (MaxWinStreakRiskMultiplier > 1.5) problems.Add("MaxWinStreakRiskMultiplier above 1.5 is aggressive scaling into a streak; refused.");
        if (!(LossesBeforeRiskReduction <= LossesBeforeCooldown && LossesBeforeCooldown <= LossesBeforeDefensive && LossesBeforeDefensive <= LossesBeforeHalt))
            problems.Add("The consecutive-loss ladder must be non-decreasing.");
        if (MaxDrawdown24hPercent > MaxDrawdown7dPercent) problems.Add("The 24h drawdown limit must not exceed the 7d limit.");
        if (MaxDrawdown7dPercent > MaxDrawdown30dPercent) problems.Add("The 7d drawdown limit must not exceed the 30d limit.");
        if (MaxDrawdown30dPercent > MaxDrawdownAllTimePercent) problems.Add("The 30d drawdown limit must not exceed the all-time limit.");
        if (RuinThresholdFraction <= 0 || RuinThresholdFraction >= 1) problems.Add("RuinThresholdFraction must be in (0, 1).");
        if (MinMarginLevelPercent < 100) problems.Add("MinMarginLevelPercent below 100 permits trading while under-margined.");
    }
}

public sealed class SizingConfig
{
    /// <summary>Nominal risk per trade before every adaptive multiplier is applied.</summary>
    public double RiskPerTradePercent { get; set; } = 0.35;

    /// <summary>Floor on risk per trade; below this, the trade is skipped rather than shrunk further.</summary>
    /// <summary>
    /// Множитель размера позиции, пока у системы нет истории.
    ///
    /// Цена разрыва замкнутого круга: первые сделки совершаются ради данных, и стоить они
    /// должны соответственно. Это не ослабление защиты, а её перенос — из отказа в размер.
    /// </summary>
    public double ColdStartRiskMultiplier { get; set; } = 0.35;

    public double MinRiskPerTradePercent { get; set; } = 0.05;

    /// <summary>Weight of signal confidence in sizing, 0..1.</summary>
    public double ConfidenceWeight { get; set; } = 0.5;

    /// <summary>Weight of expected-value magnitude in sizing, 0..1.</summary>
    public double EdgeWeight { get; set; } = 0.35;

    /// <summary>ATR percentile treated as the neutral point for volatility targeting.</summary>
    public double VolatilityTargetPercentile { get; set; } = 0.50;

    /// <summary>Strength of volatility targeting, 0 (off) to 1 (full inverse scaling).</summary>
    public double VolatilityTargetStrength { get; set; } = 0.6;

    /// <summary>Floor and ceiling on the volatility-target multiplier.</summary>
    public double MinVolatilityMultiplier { get; set; } = 0.35;
    public double MaxVolatilityMultiplier { get; set; } = 1.0;

    /// <summary>
    /// Fraction of free margin an estimated position margin may consume. Belt-and-braces
    /// against a sizing bug turning into a margin call.
    /// </summary>
    public double MaxMarginUtilization { get; set; } = 0.20;

    internal void Validate(List<string> problems)
    {
        if (RiskPerTradePercent <= 0) problems.Add("RiskPerTradePercent must be positive.");
        if (MinRiskPerTradePercent <= 0 || MinRiskPerTradePercent > RiskPerTradePercent) problems.Add("MinRiskPerTradePercent must be in (0, RiskPerTradePercent].");
        if (MaxVolatilityMultiplier > 1.0) problems.Add("MaxVolatilityMultiplier above 1.0 would size UP into volatility.");
        if (MinVolatilityMultiplier <= 0 || MinVolatilityMultiplier > MaxVolatilityMultiplier) problems.Add("MinVolatilityMultiplier must be in (0, MaxVolatilityMultiplier].");
        if (MaxMarginUtilization <= 0 || MaxMarginUtilization > 1) problems.Add("MaxMarginUtilization must be in (0, 1].");
    }
}

public sealed class ExitConfig
{
    /// <summary>Stop distance in ATR when the ATR method is chosen.</summary>
    public double AtrStopMultiple { get; set; } = 1.6;

    /// <summary>Buffer beyond a structural level, in ATR, when the structure method is chosen.</summary>
    public double StructureStopBufferAtr { get; set; } = 0.35;

    /// <summary>Hard bounds on stop distance, in ATR. Outside these the trade is refused.</summary>
    public double MinStopInAtr { get; set; } = 0.6;
    public double MaxStopInAtr { get; set; } = 4.0;

    /// <summary>
    /// Quantile of the historical MAE distribution a stop must clear (spec section 45).
    /// A stop inside the region where winners routinely dip is a stop that converts winners
    /// into losers.
    /// </summary>
    public double MaeStopQuantile { get; set; } = 0.75;

    public int MinSampleForMaeStops { get; set; } = 30;

    /// <summary>First target, in R, and the fraction of the position closed there.</summary>
    public double Target1R { get; set; } = 1.2;
    public double Target1ClosePercent { get; set; } = 0.40;

    /// <summary>Second target, in R, and the fraction of the ORIGINAL position closed there.</summary>
    public double Target2R { get; set; } = 2.2;
    public double Target2ClosePercent { get; set; } = 0.35;

    /// <summary>Move to net break-even once this many R of open profit exists.</summary>
    public double BreakEvenTriggerR { get; set; } = 0.9;

    /// <summary>Extra cushion beyond true net break-even, in R, so noise does not scratch the trade.</summary>
    public double BreakEvenBufferR { get; set; } = 0.08;

    /// <summary>Trailing stop distance in ATR, by regime family.</summary>
    public double TrendTrailAtr { get; set; } = 2.2;
    public double RangeTrailAtr { get; set; } = 1.2;
    public double HighVolTrailAtr { get; set; } = 2.8;

    /// <summary>Open profit in R before trailing begins. Trailing from the start strangles trends.</summary>
    public double TrailActivationR { get; set; } = 1.0;

    /// <summary>Maximum holding time in signal-timeframe bars before a time stop applies.</summary>
    public int TimeStopBarsTrend { get; set; } = 120;
    public int TimeStopBarsRange { get; set; } = 48;

    /// <summary>A time stop only fires if progress is below this many R (spec section 48).</summary>
    public double TimeStopMaxProgressR { get; set; } = 0.35;

    /// <summary>Close early when the entry reason has been invalidated (spec section 49).</summary>
    public bool EnableInvalidationExit { get; set; } = true;

    /// <summary>Protect profit once this much R has been given back from peak open profit.</summary>
    public double ProfitGiveBackFraction { get; set; } = 0.45;

    /// <summary>Profit protection only engages above this much peak open profit, in R.</summary>
    public double ProfitProtectionMinPeakR { get; set; } = 1.5;

    internal void Validate(List<string> problems)
    {
        if (MinStopInAtr <= 0 || MinStopInAtr >= MaxStopInAtr) problems.Add("MinStopInAtr must be positive and below MaxStopInAtr.");
        if (Target1R <= 0 || Target2R <= Target1R) problems.Add("Target2R must exceed Target1R and both must be positive.");
        if (Target1ClosePercent + Target2ClosePercent >= 1.0) problems.Add("Partial closes must leave a runner: Target1 + Target2 close percentages must be below 100%.");
        if (Target1ClosePercent <= 0 || Target2ClosePercent <= 0) problems.Add("Partial close percentages must be positive.");
        if (BreakEvenTriggerR <= 0) problems.Add("BreakEvenTriggerR must be positive.");
        if (ProfitGiveBackFraction <= 0 || ProfitGiveBackFraction >= 1) problems.Add("ProfitGiveBackFraction must be in (0, 1).");
        if (TimeStopBarsTrend < 1 || TimeStopBarsRange < 1) problems.Add("Time stop bar counts must be positive.");
    }
}

public sealed class ExecutionConfig
{
    /// <summary>
    /// Maximum acceptable slippage as a fraction of ATR.
    ///
    /// Expressed relative to volatility rather than in pips, and that is not a stylistic
    /// preference. A crypto CFD commonly quotes a pip size of 0.01 on an instrument priced
    /// in the tens of thousands, so a limit of "8 pips" is eight cents on something that
    /// moves in hundreds of dollars: every order would be rejected, and the system would
    /// appear to work while never trading. A fraction of ATR means the same number, and the
    /// same intent, on BTC at 60,000 and on XRP at 0.5.
    /// </summary>
    public double MaxSlippageInAtr { get; set; } = 0.25;

    /// <summary>
    /// Maximum acceptable slippage as a multiple of the current spread.
    ///
    /// The effective limit is the LARGER of this and <see cref="MaxSlippageInAtr"/>. Taking
    /// the larger is deliberate: these are two ways of asking the same question, and
    /// whichever is more permissive is the one that keeps a correctly-sized limit from
    /// being overridden by a badly-scaled one.
    /// </summary>
    public double MaxSlippageSpreadMultiple { get; set; } = 3.0;

    /// <summary>Assumed slippage in normal conditions, as a fraction of the spread.</summary>
    public double NormalSlippageSpreadFraction { get; set; } = 0.35;

    /// <summary>Assumed slippage under stress, as a fraction of the spread.</summary>
    public double StressSlippageSpreadFraction { get; set; } = 1.5;

    /// <summary>Retries for a transient broker error. Deliberately small.</summary>
    public int MaxOrderRetries { get; set; } = 2;

    /// <summary>Delay between retries, in milliseconds.</summary>
    public int RetryDelayMs { get; set; } = 400;

    /// <summary>Observations kept for the execution quality estimate.</summary>
    public int ExecutionQualityWindow { get; set; } = 100;

    /// <summary>Minutes between broker/bot position reconciliations (spec section 145).</summary>
    public int ReconciliationIntervalMinutes { get; set; } = 5;

    /// <summary>Label prefix identifying this system's positions. Must be stable across restarts.</summary>
    public string OrderLabelPrefix { get; set; } = "QCV3";

    internal void Validate(List<string> problems)
    {
        if (MaxSlippageInAtr <= 0) problems.Add("MaxSlippageInAtr must be positive.");
        if (MaxSlippageSpreadMultiple <= 0) problems.Add("MaxSlippageSpreadMultiple must be positive.");
        if (MaxOrderRetries < 0 || MaxOrderRetries > 5) problems.Add("MaxOrderRetries must be in [0, 5].");
        if (StressSlippageSpreadFraction < NormalSlippageSpreadFraction) problems.Add("Stress slippage must not be below normal slippage.");
        if (string.IsNullOrWhiteSpace(OrderLabelPrefix)) problems.Add("OrderLabelPrefix is required for restart recovery.");
        if (ReconciliationIntervalMinutes < 1) problems.Add("ReconciliationIntervalMinutes must be at least 1.");
    }
}

public sealed class AdaptationConfig
{
    /// <summary>Half-life, in trades, of the "recent performance" estimate (spec section 21).</summary>
    public double RecentPerformanceHalfLife { get; set; } = 25;

    /// <summary>Trades before a strategy's own statistics influence its weight at all.</summary>
    public int MinTradesForWeighting { get; set; } = 20;

    /// <summary>Trades before degradation may disable a strategy. Below this, only weight is cut.</summary>
    public int MinTradesForDisable { get; set; } = 30;

    /// <summary>CUSUM slack and threshold for expectancy change detection.</summary>
    public double CusumSlack { get; set; } = 0.5;
    public double CusumThreshold { get; set; } = 5.0;

    /// <summary>Expectancy in R below which a strategy is considered degraded.</summary>
    public double DegradedExpectancyR { get; set; } = 0.0;

    /// <summary>Profit factor below which a strategy is considered degraded.</summary>
    public double DegradedProfitFactor { get; set; } = 0.95;

    /// <summary>Hours a disabled strategy must shadow-trade before it may be reconsidered.</summary>
    public int ShadowHoursBeforeRecovery { get; set; } = 72;

    /// <summary>Shadow trades with positive expectancy required before reactivation (spec section 24).</summary>
    public int ShadowTradesForRecovery { get; set; } = 25;

    /// <summary>
    /// Сколько виртуальных позиций одной отключённой стратегии ведётся одновременно.
    ///
    /// Лимит нужен не ради экономии памяти, а ради независимости наблюдений: десяток
    /// виртуальных сделок, открытых на одном движении рынка, — это одно наблюдение,
    /// посчитанное десять раз, и порог восстановления был бы взят фиктивной статистикой.
    /// </summary>
    public int MaxConcurrentShadowPositions { get; set; } = 3;

    /// <summary>
    /// Сколько последних закрытых сделок переживает перезапуск.
    ///
    /// Компромисс между памятью системы и размером снимка состояния. Пятисот сделок
    /// достаточно, чтобы восстановить все срезы статистики и калибровку: оценки и так
    /// взвешены в пользу недавнего, и сделка годичной давности влияет на решение мало.
    ///
    /// Ноль означает, что история не сохраняется — то есть адаптивный слой обнуляется при
    /// каждом перезапуске. Это допустимо только в бэктесте.
    /// </summary>
    public int PersistedTradeHistory { get; set; } = 300;

    /// <summary>
    /// Путей в симуляции Монте-Карло для отчёта о распределении просадок.
    ///
    /// Считается при выводе дашборда, то есть редко. Меньше тысячи путей дают заметно
    /// шумные хвосты, а именно хвосты здесь и интересны.
    /// </summary>
    public int MonteCarloPaths { get; set; } = 2000;

    /// <summary>
    /// Стоп по времени для виртуальной сделки, если план выхода его не задал. Без него
    /// виртуальная позиция в боковике могла бы не закрыться никогда и навсегда занять
    /// место в лимите наблюдений.
    /// </summary>
    public int ShadowFallbackTimeStopBars { get; set; } = 48;

    /// <summary>Weight a recovered strategy resumes at. Recovery is cautious by construction.</summary>
    public double RecoveryWeightFraction { get; set; } = 0.35;

    /// <summary>Minutes between adaptation passes. Adaptation is periodic, never per tick.</summary>
    public int AdaptationIntervalMinutes { get; set; } = 15;

    /// <summary>Rejected signals tracked for filter-value accounting (spec sections 119-120).</summary>
    public int FalsePositiveDatabaseSize { get; set; } = 500;

    /// <summary>
    /// Bars a rejected signal is followed for, to judge whether the filter that killed it
    /// saved money or cost money.
    /// </summary>
    public int RejectedSignalFollowUpBars { get; set; } = 60;

    internal void Validate(List<string> problems)
    {
        if (RecentPerformanceHalfLife <= 0) problems.Add("RecentPerformanceHalfLife must be positive.");
        if (MinTradesForDisable < MinTradesForWeighting) problems.Add("MinTradesForDisable must be at least MinTradesForWeighting.");
        if (RecoveryWeightFraction <= 0 || RecoveryWeightFraction > 1) problems.Add("RecoveryWeightFraction must be in (0, 1].");
        if (AdaptationIntervalMinutes < 1) problems.Add("AdaptationIntervalMinutes must be at least 1.");
        if (ShadowTradesForRecovery < 10) problems.Add("ShadowTradesForRecovery below 10 is not evidence of recovery.");
        if (MonteCarloPaths < 500) problems.Add("MonteCarloPaths below 500 gives noisy tails, which are the only part that matters.");
        if (PersistedTradeHistory < 0) problems.Add("PersistedTradeHistory cannot be negative.");
        if (MaxConcurrentShadowPositions < 1) problems.Add("MaxConcurrentShadowPositions must be at least 1 or disabled strategies can never recover.");
        if (ShadowFallbackTimeStopBars < 1) problems.Add("ShadowFallbackTimeStopBars must be positive.");
    }
}
