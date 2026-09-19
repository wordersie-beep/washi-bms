using System;
using System.Collections.Generic;
using Quant.Core.Config;
using Quant.Core.Numerics;
using Quant.Core.Primitives;

namespace Quant.Core.Stats;

/// <summary>
/// The system's performance memory, sliced every way a decision needs (spec section 124).
///
/// Slices are created lazily and keyed by a string built from the dimensions involved. The
/// point of slicing this finely is that "does this strategy work" is almost never the
/// question that matters. "Does this strategy work in THIS regime, on THIS symbol, at THIS
/// confidence level" is, and a system that cannot answer it cannot adapt on anything but
/// aggregate noise.
///
/// The obvious hazard is that fine slices are small slices, and small samples say nothing.
/// That is handled everywhere downstream by Bayesian shrinkage and by the effective sample
/// size travelling with each estimate, never by pretending a six-trade slice is evidence.
/// </summary>
public sealed class PerformanceStore
{
    private readonly AdaptationConfig _config;
    private readonly Dictionary<string, SegmentStats> _segments = new Dictionary<string, SegmentStats>(StringComparer.Ordinal);
    private readonly List<TradeRecord> _trades;
    private readonly List<TradeRecord> _virtualTrades = new List<TradeRecord>();
    private readonly int _maxTrades;

    public PerformanceStore(AdaptationConfig config, int maxTradesRetained = 2000)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _maxTrades = Math.Max(100, maxTradesRetained);
        _trades = new List<TradeRecord>(Math.Min(_maxTrades, 512));
    }

    /// <summary>All retained trades, oldest first. Bounded so a 24/7 process stays bounded.</summary>
    public IReadOnlyList<TradeRecord> Trades => _trades;

    /// <summary>
    /// Виртуальные сделки отключённых стратегий.
    ///
    /// Хранятся отдельно и сохраняются наравне с настоящими: теневая запись — единственный
    /// путь отключённой стратегии обратно в работу, и обнулять её при каждом перезапуске
    /// значило бы сделать восстановление недостижимым там, где процесс перезапускается
    /// регулярно, — то есть в облаке.
    /// </summary>
    public IReadOnlyList<TradeRecord> VirtualTrades => _virtualTrades;

    public SegmentStats Overall => Get("all");

    public int TotalTrades => Overall.TotalTrades;

    /// <summary>
    /// Folds a closed trade into every slice it belongs to.
    ///
    /// Virtual trades — shadow mode, and disabled strategies being monitored for recovery —
    /// are recorded ONLY in their own shadow slices. Letting them into the real slices would
    /// mean adaptation decisions were driven partly by trades that never paid a spread.
    /// </summary>
    public void Record(TradeRecord trade)
    {
        if (trade == null) return;

        if (trade.IsVirtual)
        {
            _virtualTrades.Add(trade);
            if (_virtualTrades.Count > _maxTrades) _virtualTrades.RemoveRange(0, _virtualTrades.Count - _maxTrades);

            Get(ShadowKey(trade.StrategyName)).Record(trade);
            Get(ShadowKey(trade.StrategyName, trade.Regime)).Record(trade);
            return;
        }

        _trades.Add(trade);
        if (_trades.Count > _maxTrades) _trades.RemoveRange(0, _trades.Count - _maxTrades);

        Get("all").Record(trade);
        Get(StrategyKey(trade.StrategyName)).Record(trade);
        Get(StrategyRegimeKey(trade.StrategyName, trade.Regime)).Record(trade);
        Get(StrategySymbolKey(trade.StrategyName, trade.SymbolName)).Record(trade);
        Get(SymbolKey(trade.SymbolName)).Record(trade);
        Get(RegimeKey(trade.Regime)).Record(trade);
        Get(SessionKey(trade.Session)).Record(trade);
        Get(WeekendKey(trade.IsWeekend)).Record(trade);
        Get(HourKey(trade.HourUtc)).Record(trade);
        Get(DayKey(trade.DayOfWeek)).Record(trade);
        Get(DirectionKey(trade.Direction)).Record(trade);
        Get(VolatilityKey(trade.VolatilityBucket)).Record(trade);
        Get(ConfidenceBucketKey(trade.EnsembleConfidence)).Record(trade);
        Get(RewardRiskBucketKey(trade.PlannedRewardToRisk)).Record(trade);
        Get(ExitReasonKey(trade.ExitReason)).Record(trade);
    }

    /// <summary>Fetches a slice, creating it on first use.</summary>
    public SegmentStats Get(string key)
    {
        if (!_segments.TryGetValue(key, out SegmentStats stats))
        {
            stats = new SegmentStats(_config.RecentPerformanceHalfLife, _config.CusumSlack, _config.CusumThreshold);
            _segments[key] = stats;
        }
        return stats;
    }

    public bool TryGet(string key, out SegmentStats stats) => _segments.TryGetValue(key, out stats);

    public IReadOnlyDictionary<string, SegmentStats> AllSegments => _segments;

    // --- Key construction -------------------------------------------------------------
    // Kept in one place so that a reader and a writer can never disagree about a key.

    public static string StrategyKey(string strategy) => "strategy:" + strategy;
    public static string StrategyRegimeKey(string strategy, MarketRegime regime) => "strategy:" + strategy + "|regime:" + regime;
    public static string StrategySymbolKey(string strategy, string symbol) => "strategy:" + strategy + "|symbol:" + symbol;
    public static string SymbolKey(string symbol) => "symbol:" + symbol;
    public static string RegimeKey(MarketRegime regime) => "regime:" + regime;
    public static string SessionKey(SessionKind session) => "session:" + session;
    public static string WeekendKey(bool isWeekend) => "weekend:" + (isWeekend ? "yes" : "no");
    public static string HourKey(int hourUtc) => "hour:" + MathUtil.ClampInt(hourUtc, 0, 23).ToString("00");
    public static string DayKey(DayOfWeek day) => "day:" + day;
    public static string DirectionKey(Side side) => "direction:" + side;
    public static string VolatilityKey(VolatilityBucket bucket) => "vol:" + bucket;
    public static string ExitReasonKey(ExitReason reason) => "exit:" + reason;
    public static string ShadowKey(string strategy) => "shadow:" + strategy;
    public static string ShadowKey(string strategy, MarketRegime regime) => "shadow:" + strategy + "|regime:" + regime;

    /// <summary>Confidence bucket key, in five-point bands (spec section 125).</summary>
    public static string ConfidenceBucketKey(double confidence)
    {
        int band = (int)Math.Floor(MathUtil.Clamp01(confidence) * 20) * 5;
        return "conf:" + MathUtil.ClampInt(band, 0, 95).ToString("00");
    }

    /// <summary>Reward-to-risk bucket key, in half-R bands (spec section 126).</summary>
    public static string RewardRiskBucketKey(double rewardToRisk)
    {
        double clamped = MathUtil.Clamp(rewardToRisk, 0, 6);
        double band = Math.Floor(clamped * 2) / 2.0;
        return "rr:" + band.ToString("F1");
    }

    // --- Cross-cutting queries --------------------------------------------------------

    /// <summary>
    /// Realised win rate inside a confidence bucket, used to check whether the system's
    /// stated confidence means anything (spec section 125).
    /// </summary>
    public double RealizedWinRateForConfidence(double confidence) =>
        Get(ConfidenceBucketKey(confidence)).WinRate;

    /// <summary>
    /// Best available statistics for a strategy in a regime, falling back up the hierarchy
    /// when the specific slice is too thin to be worth reading.
    ///
    /// Returns both the chosen slice and how much it should be trusted, because a caller
    /// that receives a fallback slice without being told it is a fallback will
    /// systematically over-trust it.
    /// </summary>
    public (SegmentStats Stats, double Trust) BestAvailable(string strategy, MarketRegime regime, string symbol, int minSample)
    {
        SegmentStats specific = Get(StrategyRegimeKey(strategy, regime));
        if (specific.TotalTrades >= minSample) return (specific, 1.0);

        SegmentStats bySymbol = Get(StrategySymbolKey(strategy, symbol));
        if (bySymbol.TotalTrades >= minSample) return (bySymbol, 0.7);

        SegmentStats byStrategy = Get(StrategyKey(strategy));
        if (byStrategy.TotalTrades >= minSample) return (byStrategy, 0.5);

        // Nothing is thick enough. Return the broadest slice and say plainly that it should
        // barely be trusted, rather than returning a thin slice that looks authoritative.
        return (Overall, 0.2);
    }

    public void Clear()
    {
        _segments.Clear();
        _trades.Clear();
    }
}
