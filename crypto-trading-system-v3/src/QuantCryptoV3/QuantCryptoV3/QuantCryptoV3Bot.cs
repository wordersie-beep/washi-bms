using System;
using System.Collections.Generic;
using cAlgo.API;
using cAlgo.API.Internals;
using Quant.Core;
using Quant.Core.Config;
using Quant.Core.Execution;
using Quant.Core.Primitives;
using Quant.Core.Stats;
using Candle = Quant.Core.Primitives.Candle;
using Quote = Quant.Core.Primitives.Quote;
using Tf = Quant.Core.Primitives.Tf;

namespace Quant.Bot;

/// <summary>
/// Точка входа cBot.
///
/// Класс намеренно тонкий: он переводит события платформы в вызовы
/// <see cref="TradingEngine"/> и ничего не решает сам. Вся торговая логика живёт в ядре,
/// которое не знает о cTrader и потому целиком покрыто тестами.
///
/// Совместимость с Cloud обеспечивается тремя свойствами, каждое из которых проверяется
/// самой сборкой:
///   * AccessRights.None — ни файловой системы, ни сети, ни реестра;
///   * ни одного обращения к Chart, ChartObjects или любому элементу интерфейса;
///   * только ссылки времени компиляции — весь .algo это одна сборка.
/// </summary>
[Robot(AccessRights = AccessRights.None, AddIndicators = false)]
public class QuantCryptoV3Bot : Robot
{
    // ================= ПАРАМЕТРЫ: РЕЖИМ =================

    [Parameter("Режим работы", Group = "Режим", DefaultValue = OperatingMode.Shadow)]
    public OperatingMode Mode { get; set; }

    [Parameter("Подтверждаю реальную торговлю", Group = "Режим", DefaultValue = false)]
    public bool LiveTradingAcknowledged { get; set; }

    [Parameter("Доп. символы (через запятую)", Group = "Режим", DefaultValue = "")]
    public string AdditionalSymbols { get; set; }

    /// <summary>
    /// Таймфрейм, на закрытии которого принимаются решения.
    ///
    /// ЭТО, а не таймфрейм графика, определяет работу бота. Раньше он был зашит в код, и
    /// запуск на минутном графике выглядел как «бот не работает»: он исправно торговал по
    /// пятиминуткам и игнорировал выбор пользователя.
    /// </summary>
    [Parameter("Сигнальный таймфрейм", Group = "Режим", DefaultValue = Tf.M5)]
    public Tf SignalTimeframe { get; set; }

    /// <summary>
    /// Старший таймфрейм, дающий трендовый контекст. Обязан быть медленнее сигнального.
    ///
    /// Чем он медленнее, тем больше истории нужно для прогрева: требуется 60 его баров.
    /// Для связки M1/H1 это 60 часов, и бот скажет об этом при старте.
    /// </summary>
    [Parameter("Контекстный таймфрейм", Group = "Режим", DefaultValue = Tf.H1)]
    public Tf ContextTimeframe { get; set; }

    [Parameter("Эталонный символ", Group = "Режим", DefaultValue = "BTCUSD")]
    public string BenchmarkSymbol { get; set; }

    [Parameter("Дашборд каждые N минут", Group = "Режим", DefaultValue = 60, MinValue = 5, MaxValue = 1440)]
    public int DashboardIntervalMinutes { get; set; }

    [Parameter("Признак жизни каждые N минут", Group = "Режим", DefaultValue = 15, MinValue = 1, MaxValue = 240)]
    public int HeartbeatIntervalMinutes { get; set; }

    [Parameter("Подробный журнал отказов", Group = "Режим", DefaultValue = false)]
    public bool VerboseJournal { get; set; }

    // ================= ПАРАМЕТРЫ: РИСК =================

    [Parameter("Риск на сделку, %", Group = "Риск", DefaultValue = 0.35, MinValue = 0.01, MaxValue = 2.0, Step = 0.05)]
    public double RiskPerTradePercent { get; set; }

    [Parameter("Жёсткий предел риска, %", Group = "Риск", DefaultValue = 1.0, MinValue = 0.1, MaxValue = 3.0, Step = 0.1)]
    public double HardMaxRiskPercent { get; set; }

    [Parameter("Общий риск портфеля, %", Group = "Риск", DefaultValue = 2.0, MinValue = 0.2, MaxValue = 6.0, Step = 0.1)]
    public double MaxTotalOpenRiskPercent { get; set; }

    [Parameter("Дневной лимит убытка, %", Group = "Риск", DefaultValue = 2.0, MinValue = 0.5, MaxValue = 10.0, Step = 0.25)]
    public double DailyLossLimitPercent { get; set; }

    [Parameter("Недельный лимит убытка, %", Group = "Риск", DefaultValue = 5.0, MinValue = 1.0, MaxValue = 20.0, Step = 0.5)]
    public double WeeklyLossLimitPercent { get; set; }

    [Parameter("Макс. просадка 24ч, %", Group = "Риск", DefaultValue = 4.0, MinValue = 1.0, MaxValue = 20.0, Step = 0.5)]
    public double MaxDrawdown24hPercent { get; set; }

    [Parameter("Макс. просадка всего, %", Group = "Риск", DefaultValue = 18.0, MinValue = 5.0, MaxValue = 50.0, Step = 1.0)]
    public double MaxDrawdownAllTimePercent { get; set; }

    [Parameter("Макс. одновременных позиций", Group = "Риск", DefaultValue = 4, MinValue = 1, MaxValue = 10)]
    public int MaxOpenPositions { get; set; }

    // ================= ПАРАМЕТРЫ: СИГНАЛ =================

    [Parameter("Мин. уверенность режима", Group = "Сигнал", DefaultValue = 0.55, MinValue = 0.3, MaxValue = 0.95, Step = 0.05)]
    public double MinRegimeConfidence { get; set; }

    [Parameter("Мин. уверенность ансамбля", Group = "Сигнал", DefaultValue = 0.55, MinValue = 0.3, MaxValue = 0.95, Step = 0.05)]
    public double MinEnsembleConfidence { get; set; }

    [Parameter("Макс. вес одной стратегии", Group = "Сигнал", DefaultValue = 0.40, MinValue = 0.15, MaxValue = 0.60, Step = 0.05)]
    public double MaxSingleStrategyWeight { get; set; }

    // ================= ПАРАМЕТРЫ: ПРЕИМУЩЕСТВО =================

    [Parameter("Базовое мин. преимущество, R", Group = "Преимущество", DefaultValue = 0.10, MinValue = 0.0, MaxValue = 0.5, Step = 0.02)]
    public double BaseMinimumEdgeR { get; set; }

    [Parameter("Сигм для нижней границы EV", Group = "Преимущество", DefaultValue = 1.0, MinValue = 0.0, MaxValue = 3.0, Step = 0.25)]
    public double EdgeConfidenceZ { get; set; }

    [Parameter("Мин. соотношение риск/прибыль", Group = "Преимущество", DefaultValue = 1.3, MinValue = 0.8, MaxValue = 4.0, Step = 0.1)]
    public double MinRewardToRisk { get; set; }

    [Parameter("Сила байесовского априора", Group = "Преимущество", DefaultValue = 25.0, MinValue = 5.0, MaxValue = 100.0, Step = 5.0)]
    public double PriorStrength { get; set; }

    // ================= ПАРАМЕТРЫ: ВЫХОДЫ =================

    [Parameter("Стоп, ATR", Group = "Выходы", DefaultValue = 1.6, MinValue = 0.5, MaxValue = 5.0, Step = 0.1)]
    public double AtrStopMultiple { get; set; }

    [Parameter("Цель 1, R", Group = "Выходы", DefaultValue = 1.2, MinValue = 0.5, MaxValue = 5.0, Step = 0.1)]
    public double Target1R { get; set; }

    [Parameter("Цель 2, R", Group = "Выходы", DefaultValue = 2.2, MinValue = 0.8, MaxValue = 10.0, Step = 0.1)]
    public double Target2R { get; set; }

    [Parameter("Безубыток при, R", Group = "Выходы", DefaultValue = 0.9, MinValue = 0.2, MaxValue = 3.0, Step = 0.1)]
    public double BreakEvenTriggerR { get; set; }

    [Parameter("Трейлинг тренда, ATR", Group = "Выходы", DefaultValue = 2.2, MinValue = 0.8, MaxValue = 6.0, Step = 0.1)]
    public double TrendTrailAtr { get; set; }

    // ================= ПАРАМЕТРЫ: ИСПОЛНЕНИЕ =================

    [Parameter("Макс. проскальзывание, ATR", Group = "Исполнение", DefaultValue = 0.25, MinValue = 0.05, MaxValue = 2.0, Step = 0.05)]
    public double MaxSlippageInAtr { get; set; }

    [Parameter("Макс. проскальзывание, спредов", Group = "Исполнение", DefaultValue = 3.0, MinValue = 1.0, MaxValue = 20.0, Step = 0.5)]
    public double MaxSlippageSpreadMultiple { get; set; }

    [Parameter("Сверка каждые N минут", Group = "Исполнение", DefaultValue = 5, MinValue = 1, MaxValue = 60)]
    public int ReconciliationIntervalMinutes { get; set; }

    // ================= ВНУТРЕННЕЕ СОСТОЯНИЕ =================

    private TradingEngine _engine;
    private EngineConfig _config;
    private CTraderBroker _broker;

    /// <summary>Ссылки на серии удерживаются, иначе подписки на события пропадают.</summary>
    private readonly List<Bars> _subscribedBars = new List<Bars>();
    private readonly List<string> _symbols = new List<string>();
    private readonly Dictionary<Tf, TimeFrame> _timeframes = new Dictionary<Tf, TimeFrame>();

    private DateTime _lastDashboardUtc = DateTime.MinValue;
    private bool _initialisationFailed;

    protected override void OnStart()
    {
        try
        {
            _config = BuildConfig();

            IReadOnlyList<string> problems = _config.Validate();
            if (problems.Count > 0)
            {
                Print("КОНФИГУРАЦИЯ ОТКЛОНЕНА — торговля не начата:");
                for (int i = 0; i < problems.Count; i++) Print("  * " + problems[i]);
                _initialisationFailed = true;
                Stop();
                return;
            }

            // Защита режима. Несоответствие режима и типа счёта — не мелочь: параметр,
            // выставленный по ошибке, не должен иметь возможности двигать реальные деньги,
            // и одинаково не должен «бумажный» прогон незаметно оказаться боевым.
            if (!ValidateModeAgainstAccount()) { _initialisationFailed = true; Stop(); return; }

            _broker = new CTraderBroker(this);

            _engine = new TradingEngine(
                _config, _broker, new LocalStorageStateStore(this),
                new CTraderJournalSink(this), InstanceId);

            _engine.Journal.Verbose = VerboseJournal;

            BuildTimeframeMap();
            RegisterSymbols();
            SubscribeToBars();
            WarmUpHistory();

            Positions.Closed += OnPositionClosed;

            _engine.Restore(Server.TimeInUtc);

            // Признак жизни идёт по ТАЙМЕРУ, а не по рыночным данным. Если бары перестали
            // приходить, это ровно тот случай, когда сообщение нужнее всего, — а событийный
            // путь в этот момент молчит вместе с рынком.
            Timer.Start(TimeSpan.FromMinutes(Math.Max(1, HeartbeatIntervalMinutes)));

            Print($"QuantCryptoV3 запущен. Режим {_config.Mode}. Символы: {string.Join(", ", _symbols)}. " +
                  $"Счёт {(Account.IsLive ? "РЕАЛЬНЫЙ" : "демо")}, капитал {Account.Equity:F2} {Account.Asset.Name}.");

            Print($"Решения принимаются на закрытии {_config.Data.SignalTimeframe}, контекст — {_config.Data.ContextTimeframe}. " +
                  $"Таймфрейм графика на это не влияет.");
            Print($"Для начала работы нужно {_config.Data.MinBarsBeforeTrading} баров {_config.Data.SignalTimeframe} " +
                  $"(≈{_config.Data.MinBarsBeforeTrading * (int)_config.Data.SignalTimeframe / 60.0:F1} ч) и " +
                  $"{_config.Data.MinBarsPerTimeframe} баров {_config.Data.ContextTimeframe} " +
                  $"(≈{_config.Data.MinBarsPerTimeframe * (int)_config.Data.ContextTimeframe / 60.0:F1} ч).");

            ReportTradingHours();

            Print(_engine.RenderHeartbeat(Server.TimeInUtc));
            Print($"Признак жизни будет печататься каждые {HeartbeatIntervalMinutes} мин, дашборд — каждые {DashboardIntervalMinutes} мин.");
            Print("Если бот не торгует — это штатное поведение: настройки по умолчанию отклоняют " +
                  "подавляющее большинство сигналов. Причины видны в признаке жизни и в дашборде.");

            Print(_engine.RenderDashboard(Server.TimeInUtc));
        }
        catch (Exception ex)
        {
            Print($"ОШИБКА ЗАПУСКА: {ex.GetType().Name}: {ex.Message}");
            _initialisationFailed = true;
            Stop();
        }
    }

    /// <summary>
    /// Сверяет заявленный режим с типом счёта.
    ///
    /// Обе несостыковки разбираются отдельно, потому что они опасны по-разному:
    /// «Live на демо-счёте» — это, скорее всего, безобидная ошибка настройки, и система
    /// продолжает как Demo; «Demo на реальном счёте» — это ошибка, при которой человек
    /// считает, что не рискует, а рискует, и торговля запрещается.
    /// </summary>
    private bool ValidateModeAgainstAccount()
    {
        if (Mode == OperatingMode.Live && !Account.IsLive)
        {
            Print("Режим Live выбран на демо-счёте. Работа продолжается в режиме Demo.");
            _config.Mode = OperatingMode.Demo;
            return true;
        }

        if (Mode == OperatingMode.Demo && Account.IsLive)
        {
            Print("ОТКАЗ: выбран режим Demo, но счёт РЕАЛЬНЫЙ. " +
                  "Для торговли на реальном счёте выберите режим Live и подтвердите его явно.");
            return false;
        }

        if (_config.Mode == OperatingMode.Live && Account.IsLive && !LiveTradingAcknowledged)
        {
            Print("ОТКАЗ: реальная торговля требует явного подтверждения параметром " +
                  "«Подтверждаю реальную торговлю».");
            return false;
        }

        return true;
    }

    private void BuildTimeframeMap()
    {
        _timeframes[Tf.M1] = TimeFrame.Minute;
        _timeframes[Tf.M3] = TimeFrame.Minute3;
        _timeframes[Tf.M5] = TimeFrame.Minute5;
        _timeframes[Tf.M15] = TimeFrame.Minute15;
        _timeframes[Tf.H1] = TimeFrame.Hour;
        _timeframes[Tf.H4] = TimeFrame.Hour4;
    }

    private void RegisterSymbols()
    {
        _symbols.Add(SymbolName);

        if (!string.IsNullOrWhiteSpace(AdditionalSymbols))
        {
            string[] extra = AdditionalSymbols.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < extra.Length; i++)
            {
                string name = extra[i].Trim();
                if (name.Length > 0 && !_symbols.Contains(name) && Symbols.Exists(name)) _symbols.Add(name);
                else if (name.Length > 0 && !Symbols.Exists(name)) Print($"Символ '{name}' недоступен — пропущен.");
            }
        }

        // Эталон нужен для рыночного контекста, даже если им не торгуют.
        if (!_symbols.Contains(BenchmarkSymbol) && Symbols.Exists(BenchmarkSymbol))
        {
            _symbols.Add(BenchmarkSymbol);
        }

        for (int i = 0; i < _symbols.Count; i++)
        {
            Symbol symbol = Symbols.GetSymbol(_symbols[i]);
            if (symbol == null) continue;

            _engine.AddSymbol(_symbols[i], ToSpec(symbol), ToSchedule(symbol));
            symbol.Tick += OnSymbolTick;
        }
    }

    private void SubscribeToBars()
    {
        for (int s = 0; s < _symbols.Count; s++)
        {
            foreach (Tf tf in _config.Data.Timeframes)
            {
                if (!_timeframes.TryGetValue(tf, out TimeFrame platformTimeframe)) continue;

                Bars bars = MarketData.GetBars(platformTimeframe, _symbols[s]);
                if (bars == null) continue;

                _subscribedBars.Add(bars);

                string symbolName = _symbols[s];
                Tf captured = tf;

                // BarOpened означает, что ПРЕДЫДУЩИЙ бар закрылся. Именно его и передаём:
                // строящийся бар не должен попадать ни в один индикатор.
                bars.BarOpened += args => OnBarOpened(symbolName, captured, args.Bars);
            }
        }
    }

    /// <summary>
    /// Прогоняет историю через движок, чтобы индикаторы и перцентили были готовы к первому
    /// решению, а не набирались неделю на живом рынке.
    ///
    /// Бары подаются с флагом прогрева: они наполняют ряды, признаки и режим, но не
    /// запускают оценку риска, сверку, попытки входа и сохранение состояния. Полный цикл
    /// на исторических барах означал бы сотни решений по ценам, которых уже нет, и дневную
    /// точку отсчёта, привязанную к историческому дню.
    /// </summary>
    private void WarmUpHistory()
    {
        for (int i = 0; i < _subscribedBars.Count; i++)
        {
            Bars bars = _subscribedBars[i];
            Tf tf = FromPlatform(bars.TimeFrame);
            if (tf == 0) continue;

            int count = Math.Min(bars.Count - 1, _config.Data.BarHistory);
            for (int b = bars.Count - 1 - count; b < bars.Count - 1; b++)
            {
                if (b < 0) continue;
                _engine.OnBarClosed(
                    DateTime.SpecifyKind(bars.OpenTimes[b], DateTimeKind.Utc),
                    bars.SymbolName, tf, ToCandle(bars, b), isWarmUp: true);
            }
        }
    }

    private void OnBarOpened(string symbolName, Tf timeframe, Bars bars)
    {
        if (_initialisationFailed || _engine == null || bars.Count < 2) return;

        try
        {
            // Индекс Count-2 — только что закрывшийся бар. Count-1 ещё формируется.
            int closedIndex = bars.Count - 2;
            _engine.OnBarClosed(Server.TimeInUtc, symbolName, timeframe, ToCandle(bars, closedIndex));
        }
        catch (Exception ex)
        {
            Print($"ОШИБКА на закрытии бара {symbolName} {timeframe}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void OnSymbolTick(SymbolTickEventArgs args)
    {
        if (_initialisationFailed || _engine == null) return;

        try
        {
            _engine.OnTick(args.SymbolName, new Quote(Server.TimeInUtc, args.Bid, args.Ask));
        }
        catch (Exception)
        {
            // Тиков очень много: логировать здесь — значит утопить журнал. Сбой на одном
            // тике безвреден, потому что решения принимаются на закрытии бара.
        }
    }

    /// <summary>
    /// Вся периодическая отчётность идёт отсюда, по таймеру.
    ///
    /// Раньше она висела на OnTick, и это было ошибкой: OnTick вызывается только для
    /// инструмента графика и только когда идут тики. При остановке фида, на выходных или
    /// при потере связи вывод замолкал — то есть именно тогда, когда по нему больше всего
    /// нужно было судить о состоянии бота.
    /// </summary>
    protected override void OnTimer()
    {
        if (_initialisationFailed || _engine == null) return;

        try
        {
            DateTime now = Server.TimeInUtc;

            Print(_engine.RenderHeartbeat(now));

            if (_lastDashboardUtc == DateTime.MinValue) _lastDashboardUtc = now;
            if ((now - _lastDashboardUtc).TotalMinutes >= DashboardIntervalMinutes)
            {
                _lastDashboardUtc = now;
                Print(_engine.RenderDashboard(now));
            }
        }
        catch (Exception ex)
        {
            Print($"ОШИБКА при выводе состояния: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void OnPositionClosed(PositionClosedEventArgs args)
    {
        if (_initialisationFailed || _engine == null) return;

        try
        {
            Position p = args.Position;
            if (p.Label == null || !p.Label.StartsWith(_config.Execution.OrderLabelPrefix, StringComparison.Ordinal)) return;

            ExitReason reason = args.Reason switch
            {
                PositionCloseReason.StopLoss => ExitReason.StopLoss,
                PositionCloseReason.TakeProfit => ExitReason.TakeProfit1,
                PositionCloseReason.StopOut => ExitReason.EmergencyShutdown,
                _ => ExitReason.ManualOrExternal,
            };

            _engine.OnBrokerPositionClosed(
                Server.TimeInUtc, p.Id, p.CurrentPrice, reason, p.GrossProfit, p.Commissions, p.Swap);
        }
        catch (Exception ex)
        {
            Print($"ОШИБКА при обработке закрытия позиции: {ex.GetType().Name}: {ex.Message}");
        }
    }

    protected override void OnError(Error error)
    {
        Print($"ОШИБКА ТОРГОВОЙ ОПЕРАЦИИ: {error.Code}");
    }

    protected override void OnStop()
    {
        if (_engine == null) return;

        try
        {
            Print(_engine.RenderDashboard(Server.TimeInUtc));
            Print(_engine.FilterLedger.Report());

            PerformanceReport report = MetricsCalculator.Compute(
                _engine.Performance.Trades, Account.Balance, _engine.Risk.LastRuinEstimate.Probability);

            Print(report.Render());
        }
        catch (Exception ex)
        {
            Print($"ОШИБКА при остановке: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Фитнес для оптимизатора (раздел 133).
    ///
    /// Использует собственный журнал сделок системы, а не только агрегаты платформы: только
    /// в нём есть R, MAE, MFE и концентрация прибыли — то есть именно то, что отличает
    /// устойчивый набор параметров от подогнанного. Аргументы платформы остаются запасным
    /// вариантом на случай, когда журнал пуст.
    /// </summary>
    protected override double GetFitness(GetFitnessArgs args)
    {
        try
        {
            if (_engine == null || _engine.Performance.TotalTrades < FitnessCalculator.MinimumTradesForFitness)
            {
                return 0;
            }

            PerformanceReport report = MetricsCalculator.Compute(
                _engine.Performance.Trades, args.Equity > 0 ? args.Equity : 10000);

            var bySymbol = new Dictionary<string, double>(StringComparer.Ordinal);
            IReadOnlyList<TradeRecord> trades = _engine.Performance.Trades;
            for (int i = 0; i < trades.Count; i++)
            {
                bySymbol.TryGetValue(trades[i].SymbolName, out double current);
                bySymbol[trades[i].SymbolName] = current + trades[i].NetProfit;
            }

            return FitnessCalculator.Compute(report, null, bySymbol).Fitness;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    // ================= ПРЕОБРАЗОВАНИЯ =================

    private static Candle ToCandle(Bars bars, int index) => new Candle(
        DateTime.SpecifyKind(bars.OpenTimes[index], DateTimeKind.Utc),
        bars.OpenPrices[index], bars.HighPrices[index], bars.LowPrices[index], bars.ClosePrices[index],
        bars.TickVolumes[index]);

    /// <summary>
    /// Переводит описание инструмента платформы в нейтральный вид.
    ///
    /// Две детали, которые легко упустить и которые дорого стоят:
    ///
    ///   МИНИМАЛЬНАЯ ДИСТАНЦИЯ СТОПА публикуется либо в пипсах, либо в ПРОЦЕНТАХ от цены —
    ///   это решает <see cref="Symbol.MinDistanceType"/>. Принять её за цену значит получить
    ///   либо бессмысленно широкий стоп, либо отказ брокера, причём молча и только на тех
    ///   инструментах, где выбран второй вариант.
    ///
    ///   ПЛЕЧО у криптовалютных CFD почти всегда ступенчатое. Одна «основная» цифра
    ///   занижает требуемую маржу для крупной позиции, а занижение маржи — это ровно та
    ///   ошибка, которая заканчивается margin call, а не отказом в сделке.
    /// </summary>
    /// <summary>
    /// Печатает расписание торгов каждого инструмента и предупреждает о тех, что не
    /// торгуются круглосуточно.
    ///
    /// Это первое, что нужно человеку, если бот «не работает». Закрытый рынок и нехватка
    /// истории — две причины бездействия, которые невозможно отличить по пустому журналу,
    /// и обе снимаются одной строкой при старте.
    /// </summary>
    private void ReportTradingHours()
    {
        for (int i = 0; i < _symbols.Count; i++)
        {
            Symbol symbol = Symbols.GetSymbol(_symbols[i]);
            if (symbol == null) continue;

            MarketSchedule schedule = _engine.ScheduleOf(_symbols[i]);
            bool openNow = true;
            string openState;

            try
            {
                openNow = symbol.MarketHours.IsOpened();
                openState = openNow ? "рынок ОТКРЫТ" : "рынок ЗАКРЫТ";

                if (!openNow)
                {
                    TimeSpan tillOpen = symbol.MarketHours.TimeTillOpen();
                    if (tillOpen > TimeSpan.Zero)
                    {
                        openState += $", откроется через {(int)tillOpen.TotalHours}ч {tillOpen.Minutes:00}м";
                    }
                }
            }
            catch (Exception)
            {
                openState = "состояние рынка платформа не сообщила";
            }

            Print($"{_symbols[i]}: {schedule.Describe()} | {openState}");
        }

        IReadOnlyList<string> notContinuous = _engine.SymbolsNotTrading24x7();
        if (notContinuous.Count > 0)
        {
            Print($"ВНИМАНИЕ: {string.Join(", ", notContinuous)} торгуются НЕ круглосуточно. " +
                  "Система рассчитана на непрерывный рынок: на открытии сессии приходит гэп, " +
                  "который стоп-лосс не удерживает, а статистика режимов собирается по рваной серии. " +
                  "Для работы 24/7 выберите крипто-символ без перерывов.");
        }
        else
        {
            Print("Все инструменты торгуются круглосуточно — режим 24/7 доступен.");
        }
    }

    /// <summary>
    /// Читает недельное расписание торгов инструмента у платформы.
    ///
    /// Система рассчитана на круглосуточный рынок. Инструмент с перерывами не вызывает ни
    /// одной ошибки — он просто перестаёт присылать бары, и внешне это неотличимо от
    /// зависшего бота. Расписание превращает догадку в строку журнала.
    /// </summary>
    private static MarketSchedule ToSchedule(Symbol s)
    {
        try
        {
            if (s?.MarketHours?.Sessions == null) return MarketSchedule.Unknown;

            var windows = new List<TradingSessionWindow>();
            foreach (TradingSession session in s.MarketHours.Sessions)
            {
                windows.Add(new TradingSessionWindow(
                    session.StartDay, session.StartTime, session.EndDay, session.EndTime));
            }

            return MarketSchedule.FromSessions(windows);
        }
        catch (Exception)
        {
            // Расписание — диагностика, а не условие работы. Его отсутствие не повод
            // отказываться от запуска.
            return MarketSchedule.Unknown;
        }
    }

    private static SymbolSpec ToSpec(Symbol s)
    {
        double minStopDistancePrice = s.MinDistanceType == SymbolMinDistanceType.Percentage
            ? s.Bid * s.MinStopLossDistance / 100.0
            : s.MinStopLossDistance * s.PipSize;

        var tiers = new List<Quant.Core.Primitives.LeverageTier>();
        double headline = 1.0;

        var dynamicLeverage = s.DynamicLeverage;
        if (dynamicLeverage != null)
        {
            for (int i = 0; i < dynamicLeverage.Count; i++)
            {
                cAlgo.API.Internals.LeverageTier tier = dynamicLeverage[i];
                if (tier.Leverage <= 0) continue;

                tiers.Add(new Quant.Core.Primitives.LeverageTier(tier.Volume, tier.Leverage));
                if (tier.Leverage > headline) headline = tier.Leverage;
            }
        }

        return new SymbolSpec(
            s.Name, s.PipSize, s.TickSize, s.Digits,
            s.VolumeInUnitsMin, s.VolumeInUnitsMax, s.VolumeInUnitsStep,
            s.Commission, s.PipValue, minStopDistancePrice,
            headline,
            s.TradingMode == SymbolTradingMode.FullAccess,
            tiers.Count > 0 ? tiers.ToArray() : null);
    }

    private Tf FromPlatform(TimeFrame timeFrame)
    {
        foreach (KeyValuePair<Tf, TimeFrame> kv in _timeframes)
        {
            if (kv.Value == timeFrame) return kv.Key;
        }
        return 0;
    }

    private EngineConfig BuildConfig()
    {
        var config = new EngineConfig
        {
            Mode = Mode,
            LiveTradingAcknowledged = LiveTradingAcknowledged,
        };

        config.Sizing.RiskPerTradePercent = RiskPerTradePercent;
        config.Risk.HardMaxRiskPerTradePercent = HardMaxRiskPercent;
        config.Risk.MaxTotalOpenRiskPercent = MaxTotalOpenRiskPercent;
        config.Risk.DailyLossLimitPercent = DailyLossLimitPercent;
        config.Risk.WeeklyLossLimitPercent = WeeklyLossLimitPercent;
        config.Risk.MaxDrawdown24hPercent = MaxDrawdown24hPercent;
        config.Risk.MaxDrawdownAllTimePercent = MaxDrawdownAllTimePercent;

        config.Portfolio.MaxOpenPositions = MaxOpenPositions;
        config.Portfolio.BenchmarkSymbol = BenchmarkSymbol;

        // Сигнальный, контекстный, корреляционный и набор агрегируемых таймфреймов
        // выставляются одним вызовом, чтобы они не могли разойтись между собой.
        config.UseTimeframes(SignalTimeframe, ContextTimeframe);

        config.Regime.MinConfidenceToTrade = MinRegimeConfidence;
        config.Strategy.MinEnsembleConfidence = MinEnsembleConfidence;
        config.Strategy.MaxSingleStrategyWeight = MaxSingleStrategyWeight;

        config.Ev.BaseMinimumEdgeR = BaseMinimumEdgeR;
        config.Ev.EdgeConfidenceZ = EdgeConfidenceZ;
        config.Ev.MinRewardToRisk = MinRewardToRisk;
        config.Probability.PriorStrength = PriorStrength;

        config.Exit.AtrStopMultiple = AtrStopMultiple;
        config.Exit.Target1R = Target1R;
        config.Exit.Target2R = Target2R;
        config.Exit.BreakEvenTriggerR = BreakEvenTriggerR;
        config.Exit.TrendTrailAtr = TrendTrailAtr;

        config.Execution.MaxSlippageInAtr = MaxSlippageInAtr;
        config.Execution.MaxSlippageSpreadMultiple = MaxSlippageSpreadMultiple;
        config.Execution.ReconciliationIntervalMinutes = ReconciliationIntervalMinutes;

        return config;
    }
}
