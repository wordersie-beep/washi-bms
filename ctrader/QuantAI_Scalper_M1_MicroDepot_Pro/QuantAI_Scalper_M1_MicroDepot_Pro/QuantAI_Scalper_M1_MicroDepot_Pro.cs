// =====================================================================================================
//  QuantAI_Scalper_M1_MicroDepot_Pro  v2.0.0
//  cTrader Automate cBot | multi-market micro-impulse scalper
//    - Forex on M1 during the week, crypto on M5 around the clock (weekends included)
//    - ONE instance trades every symbol of the two lists (a demo account allows one cloud instance)
//    - up to Max Open Positions at once, at most one per symbol
//  Needs cTrader 5.0+ (Algo API 1.0.9+): Windows, Mac, Web and Mobile, local or cloud. C# 7.3 syntax only.
//  Ready to run: the instance opens on EURUSD m1 and every parameter below already holds its working value.
// -----------------------------------------------------------------------------------------------------
//  ENGINE (per symbol)
//   1. Tick Velocity Engine   Ticks in a sliding window (default 3 s) against the average tick rate of the
//                             last 100 bars; a ratio of at least N marks an institutional inflow.
//   2. Volman 20 EMA Squeeze  A tight box of recent bars hugging the 20 EMA, entered on the breaking tick.
//   3. Liquidity Sweep        A false pierce of the 10-bar high/low, entered when price reclaims the EMA.
//   4. AI classifier          Gaussian Naive Bayes over [Tick_Velocity, EMA20_Distance, RSI_Slope,
//                             Spread_Ratio], trained per symbol on history, then online on every closed bar
//                             with the outcome this bot's own exit rules would have produced.
//   5. Exits                  SL 0.8 x ATR, TP1 1.0 x ATR closing 60 %, micro break-even at +0.4R, ATR
//                             trailing for the runner, time exit after 5 bars of the ATR timeframe.
//  PORTFOLIO
//   - Size: Fixed Lots per trade (0 = Risk Percent of balance), capped by Max Lots, by the broker maximum
//     and by margin: a trade may use Margin Per Trade % of free margin, and all open positions together may
//     block Max Total Margin % of equity. When even the minimum volume needs more than the per-trade share,
//     the minimum volume is still taken while the total limit allows it (a 50 EUR account keeps trading
//     0.01 lot).
//   - Forex follows the session hours and Max Spread / ATR; crypto trades 24/7 on its own timeframe and
//     spread limit. A symbol the broker does not offer is skipped with a log line.
//   - Daily loss limit for the whole account; loss-streak pause and cooldown per symbol.
//
//  Every threshold that drives a trading decision is a UI parameter and is used exactly as entered. The log
//  prints the values in force, every skipped signal with its reasons and a STATUS summary every 15 minutes.
//  Pip-denominated parameters mean broker pips on Forex and half a basis point of price on crypto and other
//  non-Forex symbols. Backtest with "Tick data": the tick velocity engine needs real ticks.
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

    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None, DefaultSymbolName = "EURUSD", DefaultTimeFrame = "M1")]
    public class QuantAI_Scalper_M1_MicroDepot_Pro : Robot
    {
        private const string BotVersion = "2.0.0";

        #region Parameters

        // ---- 0. Markets --------------------------------------------------------------------------------

        [Parameter("Forex Symbols", Group = "0. Markets", DefaultValue = "EURUSD,GBPUSD,USDJPY,AUDUSD,USDCAD,USDCHF")]
        public string FxSymbols { get; set; }

        [Parameter("Crypto Symbols (24/7)", Group = "0. Markets", DefaultValue = "BTCUSD,ETHUSD")]
        public string CryptoSymbols { get; set; }

        [Parameter("Max Open Positions", Group = "0. Markets", DefaultValue = 8, MinValue = 1)]
        public int MaxOpenPositions { get; set; }

        [Parameter("Crypto Signal & ATR TimeFrame", Group = "0. Markets", DefaultValue = "Minute5")]
        public TimeFrame CryptoTimeFrame { get; set; }

        [Parameter("Crypto Max Spread / ATR", Group = "0. Markets", DefaultValue = 0.35, MinValue = 0.0, Step = 0.01)]
        public double CryptoMaxSpreadToAtr { get; set; }

        [Parameter("Crypto Trades 24/7 (ignore session)", Group = "0. Markets", DefaultValue = true)]
        public bool CryptoIgnoresSession { get; set; }

        // ---- 1. Risk and money ------------------------------------------------------------------------

        [Parameter("Fixed Lots Per Trade (0 = Risk %)", Group = "1. Risk & Money", DefaultValue = 4.0, MinValue = 0.0, Step = 0.01)]
        public double FixedLots { get; set; }

        [Parameter("Risk Percent (% of balance)", Group = "1. Risk & Money", DefaultValue = 1.0, MinValue = 0.01, MaxValue = 100.0, Step = 0.05)]
        public double RiskPercent { get; set; }

        [Parameter("Min Lots (floor)", Group = "1. Risk & Money", DefaultValue = 0.01, MinValue = 0.0, Step = 0.01)]
        public double MinLots { get; set; }

        [Parameter("Max Lots (cap)", Group = "1. Risk & Money", DefaultValue = 10.0, MinValue = 0.01, Step = 0.01)]
        public double MaxLots { get; set; }

        [Parameter("Use Min Lot If Size Is Smaller", Group = "1. Risk & Money", DefaultValue = true)]
        public bool AllowMinLotOverride { get; set; }

        [Parameter("Margin Per Trade (% of free margin)", Group = "1. Risk & Money", DefaultValue = 30.0, MinValue = 1.0, MaxValue = 100.0, Step = 5.0)]
        public double MarginPerTradePercent { get; set; }

        [Parameter("Max Total Margin (% of equity)", Group = "1. Risk & Money", DefaultValue = 90.0, MinValue = 1.0, MaxValue = 100.0, Step = 5.0)]
        public double MaxTotalMarginPercent { get; set; }

        [Parameter("Leverage For Margin Check (0 = broker)", Group = "1. Risk & Money", DefaultValue = 0.0, MinValue = 0.0, Step = 1.0)]
        public double LeverageOverride { get; set; }

        [Parameter("Max Slippage Pips (0 = market)", Group = "1. Risk & Money", DefaultValue = 0.5, MinValue = 0.0, Step = 0.1)]
        public double MaxSlippagePips { get; set; }

        [Parameter("Daily Max Loss % (0 = off)", Group = "1. Risk & Money", DefaultValue = 6.0, MinValue = 0.0, Step = 0.5)]
        public double DailyMaxLossPercent { get; set; }

        [Parameter("Max Consecutive Losses Per Symbol (0 = off)", Group = "1. Risk & Money", DefaultValue = 4, MinValue = 0)]
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

        [Parameter("Forex ATR & Time-Exit TimeFrame", Group = "2. Exits", DefaultValue = "Minute")]
        public TimeFrame RiskTimeFrame { get; set; }

        [Parameter("ATR Period", Group = "2. Exits", DefaultValue = 14, MinValue = 2)]
        public int AtrPeriod { get; set; }

        // ---- 3. Entry filters --------------------------------------------------------------------------

        [Parameter("Forex Max Spread / ATR", Group = "3. Entry Filters", DefaultValue = 0.12, MinValue = 0.0, Step = 0.01)]
        public double MaxSpreadToAtr { get; set; }

        [Parameter("Use Session Filter (Forex)", Group = "3. Entry Filters", DefaultValue = true)]
        public bool UseSessionFilter { get; set; }

        [Parameter("Session Start Hour (UTC)", Group = "3. Entry Filters", DefaultValue = 6, MinValue = 0, MaxValue = 23)]
        public int SessionStartHour { get; set; }

        [Parameter("Session End Hour (UTC)", Group = "3. Entry Filters", DefaultValue = 20, MinValue = 0, MaxValue = 24)]
        public int SessionEndHour { get; set; }

        [Parameter("Cooldown After Close (sec, per symbol)", Group = "3. Entry Filters", DefaultValue = 30, MinValue = 0)]
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

        [Parameter("Forex Signal TimeFrame (M1/M5)", Group = "5. Volman Setups", DefaultValue = "Minute")]
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

        [Parameter("Min Confidence (0.00-1.00)", Group = "6. AI Classifier (Naive Bayes)", DefaultValue = 0.50, MinValue = 0.0, MaxValue = 1.0, Step = 0.01)]
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

        [Parameter("Log Every Bar (all symbols)", Group = "7. Log & Display", DefaultValue = false)]
        public bool LogBarSummary { get; set; }

        [Parameter("Log Skip Reasons", Group = "7. Log & Display", DefaultValue = true)]
        public bool LogSkipReasons { get; set; }

        [Parameter("Show Chart HUD", Group = "7. Log & Display", DefaultValue = true)]
        public bool ShowHud { get; set; }

        [Parameter("Status Summary Every (min, 0 = off)", Group = "7. Log & Display", DefaultValue = 15, MinValue = 0)]
        public int StatusEveryMinutes { get; set; }

        #endregion

        #region Technical constants

        // Numerical plumbing only - none of these decides whether a trade is taken.
        private const int IndicatorWarmupFactor = 3;     // bars per indicator period before values are trusted
        private const int CalibrationWindowBars = 30;    // bars used to match OnTick counts with broker tick volume
        private const int CalibrationMinBars = 3;        // calibration is applied once this many full bars were seen
        private const int SpreadWindowTicks = 500;       // in-session spreads kept for the rolling median
        private const int SpreadCalibrationTicks = 300;  // in-session ticks before the AI is retrained on the live spread
        private const int TypicalAtrBars = 60;           // ATR bars averaged when judging whether the spread fits the strategy
        private const int MarginFitIterations = 20;      // volume steps tried when dynamic leverage makes margin non-linear
        private const int MaxNoMoneyRetries = 2;         // halve the volume and retry after a "no money" reject
        private const double RetryDelaySeconds = 1.0;    // first pause before retrying a failed close/modify
        private const double MaxRetryDelaySeconds = 60.0; // the pause doubles per failure up to this
        private const double ErrorLogIntervalSeconds = 10.0;
        private const double TrailMinIntervalSeconds = 1.0; // at most one trailing-stop request per position per second
        private const double VolumeEpsilon = 1e-6;
        private const double FxPipRatio = 0.5e-4;        // broker pip / price at or above this = a Forex-style pip
        private const double NonFxPipRatio = 0.5e-4;     // otherwise one "pip" of the parameters = 0.5 basis point of price
        private const string ObjPrefix = "QAI_";

        #endregion

        #region State

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private readonly List<Market> _markets = new List<Market>();
        private readonly Dictionary<string, Market> _bySymbol = new Dictionary<string, Market>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, TradeState> _trades = new Dictionary<int, TradeState>();

        private DateTime _day = DateTime.MinValue;
        private double _dayStartBalance;
        private int _tradesToday;
        private bool _dailyHalt;

        private string _ccy = "";
        private bool _quiet;
        private bool _hudEnabled;
        private bool _isPepperstone;
        private Market _chartMarket;
        private DateTime _lastErrorLog = DateTime.MinValue;
        private string _lastEvent = "-";

        private DateTime _nextStatus = DateTime.MinValue;
        private DateTime _statusSince;
        private bool? _wasInSession;

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

            _isPepperstone = Account.BrokerName != null && Account.BrokerName.IndexOf("pepperstone", StringComparison.OrdinalIgnoreCase) >= 0;
            _hudEnabled = ShowHud && (RunningMode == RunningMode.RealTime || RunningMode == RunningMode.VisualBacktesting);
            _statusSince = Server.Time;
            ResetDay(Server.Time);
            LogBanner();

            List<string> crypto = MarketMath.ParseSymbols(CryptoSymbols);
            var cryptoSet = new HashSet<string>(crypto, StringComparer.OrdinalIgnoreCase);
            foreach (string name in MarketMath.ParseSymbols(FxSymbols))
            {
                if (!cryptoSet.Contains(name))
                    AddMarket(name, false);
            }
            foreach (string name in crypto)
                AddMarket(name, true);

            if (_markets.Count == 0)
            {
                Log("FATAL: none of the listed symbols can be traded on this account - check 'Forex Symbols' and 'Crypto Symbols'. The cBot stops.");
                Stop();
                return;
            }

            RecoverOpenPositions();
            Positions.Closed += OnPositionClosed;
            Timer.Start(TimeSpan.FromSeconds(1));
            CheckSessionTransition(Server.Time);

            var names = new List<string>();
            foreach (Market m in _markets)
                names.Add(m.Name + (m.IsCrypto ? " (crypto " + m.SigTf.ShortName + ")" : ""));
            Log("READY: " + _markets.Count + " markets - " + string.Join(", ", names) + " | max " + MaxOpenPositions + " open positions");
            UpdateHud();
        }

        protected override void OnTick()
        {
            if (_chartMarket != null)
                OnMarketTick(_chartMarket);
        }

        protected override void OnTimer()
        {
            try
            {
                // Time exits must fire even when a feed goes quiet, so management also runs here.
                DateTime now = Server.Time;
                CheckNewDay(now);
                CheckDailyLoss();
                foreach (Market m in _markets)
                    ManageMarketPositions(m);
                CheckSessionTransition(now);
                EmitStatusIfDue(now);
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
                    + ", net " + Signed(_statNet, 2) + " " + _ccy);
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
            if (SignalTimeFrame == null || RiskTimeFrame == null || CryptoTimeFrame == null)
                return "a TimeFrame parameter is empty";
            if (MarketMath.ParseSymbols(FxSymbols).Count == 0 && MarketMath.ParseSymbols(CryptoSymbols).Count == 0)
                return "both symbol lists are empty";
            return null;
        }

        /// <summary>The broker's name for a listed symbol: exact match first, then the same letters and digits (BTC/USD = BTCUSD).</summary>
        private string ResolveSymbolName(string requested)
        {
            if (Symbols.Exists(requested))
                return requested;
            string key = MarketMath.SymbolKey(requested);
            if (key.Length == 0)
                return null;
            for (int i = 0; i < Symbols.Count; i++)
            {
                string candidate = Symbols[i];
                if (!string.IsNullOrEmpty(candidate) && MarketMath.SymbolKey(candidate) == key)
                    return candidate;
            }
            return null;
        }

        /// <summary>Resolves a symbol, loads its history, builds indicators and trains its AI.</summary>
        private void AddMarket(string requested, bool isCrypto)
        {
            string name = requested;
            try
            {
                name = ResolveSymbolName(requested);
                if (name == null)
                {
                    Log("MARKET " + requested + ": not offered on this account - skipped");
                    return;
                }
                if (_bySymbol.ContainsKey(name))
                    return;
                Symbol sym = Symbols.GetSymbol(name);
                if (sym == null)
                {
                    Log("MARKET " + name + ": symbol could not be loaded - skipped");
                    return;
                }
                if (sym.TradingMode != SymbolTradingMode.FullAccess)
                    Log("MARKET " + name + ": WARNING trading mode is " + sym.TradingMode + " - new entries wait until the broker enables trading");

                var m = new Market();
                m.Name = name;
                m.Symbol = sym;
                m.IsCrypto = isCrypto;
                m.IsChart = string.Equals(name, SymbolName, StringComparison.OrdinalIgnoreCase);
                m.SigTf = isCrypto ? CryptoTimeFrame : SignalTimeFrame;
                m.RiskTf = isCrypto ? CryptoTimeFrame : RiskTimeFrame;
                m.SameTf = m.SigTf.Equals(m.RiskTf);
                m.MaxSpreadToAtr = isCrypto ? CryptoMaxSpreadToAtr : MaxSpreadToAtr;
                m.Sig = MarketData.GetBars(m.SigTf, name);
                m.Risk = m.SameTf ? m.Sig : MarketData.GetBars(m.RiskTf, name);
                m.SigSec = TimeFrameSeconds(m.SigTf, m.Sig);
                m.RiskSec = TimeFrameSeconds(m.RiskTf, m.Risk);
                if (m.SigSec < 60.0 || m.RiskSec < 60.0)
                {
                    Log("MARKET " + name + ": signal/ATR timeframes must be time based (m1 or higher) - skipped");
                    return;
                }

                LoadHistory(m);

                m.Ema = Indicators.ExponentialMovingAverage(m.Sig.ClosePrices, EmaPeriod);
                m.AtrSig = Indicators.AverageTrueRange(m.Sig, AtrPeriod, MovingAverageType.WilderSmoothing);
                m.AtrRisk = m.SameTf ? m.AtrSig : Indicators.AverageTrueRange(m.Risk, AtrPeriod, MovingAverageType.WilderSmoothing);
                m.Rsi = Indicators.RelativeStrengthIndex(m.Sig.ClosePrices, RsiPeriod);

                double price = sym.Bid > 0 ? sym.Bid : (m.Sig.Count > 0 ? m.Sig.ClosePrices[m.Sig.Count - 1] : 0.0);
                m.FxLike = MarketMath.IsFxLike(sym.PipSize, price, FxPipRatio);
                m.BotPip = MarketMath.BotPip(sym.PipSize, price, m.FxLike, NonFxPipRatio);

                m.Ticks = new TickVelocityEngine(Math.Max(TickWindowSeconds, m.SigSec) + 5.0);
                m.Calib = new TickCalibrator(CalibrationWindowBars);
                m.Spreads = new SpreadTracker(SpreadWindowTicks);
                m.Ai = new GaussianNaiveBayes(AiFeatures.Dim, AiTrainingBars * 2);
                m.EngineStart = Server.Time;
                m.HBuf = new double[AiHorizonBars];
                m.LBuf = new double[AiHorizonBars];
                m.WinH = new double[SqueezeBars];
                m.WinL = new double[SqueezeBars];
                m.CommissionPerUnit = CommissionEstimatePerUnit(m);
                m.CommissionSource = m.CommissionPerUnit > 0 ? "symbol info" : "none reported";
                int lastClosed = m.Sig.Count - 2;
                if (lastClosed >= 0)
                    m.BaselineTicksPerBar = MeanTickVolume(m.Sig, lastClosed, TickBaselineBars);

                Log("MARKET " + name + ": " + (isCrypto ? "crypto" : "forex") + " | signal " + m.SigTf.ShortName + ", ATR/time-exit " + m.RiskTf.ShortName
                    + " | spread/ATR <= " + F(m.MaxSpreadToAtr, 2) + " | " + (m.FxLike ? "pip " + sym.PipSize.ToString("G", Inv) : "1 pip = 0.5 bp = " + P(m, m.BotPip))
                    + " | volume min " + Units(sym.VolumeInUnitsMin) + " step " + Units(sym.VolumeInUnitsStep) + ", lot " + Units(sym.LotSize)
                    + " | commission " + CommissionText(m) + " | spread now " + D(m, sym.Spread) + " | tick baseline " + F(m.BaselineTicksPerBar, 1) + "/bar"
                    + (m.BaselineTicksPerBar <= 0 ? " | WARNING: no tick volume in history, the tick filter cannot pass" : ""));

                TrainFromHistory(m);
                if (lastClosed >= 0)
                {
                    m.LastSigClosed = m.Sig.OpenTimes[lastClosed];
                    EvaluateSetups(m, lastClosed);
                }
                if (m.Risk.Count >= 2)
                    m.LastRiskClosed = m.Risk.OpenTimes[m.Risk.Count - 2];

                _markets.Add(m);
                _bySymbol[name] = m;

                if (m.IsChart)
                {
                    _chartMarket = m;   // its ticks arrive through OnTick
                    DrawSetups(m);
                }
                else
                {
                    sym.Tick += args => OnMarketTick(m);
                }
            }
            catch (Exception ex)
            {
                Log("MARKET " + name + ": could not start (" + ex.GetType().Name + ": " + ex.Message + ") - skipped");
            }
        }

        private void OnMarketTick(Market m)
        {
            try
            {
                DateTime now = Server.Time;
                m.Ticks.AddTick(now);
                CountTickForCalibration(m);
                TrackSpread(m, now);
                CheckSpreadCalibration(m);
                CheckNewDay(now);
                ProcessClosedBars(m);
                CheckDailyLoss();
                ManageMarketPositions(m);
                EvaluateEntries(m);
            }
            catch (Exception ex)
            {
                LogError(m.Name + " tick", ex);
            }
        }

        #endregion

        #region History and AI training

        private int WarmupBars()
        {
            int indicators = Math.Max(EmaPeriod, Math.Max(RsiPeriod, AtrPeriod)) * IndicatorWarmupFactor + RsiSlopeBars;
            int patterns = Math.Max(SqueezeBars, SweepLookbackBars + 1);
            return Math.Max(Math.Max(indicators, patterns), TickBaselineBars) + 1;
        }

        private void LoadHistory(Market m)
        {
            int needSig = AiTrainingBars + WarmupBars() + 2;
            EnsureBars(m.Sig, needSig, m.Name + " " + m.SigTf.ShortName);
            if (!m.SameTf)
            {
                // Labels are simulated on the ATR timeframe, so it has to cover the same span of time.
                int needRisk = (int)Math.Ceiling(needSig * m.SigSec / m.RiskSec) + AtrPeriod * IndicatorWarmupFactor + AiHorizonBars + 2;
                EnsureBars(m.Risk, needRisk, m.Name + " " + m.RiskTf.ShortName);
            }
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
                Log("HISTORY: " + what + " has " + bars.Count + " bars, wanted " + needed + " - the AI trains on what is available and keeps learning online");
        }

        /// <summary>
        /// Spread used to label training samples: the fixed UI value, otherwise the median of recent in-session
        /// spreads (robust to rollover and news spikes), otherwise the spread of the moment.
        /// </summary>
        private double TrainingSpread(Market m)
        {
            if (AiTrainSpreadPips > 0)
                return AiTrainSpreadPips * m.BotPip;
            if (m.Spreads != null && m.Spreads.Count > 0)
                return m.Spreads.Median();
            return Math.Max(m.Symbol.Spread, 0.0);
        }

        /// <summary>
        /// The start-up bootstrap only knows the spread of the moment the cBot was launched, which is stale or
        /// wide at weekends and rollover. Once enough in-session ticks were seen, the model is rebuilt from
        /// history with the live median spread; until then AI-filtered entries of this symbol wait.
        /// </summary>
        private void CheckSpreadCalibration(Market m)
        {
            if (m.SpreadChecked || m.Spreads.Count < SpreadCalibrationTicks)
                return;
            m.SpreadChecked = true;
            double live = m.Spreads.Median();
            CheckSpreadFitsStrategy(m, live);
            if (AiTrainSpreadPips > 0)
                return;
            m.SpreadCalibrated = true;
            LogM(m, "AI RETRAIN: live spread median " + D(m, live) + " over " + m.Spreads.Count + " ticks (start-up used " + D(m, m.BootstrapSpread) + ")");
            m.Ai = new GaussianNaiveBayes(AiFeatures.Dim, AiTrainingBars * 2);
            m.Pending.Clear();
            TrainFromHistory(m);
        }

        /// <summary>
        /// A typical spread above the symbol's Max Spread / ATR x typical ATR means nearly every signal will be
        /// skipped - on Forex that is the signature of a mark-up (Standard) account - so it is said once, loudly.
        /// </summary>
        private void CheckSpreadFitsStrategy(Market m, double typicalSpread)
        {
            double typicalAtr = MeanRiskAtr(m, TypicalAtrBars);
            if (!(typicalAtr > 0) || !(typicalSpread >= 0))
                return;
            double limit = m.MaxSpreadToAtr * typicalAtr;
            if (typicalSpread <= limit)
            {
                LogM(m, "SPREAD CHECK OK: typical spread " + D(m, typicalSpread) + " <= " + Pct(m.MaxSpreadToAtr) + " of the typical ATR " + D(m, typicalAtr)
                     + " (" + D(m, limit) + ")");
                return;
            }
            m.SpreadWarning = true;
            string hint;
            if (m.IsCrypto)
                hint = "Crypto spreads are wide against " + m.SigTf.ShortName + " moves; raise 'Crypto Max Spread / ATR' or use a higher crypto timeframe to trade it more often.";
            else if (_isPepperstone)
                hint = "This looks like a Pepperstone Standard account (1 pip mark-up); the strategy is built for a Razor account (raw spread + commission).";
            else
                hint = "The strategy is built for a raw-spread (ECN) account with commission.";
            LogM(m, "WARNING: typical spread " + D(m, typicalSpread) + " is above " + Pct(m.MaxSpreadToAtr) + " of the typical ATR " + D(m, typicalAtr)
                 + " (" + D(m, limit) + "), so most signals will be skipped as 'Spread too high'. " + hint);
        }

        private double MeanRiskAtr(Market m, int bars)
        {
            int last = m.Risk.Count - 2;
            double sum = 0.0;
            int n = 0;
            for (int i = last; i >= 0 && n < bars; i--)
            {
                double v = m.AtrRisk.Result[i];
                if (Valid(v) && v > 0)
                {
                    sum += v;
                    n++;
                }
            }
            return n > 0 ? sum / n : 0.0;
        }

        private void TrainFromHistory(Market m)
        {
            int lastClosed = m.Sig.Count - 2;
            int first = Math.Max(WarmupBars(), lastClosed - AiTrainingBars + 1);
            double spread = TrainingSpread(m);
            m.BootstrapSpread = spread;
            int bars = 0;
            for (int c = first; c <= lastClosed; c++)
            {
                PendingSample s = BuildSample(m, c, spread);
                if (s == null)
                    continue;
                bars++;
                if (!TryResolveSample(m, s))
                    m.Pending.Add(s);
            }
            m.AiWasReady = m.Ai.IsReady(AiMinSamplesPerClass);
            LogM(m, "AI: " + bars + " " + m.SigTf.ShortName + " bars -> " + m.Ai.Count + " samples, base win-rate " + Pct(m.Ai.WinRate) + ", label spread "
                 + D(m, spread) + " | " + AiStateText(m));
        }

        private string AiStateText(Market m)
        {
            if (!m.Ai.IsReady(AiMinSamplesPerClass))
                return "warming up: wins " + m.Ai.Wins + ", losses " + m.Ai.Losses + " (need " + AiMinSamplesPerClass + " each)"
                       + (UseAiFilter ? " - entries wait" : " - AI filter OFF");
            var sb = new StringBuilder("ready, class means win/loss:");
            for (int f = 0; f < AiFeatures.Dim; f++)
            {
                sb.Append(' ').Append(AiFeatures.Names[f]).Append(' ')
                  .Append(F(m.Ai.ClassMean(true, f), 3)).Append('/').Append(F(m.Ai.ClassMean(false, f), 3));
            }
            return sb.ToString();
        }

        /// <summary>Features and replay inputs for the hypothetical entry at the close of signal bar c.</summary>
        private PendingSample BuildSample(Market m, int c, double spread)
        {
            if (c < WarmupBars() || c > m.Sig.Count - 2 || c - RsiSlopeBars < 0)
                return null;

            double atrS = m.AtrSig.Result[c];
            double ema = m.Ema.Result[c];
            double rsiNow = m.Rsi.Result[c];
            double rsiPrev = m.Rsi.Result[c - RsiSlopeBars];
            double close = m.Sig.ClosePrices[c];
            if (!Valid(atrS) || atrS <= 0 || !Valid(ema) || !Valid(rsiNow) || !Valid(rsiPrev))
                return null;

            DateTime closeTime = m.Sig.OpenTimes[c].AddSeconds(m.SigSec);
            int r = LastIndexBefore(m.Risk, closeTime);
            if (r < 0 || r > m.Risk.Count - 2)
                return null;
            double atrR = m.AtrRisk.Result[r];
            if (!Valid(atrR) || atrR <= 0)
                return null;

            // History has no intrabar tick timing, so the classifier's velocity feature is the bar's tick
            // volume against the average of the previous N bars. Live, the same quantity is measured over a
            // rolling window of one bar length (see BarTickVelocity), keeping training and inference aligned.
            double baseline = MeanTickVolume(m.Sig, c - 1, TickBaselineBars);
            if (baseline <= 0)
                return null;
            double tv = m.Sig.TickVolumes[c] / baseline;

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
        private bool TryResolveSample(Market m, PendingSample s)
        {
            if (m.Risk.Count < 2 || m.Risk.OpenTimes[0] > s.CloseTime)
                return true;
            int j0 = FirstIndexAtOrAfter(m.Risk, s.CloseTime);
            if (j0 < 0)
                return false;
            int jEnd = j0 + AiHorizonBars - 1;
            if (jEnd > m.Risk.Count - 2)
                return false;

            for (int k = 0; k < AiHorizonBars; k++)
            {
                m.HBuf[k] = m.Risk.HighPrices[j0 + k];
                m.LBuf[k] = m.Risk.LowPrices[j0 + k];
            }
            double sl = SlAtrMultiplier * s.AtrRisk;
            double tp = Tp1AtrMultiplier * s.AtrRisk;
            double beTrigger = BeTriggerR > 0 ? BeTriggerR * sl : 0.0;
            double beOffset = BeOffsetPips * m.BotPip;
            bool longWin = OutcomeSimulator.Tp1First(true, s.EntryBid, s.Spread, sl, tp, beTrigger, beOffset, m.HBuf, m.LBuf, AiHorizonBars);
            bool shortWin = OutcomeSimulator.Tp1First(false, s.EntryBid, s.Spread, sl, tp, beTrigger, beOffset, m.HBuf, m.LBuf, AiHorizonBars);
            m.Ai.Add(s.LongX, longWin);
            m.Ai.Add(s.ShortX, shortWin);
            return true;
        }

        private void ResolvePending(Market m)
        {
            for (int i = m.Pending.Count - 1; i >= 0; i--)
            {
                if (TryResolveSample(m, m.Pending[i]))
                    m.Pending.RemoveAt(i);
            }
            bool ready = m.Ai.IsReady(AiMinSamplesPerClass);
            if (ready != m.AiWasReady)
            {
                m.AiWasReady = ready;
                LogM(m, "AI " + AiStateText(m));
            }
        }

        #endregion

        #region Bar processing and setups

        private void ProcessClosedBars(Market m)
        {
            int rLast = m.Risk.Count - 2;
            if (rLast >= 0 && m.Risk.OpenTimes[rLast] != m.LastRiskClosed)
            {
                m.LastRiskClosed = m.Risk.OpenTimes[rLast];
                ResolvePending(m);
            }

            int sLast = m.Sig.Count - 2;
            if (sLast >= 0 && m.Sig.OpenTimes[sLast] != m.LastSigClosed)
            {
                // Normally exactly one bar closed; after a reconnect several may have, and each feeds the AI.
                int from = sLast;
                int prev = IndexOfTime(m.Sig, m.LastSigClosed);
                if (prev >= 0 && prev < sLast)
                    from = prev + 1;
                for (int c = from; c <= sLast; c++)
                    OnSignalBarClosed(m, c, c == sLast);
                m.LastSigClosed = m.Sig.OpenTimes[sLast];
            }
        }

        private void OnSignalBarClosed(Market m, int c, bool latest)
        {
            PendingSample s = BuildSample(m, c, TrainingSpread(m));
            if (s != null && !TryResolveSample(m, s))
                m.Pending.Add(s);
            if (!latest)
                return;

            m.SkipKeys.Clear();
            m.BaselineTicksPerBar = MeanTickVolume(m.Sig, c, TickBaselineBars);
            EvaluateSetups(m, c);
            if (LogBarSummary)
                LogBar(m, c, s);
            if (m.IsChart)
                DrawSetups(m);
        }

        /// <summary>Arms or disarms the squeeze and sweep setups from the bar that just closed.</summary>
        private void EvaluateSetups(Market m, int c)
        {
            double atrS = m.AtrSig.Result[c];
            double ema = m.Ema.Result[c];
            double buffer = BreakoutBufferPips * m.BotPip;
            m.SetupAtr = atrS;
            m.ReclaimEma = ema;
            m.SqzArmed = false;

            if (!Valid(atrS) || atrS <= 0 || !Valid(ema))
            {
                m.BullSweep = false;
                m.BearSweep = false;
                m.SqzNote = "indicators warming up";
                m.SwpIdle = "indicators warming up";
                m.SwpNote = m.SwpIdle;
                return;
            }

            // ---- 20 EMA squeeze -------------------------------------------------------------------------
            if (!UseSqueeze)
            {
                m.SqzNote = "off";
            }
            else if (c - SqueezeBars + 1 < 0)
            {
                m.SqzNote = "not enough bars";
            }
            else
            {
                int start = c - SqueezeBars + 1;
                for (int i = 0; i < SqueezeBars; i++)
                {
                    m.WinH[i] = m.Sig.HighPrices[start + i];
                    m.WinL[i] = m.Sig.LowPrices[start + i];
                }
                SqueezeCheck q = VolmanSetups.CheckSqueeze(m.WinH, m.WinL, SqueezeBars, ema, atrS, SqueezeMaxRangeAtr, SqueezeMaxEmaGapAtr);
                if (q.Ok)
                {
                    m.SqzArmed = true;
                    m.StatusSqz++;
                    m.SqzHigh = q.BoxHigh;
                    m.SqzLow = q.BoxLow;
                    m.SqzStart = m.Sig.OpenTimes[start];
                    m.SqzNote = "ARMED " + P(m, q.BoxLow) + "-" + P(m, q.BoxHigh) + " (" + D(m, q.Height) + "), BUY>=" + P(m, q.BoxHigh + buffer)
                                + " SELL<=" + P(m, q.BoxLow - buffer);
                }
                else if (!q.RangeOk)
                {
                    m.SqzNote = "no: box " + D(m, q.Height) + " > max " + D(m, q.MaxHeight) + " (" + F(SqueezeMaxRangeAtr, 2) + " ATR)";
                }
                else
                {
                    m.SqzNote = "no: EMA " + D(m, q.EmaGap) + " from box > max " + D(m, q.MaxGap) + " (" + F(SqueezeMaxEmaGapAtr, 2) + " ATR)";
                }
            }

            // ---- Liquidity sweep ------------------------------------------------------------------------
            if (!UseSweep)
            {
                m.BullSweep = false;
                m.BearSweep = false;
                m.SwpIdle = "off";
            }
            else if (c - SweepLookbackBars < 0)
            {
                m.SwpIdle = "not enough bars";
            }
            else
            {
                double priorLow = double.MaxValue;
                double priorHigh = double.MinValue;
                for (int i = c - SweepLookbackBars; i < c; i++)
                {
                    priorLow = Math.Min(priorLow, m.Sig.LowPrices[i]);
                    priorHigh = Math.Max(priorHigh, m.Sig.HighPrices[i]);
                }
                double lo = m.Sig.LowPrices[c];
                double hi = m.Sig.HighPrices[c];
                double cl = m.Sig.ClosePrices[c];
                double minPierce = SweepMinPiercePips * m.BotPip;

                if (m.BullSweep)
                {
                    if (lo < m.BullExtreme)
                    {
                        m.BullSweep = false;
                    }
                    else if (--m.BullBarsLeft <= 0)
                    {
                        m.BullSweep = false;
                        LogM(m, "SWEEP BULL expired: no reclaim of the EMA within " + SweepMaxBarsToReclaim + " bars");
                    }
                }
                if (m.BearSweep)
                {
                    if (hi > m.BearExtreme)
                    {
                        m.BearSweep = false;
                    }
                    else if (--m.BearBarsLeft <= 0)
                    {
                        m.BearSweep = false;
                        LogM(m, "SWEEP BEAR expired: no loss of the EMA within " + SweepMaxBarsToReclaim + " bars");
                    }
                }

                if (SweepDetector.IsBullSweep(priorLow, lo, cl, minPierce, SweepRequireCloseInside))
                {
                    m.BullSweep = true;
                    m.StatusSwp++;
                    m.BullExtreme = lo;
                    m.BullBarsLeft = SweepMaxBarsToReclaim;
                    m.BullTime = m.Sig.OpenTimes[c];
                    LogM(m, "SWEEP BULL: " + SweepLookbackBars + "-bar low " + P(m, priorLow) + " pierced to " + P(m, lo) + " (" + D(m, priorLow - lo)
                         + "), close " + P(m, cl) + " -> BUY on reclaim of EMA >= " + P(m, ema + buffer) + " within " + SweepMaxBarsToReclaim + " bars");
                }
                if (SweepDetector.IsBearSweep(priorHigh, hi, cl, minPierce, SweepRequireCloseInside))
                {
                    m.BearSweep = true;
                    m.StatusSwp++;
                    m.BearExtreme = hi;
                    m.BearBarsLeft = SweepMaxBarsToReclaim;
                    m.BearTime = m.Sig.OpenTimes[c];
                    LogM(m, "SWEEP BEAR: " + SweepLookbackBars + "-bar high " + P(m, priorHigh) + " pierced to " + P(m, hi) + " (" + D(m, hi - priorHigh)
                         + "), close " + P(m, cl) + " -> SELL on loss of EMA <= " + P(m, ema - buffer) + " within " + SweepMaxBarsToReclaim + " bars");
                }

                bool piercedLow = lo < priorLow;
                bool piercedHigh = hi > priorHigh;
                double pierce = Math.Max(piercedLow ? priorLow - lo : 0.0, piercedHigh ? hi - priorHigh : 0.0);
                if (!piercedLow && !piercedHigh)
                    m.SwpIdle = "no: " + SweepLookbackBars + "-bar H " + P(m, priorHigh) + " / L " + P(m, priorLow) + " not pierced";
                else if (pierce < minPierce)
                    m.SwpIdle = "no: pierce " + D(m, pierce) + " < min " + D(m, minPierce);
                else
                    m.SwpIdle = "no: pierce " + D(m, pierce) + " but the bar closed beyond the swept level";
            }
            m.SwpNote = SweepNote(m);
        }

        private string SweepNote(Market m)
        {
            if (!UseSweep)
                return "off";
            double buffer = BreakoutBufferPips * m.BotPip;
            var parts = new List<string>(2);
            if (m.BullSweep)
                parts.Add("BULL armed, wick " + P(m, m.BullExtreme) + ", BUY>=" + P(m, m.ReclaimEma + buffer) + " (" + m.BullBarsLeft + " bars left)");
            if (m.BearSweep)
                parts.Add("BEAR armed, wick " + P(m, m.BearExtreme) + ", SELL<=" + P(m, m.ReclaimEma - buffer) + " (" + m.BearBarsLeft + " bars left)");
            return parts.Count > 0 ? string.Join(" & ", parts) : m.SwpIdle;
        }

        private void LogBar(Market m, int c, PendingSample s)
        {
            double atrR = RiskAtr(m);
            double spread = m.Symbol.Spread;
            bool armed = m.SqzArmed || m.BullSweep || m.BearSweep;
            var sb = new StringBuilder(256);
            sb.Append("BAR ").Append(m.Sig.OpenTimes[c].ToString("HH:mm", Inv)).Append(armed ? " ARMED" : " NO SETUP");
            sb.Append(" | C ").Append(P(m, m.Sig.ClosePrices[c])).Append(" EMA ").Append(P(m, m.Ema.Result[c]));
            sb.Append(" ATR ").Append(D(m, atrR));
            if (!m.SameTf)
                sb.Append(" (sig ").Append(D(m, m.AtrSig.Result[c])).Append(')');
            sb.Append(" | Spread ").Append(D(m, spread)).Append(" = ").Append(atrR > 0 ? Pct(spread / atrR) : "n/a")
              .Append(" ATR (max ").Append(Pct(m.MaxSpreadToAtr)).Append(')');
            if (s != null)
                sb.Append(" | TV bar ").Append(F(Math.Exp(s.LongX[0]), 2)).Append('x');
            sb.Append(" | SQZ ").Append(m.SqzNote).Append(" | SWP ").Append(m.SwpNote);
            if (s != null)
            {
                if (m.Ai.IsReady(AiMinSamplesPerClass))
                {
                    sb.Append(" | AI L ").Append(Pct(m.Ai.Predict(s.LongX, AiUseEmpiricalPrior, null)))
                      .Append(" S ").Append(Pct(m.Ai.Predict(s.ShortX, AiUseEmpiricalPrior, null)));
                }
                else
                {
                    sb.Append(" | AI warming ").Append(m.Ai.Wins).Append('/').Append(m.Ai.Losses);
                }
            }
            LogM(m, sb.ToString());
        }

        #endregion

        #region Entries

        private void EvaluateEntries(Market m)
        {
            if (!m.SqzArmed && !m.BullSweep && !m.BearSweep)
                return;
            int f = m.Sig.Count - 1;
            if (f < 1)
                return;

            double bid = m.Symbol.Bid;
            double buffer = BreakoutBufferPips * m.BotPip;

            // A new extreme beyond the swept wick means the pierce was not false after all.
            if (m.BullSweep && m.Sig.LowPrices[f] < m.BullExtreme)
            {
                m.BullSweep = false;
                LogM(m, "SWEEP BULL cancelled: new low " + P(m, m.Sig.LowPrices[f]) + " below the swept wick " + P(m, m.BullExtreme));
                m.SwpNote = SweepNote(m);
            }
            if (m.BearSweep && m.Sig.HighPrices[f] > m.BearExtreme)
            {
                m.BearSweep = false;
                LogM(m, "SWEEP BEAR cancelled: new high " + P(m, m.Sig.HighPrices[f]) + " above the swept wick " + P(m, m.BearExtreme));
                m.SwpNote = SweepNote(m);
            }

            if (m.SqzArmed)
            {
                double up = m.SqzHigh + buffer;
                double dn = m.SqzLow - buffer;
                if (bid >= up)
                {
                    if (ConsiderEntry(m, "SQZ", TradeType.Buy, up, bid - up))
                        return;
                }
                else if (bid <= dn)
                {
                    if (ConsiderEntry(m, "SQZ", TradeType.Sell, dn, dn - bid))
                        return;
                }
            }
            if (m.BullSweep)
            {
                double level = m.ReclaimEma + buffer;
                if (bid >= level && ConsiderEntry(m, "SWP", TradeType.Buy, level, bid - level))
                    return;
            }
            if (m.BearSweep)
            {
                double level = m.ReclaimEma - buffer;
                if (bid <= level && ConsiderEntry(m, "SWP", TradeType.Sell, level, level - bid))
                    return;
            }
        }

        /// <summary>Runs every filter on a triggered setup, logs all failing reasons, or opens the trade.</summary>
        private bool ConsiderEntry(Market m, string setup, TradeType type, double level, double chase)
        {
            int dir = type == TradeType.Buy ? 1 : -1;
            string tag = setup + " " + (dir > 0 ? "BUY" : "SELL");
            var reasons = new List<string>(8);
            var codes = new StringBuilder(32);

            AddGlobalGates(m, reasons, codes);

            double atrR = RiskAtr(m);
            double maxChase = MaxChaseAtr * m.SetupAtr;
            if (chase > maxChase)
                AddReason(reasons, codes, "CHASE", "Price already " + D(m, chase) + " past trigger > max " + D(m, maxChase) + " (" + F(MaxChaseAtr, 2) + " ATR)");

            double spread = m.Symbol.Spread;
            double spreadRatio = atrR > 0 ? spread / atrR : double.PositiveInfinity;
            if (!(spreadRatio <= m.MaxSpreadToAtr))
            {
                AddReason(reasons, codes, "SPREAD", "Spread too high (" + D(m, spread) + " = " + (atrR > 0 ? Pct(spreadRatio) : "n/a")
                          + " of ATR " + D(m, atrR) + " > " + Pct(m.MaxSpreadToAtr) + ")");
            }

            double burst = BurstRatio(m);
            if (UseTickVelocity && burst < TickVelocityMultiplier)
            {
                AddReason(reasons, codes, "TV", "Low Tick Velocity (" + F(burst, 2) + "x < " + F(TickVelocityMultiplier, 2) + "x in "
                          + F(TickWindowSeconds, 1) + "s)");
            }

            double conf = double.NaN;
            string aiInfo = "";
            if (UseAiFilter)
            {
                if (AiTrainSpreadPips <= 0 && !m.SpreadCalibrated)
                {
                    AddReason(reasons, codes, "AISPR", "AI calibrating on the live spread (" + m.Spreads.Count + "/" + SpreadCalibrationTicks + " ticks)");
                }
                else if (!m.Ai.IsReady(AiMinSamplesPerClass))
                {
                    AddReason(reasons, codes, "AIWARM", "AI warming up (wins " + m.Ai.Wins + "/" + AiMinSamplesPerClass + ", losses "
                              + m.Ai.Losses + "/" + AiMinSamplesPerClass + ")");
                }
                else
                {
                    double[] x = LiveFeatures(m, dir);
                    if (x == null)
                    {
                        AddReason(reasons, codes, "AINA", "AI features unavailable (indicators warming up)");
                    }
                    else
                    {
                        conf = m.Ai.Predict(x, AiUseEmpiricalPrior, m.Contrib);
                        m.LastConf = conf;
                        aiInfo = FormatContributions(m.Contrib);
                        if (!(conf >= MinConfidence))
                            AddReason(reasons, codes, "AI", "AI Confidence " + Pct(conf) + " < Target " + Pct(MinConfidence) + " " + aiInfo);
                    }
                }
            }

            RecordSignal(m, SignalKey(m, tag), codes.ToString(), conf);
            if (reasons.Count > 0)
            {
                LogSkip(m, tag, level, codes.ToString(), reasons);
                return false;
            }
            return OpenTrade(m, setup, type, level, conf, burst, aiInfo);
        }

        private void AddGlobalGates(Market m, List<string> reasons, StringBuilder codes)
        {
            DateTime now = Server.Time;
            if (_dailyHalt)
                AddReason(reasons, codes, "DAY", "Daily loss limit " + F(DailyMaxLossPercent, 1) + "% reached - halted until 00:00 UTC");
            if (MaxTradesPerDay > 0 && _tradesToday >= MaxTradesPerDay)
                AddReason(reasons, codes, "MAXTR", "Max trades per day reached (" + MaxTradesPerDay + ")");
            if (Positions.FindAll(BotLabel, m.Name).Length > 0)
            {
                AddReason(reasons, codes, "POS", "Position already open on " + m.Name);
            }
            else
            {
                int open = Positions.FindAll(BotLabel).Length;
                if (open >= MaxOpenPositions)
                    AddReason(reasons, codes, "MAXPOS", "Max open positions reached (" + open + "/" + MaxOpenPositions + ")");
            }
            if (now < m.PauseUntil)
                AddReason(reasons, codes, "STREAK", "Loss-streak pause on " + m.Name + " until " + m.PauseUntil.ToString("HH:mm", Inv) + " UTC");
            if (CooldownSeconds > 0 && m.LastCloseTime != DateTime.MinValue)
            {
                double since = (now - m.LastCloseTime).TotalSeconds;
                if (since < CooldownSeconds)
                    AddReason(reasons, codes, "COOL", "Cooldown after close (" + F(CooldownSeconds - since, 0) + "s left)");
            }
            if (!InSessionFor(m, now))
            {
                AddReason(reasons, codes, "SESSION", "Outside session " + SessionStartHour.ToString("00", Inv) + ":00-"
                          + SessionEndHour.ToString("00", Inv) + ":00 UTC");
            }
            if (!m.Symbol.MarketHours.IsOpened())
                AddReason(reasons, codes, "CLOSED", "Market closed");
            else if (m.Symbol.TradingMode != SymbolTradingMode.FullAccess)
                AddReason(reasons, codes, "TMODE", "Broker trading mode " + m.Symbol.TradingMode);
            double need = RequiredWarmupSeconds(m);
            double ran = (now - m.EngineStart).TotalSeconds;
            if (ran < need)
                AddReason(reasons, codes, "WARM", "Tick engine warming up (" + F(ran, 0) + "/" + F(need, 0) + " s)");
        }

        private double RequiredWarmupSeconds(Market m)
        {
            if (UseAiFilter)
                return Math.Max(TickWindowSeconds, m.SigSec);
            return UseTickVelocity ? TickWindowSeconds : 0.0;
        }

        private static void AddReason(List<string> reasons, StringBuilder codes, string code, string text)
        {
            reasons.Add(text);
            codes.Append(code).Append(',');
        }

        private string SignalKey(Market m, string tag)
        {
            return tag + "|" + m.Sig.OpenTimes[m.Sig.Count - 1].Ticks.ToString(Inv);
        }

        private void LogSkip(Market m, string tag, double level, string codes, List<string> reasons)
        {
            m.LastSkip = "SKIP [" + tag + "] " + string.Join(" | ", reasons);
            if (!LogSkipReasons)
                return;
            // One line per setup, direction and combination of reasons per bar: detailed, never a flood.
            if (!m.SkipKeys.Add(tag + "|" + codes))
                return;
            LogM(m, "SKIP [" + tag + " @" + P(m, level) + "]: " + string.Join(" | ", reasons));
        }

        private bool OpenTrade(Market m, string setup, TradeType type, double level, double conf, double burst, string aiInfo)
        {
            string tag = setup + " " + (type == TradeType.Buy ? "BUY" : "SELL");
            Symbol sym = m.Symbol;
            double pip = sym.PipSize;
            double atrR = RiskAtr(m);
            double slPips = Math.Round(SlAtrMultiplier * atrR / pip, 1);
            double tp1Pips = Math.Round(Tp1AtrMultiplier * atrR / pip, 1);
            if (slPips <= 0 || tp1Pips <= 0)
            {
                RecordSignal(m, SignalKey(m, tag), "DIST");
                LogSkip(m, tag, level, "DIST", new List<string> { "SL/TP distance rounds to zero (ATR " + D(m, atrR) + ")" });
                return false;
            }
            double minStopPips = BrokerMinDistancePips(m, true);
            if (minStopPips > 0 && slPips < minStopPips - 1e-9)
            {
                RecordSignal(m, SignalKey(m, tag), "MINSL");
                LogSkip(m, tag, level, "MINSL", new List<string> { "SL " + D(m, slPips * pip) + " is closer than the broker minimum stop distance "
                                                                    + D(m, minStopPips * pip) });
                return false;
            }

            string sizing;
            double volume = ComputeVolume(m, type, slPips, out sizing);
            if (volume <= 0)
            {
                RecordSignal(m, SignalKey(m, tag), "SIZE");
                LogSkip(m, tag, level, "SIZE", new List<string> { sizing });
                return false;
            }

            TradeResult result = null;
            SplitPlan plan = new SplitPlan();
            bool closeAllAtTp1 = false;
            for (int attempt = 0; ; attempt++)
            {
                plan = VolumeMath.PlanSplit(volume, Tp1ClosePercent, sym.VolumeInUnitsMin, sym.VolumeInUnitsStep);
                closeAllAtTp1 = !plan.Splittable && (Tp1ClosePercent >= 100.0 || NoSplitMode == NoSplitAction.CloseAllAtTp1);
                double? tpPips = null;
                if (closeAllAtTp1)
                    tpPips = tp1Pips;
                else if (Tp2AtrMultiplier > 0)
                    tpPips = Math.Round(Tp2AtrMultiplier * atrR / pip, 1);
                double minTpPips = BrokerMinDistancePips(m, false);
                if (tpPips.HasValue && minTpPips > 0 && tpPips.Value < minTpPips - 1e-9)
                    tpPips = null;   // too close for a server-side TP: the bot closes at the target itself

                string comment = "QAI|" + setup + "|c=" + (double.IsNaN(conf) ? "na" : F(conf, 2)) + "|v=" + volume.ToString("R", Inv)
                                 + "|s=" + F(slPips, 1) + "|t=" + F(tp1Pips, 1);
                result = SendMarketOrder(m, type, volume, slPips, tpPips, comment);
                if (result.IsSuccessful || result.Error != ErrorCode.NoMoney || attempt >= MaxNoMoneyRetries)
                    break;
                double smaller = VolumeMath.FloorToStep(volume / 2.0, sym.VolumeInUnitsStep);
                if (smaller < sym.VolumeInUnitsMin - VolumeEpsilon)
                    break;
                LogM(m, "ORDER [" + tag + "]: not enough money for " + Lots(m, volume) + " -> retry with " + Lots(m, smaller));
                volume = smaller;
            }

            if (result == null || !result.IsSuccessful || result.Position == null)
            {
                // The setup is dropped so a rejecting broker is not hit again on every tick of the bar.
                RecordSignal(m, SignalKey(m, tag), "ORDER");
                ConsumeSetup(m, setup, type);
                LogM(m, "ORDER FAILED [" + tag + "]: " + (result != null && result.Error.HasValue ? result.Error.Value.ToString() : "no position returned")
                     + " - setup dropped | " + sizing);
                return false;
            }

            Position p = result.Position;
            var st = new TradeState();
            st.PositionId = p.Id;
            st.SymbolName = m.Name;
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
            m.TradesToday++;
            m.StatusTrades++;
            ConsumeSetup(m, setup, type);

            // Never keep a position without a stop: attach it if the fill came back without one, else flatten.
            if (!p.StopLoss.HasValue)
            {
                double slPrice = Math.Round(st.IsLong ? p.EntryPrice - slPips * pip : p.EntryPrice + slPips * pip, sym.Digits);
                TradeResult fix = p.ModifyStopLossPrice(slPrice);
                if (!fix.IsSuccessful)
                {
                    st.CloseReason = "NO STOP";
                    LogM(m, "PROTECTION #" + p.Id + ": stop loss could not be attached (" + fix.Error + ") -> closing the position");
                    ClosePosition(p);
                    return true;
                }
                st.RiskDist = Math.Abs(p.EntryPrice - slPrice);
                LogM(m, "PROTECTION #" + p.Id + ": fill came back without a stop -> SL attached at " + P(m, slPrice));
            }

            string commissionNote = "";
            if (p.VolumeInUnits > 0 && Math.Abs(p.Commissions) > 0)
            {
                m.CommissionPerUnit = 2.0 * Math.Abs(p.Commissions) / p.VolumeInUnits;
                m.CommissionSource = "last fill";
                commissionNote = " | commission " + F(2.0 * Math.Abs(p.Commissions), 2) + " " + _ccy + " round turn (" + CommissionText(m) + ")";
            }

            string tp1Text = plan.Splittable
                ? Lots(m, plan.PartialVolume) + " (" + F(plan.PartialVolume / st.InitialVolume * 100.0, 0) + "%), runner " + Lots(m, plan.RemainingVolume)
                : (closeAllAtTp1 ? "100% (volume cannot be split)" : "none closed - whole position trails (volume cannot be split)");
            _lastEvent = m.Name + " ENTRY " + tag + " " + Lots(m, p.VolumeInUnits) + " @" + P(m, p.EntryPrice);
            LogM(m, "ENTRY " + tag + " #" + p.Id + " " + Lots(m, p.VolumeInUnits) + " @" + P(m, p.EntryPrice) + " | SL " + P(m, p.StopLoss) + " ("
                 + D(m, st.RiskDist) + " = " + F(SlAtrMultiplier, 2) + " ATR) | TP1 " + P(m, TargetPrice(p, st.Tp1Dist)) + " (" + D(m, st.Tp1Dist) + ") closes "
                 + tp1Text + " | conf " + (double.IsNaN(conf) ? "n/a (AI off)" : Pct(conf) + " " + aiInfo) + " | TV " + F(burst, 2) + "x | " + sizing
                 + commissionNote + " | open positions " + Positions.FindAll(BotLabel).Length + "/" + MaxOpenPositions);
            if (m.IsChart)
                DrawEntry(m, p);
            return true;
        }

        private TradeResult SendMarketOrder(Market m, TradeType type, double volume, double slPips, double? tpPips, string comment)
        {
            if (MaxSlippagePips > 0)
            {
                // The allowed slippage is never tighter than the spread itself (matters on crypto).
                double range = Math.Max(MaxSlippagePips * m.BotPip, m.Symbol.Spread) / m.Symbol.PipSize;
                double basePrice = type == TradeType.Buy ? m.Symbol.Ask : m.Symbol.Bid;
                return ExecuteMarketRangeOrder(type, m.Name, volume, range, basePrice, BotLabel, slPips, tpPips, comment);
            }
            return ExecuteMarketOrder(type, m.Name, volume, BotLabel, slPips, tpPips, comment);
        }

        private void ConsumeSetup(Market m, string setup, TradeType type)
        {
            if (setup == "SQZ")
            {
                m.SqzArmed = false;
                m.SqzNote = "used";
            }
            else if (type == TradeType.Buy)
            {
                m.BullSweep = false;
            }
            else
            {
                m.BearSweep = false;
            }
            m.SwpNote = SweepNote(m);
        }

        /// <summary>
        /// Volume in units: Fixed Lots, or the size whose stop-loss plus round-turn commission equals Risk Percent
        /// of the balance; then bounded by the lot cap/floor, the broker maximum and the margin rules.
        /// </summary>
        private double ComputeVolume(Market m, TradeType type, double slPips, out string note)
        {
            Symbol sym = m.Symbol;
            double balance = Account.Balance;
            double pipValue = sym.PipValue;   // account currency per broker pip for one unit
            double step = sym.VolumeInUnitsStep;
            double vMin = sym.VolumeInUnitsMin;
            if (pipValue <= 0 || slPips <= 0 || balance <= 0 || step <= 0)
            {
                note = "Sizing impossible (balance " + F(balance, 2) + ", pip value " + pipValue.ToString("G6", Inv) + ")";
                return 0.0;
            }

            double lossPerUnit = slPips * pipValue + m.CommissionPerUnit;
            var sb = new StringBuilder(220);
            double raw;
            if (FixedLots > 0)
            {
                raw = sym.QuantityToVolumeInUnits(FixedLots);
                sb.Append("size: fixed ").Append(F(FixedLots, 2)).Append(" lot = ").Append(Units(raw)).Append('u');
            }
            else
            {
                double riskMoney = balance * RiskPercent / 100.0;
                raw = riskMoney / lossPerUnit;
                sb.Append("size: risk ").Append(F(riskMoney, 2)).Append(' ').Append(_ccy).Append(" / (SL ").Append(D(m, slPips * sym.PipSize))
                  .Append(" + commission) = ").Append(Units(raw)).Append('u');
            }

            double units = VolumeMath.FloorToStep(raw, step);
            double cap = Math.Min(sym.VolumeInUnitsMax, sym.QuantityToVolumeInUnits(MaxLots));
            if (units > cap + VolumeEpsilon)
            {
                units = VolumeMath.FloorToStep(cap, step);
                sb.Append(" -> Max Lots / broker cap");
            }
            double floor = Math.Max(vMin, sym.QuantityToVolumeInUnits(MinLots));
            if (units < floor - VolumeEpsilon)
            {
                if (!AllowMinLotOverride)
                {
                    note = sb.Append(" -> below Min Lots ").Append(Lots(m, floor)).Append(" and min-lot override is OFF").ToString();
                    return 0.0;
                }
                units = VolumeMath.CeilToStep(floor, step);
                sb.Append(" -> Min Lots floor");
            }

            double freeMargin = Account.FreeMargin;
            string how = "";
            Func<double, double> marginFor = v => EstimateMargin(m, type, v, out how);
            MarginFit fit = MarketMath.FitMargin(units, vMin, step, freeMargin, Account.Equity, Account.Margin, MarginPerTradePercent,
                                                 MaxTotalMarginPercent, AllowMinLotOverride, marginFor, MarginFitIterations);
            switch (fit.Rule)
            {
                case MarginRule.TotalLimitReached:
                    note = sb.Append(" -> Total margin limit reached: open positions block ").Append(F(Account.Margin, 2)).Append(' ').Append(_ccy)
                             .Append(" of max ").Append(F(MaxTotalMarginPercent, 0)).Append("% of equity ").Append(F(Account.Equity, 2)).Append(' ')
                             .Append(_ccy).ToString();
                    return 0.0;
                case MarginRule.Insufficient:
                    note = sb.Append(" -> Insufficient margin: ").Append(Lots(m, vMin)).Append(" needs ").Append(F(fit.MinNeed, 2)).Append(' ').Append(_ccy)
                             .Append(", available ").Append(F(fit.TotalLeft, 2)).Append(' ').Append(_ccy).Append(" (free margin ").Append(F(freeMargin, 2))
                             .Append(", total limit ").Append(F(MaxTotalMarginPercent, 0)).Append("% of equity")
                             .Append(AllowMinLotOverride ? "" : ", min-lot override OFF").Append(", ").Append(how).Append(')').ToString();
                    return 0.0;
                case MarginRule.ReducedToShare:
                    sb.Append(" -> margin limit ").Append(F(fit.PerTrade, 2)).Append(' ').Append(_ccy).Append(" (").Append(F(MarginPerTradePercent, 0))
                      .Append("% of free margin, all positions <= ").Append(F(MaxTotalMarginPercent, 0)).Append("% of equity, ").Append(how).Append(')');
                    break;
                case MarginRule.MinimumWithinTotal:
                    sb.Append(" -> min volume within the ").Append(F(MaxTotalMarginPercent, 0)).Append("% total margin limit (").Append(how).Append(')');
                    break;
            }
            units = fit.Units;

            double effectiveRisk = units * lossPerUnit;
            sb.Append(" = ").Append(Lots(m, units)).Append(", risk incl. commission ").Append(F(effectiveRisk, 2)).Append(' ').Append(_ccy)
              .Append(" (").Append(F(effectiveRisk / balance * 100.0, 2)).Append("%), margin ").Append(F(EstimateMargin(m, type, units, out how), 2))
              .Append(' ').Append(_ccy);
            note = sb.ToString();
            return units;
        }

        /// <summary>Margin the broker blocks for the volume: the platform's own figure unless a leverage is forced in the UI.</summary>
        private double EstimateMargin(Market m, TradeType type, double units, out string how)
        {
            Symbol sym = m.Symbol;
            if (LeverageOverride <= 0)
            {
                try
                {
                    double margin = sym.GetEstimatedMargin(type, units);
                    if (Valid(margin) && margin > 0)
                    {
                        how = "broker margin";
                        return margin;
                    }
                }
                catch (Exception)
                {
                    // fall back to leverage maths below
                }
            }
            double leverage = EffectiveLeverage(m);
            if (!(leverage > 0))
            {
                how = "no leverage info";
                return 0.0;
            }
            double price = type == TradeType.Buy ? sym.Ask : sym.Bid;
            how = "leverage 1:" + F(leverage, 0);
            return units * price * (sym.PipValue / sym.PipSize) / leverage;
        }

        private double EffectiveLeverage(Market m)
        {
            if (LeverageOverride > 0)
                return LeverageOverride;
            double leverage = Account.PreciseLeverage;
            double symbolLeverage = SymbolLeverage(m);
            if (symbolLeverage > 0 && (leverage <= 0 || symbolLeverage < leverage))
                leverage = symbolLeverage;
            return leverage;
        }

        /// <summary>Leverage of the smallest dynamic-leverage tier, the one that applies to small positions.</summary>
        private static double SymbolLeverage(Market m)
        {
            try
            {
                var tiers = m.Symbol.DynamicLeverage;
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

        /// <summary>Broker minimum distance for a stop (true) or target (false) in broker pips; 0 when there is none.</summary>
        private static double BrokerMinDistancePips(Market m, bool stopLoss)
        {
            try
            {
                Symbol sym = m.Symbol;
                double d = stopLoss ? sym.MinStopLossDistance : sym.MinTakeProfitDistance;
                if (!(d > 0))
                    return 0.0;
                if (sym.MinDistanceType == SymbolMinDistanceType.Pips)
                    return d;
                return sym.Bid * d / 100.0 / sym.PipSize;
            }
            catch (Exception)
            {
                return 0.0;
            }
        }

        /// <summary>Round-turn commission per unit derived from the symbol's commission settings (0 if unknown).</summary>
        private double CommissionEstimatePerUnit(Market m)
        {
            try
            {
                Symbol sym = m.Symbol;
                CommissionBasis basis;
                switch (sym.CommissionType)
                {
                    case SymbolCommissionType.UsdPerMillionUsdVolume:
                        basis = CommissionBasis.UsdPerMillionUsd;
                        break;
                    case SymbolCommissionType.UsdPerOneLot:
                        basis = CommissionBasis.UsdPerLot;
                        break;
                    case SymbolCommissionType.QuoteCurrencyPerOneLot:
                        basis = CommissionBasis.QuotePerLot;
                        break;
                    case SymbolCommissionType.PercentageOfTradingVolume:
                        basis = CommissionBasis.PercentOfVolume;
                        break;
                    default:
                        return 0.0;
                }
                double price = sym.Bid > 0 ? sym.Bid : sym.Ask;
                bool quoteUsd = sym.QuoteAsset != null && sym.QuoteAsset.Name == "USD";
                bool baseUsd = sym.BaseAsset != null && sym.BaseAsset.Name == "USD";
                return CommissionMath.RoundTurnPerUnit(basis, sym.Commission, price, sym.LotSize, sym.PipValue / sym.PipSize,
                                                       quoteUsd, baseUsd, _ccy == "USD");
            }
            catch (Exception)
            {
                return 0.0;
            }
        }

        private string CommissionText(Market m)
        {
            double pipValue = m.Symbol.PipValue;
            if (!(m.CommissionPerUnit > 0) || !(pipValue > 0))
                return "0 (" + m.CommissionSource + ")";
            double perMinVolume = m.CommissionPerUnit * m.Symbol.VolumeInUnitsMin;
            return D(m, m.CommissionPerUnit / pipValue * m.Symbol.PipSize) + " = " + F(perMinVolume, 3) + " " + _ccy + " per min volume (" + m.CommissionSource + ")";
        }

        private double[] LiveFeatures(Market m, int dir)
        {
            int f = m.Sig.Count - 1;
            if (f < 1 || f - RsiSlopeBars < 0)
                return null;
            double atrS = m.AtrSig.Result[f - 1];
            double ema = m.Ema.Result[f];
            double rsiNow = m.Rsi.Result[f];
            double rsiPrev = m.Rsi.Result[f - RsiSlopeBars];
            double atrR = RiskAtr(m);
            if (!Valid(atrS) || atrS <= 0 || !Valid(ema) || !Valid(rsiNow) || !Valid(rsiPrev) || atrR <= 0)
                return null;
            return AiFeatures.Build(BarTickVelocity(m), m.Symbol.Bid - ema, atrS, rsiNow - rsiPrev, m.Symbol.Spread / atrR, dir);
        }

        #endregion

        #region Position management

        private void ManageMarketPositions(Market m)
        {
            Position[] mine = Positions.FindAll(BotLabel, m.Name);
            if (mine.Length == 0)
                return;
            // Requests sent into a closed market only fail; server-side stops keep protecting the position.
            if (!m.Symbol.MarketHours.IsOpened())
                return;
            DateTime now = Server.Time;

            foreach (Position p in mine)
            {
                TradeState st = GetOrRecoverState(m, p);
                if (now < st.RetryAfter)
                    continue;

                bool isLong = p.TradeType == TradeType.Buy;
                double px = isLong ? m.Symbol.Bid : m.Symbol.Ask;
                double fav = isLong ? px - p.EntryPrice : p.EntryPrice - px;
                if (fav > st.MaxFavorable)
                    st.MaxFavorable = fav;

                // 1. TP1: partial close (or full close / hand-over to the trail when the volume cannot be split).
                if (!st.Tp1Done && fav >= st.Tp1Dist - m.Symbol.TickSize * 0.5)
                {
                    if (HandleTp1(m, p, st, px, fav))
                        continue;
                }

                // 2. Time exit: the market did not deliver TP1 within N bars of the ATR timeframe.
                if (TimeExitBars > 0 && !st.Tp1Done && (now - st.EntryTime).TotalSeconds >= TimeExitBars * m.RiskSec)
                {
                    st.CloseReason = "TIME EXIT";
                    LogM(m, "TIME EXIT #" + p.Id + ": TP1 not reached within " + TimeExitBars + " " + m.RiskTf.ShortName
                         + " bars, closing at market (" + SignedD(m, fav) + ", best " + SignedD(m, Math.Max(st.MaxFavorable, fav)) + ")");
                    TradeResult r = ClosePosition(p);
                    if (r.IsSuccessful)
                    {
                        _statTimeExits++;
                    }
                    else
                    {
                        st.CloseReason = null;
                        Backoff(st, now);
                        LogM(m, "TIME EXIT failed #" + p.Id + ": " + r.Error);
                    }
                    continue;
                }

                // 3. Micro break-even.
                if (BeTriggerR > 0 && !st.BeDone && fav >= BeTriggerR * st.RiskDist)
                    MoveToBreakeven(m, p, st, px, "+" + F(BeTriggerR, 2) + "R");

                // 4. Ultra-short ATR trailing stop.
                if (TrailAtrMultiplier > 0 && (st.Tp1Done || !TrailOnlyAfterTp1))
                    Trail(m, p, st, px);
            }
        }

        /// <summary>Returns true when the position was closed completely.</summary>
        private bool HandleTp1(Market m, Position p, TradeState st, double px, double fav)
        {
            DateTime now = Server.Time;
            if (st.Splittable)
            {
                double part = st.PartialVolume;
                if (p.VolumeInUnits - part < m.Symbol.VolumeInUnitsMin - VolumeEpsilon)
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
                        LogM(m, "TP1 #" + p.Id + " " + SignedD(m, fav) + ": closed " + Lots(m, part) + " (" + F(part / st.InitialVolume * 100.0, 0)
                             + "%), runner " + Lots(m, p.VolumeInUnits)
                             + (TrailAtrMultiplier > 0 ? " trails " + F(TrailAtrMultiplier, 2) + " x ATR" : " keeps its stop"));
                        if (BeTriggerR > 0 && !st.BeDone)
                            MoveToBreakeven(m, p, st, px, "TP1");
                        return false;
                    }
                    LogM(m, "TP1 partial close failed #" + p.Id + ": " + r.Error);
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
                LogM(m, "TP1 #" + p.Id + " " + SignedD(m, fav) + ": " + Lots(m, p.VolumeInUnits) + " cannot be split -> closing 100%");
                TradeResult r = ClosePosition(p);
                if (r.IsSuccessful)
                {
                    _statTp1++;
                    return true;
                }
                st.CloseReason = null;
                Backoff(st, now);
                LogM(m, "TP1 close failed #" + p.Id + ": " + r.Error);
                return false;
            }

            st.Tp1Done = true;
            _statTp1++;
            LogM(m, "TP1 #" + p.Id + " " + SignedD(m, fav) + ": " + Lots(m, p.VolumeInUnits) + " cannot be split -> whole position "
                 + (TrailAtrMultiplier > 0 ? "trails " + F(TrailAtrMultiplier, 2) + " x ATR" : "keeps its stop"));
            if (BeTriggerR > 0 && !st.BeDone)
                MoveToBreakeven(m, p, st, px, "TP1");
            return false;
        }

        private void MoveToBreakeven(Market m, Position p, TradeState st, double px, string why)
        {
            bool isLong = p.TradeType == TradeType.Buy;
            double target = Math.Round(BreakevenPrice(m, p), m.Symbol.Digits);
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
                    LogM(m, "MICRO-BE #" + p.Id + " waiting: break-even " + P(m, target) + " is not yet on the safe side of " + P(m, px));
                    st.BeBlockedLogged = true;
                }
                return;
            }
            TradeResult r = p.ModifyStopLossPrice(target);
            if (r.IsSuccessful)
            {
                st.BeDone = true;
                st.Failures = 0;
                LogM(m, "MICRO-BE #" + p.Id + " (" + why + "): SL -> " + P(m, target) + " (entry " + (isLong ? "+" : "-") + D(m, BeOffsetPips * m.BotPip) + ")");
            }
            else
            {
                Backoff(st, Server.Time);
                LogM(m, "MICRO-BE failed #" + p.Id + ": " + r.Error);
            }
        }

        private void Trail(Market m, Position p, TradeState st, double px)
        {
            double atr = RiskAtr(m);
            if (atr <= 0)
                return;
            bool isLong = p.TradeType == TradeType.Buy;
            double dist = TrailAtrMultiplier * atr;
            double candidate = isLong ? px - dist : px + dist;
            if (st.BeDone)
            {
                double be = BreakevenPrice(m, p);
                candidate = isLong ? Math.Max(candidate, be) : Math.Min(candidate, be);
            }
            candidate = Math.Round(candidate, m.Symbol.Digits);
            if (isLong ? candidate >= px : candidate <= px)
                return;

            DateTime now = Server.Time;
            if ((now - st.LastTrail).TotalSeconds < TrailMinIntervalSeconds)
                return;
            double step = Math.Max(TrailStepPips * m.BotPip, m.Symbol.TickSize);
            if (p.StopLoss.HasValue)
            {
                double improvement = isLong ? candidate - p.StopLoss.Value : p.StopLoss.Value - candidate;
                if (improvement < step - m.Symbol.TickSize * 0.01)
                    return;
            }
            st.LastTrail = now;
            TradeResult r = p.ModifyStopLossPrice(candidate);
            if (r.IsSuccessful)
            {
                st.Failures = 0;
                LogM(m, "TRAIL #" + p.Id + ": SL -> " + P(m, candidate) + " (" + F(TrailAtrMultiplier, 2) + " x ATR = " + D(m, dist) + ", locked "
                     + SignedD(m, isLong ? candidate - p.EntryPrice : p.EntryPrice - candidate) + ")");
            }
            else
            {
                Backoff(st, now);
                LogM(m, "TRAIL failed #" + p.Id + ": " + r.Error);
            }
        }

        private static void Backoff(TradeState st, DateTime now)
        {
            st.Failures++;
            double delay = Math.Min(MaxRetryDelaySeconds, RetryDelaySeconds * Math.Pow(2.0, st.Failures - 1));
            st.RetryAfter = now.AddSeconds(delay);
        }

        private double BreakevenPrice(Market m, Position p)
        {
            double offset = BeOffsetPips * m.BotPip;
            return p.TradeType == TradeType.Buy ? p.EntryPrice + offset : p.EntryPrice - offset;
        }

        private static double TargetPrice(Position p, double distance)
        {
            return p.TradeType == TradeType.Buy ? p.EntryPrice + distance : p.EntryPrice - distance;
        }

        private TradeState GetOrRecoverState(Market m, Position p)
        {
            TradeState st;
            if (_trades.TryGetValue(p.Id, out st))
                return st;

            // A position opened by an earlier run of this cBot: rebuild its plan from the order comment.
            double pip = m.Symbol.PipSize;
            double atrR = RiskAtr(m);
            bool isLong = p.TradeType == TradeType.Buy;
            string comment = p.Comment ?? "";
            double initVolume = CommentValue(comment, "v=");
            double slPips = CommentValue(comment, "s=");
            double tpPips = CommentValue(comment, "t=");

            st = new TradeState();
            st.PositionId = p.Id;
            st.SymbolName = m.Name;
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
                SplitPlan plan = VolumeMath.PlanSplit(p.VolumeInUnits, Tp1ClosePercent, m.Symbol.VolumeInUnitsMin, m.Symbol.VolumeInUnitsStep);
                st.Splittable = plan.Splittable;
                st.PartialVolume = plan.PartialVolume;
                st.CloseAllAtTp1 = !plan.Splittable && (Tp1ClosePercent >= 100.0 || NoSplitMode == NoSplitAction.CloseAllAtTp1);
            }
            st.Confidence = double.NaN;
            _trades[p.Id] = st;
            LogM(m, "RECOVERED #" + p.Id + " " + (isLong ? "BUY" : "SELL") + " " + Lots(m, p.VolumeInUnits) + " @" + P(m, p.EntryPrice) + " SL " + P(m, p.StopLoss)
                 + " | R " + D(m, st.RiskDist) + ", TP1 " + D(m, st.Tp1Dist) + (st.Tp1Done ? ", TP1 already taken" : "") + (st.BeDone ? ", stop at/after BE" : ""));
            return st;
        }

        private void RecoverOpenPositions()
        {
            foreach (Position p in Positions.FindAll(BotLabel))
            {
                Market m;
                if (_bySymbol.TryGetValue(p.SymbolName, out m))
                    GetOrRecoverState(m, p);
                else
                    Log("RECOVER: position #" + p.Id + " on " + p.SymbolName + " is not in the symbol lists - it keeps its server-side stop but is not managed");
            }
        }

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            try
            {
                Position p = args.Position;
                if (p.Label != BotLabel)
                    return;

                Market m;
                _bySymbol.TryGetValue(p.SymbolName, out m);
                TradeState st;
                _trades.TryGetValue(p.Id, out st);
                _trades.Remove(p.Id);
                if (m != null)
                    m.LastCloseTime = Server.Time;

                // Total result of the trade = TP1 partial fill(s) + final fill, read from history.
                int expectedFills = st != null && st.Tp1Done && st.Splittable ? 2 : 1;
                double net = 0.0;
                double gross = 0.0;
                int fills = 0;
                foreach (HistoricalTrade h in History.FindAll(BotLabel, p.SymbolName))
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
                    if (m != null)
                        m.ConsecLosses = 0;
                }
                else if (gross < 0)
                {
                    _statLosses++;
                    if (m != null)
                        m.ConsecLosses++;
                }
                else
                {
                    _statScratch++;
                }

                string why = DescribeClose(args.Reason, st);
                if (args.Reason == PositionCloseReason.TakeProfit && st != null && !st.Tp1Done)
                    _statTp1++;   // server-side TP1 of an unsplittable position
                _lastEvent = p.SymbolName + " CLOSED by " + why + " " + Signed(net, 2) + " " + _ccy;
                Log(p.SymbolName + " CLOSED #" + p.Id + " " + (st != null ? st.Setup : "?") + " " + (p.TradeType == TradeType.Buy ? "BUY" : "SELL") + " by " + why
                    + ": net " + Signed(net, 2) + " " + _ccy + " (gross " + Signed(gross, 2) + ", " + fills + " fill(s)) | day "
                    + Signed(Account.Balance - _dayStartBalance, 2) + " " + _ccy + " | loss streak " + (m != null ? m.ConsecLosses : 0));

                if (m != null && MaxConsecutiveLosses > 0 && m.ConsecLosses >= MaxConsecutiveLosses)
                {
                    m.PauseUntil = Server.Time.AddMinutes(LossStreakPauseMinutes);
                    m.ConsecLosses = 0;
                    LogM(m, "PAUSE: " + MaxConsecutiveLosses + " losses in a row -> no entries on " + m.Name + " until " + m.PauseUntil.ToString("HH:mm", Inv) + " UTC");
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

        #region Tick engine, spread, sessions, day guards

        private void CountTickForCalibration(Market m)
        {
            if (m.Risk.Count < 1)
                return;
            DateTime open = m.Risk.OpenTimes[m.Risk.Count - 1];
            if (open != m.CalBarOpen)
            {
                // The bar that just finished was watched from its first tick: compare our count with the broker's.
                if (m.CalBarFull)
                {
                    int idx = IndexOfTime(m.Risk, m.CalBarOpen);
                    if (idx >= 0 && idx <= m.Risk.Count - 2)
                        m.Calib.AddBar(m.CalBarTicks, m.Risk.TickVolumes[idx]);
                }
                m.CalBarFull = m.CalBarOpen != DateTime.MinValue;
                m.CalBarOpen = open;
                m.CalBarTicks = 0;
            }
            m.CalBarTicks++;
        }

        private double CalibratedTicks(Market m, double seconds)
        {
            return m.Ticks.CountSince(Server.Time, seconds) / m.Calib.Factor(CalibrationMinBars);
        }

        /// <summary>Tick density of the last few seconds relative to the N-bar average (the inflow detector).</summary>
        private double BurstRatio(Market m)
        {
            if (m.BaselineTicksPerBar <= 0 || m.SigSec <= 0)
                return 0.0;
            double expected = m.BaselineTicksPerBar * TickWindowSeconds / m.SigSec;
            return expected > 0 ? CalibratedTicks(m, TickWindowSeconds) / expected : 0.0;
        }

        /// <summary>Ticks in the last bar-length window relative to the average bar (the AI feature).</summary>
        private double BarTickVelocity(Market m)
        {
            return m.BaselineTicksPerBar > 0 ? CalibratedTicks(m, m.SigSec) / m.BaselineTicksPerBar : 0.0;
        }

        private void TrackSpread(Market m, DateTime now)
        {
            double s = m.Symbol.Spread;
            if (s < 0 || !InSessionFor(m, now))
                return;
            m.Spreads.Add(s);
        }

        private static double RiskAtr(Market m)
        {
            if (m.Risk == null || m.Risk.Count < 2)
                return 0.0;
            double v = m.AtrRisk.Result[m.Risk.Count - 2];
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

        private bool InSessionFor(Market m, DateTime now)
        {
            return (m.IsCrypto && CryptoIgnoresSession) || InSession(now);
        }

        private void ResetDay(DateTime now)
        {
            _day = now.Date;
            _dayStartBalance = Account.Balance;
            _tradesToday = 0;
            _dailyHalt = false;
            foreach (Market m in _markets)
                m.TradesToday = 0;
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
                    + " -> no new entries on any symbol until 00:00 UTC (open positions keep their stops)");
            }
        }

        #endregion

        #region Status summary

        /// <summary>Counts one signal instance (setup, direction, bar) and each distinct reason that blocked it.</summary>
        private void RecordSignal(Market m, string key, string codes)
        {
            RecordSignal(m, key, codes, double.NaN);
        }

        private void RecordSignal(Market m, string key, string codes, double confidence)
        {
            if (m.StatusKeys.Add(key))
            {
                m.StatusSignals++;
                if (!double.IsNaN(confidence))
                {
                    m.StatusConfMin = m.StatusConfCount == 0 ? confidence : Math.Min(m.StatusConfMin, confidence);
                    m.StatusConfMax = m.StatusConfCount == 0 ? confidence : Math.Max(m.StatusConfMax, confidence);
                    m.StatusConfSum += confidence;
                    m.StatusConfCount++;
                }
            }
            foreach (string code in codes.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!m.StatusKeys.Add(key + "|" + code))
                    continue;
                int n;
                m.StatusReasons.TryGetValue(code, out n);
                m.StatusReasons[code] = n + 1;
            }
        }

        private void CheckSessionTransition(DateTime now)
        {
            if (!UseSessionFilter)
                return;
            bool anyFxOpen = false;
            foreach (Market m in _markets)
            {
                if (!m.IsCrypto && m.Symbol.MarketHours.IsOpened())
                {
                    anyFxOpen = true;
                    break;
                }
            }
            bool inSession = InSession(now) && anyFxOpen;
            if (_wasInSession.HasValue && _wasInSession.Value == inSession)
                return;
            bool first = !_wasInSession.HasValue;
            _wasInSession = inSession;
            if (inSession)
                Log("SESSION OPEN (forex): entries allowed until " + SessionEndHour.ToString("00", Inv) + ":00 UTC");
            else
                Log("SESSION CLOSED (forex)" + (first ? " at start" : "") + ": no new forex entries until the market is open and it is "
                    + SessionStartHour.ToString("00", Inv) + ":00-" + SessionEndHour.ToString("00", Inv) + ":00 UTC"
                    + (CryptoIgnoresSession ? " | crypto keeps trading 24/7" : ""));
        }

        /// <summary>A plain-language summary every N minutes: what formed, what fired, what blocked it, and why right now.</summary>
        private void EmitStatusIfDue(DateTime now)
        {
            if (StatusEveryMinutes <= 0)
                return;
            if (_nextStatus == DateTime.MinValue)
            {
                _nextStatus = NextStatusTime(now);
                _statusSince = now;
                return;
            }
            if (now < _nextStatus)
                return;
            _nextStatus = NextStatusTime(now);

            var closed = new List<string>();
            int openMarkets = 0;
            int signals = 0;
            foreach (Market m in _markets)
            {
                if (m.Symbol.MarketHours.IsOpened())
                    openMarkets++;
                else
                    closed.Add(m.Name);
                signals += m.StatusSignals;
            }
            if (openMarkets > 0 || signals > 0)
            {
                Log("STATUS " + now.ToString("HH:mm", Inv) + " UTC since " + _statusSince.ToString("HH:mm", Inv) + " | positions "
                    + Positions.FindAll(BotLabel).Length + "/" + MaxOpenPositions + " | today " + _tradesToday + " trade(s), "
                    + Signed(Account.Equity - _dayStartBalance, 2) + " " + _ccy + (_dailyHalt ? " | HALTED (daily loss)" : "")
                    + (closed.Count > 0 ? " | market closed: " + string.Join(",", closed) : ""));
                foreach (Market m in _markets)
                {
                    if (m.Symbol.MarketHours.IsOpened() || m.StatusSignals > 0)
                        Log(m.Name + " | " + StatusCounts(m) + " | now " + StatusNow(m, now));
                }
            }
            foreach (Market m in _markets)
                ResetStatusCounters(m);
            _statusSince = now;
        }

        private DateTime NextStatusTime(DateTime now)
        {
            long period = TimeSpan.FromMinutes(Math.Max(1, StatusEveryMinutes)).Ticks;
            return new DateTime((now.Ticks / period + 1) * period, now.Kind);
        }

        private static void ResetStatusCounters(Market m)
        {
            m.StatusSignals = 0;
            m.StatusTrades = 0;
            m.StatusSqz = 0;
            m.StatusSwp = 0;
            m.StatusKeys.Clear();
            m.StatusReasons.Clear();
            m.StatusConfCount = 0;
            m.StatusConfSum = 0.0;
        }

        private string StatusCounts(Market m)
        {
            var sb = new StringBuilder(160);
            if (m.StatusSqz == 0 && m.StatusSwp == 0)
                sb.Append("no setup formed");
            else
                sb.Append("squeeze armed on ").Append(m.StatusSqz).Append(" bar(s), sweeps ").Append(m.StatusSwp);
            sb.Append(" | signals ").Append(m.StatusSignals).Append(", trades ").Append(m.StatusTrades);
            if (m.StatusConfCount > 0)
                sb.Append(" | AI conf ").Append(Pct(m.StatusConfMin)).Append('-').Append(Pct(m.StatusConfMax))
                  .Append(" (avg ").Append(Pct(m.StatusConfSum / m.StatusConfCount)).Append(", target ").Append(Pct(MinConfidence)).Append(')');
            if (m.StatusReasons.Count > 0)
            {
                var items = new List<KeyValuePair<string, int>>(m.StatusReasons);
                items.Sort((a, b) => b.Value.CompareTo(a.Value));
                sb.Append(" | skipped by:");
                for (int i = 0; i < items.Count; i++)
                    sb.Append(i == 0 ? " " : ", ").Append(ReasonName(items[i].Key)).Append(' ').Append(items[i].Value);
            }
            return sb.ToString();
        }

        private string StatusNow(Market m, DateTime now)
        {
            double atr = RiskAtr(m);
            double spread = m.Symbol.Spread;
            double limit = m.MaxSpreadToAtr * atr;
            string ai;
            if (!UseAiFilter)
                ai = "AI filter off";
            else if (AiTrainSpreadPips <= 0 && !m.SpreadCalibrated)
                ai = "AI calibrating spread " + m.Spreads.Count + "/" + SpreadCalibrationTicks;
            else if (!m.Ai.IsReady(AiMinSamplesPerClass))
                ai = "AI warming up " + m.Ai.Wins + "/" + m.Ai.Losses;
            else
                ai = "AI ready";
            return "spread " + D(m, spread) + (spread <= limit ? " <= " : " > ") + "limit " + D(m, limit) + " (" + Pct(m.MaxSpreadToAtr) + " of ATR " + D(m, atr) + ")"
                   + ", tick burst " + F(BurstRatio(m), 2) + "x (need " + F(TickVelocityMultiplier, 2) + "x" + (UseTickVelocity ? "" : ", filter off") + ")"
                   + ", " + ai + ", " + (InSessionFor(m, now) ? "in session" : "OUT OF SESSION") + (m.SpreadWarning ? ", SPREAD TOO WIDE" : "")
                   + " | today " + m.TradesToday + " trade(s)";
        }

        private static string ReasonName(string code)
        {
            switch (code)
            {
                case "SPREAD": return "Spread";
                case "TV": return "Tick velocity";
                case "AI": return "AI confidence";
                case "AIWARM": return "AI warming up";
                case "AISPR": return "AI spread calibration";
                case "AINA": return "AI features n/a";
                case "CHASE": return "Chase";
                case "SESSION": return "Session";
                case "CLOSED": return "Market closed";
                case "TMODE": return "Trading disabled";
                case "POS": return "Position open";
                case "MAXPOS": return "Max open positions";
                case "COOL": return "Cooldown";
                case "DAY": return "Daily loss limit";
                case "STREAK": return "Loss-streak pause";
                case "MAXTR": return "Max trades/day";
                case "WARM": return "Warm-up";
                case "SIZE": return "Size/margin";
                case "DIST": return "SL distance";
                case "MINSL": return "Broker min stop";
                case "ORDER": return "Order rejected";
                default: return code;
            }
        }

        #endregion

        #region Chart

        private static readonly Color HudColorDark = Color.Silver;
        private static readonly Color HudColorLight = Color.DimGray;
        private static readonly Color BoxColor = Color.Gold;
        private static readonly Color BullColor = Color.DodgerBlue;
        private static readonly Color BearColor = Color.OrangeRed;

        private void DrawSetups(Market m)
        {
            if (!_hudEnabled)
                return;
            try
            {
                if (m.SqzArmed)
                    Chart.DrawRectangle(ObjPrefix + "SQZ", m.SqzStart, m.SqzHigh, m.Sig.OpenTimes[m.Sig.Count - 1].AddSeconds(m.SigSec), m.SqzLow, BoxColor);
                else
                    Chart.RemoveObject(ObjPrefix + "SQZ");
                if (m.BullSweep)
                    Chart.DrawIcon(ObjPrefix + "SWP_BULL", ChartIconType.UpTriangle, m.BullTime, m.BullExtreme, BullColor);
                else
                    Chart.RemoveObject(ObjPrefix + "SWP_BULL");
                if (m.BearSweep)
                    Chart.DrawIcon(ObjPrefix + "SWP_BEAR", ChartIconType.DownTriangle, m.BearTime, m.BearExtreme, BearColor);
                else
                    Chart.RemoveObject(ObjPrefix + "SWP_BEAR");
            }
            catch (Exception ex)
            {
                _hudEnabled = false;
                Log("CHART drawing disabled: " + ex.Message);
            }
        }

        private void DrawEntry(Market m, Position p)
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
            if (!_hudEnabled || _markets.Count == 0)
                return;
            try
            {
                DateTime now = Server.Time;
                var sb = new StringBuilder(1024);
                sb.Append("QuantAI Scalper v").Append(BotVersion).Append(" | ").Append(_markets.Count).Append(" markets | positions ")
                  .Append(Positions.FindAll(BotLabel).Length).Append('/').Append(MaxOpenPositions).Append(" | ").Append(now.ToString("HH:mm:ss", Inv)).Append(" UTC\n");
                sb.Append("State: ").Append(StateText(now)).Append(" | today ").Append(_tradesToday).Append(" trade(s), ")
                  .Append(Signed(Account.Equity - _dayStartBalance, 2)).Append(' ').Append(_ccy).Append('\n');
                foreach (Market m in _markets)
                {
                    sb.Append(m.Name).Append(": ");
                    if (!m.Symbol.MarketHours.IsOpened())
                    {
                        sb.Append("market closed\n");
                        continue;
                    }
                    double atr = RiskAtr(m);
                    sb.Append(m.SqzArmed ? "SQZ armed" : (m.BullSweep || m.BearSweep ? "SWP armed" : "no setup"))
                      .Append(" | spread ").Append(D(m, m.Symbol.Spread)).Append('/').Append(D(m, m.MaxSpreadToAtr * atr))
                      .Append(" | burst ").Append(F(BurstRatio(m), 2)).Append('x')
                      .Append(" | AI ").Append(double.IsNaN(m.LastConf) ? "-" : Pct(m.LastConf));
                    foreach (Position p in Positions.FindAll(BotLabel, m.Name))
                    {
                        bool isLong = p.TradeType == TradeType.Buy;
                        double px = isLong ? m.Symbol.Bid : m.Symbol.Ask;
                        sb.Append(" | ").Append(isLong ? "BUY " : "SELL ").Append(F(m.Symbol.VolumeInUnitsToQuantity(p.VolumeInUnits), 2)).Append(' ')
                          .Append(SignedD(m, isLong ? px - p.EntryPrice : p.EntryPrice - px));
                    }
                    sb.Append('\n');
                }
                sb.Append("Last: ").Append(_lastEvent);
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
            if (MaxTradesPerDay > 0 && _tradesToday >= MaxTradesPerDay)
                return "DONE (max trades)";
            return InSession(now) ? "TRADING" : "FOREX OUT OF SESSION" + (CryptoIgnoresSession ? ", crypto trading" : "");
        }

        #endregion

        #region Helpers

        private void LogBanner()
        {
            Log("=== QuantAI_Scalper_M1_MicroDepot_Pro v" + BotVersion + " | balance " + F(Account.Balance, 2) + " " + _ccy + " | broker "
                + (string.IsNullOrEmpty(Account.BrokerName) ? "?" : Account.BrokerName) + " " + (Account.IsLive ? "LIVE" : "DEMO") + " " + Account.AccountType
                + " | leverage 1:" + F(Account.PreciseLeverage, 0) + " | mode " + RunningMode + " ===");
            Log("PARAMS markets: forex [" + FxSymbols + "] on " + SignalTimeFrame.ShortName + " | crypto [" + CryptoSymbols + "] on " + CryptoTimeFrame.ShortName
                + (CryptoIgnoresSession ? " 24/7" : " in session") + " | max open positions " + MaxOpenPositions);
            Log("PARAMS risk: " + (FixedLots > 0 ? "fixed " + F(FixedLots, 2) + " lot per trade" : F(RiskPercent, 2) + "% of balance per trade")
                + " | lots " + F(MinLots, 2) + "-" + F(MaxLots, 2) + " (min-lot override " + OnOff(AllowMinLotOverride) + ") | margin per trade "
                + F(MarginPerTradePercent, 0) + "% of free, all positions <= " + F(MaxTotalMarginPercent, 0) + "% of equity | leverage check "
                + (LeverageOverride > 0 ? "1:" + F(LeverageOverride, 0) : "broker") + " | slippage " + (MaxSlippagePips > 0 ? F(MaxSlippagePips, 1) + " pip (>= spread)" : "market")
                + " | daily loss " + (DailyMaxLossPercent > 0 ? F(DailyMaxLossPercent, 1) + "%" : "off") + " | loss streak per symbol "
                + (MaxConsecutiveLosses > 0 ? MaxConsecutiveLosses + " -> pause " + LossStreakPauseMinutes + "m" : "off")
                + " | max trades/day " + (MaxTradesPerDay > 0 ? MaxTradesPerDay.ToString(Inv) : "off"));
            Log("PARAMS exits: SL " + F(SlAtrMultiplier, 2) + " x ATR(" + AtrPeriod + ") | TP1 " + F(Tp1AtrMultiplier, 2) + " x ATR closes "
                + F(Tp1ClosePercent, 0) + "% (unsplittable: " + NoSplitMode + ") | runner TP " + (Tp2AtrMultiplier > 0 ? F(Tp2AtrMultiplier, 2) + " x ATR" : "none")
                + " | trail " + (TrailAtrMultiplier > 0 ? F(TrailAtrMultiplier, 2) + " x ATR, step " + F(TrailStepPips, 1) + " pip, " + (TrailOnlyAfterTp1 ? "after TP1" : "from entry") : "off")
                + " | micro-BE " + (BeTriggerR > 0 ? "+" + F(BeTriggerR, 2) + "R -> entry+" + F(BeOffsetPips, 1) + " pip" : "off")
                + " | time exit " + (TimeExitBars > 0 ? TimeExitBars + " bars (forex " + RiskTimeFrame.ShortName + ", crypto " + CryptoTimeFrame.ShortName + ")" : "off"));
            Log("PARAMS filters: spread/ATR forex <= " + F(MaxSpreadToAtr, 3) + ", crypto <= " + F(CryptoMaxSpreadToAtr, 3) + " | session "
                + (UseSessionFilter ? SessionStartHour.ToString("00", Inv) + ":00-" + SessionEndHour.ToString("00", Inv) + ":00 UTC (forex)" : "off")
                + " | cooldown " + CooldownSeconds + "s per symbol | max chase " + F(MaxChaseAtr, 2) + " x ATR | tick velocity " + OnOff(UseTickVelocity)
                + " N " + F(TickVelocityMultiplier, 2) + "x over " + F(TickWindowSeconds, 1) + "s vs " + TickBaselineBars + " bars");
            Log("PARAMS setups: EMA " + EmaPeriod + " | squeeze " + (UseSqueeze ? SqueezeBars + " bars, range <= " + F(SqueezeMaxRangeAtr, 2)
                + " x ATR, EMA gap <= " + F(SqueezeMaxEmaGapAtr, 2) + " x ATR" : "off") + " | buffer " + F(BreakoutBufferPips, 1) + " pip | sweep "
                + (UseSweep ? SweepLookbackBars + "-bar H/L, pierce >= " + F(SweepMinPiercePips, 1) + " pip, close inside " + OnOff(SweepRequireCloseInside)
                + ", EMA reclaim within " + SweepMaxBarsToReclaim + " bars" : "off"));
            Log("PARAMS AI: " + OnOff(UseAiFilter) + " | Min Confidence " + F(MinConfidence, 2) + " | window " + AiTrainingBars + " bars | min "
                + AiMinSamplesPerClass + " per class | horizon " + AiHorizonBars + " ATR-TF bars | RSI(" + RsiPeriod + ") slope " + RsiSlopeBars
                + " bars | prior " + (AiUseEmpiricalPrior ? "empirical" : "balanced") + " | train spread "
                + (AiTrainSpreadPips > 0 ? F(AiTrainSpreadPips, 1) + " pip (fixed)" : "live median, retrain after " + SpreadCalibrationTicks + " ticks")
                + " | status every " + (StatusEveryMinutes > 0 ? StatusEveryMinutes + " min" : "off"));
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

        private void LogM(Market m, string message)
        {
            Log(m.Name + " " + message);
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

        private static string FormatContributions(double[] c)
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

        private static string P(Market m, double price)
        {
            return price.ToString("F" + m.Symbol.Digits, Inv);
        }

        private static string P(Market m, double? price)
        {
            return price.HasValue ? P(m, price.Value) : "none";
        }

        /// <summary>A price distance: in pips on Forex, in price units on everything else.</summary>
        private static string D(Market m, double distance)
        {
            if (m.FxLike)
                return F(distance / m.Symbol.PipSize, 1) + "p";
            return distance.ToString("F" + m.Symbol.Digits, Inv);
        }

        private static string SignedD(Market m, double distance)
        {
            return (distance >= 0 ? "+" : "") + D(m, distance);
        }

        private static string Lots(Market m, double units)
        {
            return F(m.Symbol.VolumeInUnitsToQuantity(units), 2) + " lot (" + Units(units) + "u)";
        }

        private static string Units(double units)
        {
            return units.ToString(units >= 100 ? "F0" : "0.####", Inv);
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

    /// <summary>Everything the bot tracks for one traded symbol.</summary>
    internal sealed class Market
    {
        public string Name;
        public Symbol Symbol;
        public bool IsCrypto;
        public bool IsChart;
        public bool FxLike;
        public double BotPip;
        public double MaxSpreadToAtr;

        public TimeFrame SigTf;
        public TimeFrame RiskTf;
        public bool SameTf;
        public Bars Sig;
        public Bars Risk;
        public double SigSec;
        public double RiskSec;
        public ExponentialMovingAverage Ema;
        public AverageTrueRange AtrSig;
        public AverageTrueRange AtrRisk;
        public RelativeStrengthIndex Rsi;

        public TickVelocityEngine Ticks;
        public TickCalibrator Calib;
        public SpreadTracker Spreads;
        public DateTime EngineStart;
        public DateTime CalBarOpen = DateTime.MinValue;
        public int CalBarTicks;
        public bool CalBarFull;
        public double BaselineTicksPerBar;

        public GaussianNaiveBayes Ai;
        public bool AiWasReady;
        public readonly List<PendingSample> Pending = new List<PendingSample>();
        public readonly double[] Contrib = new double[AiFeatures.Dim];
        public double[] HBuf;
        public double[] LBuf;
        public double[] WinH;
        public double[] WinL;
        public double LastConf = double.NaN;

        public DateTime LastSigClosed = DateTime.MinValue;
        public DateTime LastRiskClosed = DateTime.MinValue;

        public bool SqzArmed;
        public double SqzHigh;
        public double SqzLow;
        public DateTime SqzStart;
        public string SqzNote = "-";
        public bool BullSweep;
        public double BullExtreme;
        public int BullBarsLeft;
        public DateTime BullTime;
        public bool BearSweep;
        public double BearExtreme;
        public int BearBarsLeft;
        public DateTime BearTime;
        public string SwpIdle = "-";
        public string SwpNote = "-";
        public double ReclaimEma;
        public double SetupAtr;

        public bool SpreadChecked;
        public bool SpreadCalibrated;
        public bool SpreadWarning;
        public double BootstrapSpread;
        public double CommissionPerUnit;
        public string CommissionSource = "none";

        public DateTime LastCloseTime = DateTime.MinValue;
        public int ConsecLosses;
        public DateTime PauseUntil = DateTime.MinValue;
        public int TradesToday;
        public readonly HashSet<string> SkipKeys = new HashSet<string>();
        public string LastSkip = "-";

        public int StatusSignals;
        public int StatusTrades;
        public int StatusSqz;
        public int StatusSwp;
        public int StatusConfCount;
        public double StatusConfSum;
        public double StatusConfMin;
        public double StatusConfMax;
        public readonly HashSet<string> StatusKeys = new HashSet<string>();
        public readonly Dictionary<string, int> StatusReasons = new Dictionary<string, int>();
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
        public string SymbolName;
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
        public DateTime LastTrail;
        public int Failures;
        public string CloseReason;
    }

    /// <summary>Feature vector of the classifier: [Tick_Velocity, EMA20_Distance, RSI_Slope, Spread_Ratio].</summary>
    internal static class AiFeatures
    {
        public const int Dim = 4;
        public static readonly string[] Names = { "TV", "EMA", "RSI", "SPR" };

        // Keeps log() finite for a tick-less window.
        private const double MinRatio = 1e-3;

        /// <param name="tickVelocity">ticks per bar-length window / average ticks per bar</param>
        /// <param name="priceMinusEma">price - EMA20</param>
        /// <param name="atr">signal timeframe ATR used to normalise the EMA distance</param>
        /// <param name="rsiDelta">RSI now - RSI k bars ago</param>
        /// <param name="spreadToAtr">spread / ATR (kept linear: raw-spread accounts often quote exactly 0.0)</param>
        /// <param name="dir">+1 for a long, -1 for a short: directional features are mirrored so one model serves both sides</param>
        public static double[] Build(double tickVelocity, double priceMinusEma, double atr, double rsiDelta, double spreadToAtr, int dir)
        {
            var x = new double[Dim];
            x[0] = Math.Log(Math.Max(tickVelocity, MinRatio));
            x[1] = dir * priceMinusEma / atr;
            x[2] = dir * rsiDelta;
            x[3] = Math.Max(spreadToAtr, 0.0);
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
        // A feature whose variance is only rounding noise (e.g. the spread when every training sample saw
        // 0.0) carries no information; scoring it would divide by ~0 and throw the posterior to 0 or 1.
        private const double ConstantFeatureTolerance = 1e-10;
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
                if (gVar <= ConstantFeatureTolerance * Math.Max(1.0, gMean * gMean))
                {
                    if (contributions != null && f < contributions.Length)
                        contributions[f] = 0.0;
                    continue;
                }
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

    /// <summary>How a symbol's base commission is quoted (mirrors cTrader's SymbolCommissionType).</summary>
    internal enum CommissionBasis
    {
        UsdPerMillionUsd,
        UsdPerLot,
        QuotePerLot,
        PercentOfVolume
    }

    /// <summary>Round-turn commission per unit of volume in account currency, from the symbol's commission settings.</summary>
    internal static class CommissionMath
    {
        /// <param name="commission">base commission for one side of the trade</param>
        /// <param name="price">current price (quote currency per unit of base)</param>
        /// <param name="lotSize">units per lot</param>
        /// <param name="quoteToAccount">value of one quote-currency unit in account currency (PipValue / PipSize)</param>
        /// <returns>0 when the commission cannot be converted into the account currency</returns>
        public static double RoundTurnPerUnit(CommissionBasis basis, double commission, double price, double lotSize,
                                              double quoteToAccount, bool quoteIsUsd, bool baseIsUsd, bool accountIsUsd)
        {
            if (!(commission > 0) || !(price > 0) || !(lotSize > 0) || !(quoteToAccount > 0))
                return 0.0;
            double usdToAccount = quoteIsUsd ? quoteToAccount : baseIsUsd ? price * quoteToAccount : accountIsUsd ? 1.0 : double.NaN;
            double usdPerUnit = quoteIsUsd ? price : baseIsUsd ? 1.0 : accountIsUsd ? price * quoteToAccount : double.NaN;
            double perSide;
            switch (basis)
            {
                case CommissionBasis.UsdPerMillionUsd:
                    perSide = commission * usdPerUnit / 1e6 * usdToAccount;
                    break;
                case CommissionBasis.UsdPerLot:
                    perSide = commission / lotSize * usdToAccount;
                    break;
                case CommissionBasis.QuotePerLot:
                    perSide = commission / lotSize * quoteToAccount;
                    break;
                case CommissionBasis.PercentOfVolume:
                    perSide = commission / 100.0 * price * quoteToAccount;
                    break;
                default:
                    return 0.0;
            }
            if (double.IsNaN(perSide) || double.IsInfinity(perSide) || perSide < 0)
                return 0.0;
            return 2.0 * perSide;
        }
    }

    /// <summary>Rolling median of the most recent in-session spreads.</summary>
    internal sealed class SpreadTracker
    {
        private readonly double[] _buffer;
        private readonly double[] _scratch;
        private int _next;
        private int _count;

        public SpreadTracker(int capacity)
        {
            _buffer = new double[Math.Max(1, capacity)];
            _scratch = new double[_buffer.Length];
        }

        public int Count
        {
            get { return _count; }
        }

        public void Add(double spread)
        {
            if (double.IsNaN(spread) || double.IsInfinity(spread) || spread < 0)
                return;
            _buffer[_next] = spread;
            _next = (_next + 1) % _buffer.Length;
            if (_count < _buffer.Length)
                _count++;
        }

        public double Median()
        {
            if (_count == 0)
                return double.NaN;
            Array.Copy(_buffer, _scratch, _count);
            Array.Sort(_scratch, 0, _count);
            int mid = _count / 2;
            return _count % 2 == 1 ? _scratch[mid] : 0.5 * (_scratch[mid - 1] + _scratch[mid]);
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

    internal enum MarginRule
    {
        Fits,
        ReducedToShare,
        MinimumWithinTotal,
        TotalLimitReached,
        Insufficient
    }

    internal struct MarginFit
    {
        public MarginRule Rule;
        public double Units;       // volume to trade, 0 when rejected
        public double PerTrade;    // margin this trade may block
        public double TotalLeft;   // margin all positions together may still block
        public double MinNeed;     // margin of the minimum volume, when it was checked
    }

    /// <summary>Symbol lists, the price unit of pip parameters and the margin rules, independent of any broker API.</summary>
    internal static class MarketMath
    {
        private const double Eps = 1e-6;

        /// <summary>Names separated by commas, semicolons or spaces; duplicates (any case) are dropped.</summary>
        public static List<string> ParseSymbols(string list)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(list))
                return result;
            foreach (string raw in list.Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string name = raw.Trim();
                if (name.Length > 0 && seen.Add(name))
                    result.Add(name);
            }
            return result;
        }

        /// <summary>Letters and digits only, upper case: "BTC/USD", "btcusd" and "BTCUSD." all give "BTCUSD".</summary>
        public static string SymbolKey(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (char ch in name)
            {
                if (char.IsLetterOrDigit(ch))
                    sb.Append(char.ToUpperInvariant(ch));
            }
            return sb.ToString();
        }

        /// <summary>True when the broker pip is a Forex-sized step of the price (EURUSD 0.0001, USDJPY 0.01).</summary>
        public static bool IsFxLike(double pipSize, double price, double minPipRatio)
        {
            return price > 0 && pipSize > 0 && pipSize / price >= minPipRatio;
        }

        /// <summary>
        /// Price distance of one "pip" of the parameters: the broker pip on Forex, otherwise a fixed fraction of the
        /// price (never below the broker pip), so buffers and offsets keep the same meaning on BTC as on EURUSD.
        /// </summary>
        public static double BotPip(double pipSize, double price, bool fxLike, double nonFxRatio)
        {
            if (fxLike || !(price > 0))
                return pipSize;
            return Math.Max(pipSize, price * nonFxRatio);
        }

        /// <summary>
        /// Fits a volume into the margin rules: one trade may block perTradePercent of free margin, all positions together
        /// totalPercent of equity. When even the minimum volume needs more than the per-trade share it is still allowed
        /// within the total limit (if allowMinimum), so a micro account can hold one minimum position.
        /// </summary>
        public static MarginFit FitMargin(double units, double minVolume, double step, double freeMargin, double equity, double usedMargin,
                                          double perTradePercent, double totalPercent, bool allowMinimum, Func<double, double> marginFor,
                                          int maxIterations)
        {
            var fit = new MarginFit();
            fit.TotalLeft = Math.Min(freeMargin, equity * totalPercent / 100.0 - usedMargin);
            fit.PerTrade = Math.Min(freeMargin * perTradePercent / 100.0, fit.TotalLeft);
            if (!(fit.TotalLeft > 0))
            {
                fit.Rule = MarginRule.TotalLimitReached;
                return fit;
            }

            double need = marginFor(units);
            if (need <= fit.PerTrade + Eps)
            {
                fit.Rule = MarginRule.Fits;
                fit.Units = units;
                return fit;
            }

            double reduced = units > 0 && need > 0 ? VolumeMath.FloorToStep(fit.PerTrade / (need / units), step) : 0.0;
            // Dynamic leverage can make margin grow faster than volume: step down until it fits.
            bool fits = false;
            for (int i = 0; i <= maxIterations && reduced >= minVolume - Eps; i++)
            {
                if (marginFor(reduced) <= fit.PerTrade + Eps)
                {
                    fits = true;
                    break;
                }
                reduced = VolumeMath.FloorToStep(reduced - step, step);
            }
            if (fits)
            {
                fit.Rule = MarginRule.ReducedToShare;
                fit.Units = reduced;
                return fit;
            }

            fit.MinNeed = marginFor(minVolume);
            if (allowMinimum && fit.MinNeed <= fit.TotalLeft + Eps)
            {
                fit.Rule = MarginRule.MinimumWithinTotal;
                fit.Units = VolumeMath.CeilToStep(minVolume, step);
                return fit;
            }
            fit.Rule = MarginRule.Insufficient;
            return fit;
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
