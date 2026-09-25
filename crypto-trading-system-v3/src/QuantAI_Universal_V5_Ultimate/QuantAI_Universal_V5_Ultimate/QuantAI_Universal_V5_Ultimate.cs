// =====================================================================================================
//  QuantAI_Universal_V5_Ultimate — адаптивный AI-cBot для cTrader (API 4.x / cTrader.Automate)
//
//  Один файл. Работает на любом инструменте графика: BTCUSD, ETHUSD, XAUUSD, EURUSD и т. д.
//  Все расстояния (стоп, цель, трейлинг, допустимый спред) выражены в ATR(14) — поэтому один и тот же
//  набор настроек применим к инструментам с совершенно разной ценой и волатильностью.
//
//  СОСТАВ
//   1. Ансамбль из 5 подстратегий: TrendFollowing (EMA 50/200), Breakout (Donchian 20 + Keltner),
//      MeanReversion (RSI + Bollinger), VolatilityExpansion (всплеск ATR), LiquidityGrab (снятие
//      ликвидности с микро-экстремумов). Сигнал — взвешенная сумма голосов с учётом AI-весов.
//   2. AI-модуль:
//      • онлайн Gaussian Naive Bayes по вектору [ATR_Ratio, RSI_Slope, ADX, Spread_Cost, Vol_Percentile]
//        оценивает вероятность того, что сделка дойдёт до +1R раньше стопа (с учётом спреда);
//      • обучение с подкреплением: каждая закрытая сделка (Win/Loss, в R) меняет веса подстратегий,
//        голосовавших за неё; веса ограничены [0.25 … 3.0] — ни одна стратегия не может ни исчезнуть,
//        ни захватить ансамбль;
//      • вероятностный фильтр режима: P(тренд) и P(возврат к среднему) — эвристика по ADX и Efficiency
//        Ratio, дообучаемая онлайн-логистической регрессией на исходах рынка;
//      • итоговый Signal Confidence 0.00–1.00 и адаптивный порог входа (тренд / флэт).
//   3. Холодный старт: при запуске модель обучается на загруженной истории графика через виртуальные
//      (теневые) сделки — по тем же правилам и с тем же спредом. Далее теневые сделки продолжаются онлайн,
//      поэтому модель учится и тогда, когда реальных сделок нет.
//   4. Риск: лот от % Equity/Balance и ATR-стопа, жёсткий потолок риска, дневной лимит убытка,
//      R:R не ниже заданного минимума, EV-фильтр (спред не более 15% потенциальной прибыли),
//      блокировка межсессионного окна 21:50–22:20 UTC.
//   5. Сопровождение: закрытие 50% объёма на +1.0R, перенос стопа в безубыток + 0.1R, ATR-трейлинг.
//   6. Журнал: причина каждого пропуска (SKIP), режим рынка, просадка, состояние AI.
//
//  ЧЕГО ЗДЕСЬ НЕТ И НЕ БУДЕТ: мартингейла, усреднения, увеличения лота после убытка. Размер позиции
//  может только уменьшаться относительно заданного риска (кроме округления до минимального лота брокера,
//  которое разрешено лишь в пределах «Потолка риска»).
//
//  ВАЖНО: ни один алгоритм не гарантирует прибыль. Модель учится на прошлом; рынок может измениться.
//  Сначала — демо-счёт.
// =====================================================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    /// <summary>От чего считать риск на сделку.</summary>
    public enum RiskBaseMode
    {
        Equity,
        Balance,
    }

    /// <summary>Фильтр ложных пробоев (снятие ликвидности). Auto — включается для золота (XAU*).</summary>
    public enum SweepProtectionMode
    {
        Auto,
        On,
        Off,
    }

    // =================================================================================================
    //  ВСПОМОГАТЕЛЬНЫЕ ТИПЫ
    // =================================================================================================

    /// <summary>Причины отказа от входа — для журнала и сводки.</summary>
    internal enum SkipReason
    {
        None = 0,
        Warmup,
        NoSignal,
        UnclearRegime,
        LowConfidence,
        NegativeEV,
        HighSpread,
        CostVsProfit,
        SessionBlock,
        DailyLossLimit,
        PositionOpen,
        Cooldown,
        SweepBlock,
        AccountTooSmall,
        MarketClosed,
        OrderFailed,
    }

    /// <summary>Голос одной подстратегии.</summary>
    internal struct StrategyVote
    {
        public int Direction;   // +1 покупка, -1 продажа, 0 нет мнения
        public double Strength; // 0..1
    }

    /// <summary>Всё, что известно о закрытом баре после анализа.</summary>
    internal sealed class BarAnalysis
    {
        public int Index;
        public double Close;
        public double Atr;
        public double AtrLongAverage;
        public double Adx;
        public double Rsi;
        public double RsiSlope;          // пункты RSI за бар, без знака направления
        public double VolPercentile;     // 0..1
        public double EfficiencyRatio;   // 0..1
        public double TrendSlopeSign;    // знак наклона EMA20
        public double PTrend;            // вероятность трендового режима
        public double RegimeConfidence;  // |2·PTrend − 1|
        public readonly StrategyVote[] Votes = new StrategyVote[QuantAI_Universal_V5_Ultimate.StrategyCount];
        public int EnsembleDirection;
        public double EnsembleScore;     // 0..1
        public int AgreeingCount;
        public bool BullishSweepRecent;  // снятие ликвидности снизу (ловушка для продавцов)
        public bool BearishSweepRecent;  // снятие ликвидности сверху (ловушка для покупателей)
    }

    /// <summary>
    /// Взвешенная онлайн-статистика признака (алгоритм Уэста): среднее и дисперсия без хранения выборки.
    /// </summary>
    internal sealed class GaussianStat
    {
        public double W;
        public double Mean;
        public double M2;

        public void Add(double x, double weight)
        {
            if (weight <= 0 || double.IsNaN(x) || double.IsInfinity(x)) return;
            double newW = W + weight;
            double delta = x - Mean;
            double r = delta * weight / newW;
            Mean += r;
            M2 += W * delta * r;
            W = newW;
        }

        public double Variance => W > 0 ? Math.Max(0, M2 / W) : 0;

        /// <summary>Объединяет две статистики (для совмещения реальных и теневых наблюдений).</summary>
        public static void Merge(GaussianStat a, GaussianStat b, out double w, out double mean, out double variance)
        {
            w = a.W + b.W;
            if (w <= 0)
            {
                mean = 0;
                variance = 0;
                return;
            }

            mean = ((a.W * a.Mean) + (b.W * b.Mean)) / w;
            double delta = a.Mean - b.Mean;
            double m2 = a.M2 + b.M2 + (a.W * b.W / w * delta * delta);
            variance = Math.Max(0, m2 / w);
        }
    }

    /// <summary>
    /// Онлайн Gaussian Naive Bayes: класс 1 — сделка дошла до +1R раньше стопа, класс 0 — нет.
    /// </summary>
    internal sealed class NaiveBayesModel
    {
        public const int FeatureCount = 5;

        public readonly GaussianStat[,] Stats = new GaussianStat[2, FeatureCount];
        public readonly double[] ClassWeight = new double[2];

        public NaiveBayesModel()
        {
            for (int c = 0; c < 2; c++)
            {
                for (int f = 0; f < FeatureCount; f++) Stats[c, f] = new GaussianStat();
            }
        }

        public double TotalWeight => ClassWeight[0] + ClassWeight[1];

        public void Add(double[] x, bool win, double weight)
        {
            if (x == null || x.Length != FeatureCount || weight <= 0) return;
            int c = win ? 1 : 0;
            ClassWeight[c] += weight;
            for (int f = 0; f < FeatureCount; f++) Stats[c, f].Add(x[f], weight);
        }

        public void Clear()
        {
            ClassWeight[0] = 0;
            ClassWeight[1] = 0;
            for (int c = 0; c < 2; c++)
            {
                for (int f = 0; f < FeatureCount; f++) Stats[c, f] = new GaussianStat();
            }
        }

        /// <summary>
        /// Апостериорная вероятность выигрыша по объединённым реальным и теневым наблюдениям.
        /// Пока данных мало, ответ сжимается к нейтральным 0.5: модель без доказательств не имеет права
        /// ни разгонять, ни душить вход.
        /// </summary>
        public static double PredictCombined(NaiveBayesModel real, NaiveBayesModel shadow, double[] x, double[] varianceFloor, double trustSamples)
        {
            double totalW = real.TotalWeight + shadow.TotalWeight;
            if (x == null || totalW <= 0) return 0.5;

            double[] logp = new double[2];
            for (int c = 0; c < 2; c++)
            {
                double classW = real.ClassWeight[c] + shadow.ClassWeight[c];
                logp[c] = Math.Log((classW + 1.0) / (totalW + 2.0));

                for (int f = 0; f < FeatureCount; f++)
                {
                    GaussianStat.Merge(real.Stats[c, f], shadow.Stats[c, f], out double w, out double mean, out double variance);
                    if (w < 3) continue; // признак без данных не голосует

                    double v = Math.Max(variance, varianceFloor[f]);
                    double d = x[f] - mean;
                    logp[c] += (-0.5 * Math.Log(2.0 * Math.PI * v)) - (d * d / (2.0 * v));
                }
            }

            double diff = logp[0] - logp[1];
            double raw = diff > 50 ? 0.0 : diff < -50 ? 1.0 : 1.0 / (1.0 + Math.Exp(diff));

            // Naive Bayes известен самоуверенностью: доверие растёт с выборкой, крайние значения срезаны.
            double trust = totalW / (totalW + Math.Max(1.0, trustSamples));
            double p = 0.5 + ((raw - 0.5) * trust);
            return Math.Max(0.10, Math.Min(0.90, p));
        }
    }

    /// <summary>Онлайн-логистическая регрессия (SGD с L2) — дообучение вероятности трендового режима.</summary>
    internal sealed class OnlineLogistic
    {
        private readonly double[] _w;

        public OnlineLogistic(int inputs)
        {
            _w = new double[inputs];
        }

        public int Updates { get; private set; }

        public double Predict(double[] x)
        {
            double z = 0;
            for (int k = 0; k < _w.Length && k < x.Length; k++) z += _w[k] * x[k];
            if (z > 30) return 1.0;
            if (z < -30) return 0.0;
            return 1.0 / (1.0 + Math.Exp(-z));
        }

        public void Update(double[] x, double y, double learningRate, double l2)
        {
            double error = y - Predict(x);
            for (int k = 0; k < _w.Length && k < x.Length; k++)
            {
                _w[k] += learningRate * ((error * x[k]) - (l2 * _w[k]));
            }
            Updates++;
        }
    }

    /// <summary>Виртуальная (теневая) сделка: источник знаний, когда реальных сделок нет.</summary>
    internal sealed class ShadowTrade
    {
        public int OpenIndex;
        public int Direction;
        public double Entry;
        public double Stop;
        public double Target;
        public double RiskDistance;
        public double[] Features;
        public double[] VoteStrengths; // сила голоса каждой стратегии в направлении сделки (0, если не голосовала)
    }

    /// <summary>Отложенная метка режима: исход становится известен через горизонт баров.</summary>
    internal sealed class RegimeSample
    {
        public int Index;
        public double[] Input;
        public int Direction;
        public double Close;
        public double Atr;
    }

    /// <summary>Сведения о реальной позиции, нужные для сопровождения и обучения.</summary>
    internal sealed class TradeMeta
    {
        public int Direction;
        public double EntryPrice;
        public double InitialRiskDistance; // 1R в цене
        public double RiskMoney;
        public bool PartialDone;
        public bool BreakevenDone;
        public double RealizedPartialProfit;
        public double[] Features;
        public double[] VoteStrengths;
        public int OpenBarIndex;
    }

    // =================================================================================================
    //  РОБОТ
    // =================================================================================================

    [Robot(AccessRights = AccessRights.None, TimeZone = TimeZones.UTC, AddIndicators = true)]
    public class QuantAI_Universal_V5_Ultimate : Robot
    {
        public const int StrategyCount = 5;

        private static readonly string[] StrategyNames =
        {
            "TrendFollowing", "Breakout", "MeanReversion", "VolExpansion", "LiquidityGrab",
        };

        // Какие стратегии относятся к трендовому режиму (остальные — к режиму возврата к среднему).
        private static readonly bool[] IsTrendStrategy = { true, true, false, true, false };

        // ----- Параметры: AI и пороги --------------------------------------------------------------

        [Parameter("Мин. уверенность режима", Group = "AI и пороги", DefaultValue = 0.30, MinValue = 0.0, MaxValue = 1.0, Step = 0.01)]
        public double MinRegimeConfidence { get; set; }

        [Parameter("Мин. уверенность ансамбля", Group = "AI и пороги", DefaultValue = 0.30, MinValue = 0.0, MaxValue = 1.0, Step = 0.01)]
        public double MinEnsembleConfidence { get; set; }

        [Parameter("Адаптивный порог AI", Group = "AI и пороги", DefaultValue = true)]
        public bool AdaptiveThreshold { get; set; }

        [Parameter("ADX сильного тренда", Group = "AI и пороги", DefaultValue = 25.0, MinValue = 10.0, MaxValue = 60.0, Step = 1.0)]
        public double TrendAdxLevel { get; set; }

        [Parameter("Порог в тренде", Group = "AI и пороги", DefaultValue = 0.28, MinValue = 0.0, MaxValue = 1.0, Step = 0.01)]
        public double TrendThreshold { get; set; }

        [Parameter("ADX флэта (ниже)", Group = "AI и пороги", DefaultValue = 20.0, MinValue = 5.0, MaxValue = 40.0, Step = 1.0)]
        public double FlatAdxLevel { get; set; }

        [Parameter("Порог во флэте", Group = "AI и пороги", DefaultValue = 0.40, MinValue = 0.0, MaxValue = 1.0, Step = 0.01)]
        public double FlatThreshold { get; set; }

        [Parameter("Требовать EV > 0", Group = "AI и пороги", DefaultValue = true)]
        public bool RequirePositiveEv { get; set; }

        [Parameter("Скорость RL-обучения", Group = "AI и пороги", DefaultValue = 0.10, MinValue = 0.0, MaxValue = 0.5, Step = 0.01)]
        public double RlLearningRate { get; set; }

        [Parameter("Обучение на истории (баров)", Group = "AI и пороги", DefaultValue = 3000, MinValue = 400, MaxValue = 20000, Step = 100)]
        public int HistoryBars { get; set; }

        [Parameter("Сбросить память AI", Group = "AI и пороги", DefaultValue = false)]
        public bool ResetAiMemory { get; set; }

        // ----- Параметры: риск ---------------------------------------------------------------------

        [Parameter("Риск на сделку, %", Group = "Риск", DefaultValue = 0.5, MinValue = 0.01, MaxValue = 5.0, Step = 0.05)]
        public double RiskPercent { get; set; }

        [Parameter("База риска", Group = "Риск", DefaultValue = RiskBaseMode.Equity)]
        public RiskBaseMode RiskBase { get; set; }

        [Parameter("Потолок риска (мин. лот), %", Group = "Риск", DefaultValue = 1.5, MinValue = 0.05, MaxValue = 10.0, Step = 0.05)]
        public double MaxRiskCapPercent { get; set; }

        [Parameter("Дневной лимит убытка, %", Group = "Риск", DefaultValue = 3.0, MinValue = 0.1, MaxValue = 50.0, Step = 0.1)]
        public double DailyLossLimitPercent { get; set; }

        [Parameter("Макс. позиций", Group = "Риск", DefaultValue = 1, MinValue = 1, MaxValue = 10)]
        public int MaxOpenPositions { get; set; }

        [Parameter("Пауза после сделки, баров", Group = "Риск", DefaultValue = 2, MinValue = 0, MaxValue = 100)]
        public int CooldownBars { get; set; }

        // ----- Параметры: выходы -------------------------------------------------------------------

        [Parameter("Стоп, ATR", Group = "Выходы", DefaultValue = 1.5, MinValue = 0.3, MaxValue = 10.0, Step = 0.1)]
        public double SlAtrMultiplier { get; set; }

        [Parameter("Тейк, ATR", Group = "Выходы", DefaultValue = 3.0, MinValue = 0.3, MaxValue = 30.0, Step = 0.1)]
        public double TpAtrMultiplier { get; set; }

        [Parameter("Мин. Risk/Reward", Group = "Выходы", DefaultValue = 1.5, MinValue = 1.0, MaxValue = 10.0, Step = 0.1)]
        public double MinRewardRisk { get; set; }

        [Parameter("Безубыток при, R", Group = "Выходы", DefaultValue = 1.0, MinValue = 0.2, MaxValue = 5.0, Step = 0.1)]
        public double BreakevenTriggerR { get; set; }

        [Parameter("Смещение безубытка, R", Group = "Выходы", DefaultValue = 0.1, MinValue = 0.0, MaxValue = 1.0, Step = 0.05)]
        public double BreakevenOffsetR { get; set; }

        [Parameter("Частичное закрытие", Group = "Выходы", DefaultValue = true)]
        public bool UsePartialClose { get; set; }

        [Parameter("Доля частичного закрытия, %", Group = "Выходы", DefaultValue = 50.0, MinValue = 10.0, MaxValue = 90.0, Step = 5.0)]
        public double PartialClosePercent { get; set; }

        [Parameter("Трейлинг, ATR", Group = "Выходы", DefaultValue = 1.5, MinValue = 0.3, MaxValue = 10.0, Step = 0.1)]
        public double TrailAtrMultiplier { get; set; }

        // ----- Параметры: фильтры ------------------------------------------------------------------

        [Parameter("Макс. спред, доля ATR", Group = "Фильтры", DefaultValue = 0.30, MinValue = 0.01, MaxValue = 2.0, Step = 0.01)]
        public double MaxSpreadAtr { get; set; }

        [Parameter("Макс. спред, доля прибыли", Group = "Фильтры", DefaultValue = 0.15, MinValue = 0.01, MaxValue = 1.0, Step = 0.01)]
        public double MaxSpreadCostOfProfit { get; set; }

        [Parameter("Блок сессии с (UTC)", Group = "Фильтры", DefaultValue = "21:50")]
        public string SessionBlockStart { get; set; }

        [Parameter("Блок сессии до (UTC)", Group = "Фильтры", DefaultValue = "22:20")]
        public string SessionBlockEnd { get; set; }

        [Parameter("Защита от ложных пробоев", Group = "Фильтры", DefaultValue = SweepProtectionMode.Auto)]
        public SweepProtectionMode SweepProtection { get; set; }

        // ----- Параметры: журнал -------------------------------------------------------------------

        [Parameter("Печатать каждый SKIP", Group = "Журнал", DefaultValue = true)]
        public bool VerboseSkips { get; set; }

        [Parameter("Сводка каждые N баров", Group = "Журнал", DefaultValue = 12, MinValue = 1, MaxValue = 1000)]
        public int StatusEveryBars { get; set; }

        [Parameter("Метка ордеров", Group = "Журнал", DefaultValue = "QAIv5")]
        public string OrderLabel { get; set; }

        // ----- Константы алгоритма (структура, а не настройки) -------------------------------------

        private const int AtrPeriod = 14;
        private const int AtrLongPeriod = 100;
        private const int VolPercentileWindow = 200;
        private const int DonchianPeriod = 20;
        private const int MicroExtremePeriod = 10;
        private const int EfficiencyPeriod = 20;
        private const int RegimeHorizon = 12;
        private const int ShadowMaxHoldBars = 60;
        private const double ShadowMinScore = 0.15;
        private const double ShadowWeight = 0.5;
        private const double NaiveBayesTrustSamples = 40.0;
        private const int WarmupIndex = 320;

        private static readonly double[] VarianceFloor = { 0.0025, 0.25, 1.0, 0.00005, 0.004 };

        // ----- Состояние ---------------------------------------------------------------------------

        private AverageTrueRange _atr;
        private RelativeStrengthIndex _rsi;
        private DirectionalMovementSystem _dms;
        private ExponentialMovingAverage _ema20;
        private ExponentialMovingAverage _ema50;
        private ExponentialMovingAverage _ema200;
        private BollingerBands _bb;

        private readonly NaiveBayesModel _nbReal = new NaiveBayesModel();
        private readonly NaiveBayesModel _nbShadow = new NaiveBayesModel();
        private readonly OnlineLogistic _regimeModel = new OnlineLogistic(5);

        private readonly double[] _realWeights = { 1.0, 1.0, 1.0, 1.0, 1.0 };
        private readonly double[] _shadowMultipliers = { 1.0, 1.0, 1.0, 1.0, 1.0 };

        private readonly List<ShadowTrade> _shadowTrades = new List<ShadowTrade>();
        private readonly List<RegimeSample> _regimeSamples = new List<RegimeSample>();
        private readonly Dictionary<int, TradeMeta> _meta = new Dictionary<int, TradeMeta>();
        private readonly Dictionary<SkipReason, int> _skipCounts = new Dictionary<SkipReason, int>();

        private int _lastProcessedIndex = -1;
        private int _lastTradeCloseIndex = -1000000;
        private int _barsSinceStatus;
        private int _shadowResolved;
        private int _shadowWins;
        private int _realTrades;
        private int _realWins;

        private DateTime _dayKey = DateTime.MinValue;
        private double _dayStartEquity;
        private bool _dailyLocked;
        private double _peakEquity;

        private TimeSpan _blockStart;
        private TimeSpan _blockEnd;
        private bool _sessionBlockEnabled;
        private bool _sweepActive;

        private double _effectiveTpAtr;
        private string _storageKey;

        // =============================================================================================
        //  ЖИЗНЕННЫЙ ЦИКЛ
        // =============================================================================================

        protected override void OnStart()
        {
            _atr = Indicators.AverageTrueRange(AtrPeriod, MovingAverageType.Simple);
            _rsi = Indicators.RelativeStrengthIndex(Bars.ClosePrices, 14);
            _dms = Indicators.DirectionalMovementSystem(14);
            _ema20 = Indicators.ExponentialMovingAverage(Bars.ClosePrices, 20);
            _ema50 = Indicators.ExponentialMovingAverage(Bars.ClosePrices, 50);
            _ema200 = Indicators.ExponentialMovingAverage(Bars.ClosePrices, 200);
            _bb = Indicators.BollingerBands(Bars.ClosePrices, 20, 2.0, MovingAverageType.Simple);

            ValidateAndReportSettings();

            // LocalStorage cTrader принимает в ключе только латиницу, цифры и пробелы (без пробелов по краям).
            _storageKey = StorageKey("QAIv5 " + SymbolName + " " + TimeFrame);
            if (ResetAiMemory)
            {
                Print("Память AI сброшена по параметру — модель начинает с нуля.");
            }
            else
            {
                LoadMemory();
            }

            EnsureHistory();
            PretrainOnHistory();

            _peakEquity = Account.Equity;
            RollDay(Server.TimeInUtc);

            Positions.Closed += OnPositionClosed;
            RebuildMetaForOpenPositions();

            Print("QuantAI_Universal_V5_Ultimate запущен: " + SymbolName + " " + TimeFrame +
                  ", баров истории " + Bars.Count + ".");
            PrintStatus(Bars.Count - 2, null);
        }

        protected override void OnStop()
        {
            SaveMemory();
            Print("Остановлен. Память AI сохранена. " + BuildStatsLine());
        }

        protected override void OnTick()
        {
            ManagePositionsOnTick();
        }

        protected override void OnBar()
        {
            int closedIndex = Bars.Count - 2;
            if (closedIndex < 0) return;

            DateTime nowUtc = Server.TimeInUtc;
            RollDay(nowUtc);
            UpdateEquityPeak();

            // Все бары, которые ещё не были обработаны (обычно ровно один).
            BarAnalysis last = null;
            for (int i = Math.Max(_lastProcessedIndex + 1, 0); i <= closedIndex; i++)
            {
                last = ProcessClosedBar(i);
                _lastProcessedIndex = i;
            }

            ManageTrailingOnBar(closedIndex);

            if (last == null)
            {
                RegisterSkip(SkipReason.Warmup, "недостаточно истории для индикаторов", null);
            }
            else
            {
                TryEnter(last, nowUtc);
            }

            _barsSinceStatus++;
            if (_barsSinceStatus >= StatusEveryBars)
            {
                _barsSinceStatus = 0;
                PrintStatus(closedIndex, last);
            }

            if (closedIndex % 50 == 0) SaveMemory();
        }

        // =============================================================================================
        //  НАСТРОЙКИ
        // =============================================================================================

        private void ValidateAndReportSettings()
        {
            // R:R — жёсткое требование: при нарушении цель поднимается, а не игнорируется молча.
            _effectiveTpAtr = TpAtrMultiplier;
            if (TpAtrMultiplier / SlAtrMultiplier < MinRewardRisk)
            {
                _effectiveTpAtr = SlAtrMultiplier * MinRewardRisk;
                Print("ВНИМАНИЕ: Тейк " + F(TpAtrMultiplier) + " ATR при стопе " + F(SlAtrMultiplier) +
                      " ATR даёт R:R ниже " + F(MinRewardRisk) + ". Тейк поднят до " + F(_effectiveTpAtr) + " ATR.");
            }

            _sessionBlockEnabled = TryParseTime(SessionBlockStart, out _blockStart) & TryParseTime(SessionBlockEnd, out _blockEnd);
            if (!_sessionBlockEnabled)
            {
                Print("ВНИМАНИЕ: не удалось прочитать окно блокировки сессии ('" + SessionBlockStart + "'–'" +
                      SessionBlockEnd + "'). Формат ЧЧ:ММ. Блокировка отключена.");
            }

            _sweepActive = SweepProtection == SweepProtectionMode.On ||
                           (SweepProtection == SweepProtectionMode.Auto &&
                            SymbolName.IndexOf("XAU", StringComparison.OrdinalIgnoreCase) >= 0);

            if (MaxRiskCapPercent < RiskPercent)
            {
                Print("ВНИМАНИЕ: Потолок риска " + F(MaxRiskCapPercent) + "% ниже риска на сделку " + F(RiskPercent) +
                      "% — позиция будет ограничена потолком.");
            }

            // Никаких скрытых порогов: печатается ровно то, что будет применяться.
            Print("Пороги (все из параметров UI): режим ≥ " + F(MinRegimeConfidence) +
                  "; вход ≥ " + (AdaptiveThreshold
                      ? F(TrendThreshold) + " при ADX > " + F(TrendAdxLevel) + ", " + F(FlatThreshold) +
                        " при ADX < " + F(FlatAdxLevel) + ", иначе " + F(MinEnsembleConfidence)
                      : F(MinEnsembleConfidence) + " (адаптивный порог выключен)") +
                  ". Риск " + F(RiskPercent) + "% от " + RiskBase + ", стоп " + F(SlAtrMultiplier) +
                  " ATR, тейк " + F(_effectiveTpAtr) + " ATR, дневной лимит " + F(DailyLossLimitPercent) + "%." +
                  (_sweepActive ? " Фильтр ложных пробоев включён." : "") +
                  (_sessionBlockEnabled ? " Блок " + SessionBlockStart + "–" + SessionBlockEnd + " UTC." : ""));
        }

        private static bool TryParseTime(string text, out TimeSpan value)
        {
            value = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(text)) return false;
            return TimeSpan.TryParseExact(text.Trim(), "h\\:mm", CultureInfo.InvariantCulture, out value) ||
                   TimeSpan.TryParseExact(text.Trim(), "hh\\:mm", CultureInfo.InvariantCulture, out value);
        }

        private void EnsureHistory()
        {
            int attempts = 0;
            while (Bars.Count < HistoryBars && attempts < 40)
            {
                attempts++;
                int loaded = Bars.LoadMoreHistory();
                if (loaded <= 0) break;
            }
        }

        // =============================================================================================
        //  ОБУЧЕНИЕ НА ИСТОРИИ (холодный старт)
        // =============================================================================================

        private void PretrainOnHistory()
        {
            int lastClosed = Bars.Count - 2;
            int from = Math.Max(WarmupIndex, lastClosed - HistoryBars);
            int processed = 0;

            for (int i = from; i <= lastClosed; i++)
            {
                ProcessClosedBar(i);
                _lastProcessedIndex = i;
                processed++;
            }

            if (processed == 0)
            {
                _lastProcessedIndex = lastClosed;
                Print("Обучение на истории пропущено: баров " + Bars.Count + ", нужно больше " + WarmupIndex +
                      ". Модель начнёт с нейтральной оценки и будет учиться онлайн.");
                return;
            }

            double rate = _shadowResolved > 0 ? (double)_shadowWins / _shadowResolved : 0;
            Print("AI обучен на истории: " + processed + " баров, теневых сделок " + _shadowResolved +
                  ", из них до +1R дошло " + (rate * 100).ToString("F1", CultureInfo.InvariantCulture) +
                  "%. Обновлений модели режима: " + _regimeModel.Updates + ".");
        }

        /// <summary>
        /// Обработка одного закрытого бара: закрываются созревшие теневые сделки и метки режима,
        /// выполняется анализ, открывается новая теневая сделка. Реальных ордеров здесь нет.
        /// </summary>
        private BarAnalysis ProcessClosedBar(int i)
        {
            ResolveShadowTrades(i);
            ResolveRegimeSamples(i);

            if (i < WarmupIndex) return null;

            BarAnalysis a = Analyze(i);
            if (a == null) return null;

            QueueRegimeSample(a);

            if (a.EnsembleDirection != 0 && a.EnsembleScore >= ShadowMinScore)
            {
                OpenShadowTrade(a);
            }

            return a;
        }

        // =============================================================================================
        //  АНАЛИЗ РЫНКА
        // =============================================================================================

        private BarAnalysis Analyze(int i)
        {
            double atr = _atr.Result[i];
            if (double.IsNaN(atr) || atr <= 0) return null;

            var a = new BarAnalysis
            {
                Index = i,
                Close = Bars.ClosePrices[i],
                Atr = atr,
                Adx = _dms.ADX[i],
                Rsi = _rsi.Result[i],
            };

            if (double.IsNaN(a.Adx) || double.IsNaN(a.Rsi)) return null;

            // Средний ATR и перцентиль волатильности.
            double sum = 0;
            int n = 0;
            for (int k = 0; k < AtrLongPeriod; k++)
            {
                double v = _atr.Result[i - k];
                if (double.IsNaN(v)) continue;
                sum += v;
                n++;
            }
            a.AtrLongAverage = n > 0 ? sum / n : atr;

            int below = 0;
            int total = 0;
            for (int k = 1; k <= VolPercentileWindow; k++)
            {
                double v = _atr.Result[i - k];
                if (double.IsNaN(v)) continue;
                total++;
                if (v < atr) below++;
            }
            a.VolPercentile = total > 0 ? (double)below / total : 0.5;

            a.RsiSlope = (_rsi.Result[i] - _rsi.Result[i - 3]) / 3.0;

            // Efficiency Ratio Кауфмана: доля направленного движения в пройденном пути.
            double path = 0;
            for (int k = 0; k < EfficiencyPeriod; k++)
            {
                path += Math.Abs(Bars.ClosePrices[i - k] - Bars.ClosePrices[i - k - 1]);
            }
            double net = Math.Abs(Bars.ClosePrices[i] - Bars.ClosePrices[i - EfficiencyPeriod]);
            a.EfficiencyRatio = path > 0 ? net / path : 0;

            a.TrendSlopeSign = Math.Sign(_ema20.Result[i] - _ema20.Result[i - 5]);

            ComputeRegime(a);
            EvaluateStrategies(a);
            ComputeEnsemble(a);
            DetectSweeps(a);
            return a;
        }

        private static double Sigmoid(double z)
        {
            if (z > 30) return 1.0;
            if (z < -30) return 0.0;
            return 1.0 / (1.0 + Math.Exp(-z));
        }

        private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

        private double[] RegimeInput(BarAnalysis a) => new[]
        {
            1.0,
            (a.Adx - 22.0) / 10.0,
            (a.EfficiencyRatio - 0.30) * 3.0,
            (a.AtrLongAverage > 0 ? a.Atr / a.AtrLongAverage : 1.0) - 1.0,
            a.VolPercentile - 0.5,
        };

        /// <summary>
        /// P(тренд): эвристика по ADX и Efficiency Ratio, смешанная с онлайн-моделью, которая учится на
        /// том, продолжился ли рынок в направлении наклона EMA20 на горизонте в 12 баров.
        /// </summary>
        private void ComputeRegime(BarAnalysis a)
        {
            double heuristic = Sigmoid(((a.Adx - 22.0) / 5.0) + (6.0 * (a.EfficiencyRatio - 0.30)));

            double learnedWeight = Math.Min(0.5, _regimeModel.Updates / (_regimeModel.Updates + 500.0));
            double learned = _regimeModel.Updates > 0 ? _regimeModel.Predict(RegimeInput(a)) : heuristic;

            // Модель предсказывает вероятность продолжения движения на 0.75 ATR — её базовая частота ниже 0.5,
            // поэтому она растягивается к шкале эвристики, а не подменяет её.
            double learnedScaled = Clamp01(learned * 2.0);
            a.PTrend = Clamp01(((1.0 - learnedWeight) * heuristic) + (learnedWeight * learnedScaled));
            a.RegimeConfidence = Math.Abs((2.0 * a.PTrend) - 1.0);
        }

        private void QueueRegimeSample(BarAnalysis a)
        {
            if (a.TrendSlopeSign == 0) return;
            _regimeSamples.Add(new RegimeSample
            {
                Index = a.Index,
                Input = RegimeInput(a),
                Direction = (int)a.TrendSlopeSign,
                Close = a.Close,
                Atr = a.Atr,
            });
        }

        private void ResolveRegimeSamples(int i)
        {
            for (int k = _regimeSamples.Count - 1; k >= 0; k--)
            {
                RegimeSample s = _regimeSamples[k];
                if (i - s.Index < RegimeHorizon) continue;

                double move = (Bars.ClosePrices[i] - s.Close) * s.Direction / Math.Max(s.Atr, 1e-12);
                double y = move >= 0.75 ? 1.0 : 0.0;
                _regimeModel.Update(s.Input, y, 0.02, 0.0005);
                _regimeSamples.RemoveAt(k);
            }
        }

        // ----- Подстратегии ------------------------------------------------------------------------

        private void EvaluateStrategies(BarAnalysis a)
        {
            int i = a.Index;
            double atr = a.Atr;
            double c = Bars.ClosePrices[i];
            double o = Bars.OpenPrices[i];
            double h = Bars.HighPrices[i];
            double l = Bars.LowPrices[i];

            // 0. TrendFollowing: EMA50/EMA200, цена по тренду, наклон EMA50; откат к EMA50 усиливает сигнал.
            {
                double e50 = _ema50.Result[i];
                double e200 = _ema200.Result[i];
                double e50Prev = _ema50.Result[i - 5];
                double minLow = Math.Min(Math.Min(Bars.LowPrices[i], Bars.LowPrices[i - 1]), Math.Min(Bars.LowPrices[i - 2], Bars.LowPrices[i - 3]));
                double maxHigh = Math.Max(Math.Max(Bars.HighPrices[i], Bars.HighPrices[i - 1]), Math.Max(Bars.HighPrices[i - 2], Bars.HighPrices[i - 3]));
                double separation = Math.Abs(e50 - e200) / atr;

                if (e50 > e200 && c > e50 && e50 > e50Prev)
                {
                    bool pullback = minLow <= e50 + (0.5 * atr);
                    a.Votes[0] = new StrategyVote { Direction = 1, Strength = Clamp01(0.35 + (0.15 * Math.Min(separation, 3.0))) * (pullback ? 1.0 : 0.7) };
                }
                else if (e50 < e200 && c < e50 && e50 < e50Prev)
                {
                    bool pullback = maxHigh >= e50 - (0.5 * atr);
                    a.Votes[0] = new StrategyVote { Direction = -1, Strength = Clamp01(0.35 + (0.15 * Math.Min(separation, 3.0))) * (pullback ? 1.0 : 0.7) };
                }
            }

            // 1. Breakout: пробой канала Дончиана (20 баров без текущего) и/или канала Кельтнера (EMA20 ± 2 ATR).
            {
                double donHigh = double.MinValue;
                double donLow = double.MaxValue;
                for (int k = 1; k <= DonchianPeriod; k++)
                {
                    donHigh = Math.Max(donHigh, Bars.HighPrices[i - k]);
                    donLow = Math.Min(donLow, Bars.LowPrices[i - k]);
                }
                double kelTop = _ema20.Result[i] + (2.0 * atr);
                double kelBottom = _ema20.Result[i] - (2.0 * atr);

                if (c > donHigh)
                {
                    a.Votes[1] = new StrategyVote { Direction = 1, Strength = Clamp01(0.40 + (0.30 * ((c - donHigh) / atr)) + (c > kelTop ? 0.20 : 0.0)) };
                }
                else if (c < donLow)
                {
                    a.Votes[1] = new StrategyVote { Direction = -1, Strength = Clamp01(0.40 + (0.30 * ((donLow - c) / atr)) + (c < kelBottom ? 0.20 : 0.0)) };
                }
                else if (c > kelTop)
                {
                    a.Votes[1] = new StrategyVote { Direction = 1, Strength = 0.35 };
                }
                else if (c < kelBottom)
                {
                    a.Votes[1] = new StrategyVote { Direction = -1, Strength = 0.35 };
                }
            }

            // 2. MeanReversion: RSI в зоне перекупленности/перепроданности и закрытие за полосой Боллинджера.
            {
                double rsi = a.Rsi;
                double bbTop = _bb.Top[i];
                double bbBottom = _bb.Bottom[i];

                if (rsi < 30 && c <= bbBottom)
                {
                    a.Votes[2] = new StrategyVote { Direction = 1, Strength = Clamp01(0.40 + ((30.0 - rsi) / 25.0) + (0.20 * ((bbBottom - c) / atr))) };
                }
                else if (rsi > 70 && c >= bbTop)
                {
                    a.Votes[2] = new StrategyVote { Direction = -1, Strength = Clamp01(0.40 + ((rsi - 70.0) / 25.0) + (0.20 * ((c - bbTop) / atr))) };
                }
            }

            // 3. VolatilityExpansion: диапазон бара намного больше предыдущего ATR, закрытие у края диапазона.
            {
                double range = h - l;
                double prevAtr = _atr.Result[i - 1];
                if (range > 0 && prevAtr > 0 && range > 1.8 * prevAtr && atr > a.AtrLongAverage)
                {
                    double closeLocation = (c - l) / range;
                    double strength = Clamp01(0.60 + (0.25 * ((range / prevAtr) - 1.8)));
                    if (c > o && closeLocation >= 0.70)
                    {
                        a.Votes[3] = new StrategyVote { Direction = 1, Strength = strength };
                    }
                    else if (c < o && closeLocation <= 0.30)
                    {
                        a.Votes[3] = new StrategyVote { Direction = -1, Strength = strength };
                    }
                }
            }

            // 4. LiquidityGrab: прокол микро-экстремума (10 баров) с возвратом внутрь и длинной тенью.
            {
                double microHigh = double.MinValue;
                double microLow = double.MaxValue;
                for (int k = 1; k <= MicroExtremePeriod; k++)
                {
                    microHigh = Math.Max(microHigh, Bars.HighPrices[i - k]);
                    microLow = Math.Min(microLow, Bars.LowPrices[i - k]);
                }

                double upperWick = h - Math.Max(o, c);
                double lowerWick = Math.Min(o, c) - l;
                bool grabHigh = h > microHigh && c < microHigh && upperWick >= 0.4 * atr;
                bool grabLow = l < microLow && c > microLow && lowerWick >= 0.4 * atr;

                if (grabHigh && (!grabLow || upperWick >= lowerWick))
                {
                    a.Votes[4] = new StrategyVote { Direction = -1, Strength = Clamp01(0.35 + (0.30 * (upperWick / atr))) };
                }
                else if (grabLow)
                {
                    a.Votes[4] = new StrategyVote { Direction = 1, Strength = Clamp01(0.35 + (0.30 * (lowerWick / atr))) };
                }
            }
        }

        private double EffectiveWeight(int s)
        {
            double w = _realWeights[s] * _shadowMultipliers[s];
            return Math.Max(0.20, Math.Min(3.0, w));
        }

        private double RegimeFactor(int s, double pTrend) => IsTrendStrategy[s] ? pTrend : 1.0 - pTrend;

        /// <summary>
        /// Взвешенная сумма голосов. Итоговая оценка 0..1 — сила согласных голосов, умноженная на степень
        /// согласия, на соответствие режиму и на средний AI-вес согласных стратегий.
        /// </summary>
        private void ComputeEnsemble(BarAnalysis a)
        {
            double netSum = 0;
            double gross = 0;

            for (int s = 0; s < StrategyCount; s++)
            {
                StrategyVote v = a.Votes[s];
                if (v.Direction == 0 || v.Strength <= 0) continue;
                double contribution = EffectiveWeight(s) * RegimeFactor(s, a.PTrend) * v.Strength;
                netSum += v.Direction * contribution;
                gross += contribution;
            }

            if (gross <= 1e-12 || Math.Abs(netSum) <= 1e-12)
            {
                a.EnsembleDirection = 0;
                a.EnsembleScore = 0;
                return;
            }

            int dir = netSum > 0 ? 1 : -1;
            double sumWrs = 0;   // Σ w·r·s согласных
            double sumWr = 0;    // Σ w·r
            double sumW = 0;     // Σ w
            double sumRs = 0;    // Σ r·s
            int agreeing = 0;

            for (int s = 0; s < StrategyCount; s++)
            {
                StrategyVote v = a.Votes[s];
                if (v.Direction != dir || v.Strength <= 0) continue;
                double w = EffectiveWeight(s);
                double r = RegimeFactor(s, a.PTrend);
                sumWrs += w * r * v.Strength;
                sumWr += w * r;
                sumW += w;
                sumRs += r * v.Strength;
                agreeing++;
            }

            double strength = sumWr > 0 ? sumWrs / sumWr : 0;
            double consensus = Math.Abs(netSum) / gross;
            double regimeFit = sumW > 0 ? sumWr / sumW : 0;
            double weightFactor = sumRs > 0 ? Math.Min(1.25, sumWrs / sumRs) : 0;
            double multiVote = 1.0 + (0.10 * Math.Max(0, agreeing - 1));

            a.EnsembleDirection = dir;
            a.AgreeingCount = agreeing;
            a.EnsembleScore = Clamp01(strength * consensus * (0.5 + (0.5 * regimeFit)) * weightFactor * multiVote);
        }

        /// <summary>
        /// Снятие ликвидности за последние 3 бара: прокол экстремума 20 баров с закрытием обратно внутри
        /// и длинной тенью. Для золота это частая ловушка — пробой, который сразу возвращается.
        /// </summary>
        private void DetectSweeps(BarAnalysis a)
        {
            for (int j = a.Index - 2; j <= a.Index; j++)
            {
                double hh = double.MinValue;
                double ll = double.MaxValue;
                for (int k = 1; k <= DonchianPeriod; k++)
                {
                    hh = Math.Max(hh, Bars.HighPrices[j - k]);
                    ll = Math.Min(ll, Bars.LowPrices[j - k]);
                }

                double atrJ = _atr.Result[j];
                if (double.IsNaN(atrJ) || atrJ <= 0) continue;

                double o = Bars.OpenPrices[j];
                double c = Bars.ClosePrices[j];
                double upperWick = Bars.HighPrices[j] - Math.Max(o, c);
                double lowerWick = Math.Min(o, c) - Bars.LowPrices[j];

                if (Bars.HighPrices[j] > hh && c < hh && upperWick >= 0.3 * atrJ) a.BearishSweepRecent = true;
                if (Bars.LowPrices[j] < ll && c > ll && lowerWick >= 0.3 * atrJ) a.BullishSweepRecent = true;
            }
        }

        // ----- Признаки для Naive Bayes ------------------------------------------------------------

        private double CurrentSpread() => Math.Max(0, Symbol.Spread);

        private double[] Features(BarAnalysis a, int direction) => new[]
        {
            a.AtrLongAverage > 0 ? a.Atr / a.AtrLongAverage : 1.0, // ATR_Ratio
            a.RsiSlope * direction,                                 // RSI_Slope в направлении сделки
            a.Adx,                                                  // ADX
            CurrentSpread() / a.Atr,                                // Spread_Cost
            a.VolPercentile,                                        // Volatility_Percentile
        };

        private double WinProbability(double[] features) =>
            NaiveBayesModel.PredictCombined(_nbReal, _nbShadow, features, VarianceFloor, NaiveBayesTrustSamples);

        /// <summary>
        /// Signal Confidence: сила ансамбля, скорректированная AI-вероятностью выигрыша.
        /// При нейтральной вероятности 0.5 равна силе ансамбля.
        /// </summary>
        private static double SignalConfidence(double ensembleScore, double pWin) => Clamp01(ensembleScore * 2.0 * pWin);

        /// <summary>
        /// Ожидание сделки в R. Выигрыш — дошли до +1R (метка модели уже учитывает спред):
        /// с частичным закрытием это половина по 1R и остаток между безубытком и тейком.
        /// </summary>
        private double ExpectedValueR(double pWin)
        {
            double rr = _effectiveTpAtr / SlAtrMultiplier;
            double remainder = (BreakevenOffsetR + rr) / 2.0;
            double part = UsePartialClose ? PartialClosePercent / 100.0 : 0.0;
            double winR = (part * 1.0) + ((1.0 - part) * remainder);
            return (pWin * winR) - (1.0 - pWin);
        }

        private double EntryThreshold(double adx)
        {
            if (!AdaptiveThreshold) return MinEnsembleConfidence;
            if (adx > TrendAdxLevel) return TrendThreshold;
            if (adx < FlatAdxLevel) return FlatThreshold;
            return MinEnsembleConfidence;
        }

        // =============================================================================================
        //  ТЕНЕВЫЕ СДЕЛКИ
        // =============================================================================================

        private void OpenShadowTrade(BarAnalysis a)
        {
            // Одна теневая сделка на направление одновременно — иначе серия похожих баров считалась бы
            // многократно одним и тем же событием.
            for (int k = 0; k < _shadowTrades.Count; k++)
            {
                if (_shadowTrades[k].Direction == a.EnsembleDirection) return;
            }

            int dir = a.EnsembleDirection;
            double spread = CurrentSpread();
            double risk = a.Atr * SlAtrMultiplier;

            // Бары строятся по Bid: покупка входит по Ask (Bid + спред), продажа — по Bid.
            double entry = dir > 0 ? a.Close + spread : a.Close;
            var votes = new double[StrategyCount];
            for (int s = 0; s < StrategyCount; s++)
            {
                if (a.Votes[s].Direction == dir) votes[s] = a.Votes[s].Strength;
            }

            _shadowTrades.Add(new ShadowTrade
            {
                OpenIndex = a.Index,
                Direction = dir,
                Entry = entry,
                RiskDistance = risk,
                Stop = entry - (dir * risk),
                Target = entry + (dir * risk),
                Features = Features(a, dir),
                VoteStrengths = votes,
            });
        }

        private void ResolveShadowTrades(int i)
        {
            if (_shadowTrades.Count == 0) return;

            double spread = CurrentSpread();
            double high = Bars.HighPrices[i];
            double low = Bars.LowPrices[i];
            double close = Bars.ClosePrices[i];

            for (int k = _shadowTrades.Count - 1; k >= 0; k--)
            {
                ShadowTrade t = _shadowTrades[k];
                if (i <= t.OpenIndex) continue;

                // Цены выхода: покупка закрывается по Bid, продажа — по Ask (Bid + спред).
                bool stopHit = t.Direction > 0 ? low <= t.Stop : high + spread >= t.Stop;
                bool targetHit = t.Direction > 0 ? high >= t.Target : low + spread <= t.Target;
                bool timeout = i - t.OpenIndex >= ShadowMaxHoldBars;

                double resultR;
                if (stopHit)
                {
                    resultR = -1.0; // стоп и цель в одном баре — считается худшее
                }
                else if (targetHit)
                {
                    resultR = 1.0;
                }
                else if (timeout)
                {
                    double exit = t.Direction > 0 ? close : close + spread;
                    resultR = (exit - t.Entry) * t.Direction / t.RiskDistance;
                }
                else
                {
                    continue;
                }

                bool win = resultR > 0;
                _nbShadow.Add(t.Features, win, ShadowWeight);
                _shadowResolved++;
                if (win) _shadowWins++;

                // RL на теневых исходах — в пять раз медленнее, чем на реальных.
                for (int s = 0; s < StrategyCount; s++)
                {
                    if (t.VoteStrengths[s] <= 0) continue;
                    double step = RlLearningRate / 5.0 * t.VoteStrengths[s] * Math.Max(-1.5, Math.Min(1.5, resultR));
                    _shadowMultipliers[s] = Math.Max(0.5, Math.Min(1.5, _shadowMultipliers[s] * Math.Exp(step)));
                }

                _shadowTrades.RemoveAt(k);
            }
        }

        // =============================================================================================
        //  ВХОД
        // =============================================================================================

        private void TryEnter(BarAnalysis a, DateTime nowUtc)
        {
            string regimeText = RegimeLabel(a);

            if (_dailyLocked)
            {
                RegisterSkip(SkipReason.DailyLossLimit, "дневной лимит убытка " + F(DailyLossLimitPercent) + "% достигнут, торговля до конца суток закрыта", a);
                return;
            }

            if (IsSessionBlocked(nowUtc))
            {
                RegisterSkip(SkipReason.SessionBlock, "межсессионное окно " + SessionBlockStart + "–" + SessionBlockEnd + " UTC (расширение спреда)", a);
                return;
            }

            if (!Symbol.MarketHours.IsOpened())
            {
                RegisterSkip(SkipReason.MarketClosed, "рынок закрыт", a);
                return;
            }

            if (Positions.FindAll(OrderLabel, SymbolName).Length >= MaxOpenPositions)
            {
                RegisterSkip(SkipReason.PositionOpen, "уже открыто позиций: " + MaxOpenPositions, a);
                return;
            }

            if (a.Index - _lastTradeCloseIndex < CooldownBars)
            {
                RegisterSkip(SkipReason.Cooldown, "пауза после закрытия сделки", a);
                return;
            }

            if (a.EnsembleDirection == 0)
            {
                RegisterSkip(SkipReason.NoSignal, "ни одна стратегия не дала сигнала", a);
                return;
            }

            if (a.RegimeConfidence < MinRegimeConfidence)
            {
                RegisterSkip(SkipReason.UnclearRegime,
                    "режим неясен: уверенность " + F(a.RegimeConfidence) + " < " + F(MinRegimeConfidence) + " (" + regimeText + ")", a);
                return;
            }

            double[] features = Features(a, a.EnsembleDirection);
            double pWin = WinProbability(features);
            double confidence = SignalConfidence(a.EnsembleScore, pWin);
            double threshold = EntryThreshold(a.Adx);

            if (confidence < threshold)
            {
                RegisterSkip(SkipReason.LowConfidence,
                    "Low Confidence " + F(confidence) + " < " + F(threshold) + " (ансамбль " + F(a.EnsembleScore) + ", AI P(win) " + F(pWin) + ")", a);
                return;
            }

            double spread = CurrentSpread();
            if (spread / a.Atr > MaxSpreadAtr)
            {
                RegisterSkip(SkipReason.HighSpread, "High Spread: " + Pct(spread / a.Atr) + " ATR при пределе " + Pct(MaxSpreadAtr), a);
                return;
            }

            double slDistance = a.Atr * SlAtrMultiplier;
            double tpDistance = a.Atr * _effectiveTpAtr;
            if (spread / tpDistance > MaxSpreadCostOfProfit)
            {
                RegisterSkip(SkipReason.CostVsProfit, "спред съедает " + Pct(spread / tpDistance) + " потенциальной прибыли при пределе " + Pct(MaxSpreadCostOfProfit), a);
                return;
            }

            double ev = ExpectedValueR(pWin);
            if (RequirePositiveEv && ev <= 0)
            {
                RegisterSkip(SkipReason.NegativeEV, "ожидание " + F(ev) + "R ≤ 0 при AI P(win) " + F(pWin), a);
                return;
            }

            if (_sweepActive)
            {
                bool trapForLongs = a.EnsembleDirection > 0 && a.BearishSweepRecent && a.Votes[4].Direction <= 0;
                bool trapForShorts = a.EnsembleDirection < 0 && a.BullishSweepRecent && a.Votes[4].Direction >= 0;
                if (trapForLongs || trapForShorts)
                {
                    RegisterSkip(SkipReason.SweepBlock, "ложный пробой: недавно снята ликвидность против направления входа", a);
                    return;
                }
            }

            double riskBase = RiskBase == RiskBaseMode.Equity ? Account.Equity : Account.Balance;
            if (!TryComputeVolume(riskBase, slDistance, out double volume, out double riskMoney, out string sizeNote))
            {
                RegisterSkip(SkipReason.AccountTooSmall, sizeNote, a);
                return;
            }

            TradeType type = a.EnsembleDirection > 0 ? TradeType.Buy : TradeType.Sell;
            double slPips = slDistance / Symbol.PipSize;
            double tpPips = tpDistance / Symbol.PipSize;

            TradeResult result = ExecuteMarketOrder(type, SymbolName, volume, OrderLabel, slPips, tpPips);
            if (!result.IsSuccessful || result.Position == null)
            {
                RegisterSkip(SkipReason.OrderFailed, "брокер отклонил ордер: " + result.Error, a);
                return;
            }

            Position p = result.Position;
            var votes = new double[StrategyCount];
            for (int s = 0; s < StrategyCount; s++)
            {
                if (a.Votes[s].Direction == a.EnsembleDirection) votes[s] = a.Votes[s].Strength;
            }

            _meta[p.Id] = new TradeMeta
            {
                Direction = a.EnsembleDirection,
                EntryPrice = p.EntryPrice,
                InitialRiskDistance = slDistance,
                RiskMoney = riskMoney,
                Features = features,
                VoteStrengths = votes,
                OpenBarIndex = a.Index,
            };

            Print("ENTRY " + type + " " + SymbolName + " объём " + volume.ToString("0.########", CultureInfo.InvariantCulture) +
                  " @ " + p.EntryPrice.ToString("F" + Symbol.Digits, CultureInfo.InvariantCulture) +
                  " | SL " + F(SlAtrMultiplier) + " ATR, TP " + F(_effectiveTpAtr) + " ATR | риск " + riskMoney.ToString("F2", CultureInfo.InvariantCulture) +
                  " | Confidence " + F(confidence) + " (порог " + F(threshold) + "), P(win) " + F(pWin) + ", EV " + F(ev) + "R" +
                  " | " + regimeText + " | голоса: " + VotesText(a) + (sizeNote.Length > 0 ? " | " + sizeNote : ""));
        }

        /// <summary>
        /// Объём от риска через штатные функции платформы (VolumeForFixedRisk / AmountRisked): они
        /// пересчитывают валюту инструмента в валюту счёта по текущему курсу. Symbol.PipValue для этого
        /// не годится — по документации SDK он фиксируется при запуске и не обновляется.
        /// Ниже минимального лота брокера — округление вверх только если минимальный лот укладывается
        /// в «Потолок риска»; иначе вход отклоняется с объяснением.
        /// </summary>
        private bool TryComputeVolume(double riskBase, double slDistance, out double volume, out double riskMoney, out string note)
        {
            volume = 0;
            riskMoney = 0;
            note = "";

            double effectiveRisk = Math.Min(RiskPercent, MaxRiskCapPercent);
            double budget = riskBase * effectiveRisk / 100.0;
            double slPips = slDistance / Symbol.PipSize;

            if (budget <= 0 || slPips <= 0)
            {
                note = "не удалось рассчитать риск: бюджет " + budget.ToString("F2", CultureInfo.InvariantCulture) + ", стоп " + slPips.ToString("F1", CultureInfo.InvariantCulture) + " п.";
                return false;
            }

            double raw = Symbol.VolumeForFixedRisk(budget, slPips, RoundingMode.Down);
            double normalized = Symbol.NormalizeVolumeInUnits(Math.Min(raw, Symbol.VolumeInUnitsMax), RoundingMode.Down);

            if (normalized < Symbol.VolumeInUnitsMin)
            {
                double minRisk = Symbol.AmountRisked(Symbol.VolumeInUnitsMin, slPips);
                double minRiskPercent = minRisk / riskBase * 100.0;
                if (minRisk > 0 && minRiskPercent <= MaxRiskCapPercent)
                {
                    volume = Symbol.VolumeInUnitsMin;
                    riskMoney = minRisk;
                    note = "объём поднят до минимального лота: риск " + minRiskPercent.ToString("F2", CultureInfo.InvariantCulture) + "% (потолок " + F(MaxRiskCapPercent) + "%)";
                    return true;
                }

                note = "Account Too Small: минимальный лот рискует " + minRisk.ToString("F2", CultureInfo.InvariantCulture) + " (" +
                       minRiskPercent.ToString("F1", CultureInfo.InvariantCulture) + "% счёта) при потолке " + F(MaxRiskCapPercent) +
                       "%. Нужен счёт от " + (minRisk / (MaxRiskCapPercent / 100.0)).ToString("F0", CultureInfo.InvariantCulture) + ".";
                return false;
            }

            volume = normalized;
            riskMoney = Symbol.AmountRisked(normalized, slPips);
            return true;
        }

        private bool IsSessionBlocked(DateTime nowUtc)
        {
            if (!_sessionBlockEnabled) return false;
            TimeSpan t = nowUtc.TimeOfDay;
            return _blockStart <= _blockEnd
                ? t >= _blockStart && t < _blockEnd
                : t >= _blockStart || t < _blockEnd;
        }

        // =============================================================================================
        //  СОПРОВОЖДЕНИЕ
        // =============================================================================================

        /// <summary>На каждом тике: при +1R — частичное закрытие и перенос стопа в безубыток + 0.1R.</summary>
        private void ManagePositionsOnTick()
        {
            foreach (Position p in Positions.FindAll(OrderLabel, SymbolName))
            {
                if (!_meta.TryGetValue(p.Id, out TradeMeta m) || m.BreakevenDone) continue;

                int dir = p.TradeType == TradeType.Buy ? 1 : -1;
                double price = dir > 0 ? Symbol.Bid : Symbol.Ask;
                double r = (price - m.EntryPrice) * dir / m.InitialRiskDistance;
                if (r < BreakevenTriggerR) continue;

                if (UsePartialClose && !m.PartialDone)
                {
                    double part = Symbol.NormalizeVolumeInUnits(p.VolumeInUnits * PartialClosePercent / 100.0, RoundingMode.Down);
                    double rest = p.VolumeInUnits - part;
                    if (part >= Symbol.VolumeInUnitsMin && rest >= Symbol.VolumeInUnitsMin)
                    {
                        double estimatedProfit = p.NetProfit * part / p.VolumeInUnits;
                        TradeResult closeResult = ClosePosition(p, part);
                        if (closeResult.IsSuccessful)
                        {
                            m.RealizedPartialProfit += estimatedProfit;
                            Print("PARTIAL " + SymbolName + ": закрыто " + F(PartialClosePercent) + "% на +" + F(r) + "R, зафиксировано ~" +
                                  estimatedProfit.ToString("F2", CultureInfo.InvariantCulture));
                        }
                    }
                    else
                    {
                        Print("PARTIAL пропущено: объём " + p.VolumeInUnits + " не делится на части не меньше минимального лота " + Symbol.VolumeInUnitsMin + ".");
                    }
                    m.PartialDone = true;
                }

                double breakeven = m.EntryPrice + (dir * BreakevenOffsetR * m.InitialRiskDistance);
                bool improves = p.StopLoss == null || (dir > 0 ? breakeven > p.StopLoss.Value : breakeven < p.StopLoss.Value);
                bool valid = dir > 0 ? breakeven < Symbol.Bid : breakeven > Symbol.Ask;

                if (improves && valid)
                {
                    TradeResult mod = ModifyPosition(p, breakeven, p.TakeProfit, ProtectionType.Absolute);
                    if (mod.IsSuccessful)
                    {
                        m.BreakevenDone = true;
                        Print("BREAKEVEN " + SymbolName + ": стоп перенесён на " + breakeven.ToString("F" + Symbol.Digits, CultureInfo.InvariantCulture) +
                              " (вход + " + F(BreakevenOffsetR) + "R).");
                    }
                }
                else if (!improves)
                {
                    m.BreakevenDone = true;
                }
            }
        }

        /// <summary>На каждом баре: ATR-трейлинг для позиций, уже переведённых в безубыток.</summary>
        private void ManageTrailingOnBar(int closedIndex)
        {
            double atr = _atr.Result[closedIndex];
            if (double.IsNaN(atr) || atr <= 0) return;

            foreach (Position p in Positions.FindAll(OrderLabel, SymbolName))
            {
                if (!_meta.TryGetValue(p.Id, out TradeMeta m) || !m.BreakevenDone) continue;

                int dir = p.TradeType == TradeType.Buy ? 1 : -1;
                double close = Bars.ClosePrices[closedIndex];
                double candidate = close - (dir * TrailAtrMultiplier * atr);
                double breakeven = m.EntryPrice + (dir * BreakevenOffsetR * m.InitialRiskDistance);

                // Трейлинг только подтягивает стоп и никогда не опускает его ниже безубытка.
                candidate = dir > 0 ? Math.Max(candidate, breakeven) : Math.Min(candidate, breakeven);
                bool improves = p.StopLoss == null || (dir > 0 ? candidate > p.StopLoss.Value + Symbol.TickSize : candidate < p.StopLoss.Value - Symbol.TickSize);
                bool valid = dir > 0 ? candidate < Symbol.Bid : candidate > Symbol.Ask;
                if (!improves || !valid) continue;

                ModifyPosition(p, candidate, p.TakeProfit, ProtectionType.Absolute);
            }
        }

        /// <summary>После перезапуска: восстанавливает сведения об уже открытых позициях бота.</summary>
        private void RebuildMetaForOpenPositions()
        {
            foreach (Position p in Positions.FindAll(OrderLabel, SymbolName))
            {
                if (_meta.ContainsKey(p.Id) || p.StopLoss == null) continue;

                int dir = p.TradeType == TradeType.Buy ? 1 : -1;
                double distance = Math.Abs(p.EntryPrice - p.StopLoss.Value);
                bool alreadyProtected = (p.StopLoss.Value - p.EntryPrice) * dir >= 0;

                double atr = _atr.Result[Math.Max(0, Bars.Count - 2)];
                if (alreadyProtected || distance <= 0) distance = double.IsNaN(atr) ? Symbol.PipSize * 10 : atr * SlAtrMultiplier;

                _meta[p.Id] = new TradeMeta
                {
                    Direction = dir,
                    EntryPrice = p.EntryPrice,
                    InitialRiskDistance = distance,
                    RiskMoney = Symbol.AmountRisked(p.VolumeInUnits, distance / Symbol.PipSize),
                    PartialDone = alreadyProtected,
                    BreakevenDone = alreadyProtected,
                    OpenBarIndex = Bars.Count - 2,
                };
                Print("Позиция " + p.Id + " подхвачена после перезапуска (сопровождение продолжается, обучение по ней не ведётся).");
            }
        }

        // =============================================================================================
        //  ОБУЧЕНИЕ НА РЕАЛЬНЫХ СДЕЛКАХ
        // =============================================================================================

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            Position p = args.Position;
            if (p.Label != OrderLabel || p.SymbolName != SymbolName) return;

            _lastTradeCloseIndex = Bars.Count - 2;

            if (!_meta.TryGetValue(p.Id, out TradeMeta m))
            {
                Print("CLOSE " + p.Id + ": " + p.NetProfit.ToString("F2", CultureInfo.InvariantCulture) + " (без данных для обучения).");
                return;
            }

            _meta.Remove(p.Id);

            double totalProfit = p.NetProfit + m.RealizedPartialProfit;
            double resultR = m.RiskMoney > 0 ? totalProfit / m.RiskMoney : 0;
            bool win = m.PartialDone || totalProfit > 0;

            _realTrades++;
            if (win) _realWins++;

            if (m.Features != null && m.VoteStrengths != null)
            {
                _nbReal.Add(m.Features, win, 1.0);

                // Обучение с подкреплением: веса стратегий, голосовавших за сделку, меняются по её результату.
                var changes = new StringBuilder();
                for (int s = 0; s < StrategyCount; s++)
                {
                    if (m.VoteStrengths[s] <= 0) continue;
                    double before = _realWeights[s];
                    double step = RlLearningRate * m.VoteStrengths[s] * Math.Max(-2.0, Math.Min(3.0, resultR)) * 0.5;
                    _realWeights[s] = Math.Max(0.25, Math.Min(3.0, before * Math.Exp(step)));
                    changes.Append(StrategyNames[s]).Append(' ').Append(F(before)).Append("→").Append(F(_realWeights[s])).Append("; ");
                }

                Print("CLOSE " + SymbolName + " " + (win ? "WIN" : "LOSS") + " " + F(resultR) + "R (" +
                      totalProfit.ToString("F2", CultureInfo.InvariantCulture) + ") | RL-веса: " + changes);
            }

            SaveMemory();
        }

        // =============================================================================================
        //  РИСК: СУТКИ И ПРОСАДКА
        // =============================================================================================

        private void RollDay(DateTime nowUtc)
        {
            if (nowUtc.Date == _dayKey) return;

            _dayKey = nowUtc.Date;
            _dayStartEquity = Account.Equity;
            if (_dailyLocked) Print("Новые сутки: блокировка по дневному лимиту снята.");
            _dailyLocked = false;
        }

        private void UpdateEquityPeak()
        {
            double equity = Account.Equity;
            if (equity > _peakEquity) _peakEquity = equity;

            if (!_dailyLocked && _dayStartEquity > 0 && equity <= _dayStartEquity * (1.0 - (DailyLossLimitPercent / 100.0)))
            {
                _dailyLocked = true;
                Print("DAILY LOSS LIMIT: капитал " + equity.ToString("F2", CultureInfo.InvariantCulture) + " против " +
                      _dayStartEquity.ToString("F2", CultureInfo.InvariantCulture) + " на начало суток. Новые входы запрещены до конца суток UTC.");
            }
        }

        private double DrawdownPercent() => _peakEquity > 0 ? Math.Max(0, (_peakEquity - Account.Equity) / _peakEquity * 100.0) : 0;

        private double DailyPnlPercent() => _dayStartEquity > 0 ? (Account.Equity - _dayStartEquity) / _dayStartEquity * 100.0 : 0;

        // =============================================================================================
        //  ПАМЯТЬ AI (LocalStorage)
        // =============================================================================================

        /// <summary>
        /// Сохраняются только знания из РЕАЛЬНЫХ сделок: RL-веса и статистика Naive Bayes. Теневые знания
        /// заново строятся из истории при каждом запуске — иначе одни и те же бары засчитывались бы
        /// повторно после каждого перезапуска.
        /// </summary>
        private void SaveMemory()
        {
            try
            {
                var sb = new StringBuilder("v5;");
                for (int s = 0; s < StrategyCount; s++) sb.Append(R(_realWeights[s])).Append(';');
                sb.Append(R(_nbReal.ClassWeight[0])).Append(';').Append(R(_nbReal.ClassWeight[1])).Append(';');
                for (int c = 0; c < 2; c++)
                {
                    for (int f = 0; f < NaiveBayesModel.FeatureCount; f++)
                    {
                        GaussianStat g = _nbReal.Stats[c, f];
                        sb.Append(R(g.W)).Append(';').Append(R(g.Mean)).Append(';').Append(R(g.M2)).Append(';');
                    }
                }
                sb.Append(_realTrades).Append(';').Append(_realWins);

                LocalStorage.SetString(_storageKey, sb.ToString(), LocalStorageScope.Type);
                LocalStorage.Flush(LocalStorageScope.Type);
            }
            catch (Exception ex)
            {
                Print("Не удалось сохранить память AI: " + ex.Message);
            }
        }

        private void LoadMemory()
        {
            try
            {
                string text = LocalStorage.GetString(_storageKey, LocalStorageScope.Type);
                if (string.IsNullOrEmpty(text) || !text.StartsWith("v5;", StringComparison.Ordinal))
                {
                    Print("Память AI для " + SymbolName + " " + TimeFrame + " не найдена — начинаю с нейтральных весов.");
                    return;
                }

                string[] parts = text.Split(';');
                int expected = 1 + StrategyCount + 2 + (2 * NaiveBayesModel.FeatureCount * 3) + 2;
                if (parts.Length < expected)
                {
                    Print("Память AI повреждена или устарела — игнорирую.");
                    return;
                }

                int idx = 1;
                for (int s = 0; s < StrategyCount; s++) _realWeights[s] = Math.Max(0.25, Math.Min(3.0, P(parts[idx++], 1.0)));
                _nbReal.ClassWeight[0] = Math.Max(0, P(parts[idx++], 0));
                _nbReal.ClassWeight[1] = Math.Max(0, P(parts[idx++], 0));
                for (int c = 0; c < 2; c++)
                {
                    for (int f = 0; f < NaiveBayesModel.FeatureCount; f++)
                    {
                        GaussianStat g = _nbReal.Stats[c, f];
                        g.W = Math.Max(0, P(parts[idx++], 0));
                        g.Mean = P(parts[idx++], 0);
                        g.M2 = Math.Max(0, P(parts[idx++], 0));
                    }
                }
                _realTrades = (int)P(parts[idx++], 0);
                _realWins = (int)P(parts[idx], 0);

                Print("Память AI загружена: реальных сделок " + _realTrades + ", веса " + WeightsText() + ".");
            }
            catch (Exception ex)
            {
                _nbReal.Clear();
                for (int s = 0; s < StrategyCount; s++) _realWeights[s] = 1.0;
                Print("Не удалось прочитать память AI (" + ex.Message + ") — начинаю с нуля.");
            }
        }

        private static string StorageKey(string raw)
        {
            var sb = new StringBuilder(raw.Length);
            foreach (char ch in raw)
            {
                bool latin = (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9');
                sb.Append(latin ? ch : ' ');
            }
            string key = sb.ToString().Trim();
            while (key.Contains("  ")) key = key.Replace("  ", " ");
            return key.Length > 0 ? key : "QAIv5";
        }

        private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        private static double P(string s, double fallback) =>
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && !double.IsNaN(v) && !double.IsInfinity(v) ? v : fallback;

        // =============================================================================================
        //  ЖУРНАЛ
        // =============================================================================================

        private void RegisterSkip(SkipReason reason, string detail, BarAnalysis a)
        {
            _skipCounts.TryGetValue(reason, out int n);
            _skipCounts[reason] = n + 1;

            if (!VerboseSkips) return;

            string context = a == null
                ? ""
                : " | " + RegimeLabel(a) + " | ADX " + a.Adx.ToString("F1", CultureInfo.InvariantCulture) +
                  " | DD " + DrawdownPercent().ToString("F2", CultureInfo.InvariantCulture) + "%";
            Print("SKIP: " + SkipTitle(reason) + " — " + detail + context);
        }

        private static string SkipTitle(SkipReason r)
        {
            switch (r)
            {
                case SkipReason.NoSignal: return "No Signal";
                case SkipReason.LowConfidence: return "Low Confidence";
                case SkipReason.HighSpread: return "High Spread";
                case SkipReason.CostVsProfit: return "High Spread vs Profit";
                case SkipReason.NegativeEV: return "Negative EV";
                case SkipReason.UnclearRegime: return "Unclear Regime";
                case SkipReason.SessionBlock: return "Session Block";
                case SkipReason.DailyLossLimit: return "Daily Loss Limit";
                case SkipReason.PositionOpen: return "Position Open";
                case SkipReason.Cooldown: return "Cooldown";
                case SkipReason.SweepBlock: return "Liquidity Sweep";
                case SkipReason.AccountTooSmall: return "Account Too Small";
                case SkipReason.MarketClosed: return "Market Closed";
                case SkipReason.OrderFailed: return "Order Failed";
                case SkipReason.Warmup: return "Warmup";
                default: return r.ToString();
            }
        }

        private string RegimeLabel(BarAnalysis a)
        {
            string kind;
            if (a.PTrend >= 0.5 + (MinRegimeConfidence / 2.0))
            {
                kind = a.TrendSlopeSign > 0 ? "ТРЕНД↑" : a.TrendSlopeSign < 0 ? "ТРЕНД↓" : "ТРЕНД";
            }
            else if (a.PTrend <= 0.5 - (MinRegimeConfidence / 2.0))
            {
                kind = "ФЛЭТ";
            }
            else
            {
                kind = "ПЕРЕХОД";
            }
            return kind + " P(тренд) " + F(a.PTrend);
        }

        private string VotesText(BarAnalysis a)
        {
            var sb = new StringBuilder();
            for (int s = 0; s < StrategyCount; s++)
            {
                StrategyVote v = a.Votes[s];
                if (v.Direction == 0) continue;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(StrategyNames[s]).Append(v.Direction > 0 ? " ▲" : " ▼").Append(F(v.Strength)).Append("×w").Append(F(EffectiveWeight(s)));
            }
            return sb.Length > 0 ? sb.ToString() : "нет";
        }

        private string WeightsText()
        {
            var sb = new StringBuilder();
            for (int s = 0; s < StrategyCount; s++)
            {
                if (s > 0) sb.Append(", ");
                sb.Append(StrategyNames[s]).Append(' ').Append(F(EffectiveWeight(s)));
            }
            return sb.ToString();
        }

        private string BuildStatsLine()
        {
            double realRate = _realTrades > 0 ? (double)_realWins / _realTrades * 100.0 : 0;
            double shadowRate = _shadowResolved > 0 ? (double)_shadowWins / _shadowResolved * 100.0 : 0;
            return "Реальных сделок " + _realTrades + " (до +1R/в плюс " + realRate.ToString("F0", CultureInfo.InvariantCulture) +
                   "%), теневых " + _shadowResolved + " (" + shadowRate.ToString("F0", CultureInfo.InvariantCulture) + "%).";
        }

        private void PrintStatus(int closedIndex, BarAnalysis a)
        {
            var sb = new StringBuilder();
            sb.Append("STATUS ").Append(SymbolName).Append(' ').Append(TimeFrame);
            if (a != null)
            {
                sb.Append(" | ").Append(RegimeLabel(a))
                  .Append(" | ADX ").Append(a.Adx.ToString("F1", CultureInfo.InvariantCulture))
                  .Append(" | ATR ").Append(a.Atr.ToString("F" + Symbol.Digits, CultureInfo.InvariantCulture))
                  .Append(" | спред/ATR ").Append(Pct(CurrentSpread() / a.Atr))
                  .Append(" | порог входа ").Append(F(EntryThreshold(a.Adx)));
            }
            sb.Append(" | капитал ").Append(Account.Equity.ToString("F2", CultureInfo.InvariantCulture))
              .Append(" | за сутки ").Append(DailyPnlPercent().ToString("F2", CultureInfo.InvariantCulture)).Append('%')
              .Append(" | просадка ").Append(DrawdownPercent().ToString("F2", CultureInfo.InvariantCulture)).Append('%')
              .Append(_dailyLocked ? " | ДНЕВНОЙ ЛИМИТ" : "");
            Print(sb.ToString());

            Print("  AI: " + BuildStatsLine() + " Веса: " + WeightsText() + ". Открытых теневых: " + _shadowTrades.Count + ".");

            if (_skipCounts.Count > 0)
            {
                var list = new List<KeyValuePair<SkipReason, int>>(_skipCounts);
                list.Sort((x, y) => y.Value.CompareTo(x.Value));
                var skips = new StringBuilder("  Причины пропусков: ");
                for (int k = 0; k < list.Count && k < 6; k++)
                {
                    if (k > 0) skips.Append(", ");
                    skips.Append(SkipTitle(list[k].Key)).Append(' ').Append(list[k].Value);
                }
                Print(skips.ToString());
            }
        }

        private static string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

        private static string Pct(double fraction) => (fraction * 100.0).ToString("F1", CultureInfo.InvariantCulture) + "%";
    }
}
