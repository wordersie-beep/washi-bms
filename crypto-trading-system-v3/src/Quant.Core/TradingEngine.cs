using System;
using System.Collections.Generic;
using Quant.Core.Adaptation;
using Quant.Core.Config;
using Quant.Core.Data;
using Quant.Core.Decision;
using Quant.Core.Ev;
using Quant.Core.Execution;
using Quant.Core.Exits;
using Quant.Core.Features;
using Quant.Core.Indicators;
using Quant.Core.Journal;
using Quant.Core.Numerics;
using Quant.Core.Portfolio;
using Quant.Core.Primitives;
using Quant.Core.Probability;
using Quant.Core.Regime;
using Quant.Core.Risk;
using Quant.Core.Sizing;
using Quant.Core.State;
using Quant.Core.Stats;
using Quant.Core.Strategies;

namespace Quant.Core;

/// <summary>
/// Оркестратор: собирает все слои в один цикл принятия решений.
///
/// Полностью свободен от зависимости на cTrader. Всё, что ему нужно от платформы, приходит
/// через <see cref="IBroker"/>, <see cref="IStateStore"/> и <see cref="IJournalSink"/>. Это
/// значит, что весь торговый цикл целиком — включая исполнение, управление позициями,
/// восстановление после рестарта и сверку — проверяется тестами без запуска платформы.
///
/// Порядок в <see cref="OnBarClosed"/> не произволен:
///   1. сначала обновляется состояние и закрываются позиции, которые должны закрыться;
///   2. затем оценивается риск и режим;
///   3. только потом рассматриваются новые входы.
/// Обратный порядок означал бы, что новая позиция открывается на основании портфеля, который
/// уже неактуален, и лимиты риска проверялись бы против устаревшей картины.
/// </summary>
public sealed class TradingEngine
{
    private readonly EngineConfig _config;
    private readonly IBroker _broker;
    private readonly IStateStore _stateStore;
    private readonly DecisionJournal _journal;
    private readonly string _instanceId;

    private readonly Dictionary<string, SymbolDataSet> _data = new Dictionary<string, SymbolDataSet>(StringComparer.Ordinal);
    private readonly Dictionary<string, FeatureEngine> _features = new Dictionary<string, FeatureEngine>(StringComparer.Ordinal);
    private readonly Dictionary<string, IMarketRegimeModel> _regimeModels = new Dictionary<string, IMarketRegimeModel>(StringComparer.Ordinal);
    private readonly Dictionary<string, RegimeAssessment> _regimes = new Dictionary<string, RegimeAssessment>(StringComparer.Ordinal);
    private readonly Dictionary<string, FeatureVector> _latestFeatures = new Dictionary<string, FeatureVector>(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _lastSignalBar = new Dictionary<string, DateTime>(StringComparer.Ordinal);
    private readonly Dictionary<string, MarketSchedule> _schedules = new Dictionary<string, MarketSchedule>(StringComparer.Ordinal);
    private readonly List<OpenPosition> _positions = new List<OpenPosition>();
    private readonly Dictionary<string, ExitPlan> _plans = new Dictionary<string, ExitPlan>(StringComparer.Ordinal);

    /// <summary>
    /// Кандидаты, прошедшие все фильтры и ожидающие ранжирования.
    ///
    /// Бары разных инструментов приходят отдельными событиями. Без этой пачки бюджет риска
    /// доставался бы тому, чьё событие пришло первым, — то есть распределялся бы очерёдностью
    /// событий платформы, а не качеством возможностей.
    /// </summary>
    private readonly List<TradeCandidate> _pendingCandidates = new List<TradeCandidate>();
    private readonly HashSet<string> _symbolsReportedThisCycle = new HashSet<string>(StringComparer.Ordinal);
    private DateTime _cycleBarOpenTimeUtc = DateTime.MinValue;
    private DateTime _cycleStartedUtc = DateTime.MinValue;

    private readonly StrategyRegistry _strategies;
    private readonly SignalCorrelationTracker _signalCorrelation;
    private readonly PerformanceStore _performance;
    private readonly StrategyWeightEngine _weights;
    private readonly ShadowTracker _shadow;
    private readonly EnsembleVoter _voter;
    private readonly CalibrationTracker _calibration;
    private readonly BayesianProbabilityModel _probability;
    private readonly CostModel _costModel;
    private readonly ExpectedValueEngine _expectedValue;
    private readonly CorrelationEngine _correlation;
    private readonly PortfolioRiskEngine _portfolio;
    private readonly OpportunityRanker _ranker;
    private readonly DrawdownTracker _drawdown;
    private readonly ExecutionQualityTracker _executionQuality;
    private readonly AnomalyDetector _anomaly;
    private readonly RiskEngine _risk;
    private readonly PositionSizer _sizer;
    private readonly ExitPlanner _exitPlanner;
    private readonly PositionManager _positionManager;
    private readonly DataQualityMonitor _dataQuality;
    private readonly TradeGate _gate;
    private readonly IdempotencyGuard _idempotency;
    private readonly ExecutionEngine _execution;
    private readonly FilterValueLedger _filterLedger;
    private readonly Dashboard _dashboard;

    private RiskAssessment _lastRisk;
    private AnomalyReport _lastAnomaly;
    private IReadOnlyDictionary<string, int> _clusters = new Dictionary<string, int>();
    private DateTime _lastAdaptationUtc = DateTime.MinValue;
    private DateTime _lastReconciliationUtc = DateTime.MinValue;
    private bool _haltedByReconciliation;

    public TradingEngine(
        EngineConfig config,
        IBroker broker,
        IStateStore stateStore,
        IJournalSink journalSink,
        string instanceId)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _stateStore = stateStore ?? new InMemoryStateStore();
        _instanceId = instanceId ?? "default";
        _journal = new DecisionJournal(journalSink ?? NullJournalSink.Instance);

        _strategies = new StrategyRegistry();
        _signalCorrelation = new SignalCorrelationTracker(config.Strategy);
        _performance = new PerformanceStore(config.Adaptation);
        _weights = new StrategyWeightEngine(config.Adaptation, config.Strategy, _performance);
        _shadow = new ShadowTracker(config.Adaptation);
        _voter = new EnsembleVoter(config.Strategy, _signalCorrelation, _weights);

        foreach (IStrategy s in _strategies.All)
        {
            _signalCorrelation.Register(s);
            _weights.Register(s);
        }

        _calibration = new CalibrationTracker(config.Probability);
        _probability = new BayesianProbabilityModel(config.Probability, config.Ev, _performance, _calibration);
        _costModel = new CostModel(config.Execution);
        _expectedValue = new ExpectedValueEngine(config.Ev, _performance);
        _correlation = new CorrelationEngine(config.Portfolio);
        _portfolio = new PortfolioRiskEngine(config.Portfolio, config.Risk, _correlation);
        _ranker = new OpportunityRanker(config.Portfolio, _correlation);
        _drawdown = new DrawdownTracker();
        _executionQuality = new ExecutionQualityTracker(config.Execution);
        _anomaly = new AnomalyDetector(config.Risk);
        _risk = new RiskEngine(config.Risk, config.Sizing, _drawdown, _executionQuality, _performance,
            new RiskOfRuinEstimator(seed: config.RandomSeed));
        _sizer = new PositionSizer(config.Sizing, config.Risk);
        _exitPlanner = new ExitPlanner(config.Exit, config.Ev, _performance);
        _positionManager = new PositionManager(config.Exit);
        _dataQuality = new DataQualityMonitor(config.Data);
        _gate = new TradeGate(config);
        _idempotency = new IdempotencyGuard();
        _execution = new ExecutionEngine(config.Execution, broker, _executionQuality, _idempotency, _instanceId);
        _filterLedger = new FilterValueLedger(config.Adaptation);
        _dashboard = new Dashboard();

        _lastRisk = new RiskAssessment { State = RiskState.Normal, RiskMultiplier = 1.0, Reasons = Array.Empty<string>() };
        _lastAnomaly = new AnomalyReport(0, false, Array.Empty<string>());
    }

    // --- Доступ для дашборда, тестов и отчётов ------------------------------------------
    public DecisionJournal Journal => _journal;
    public PerformanceStore Performance => _performance;
    public CalibrationTracker Calibration => _calibration;
    public ExecutionQualityTracker ExecutionQuality => _executionQuality;
    public StrategyWeightEngine Weights => _weights;
    public ShadowTracker Shadow => _shadow;
    public IdempotencyGuard Idempotency => _idempotency;
    public FilterValueLedger FilterLedger => _filterLedger;
    public CorrelationEngine Correlation => _correlation;
    public RiskEngine Risk => _risk;
    public IReadOnlyList<OpenPosition> Positions => _positions;
    public IReadOnlyDictionary<string, RegimeAssessment> Regimes => _regimes;
    public bool IsHaltedByReconciliation => _haltedByReconciliation;
    public long TradesClosed { get; private set; }

    /// <summary>Регистрирует инструмент. Вызывается один раз на старте для каждого символа.</summary>
    public void AddSymbol(string symbolName, SymbolSpec spec, MarketSchedule schedule = null)
    {
        if (string.IsNullOrWhiteSpace(symbolName) || _data.ContainsKey(symbolName)) return;

        _data[symbolName] = new SymbolDataSet(symbolName, spec, _config.Data);
        _features[symbolName] = new FeatureEngine(_config.Data);
        _regimeModels[symbolName] = new StatisticalRegimeModel(_config.Regime);
        _regimes[symbolName] = RegimeAssessment.Unknown;
        _schedules[symbolName] = schedule ?? MarketSchedule.Unknown;
    }

    /// <summary>
    /// Режимы, в которых приказы не должны доходить до настоящего счёта.
    ///
    /// Shadow — наблюдение: решения принимаются и записываются, денег нет вовсе.
    /// Paper — исполнение моделируется по живым ценам со спредом и комиссией, но приказы
    /// никуда не уходят. Demo и Live отличаются только типом счёта.
    /// </summary>
    public static bool RequiresSimulatedBroker(OperatingMode mode) =>
        mode == OperatingMode.Shadow || mode == OperatingMode.Paper;

    /// <summary>Расписание торгов инструмента. Никогда не null.</summary>
    public MarketSchedule ScheduleOf(string symbolName) =>
        _schedules.TryGetValue(symbolName, out MarketSchedule s) ? s : MarketSchedule.Unknown;

    /// <summary>
    /// Инструменты, которые торгуются НЕ круглосуточно.
    ///
    /// Система рассчитана на непрерывный рынок (раздел 1). Инструмент с перерывами её не
    /// ломает, но меняет: на открытии сессии приходит гэп, который стоп не удерживает, а
    /// статистика режимов собирается по рваной серии. Об этом человек должен узнать при
    /// старте, а не из отсутствия сделок.
    /// </summary>
    public IReadOnlyList<string> SymbolsNotTrading24x7()
    {
        var result = new List<string>();
        foreach (KeyValuePair<string, MarketSchedule> kv in _schedules)
        {
            if (!kv.Value.IsContinuous) result.Add(kv.Key);
        }
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    public SymbolDataSet Data(string symbolName) => _data.TryGetValue(symbolName, out SymbolDataSet d) ? d : null;

    public void UpdateSpec(string symbolName, SymbolSpec spec) => Data(symbolName)?.UpdateSpec(spec);

    public void OnQuote(string symbolName, in Quote quote) => Data(symbolName)?.OnQuote(quote);

    /// <summary>
    /// Основной цикл: вызывается на закрытии бара сигнального таймфрейма.
    /// </summary>
    /// <param name="isWarmUp">
    /// true — бар ИСТОРИЧЕСКИЙ, подаётся при старте для наполнения индикаторов.
    ///
    /// В этом режиме выполняется всё, что накапливает состояние рынка — ряды, признаки,
    /// режим с его гистерезисом, — и НЕ выполняется ничего, что принимает решения или
    /// обращается наружу.
    ///
    /// Разделение обязательно. Без него прогрев пятисот баров означает пятьсот оценок
    /// риска по ценам двухдневной давности, пятьсот попыток входа по котировкам, которых
    /// уже нет, сверку с брокером на историческом времени и пятьсот записей состояния —
    /// причём дневная точка отсчёта окажется привязана к историческому дню, и дневной
    /// лимит убытка после этого считается от капитала, которого на счёте никогда не было.
    /// Торговля при этом не происходит только случайно: котировки во время прогрева ещё
    /// нет, и проверка качества данных отвергает кандидата. Полагаться на случайную защиту
    /// нельзя, а журнал она всё равно забивает сотнями отказов, за которыми не видно
    /// настоящих.
    /// </param>
    public void OnBarClosed(DateTime nowUtc, string symbolName, Tf timeframe, in Candle bar, bool isWarmUp = false)
    {
        SymbolDataSet data = Data(symbolName);
        if (data == null) return;

        if (!data.OnBarClosed(timeframe, bar)) return;

        // Корреляция считается только на своём таймфрейме: сопоставлять 5-минутный ряд с
        // часовым — значит измерять не корреляцию, а рассинхронизацию.
        if (timeframe == _config.Portfolio.CorrelationTimeframe)
        {
            _correlation.Observe(symbolName, bar.Close);
        }

        if (timeframe != _config.Data.SignalTimeframe) return;

        _anomaly.OnBarClosed();

        // Учёт ценности фильтров отслеживает ОТКЛОНЁННЫЕ сигналы. Во время прогрева
        // решений не принимается, отклонять нечего.
        if (!isWarmUp) _filterLedger.OnBarClosed(symbolName, bar.High, bar.Low);

        if (!data.IsReady) return;

        // 1. Признаки и режим.
        GlobalMarketContext global = BuildGlobalContext();
        FeatureVector features = _features[symbolName].Build(
            nowUtc, data, global,
            _correlation.Correlation(symbolName, _config.Portfolio.BenchmarkSymbol),
            _correlation.MeanAbsoluteCorrelation(symbolName));

        if (features == null) return;

        _latestFeatures[symbolName] = features;
        _regimes[symbolName] = _regimeModels[symbolName].Classify(features);
        _lastAnomaly = _anomaly.Evaluate(features);

        // Прогрев заканчивается здесь. Всё выше накапливает состояние рынка и обязано
        // выполняться; всё ниже принимает решения и обращается наружу.
        if (isWarmUp) return;

        // 2. Состояние счёта и риск.
        AccountSnapshot account = _broker.GetAccount();
        _lastRisk = _risk.Evaluate(nowUtc, account, _lastAnomaly.Severity, _anomaly.InRecoveryPeriod);

        BeginOrJoinCycle(nowUtc, symbolName, bar.OpenTimeUtc);

        // 3. Виртуальные позиции отключённых стратегий — их единственный путь обратно.
        _shadow.OnBarClosed(nowUtc, symbolName, bar, _performance.Record);

        // 4. Сопровождение открытых позиций — ДО рассмотрения новых.
        ManageOpenPositions(nowUtc, account, symbolName);

        // 5. Периодические задачи.
        MaybeReconcile(nowUtc);
        MaybeAdapt(nowUtc);

        // 6. Новые входы: кандидат встаёт в пачку этого бара.
        if (!_haltedByReconciliation)
        {
            ConsiderEntry(nowUtc, symbolName, data, features, account, global);
        }

        // 7. Пачка исполняется, как только отчитались все инструменты.
        TryCloseCycle(nowUtc);

        SaveState(nowUtc, account);
    }

    /// <summary>
    /// Заводит виртуальные сделки по сигналам отключённых стратегий.
    ///
    /// Счётчик сигналов сам по себе бесполезен: восстановление требует ЗАПИСИ ИСХОДОВ, а не
    /// факта, что сигналы были. Поэтому каждый такой сигнал получает настоящий план выхода
    /// и настоящую оценку издержек — и дальше ведётся по барам до стопа, цели или времени.
    /// </summary>
    private void TrackDisabledSignals(
        DateTime nowUtc, string symbolName, SymbolDataSet data, FeatureVector features,
        RegimeAssessment regime, IReadOnlyList<StrategySignal> disabledSignals)
    {
        if (disabledSignals == null || disabledSignals.Count == 0) return;

        for (int i = 0; i < disabledSignals.Count; i++)
        {
            StrategySignal signal = disabledSignals[i];
            _weights.NoteShadowTrade(signal.StrategyName);

            if (_shadow.OpenCountFor(signal.StrategyName) >= _config.Adaptation.MaxConcurrentShadowPositions) continue;

            double entryPrice = signal.Direction == Side.Long ? data.LatestQuote.Ask : data.LatestQuote.Bid;
            if (entryPrice <= 0) continue;

            double atr = features.Atr;
            if (atr <= 0) continue;

            bool stressed = features.SpreadPercentile > 0.85 || features.AtrPercentile > 0.85;
            CostEstimate costAtOneAtr = _costModel.Estimate(
                data.Spec, data.Spread.CostingSpread(), atr, features.Price, stressed, _executionQuality.SlippageMultiplier);

            ExitPlan plan = _exitPlanner.Build(
                data.Spec, data, signal.Direction, entryPrice, signal, regime.Primary, costAtOneAtr);

            if (!plan.IsValid) continue;

            CostEstimate cost = _costModel.Estimate(
                data.Spec, data.Spread.CostingSpread(), plan.StopDistance,
                features.Price, stressed, _executionQuality.SlippageMultiplier);

            _shadow.Open(nowUtc, symbolName, signal, plan, entryPrice, regime.Primary, features, cost.TotalR, _config.Mode);
        }
    }

    /// <summary>Контекст рынка от эталонного инструмента (разделы 25–27).</summary>
    private GlobalMarketContext BuildGlobalContext()
    {
        string benchmark = _config.Portfolio.BenchmarkSymbol;
        if (!_latestFeatures.TryGetValue(benchmark, out FeatureVector f) || f == null)
        {
            return GlobalMarketContext.Unavailable;
        }

        _regimes.TryGetValue(benchmark, out RegimeAssessment regime);
        regime ??= RegimeAssessment.Unknown;

        return new GlobalMarketContext
        {
            IsAvailable = true,
            TrendStrength = f.TrendStrength,
            VolatilityPercentile = f.AtrPercentile,
            MomentumInAtr = MathUtil.SafeDiv(f.Return20, f.AtrFraction),
            Regime = regime.Primary,
            RegimeConfidence = regime.Confidence,
            UniverseCorrelation = _correlation.UniverseCorrelation(),
        };
    }

    private void ConsiderEntry(
        DateTime nowUtc, string symbolName, SymbolDataSet data,
        FeatureVector features, AccountSnapshot account, GlobalMarketContext global)
    {
        RegimeAssessment regime = _regimes[symbolName];

        DataQualityReport quality = _dataQuality.Evaluate(
            nowUtc, data.Spec, data.LatestQuote, data.Signal, data.Ticks, data.Spread, ScheduleOf(symbolName));

        var ctx = new StrategyContext(nowUtc, data, features, regime, global, _config);

        EnsembleDecision ensemble = _voter.Vote(
            ctx, _strategies.Ensemble, _executionQuality.Quality,
            collectDisabled: true, out IReadOnlyList<StrategySignal> disabledSignals);

        // Отключённые стратегии продолжают наблюдаться виртуально — это их путь обратно.
        TrackDisabledSignals(nowUtc, symbolName, data, features, regime, disabledSignals);

        string signalId = ensemble.Leader == null
            ? null
            : IdempotencyGuard.BuildSignalId(symbolName, ensemble.Leader.StrategyName, ensemble.Direction, data.Signal.LastBarOpenTimeUtc);

        var candidate = new TradeCandidate
        {
            SignalId = signalId,
            TimeUtc = nowUtc,
            SymbolName = symbolName,
            Data = data,
            Features = features,
            Regime = regime,
            Ensemble = ensemble,
            DataQuality = quality,
        };

        GateOutcome pre = _gate.CheckPreTrade(candidate, _lastRisk, _lastAnomaly, _lastSignalBar, nowUtc);
        if (!pre.Passed) { RecordRejection(candidate, pre, account); return; }

        // Издержки при стопе в одну ATR — масштабируются планировщиком выхода под реальный стоп.
        double atr = features.Atr;
        bool stressed = features.SpreadPercentile > 0.85 || features.AtrPercentile > 0.85;
        CostEstimate costAtOneAtr = _costModel.Estimate(
            data.Spec, data.Spread.CostingSpread(), atr, features.Price, stressed, _executionQuality.SlippageMultiplier);

        double entryPrice = ensemble.Direction == Side.Long ? data.LatestQuote.Ask : data.LatestQuote.Bid;

        candidate.Exit = _exitPlanner.Build(
            data.Spec, data, ensemble.Direction, entryPrice, ensemble.Leader, regime.Primary, costAtOneAtr);

        if (!candidate.Exit.IsValid)
        {
            RecordRejection(candidate, GateOutcome.Reject(candidate.Exit.RejectionReason, candidate.Exit.Detail), account);
            return;
        }

        candidate.Cost = _costModel.Estimate(
            data.Spec, data.Spread.CostingSpread(), candidate.Exit.StopDistance,
            features.Price, stressed, _executionQuality.SlippageMultiplier);

        candidate.Probability = _probability.Estimate(new ProbabilityQuery
        {
            StrategyName = ensemble.Leader.StrategyName,
            SymbolName = symbolName,
            Regime = regime.Primary,
            Direction = ensemble.Direction,
            EnsembleConfidence = ensemble.Confidence,
            RewardToRisk = candidate.Exit.RewardToRisk,
            Session = features.Session,
            IsWeekend = features.IsWeekend,
            VolatilityBucket = features.VolatilityBucket,
            Features = features,
            Ensemble = ensemble,
        });

        // Эталонный инструмент СМЕЩАЕТ вероятность, но не накладывает вето (раздел 26).
        double benchmarkAdjustment = global.DirectionalAgreement(ensemble.Direction) * _config.Portfolio.BenchmarkInfluence;
        var adjusted = new ProbabilityEstimate(
            MathUtil.Clamp01(candidate.Probability.PWin + (benchmarkAdjustment * 0.1)),
            candidate.Probability.StandardError,
            candidate.Probability.EffectiveSample,
            candidate.Probability.CalibrationQuality,
            candidate.Probability.Basis);
        candidate.Probability = adjusted;

        candidate.ExpectedValue = _expectedValue.Evaluate(
            candidate.Probability, candidate.Exit.RewardToRisk, candidate.Cost,
            ensemble.Leader.StrategyName, regime.Primary, symbolName,
            regime.Confidence, MathUtil.Clamp01(features.AtrPercentile - 0.5) * 2);

        GateOutcome economics = _gate.CheckEconomics(candidate);
        if (!economics.Passed) { RecordRejection(candidate, economics, account); return; }

        // Сайзинг и лимиты портфеля.
        PortfolioExposure exposure = _portfolio.Compute(_positions, account.Equity, _clusters);
        double budget = Math.Max(0, _config.Risk.MaxTotalOpenRiskPercent - exposure.TotalOpenRiskPercent);

        candidate.Sizing = _sizer.Compute(
            data.Spec, account, entryPrice, candidate.Exit.StopPrice, ensemble.Direction,
            _lastRisk.RiskMultiplier, regime.RiskMultiplier, ensemble.Confidence,
            candidate.ExpectedValue.EdgeSurplusR,
            ensemble.Weights != null && ensemble.Weights.TryGetValue(ensemble.Leader.StrategyName, out double w) ? w : 0.2,
            features.AtrPercentile,
            _correlation.MeanAbsoluteCorrelation(symbolName),
            quality.Score, _executionQuality.Quality, budget);

        GateOutcome portfolio = _gate.CheckPortfolio(candidate, _portfolio, exposure, _clusters, CountPositionsIn(symbolName));
        if (!portfolio.Passed) { RecordRejection(candidate, portfolio, account); return; }

        candidate.OpportunityScore = _ranker.Score(candidate, _positions);

        // Кандидат не исполняется здесь: он встаёт в пачку и будет исполнен после
        // ранжирования против остальных возможностей этого же бара.
        _pendingCandidates.Add(candidate);
    }

    /// <summary>
    /// Отмечает, что инструмент отчитался по бару, и закрывает окно сбора, как только
    /// отчитались все, у кого есть данные.
    /// </summary>
    private void BeginOrJoinCycle(DateTime nowUtc, string symbolName, DateTime barOpenTimeUtc)
    {
        if (barOpenTimeUtc != _cycleBarOpenTimeUtc)
        {
            // Пришёл бар НОВОГО времени, а прошлая пачка ещё не исполнена: кто-то из
            // инструментов промолчал. Исполняем накопленное, не дожидаясь молчащего.
            FlushPendingCandidates(nowUtc);

            _cycleBarOpenTimeUtc = barOpenTimeUtc;
            _cycleStartedUtc = nowUtc;
            _symbolsReportedThisCycle.Clear();
        }

        _symbolsReportedThisCycle.Add(symbolName);
    }

    /// <summary>
    /// Закрывает окно сбора, если все инструменты отчитались или окно истекло по времени.
    /// Вызывается и после обработки бара, и с тика: тики идут часто, поэтому задержка
    /// исполнения ограничена сверху окном, а не паузой до следующего бара.
    /// </summary>
    private void TryCloseCycle(DateTime nowUtc)
    {
        if (_pendingCandidates.Count == 0) return;

        bool everyoneReported = _symbolsReportedThisCycle.Count >= _data.Count;
        bool windowExpired = _cycleStartedUtc != DateTime.MinValue &&
                             (nowUtc - _cycleStartedUtc).TotalSeconds >= _config.Portfolio.CandidateBatchWindowSeconds;

        if (everyoneReported || windowExpired) FlushPendingCandidates(nowUtc);
    }

    /// <summary>
    /// Ранжирует накопленных кандидатов и исполняет их по убыванию оценки.
    ///
    /// Лимиты портфеля пересчитываются ПЕРЕД КАЖДЫМ исполнением: первая открытая позиция
    /// меняет и суммарный риск, и корреляционную картину, и потому кандидат, проходивший
    /// лимиты в момент постановки в очередь, может их уже не проходить. Пропустить эту
    /// проверку значило бы открыть пачку позиций, каждая из которых по отдельности
    /// допустима, а вместе они превышают бюджет.
    /// </summary>
    private void FlushPendingCandidates(DateTime nowUtc)
    {
        if (_pendingCandidates.Count == 0) return;

        var batch = new List<TradeCandidate>(_pendingCandidates);
        _pendingCandidates.Clear();

        AccountSnapshot account = _broker.GetAccount();
        IReadOnlyList<TradeCandidate> ranked = _ranker.Rank(batch, _positions);

        if (batch.Count > 1)
        {
            _journal.Write($"РАНЖИРОВАНИЕ: {batch.Count} кандидатов, к исполнению {ranked.Count}, " +
                           $"лучший {ranked[0].SymbolName} ({ranked[0].OpportunityScore:F3})");
        }

        for (int i = 0; i < ranked.Count; i++)
        {
            TradeCandidate c = ranked[i];
            double entryPrice = c.Direction == Side.Long ? c.Data.LatestQuote.Ask : c.Data.LatestQuote.Bid;

            if (entryPrice <= 0)
            {
                RecordRejection(c, GateOutcome.Reject(NoTradeReason.DataQuality, "нет котировки в момент исполнения"), account);
                continue;
            }

            PortfolioExposure exposure = _portfolio.Compute(_positions, account.Equity, _clusters);
            GateOutcome portfolio = _gate.CheckPortfolio(c, _portfolio, exposure, _clusters, CountPositionsIn(c.SymbolName));

            if (!portfolio.Passed)
            {
                RecordRejection(c, portfolio, account);
                continue;
            }

            Execute(nowUtc, c, account, exposure, entryPrice);
            account = _broker.GetAccount();
        }

        // Кандидаты, не поместившиеся в лимит на цикл, отклоняются явно — с причиной,
        // которая видна в журнале, а не тихо исчезают.
        for (int i = 0; i < batch.Count; i++)
        {
            if (Contains(ranked, batch[i])) continue;
            RecordRejection(batch[i], GateOutcome.Reject(NoTradeReason.NotRankedHighEnough,
                $"оценка {batch[i].OpportunityScore:F3} не вошла в {_config.Portfolio.MaxCandidatesPerCycle} лучших"), account);
        }
    }

    private static bool Contains(IReadOnlyList<TradeCandidate> list, TradeCandidate item)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i], item)) return true;
        }
        return false;
    }

    private void Execute(DateTime nowUtc, TradeCandidate c, AccountSnapshot account, PortfolioExposure exposure, double entryPrice)
    {
        // Защита режима. Shadow и Paper обещают человеку, что деньги не двигаются. Обещание
        // не может держаться на том, что кто-то подставил правильного брокера: параметр,
        // выставленный по ошибке, не должен иметь возможности отправить приказ на счёт.
        //
        // Отказ здесь громкий и полный. Тихо исполнить приказ, пометив сделку виртуальной,
        // было бы худшим из возможных исходов: деньги двигаются, а память системы об этом
        // не знает и ничему не учится.
        if (RequiresSimulatedBroker(_config.Mode) && !_broker.IsSimulated)
        {
            RecordRejection(c, GateOutcome.Reject(NoTradeReason.ModeGuard,
                $"режим {_config.Mode} запрещает отправку приказов, но подключён настоящий брокер"), account);
            return;
        }

        SymbolDataSet data = c.Data;
        double predictedSlippage = _costModel.OneWaySlippage(
            data.Spread.CostingSpread(), c.Features.SpreadPercentile > 0.85, _executionQuality.SlippageMultiplier);

        BrokerResult result = _execution.Open(
            c.SignalId, c.SymbolName, c.Direction, c.Sizing.VolumeInUnits,
            c.Exit, data.Spec, entryPrice, predictedSlippage,
            c.Features.Atr, data.Spread.CostingSpread(), _journal.Write);

        if (result == null) return;                        // дубль — уже исполнялся
        if (!result.IsSuccessful)
        {
            RecordRejection(c, GateOutcome.Reject(NoTradeReason.BrokerConstraint, result.Error), account);
            return;
        }

        var position = new OpenPosition
        {
            TradeId = Guid.NewGuid().ToString("N"),
            SignalId = c.SignalId,
            BrokerPositionId = result.PositionId,
            Label = _execution.BuildLabel(),
            SymbolName = c.SymbolName,
            Direction = c.Direction,
            StrategyName = c.Ensemble.Leader.StrategyName,
            Regime = c.Regime.Primary,
            EntryTimeUtc = nowUtc,
            RequestedEntryPrice = entryPrice,
            EntryPrice = result.FilledPrice,
            InitialVolumeInUnits = result.FilledVolume,
            CurrentVolumeInUnits = result.FilledVolume,
            InitialStopPrice = c.Exit.StopPrice,
            CurrentStopPrice = c.Exit.StopPrice,
            Target1Price = c.Exit.Target1Price,
            Target2Price = c.Exit.Target2Price,
            RiskPerUnit = Math.Abs(result.FilledPrice - c.Exit.StopPrice),
            RiskAmount = c.Sizing.RiskAmount,
            RiskFractionOfEquity = c.Sizing.RiskPercent / 100.0,
            SignalConfidence = c.Ensemble.Leader.Confidence,
            EnsembleConfidence = c.Ensemble.Confidence,
            EffectiveVotes = c.Ensemble.EffectiveVotes,
            EstimatedWinProbability = c.Probability.PWin,
            ExpectedValueR = c.ExpectedValue.ExpectedValueR,
            PlannedRewardToRisk = c.Exit.RewardToRisk,
            Session = c.Features.Session,
            IsWeekend = c.Features.IsWeekend,
            VolatilityBucket = c.Features.VolatilityBucket,
            AtrAtEntry = c.Features.Atr,
            SpreadAtEntry = c.Features.Spread,
            InvalidationPrice = c.Exit.InvalidationPrice,
            Mode = _config.Mode,
            IsVirtual = _config.Mode == OperatingMode.Shadow,
            MoneyPerPricePerUnit = data.Spec?.MoneyPerPricePerUnit ?? 1.0,
        };

        _positions.Add(position);
        _plans[position.TradeId] = c.Exit;
        _lastSignalBar[c.SymbolName] = c.Features.TimeUtc;

        _journal.RecordAcceptance(BuildRecord(c, accepted: true, null, account, exposure));
    }

    /// <param name="closedSymbol">
    /// Символ, бар которого только что закрылся.
    ///
    /// Счётчик удержания продвигается ТОЛЬКО у позиций этого символа. Время-стоп задан в
    /// барах сигнального таймфрейма конкретного инструмента; если считать закрытия любого
    /// символа, то в портфеле из N инструментов — а эталонный добавляется всегда, значит
    /// N не меньше двух — стоп по времени срабатывает в N раз раньше задуманного.
    ///
    /// Остальные позиции всё равно пересматриваются: цена по ним могла уйти, и стоп или
    /// цель могли стать достижимы.
    /// </param>
    private void ManageOpenPositions(DateTime nowUtc, AccountSnapshot account, string closedSymbol)
    {
        for (int i = _positions.Count - 1; i >= 0; i--)
        {
            OpenPosition p = _positions[i];
            SymbolDataSet data = Data(p.SymbolName);
            if (data == null || !_plans.TryGetValue(p.TradeId, out ExitPlan plan)) continue;

            bool ownBarClosed = string.Equals(p.SymbolName, closedSymbol, StringComparison.Ordinal);

            // Курс пересчёта обновляется на каждом сопровождении: он меняется вместе с
            // курсом валюты счёта к котируемой, а устаревший означает риск, посчитанный по
            // вчерашнему курсу.
            if (data.Spec != null) p.MoneyPerPricePerUnit = data.Spec.MoneyPerPricePerUnit;

            Quote quote = data.LatestQuote;
            if (!quote.IsWellFormed) continue;

            // Цена, по которой позиция была бы закрыта прямо сейчас.
            double exitPrice = p.Direction == Side.Long ? quote.Bid : quote.Ask;
            _latestFeatures.TryGetValue(p.SymbolName, out FeatureVector features);

            IReadOnlyList<ExitAction> actions = _positionManager.Evaluate(
                p, plan, exitPrice, data.Spec, data, features, onClosedBar: ownBarClosed);

            for (int a = 0; a < actions.Count; a++)
            {
                ApplyAction(nowUtc, p, plan, actions[a], exitPrice, account);
                if (p.CurrentVolumeInUnits <= 0) break;
            }

            if (p.CurrentVolumeInUnits <= 0 || p.OutcomeRecorded)
            {
                // Удаление по ссылке, а не по индексу: событие закрытия от платформы могло
                // прийти синхронно внутри ApplyAction и уже сдвинуть список, и тогда
                // RemoveAt(i) убрал бы ЧУЖУЮ позицию.
                _positions.Remove(p);
                _plans.Remove(p.TradeId);
            }
        }
    }

    private void ApplyAction(DateTime nowUtc, OpenPosition p, ExitPlan plan, ExitAction action, double exitPrice, AccountSnapshot account)
    {
        switch (action.Kind)
        {
            case ExitActionKind.MoveStop:
            {
                BrokerResult r = _execution.MoveStop(p.BrokerPositionId, action.NewStopPrice, _journal.Write);
                if (r.IsSuccessful)
                {
                    p.CurrentStopPrice = action.NewStopPrice;
                    if (action.Reason == ExitReason.BreakEven) p.BreakEvenApplied = true;
                    if (action.Reason == ExitReason.TrailingStop) p.TrailingActive = true;
                }
                break;
            }

            case ExitActionKind.PartialClose:
            {
                BrokerResult r = _execution.ClosePartial(p.BrokerPositionId, action.CloseVolumeInUnits, _journal.Write);
                if (r.IsSuccessful)
                {
                    p.CurrentVolumeInUnits -= r.FilledVolume;
                    if (action.Reason == ExitReason.TakeProfit1) p.Target1Filled = true;
                    if (action.Reason == ExitReason.TakeProfit2) p.Target2Filled = true;

                    double perUnit = p.Direction == Side.Long ? r.FilledPrice - p.EntryPrice : p.EntryPrice - r.FilledPrice;
                    p.RealisedProfit += perUnit * r.FilledVolume * p.MoneyPerPricePerUnit;
                }
                break;
            }

            case ExitActionKind.FullClose:
            {
                BrokerResult r = _execution.CloseAll(p.BrokerPositionId, _journal.Write);
                if (r.IsSuccessful)
                {
                    double perUnit = p.Direction == Side.Long ? r.FilledPrice - p.EntryPrice : p.EntryPrice - r.FilledPrice;
                    p.RealisedProfit += perUnit * r.FilledVolume * p.MoneyPerPricePerUnit;
                    p.CurrentVolumeInUnits = 0;
                    CloseTrade(nowUtc, p, r.FilledPrice, action.Reason, account);
                }
                break;
            }
        }
    }

    /// <summary>Фиксирует закрытую сделку во всех слоях памяти системы.</summary>
    public void CloseTrade(DateTime nowUtc, OpenPosition p, double exitPrice, ExitReason reason, AccountSnapshot account)
    {
        // Идемпотентность: позиция учитывается ровно один раз, каким бы путём ни пришло
        // известие о её закрытии.
        if (p.OutcomeRecorded) return;
        p.OutcomeRecorded = true;

        double riskAmount = p.RiskAmount > 0 ? p.RiskAmount : p.RiskPerUnit * p.InitialVolumeInUnits;
        double netProfit = p.RealisedProfit - p.RealisedCommission;
        double r = riskAmount > 0 ? netProfit / riskAmount : 0;

        var record = new TradeRecord
        {
            TradeId = p.TradeId,
            SignalId = p.SignalId,
            BrokerPositionId = p.BrokerPositionId,
            SymbolName = p.SymbolName,
            Direction = p.Direction,
            StrategyName = p.StrategyName,
            Regime = p.Regime,
            RegimeConfidence = _regimes.TryGetValue(p.SymbolName, out RegimeAssessment ra) ? ra.Confidence : 0,
            SignalConfidence = p.SignalConfidence,
            EnsembleConfidence = p.EnsembleConfidence,
            EffectiveVotes = p.EffectiveVotes,
            EstimatedWinProbability = p.EstimatedWinProbability,
            ExpectedValueR = p.ExpectedValueR,
            PlannedRewardToRisk = p.PlannedRewardToRisk,
            Session = p.Session,
            IsWeekend = p.IsWeekend,
            HourUtc = p.EntryTimeUtc.Hour,
            DayOfWeek = p.EntryTimeUtc.DayOfWeek,
            VolatilityBucket = p.VolatilityBucket,
            AtrAtEntry = p.AtrAtEntry,
            SpreadAtEntry = p.SpreadAtEntry,
            EntryTimeUtc = p.EntryTimeUtc,
            ExitTimeUtc = nowUtc,
            RequestedEntryPrice = p.RequestedEntryPrice,
            EntryPrice = p.EntryPrice,
            ExitPrice = exitPrice,
            InitialStopPrice = p.InitialStopPrice,
            InitialTargetPrice = p.Target1Price,
            VolumeInUnits = p.InitialVolumeInUnits,
            RiskAmount = riskAmount,
            RiskFractionOfEquity = p.RiskFractionOfEquity,
            GrossProfit = p.RealisedProfit,
            Commission = p.RealisedCommission,
            NetProfit = netProfit,
            EntrySlippage = p.Direction == Side.Long ? p.EntryPrice - p.RequestedEntryPrice : p.RequestedEntryPrice - p.EntryPrice,
            R = r,
            MaeR = p.MaxAdverseExcursionR,
            MfeR = p.MaxFavourableExcursionR,
            ExitReason = reason,
            IsVirtual = p.IsVirtual,
            Mode = p.Mode,
        };

        _performance.Record(record);
        _probability.Observe(record);
        _risk.OnTradeClosed(nowUtc, record.IsWin);
        TradesClosed++;

        _journal.Write($"ЗАКРЫТА {record}");
    }

    /// <summary>
    /// Обрабатывает позицию, закрытую НА СТОРОНЕ БРОКЕРА — по стопу, тейку или стоп-ауту.
    ///
    /// Этот путь обязателен и не является дублированием управления позициями. Стоп
    /// срабатывает у брокера между барами, и если система узнаёт об этом только на следующем
    /// цикле, то в промежутке она считает позицию открытой: сопровождает несуществующую
    /// позицию, держит под неё риск-бюджет и блокирует новые входы. Событие от брокера —
    /// единственный своевременный источник этого факта.
    /// </summary>
    public void OnBrokerPositionClosed(
        DateTime nowUtc, long brokerPositionId, double exitPrice,
        ExitReason reason, double grossProfit, double commission, double swap)
    {
        for (int i = 0; i < _positions.Count; i++)
        {
            OpenPosition p = _positions[i];
            if (p.BrokerPositionId != brokerPositionId) continue;

            // Прибыль берётся от брокера, а не пересчитывается: его цифра уже включает
            // реальную цену исполнения, комиссию и своп, и расхождение с ней означало бы,
            // что вся статистика системы описывает не те сделки, которые были совершены.
            p.RealisedProfit = grossProfit;
            p.RealisedCommission = commission + Math.Abs(swap);
            p.CurrentVolumeInUnits = 0;

            CloseTrade(nowUtc, p, exitPrice, reason, _broker.GetAccount());

            _positions.Remove(p);
            _plans.Remove(p.TradeId);
            return;
        }
    }

    /// <summary>
    /// Лёгкое сопровождение на тике: только отслеживание экстремумов хода (MAE/MFE).
    ///
    /// Никаких тяжёлых вычислений и никаких обращений к брокеру: тиков много, и всё, что
    /// делается на каждом из них, должно быть дёшево (раздел 149). Решения принимаются на
    /// закрытии бара.
    /// </summary>
    public void OnTick(string symbolName, in Quote quote)
    {
        SymbolDataSet data = Data(symbolName);
        if (data == null) return;

        data.OnQuote(quote);

        for (int i = 0; i < _positions.Count; i++)
        {
            OpenPosition p = _positions[i];
            if (!string.Equals(p.SymbolName, symbolName, StringComparison.Ordinal)) continue;

            double price = p.Direction == Side.Long ? quote.Bid : quote.Ask;
            p.UpdateExcursions(price);
        }

        // Страховка на случай молчащего инструмента: окно сбора кандидатов не должно
        // оставаться открытым до следующего бара только потому, что кто-то не отчитался.
        if (_pendingCandidates.Count > 0) TryCloseCycle(quote.TimeUtc);
    }

    private void MaybeReconcile(DateTime nowUtc)
    {
        if (_config.Mode == OperatingMode.Shadow) return;

        if (_lastReconciliationUtc != DateTime.MinValue &&
            (nowUtc - _lastReconciliationUtc).TotalMinutes < _config.Execution.ReconciliationIntervalMinutes)
        {
            return;
        }

        _lastReconciliationUtc = nowUtc;
        ReconciliationReport report = _execution.Reconcile(_positions);

        if (report.IsConsistent)
        {
            _haltedByReconciliation = false;
            return;
        }

        // Расхождение между тем, что система думает, и тем, что есть у брокера, — это
        // состояние, в котором любое дальнейшее решение принимается вслепую.
        _haltedByReconciliation = true;
        _risk.ForceHalt(nowUtc, "расхождение при сверке: " + report.Summary);
        _journal.Write($"АВАРИЙНАЯ ОСТАНОВКА: {report.Summary}");
    }

    private void MaybeAdapt(DateTime nowUtc)
    {
        if (_lastAdaptationUtc != DateTime.MinValue &&
            (nowUtc - _lastAdaptationUtc).TotalMinutes < _config.Adaptation.AdaptationIntervalMinutes)
        {
            return;
        }

        _lastAdaptationUtc = nowUtc;
        _clusters = _correlation.BuildClusters();

        IReadOnlyList<string> changes = _weights.Review(nowUtc);
        for (int i = 0; i < changes.Count; i++) _journal.Write("АДАПТАЦИЯ: " + changes[i]);

        // Стратегия, снова допущенная к торговле, больше не наблюдается виртуально: иначе
        // одна и та же ситуация породила бы и настоящую сделку, и теневую.
        foreach (KeyValuePair<string, Adaptation.StrategyState> kv in _weights.States)
        {
            if (kv.Value.Status != StrategyStatus.Disabled) _shadow.Forget(kv.Key);
        }
    }

    private int CountPositionsIn(string symbolName)
    {
        int count = 0;
        for (int i = 0; i < _positions.Count; i++)
        {
            if (string.Equals(_positions[i].SymbolName, symbolName, StringComparison.Ordinal)) count++;
        }
        return count;
    }

    private void RecordRejection(TradeCandidate c, GateOutcome outcome, AccountSnapshot account)
    {
        PortfolioExposure exposure = _portfolio.Compute(_positions, account.Equity, _clusters);
        _journal.RecordRejection(BuildRecord(c, accepted: false, outcome, account, exposure));

        // Отказ ставится на виртуальное наблюдение, чтобы позже можно было посчитать, сколько
        // этот фильтр реально сэкономил и сколько стоил (разделы 119–120).
        if (c.Exit != null && c.Exit.IsValid && c.Direction != Side.None)
        {
            _filterLedger.Track(new RejectedSignal
            {
                TimeUtc = c.TimeUtc,
                SymbolName = c.SymbolName,
                Direction = c.Direction,
                StrategyName = c.Leader?.StrategyName,
                Reason = outcome.Reason,
                EntryPrice = c.Features.Price,
                StopDistance = c.Exit.StopDistance,
                TargetDistance = Math.Abs(c.Exit.Target1Price - c.Features.Price),
            });
        }
    }

    private DecisionRecord BuildRecord(TradeCandidate c, bool accepted, GateOutcome outcome, AccountSnapshot account, PortfolioExposure exposure) =>
        new DecisionRecord
        {
            TimeUtc = c.TimeUtc,
            SignalId = c.SignalId,
            SymbolName = c.SymbolName,
            Direction = c.Direction,
            Accepted = accepted,
            Regime = c.Regime?.Primary ?? MarketRegime.Unknown,
            RegimeConfidence = c.Regime?.Confidence ?? 0,
            StrategyName = c.Leader?.StrategyName,
            StrategyConfidence = c.Leader?.Confidence ?? 0,
            EnsembleConfidence = c.Ensemble?.Confidence ?? 0,
            EffectiveVotes = c.Ensemble?.EffectiveVotes ?? 0,
            RawVotes = c.Ensemble?.RawVotes ?? 0,
            WinProbability = c.Probability.PWin,
            ProbabilityConfidence = c.Probability.Confidence,
            RewardToRisk = c.Exit?.RewardToRisk ?? 0,
            ExpectedValueR = c.ExpectedValue?.ExpectedValueR ?? 0,
            EdgeLowerBoundR = c.ExpectedValue?.LowerBoundR ?? 0,
            RequiredEdgeR = c.ExpectedValue?.RequiredEdgeR ?? 0,
            CostR = c.ExpectedValue?.CostR ?? 0,
            StopInAtr = c.Exit?.StopInAtr ?? 0,
            SpreadPercentile = c.Features?.SpreadPercentile ?? 0,
            AtrPercentile = c.Features?.AtrPercentile ?? 0,
            RiskPercent = c.Sizing?.RiskPercent ?? 0,
            PortfolioHeatPercent = exposure.TotalOpenRiskPercent,
            RiskState = _lastRisk.State,
            OpportunityScore = c.OpportunityScore,
            RejectionReason = outcome?.Reason ?? NoTradeReason.None,
            RejectionDetail = outcome?.Detail,
        };

    /// <summary>
    /// Одна строка «я жив и вот что делаю».
    ///
    /// Существует потому, что молчание правильно работающей системы неотличимо от
    /// молчания сломанной. На настройках по умолчанию бот отклоняет подавляющее
    /// большинство сигналов и пишет отказ в журнал только при смене причины — то есть
    /// после первых строк лог замолкает. Без периодического признака жизни единственный
    /// способ узнать, жив ли процесс, — ждать сделку, которой может не быть неделями.
    ///
    /// Печатается по таймеру, а не по рыночным данным: если бар не приходит, это ровно тот
    /// случай, когда сообщение нужнее всего.
    /// </summary>
    public string RenderHeartbeat(DateTime nowUtc)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendFormat("[жив] {0:HH:mm:ss}Z", nowUtc);

        foreach (KeyValuePair<string, SymbolDataSet> kv in _data)
        {
            SymbolDataSet data = kv.Value;
            sb.AppendFormat(" | {0} баров {1}", kv.Key, data.Signal.BarsProcessed);

            if (!data.IsReady)
            {
                long remaining = data.SignalBarsRemaining;
                sb.Append(remaining > 0
                    ? $" — ПРОГРЕВ, нужно ещё {remaining}"
                    : " — ПРОГРЕВ, ждём контекстный таймфрейм");
                continue;
            }

            if (_regimes.TryGetValue(kv.Key, out RegimeAssessment regime) && regime.Primary != MarketRegime.Unknown)
            {
                sb.AppendFormat(" {0}({1:P0})", regime.Primary, regime.Confidence);
            }

            // Закрытый рынок — главная причина, по которой исправный бот ничего не делает.
            // Не назвать её значит оставить человека гадать между «завис» и «ждёт».
            MarketSchedule schedule = ScheduleOf(kv.Key);
            if (!schedule.IsOpenAt(nowUtc))
            {
                int untilOpen = schedule.MinutesUntilOpen(nowUtc);
                sb.Append(untilOpen > 0
                    ? $" — РЫНОК ЗАКРЫТ, откроется через {untilOpen / 60}ч {untilOpen % 60:00}м"
                    : " — РЫНОК ЗАКРЫТ");
            }
        }

        sb.AppendFormat(" | риск {0}", _lastRisk.State);
        if (!_lastRisk.AllowsNewPositions) sb.AppendFormat(" [БЛОКИРОВКА: {0}]", _lastRisk.BlockingReason);

        sb.AppendFormat(" | позиций {0}", _positions.Count);

        long decisions = _journal.TotalAccepted + _journal.TotalRejected;
        sb.AppendFormat(" | решений {0} (принято {1})", decisions, _journal.TotalAccepted);

        if (_journal.TryGetLeadingRejection(out NoTradeReason reason, out double share))
        {
            sb.AppendFormat(" | чаще всего: {0} {1:P0}", reason, share);
        }

        if (_haltedByReconciliation) sb.Append(" | !! ОСТАНОВЛЕН ПО СВЕРКЕ !!");

        return sb.ToString();
    }

    public string RenderDashboard(DateTime nowUtc)
    {
        AccountSnapshot account = _broker.GetAccount();
        PortfolioExposure exposure = _portfolio.Compute(_positions, account.Equity, _clusters);

        return _dashboard.Render(
            nowUtc, _config.Mode, account, _lastRisk, exposure, _positions,
            _regimes, BuildSymbolStatus(nowUtc), _weights.States, _performance, _calibration,
            _executionQuality, _journal);
    }

    /// <summary>
    /// Короткая причина, по которой инструмент сейчас не торгуется, — или null, если
    /// торгуется.
    ///
    /// Существует потому, что «bars=0» само по себе не отвечает на вопрос, который человек
    /// задаёт, глядя на дашборд: это прогрев, закрытый рынок или неисправность. Три
    /// состояния выглядят одинаково и требуют совершенно разных действий.
    /// </summary>
    private IReadOnlyDictionary<string, string> BuildSymbolStatus(DateTime nowUtc)
    {
        var status = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (KeyValuePair<string, SymbolDataSet> kv in _data)
        {
            MarketSchedule schedule = ScheduleOf(kv.Key);
            if (!schedule.IsOpenAt(nowUtc))
            {
                int untilOpen = schedule.MinutesUntilOpen(nowUtc);
                status[kv.Key] = untilOpen > 0
                    ? $"РЫНОК ЗАКРЫТ, откроется через {untilOpen / 60}ч {untilOpen % 60:00}м"
                    : "РЫНОК ЗАКРЫТ";
                continue;
            }

            if (!kv.Value.IsReady)
            {
                long remaining = kv.Value.SignalBarsRemaining;
                status[kv.Key] = remaining > 0
                    ? $"ПРОГРЕВ: нужно ещё {remaining} баров {_config.Data.SignalTimeframe}"
                    : $"ПРОГРЕВ: ждём {_config.Data.ContextTimeframe}";
                continue;
            }

            status[kv.Key] = null;
        }

        return status;
    }

    // --- Персистентность ------------------------------------------------------------------

    private void SaveState(DateTime nowUtc, AccountSnapshot account)
    {
        var state = new BotState
        {
            InstanceId = _instanceId,
            SavedAtUtc = nowUtc,
            Mode = (int)_config.Mode,
            RiskState = (int)_risk.State,
            ConsecutiveLosses = _risk.ConsecutiveLosses,
            ConsecutiveWins = _risk.ConsecutiveWins,
            CooldownUntilUtc = _risk.CooldownUntilUtc,
            DayStartUtc = nowUtc.Date,
            DayStartEquity = _risk.DayStartEquity,
            WeekStartUtc = nowUtc.Date.AddDays(-(((int)nowUtc.DayOfWeek + 6) % 7)),
            WeekStartEquity = _risk.WeekStartEquity,
            AllTimePeakEquity = _drawdown.AllTimePeak,
            LastEquity = account.Equity,
            TotalTradesRecorded = TradesClosed,
        };

        foreach (string id in _idempotency.ExecutedIds) state.ExecutedSignalIds.Add(id);
        foreach (KeyValuePair<string, DateTime> kv in _lastSignalBar) state.LastSignalBarUtc[kv.Key] = kv.Value;

        for (int i = 0; i < _positions.Count; i++) state.Positions.Add(ToPersisted(_positions[i]));

        foreach (KeyValuePair<string, Adaptation.StrategyState> kv in _weights.States)
        {
            state.Strategies.Add(new PersistedStrategy
            {
                Name = kv.Key,
                Status = (int)kv.Value.Status,
                Weight = kv.Value.Weight,
                DisabledAtUtc = kv.Value.DisabledAtUtc,
                RecoveringSinceUtc = kv.Value.RecoveringSinceUtc,
                ShadowTrades = kv.Value.ShadowTrades,
                DisableCount = kv.Value.DisableCount,
                LastStatusReason = kv.Value.LastStatusReason,
            });
        }

        _stateStore.Save(state);
    }

    /// <summary>
    /// Восстанавливает состояние после рестарта (раздел 146).
    ///
    /// Позиции берутся от БРОКЕРА, а их контекст — из сохранённого состояния. Порядок именно
    /// такой: брокер — источник истины о том, что открыто, сохранённое состояние — источник
    /// истины о том, почему. Доверять сохранённому состоянию в вопросе наличия позиции значит
    /// рисковать управлением позицией, которой больше нет, или игнорированием той, которая
    /// есть.
    /// </summary>
    public void Restore(DateTime nowUtc)
    {
        BotState state = _stateStore.Load();
        AccountSnapshot account = _broker.GetAccount();

        IReadOnlyList<BrokerPosition> brokerPositions = _execution.GetOpenPositions();

        if (state == null)
        {
            if (brokerPositions.Count > 0)
            {
                // Позиции есть, а контекста к ним нет. Управлять ими вслепую нельзя, закрывать
                // их самовольно — тоже: это чужое решение. Система останавливается и говорит об этом.
                _haltedByReconciliation = true;
                _risk.ForceHalt(nowUtc, $"{brokerPositions.Count} позиций у брокера без сохранённого состояния");
                _journal.Write($"ОСТАНОВКА ПРИ СТАРТЕ: у брокера {brokerPositions.Count} позиций, сохранённого состояния нет. Требуется вмешательство.");
            }
            return;
        }

        _risk.Restore(
            (RiskState)state.RiskState, state.ConsecutiveLosses, state.ConsecutiveWins, state.CooldownUntilUtc,
            state.DayStartUtc, state.DayStartEquity, state.WeekStartUtc, state.WeekStartEquity, nowUtc);

        _drawdown.Restore(state.AllTimePeakEquity, account.Equity, nowUtc);
        _idempotency.Restore(state.ExecutedSignalIds);
        TradesClosed = state.TotalTradesRecorded;

        foreach (KeyValuePair<string, DateTime> kv in state.LastSignalBarUtc) _lastSignalBar[kv.Key] = kv.Value;

        foreach (PersistedStrategy s in state.Strategies)
        {
            Adaptation.StrategyState target = _weights.StateOf(s.Name);
            target.Status = (Adaptation.StrategyStatus)s.Status;
            target.Weight = s.Weight;
            target.DisabledAtUtc = s.DisabledAtUtc;
            target.RecoveringSinceUtc = s.RecoveringSinceUtc;
            target.ShadowTrades = s.ShadowTrades;
            target.DisableCount = s.DisableCount;
            target.LastStatusReason = s.LastStatusReason;
        }

        // Сопоставляем позиции брокера с сохранённым контекстом.
        var persistedById = new Dictionary<long, PersistedPosition>();
        for (int i = 0; i < state.Positions.Count; i++) persistedById[state.Positions[i].BrokerPositionId] = state.Positions[i];

        int recovered = 0, orphaned = 0;
        var matched = new HashSet<long>();

        for (int i = 0; i < brokerPositions.Count; i++)
        {
            BrokerPosition bp = brokerPositions[i];
            if (!persistedById.TryGetValue(bp.PositionId, out PersistedPosition pp)) { orphaned++; continue; }

            OpenPosition p = FromPersisted(pp, bp);
            _positions.Add(p);
            _plans[p.TradeId] = RebuildPlan(pp, p);
            matched.Add(bp.PositionId);
            recovered++;
        }

        // Позиции, которые были открыты, а у брокера их больше нет: пока робот был
        // выключен, сработал стоп, цель или ручное закрытие.
        //
        // Просто забыть их нельзя. Их исход не попал бы ни в статистику, ни в счётчик
        // серии убытков, ни в калибровку — то есть перезапуск бесшумно стирал бы
        // проигранные сделки, а именно они должны были бы ужесточить постуру риска.
        int closedWhileOffline = 0;
        for (int i = 0; i < state.Positions.Count; i++)
        {
            PersistedPosition pp = state.Positions[i];
            if (matched.Contains(pp.BrokerPositionId)) continue;

            RecordPositionClosedWhileOffline(nowUtc, pp);
            closedWhileOffline++;
        }

        if (closedWhileOffline > 0)
        {
            _journal.Write($"ВОССТАНОВЛЕНИЕ: {closedWhileOffline} позиций закрылись, пока робот был выключен — " +
                           "их исходы учтены в статистике и в счётчике серии.");
        }

        _journal.Write($"ВОССТАНОВЛЕНИЕ: {recovered} позиций восстановлено, {orphaned} без контекста, " +
                       $"постура риска {(RiskState)state.RiskState}, серия убытков {state.ConsecutiveLosses}.");

        if (orphaned > 0)
        {
            _haltedByReconciliation = true;
            _risk.ForceHalt(nowUtc, $"{orphaned} позиций у брокера не имеют сохранённого контекста");
            return;
        }

        FinishRestore(nowUtc, state, persistedById);
    }

    /// <summary>
    /// Доводит восстановление до состояния, в котором можно принимать решения.
    ///
    /// Сопоставить позиции по идентификатору мало. Пока робот был выключен, позиция могла
    /// быть частично закрыта, её стоп мог быть сдвинут или снят вовсе, а сигнал, чей ордер
    /// ушёл в таймаут, остался помеченным как исполненный. Каждое из этих состояний
    /// выглядит рабочим и ведёт к неверным решениям:
    ///
    ///   * расхождение объёма — риск считается по несуществующему объёму;
    ///   * снятый стоп — позиция без защиты, а система считает, что защита есть;
    ///   * зависшая отметка — сигнал заблокирован навсегда, сделок по нему не будет.
    ///
    /// Поэтому после сопоставления идёт полная сверка, а не ожидание её по расписанию.
    /// </summary>
    private void FinishRestore(DateTime nowUtc, BotState state, Dictionary<long, PersistedPosition> persistedById)
    {
        // 1. Курс пересчёта в валюту счёта. Без него портфельный риск после рестарта
        //    считается по единице, то есть в котируемой валюте.
        for (int i = 0; i < _positions.Count; i++)
        {
            OpenPosition p = _positions[i];
            SymbolSpec spec = Data(p.SymbolName)?.Spec;
            if (spec != null) p.MoneyPerPricePerUnit = spec.MoneyPerPricePerUnit;

            if (!persistedById.TryGetValue(p.BrokerPositionId, out PersistedPosition pp)) continue;

            // 2. Расхождение объёма — частичное закрытие во время простоя. Брокер здесь
            //    источник правды, но молчать об этом нельзя: часть сделки закрылась, и её
            //    результат в статистику не попал.
            double tolerance = Math.Max(1e-6, pp.InitialVolumeInUnits * 1e-4);
            if (Math.Abs(pp.CurrentVolumeInUnits - p.CurrentVolumeInUnits) > tolerance)
            {
                _journal.Write($"ВОССТАНОВЛЕНИЕ: #{p.BrokerPositionId} {p.SymbolName} объём изменился за время " +
                               $"простоя: было {pp.CurrentVolumeInUnits:F6}, у брокера {p.CurrentVolumeInUnits:F6}. " +
                               "Принят объём брокера; результат закрытой части в статистику не попадёт.");
            }

            // 3. Стоп у брокера отличается от сохранённого либо отсутствует.
            if (Math.Abs(pp.CurrentStopPrice - p.CurrentStopPrice) > 1e-9)
            {
                _journal.Write($"ВОССТАНОВЛЕНИЕ: #{p.BrokerPositionId} {p.SymbolName} стоп у брокера " +
                               $"{p.CurrentStopPrice:F5}, сохранён был {pp.CurrentStopPrice:F5}. Принят стоп брокера.");
            }
        }

        // 4. Позиция без стопа — это позиция без ограничения убытка. Защита
        //    восстанавливается немедленно: ждать следующего бара значит держать капитал
        //    открытым ровно в том состоянии, которого вся система призвана не допускать.
        RestoreMissingStops(nowUtc);

        // 5. Отметки исполнения, оставшиеся от ордеров с неизвестным исходом. Если позиции
        //    по такому сигналу нет, значит ордер до биржи не дошёл, и держать сигнал
        //    заблокированным больше не за что.
        ReleaseStaleIdempotencyMarks();

        // 6. Полная сверка — сразу, не дожидаясь расписания.
        _lastReconciliationUtc = DateTime.MinValue;
        MaybeReconcile(nowUtc);
    }

    /// <summary>Возвращает стоп-лосс позициям, у которых его не оказалось у брокера.</summary>
    private void RestoreMissingStops(DateTime nowUtc)
    {
        IReadOnlyList<BrokerPosition> brokerPositions = _execution.GetOpenPositions();
        var stopById = new Dictionary<long, double?>();
        for (int i = 0; i < brokerPositions.Count; i++)
        {
            stopById[brokerPositions[i].PositionId] = brokerPositions[i].StopLoss;
        }

        for (int i = 0; i < _positions.Count; i++)
        {
            OpenPosition p = _positions[i];
            if (!stopById.TryGetValue(p.BrokerPositionId, out double? brokerStop)) continue;
            if (brokerStop.HasValue && brokerStop.Value > 0) continue;

            _journal.Write($"ВОССТАНОВЛЕНИЕ: #{p.BrokerPositionId} {p.SymbolName} у брокера БЕЗ СТОПА — " +
                           $"восстанавливаю {p.CurrentStopPrice:F5}.");

            BrokerResult r = _execution.MoveStop(p.BrokerPositionId, p.CurrentStopPrice, _journal.Write);
            if (r == null || !r.IsSuccessful)
            {
                _haltedByReconciliation = true;
                _risk.ForceHalt(nowUtc, $"позиция #{p.BrokerPositionId} осталась без стоп-лосса: {r?.Error ?? "нет ответа"}");
            }
        }
    }

    /// <summary>
    /// Снимает отметки исполнения с сигналов, по которым позиции не оказалось.
    ///
    /// Отметка ставится ДО отправки ордера и не снимается, когда исход неизвестен, — иначе
    /// повтор открыл бы дубль. Но после рестарта неизвестность разрешима: если позиции по
    /// сигналу нет ни у брокера, ни в памяти, значит ордер не исполнился, и сигнал должен
    /// снова стать доступным.
    /// </summary>
    private void ReleaseStaleIdempotencyMarks()
    {
        var live = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < _positions.Count; i++)
        {
            if (!string.IsNullOrEmpty(_positions[i].SignalId)) live.Add(_positions[i].SignalId);
        }

        IReadOnlyList<TradeRecord> closed = _performance.Trades;
        if (closed != null)
        {
            for (int i = 0; i < closed.Count; i++)
            {
                if (!string.IsNullOrEmpty(closed[i].SignalId)) live.Add(closed[i].SignalId);
            }
        }

        var stale = new List<string>();
        foreach (string id in _idempotency.ExecutedIds)
        {
            if (!live.Contains(id)) stale.Add(id);
        }

        for (int i = 0; i < stale.Count; i++) _idempotency.Release(stale[i]);

        if (stale.Count > 0)
        {
            _journal.Write($"ВОССТАНОВЛЕНИЕ: снято {stale.Count} отметок исполнения без позиции — " +
                           "эти ордера до биржи не дошли, сигналы снова доступны.");
        }
    }

    /// <summary>
    /// Учитывает позицию, закрывшуюся, пока робот был выключен.
    ///
    /// Точная цена выхода неизвестна: брокер её уже не показывает, а история сделок может
    /// быть недоступна. Исход поэтому оценивается КОНСЕРВАТИВНО — как срабатывание стопа.
    /// Это может недооценить результат, если на самом деле сработала цель, и такая ошибка
    /// предпочтительна: она ужесточает постуру риска, тогда как противоположная ослабила бы
    /// её на основании догадки.
    /// </summary>
    private void RecordPositionClosedWhileOffline(DateTime nowUtc, PersistedPosition pp)
    {
        var side = (Side)pp.Direction;
        double exitPrice = pp.CurrentStopPrice > 0 ? pp.CurrentStopPrice : pp.InitialStopPrice;

        double perUnit = side == Side.Long ? exitPrice - pp.EntryPrice : pp.EntryPrice - exitPrice;
        double conversion = Data(pp.SymbolName)?.Spec?.MoneyPerPricePerUnit ?? 1.0;
        double gross = perUnit * pp.CurrentVolumeInUnits * conversion;
        double riskAmount = pp.RiskAmount > 0 ? pp.RiskAmount : pp.RiskPerUnit * pp.InitialVolumeInUnits;

        var record = new TradeRecord
        {
            TradeId = pp.TradeId,
            SignalId = pp.SignalId,
            BrokerPositionId = pp.BrokerPositionId,
            SymbolName = pp.SymbolName,
            Direction = side,
            StrategyName = pp.StrategyName,
            Regime = (MarketRegime)pp.Regime,
            EnsembleConfidence = pp.EnsembleConfidence,
            EstimatedWinProbability = pp.EstimatedWinProbability,
            ExpectedValueR = pp.ExpectedValueR,
            PlannedRewardToRisk = pp.PlannedRewardToRisk,
            HourUtc = pp.EntryTimeUtc.Hour,
            DayOfWeek = pp.EntryTimeUtc.DayOfWeek,
            Session = SessionClassifier.Classify(pp.EntryTimeUtc),
            IsWeekend = SessionClassifier.IsWeekend(pp.EntryTimeUtc),
            AtrAtEntry = pp.AtrAtEntry,
            SpreadAtEntry = pp.SpreadAtEntry,
            EntryTimeUtc = pp.EntryTimeUtc,
            ExitTimeUtc = nowUtc,
            EntryPrice = pp.EntryPrice,
            RequestedEntryPrice = pp.EntryPrice,
            ExitPrice = exitPrice,
            InitialStopPrice = pp.InitialStopPrice,
            InitialTargetPrice = pp.Target1Price,
            VolumeInUnits = pp.InitialVolumeInUnits,
            RiskAmount = riskAmount,
            RiskFractionOfEquity = pp.RiskFractionOfEquity,
            GrossProfit = gross,
            NetProfit = gross,
            R = riskAmount > 0 ? gross / riskAmount : 0,
            MaeR = Math.Max(pp.MaxAdverseExcursionR, 1.0),
            MfeR = pp.MaxFavourableExcursionR,
            ExitReason = ExitReason.ManualOrExternal,
            Mode = _config.Mode,
        };

        _performance.Record(record);
        _probability.Observe(record);
        _risk.OnTradeClosed(nowUtc, record.IsWin);
        TradesClosed++;
    }

    private static PersistedPosition ToPersisted(OpenPosition p) => new PersistedPosition
    {
        TradeId = p.TradeId, SignalId = p.SignalId, BrokerPositionId = p.BrokerPositionId, Label = p.Label,
        SymbolName = p.SymbolName, Direction = (int)p.Direction, StrategyName = p.StrategyName, Regime = (int)p.Regime,
        EntryTimeUtc = p.EntryTimeUtc, EntryPrice = p.EntryPrice,
        InitialVolumeInUnits = p.InitialVolumeInUnits, CurrentVolumeInUnits = p.CurrentVolumeInUnits,
        InitialStopPrice = p.InitialStopPrice, CurrentStopPrice = p.CurrentStopPrice,
        Target1Price = p.Target1Price, Target2Price = p.Target2Price,
        RiskPerUnit = p.RiskPerUnit, RiskAmount = p.RiskAmount, RiskFractionOfEquity = p.RiskFractionOfEquity,
        EnsembleConfidence = p.EnsembleConfidence, EstimatedWinProbability = p.EstimatedWinProbability,
        ExpectedValueR = p.ExpectedValueR, PlannedRewardToRisk = p.PlannedRewardToRisk,
        MaxFavourableExcursionR = p.MaxFavourableExcursionR, MaxAdverseExcursionR = p.MaxAdverseExcursionR,
        PeakOpenProfitR = p.PeakOpenProfitR, Target1Filled = p.Target1Filled, Target2Filled = p.Target2Filled,
        BreakEvenApplied = p.BreakEvenApplied, BarsHeld = p.BarsHeld,
        AtrAtEntry = p.AtrAtEntry, SpreadAtEntry = p.SpreadAtEntry,
        InvalidationPrice = p.InvalidationPrice ?? 0, HasInvalidation = p.InvalidationPrice.HasValue,
    };

    private static OpenPosition FromPersisted(PersistedPosition pp, BrokerPosition bp) => new OpenPosition
    {
        TradeId = pp.TradeId, SignalId = pp.SignalId, BrokerPositionId = bp.PositionId, Label = pp.Label,
        SymbolName = pp.SymbolName, Direction = (Side)pp.Direction, StrategyName = pp.StrategyName,
        Regime = (MarketRegime)pp.Regime, EntryTimeUtc = pp.EntryTimeUtc, EntryPrice = pp.EntryPrice,
        InitialVolumeInUnits = pp.InitialVolumeInUnits,
        // Объём берётся от БРОКЕРА: он мог измениться, пока робот был выключен.
        CurrentVolumeInUnits = bp.VolumeInUnits,
        InitialStopPrice = pp.InitialStopPrice,
        CurrentStopPrice = bp.StopLoss ?? pp.CurrentStopPrice,
        Target1Price = pp.Target1Price, Target2Price = pp.Target2Price,
        RiskPerUnit = pp.RiskPerUnit, RiskAmount = pp.RiskAmount, RiskFractionOfEquity = pp.RiskFractionOfEquity,
        EnsembleConfidence = pp.EnsembleConfidence, EstimatedWinProbability = pp.EstimatedWinProbability,
        ExpectedValueR = pp.ExpectedValueR, PlannedRewardToRisk = pp.PlannedRewardToRisk,
        AtrAtEntry = pp.AtrAtEntry, SpreadAtEntry = pp.SpreadAtEntry,
        InvalidationPrice = pp.HasInvalidation ? pp.InvalidationPrice : (double?)null,
        MaxFavourableExcursionR = pp.MaxFavourableExcursionR,
        MaxAdverseExcursionR = pp.MaxAdverseExcursionR,
        PeakOpenProfitR = pp.PeakOpenProfitR,
        Target1Filled = pp.Target1Filled, Target2Filled = pp.Target2Filled,
        BreakEvenApplied = pp.BreakEvenApplied, BarsHeld = pp.BarsHeld,
    };

    private ExitPlan RebuildPlan(PersistedPosition pp, OpenPosition p) => new ExitPlan
    {
        IsValid = true,
        StopPrice = p.CurrentStopPrice,
        StopDistance = pp.RiskPerUnit,
        StopInAtr = pp.AtrAtEntry > 0 ? pp.RiskPerUnit / pp.AtrAtEntry : 1.5,
        Target1Price = pp.Target1Price,
        Target2Price = pp.Target2Price,
        Target1R = _config.Exit.Target1R,
        Target2R = _config.Exit.Target2R,
        Target1ClosePercent = _config.Exit.Target1ClosePercent,
        Target2ClosePercent = _config.Exit.Target2ClosePercent,
        BreakEvenTriggerR = _config.Exit.BreakEvenTriggerR,
        NetBreakEvenPrice = p.Direction == Side.Long
            ? p.EntryPrice + (pp.RiskPerUnit * _config.Exit.BreakEvenBufferR)
            : p.EntryPrice - (pp.RiskPerUnit * _config.Exit.BreakEvenBufferR),
        TrailDistanceInAtr = _config.Exit.TrendTrailAtr,
        TrailActivationR = _config.Exit.TrailActivationR,
        TimeStopBars = _config.Exit.TimeStopBarsTrend,
        InvalidationPrice = p.InvalidationPrice,
    };
}
