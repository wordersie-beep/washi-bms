using System;
using Quant.Core.Numerics;

namespace Quant.Core.Stats;

/// <summary>
/// Performance statistics for one slice of the trade history (spec sections 20-22).
///
/// Holds a LONG-TERM view and a decayed RECENT view side by side, deliberately. Using only
/// the long-term view means the system never notices an edge decaying; using only the recent
/// view means a run of five bad trades erases two hundred good ones. Both are kept, and the
/// recent view carries its own effective sample size so callers know how much it is worth.
/// </summary>
public sealed class SegmentStats
{
    private readonly RollingWindow _recentR;
    private readonly RollingWindow _maeR;
    private readonly RollingWindow _mfeR;
    private readonly RollingWindow _durations;
    private readonly RollingWindow _captureRatios;
    private readonly Ewma _recentExpectancy;
    private readonly Cusum _cusum;

    private double _sumR;
    private double _sumRSquared;
    private double _grossWinR;
    private double _grossLossR;
    private double _peakCumulativeR;
    private double _cumulativeR;
    private double _maxDrawdownR;

    public SegmentStats(double recentHalfLife, double cusumSlack, double cusumThreshold, int windowSize = 200)
    {
        _recentR = new RollingWindow(windowSize);
        _maeR = new RollingWindow(windowSize);
        _mfeR = new RollingWindow(windowSize);
        _durations = new RollingWindow(windowSize);
        _captureRatios = new RollingWindow(windowSize);
        _recentExpectancy = new Ewma(recentHalfLife);
        _cusum = new Cusum(cusumSlack, cusumThreshold);
    }

    public int TotalTrades { get; private set; }
    public int Wins { get; private set; }
    public int Losses { get; private set; }
    public int LongestWinStreak { get; private set; }
    public int LongestLossStreak { get; private set; }
    public int CurrentStreak { get; private set; }
    public DateTime LastTradeUtc { get; private set; }

    public double WinRate => MathUtil.SafeDiv(Wins, TotalTrades);

    /// <summary>Long-term expectancy in R per trade.</summary>
    public double ExpectancyR => MathUtil.SafeDiv(_sumR, TotalTrades);

    /// <summary>Decayed recent expectancy in R per trade.</summary>
    public double RecentExpectancyR => _recentExpectancy.Value;

    /// <summary>Effective sample size behind <see cref="RecentExpectancyR"/>.</summary>
    public double RecentEffectiveSample => _recentExpectancy.EffectiveSampleSize;

    /// <summary>
    /// Profit factor computed in R. Returns 0 with no losses AND no wins; returns a large
    /// but finite number when there are wins and no losses, never infinity.
    /// </summary>
    public double ProfitFactor
    {
        get
        {
            if (_grossLossR <= MathUtil.Epsilon) return _grossWinR > 0 ? 10.0 : 0.0;
            return MathUtil.Clamp(_grossWinR / _grossLossR, 0, 10.0);
        }
    }

    public double AverageWinR => Wins == 0 ? 0 : _grossWinR / Wins;
    public double AverageLossR => Losses == 0 ? 0 : _grossLossR / Losses;
    public double PayoffRatio => MathUtil.SafeDiv(AverageWinR, AverageLossR);

    public double MedianR => _recentR.Median();
    public double StdDevR
    {
        get
        {
            if (TotalTrades < 2) return 0;
            double mean = ExpectancyR;
            double v = (_sumRSquared - (TotalTrades * mean * mean)) / (TotalTrades - 1);
            return v > 0 ? Math.Sqrt(v) : 0;
        }
    }

    /// <summary>Maximum peak-to-trough drawdown of the cumulative R curve, in R.</summary>
    public double MaxDrawdownR => _maxDrawdownR;

    public double CumulativeR => _cumulativeR;

    public double AverageMaeR => _maeR.Mean;
    public double AverageMfeR => _mfeR.Mean;
    public double AverageDurationMinutes => _durations.Mean;
    public double AverageCaptureRatio => _captureRatios.Mean;

    /// <summary>True when the CUSUM detector has flagged a sustained fall in expectancy.</summary>
    public bool ExpectancyDeteriorating => _cusum.DownwardShiftDetected;
    public double DeteriorationSeverity => _cusum.DownwardSeverity;

    /// <summary>
    /// Quantile of the MAE distribution (spec section 45). A stop placed inside this is a
    /// stop that will convert winners into losers.
    /// </summary>
    public double MaeQuantile(double q) => _maeR.Quantile(q);

    /// <summary>
    /// Quantile of the MFE distribution (spec section 44). Targets beyond what the setup
    /// historically reaches are not targets, they are hopes.
    /// </summary>
    public double MfeQuantile(double q) => _mfeR.Quantile(q);

    public int MaeSampleSize => _maeR.Count;
    public int MfeSampleSize => _mfeR.Count;

    /// <summary>
    /// Sharpe-like ratio: expectancy per unit of R dispersion. Not annualised — it is a
    /// per-trade quality measure, and calling it a Sharpe ratio would overstate what it is.
    /// </summary>
    public double RiskAdjustedExpectancy => MathUtil.SafeDiv(ExpectancyR, StdDevR);

    /// <summary>Sortino-like ratio, penalising only downside dispersion.</summary>
    public double DownsideAdjustedExpectancy
    {
        get
        {
            int n = _recentR.Count;
            if (n < 3) return 0;

            double sumSq = 0;
            int count = 0;
            for (int i = 0; i < n; i++)
            {
                double r = _recentR[i];
                if (r < 0) { sumSq += r * r; count++; }
            }

            if (count == 0) return ExpectancyR > 0 ? 10.0 : 0.0;
            return MathUtil.Clamp(MathUtil.SafeDiv(ExpectancyR, Math.Sqrt(sumSq / count)), -10, 10);
        }
    }

    /// <summary>
    /// One-sided t-like statistic testing whether expectancy is below zero.
    ///
    /// This is the second, independent route to declaring a strategy degraded. The CUSUM
    /// catches a SHIFT; this catches a level that is simply, significantly bad, which a
    /// change detector will miss if the strategy was never good to begin with.
    /// Returns 0 below a usable sample size.
    /// </summary>
    public double ExpectancyTStatistic
    {
        get
        {
            if (TotalTrades < 10) return 0;
            double sd = StdDevR;
            if (sd < MathUtil.Epsilon) return ExpectancyR > 0 ? 10 : (ExpectancyR < 0 ? -10 : 0);
            return MathUtil.Clamp(ExpectancyR / (sd / Math.Sqrt(TotalTrades)), -20, 20);
        }
    }

    public void Record(TradeRecord trade)
    {
        if (trade == null) return;

        double r = MathUtil.Finite(trade.R);

        // Captured BEFORE the trade is folded in, so the change detector below is measured
        // against the level that prevailed up to this trade rather than against one that
        // already includes it.
        double meanBefore = TotalTrades == 0 ? 0 : _sumR / TotalTrades;
        double sdBefore = StdDevR;

        TotalTrades++;
        LastTradeUtc = trade.ExitTimeUtc;

        _sumR += r;
        _sumRSquared += r * r;
        _recentR.Add(r);
        _maeR.Add(Math.Abs(MathUtil.Finite(trade.MaeR)));
        _mfeR.Add(Math.Abs(MathUtil.Finite(trade.MfeR)));
        _durations.Add(trade.DurationMinutes);
        _captureRatios.Add(trade.CaptureRatio);

        if (r > 0)
        {
            Wins++;
            _grossWinR += r;
            CurrentStreak = CurrentStreak > 0 ? CurrentStreak + 1 : 1;
            if (CurrentStreak > LongestWinStreak) LongestWinStreak = CurrentStreak;
        }
        else
        {
            Losses++;
            _grossLossR += -r;
            CurrentStreak = CurrentStreak < 0 ? CurrentStreak - 1 : -1;
            if (-CurrentStreak > LongestLossStreak) LongestLossStreak = -CurrentStreak;
        }

        // Drawdown of the cumulative R curve.
        _cumulativeR += r;
        if (_cumulativeR > _peakCumulativeR) _peakCumulativeR = _cumulativeR;
        double drawdown = _peakCumulativeR - _cumulativeR;
        if (drawdown > _maxDrawdownR) _maxDrawdownR = drawdown;

        _recentExpectancy.Add(r);

        // The change detector is referenced against the LONG-RUN mean, not against the
        // decayed recent mean.
        //
        // Referencing it against the recent mean was a defect: the recent mean chases the
        // very deterioration the detector exists to find, so after a long enough bad run the
        // deviations shrink to nothing and the alarm quietly switches itself off precisely
        // when the strategy is at its worst. The long-run mean moves slowly enough to stay a
        // meaningful baseline while the shift is happening.
        if (TotalTrades > 10 && sdBefore > MathUtil.Epsilon)
        {
            _cusum.Add((r - meanBefore) / sdBefore);
        }
    }

    /// <summary>
    /// Posterior over the win rate, shrunk toward the supplied prior (spec section 22).
    /// This is what stops ten trades being mistaken for evidence.
    /// </summary>
    public BetaBinomial WinRatePosterior(double priorMean, double priorStrength) =>
        BetaBinomial.FromCounts(priorMean, priorStrength, Wins, Losses);

    public void Reset()
    {
        _recentR.Clear(); _maeR.Clear(); _mfeR.Clear(); _durations.Clear(); _captureRatios.Clear();
        _recentExpectancy.Reset();
        _cusum.Reset();
        _sumR = _sumRSquared = _grossWinR = _grossLossR = 0;
        _peakCumulativeR = _cumulativeR = _maxDrawdownR = 0;
        TotalTrades = Wins = Losses = 0;
        LongestWinStreak = LongestLossStreak = CurrentStreak = 0;
    }

    public override string ToString() =>
        string.Format("n={0} wr={1:P0} exp={2:F3}R recent={3:F3}R pf={4:F2} maxDD={5:F1}R",
            TotalTrades, WinRate, ExpectancyR, RecentExpectancyR, ProfitFactor, MaxDrawdownR);
}
