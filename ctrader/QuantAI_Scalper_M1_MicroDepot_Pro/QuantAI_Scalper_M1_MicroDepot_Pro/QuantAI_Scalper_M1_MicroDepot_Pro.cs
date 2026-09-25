// =====================================================================================================
//  QuantAI_Scalper_M1_MicroDepot_Pro  v1.0.0
//  cTrader Automate cBot | EURUSD M1/M5 micro-impulse scalper for small (50 EUR) accounts
//  Builds on cTrader 4.2+ and 5.x (.NET 6 or legacy .NET Framework target, C# 7.3 syntax only).
// -----------------------------------------------------------------------------------------------------
//  ENGINE
//   1. Tick Velocity Engine   Counts real ticks in a sliding window (default 3 s) and compares that
//                             density with the average tick rate of the last N signal bars. A ratio of
//                             at least N (UI) marks an institutional inflow; entries require it.
//   2. Volman 20 EMA Squeeze  The last K bars form a tight box hugging the 20 EMA; the entry fires on
//                             the tick that breaks the box (impulsive micro-breakout).
//   3. Liquidity Sweep        A fast false pierce of the 10-bar high/low; the entry fires when price
//                             reclaims the 20 EMA within a few bars (return to the mean after the stop run).
//   4. AI classifier          Gaussian Naive Bayes over [Tick_Velocity, EMA20_Distance, RSI_Slope,
//                             Spread_Ratio]. Trained on history at start, then updated online from every
//                             closed bar with the outcome this bot's own exit rules would have produced.
//                             Output: Signal Confidence 0.00-1.00, compared with Min Confidence (UI).
//   5. Risk and exits         % risk sizing with a 0.01-lot floor and a margin cap, Spread/ATR filter,
//                             SL 0.8 x ATR, TP1 1.0 x ATR closing 60 %, micro break-even (+0.1 pip) at
//                             +0.4R, ultra-short ATR trailing for the runner, time exit after 5 M1 bars,
//                             daily loss limit and loss-streak pause.
//
//  Every threshold that drives a trading decision is a UI parameter and is used exactly as entered:
//  nothing is clamped, capped or overridden in code. The start-up log prints the values in force and
//  every skipped signal is logged with its reasons, e.g.
//      SKIP [SQZ BUY @1.08540]: Spread too high (...) | Low Tick Velocity (...) | AI Confidence 28% < Target 55%
//  Backtest with "Tick data": the tick velocity engine needs real ticks.
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
    /// <summary>What to do at TP1 when the position is too small to be split (for example 0.01 lot).</summary>
    public enum NoSplitAction
    {
        CloseAllAtTp1,
        TrailWholePosition
    }

    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class QuantAI_Scalper_M1_MicroDepot_Pro : Robot
    {
        private const string BotVersion = "1.0.0";

        #region Parameters

        // ---- 1. Risk and money ------------------------------------------------------------------------

        [Parameter("Risk Percent (% of balance)", Group = "1. Risk & Money", DefaultValue = 1.0, MinValue = 0.01, MaxValue = 100.0, Step = 0.05)]
        public double RiskPercent { get; set; }

        [Parameter("Min Lots (floor)", Group = "1. Risk & Money", DefaultValue = 0.01, MinValue = 0.0, Step = 0.01)]
        public double MinLots { get; set; }

        [Parameter("Max Lots (cap)", Group = "1. Risk & Money", DefaultValue = 1.0, MinValue = 0.01, Step = 0.01)]
        public double MaxLots { get; set; }

        [Parameter("Use Min Lot If Risk Size Is Smaller", Group = "1. Risk & Money", DefaultValue = true)]
        public bool AllowMinLotOverride { get; set; }

        [Parameter("Max Margin Use (% of free margin)", Group = "1. Risk & Money", DefaultValue = 80.0, MinValue = 1.0, MaxValue = 100.0, Step = 5.0)]
        public double MaxMarginUsePercent { get; set; }

        [Parameter("Leverage For Margin Check (0 = auto)", Group = "1. Risk & Money", DefaultValue = 0.0, MinValue = 0.0, Step = 1.0)]
        public double LeverageOverride { get; set; }

        [Parameter("Max Slippage Pips (0 = plain market)", Group = "1. Risk & Money", DefaultValue = 0.5, MinValue = 0.0, Step = 0.1)]
        public double MaxSlippagePips { get; set; }

        [Parameter("Daily Max Loss % (0 = off)", Group = "1. Risk & Money", DefaultValue = 6.0, MinValue = 0.0, Step = 0.5)]
        public double DailyMaxLossPercent { get; set; }

        [Parameter("Max Consecutive Losses (0 = off)", Group = "1. Risk & Money", DefaultValue = 4, MinValue = 0)]
        public int MaxConsecutiveLosses { get; set; }

        [Parameter("Loss-Streak Pause (minutes)", Group = "1. Risk & Money", DefaultValue = 30, MinValue = 0)]
        public int LossStreakPauseMinutes { get; set; }

        [Parameter("Max Trades Per Day (0 = off)", Group = "1. Risk & Money", DefaultValue = 0, MinValue = 0)]
        public int MaxTradesPerDay { get; set; }

        // ---- 2. Exits ----------------------------------------------------------------------------------

        [Parameter("SL ATR Multiplier", Group = "2. Exits", DefaultValue = 0.8, MinValue = 0.05, Step = 0.05)]
        public double SlAtrMultiplier { get; set; }

        [Parameter("TP1 ATR Multiplier", Group = "2. Exits", DefaultValue = 1.0, MinValue = 0.05, Step = 0.05)]
        public double Tp1AtrMultiplier { get; set; }

        [Parameter("TP1 Close Percent", Group = "2. Exits", DefaultValue = 60.0, MinValue = 1.0, MaxValue = 100.0, Step = 5.0)]
        public double Tp1ClosePercent { get; set; }

        [Parameter("If Volume Cannot Be Split", Group = "2. Exits", DefaultValue = NoSplitAction.CloseAllAtTp1)]
        public NoSplitAction NoSplitMode { get; set; }

        [Parameter("Runner Hard TP ATR Mult (0 = none)", Group = "2. Exits", DefaultValue = 0.0, MinValue = 0.0, Step = 0.1)]
        public double Tp2AtrMultiplier { get; set; }

        [Parameter("Trailing ATR Multiplier (0 = off)", Group = "2. Exits", DefaultValue = 0.5, MinValue = 0.0, Step = 0.05)]
        public double TrailAtrMultiplier { get; set; }

        [Parameter("Trailing Step Pips", Group = "2. Exits", DefaultValue = 0.2, MinValue = 0.0, Step = 0.1)]
        public double TrailStepPips { get; set; }

        [Parameter("Trail Only After TP1", Group = "2. Exits", DefaultValue = true)]
        public bool TrailOnlyAfterTp1 { get; set; }

        [Parameter("Break-Even Trigger R (0 = off)", Group = "2. Exits", DefaultValue = 0.4, MinValue = 0.0, Step = 0.05)]
        public double BeTriggerR { get; set; }

        [Parameter("Break-Even Offset Pips", Group = "2. Exits", DefaultValue = 0.1, MinValue = 0.0, Step = 0.1)]
        public double BeOffsetPips { get; set; }

        [Parameter("Time Exit Bars (0 = off)", Group = "2. Exits", DefaultValue = 5, MinValue = 0)]
        public int TimeExitBars { get; set; }

        [Parameter("ATR & Time-Exit TimeFrame", Group = "2. Exits", DefaultValue = "Minute")]
        public TimeFrame RiskTimeFrame { get; set; }

        [Parameter("ATR Period", Group = "2. Exits", DefaultValue = 14, MinValue = 2)]
        public int AtrPeriod { get; set; }

        // ---- 3. Entry filters --------------------------------------------------------------------------

        [Parameter("Max Spread / ATR", Group = "3. Entry Filters", DefaultValue = 0.12, MinValue = 0.0, Step = 0.01)]
        public double MaxSpreadToAtr { get; set; }

        [Parameter("Use Session Filter", Group = "3. Entry Filters", DefaultValue = true)]
        public bool UseSessionFilter { get; set; }

        [Parameter("Session Start Hour (UTC)", Group = "3. Entry Filters", DefaultValue = 6, MinValue = 0, MaxValue = 23)]
        public int SessionStartHour { get; set; }

        [Parameter("Session End Hour (UTC)", Group = "3. Entry Filters", DefaultValue = 20, MinValue = 0, MaxValue = 24)]
        public int SessionEndHour { get; set; }

        [Parameter("Cooldown After Close (sec)", Group = "3. Entry Filters", DefaultValue = 30, MinValue = 0)]
        public int CooldownSeconds { get; set; }

        [Parameter("Max Chase Beyond Trigger (x ATR)", Group = "3. Entry Filters", DefaultValue = 0.5, MinValue = 0.0, Step = 0.05)]
        public double MaxChaseAtr { get; set; }

        // ---- 4. Tick velocity engine -------------------------------------------------------------------

        [Parameter("Use Tick Velocity Filter", Group = "4. Tick Velocity Engine", DefaultValue = true)]
        public bool UseTickVelocity { get; set; }

        [Parameter("Tick Window (sec)", Group = "4. Tick Velocity Engine", DefaultValue = 3.0, MinValue = 0.5, Step = 0.5)]
        public double TickWindowSeconds { get; set; }

        [Parameter("Baseline Bars", Group = "4. Tick Velocity Engine", DefaultValue = 100, MinValue = 5)]
        public int TickBaselineBars { get; set; }

        [Parameter("Velocity Multiplier N", Group = "4. Tick Velocity Engine", DefaultValue = 2.0, MinValue = 0.0, Step = 0.1)]
        public double TickVelocityMultiplier { get; set; }

        // ---- 5. Volman setups --------------------------------------------------------------------------

        [Parameter("Signal TimeFrame (M1/M5)", Group = "5. Volman Setups", DefaultValue = "Minute")]
        public TimeFrame SignalTimeFrame { get; set; }

        [Parameter("EMA Period", Group = "5. Volman Setups", DefaultValue = 20, MinValue = 2)]
        public int EmaPeriod { get; set; }

        [Parameter("Use 20 EMA Squeeze", Group = "5. Volman Setups", DefaultValue = true)]
        public bool UseSqueeze { get; set; }

        [Parameter("Squeeze Bars", Group = "5. Volman Setups", DefaultValue = 6, MinValue = 2)]
        public int SqueezeBars { get; set; }

        [Parameter("Squeeze Max Range (x ATR)", Group = "5. Volman Setups", DefaultValue = 1.5, MinValue = 0.05, Step = 0.05)]
        public double SqueezeMaxRangeAtr { get; set; }

        [Parameter("Squeeze Max EMA Gap (x ATR)", Group = "5. Volman Setups", DefaultValue = 0.3, MinValue = 0.0, Step = 0.05)]
        public double SqueezeMaxEmaGapAtr { get; set; }

        [Parameter("Breakout Buffer Pips", Group = "5. Volman Setups", DefaultValue = 0.2, MinValue = 0.0, Step = 0.1)]
        public double BreakoutBufferPips { get; set; }

        [Parameter("Use Liquidity Sweep", Group = "5. Volman Setups", DefaultValue = true)]
        public bool UseSweep { get; set; }

        [Parameter("Sweep Lookback Bars", Group = "5. Volman Setups", DefaultValue = 10, MinValue = 2)]
        public int SweepLookbackBars { get; set; }

        [Parameter("Sweep Min Pierce Pips", Group = "5. Volman Setups", DefaultValue = 0.2, MinValue = 0.0, Step = 0.1)]
        public double SweepMinPiercePips { get; set; }

        [Parameter("Sweep Bar Must Close Back Inside", Group = "5. Volman Setups", DefaultValue = true)]
        public bool SweepRequireCloseInside { get; set; }

        [Parameter("Sweep Max Bars To EMA Reclaim", Group = "5. Volman Setups", DefaultValue = 3, MinValue = 1)]
        public int SweepMaxBarsToReclaim { get; set; }

        // ---- 6. AI classifier --------------------------------------------------------------------------

        [Parameter("Use AI Filter", Group = "6. AI Classifier (Naive Bayes)", DefaultValue = true)]
        public bool UseAiFilter { get; set; }

        [Parameter("Min Confidence (0.00-1.00)", Group = "6. AI Classifier (Naive Bayes)", DefaultValue = 0.55, MinValue = 0.0, MaxValue = 1.0, Step = 0.01)]
        public double MinConfidence { get; set; }

        [Parameter("Training Window (bars)", Group = "6. AI Classifier (Naive Bayes)", DefaultValue = 3000, MinValue = 50)]
        public int AiTrainingBars { get; set; }

        [Parameter("Min Samples Per Class", Group = "6. AI Classifier (Naive Bayes)", DefaultValue = 100, MinValue = 2)]
        public int AiMinSamplesPerClass { get; set; }

        [Parameter("Label Horizon (ATR TF bars)", Group = "6. AI Classifier (Naive Bayes)", DefaultValue = 5, MinValue = 1)]
        public int AiHorizonBars { get; set; }

        [Parameter("RSI Period", Group = "6. AI Classifier (Naive Bayes)", DefaultValue = 14, MinValue = 2)]
        public int RsiPeriod { get; set; }

        [Parameter("RSI Slope Bars", Group = "6. AI Classifier (Naive Bayes)", DefaultValue = 3, MinValue = 1)]
        public int RsiSlopeBars { get; set; }

        [Parameter("Use Empirical Win-Rate Prior", Group = "6. AI Classifier (Naive Bayes)", DefaultValue = false)]
        public bool AiUseEmpiricalPrior { get; set; }

        [Parameter("Training Spread Pips (0 = live)", Group = "6. AI Classifier (Naive Bayes)", DefaultValue = 0.0, MinValue = 0.0, Step = 0.1)]
        public double AiTrainSpreadPips { get; set; }

        // ---- 7. Log and display ------------------------------------------------------------------------

        [Parameter("Position Label", Group = "7. Log & Display", DefaultValue = "QuantAI_M1")]
        public string BotLabel { get; set; }

        [Parameter("Log Every Bar", Group = "7. Log & Display", DefaultValue = true)]
        public bool LogBarSummary { get; set; }

        [Parameter("Log Skip Reasons", Group = "7. Log & Display", DefaultValue = true)]
        public bool LogSkipReasons { get; set; }

        [Parameter("Show Chart HUD", Group = "7. Log & Display", DefaultValue = true)]
        public bool ShowHud { get; set; }

        #endregion

        #region Technical constants

        // Numerical plumbing only - none of these decides whether a trade is taken.
        private const int IndicatorWarmupFactor = 3;     // bars per indicator period before values are trusted
        private const int CalibrationWindowBars = 30;    // bars used to match OnTick counts with broker tick volume
        private const int CalibrationMinBars = 3;        // calibration is applied once this many full bars were seen
        private const double SpreadSmoothing = 0.02;     // EMA factor of the tracked average spread
        private const int MaxNoMoneyRetries = 2;         // halve the volume and retry after a "no money" reject
        private const double RetryDelaySeconds = 1.0;    // first pause before retrying a failed close/modify
        private const double MaxRetryDelaySeconds = 60.0; // the pause doubles per failure up to this
        private const double ErrorLogIntervalSeconds = 10.0;
        private const double VolumeEpsilon = 1e-6;
        private const string ObjPrefix = "QAI_";

        #endregion

        #region State

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private Bars _sig;
        private Bars _risk;
        private bool _sameTf;
        private double _sigSec;
        private double _riskSec;
        private ExponentialMovingAverage _ema;
        private AverageTrueRange _atrSig;
        private AverageTrueRange _atrRisk;
        private RelativeStrengthIndex _rsi;

        private TickVelocityEngine _ticks;
        private TickCalibrator _calib;
        private DateTime _engineStart;
        private DateTime _calBarOpen = DateTime.MinValue;
        private int _calBarTicks;
        private bool _calBarFull;
        private double _baselineTicksPerBar;
        private double _avgSpread;

        private GaussianNaiveBayes _ai;
        private bool _aiWasReady;
        private readonly List<PendingSample> _pending = new List<PendingSample>();
        private readonly double[] _contrib = new double[AiFeatures.Dim];
        private double[] _hBuf = new double[1];
        private double[] _lBuf = new double[1];
        private double _lastConf = double.NaN;

        private DateTime _lastSigClosed = DateTime.MinValue;
        private DateTime _lastRiskClosed = DateTime.MinValue;

        private double[] _winH = new double[2];
        private double[] _winL = new double[2];
        private bool _sqzArmed;
        private double _sqzHigh;
        private double _sqzLow;
        private DateTime _sqzStart;
        private string _sqzNote = "-";

        private bool _bullSweep;
        private double _bullExtreme;
        private int _bullBarsLeft;
        private DateTime _bullTime;
        private bool _bearSweep;
        private double _bearExtreme;
        private int _bearBarsLeft;
        private DateTime _bearTime;
        private string _swpIdle = "-";
        private string _swpNote = "-";
        private double _reclaimEma;
        private double _setupAtr;

        private DateTime _day = DateTime.MinValue;
        private double _dayStartBalance;
        private int _tradesToday;
        private bool _dailyHalt;
        private int _consecLosses;
        private DateTime _pauseUntil = DateTime.MinValue;
        private DateTime _lastCloseTime = DateTime.MinValue;

        private readonly Dictionary<int, TradeState> _trades = new Dictionary<int, TradeState>();
        private readonly HashSet<string> _skipKeys = new HashSet<string>();
        private string _lastSkip = "-";
        private string _ccy = "";
        private bool _quiet;
        private bool _hudEnabled;
        private DateTime _lastErrorLog = DateTime.MinValue;

        private int _statTrades;
        private int _statWins;
        private int _statLosses;
        private int _statScratch;
        private int _statTp1;
        private int _statTimeExits;
        private double _statNet;

        #endregion

        #region Lifecycle

        protected override void OnStart()
        {
            _quiet = RunningMode == RunningMode.Optimization;
            _ccy = Account.Asset != null ? Account.Asset.Name : "";

            string invalid = ValidateParameters();
            if (invalid != null)
            {
                Log("FATAL: invalid parameters - " + invalid + ". The cBot stops; fix the value in the parameter panel.");
                Stop();
                return;
            }

            _sig = MarketData.GetBars(SignalTimeFrame);
            _risk = MarketData.GetBars(RiskTimeFrame);
            _sameTf = SignalTimeFrame.Equals(RiskTimeFrame);
            _sigSec = TimeFrameSeconds(SignalTimeFrame, _sig);
            _riskSec = TimeFrameSeconds(RiskTimeFrame, _risk);
            if (_sigSec < 60.0 || _riskSec < 60.0)
            {
                Log("FATAL: Signal TimeFrame and ATR TimeFrame must be time based (m1 or higher), got "
                    + SignalTimeFrame + " / " + RiskTimeFrame + ". The cBot stops.");
                Stop();
                return;
            }

            LoadHistory();

            _ema = Indicators.ExponentialMovingAverage(_sig.ClosePrices, EmaPeriod);
            _atrSig = Indicators.AverageTrueRange(_sig, AtrPeriod, MovingAverageType.WilderSmoothing);
            _atrRisk = Indicators.AverageTrueRange(_risk, AtrPeriod, MovingAverageType.WilderSmoothing);
            _rsi = Indicators.RelativeStrengthIndex(_sig.ClosePrices, RsiPeriod);

            _ticks = new TickVelocityEngine(Math.Max(TickWindowSeconds, _sigSec) + 5.0);
            _calib = new TickCalibrator(CalibrationWindowBars);
            _ai = new GaussianNaiveBayes(AiFeatures.Dim, AiTrainingBars * 2);
            _engineStart = Server.Time;
            _avgSpread = Symbol.Spread > 0 ? Symbol.Spread : 0.0;
            _hBuf = new double[AiHorizonBars];
            _lBuf = new double[AiHorizonBars];
            _winH = new double[SqueezeBars];
            _winL = new double[SqueezeBars];
            _hudEnabled = ShowHud && (RunningMode == RunningMode.RealTime || RunningMode == RunningMode.VisualBacktesting);

            ResetDay(Server.Time);
            LogBanner();
            TrainFromHistory();
            RecoverOpenPositions();
            Positions.Closed += OnPositionClosed;

            int lastClosed = _sig.Count - 2;
            if (lastClosed >= 0)
            {
                _lastSigClosed = _sig.OpenTimes[lastClosed];
                _baselineTicksPerBar = MeanTickVolume(_sig, lastClosed, TickBaselineBars);
                EvaluateSetups(lastClosed);
                DrawSetups();
            }
            if (_baselineTicksPerBar <= 0)
                Log("WARNING: the broker's " + SignalTimeFrame.ShortName + " bars carry no tick volume, so tick velocity cannot be measured"
                    + (UseTickVelocity ? " and every signal will be skipped as Low Tick Velocity; switch 'Use Tick Velocity Filter' off for this broker" : ""));
            else
                Log("TICK BASELINE: " + F(_baselineTicksPerBar, 1) + " ticks per " + SignalTimeFrame.ShortName + " bar ("
                    + F(_baselineTicksPerBar / _sigSec, 2) + " ticks/s) over the last " + TickBaselineBars + " bars");
            if (_risk.Count >= 2)
                _lastRiskClosed = _risk.OpenTimes[_risk.Count - 2];

            Timer.Start(TimeSpan.FromSeconds(1));
            UpdateHud();
        }

        protected override void OnTick()
        {
            try
            {
                DateTime now = Server.Time;
                _ticks.AddTick(now);
                CountTickForCalibration();
                TrackSpread();
                CheckNewDay(now);
                ProcessClosedBars();
                CheckDailyLoss();
                ManagePositions();
                EvaluateEntries();
            }
            catch (Exception ex)
            {
                LogError("OnTick", ex);
            }
        }

        protected override void OnTimer()
        {
            try
            {
                // Time exits must fire even when the feed goes quiet, so management also runs here.
                CheckNewDay(Server.Time);
                CheckDailyLoss();
                ManagePositions();
                UpdateHud();
            }
            catch (Exception ex)
            {
                LogError("OnTimer", ex);
            }
        }

        protected override void OnError(Error error)
        {
            // The default handler may stop the cBot on a rejected request. A scalper has to survive a
            // closed market, a "no money" reject or bad stops, so the error is logged and trading goes on.
            Log("TRADE ERROR: " + error.Code);
        }

        protected override void OnStop()
        {
            try
            {
                Log("STOP v" + BotVersion + ": trades " + _statTrades + " (wins " + _statWins + ", losses " + _statLosses
                    + ", scratch " + _statScratch + "), TP1 hits " + _statTp1 + ", time exits " + _statTimeExits
                    + ", net " + Signed(_statNet, 2) + " " + _ccy
                    + (_ai != null ? " | AI samples " + _ai.Count + ", base win-rate " + Pct(_ai.WinRate) : ""));
            }
            catch (Exception ex)
            {
                Log("STOP: " + ex.Message);
            }
        }

        private string ValidateParameters()
        {
            if (MinLots > MaxLots)
                return "Min Lots (" + F(MinLots, 2) + ") is greater than Max Lots (" + F(MaxLots, 2) + ")";
            if (string.IsNullOrWhiteSpace(BotLabel))
                return "Position Label is empty";
            if (SignalTimeFrame == null || RiskTimeFrame == null)
                return "a TimeFrame parameter is empty";
            return null;
        }

        #endregion

        #region History and AI training

        private int WarmupBars()
        {
            int indicators = Math.Max(EmaPeriod, Math.Max(RsiPeriod, AtrPeriod)) * IndicatorWarmupFactor + RsiSlopeBars;
            int patterns = Math.Max(SqueezeBars, SweepLookbackBars + 1);
            return Math.Max(Math.Max(indicators, patterns), TickBaselineBars) + 1;
        }

        private void LoadHistory()
        {
            int needSig = AiTrainingBars + WarmupBars() + 2;
            EnsureBars(_sig, needSig, "signal " + SignalTimeFrame.ShortName);
            // Labels are simulated on the ATR timeframe, so it has to cover the same span of time.
            int needRisk = (int)Math.Ceiling(needSig * _sigSec / _riskSec) + AtrPeriod * IndicatorWarmupFactor + AiHorizonBars + 2;
            EnsureBars(_risk, needRisk, "ATR " + RiskTimeFrame.ShortName);
        }

        private void EnsureBars(Bars bars, int needed, string what)
        {
            try
            {
                while (bars.Count < needed)
                {
                    if (bars.LoadMoreHistory() <= 0)
                        break;
                }
            }
            catch (Exception ex)
            {
                Log("HISTORY: cannot load more " + what + " bars (" + ex.Message + ")");
            }
            if (bars.Count < needed)
                Log("HISTORY: " + what + " has " + bars.Count + " bars, wanted " + needed
                    + " - the AI trains on what is available and keeps learning online");
        }

        private double TrainingSpread()
        {
            if (AiTrainSpreadPips > 0)
                return AiTrainSpreadPips * Symbol.PipSize;
            if (_avgSpread > 0)
                return _avgSpread;
            return Math.Max(Symbol.Spread, 0.0);
        }

        private void TrainFromHistory()
        {
            int lastClosed = _sig.Count - 2;
            int first = Math.Max(WarmupBars(), lastClosed - AiTrainingBars + 1);
            double spread = TrainingSpread();
            int bars = 0;
            for (int c = first; c <= lastClosed; c++)
            {
                PendingSample s = BuildSample(c, spread);
                if (s == null)
                    continue;
                bars++;
                if (!TryResolveSample(s))
                    _pending.Add(s);
            }
            Log("AI BOOTSTRAP: " + bars + " " + SignalTimeFrame.ShortName + " bars -> " + _ai.Count + " labelled samples (+"
                + (_pending.Count * 2) + " pending). Label = TP1 reached before SL/BE within " + AiHorizonBars + " "
                + RiskTimeFrame.ShortName + " bars; spread " + Pips(spread) + "; base win-rate " + Pct(_ai.WinRate)
                + "; prior " + (AiUseEmpiricalPrior ? "empirical" : "balanced 50/50"));
            _aiWasReady = _ai.IsReady(AiMinSamplesPerClass);
            LogAiState();
        }

        private void LogAiState()
        {
            if (_ai.IsReady(AiMinSamplesPerClass))
            {
                var sb = new StringBuilder("AI READY: ");
                sb.Append(_ai.Wins).Append(" wins / ").Append(_ai.Losses).Append(" losses; class means win/loss:");
                for (int f = 0; f < AiFeatures.Dim; f++)
                {
                    sb.Append(' ').Append(AiFeatures.Names[f]).Append(' ')
                      .Append(F(_ai.ClassMean(true, f), 3)).Append('/').Append(F(_ai.ClassMean(false, f), 3));
                }
                Log(sb.ToString());
            }
            else
            {
                Log("AI WARMING UP: wins " + _ai.Wins + ", losses " + _ai.Losses + " (need " + AiMinSamplesPerClass + " each)"
                    + (UseAiFilter ? " - entries wait for the model" : " - AI filter is OFF, entries are not blocked"));
            }
        }

        /// <summary>Features and replay inputs for the hypothetical entry at the close of signal bar c.</summary>
        private PendingSample BuildSample(int c, double spread)
        {
            if (c < WarmupBars() || c > _sig.Count - 2 || c - RsiSlopeBars < 0)
                return null;

            double atrS = _atrSig.Result[c];
            double ema = _ema.Result[c];
            double rsiNow = _rsi.Result[c];
            double rsiPrev = _rsi.Result[c - RsiSlopeBars];
            double close = _sig.ClosePrices[c];
            if (!Valid(atrS) || atrS <= 0 || !Valid(ema) || !Valid(rsiNow) || !Valid(rsiPrev))
                return null;

            DateTime closeTime = _sig.OpenTimes[c].AddSeconds(_sigSec);
            int r = LastIndexBefore(_risk, closeTime);
            if (r < 0 || r > _risk.Count - 2)
                return null;
            double atrR = _atrRisk.Result[r];
            if (!Valid(atrR) || atrR <= 0)
                return null;

            // History has no intrabar tick timing, so the classifier's velocity feature is the bar's tick
            // volume against the average of the previous N bars. Live, the same quantity is measured over a
            // rolling window of one bar length (see BarTickVelocity), keeping training and inference aligned.
            double baseline = MeanTickVolume(_sig, c - 1, TickBaselineBars);
            if (baseline <= 0)
                return null;
            double tv = _sig.TickVolumes[c] / baseline;

            var s = new PendingSample();
            s.CloseTime = closeTime;
            s.EntryBid = close;
            s.Spread = spread;
            s.AtrRisk = atrR;
            s.LongX = AiFeatures.Build(tv, close - ema, atrS, rsiNow - rsiPrev, spread / atrR, 1);
            s.ShortX = AiFeatures.Build(tv, close - ema, atrS, rsiNow - rsiPrev, spread / atrR, -1);
            return s;
        }

        /// <summary>Labels the sample once enough ATR-timeframe bars have closed. True when it is finished with.</summary>
        private bool TryResolveSample(PendingSample s)
        {
            if (_risk.Count < 2 || _risk.OpenTimes[0] > s.CloseTime)
                return true;
            int j0 = FirstIndexAtOrAfter(_risk, s.CloseTime);
            if (j0 < 0)
                return false;
            int jEnd = j0 + AiHorizonBars - 1;
            if (jEnd > _risk.Count - 2)
                return false;

            for (int k = 0; k < AiHorizonBars; k++)
            {
                _hBuf[k] = _risk.HighPrices[j0 + k];
                _lBuf[k] = _risk.LowPrices[j0 + k];
            }
            double sl = SlAtrMultiplier * s.AtrRisk;
            double tp = Tp1AtrMultiplier * s.AtrRisk;
            double beTrigger = BeTriggerR > 0 ? BeTriggerR * sl : 0.0;
            double beOffset = BeOffsetPips * Symbol.PipSize;
            bool longWin = OutcomeSimulator.Tp1First(true, s.EntryBid, s.Spread, sl, tp, beTrigger, beOffset, _hBuf, _lBuf, AiHorizonBars);
            bool shortWin = OutcomeSimulator.Tp1First(false, s.EntryBid, s.Spread, sl, tp, beTrigger, beOffset, _hBuf, _lBuf, AiHorizonBars);
            _ai.Add(s.LongX, longWin);
            _ai.Add(s.ShortX, shortWin);
            return true;
        }

        private void ResolvePending()
        {
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                if (TryResolveSample(_pending[i]))
                    _pending.RemoveAt(i);
            }
            bool ready = _ai.IsReady(AiMinSamplesPerClass);
            if (ready != _aiWasReady)
            {
                _aiWasReady = ready;
                LogAiState();
            }
        }

        #endregion

        #region Bar processing and setups

        private void ProcessClosedBars()
        {
            int rLast = _risk.Count - 2;
            if (rLast >= 0 && _risk.OpenTimes[rLast] != _lastRiskClosed)
            {
                _lastRiskClosed = _risk.OpenTimes[rLast];
                ResolvePending();
            }

            int sLast = _sig.Count - 2;
            if (sLast >= 0 && _sig.OpenTimes[sLast] != _lastSigClosed)
            {
                // Normally exactly one bar closed; after a reconnect several may have, and each feeds the AI.
                int from = sLast;
                int prev = IndexOfTime(_sig, _lastSigClosed);
                if (prev >= 0 && prev < sLast)
                    from = prev + 1;
                for (int c = from; c <= sLast; c++)
                    OnSignalBarClosed(c, c == sLast);
                _lastSigClosed = _sig.OpenTimes[sLast];
            }
        }

        private void OnSignalBarClosed(int c, bool latest)
        {
            PendingSample s = BuildSample(c, TrainingSpread());
            if (s != null && !TryResolveSample(s))
                _pending.Add(s);
            if (!latest)
                return;

            _skipKeys.Clear();
            _baselineTicksPerBar = MeanTickVolume(_sig, c, TickBaselineBars);
            EvaluateSetups(c);
            if (LogBarSummary)
                LogBar(c, s);
            DrawSetups();
        }

        /// <summary>Arms or disarms the squeeze and sweep setups from the bar that just closed.</summary>
        private void EvaluateSetups(int c)
        {
            double pip = Symbol.PipSize;
            double atrS = _atrSig.Result[c];
            double ema = _ema.Result[c];
            double buffer = BreakoutBufferPips * pip;
            _setupAtr = atrS;
            _reclaimEma = ema;
            _sqzArmed = false;

            if (!Valid(atrS) || atrS <= 0 || !Valid(ema))
            {
                _bullSweep = false;
                _bearSweep = false;
                _sqzNote = "indicators warming up";
                _swpIdle = "indicators warming up";
                _swpNote = _swpIdle;
                return;
            }

            // ---- 20 EMA squeeze -------------------------------------------------------------------------
            if (!UseSqueeze)
            {
                _sqzNote = "off";
            }
            else if (c - SqueezeBars + 1 < 0)
            {
                _sqzNote = "not enough bars";
            }
            else
            {
                int start = c - SqueezeBars + 1;
                for (int i = 0; i < SqueezeBars; i++)
                {
                    _winH[i] = _sig.HighPrices[start + i];
                    _winL[i] = _sig.LowPrices[start + i];
                }
                SqueezeCheck q = VolmanSetups.CheckSqueeze(_winH, _winL, SqueezeBars, ema, atrS, SqueezeMaxRangeAtr, SqueezeMaxEmaGapAtr);
                if (q.Ok)
                {
                    _sqzArmed = true;
                    _sqzHigh = q.BoxHigh;
                    _sqzLow = q.BoxLow;
                    _sqzStart = _sig.OpenTimes[start];
                    _sqzNote = "ARMED " + P(q.BoxLow) + "-" + P(q.BoxHigh) + " (" + Pips(q.Height) + "), BUY>=" + P(q.BoxHigh + buffer)
                               + " SELL<=" + P(q.BoxLow - buffer);
                }
                else if (!q.RangeOk)
                {
                    _sqzNote = "no: box " + Pips(q.Height) + " > max " + Pips(q.MaxHeight) + " (" + F(SqueezeMaxRangeAtr, 2) + " ATR)";
                }
                else
                {
                    _sqzNote = "no: EMA " + Pips(q.EmaGap) + " from box > max " + Pips(q.MaxGap) + " (" + F(SqueezeMaxEmaGapAtr, 2) + " ATR)";
                }
            }

            // ---- Liquidity sweep ------------------------------------------------------------------------
            if (!UseSweep)
            {
                _bullSweep = false;
                _bearSweep = false;
                _swpIdle = "off";
            }
            else if (c - SweepLookbackBars < 0)
            {
                _swpIdle = "not enough bars";
            }
            else
            {
                double priorLow = double.MaxValue;
                double priorHigh = double.MinValue;
                for (int i = c - SweepLookbackBars; i < c; i++)
                {
                    priorLow = Math.Min(priorLow, _sig.LowPrices[i]);
                    priorHigh = Math.Max(priorHigh, _sig.HighPrices[i]);
                }
                double lo = _sig.LowPrices[c];
                double hi = _sig.HighPrices[c];
                double cl = _sig.ClosePrices[c];
                double minPierce = SweepMinPiercePips * pip;

                if (_bullSweep)
                {
                    if (lo < _bullExtreme)
                    {
                        _bullSweep = false;
                    }
                    else if (--_bullBarsLeft <= 0)
                    {
                        _bullSweep = false;
                        Log("SWEEP BULL expired: no reclaim of the EMA within " + SweepMaxBarsToReclaim + " bars");
                    }
                }
                if (_bearSweep)
                {
                    if (hi > _bearExtreme)
                    {
                        _bearSweep = false;
                    }
                    else if (--_bearBarsLeft <= 0)
                    {
                        _bearSweep = false;
                        Log("SWEEP BEAR expired: no loss of the EMA within " + SweepMaxBarsToReclaim + " bars");
                    }
                }

                if (SweepDetector.IsBullSweep(priorLow, lo, cl, minPierce, SweepRequireCloseInside))
                {
                    _bullSweep = true;
                    _bullExtreme = lo;
                    _bullBarsLeft = SweepMaxBarsToReclaim;
                    _bullTime = _sig.OpenTimes[c];
                    Log("SWEEP BULL: " + SweepLookbackBars + "-bar low " + P(priorLow) + " pierced to " + P(lo) + " (" + Pips(priorLow - lo)
                        + "), close " + P(cl) + " -> BUY on reclaim of EMA >= " + P(ema + buffer) + " within " + SweepMaxBarsToReclaim + " bars");
                }
                if (SweepDetector.IsBearSweep(priorHigh, hi, cl, minPierce, SweepRequireCloseInside))
                {
                    _bearSweep = true;
                    _bearExtreme = hi;
                    _bearBarsLeft = SweepMaxBarsToReclaim;
                    _bearTime = _sig.OpenTimes[c];
                    Log("SWEEP BEAR: " + SweepLookbackBars + "-bar high " + P(priorHigh) + " pierced to " + P(hi) + " (" + Pips(hi - priorHigh)
                        + "), close " + P(cl) + " -> SELL on loss of EMA <= " + P(ema - buffer) + " within " + SweepMaxBarsToReclaim + " bars");
                }

                bool piercedLow = lo < priorLow;
                bool piercedHigh = hi > priorHigh;
                double pierce = Math.Max(piercedLow ? priorLow - lo : 0.0, piercedHigh ? hi - priorHigh : 0.0);
                if (!piercedLow && !piercedHigh)
                    _swpIdle = "no: " + SweepLookbackBars + "-bar H " + P(priorHigh) + " / L " + P(priorLow) + " not pierced";
                else if (pierce < minPierce)
                    _swpIdle = "no: pierce " + Pips(pierce) + " < min " + F(SweepMinPiercePips, 1) + "p";
                else
                    _swpIdle = "no: pierce " + Pips(pierce) + " but the bar closed beyond the swept level";
            }
            _swpNote = SweepNote();
        }

        private string SweepNote()
        {
            if (!UseSweep)
                return "off";
            double buffer = BreakoutBufferPips * Symbol.PipSize;
            var parts = new List<string>(2);
            if (_bullSweep)
                parts.Add("BULL armed, wick " + P(_bullExtreme) + ", BUY>=" + P(_reclaimEma + buffer) + " (" + _bullBarsLeft + " bars left)");
            if (_bearSweep)
                parts.Add("BEAR armed, wick " + P(_bearExtreme) + ", SELL<=" + P(_reclaimEma - buffer) + " (" + _bearBarsLeft + " bars left)");
            return parts.Count > 0 ? string.Join(" & ", parts) : _swpIdle;
        }

        private void LogBar(int c, PendingSample s)
        {
            double atrR = RiskAtr();
            double spread = Symbol.Spread;
            bool armed = _sqzArmed || _bullSweep || _bearSweep;
            var sb = new StringBuilder(256);
            sb.Append("BAR ").Append(_sig.OpenTimes[c].ToString("HH:mm", Inv)).Append(armed ? " ARMED" : " NO SETUP");
            sb.Append(" | C ").Append(P(_sig.ClosePrices[c])).Append(" EMA ").Append(P(_ema.Result[c]));
            sb.Append(" ATR ").Append(Pips(atrR));
            if (!_sameTf)
                sb.Append(" (sig ").Append(Pips(_atrSig.Result[c])).Append(')');
            sb.Append(" | Spread ").Append(Pips(spread)).Append(" = ").Append(atrR > 0 ? Pct(spread / atrR) : "n/a")
              .Append(" ATR (max ").Append(Pct(MaxSpreadToAtr)).Append(')');
            if (s != null)
                sb.Append(" | TV bar ").Append(F(Math.Exp(s.LongX[0]), 2)).Append('x');
            sb.Append(" | SQZ ").Append(_sqzNote).Append(" | SWP ").Append(_swpNote);
            if (s != null)
            {
                if (_ai.IsReady(AiMinSamplesPerClass))
                {
                    sb.Append(" | AI L ").Append(Pct(_ai.Predict(s.LongX, AiUseEmpiricalPrior, null)))
                      .Append(" S ").Append(Pct(_ai.Predict(s.ShortX, AiUseEmpiricalPrior, null)));
                }
                else
                {
                    sb.Append(" | AI warming ").Append(_ai.Wins).Append('/').Append(_ai.Losses);
                }
            }
            Log(sb.ToString());
        }

        #endregion

        #region Entries

        private void EvaluateEntries()
        {
            if (!_sqzArmed && !_bullSweep && !_bearSweep)
                return;
            int f = _sig.Count - 1;
            if (f < 1)
                return;

            double bid = Symbol.Bid;
            double buffer = BreakoutBufferPips * Symbol.PipSize;

            // A new extreme beyond the swept wick means the pierce was not false after all.
            if (_bullSweep && _sig.LowPrices[f] < _bullExtreme)
            {
                _bullSweep = false;
                Log("SWEEP BULL cancelled: new low " + P(_sig.LowPrices[f]) + " below the swept wick " + P(_bullExtreme));
                _swpNote = SweepNote();
            }
            if (_bearSweep && _sig.HighPrices[f] > _bearExtreme)
            {
                _bearSweep = false;
                Log("SWEEP BEAR cancelled: new high " + P(_sig.HighPrices[f]) + " above the swept wick " + P(_bearExtreme));
                _swpNote = SweepNote();
            }

            if (_sqzArmed)
            {
                double up = _sqzHigh + buffer;
                double dn = _sqzLow - buffer;
                if (bid >= up)
                {
                    if (ConsiderEntry("SQZ", TradeType.Buy, up, bid - up))
                        return;
                }
                else if (bid <= dn)
                {
                    if (ConsiderEntry("SQZ", TradeType.Sell, dn, dn - bid))
                        return;
                }
            }
            if (_bullSweep)
            {
                double level = _reclaimEma + buffer;
                if (bid >= level && ConsiderEntry("SWP", TradeType.Buy, level, bid - level))
                    return;
            }
            if (_bearSweep)
            {
                double level = _reclaimEma - buffer;
                if (bid <= level && ConsiderEntry("SWP", TradeType.Sell, level, level - bid))
                    return;
            }
        }

        /// <summary>Runs every filter on a triggered setup, logs all failing reasons, or opens the trade.</summary>
        private bool ConsiderEntry(string setup, TradeType type, double level, double chase)
        {
            int dir = type == TradeType.Buy ? 1 : -1;
            string tag = setup + " " + (dir > 0 ? "BUY" : "SELL");
            var reasons = new List<string>(8);
            var codes = new StringBuilder(32);

            AddGlobalGates(reasons, codes);

            double atrR = RiskAtr();
            double maxChase = MaxChaseAtr * _setupAtr;
            if (chase > maxChase)
                AddReason(reasons, codes, "CHASE", "Price already " + Pips(chase) + " past trigger > max " + Pips(maxChase) + " (" + F(MaxChaseAtr, 2) + " ATR)");

            double spread = Symbol.Spread;
            double spreadRatio = atrR > 0 ? spread / atrR : double.PositiveInfinity;
            if (!(spreadRatio <= MaxSpreadToAtr))
            {
                AddReason(reasons, codes, "SPREAD", "Spread too high (" + Pips(spread) + " = " + (atrR > 0 ? Pct(spreadRatio) : "n/a")
                          + " of ATR " + Pips(atrR) + " > " + Pct(MaxSpreadToAtr) + ")");
            }

            double burst = BurstRatio();
            if (UseTickVelocity && burst < TickVelocityMultiplier)
            {
                AddReason(reasons, codes, "TV", "Low Tick Velocity (" + F(burst, 2) + "x < " + F(TickVelocityMultiplier, 2) + "x in "
                          + F(TickWindowSeconds, 1) + "s)");
            }

            double conf = double.NaN;
            string aiInfo = "";
            if (UseAiFilter)
            {
                if (!_ai.IsReady(AiMinSamplesPerClass))
                {
                    AddReason(reasons, codes, "AIWARM", "AI warming up (wins " + _ai.Wins + "/" + AiMinSamplesPerClass + ", losses "
                              + _ai.Losses + "/" + AiMinSamplesPerClass + ")");
                }
                else
                {
                    double[] x = LiveFeatures(dir);
                    if (x == null)
                    {
                        AddReason(reasons, codes, "AINA", "AI features unavailable (indicators warming up)");
                    }
                    else
                    {
                        conf = _ai.Predict(x, AiUseEmpiricalPrior, _contrib);
                        _lastConf = conf;
                        aiInfo = FormatContributions(_contrib);
                        if (!(conf >= MinConfidence))
                            AddReason(reasons, codes, "AI", "AI Confidence " + Pct(conf) + " < Target " + Pct(MinConfidence) + " " + aiInfo);
                    }
                }
            }

            if (reasons.Count > 0)
            {
                LogSkip(tag, level, codes.ToString(), reasons);
                return false;
            }
            return OpenTrade(setup, type, level, conf, burst, aiInfo);
        }

        private void AddGlobalGates(List<string> reasons, StringBuilder codes)
        {
            DateTime now = Server.Time;
            if (_dailyHalt)
                AddReason(reasons, codes, "DAY", "Daily loss limit " + F(DailyMaxLossPercent, 1) + "% reached - halted until 00:00 UTC");
            if (now < _pauseUntil)
                AddReason(reasons, codes, "STREAK", "Loss-streak pause until " + _pauseUntil.ToString("HH:mm", Inv) + " UTC");
            if (MaxTradesPerDay > 0 && _tradesToday >= MaxTradesPerDay)
                AddReason(reasons, codes, "MAXTR", "Max trades per day reached (" + MaxTradesPerDay + ")");
            if (CooldownSeconds > 0 && _lastCloseTime != DateTime.MinValue)
            {
                double since = (now - _lastCloseTime).TotalSeconds;
                if (since < CooldownSeconds)
                    AddReason(reasons, codes, "COOL", "Cooldown after close (" + F(CooldownSeconds - since, 0) + "s left)");
            }
            if (!InSession(now))
            {
                AddReason(reasons, codes, "SESSION", "Outside session " + SessionStartHour.ToString("00", Inv) + ":00-"
                          + SessionEndHour.ToString("00", Inv) + ":00 UTC");
            }
            if (!Symbol.MarketHours.IsOpened())
                AddReason(reasons, codes, "CLOSED", "Market closed");
            if (Positions.FindAll(BotLabel, SymbolName).Length > 0)
                AddReason(reasons, codes, "POS", "Position already open");
            double need = RequiredWarmupSeconds();
            double ran = (now - _engineStart).TotalSeconds;
            if (ran < need)
                AddReason(reasons, codes, "WARM", "Tick engine warming up (" + F(ran, 0) + "/" + F(need, 0) + " s)");
        }

        private double RequiredWarmupSeconds()
        {
            if (UseAiFilter)
                return Math.Max(TickWindowSeconds, _sigSec);
            return UseTickVelocity ? TickWindowSeconds : 0.0;
        }

        private static void AddReason(List<string> reasons, StringBuilder codes, string code, string text)
        {
            reasons.Add(text);
            codes.Append(code).Append(',');
        }

        private void LogSkip(string tag, double level, string codes, List<string> reasons)
        {
            _lastSkip = "SKIP [" + tag + "] " + string.Join(" | ", reasons);
            if (!LogSkipReasons)
                return;
            // One line per setup, direction and combination of reasons per bar: detailed, never a flood.
            if (!_skipKeys.Add(tag + "|" + codes))
                return;
            Log("SKIP [" + tag + " @" + P(level) + "]: " + string.Join(" | ", reasons));
        }

        private bool OpenTrade(string setup, TradeType type, double level, double conf, double burst, string aiInfo)
        {
            string tag = setup + " " + (type == TradeType.Buy ? "BUY" : "SELL");
            double pip = Symbol.PipSize;
            double atrR = RiskAtr();
            double slPips = Math.Round(SlAtrMultiplier * atrR / pip, 1);
            double tp1Pips = Math.Round(Tp1AtrMultiplier * atrR / pip, 1);
            if (slPips <= 0 || tp1Pips <= 0)
            {
                LogSkip(tag, level, "DIST", new List<string> { "SL/TP distance rounds to zero (ATR " + Pips(atrR) + ")" });
                return false;
            }

            string sizing;
            double volume = ComputeVolume(type, slPips, out sizing);
            if (volume <= 0)
            {
                LogSkip(tag, level, "SIZE", new List<string> { sizing });
                return false;
            }

            TradeResult result = null;
            SplitPlan plan = new SplitPlan();
            bool closeAllAtTp1 = false;
            for (int attempt = 0; ; attempt++)
            {
                plan = VolumeMath.PlanSplit(volume, Tp1ClosePercent, Symbol.VolumeInUnitsMin, Symbol.VolumeInUnitsStep);
                closeAllAtTp1 = !plan.Splittable && (Tp1ClosePercent >= 100.0 || NoSplitMode == NoSplitAction.CloseAllAtTp1);
                double? tpPips = null;
                if (closeAllAtTp1)
                    tpPips = tp1Pips;
                else if (Tp2AtrMultiplier > 0)
                    tpPips = Math.Round(Tp2AtrMultiplier * atrR / pip, 1);

                string comment = "QAI|" + setup + "|c=" + (double.IsNaN(conf) ? "na" : F(conf, 2)) + "|v=" + F(volume, 0)
                                 + "|s=" + F(slPips, 1) + "|t=" + F(tp1Pips, 1);
                result = SendMarketOrder(type, volume, slPips, tpPips, comment);
                if (result.IsSuccessful || result.Error != ErrorCode.NoMoney || attempt >= MaxNoMoneyRetries)
                    break;
                double smaller = VolumeMath.FloorToStep(volume / 2.0, Symbol.VolumeInUnitsStep);
                if (smaller < Symbol.VolumeInUnitsMin - VolumeEpsilon)
                    break;
                Log("ORDER [" + tag + "]: not enough money for " + Lots(volume) + " -> retry with " + Lots(smaller));
                volume = smaller;
            }

            if (result == null || !result.IsSuccessful || result.Position == null)
            {
                Log("ORDER FAILED [" + tag + "]: " + (result != null && result.Error.HasValue ? result.Error.Value.ToString() : "no position returned")
                    + " | " + sizing);
                return false;
            }

            Position p = result.Position;
            var st = new TradeState();
            st.PositionId = p.Id;
            st.Setup = setup;
            st.IsLong = p.TradeType == TradeType.Buy;
            st.EntryTime = p.EntryTime;
            st.RiskDist = p.StopLoss.HasValue ? Math.Abs(p.EntryPrice - p.StopLoss.Value) : slPips * pip;
            st.Tp1Dist = tp1Pips * pip;
            st.InitialVolume = p.VolumeInUnits;
            st.Splittable = plan.Splittable;
            st.PartialVolume = plan.PartialVolume;
            st.CloseAllAtTp1 = closeAllAtTp1;
            st.Confidence = conf;
            _trades[p.Id] = st;
            _tradesToday++;
            ConsumeSetup(setup, type);

            // Never keep a position without a stop: attach it if the fill came back without one, else flatten.
            if (!p.StopLoss.HasValue)
            {
                double slPrice = Math.Round(st.IsLong ? p.EntryPrice - slPips * pip : p.EntryPrice + slPips * pip, Symbol.Digits);
                TradeResult fix = p.ModifyStopLossPrice(slPrice);
                if (!fix.IsSuccessful)
                {
                    st.CloseReason = "NO STOP";
                    Log("PROTECTION #" + p.Id + ": stop loss could not be attached (" + fix.Error + ") -> closing the position");
                    ClosePosition(p);
                    return true;
                }
                st.RiskDist = Math.Abs(p.EntryPrice - slPrice);
                Log("PROTECTION #" + p.Id + ": fill came back without a stop -> SL attached at " + P(slPrice));
            }

            string tp1Text = plan.Splittable
                ? Lots(plan.PartialVolume) + " (" + F(plan.PartialVolume / st.InitialVolume * 100.0, 0) + "%), runner " + Lots(plan.RemainingVolume)
                : (closeAllAtTp1 ? "100% (volume cannot be split)" : "none closed - whole position trails (volume cannot be split)");
            Log("ENTRY " + tag + " #" + p.Id + " " + Lots(p.VolumeInUnits) + " @" + P(p.EntryPrice) + " | SL " + P(p.StopLoss) + " (" + F(slPips, 1)
                + "p = " + F(SlAtrMultiplier, 2) + " ATR) | TP1 " + P(TargetPrice(p, st.Tp1Dist)) + " (" + F(tp1Pips, 1) + "p) closes " + tp1Text
                + " | conf " + (double.IsNaN(conf) ? "n/a (AI off)" : Pct(conf) + " " + aiInfo) + " | TV " + F(burst, 2) + "x | " + sizing);
            DrawEntry(p);
            return true;
        }

        private TradeResult SendMarketOrder(TradeType type, double volume, double slPips, double? tpPips, string comment)
        {
            if (MaxSlippagePips > 0)
            {
                double basePrice = type == TradeType.Buy ? Symbol.Ask : Symbol.Bid;
                return ExecuteMarketRangeOrder(type, SymbolName, volume, MaxSlippagePips, basePrice, BotLabel, slPips, tpPips, comment);
            }
            return ExecuteMarketOrder(type, SymbolName, volume, BotLabel, slPips, tpPips, comment);
        }

        private void ConsumeSetup(string setup, TradeType type)
        {
            if (setup == "SQZ")
            {
                _sqzArmed = false;
                _sqzNote = "used";
            }
            else if (type == TradeType.Buy)
            {
                _bullSweep = false;
            }
            else
            {
                _bearSweep = false;
            }
            _swpNote = SweepNote();
        }

        /// <summary>Risk-based volume in units, bounded by the lot floor/cap and by free margin.</summary>
        private double ComputeVolume(TradeType type, double slPips, out string note)
        {
            double balance = Account.Balance;
            double pipValue = Symbol.PipValue;   // account currency per pip for one unit
            double step = Symbol.VolumeInUnitsStep;
            double vMin = Symbol.VolumeInUnitsMin;
            if (pipValue <= 0 || slPips <= 0 || balance <= 0 || step <= 0)
            {
                note = "Sizing impossible (balance " + F(balance, 2) + ", pip value " + pipValue.ToString("G6", Inv) + ")";
                return 0.0;
            }

            double riskMoney = balance * RiskPercent / 100.0;
            double raw = riskMoney / (slPips * pipValue);
            var sb = new StringBuilder(160);
            sb.Append("size: risk ").Append(F(riskMoney, 2)).Append(' ').Append(_ccy).Append(" / (").Append(F(slPips, 1)).Append("p x ")
              .Append(F(pipValue * Symbol.LotSize, 2)).Append(' ').Append(_ccy).Append("/pip/lot) = ").Append(F(raw, 0)).Append('u');

            double units = VolumeMath.FloorToStep(raw, step);
            double cap = Math.Min(Symbol.VolumeInUnitsMax, Symbol.QuantityToVolumeInUnits(MaxLots));
            if (units > cap + VolumeEpsilon)
            {
                units = VolumeMath.FloorToStep(cap, step);
                sb.Append(" -> Max Lots cap");
            }
            double floor = Math.Max(vMin, Symbol.QuantityToVolumeInUnits(MinLots));
            if (units < floor - VolumeEpsilon)
            {
                if (!AllowMinLotOverride)
                {
                    note = sb.Append(" -> below Min Lots ").Append(Lots(floor)).Append(" and min-lot override is OFF").ToString();
                    return 0.0;
                }
                units = VolumeMath.CeilToStep(floor, step);
                sb.Append(" -> Min Lots floor");
            }

            double leverage = EffectiveLeverage();
            if (leverage > 0)
            {
                double price = type == TradeType.Buy ? Symbol.Ask : Symbol.Bid;
                double marginPerUnit = price * (pipValue / Symbol.PipSize) / leverage;
                double budget = Account.FreeMargin * MaxMarginUsePercent / 100.0;
                if (marginPerUnit > 0 && units * marginPerUnit > budget + VolumeEpsilon)
                {
                    double byMargin = VolumeMath.FloorToStep(budget / marginPerUnit, step);
                    if (byMargin < vMin - VolumeEpsilon)
                    {
                        note = sb.Append(" -> Insufficient margin: ").Append(Lots(vMin)).Append(" needs ").Append(F(vMin * marginPerUnit, 2))
                                 .Append(' ').Append(_ccy).Append(", budget ").Append(F(budget, 2)).Append(' ').Append(_ccy).Append(" (")
                                 .Append(F(MaxMarginUsePercent, 0)).Append("% of free margin, leverage 1:").Append(F(leverage, 0)).Append(')').ToString();
                        return 0.0;
                    }
                    units = byMargin;
                    sb.Append(" -> margin cap (1:").Append(F(leverage, 0)).Append(')');
                }
            }

            double effectiveRisk = units * slPips * pipValue;
            sb.Append(" = ").Append(Lots(units)).Append(", risk ").Append(F(effectiveRisk, 2)).Append(' ').Append(_ccy)
              .Append(" (").Append(F(effectiveRisk / balance * 100.0, 2)).Append("%)");
            note = sb.ToString();
            return units;
        }

        private double EffectiveLeverage()
        {
            if (LeverageOverride > 0)
                return LeverageOverride;
            double leverage = Account.PreciseLeverage;
            double symbolLeverage = SymbolLeverage();
            if (symbolLeverage > 0 && (leverage <= 0 || symbolLeverage < leverage))
                leverage = symbolLeverage;
            return leverage;
        }

        /// <summary>Leverage of the smallest dynamic-leverage tier, the one that applies to micro positions.</summary>
        private double SymbolLeverage()
        {
            try
            {
                var tiers = Symbol.DynamicLeverage;
                if (tiers == null)
                    return 0.0;
                double smallestVolume = double.MaxValue;
                double leverage = 0.0;
                for (int i = 0; i < tiers.Count; i++)
                {
                    LeverageTier tier = tiers[i];
                    if (tier != null && tier.Leverage > 0 && tier.Volume < smallestVolume)
                    {
                        smallestVolume = tier.Volume;
                        leverage = tier.Leverage;
                    }
                }
                return leverage;
            }
            catch (Exception)
            {
                return 0.0;
            }
        }

        private double[] LiveFeatures(int dir)
        {
            int f = _sig.Count - 1;
            if (f < 1 || f - RsiSlopeBars < 0)
                return null;
            double atrS = _atrSig.Result[f - 1];
            double ema = _ema.Result[f];
            double rsiNow = _rsi.Result[f];
            double rsiPrev = _rsi.Result[f - RsiSlopeBars];
            double atrR = RiskAtr();
            if (!Valid(atrS) || atrS <= 0 || !Valid(ema) || !Valid(rsiNow) || !Valid(rsiPrev) || atrR <= 0)
                return null;
            return AiFeatures.Build(BarTickVelocity(), Symbol.Bid - ema, atrS, rsiNow - rsiPrev, Symbol.Spread / atrR, dir);
        }

        #endregion

        #region Position management

        private void ManagePositions()
        {
            Position[] mine = Positions.FindAll(BotLabel, SymbolName);
            if (mine.Length == 0)
                return;
            // Requests sent into a closed market only fail; server-side stops keep protecting the position.
            if (!Symbol.MarketHours.IsOpened())
                return;
            DateTime now = Server.Time;

            foreach (Position p in mine)
            {
                TradeState st = GetOrRecoverState(p);
                if (now < st.RetryAfter)
                    continue;

                bool isLong = p.TradeType == TradeType.Buy;
                double px = isLong ? Symbol.Bid : Symbol.Ask;
                double fav = isLong ? px - p.EntryPrice : p.EntryPrice - px;
                if (fav > st.MaxFavorable)
                    st.MaxFavorable = fav;

                // 1. TP1: partial close (or full close / hand-over to the trail when the volume cannot be split).
                if (!st.Tp1Done && fav >= st.Tp1Dist - Symbol.TickSize * 0.5)
                {
                    if (HandleTp1(p, st, px, fav))
                        continue;
                }

                // 2. Time exit: the market did not deliver TP1 within N bars of the ATR timeframe.
                if (TimeExitBars > 0 && !st.Tp1Done && (now - st.EntryTime).TotalSeconds >= TimeExitBars * _riskSec)
                {
                    st.CloseReason = "TIME EXIT";
                    Log("TIME EXIT #" + p.Id + ": TP1 not reached within " + TimeExitBars + " " + RiskTimeFrame.ShortName
                        + " bars, closing at market (" + SignedPips(fav) + ", best " + SignedPips(Math.Max(st.MaxFavorable, fav)) + ")");
                    TradeResult r = ClosePosition(p);
                    if (r.IsSuccessful)
                    {
                        _statTimeExits++;
                    }
                    else
                    {
                        st.CloseReason = null;
                        Backoff(st, now);
                        Log("TIME EXIT failed #" + p.Id + ": " + r.Error);
                    }
                    continue;
                }

                // 3. Micro break-even.
                if (BeTriggerR > 0 && !st.BeDone && fav >= BeTriggerR * st.RiskDist)
                    MoveToBreakeven(p, st, px, "+" + F(BeTriggerR, 2) + "R");

                // 4. Ultra-short ATR trailing stop.
                if (TrailAtrMultiplier > 0 && (st.Tp1Done || !TrailOnlyAfterTp1))
                    Trail(p, st, px);
            }
        }

        /// <summary>Returns true when the position was closed completely.</summary>
        private bool HandleTp1(Position p, TradeState st, double px, double fav)
        {
            DateTime now = Server.Time;
            if (st.Splittable)
            {
                double part = st.PartialVolume;
                if (p.VolumeInUnits - part < Symbol.VolumeInUnitsMin - VolumeEpsilon)
                {
                    st.Splittable = false;   // volume was changed outside the bot
                }
                else
                {
                    TradeResult r = ClosePosition(p, part);
                    if (r.IsSuccessful)
                    {
                        st.Tp1Done = true;
                        st.Failures = 0;
                        _statTp1++;
                        Log("TP1 #" + p.Id + " " + SignedPips(fav) + ": closed " + Lots(part) + " (" + F(part / st.InitialVolume * 100.0, 0)
                            + "%), runner " + Lots(p.VolumeInUnits)
                            + (TrailAtrMultiplier > 0 ? " trails " + F(TrailAtrMultiplier, 2) + " x ATR" : " keeps its stop"));
                        if (BeTriggerR > 0 && !st.BeDone)
                            MoveToBreakeven(p, st, px, "TP1");
                        return false;
                    }
                    Log("TP1 partial close failed #" + p.Id + ": " + r.Error);
                    if (r.Error == ErrorCode.BadVolume)
                    {
                        st.Splittable = false;
                    }
                    else
                    {
                        Backoff(st, now);
                        return false;
                    }
                }
            }

            if (st.CloseAllAtTp1 || (!st.Splittable && Tp1ClosePercent >= 100.0))
            {
                // The order carries a server-side TP at TP1; this is only the backstop if it is missing.
                if (p.TakeProfit.HasValue)
                    return false;
                st.CloseReason = "TP1 (100%)";
                Log("TP1 #" + p.Id + " " + SignedPips(fav) + ": " + Lots(p.VolumeInUnits) + " cannot be split -> closing 100%");
                TradeResult r = ClosePosition(p);
                if (r.IsSuccessful)
                {
                    _statTp1++;
                    return true;
                }
                st.CloseReason = null;
                Backoff(st, now);
                Log("TP1 close failed #" + p.Id + ": " + r.Error);
                return false;
            }

            st.Tp1Done = true;
            _statTp1++;
            Log("TP1 #" + p.Id + " " + SignedPips(fav) + ": " + Lots(p.VolumeInUnits) + " cannot be split -> whole position "
                + (TrailAtrMultiplier > 0 ? "trails " + F(TrailAtrMultiplier, 2) + " x ATR" : "keeps its stop"));
            if (BeTriggerR > 0 && !st.BeDone)
                MoveToBreakeven(p, st, px, "TP1");
            return false;
        }

        private void MoveToBreakeven(Position p, TradeState st, double px, string why)
        {
            bool isLong = p.TradeType == TradeType.Buy;
            double target = Math.Round(BreakevenPrice(p), Symbol.Digits);
            if (p.StopLoss.HasValue && (isLong ? p.StopLoss.Value >= target : p.StopLoss.Value <= target))
            {
                st.BeDone = true;
                return;
            }
            // A stop has to stay on the losing side of the price it is triggered by.
            if (isLong ? target >= px : target <= px)
            {
                if (!st.BeBlockedLogged)
                {
                    Log("MICRO-BE #" + p.Id + " waiting: break-even " + P(target) + " is not yet on the safe side of " + P(px));
                    st.BeBlockedLogged = true;
                }
                return;
            }
            TradeResult r = p.ModifyStopLossPrice(target);
            if (r.IsSuccessful)
            {
                st.BeDone = true;
                st.Failures = 0;
                Log("MICRO-BE #" + p.Id + " (" + why + "): SL -> " + P(target) + " (entry " + (isLong ? "+" : "-") + F(BeOffsetPips, 1) + "p)");
            }
            else
            {
                Backoff(st, Server.Time);
                Log("MICRO-BE failed #" + p.Id + ": " + r.Error);
            }
        }

        private void Trail(Position p, TradeState st, double px)
        {
            double atr = RiskAtr();
            if (atr <= 0)
                return;
            bool isLong = p.TradeType == TradeType.Buy;
            double dist = TrailAtrMultiplier * atr;
            double candidate = isLong ? px - dist : px + dist;
            if (st.BeDone)
            {
                double be = BreakevenPrice(p);
                candidate = isLong ? Math.Max(candidate, be) : Math.Min(candidate, be);
            }
            candidate = Math.Round(candidate, Symbol.Digits);
            if (isLong ? candidate >= px : candidate <= px)
                return;

            double step = Math.Max(TrailStepPips * Symbol.PipSize, Symbol.TickSize);
            if (p.StopLoss.HasValue)
            {
                double improvement = isLong ? candidate - p.StopLoss.Value : p.StopLoss.Value - candidate;
                if (improvement < step - Symbol.TickSize * 0.01)
                    return;
            }
            TradeResult r = p.ModifyStopLossPrice(candidate);
            if (r.IsSuccessful)
            {
                st.Failures = 0;
                Log("TRAIL #" + p.Id + ": SL -> " + P(candidate) + " (" + F(TrailAtrMultiplier, 2) + " x ATR = " + Pips(dist) + ", locked "
                    + SignedPips(isLong ? candidate - p.EntryPrice : p.EntryPrice - candidate) + ")");
            }
            else
            {
                Backoff(st, Server.Time);
                Log("TRAIL failed #" + p.Id + ": " + r.Error);
            }
        }

        private static void Backoff(TradeState st, DateTime now)
        {
            st.Failures++;
            double delay = Math.Min(MaxRetryDelaySeconds, RetryDelaySeconds * Math.Pow(2.0, st.Failures - 1));
            st.RetryAfter = now.AddSeconds(delay);
        }

        private double BreakevenPrice(Position p)
        {
            double offset = BeOffsetPips * Symbol.PipSize;
            return p.TradeType == TradeType.Buy ? p.EntryPrice + offset : p.EntryPrice - offset;
        }

        private double TargetPrice(Position p, double distance)
        {
            return p.TradeType == TradeType.Buy ? p.EntryPrice + distance : p.EntryPrice - distance;
        }

        private TradeState GetOrRecoverState(Position p)
        {
            TradeState st;
            if (_trades.TryGetValue(p.Id, out st))
                return st;

            // A position opened by an earlier run of this cBot: rebuild its plan from the order comment.
            double pip = Symbol.PipSize;
            double atrR = RiskAtr();
            bool isLong = p.TradeType == TradeType.Buy;
            string comment = p.Comment ?? "";
            double initVolume = CommentValue(comment, "v=");
            double slPips = CommentValue(comment, "s=");
            double tpPips = CommentValue(comment, "t=");

            st = new TradeState();
            st.PositionId = p.Id;
            st.Setup = CommentSetup(comment);
            st.IsLong = isLong;
            st.EntryTime = p.EntryTime;
            bool stopOnLossSide = p.StopLoss.HasValue && (isLong ? p.StopLoss.Value < p.EntryPrice : p.StopLoss.Value > p.EntryPrice);
            st.RiskDist = slPips > 0 ? slPips * pip : (stopOnLossSide ? Math.Abs(p.EntryPrice - p.StopLoss.Value) : SlAtrMultiplier * atrR);
            st.Tp1Dist = tpPips > 0 ? tpPips * pip : Tp1AtrMultiplier * atrR;
            st.BeDone = p.StopLoss.HasValue && !stopOnLossSide;
            st.InitialVolume = initVolume > 0 ? initVolume : p.VolumeInUnits;
            st.Tp1Done = initVolume > 0 && p.VolumeInUnits < initVolume - VolumeEpsilon;
            if (!st.Tp1Done)
            {
                SplitPlan plan = VolumeMath.PlanSplit(p.VolumeInUnits, Tp1ClosePercent, Symbol.VolumeInUnitsMin, Symbol.VolumeInUnitsStep);
                st.Splittable = plan.Splittable;
                st.PartialVolume = plan.PartialVolume;
                st.CloseAllAtTp1 = !plan.Splittable && (Tp1ClosePercent >= 100.0 || NoSplitMode == NoSplitAction.CloseAllAtTp1);
            }
            st.Confidence = double.NaN;
            _trades[p.Id] = st;
            Log("RECOVERED #" + p.Id + " " + (isLong ? "BUY" : "SELL") + " " + Lots(p.VolumeInUnits) + " @" + P(p.EntryPrice) + " SL " + P(p.StopLoss)
                + " | R " + Pips(st.RiskDist) + ", TP1 " + Pips(st.Tp1Dist) + (st.Tp1Done ? ", TP1 already taken" : "") + (st.BeDone ? ", stop at/after BE" : ""));
            return st;
        }

        private void RecoverOpenPositions()
        {
            foreach (Position p in Positions.FindAll(BotLabel, SymbolName))
                GetOrRecoverState(p);
        }

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            try
            {
                Position p = args.Position;
                if (p.Label != BotLabel || p.SymbolName != SymbolName)
                    return;

                TradeState st;
                _trades.TryGetValue(p.Id, out st);
                _trades.Remove(p.Id);
                _lastCloseTime = Server.Time;

                // Total result of the trade = TP1 partial fill(s) + final fill, read from history.
                int expectedFills = st != null && st.Tp1Done && st.Splittable ? 2 : 1;
                double net = 0.0;
                double gross = 0.0;
                int fills = 0;
                foreach (HistoricalTrade h in History.FindAll(BotLabel, SymbolName))
                {
                    if (h.PositionId != p.Id)
                        continue;
                    net += h.NetProfit;
                    gross += h.GrossProfit;
                    fills++;
                }
                if (fills < expectedFills)
                {
                    net += p.NetProfit;
                    gross += p.GrossProfit;
                    fills++;
                }

                _statTrades++;
                _statNet += net;
                if (gross > 0)
                {
                    _statWins++;
                    _consecLosses = 0;
                }
                else if (gross < 0)
                {
                    _statLosses++;
                    _consecLosses++;
                }
                else
                {
                    _statScratch++;
                }

                string why = DescribeClose(args.Reason, st);
                if (args.Reason == PositionCloseReason.TakeProfit && st != null && !st.Tp1Done)
                    _statTp1++;   // server-side TP1 of an unsplittable position
                Log("CLOSED #" + p.Id + " " + (st != null ? st.Setup : "?") + " " + (p.TradeType == TradeType.Buy ? "BUY" : "SELL") + " by " + why
                    + ": net " + Signed(net, 2) + " " + _ccy + " (gross " + Signed(gross, 2) + ", " + fills + " fill(s)) | day "
                    + Signed(Account.Balance - _dayStartBalance, 2) + " " + _ccy + " | loss streak " + _consecLosses);

                if (MaxConsecutiveLosses > 0 && _consecLosses >= MaxConsecutiveLosses)
                {
                    _pauseUntil = Server.Time.AddMinutes(LossStreakPauseMinutes);
                    _consecLosses = 0;
                    Log("PAUSE: " + MaxConsecutiveLosses + " losses in a row -> no entries until " + _pauseUntil.ToString("HH:mm", Inv) + " UTC");
                }
            }
            catch (Exception ex)
            {
                LogError("OnPositionClosed", ex);
            }
        }

        private static string DescribeClose(PositionCloseReason reason, TradeState st)
        {
            if (st != null && st.CloseReason != null)
                return st.CloseReason;
            if (reason == PositionCloseReason.StopLoss)
            {
                if (st != null && st.Tp1Done)
                    return "TRAIL STOP";
                return st != null && st.BeDone ? "BREAK-EVEN STOP" : "STOP LOSS";
            }
            if (reason == PositionCloseReason.TakeProfit)
                return st != null && st.Tp1Done ? "RUNNER TP" : "TP1 (server)";
            if (reason == PositionCloseReason.StopOut)
                return "STOP OUT";
            return "CLOSED outside the bot";
        }

        #endregion

        #region Tick engine, spread, day guards

        private void CountTickForCalibration()
        {
            if (_risk.Count < 1)
                return;
            DateTime open = _risk.OpenTimes[_risk.Count - 1];
            if (open != _calBarOpen)
            {
                // The bar that just finished was watched from its first tick: compare our count with the broker's.
                if (_calBarFull)
                {
                    int idx = IndexOfTime(_risk, _calBarOpen);
                    if (idx >= 0 && idx <= _risk.Count - 2)
                        _calib.AddBar(_calBarTicks, _risk.TickVolumes[idx]);
                }
                _calBarFull = _calBarOpen != DateTime.MinValue;
                _calBarOpen = open;
                _calBarTicks = 0;
            }
            _calBarTicks++;
        }

        private double CalibratedTicks(double seconds)
        {
            return _ticks.CountSince(Server.Time, seconds) / _calib.Factor(CalibrationMinBars);
        }

        /// <summary>Tick density of the last few seconds relative to the N-bar average (the inflow detector).</summary>
        private double BurstRatio()
        {
            if (_baselineTicksPerBar <= 0 || _sigSec <= 0)
                return 0.0;
            double expected = _baselineTicksPerBar * TickWindowSeconds / _sigSec;
            return expected > 0 ? CalibratedTicks(TickWindowSeconds) / expected : 0.0;
        }

        /// <summary>Ticks in the last bar-length window relative to the average bar (the AI feature).</summary>
        private double BarTickVelocity()
        {
            return _baselineTicksPerBar > 0 ? CalibratedTicks(_sigSec) / _baselineTicksPerBar : 0.0;
        }

        private void TrackSpread()
        {
            double s = Symbol.Spread;
            if (s <= 0)
                return;
            _avgSpread = _avgSpread <= 0 ? s : _avgSpread + SpreadSmoothing * (s - _avgSpread);
        }

        private double RiskAtr()
        {
            if (_risk == null || _risk.Count < 2)
                return 0.0;
            double v = _atrRisk.Result[_risk.Count - 2];
            return Valid(v) && v > 0 ? v : 0.0;
        }

        private bool InSession(DateTime now)
        {
            if (!UseSessionFilter || SessionStartHour == SessionEndHour)
                return true;
            int h = now.Hour;
            if (SessionStartHour < SessionEndHour)
                return h >= SessionStartHour && h < SessionEndHour;
            return h >= SessionStartHour || h < SessionEndHour;
        }

        private void ResetDay(DateTime now)
        {
            _day = now.Date;
            _dayStartBalance = Account.Balance;
            _tradesToday = 0;
            _dailyHalt = false;
        }

        private void CheckNewDay(DateTime now)
        {
            if (now.Date == _day)
                return;
            Log("NEW DAY " + now.ToString("yyyy-MM-dd", Inv) + ": previous day " + Signed(Account.Balance - _dayStartBalance, 2) + " " + _ccy
                + ", " + _tradesToday + " trade(s); balance " + F(Account.Balance, 2) + " " + _ccy);
            ResetDay(now);
        }

        private void CheckDailyLoss()
        {
            if (DailyMaxLossPercent <= 0 || _dailyHalt || _dayStartBalance <= 0)
                return;
            double loss = _dayStartBalance - Account.Equity;
            if (loss >= _dayStartBalance * DailyMaxLossPercent / 100.0)
            {
                _dailyHalt = true;
                Log("HALT: day loss " + F(loss, 2) + " " + _ccy + " >= " + F(DailyMaxLossPercent, 1) + "% of " + F(_dayStartBalance, 2) + " " + _ccy
                    + " -> no new entries until 00:00 UTC (open positions keep their stops)");
            }
        }

        #endregion

        #region Chart

        private static readonly Color HudColorDark = Color.Silver;
        private static readonly Color HudColorLight = Color.DimGray;
        private static readonly Color BoxColor = Color.Gold;
        private static readonly Color BullColor = Color.DodgerBlue;
        private static readonly Color BearColor = Color.OrangeRed;

        private void DrawSetups()
        {
            if (!_hudEnabled)
                return;
            try
            {
                if (_sqzArmed)
                    Chart.DrawRectangle(ObjPrefix + "SQZ", _sqzStart, _sqzHigh, _sig.OpenTimes[_sig.Count - 1].AddSeconds(_sigSec), _sqzLow, BoxColor);
                else
                    Chart.RemoveObject(ObjPrefix + "SQZ");
                if (_bullSweep)
                    Chart.DrawIcon(ObjPrefix + "SWP_BULL", ChartIconType.UpTriangle, _bullTime, _bullExtreme, BullColor);
                else
                    Chart.RemoveObject(ObjPrefix + "SWP_BULL");
                if (_bearSweep)
                    Chart.DrawIcon(ObjPrefix + "SWP_BEAR", ChartIconType.DownTriangle, _bearTime, _bearExtreme, BearColor);
                else
                    Chart.RemoveObject(ObjPrefix + "SWP_BEAR");
            }
            catch (Exception ex)
            {
                _hudEnabled = false;
                Log("CHART drawing disabled: " + ex.Message);
            }
        }

        private void DrawEntry(Position p)
        {
            if (!_hudEnabled)
                return;
            try
            {
                bool isLong = p.TradeType == TradeType.Buy;
                Chart.DrawIcon(ObjPrefix + "E_" + p.Id, isLong ? ChartIconType.UpArrow : ChartIconType.DownArrow, Server.Time, p.EntryPrice,
                               isLong ? BullColor : BearColor);
            }
            catch (Exception ex)
            {
                _hudEnabled = false;
                Log("CHART drawing disabled: " + ex.Message);
            }
        }

        private void UpdateHud()
        {
            if (!_hudEnabled || _ai == null)
                return;
            try
            {
                DateTime now = Server.Time;
                double atrR = RiskAtr();
                double spread = Symbol.Spread;
                double burst = BurstRatio();
                var sb = new StringBuilder(512);
                sb.Append("QuantAI Scalper v").Append(BotVersion).Append(" | ").Append(SymbolName).Append(" | signal ").Append(SignalTimeFrame.ShortName)
                  .Append(" | ATR ").Append(RiskTimeFrame.ShortName).Append(" | ").Append(now.ToString("HH:mm:ss", Inv)).Append(" UTC\n");
                sb.Append("Spread ").Append(Pips(spread)).Append(" / ATR ").Append(Pips(atrR)).Append(" = ")
                  .Append(atrR > 0 ? Pct(spread / atrR) : "n/a").Append(" (max ").Append(Pct(MaxSpreadToAtr)).Append(")\n");
                sb.Append("Tick velocity ").Append(F(TickWindowSeconds, 1)).Append("s: ").Append(F(burst, 2)).Append("x (N ")
                  .Append(F(TickVelocityMultiplier, 2)).Append("x)").Append(burst >= TickVelocityMultiplier ? " INFLOW" : "")
                  .Append(" | bar ").Append(F(BarTickVelocity(), 2)).Append("x").Append(UseTickVelocity ? "" : " (filter OFF)").Append('\n');
                sb.Append("AI: ").Append(_ai.IsReady(AiMinSamplesPerClass)
                        ? _ai.Count + " samples, base win " + Pct(_ai.WinRate)
                        : "warming " + _ai.Wins + "/" + _ai.Losses + " of " + AiMinSamplesPerClass)
                  .Append(" | target ").Append(Pct(MinConfidence)).Append(" | last ").Append(double.IsNaN(_lastConf) ? "-" : Pct(_lastConf))
                  .Append(UseAiFilter ? "" : " (filter OFF)").Append('\n');
                sb.Append("SQZ: ").Append(_sqzNote).Append('\n');
                sb.Append("SWP: ").Append(_swpNote).Append('\n');
                sb.Append("State: ").Append(StateText(now)).Append(" | today ").Append(_tradesToday).Append(" trade(s), ")
                  .Append(Signed(Account.Equity - _dayStartBalance, 2)).Append(' ').Append(_ccy).Append(" | streak ").Append(_consecLosses).Append('\n');
                foreach (Position p in Positions.FindAll(BotLabel, SymbolName))
                {
                    TradeState st;
                    _trades.TryGetValue(p.Id, out st);
                    bool isLong = p.TradeType == TradeType.Buy;
                    double px = isLong ? Symbol.Bid : Symbol.Ask;
                    sb.Append(isLong ? "BUY " : "SELL ").Append(Lots(p.VolumeInUnits)).Append(" @").Append(P(p.EntryPrice)).Append(' ')
                      .Append(SignedPips(isLong ? px - p.EntryPrice : p.EntryPrice - px)).Append(" | SL ").Append(P(p.StopLoss));
                    if (st != null)
                    {
                        sb.Append(" | TP1 ").Append(st.Tp1Done ? "done" : P(TargetPrice(p, st.Tp1Dist))).Append(" | BE ").Append(st.BeDone ? "yes" : "no");
                        if (TimeExitBars > 0 && !st.Tp1Done)
                        {
                            double left = TimeExitBars * _riskSec - (now - st.EntryTime).TotalSeconds;
                            sb.Append(" | time exit in ").Append(F(Math.Max(0.0, left), 0)).Append('s');
                        }
                    }
                    sb.Append('\n');
                }
                sb.Append("Last: ").Append(_lastSkip);
                Color color = Application != null && Application.ColorTheme == ColorTheme.Light ? HudColorLight : HudColorDark;
                Chart.DrawStaticText(ObjPrefix + "HUD", sb.ToString(), VerticalAlignment.Top, HorizontalAlignment.Left, color);
            }
            catch (Exception ex)
            {
                _hudEnabled = false;
                Log("HUD disabled: " + ex.Message);
            }
        }

        private string StateText(DateTime now)
        {
            if (_dailyHalt)
                return "HALTED (daily loss)";
            if (now < _pauseUntil)
                return "PAUSED until " + _pauseUntil.ToString("HH:mm", Inv);
            if (MaxTradesPerDay > 0 && _tradesToday >= MaxTradesPerDay)
                return "DONE (max trades)";
            if (!InSession(now))
                return "OUT OF SESSION";
            return "TRADING";
        }

        #endregion

        #region Helpers

        private void LogBanner()
        {
            Log("=== QuantAI_Scalper_M1_MicroDepot_Pro v" + BotVersion + " | " + SymbolName + " | signal " + SignalTimeFrame.ShortName + " | ATR/time-exit "
                + RiskTimeFrame.ShortName + " | balance " + F(Account.Balance, 2) + " " + _ccy + " | leverage 1:" + F(EffectiveLeverage(), 0)
                + " | mode " + RunningMode + " ===");
            Log("PARAMS risk: " + F(RiskPercent, 2) + "% per trade | lots " + F(MinLots, 2) + "-" + F(MaxLots, 2) + " (min-lot override "
                + OnOff(AllowMinLotOverride) + ") | margin use " + F(MaxMarginUsePercent, 0) + "% | leverage check "
                + (LeverageOverride > 0 ? "1:" + F(LeverageOverride, 0) : "auto") + " | slippage " + (MaxSlippagePips > 0 ? F(MaxSlippagePips, 1) + "p" : "market")
                + " | daily loss " + (DailyMaxLossPercent > 0 ? F(DailyMaxLossPercent, 1) + "%" : "off") + " | loss streak "
                + (MaxConsecutiveLosses > 0 ? MaxConsecutiveLosses + " -> pause " + LossStreakPauseMinutes + "m" : "off")
                + " | max trades/day " + (MaxTradesPerDay > 0 ? MaxTradesPerDay.ToString(Inv) : "off"));
            Log("PARAMS exits: SL " + F(SlAtrMultiplier, 2) + " x ATR(" + AtrPeriod + ") | TP1 " + F(Tp1AtrMultiplier, 2) + " x ATR closes "
                + F(Tp1ClosePercent, 0) + "% (unsplittable: " + NoSplitMode + ") | runner TP " + (Tp2AtrMultiplier > 0 ? F(Tp2AtrMultiplier, 2) + " x ATR" : "none")
                + " | trail " + (TrailAtrMultiplier > 0 ? F(TrailAtrMultiplier, 2) + " x ATR, step " + F(TrailStepPips, 1) + "p, " + (TrailOnlyAfterTp1 ? "after TP1" : "from entry") : "off")
                + " | micro-BE " + (BeTriggerR > 0 ? "+" + F(BeTriggerR, 2) + "R -> entry+" + F(BeOffsetPips, 1) + "p" : "off")
                + " | time exit " + (TimeExitBars > 0 ? TimeExitBars + " " + RiskTimeFrame.ShortName + " bars" : "off"));
            Log("PARAMS filters: spread/ATR <= " + F(MaxSpreadToAtr, 3) + " | session " + (UseSessionFilter ? SessionStartHour.ToString("00", Inv) + ":00-"
                + SessionEndHour.ToString("00", Inv) + ":00 UTC" : "off") + " | cooldown " + CooldownSeconds + "s | max chase " + F(MaxChaseAtr, 2) + " x ATR");
            Log("PARAMS tick velocity: " + OnOff(UseTickVelocity) + " | N " + F(TickVelocityMultiplier, 2) + "x over " + F(TickWindowSeconds, 1)
                + "s vs the " + TickBaselineBars + "-bar average");
            Log("PARAMS setups: EMA " + EmaPeriod + " | squeeze " + (UseSqueeze ? SqueezeBars + " bars, range <= " + F(SqueezeMaxRangeAtr, 2)
                + " x ATR, EMA gap <= " + F(SqueezeMaxEmaGapAtr, 2) + " x ATR" : "off") + " | buffer " + F(BreakoutBufferPips, 1) + "p | sweep "
                + (UseSweep ? SweepLookbackBars + "-bar H/L, pierce >= " + F(SweepMinPiercePips, 1) + "p, close inside " + OnOff(SweepRequireCloseInside)
                + ", EMA reclaim within " + SweepMaxBarsToReclaim + " bars" : "off"));
            Log("PARAMS AI: " + OnOff(UseAiFilter) + " | Min Confidence " + F(MinConfidence, 2) + " | window " + AiTrainingBars + " bars | min "
                + AiMinSamplesPerClass + " per class | horizon " + AiHorizonBars + " " + RiskTimeFrame.ShortName + " bars | RSI(" + RsiPeriod + ") slope "
                + RsiSlopeBars + " bars | prior " + (AiUseEmpiricalPrior ? "empirical" : "balanced") + " | train spread "
                + (AiTrainSpreadPips > 0 ? F(AiTrainSpreadPips, 1) + "p" : "live"));
            Log("SYMBOL: pip " + Symbol.PipSize.ToString("G", Inv) + ", digits " + Symbol.Digits + ", volume min " + F(Symbol.VolumeInUnitsMin, 0)
                + "u step " + F(Symbol.VolumeInUnitsStep, 0) + "u, pip value " + F(Symbol.PipValue * Symbol.LotSize, 2) + " " + _ccy + "/lot, spread now "
                + Pips(Symbol.Spread));
            if (TimeExitBars > 0 && AiHorizonBars != TimeExitBars)
                Log("NOTE: AI label horizon (" + AiHorizonBars + ") differs from Time Exit Bars (" + TimeExitBars + "); the model then learns a different holding time than the bot uses");
            if (!UseSqueeze && !UseSweep)
                Log("NOTE: both setups are OFF - the cBot only manages positions carrying the label '" + BotLabel + "'");
            if (IsBacktesting)
                Log("NOTE: backtest with 'Tick data' - on bar data the tick velocity engine sees only synthetic ticks");
        }

        private void Log(string message)
        {
            if (_quiet)
                return;
            Print((object)message);
        }

        private void LogError(string where, Exception ex)
        {
            DateTime now = Server.Time;
            if (_lastErrorLog != DateTime.MinValue && (now - _lastErrorLog).TotalSeconds < ErrorLogIntervalSeconds)
                return;
            _lastErrorLog = now;
            string stack = ex.StackTrace ?? "";
            int cut = stack.IndexOf('\n');
            Log("ERROR in " + where + ": " + ex.GetType().Name + ": " + ex.Message + (stack.Length > 0 ? " @ " + (cut > 0 ? stack.Substring(0, cut) : stack).Trim() : ""));
        }

        private string FormatContributions(double[] c)
        {
            var sb = new StringBuilder("[", 48);
            for (int f = 0; f < AiFeatures.Dim; f++)
            {
                if (f > 0)
                    sb.Append(' ');
                sb.Append(AiFeatures.Names[f]).Append(c[f] >= 0 ? " +" : " ").Append(F(c[f], 2));
            }
            return sb.Append(']').ToString();
        }

        private string P(double price)
        {
            return price.ToString("F" + Symbol.Digits, Inv);
        }

        private string P(double? price)
        {
            return price.HasValue ? P(price.Value) : "none";
        }

        private string Pips(double priceDistance)
        {
            return F(priceDistance / Symbol.PipSize, 1) + "p";
        }

        private string SignedPips(double priceDistance)
        {
            double pips = priceDistance / Symbol.PipSize;
            return (pips >= 0 ? "+" : "") + F(pips, 1) + "p";
        }

        private string Lots(double units)
        {
            return F(Symbol.VolumeInUnitsToQuantity(units), 2) + " lot (" + F(units, 0) + "u)";
        }

        private static string F(double value, int decimals)
        {
            return value.ToString("F" + decimals, Inv);
        }

        private static string Signed(double value, int decimals)
        {
            return (value >= 0 ? "+" : "") + F(value, decimals);
        }

        private static string Pct(double ratio)
        {
            return double.IsNaN(ratio) ? "n/a" : F(ratio * 100.0, 0) + "%";
        }

        private static string OnOff(bool value)
        {
            return value ? "ON" : "OFF";
        }

        private static bool Valid(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static double CommentValue(string comment, string key)
        {
            foreach (string part in comment.Split('|'))
            {
                if (!part.StartsWith(key, StringComparison.Ordinal))
                    continue;
                double v;
                if (double.TryParse(part.Substring(key.Length), NumberStyles.Float, Inv, out v))
                    return v;
            }
            return 0.0;
        }

        private static string CommentSetup(string comment)
        {
            string[] parts = comment.Split('|');
            return parts.Length > 1 && parts[0] == "QAI" ? parts[1] : "?";
        }

        private static double MeanTickVolume(Bars bars, int endIndex, int count)
        {
            int start = Math.Max(0, endIndex - count + 1);
            if (endIndex < start || endIndex >= bars.Count)
                return 0.0;
            double sum = 0.0;
            for (int i = start; i <= endIndex; i++)
                sum += bars.TickVolumes[i];
            return sum / (endIndex - start + 1);
        }

        private static int FirstIndexAtOrAfter(Bars bars, DateTime time)
        {
            int lo = 0;
            int hi = bars.Count;
            while (lo < hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                if (bars.OpenTimes[mid] >= time)
                    hi = mid;
                else
                    lo = mid + 1;
            }
            return lo < bars.Count ? lo : -1;
        }

        private static int LastIndexBefore(Bars bars, DateTime time)
        {
            int first = FirstIndexAtOrAfter(bars, time);
            return first < 0 ? bars.Count - 1 : first - 1;
        }

        private static int IndexOfTime(Bars bars, DateTime time)
        {
            int i = FirstIndexAtOrAfter(bars, time);
            return i >= 0 && bars.OpenTimes[i] == time ? i : -1;
        }

        private static double TimeFrameSeconds(TimeFrame tf, Bars bars)
        {
            TimeFrame[] known =
            {
                TimeFrame.Minute, TimeFrame.Minute2, TimeFrame.Minute3, TimeFrame.Minute4, TimeFrame.Minute5, TimeFrame.Minute6,
                TimeFrame.Minute7, TimeFrame.Minute8, TimeFrame.Minute9, TimeFrame.Minute10, TimeFrame.Minute15, TimeFrame.Minute20,
                TimeFrame.Minute30, TimeFrame.Minute45, TimeFrame.Hour, TimeFrame.Hour2, TimeFrame.Hour3, TimeFrame.Hour4,
                TimeFrame.Hour6, TimeFrame.Hour8, TimeFrame.Hour12, TimeFrame.Daily
            };
            double[] minutes = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 15, 20, 30, 45, 60, 120, 180, 240, 360, 480, 720, 1440 };
            for (int i = 0; i < known.Length; i++)
            {
                if (tf.Equals(known[i]))
                    return minutes[i] * 60.0;
            }
            // Anything else: the shortest spacing between recent bars.
            double best = double.MaxValue;
            for (int i = Math.Max(1, bars.Count - 300); i < bars.Count; i++)
            {
                double d = (bars.OpenTimes[i] - bars.OpenTimes[i - 1]).TotalSeconds;
                if (d > 0 && d < best)
                    best = d;
            }
            return best == double.MaxValue ? 0.0 : best;
        }

        #endregion
    }

    #region Model and engine types (no cTrader dependencies)

    /// <summary>A closed signal bar waiting for enough future bars to know how the trade would have ended.</summary>
    internal sealed class PendingSample
    {
        public DateTime CloseTime;
        public double EntryBid;
        public double Spread;
        public double AtrRisk;
        public double[] LongX;
        public double[] ShortX;
    }

    /// <summary>Per-position management plan.</summary>
    internal sealed class TradeState
    {
        public int PositionId;
        public string Setup;
        public bool IsLong;
        public DateTime EntryTime;
        public double RiskDist;
        public double Tp1Dist;
        public double InitialVolume;
        public double PartialVolume;
        public bool Splittable;
        public bool CloseAllAtTp1;
        public bool Tp1Done;
        public bool BeDone;
        public bool BeBlockedLogged;
        public double MaxFavorable = double.NegativeInfinity;
        public double Confidence;
        public DateTime RetryAfter;
        public int Failures;
        public string CloseReason;
    }

    /// <summary>Feature vector of the classifier: [Tick_Velocity, EMA20_Distance, RSI_Slope, Spread_Ratio].</summary>
    internal static class AiFeatures
    {
        public const int Dim = 4;
        public static readonly string[] Names = { "TV", "EMA", "RSI", "SPR" };

        // Keeps log() finite for a tick-less window or a zero spread.
        private const double MinRatio = 1e-3;

        /// <param name="tickVelocity">ticks per bar-length window / average ticks per bar</param>
        /// <param name="priceMinusEma">price - EMA20</param>
        /// <param name="atr">signal timeframe ATR used to normalise the EMA distance</param>
        /// <param name="rsiDelta">RSI now - RSI k bars ago</param>
        /// <param name="spreadToAtr">spread / ATR</param>
        /// <param name="dir">+1 for a long, -1 for a short: directional features are mirrored so one model serves both sides</param>
        public static double[] Build(double tickVelocity, double priceMinusEma, double atr, double rsiDelta, double spreadToAtr, int dir)
        {
            var x = new double[Dim];
            x[0] = Math.Log(Math.Max(tickVelocity, MinRatio));
            x[1] = dir * priceMinusEma / atr;
            x[2] = dir * rsiDelta;
            x[3] = Math.Log(Math.Max(spreadToAtr, MinRatio));
            return x;
        }
    }

    /// <summary>
    /// Gaussian Naive Bayes over a rolling window of labelled samples. Sufficient statistics are kept per
    /// class so adding or expiring a sample costs O(features); they are rebuilt from the buffer once per
    /// window length to cancel floating-point drift.
    /// </summary>
    internal sealed class GaussianNaiveBayes
    {
        // Numerical guards: inputs are winsorised at +-4 global sigma so one outlier cannot saturate the
        // posterior, and every class variance gets a small floor relative to the feature's own variance.
        private const double WinsorSigma = 4.0;
        private const double VarianceFloorRatio = 1e-3;
        private const double MaxLogOdds = 50.0;

        private readonly int _dim;
        private readonly int _capacity;
        private readonly double[][] _x;
        private readonly bool[] _y;
        private readonly double[] _n = new double[2];
        private readonly double[][] _sum;
        private readonly double[][] _sq;
        private int _start;
        private int _count;
        private int _addsSinceRebuild;

        public GaussianNaiveBayes(int dim, int capacity)
        {
            _dim = dim;
            _capacity = Math.Max(4, capacity);
            _x = new double[_capacity][];
            _y = new bool[_capacity];
            _sum = new[] { new double[dim], new double[dim] };
            _sq = new[] { new double[dim], new double[dim] };
        }

        public int Count
        {
            get { return _count; }
        }

        public int Wins
        {
            get { return (int)Math.Round(_n[1]); }
        }

        public int Losses
        {
            get { return (int)Math.Round(_n[0]); }
        }

        public double WinRate
        {
            get { return _count > 0 ? _n[1] / _count : double.NaN; }
        }

        public bool IsReady(int minPerClass)
        {
            double need = Math.Max(2, minPerClass);
            return _n[0] >= need && _n[1] >= need;
        }

        public double ClassMean(bool win, int feature)
        {
            int c = win ? 1 : 0;
            return _n[c] > 0 ? _sum[c][feature] / _n[c] : double.NaN;
        }

        public bool Add(double[] x, bool win)
        {
            if (x == null || x.Length != _dim)
                return false;
            for (int f = 0; f < _dim; f++)
            {
                if (double.IsNaN(x[f]) || double.IsInfinity(x[f]))
                    return false;
            }
            if (_count == _capacity)
            {
                Accumulate(_x[_start], _y[_start], -1.0);
                _x[_start] = null;
                _start = (_start + 1) % _capacity;
                _count--;
            }
            int idx = (_start + _count) % _capacity;
            _x[idx] = (double[])x.Clone();
            _y[idx] = win;
            _count++;
            Accumulate(_x[idx], win, 1.0);
            if (++_addsSinceRebuild >= _capacity)
                Rebuild();
            return true;
        }

        /// <summary>
        /// P(win | x). With a balanced prior 0.5 means "the features look neither like winners nor like losers".
        /// contributions (optional) receives each feature's log-likelihood ratio win vs loss.
        /// </summary>
        public double Predict(double[] x, bool empiricalPrior, double[] contributions)
        {
            if (x == null || x.Length != _dim || _n[0] < 2 || _n[1] < 2)
                return double.NaN;
            double n = _n[0] + _n[1];
            double logOdds = empiricalPrior ? Math.Log(_n[1] / _n[0]) : 0.0;
            for (int f = 0; f < _dim; f++)
            {
                double gMean = (_sum[0][f] + _sum[1][f]) / n;
                double gVar = Math.Max((_sq[0][f] + _sq[1][f]) / n - gMean * gMean, 0.0);
                double gSd = Math.Sqrt(gVar);
                double xf = x[f];
                if (gSd > 0)
                    xf = Math.Max(gMean - WinsorSigma * gSd, Math.Min(gMean + WinsorSigma * gSd, xf));
                double floor = Math.Max(gVar * VarianceFloorRatio, 1e-12);
                double c = LogPdf(xf, 1, f, floor) - LogPdf(xf, 0, f, floor);
                if (contributions != null && f < contributions.Length)
                    contributions[f] = c;
                logOdds += c;
            }
            logOdds = Math.Max(-MaxLogOdds, Math.Min(MaxLogOdds, logOdds));
            return 1.0 / (1.0 + Math.Exp(-logOdds));
        }

        private double LogPdf(double x, int c, int f, double floor)
        {
            double m = _sum[c][f] / _n[c];
            double v = Math.Max(_sq[c][f] / _n[c] - m * m, 0.0) + floor;
            double d = x - m;
            return -0.5 * Math.Log(2.0 * Math.PI * v) - d * d / (2.0 * v);
        }

        private void Accumulate(double[] x, bool win, double sign)
        {
            int c = win ? 1 : 0;
            _n[c] += sign;
            for (int f = 0; f < _dim; f++)
            {
                _sum[c][f] += sign * x[f];
                _sq[c][f] += sign * x[f] * x[f];
            }
        }

        private void Rebuild()
        {
            _n[0] = 0;
            _n[1] = 0;
            for (int c = 0; c < 2; c++)
            {
                for (int f = 0; f < _dim; f++)
                {
                    _sum[c][f] = 0;
                    _sq[c][f] = 0;
                }
            }
            for (int i = 0; i < _count; i++)
            {
                int idx = (_start + i) % _capacity;
                Accumulate(_x[idx], _y[idx], 1.0);
            }
            _addsSinceRebuild = 0;
        }
    }

    /// <summary>Sliding store of tick timestamps answering "how many ticks in the last N seconds".</summary>
    internal sealed class TickVelocityEngine
    {
        private readonly List<long> _times = new List<long>();
        private readonly long _retention;
        private int _head;

        public TickVelocityEngine(double retentionSeconds)
        {
            _retention = TimeSpan.FromSeconds(Math.Max(1.0, retentionSeconds)).Ticks;
        }

        public int Count
        {
            get { return _times.Count - _head; }
        }

        public void AddTick(DateTime time)
        {
            long t = time.Ticks;
            int n = _times.Count;
            if (n > _head && t < _times[n - 1])
                t = _times[n - 1];   // keep the store monotonic if the clock steps back
            _times.Add(t);
            long limit = t - _retention;
            while (_head < _times.Count && _times[_head] < limit)
                _head++;
            if (_head > 4096 && _head > _times.Count / 2)
            {
                _times.RemoveRange(0, _head);
                _head = 0;
            }
        }

        public int CountSince(DateTime now, double seconds)
        {
            long from = now.Ticks - (long)(seconds * TimeSpan.TicksPerSecond);
            int lo = _head;
            int hi = _times.Count;
            while (lo < hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                if (_times[mid] > from)
                    hi = mid;
                else
                    lo = mid + 1;
            }
            return _times.Count - lo;
        }
    }

    /// <summary>
    /// Ratio of ticks seen by OnTick to the broker's tick volume over recent fully observed bars. It makes
    /// the live count comparable with the historical tick-volume baseline whatever the feed counts as a tick.
    /// </summary>
    internal sealed class TickCalibrator
    {
        private readonly int _window;
        private readonly Queue<double> _observed = new Queue<double>();
        private readonly Queue<double> _reported = new Queue<double>();
        private double _sumObserved;
        private double _sumReported;

        public TickCalibrator(int window)
        {
            _window = Math.Max(1, window);
        }

        public int Bars
        {
            get { return _observed.Count; }
        }

        public void AddBar(double observed, double reported)
        {
            if (observed <= 0 || reported <= 0)
                return;
            _observed.Enqueue(observed);
            _reported.Enqueue(reported);
            _sumObserved += observed;
            _sumReported += reported;
            while (_observed.Count > _window)
            {
                _sumObserved -= _observed.Dequeue();
                _sumReported -= _reported.Dequeue();
            }
        }

        public double Factor(int minBars)
        {
            if (_observed.Count < Math.Max(1, minBars) || _sumObserved <= 0 || _sumReported <= 0)
                return 1.0;
            return _sumObserved / _sumReported;
        }
    }

    /// <summary>
    /// Replays the bot's exit rules over future bars (bid OHLC): SL, micro break-even and TP1. When SL and
    /// TP1 fall inside the same bar the stop is assumed first, so labels err on the pessimistic side.
    /// </summary>
    internal static class OutcomeSimulator
    {
        public static bool Tp1First(bool isLong, double entryBid, double spread, double slDist, double tp1Dist,
                                    double beTriggerDist, double beOffset, double[] highs, double[] lows, int count)
        {
            bool useBe = beTriggerDist > 0;
            if (isLong)
            {
                double entry = entryBid + spread;                  // buy at ask, exits trigger on bid
                double sl = entry - slDist;
                double tp = entry + tp1Dist;
                double beTrigger = entry + beTriggerDist;
                double beStop = entry + beOffset;
                for (int k = 0; k < count; k++)
                {
                    if (lows[k] <= sl)
                        return false;
                    if (highs[k] >= tp)
                        return true;
                    if (useBe && highs[k] >= beTrigger && beStop > sl)
                        sl = beStop;                               // effective from the next bar
                }
                return false;
            }
            else
            {
                double entry = entryBid;                           // sell at bid, exits trigger on ask
                double sl = entry + slDist;
                double tp = entry - tp1Dist;
                double beTrigger = entry - beTriggerDist;
                double beStop = entry - beOffset;
                for (int k = 0; k < count; k++)
                {
                    double askHigh = highs[k] + spread;
                    double askLow = lows[k] + spread;
                    if (askHigh >= sl)
                        return false;
                    if (askLow <= tp)
                        return true;
                    if (useBe && askLow <= beTrigger && beStop < sl)
                        sl = beStop;
                }
                return false;
            }
        }
    }

    internal struct SqueezeCheck
    {
        public bool Ok;
        public bool RangeOk;
        public bool EmaOk;
        public double BoxHigh;
        public double BoxLow;
        public double Height;
        public double EmaGap;
        public double MaxHeight;
        public double MaxGap;
    }

    /// <summary>Bob Volman style 20 EMA squeeze: a tight box of recent bars with the EMA inside or right at it.</summary>
    internal static class VolmanSetups
    {
        public static SqueezeCheck CheckSqueeze(double[] highs, double[] lows, int count, double ema, double atr, double maxRangeAtr, double maxEmaGapAtr)
        {
            var q = new SqueezeCheck();
            double hi = double.MinValue;
            double lo = double.MaxValue;
            for (int i = 0; i < count; i++)
            {
                hi = Math.Max(hi, highs[i]);
                lo = Math.Min(lo, lows[i]);
            }
            q.BoxHigh = hi;
            q.BoxLow = lo;
            q.Height = hi - lo;
            q.EmaGap = ema > hi ? ema - hi : (ema < lo ? lo - ema : 0.0);
            q.MaxHeight = maxRangeAtr * atr;
            q.MaxGap = maxEmaGapAtr * atr;
            q.RangeOk = count > 0 && q.Height <= q.MaxHeight;
            q.EmaOk = q.EmaGap <= q.MaxGap;
            q.Ok = atr > 0 && q.RangeOk && q.EmaOk;
            return q;
        }
    }

    /// <summary>False pierce of an N-bar extreme (stop run) that may reverse back to the mean.</summary>
    internal static class SweepDetector
    {
        private const double PriceEpsilon = 1e-10;

        public static bool IsBullSweep(double priorLow, double low, double close, double minPierce, bool requireCloseInside)
        {
            return low < priorLow && priorLow - low >= minPierce - PriceEpsilon && (!requireCloseInside || close > priorLow);
        }

        public static bool IsBearSweep(double priorHigh, double high, double close, double minPierce, bool requireCloseInside)
        {
            return high > priorHigh && high - priorHigh >= minPierce - PriceEpsilon && (!requireCloseInside || close < priorHigh);
        }
    }

    internal struct SplitPlan
    {
        public bool Splittable;
        public double PartialVolume;
        public double RemainingVolume;
    }

    /// <summary>Volume rounding and the TP1 split, independent of any broker API.</summary>
    internal static class VolumeMath
    {
        private const double Eps = 1e-6;

        public static double FloorToStep(double volume, double step)
        {
            if (step <= 0)
                return volume;
            return Math.Floor(volume / step + 1e-9) * step;
        }

        public static double CeilToStep(double volume, double step)
        {
            if (step <= 0)
                return volume;
            return Math.Ceiling(volume / step - 1e-9) * step;
        }

        public static double RoundToStep(double volume, double step)
        {
            if (step <= 0)
                return volume;
            return Math.Round(volume / step, MidpointRounding.AwayFromZero) * step;
        }

        /// <summary>
        /// Part closed at TP1 = closePercent of the volume rounded to the step; both the part and the runner
        /// must be tradable (>= min volume), otherwise the position cannot be split.
        /// </summary>
        public static SplitPlan PlanSplit(double volume, double closePercent, double minVolume, double step)
        {
            var plan = new SplitPlan();
            if (closePercent >= 100.0 || volume <= 0 || step <= 0)
                return plan;
            double part = RoundToStep(volume * closePercent / 100.0, step);
            if (part < minVolume - Eps)
                part = CeilToStep(minVolume, step);
            if (volume - part < minVolume - Eps)
                part = FloorToStep(volume - minVolume, step);
            double rest = volume - part;
            if (part >= minVolume - Eps && rest >= minVolume - Eps && part > Eps && rest > Eps)
            {
                plan.Splittable = true;
                plan.PartialVolume = part;
                plan.RemainingVolume = rest;
            }
            return plan;
        }
    }

    #endregion
}
