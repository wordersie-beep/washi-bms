using System;
using Quant.Core.Config;
using Quant.Core.Numerics;
using Quant.Core.Primitives;

namespace Quant.Core.Data;

/// <summary>
/// Microstructure statistics derived from the tick stream: arrival rate, price velocity and
/// the gap between ticks. These are what detect a feed going quiet or a market dislocating
/// in the seconds before a bar closes.
/// </summary>
public sealed class TickStatistics
{
    private readonly RollingWindow _interArrivalSeconds;
    private readonly RollingWindow _absTickReturns;
    private readonly DataConfig _config;
    private DateTime _lastTickUtc;
    private double _lastMid;
    private bool _hasPrevious;

    public TickStatistics(DataConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _interArrivalSeconds = new RollingWindow(config.TickHistory);
        _absTickReturns = new RollingWindow(config.TickHistory);
    }

    public long TicksSeen { get; private set; }
    public long RejectedTicks { get; private set; }
    public DateTime LastTickUtc => _lastTickUtc;
    public double LastMid => _lastMid;

    public bool IsReady => _interArrivalSeconds.Count >= 30;

    /// <summary>Mean seconds between ticks. Rising means the feed is thinning.</summary>
    public double MeanInterArrivalSeconds => _interArrivalSeconds.Mean;

    /// <summary>Ticks per second over the observed window. Zero when unknown.</summary>
    public double TickRate => MathUtil.SafeDiv(1.0, MeanInterArrivalSeconds);

    /// <summary>Z-score of the latest inter-arrival gap. Large positive means the feed stalled.</summary>
    public double InterArrivalZScore => _interArrivalSeconds.ZScoreOfNewest();

    /// <summary>Z-score of the latest absolute tick return. Large means a violent print.</summary>
    public double VelocityZScore => _absTickReturns.ZScoreOfNewest();

    /// <summary>Percentile rank of the latest absolute tick return, 0..1.</summary>
    public double VelocityPercentile => _absTickReturns.PercentileRankOfNewest(minSample: 30);

    /// <summary>
    /// Folds a quote into the statistics.
    ///
    /// Returns false when the quote is rejected — malformed, out of order, or a price jump
    /// so large relative to ATR that it is far more likely to be a bad print than a real
    /// move. Rejected quotes are counted: a feed producing many of them is itself a reason
    /// to stop trading (spec section 6).
    /// </summary>
    public bool Observe(in Quote quote, double atr)
    {
        if (!quote.IsWellFormed) { RejectedTicks++; return false; }

        if (_hasPrevious)
        {
            if (quote.TimeUtc < _lastTickUtc) { RejectedTicks++; return false; }

            double jump = Math.Abs(quote.Mid - _lastMid);
            if (atr > 0 && jump > atr * _config.MaxTickJumpInAtr)
            {
                RejectedTicks++;
                return false;
            }

            double gapSeconds = (quote.TimeUtc - _lastTickUtc).TotalSeconds;
            if (gapSeconds >= 0) _interArrivalSeconds.Add(gapSeconds);
            if (_lastMid > 0) _absTickReturns.Add(Math.Abs(Math.Log(quote.Mid / _lastMid)));
        }

        _lastTickUtc = quote.TimeUtc;
        _lastMid = quote.Mid;
        _hasPrevious = true;
        TicksSeen++;
        return true;
    }

    /// <summary>Fraction of quotes rejected. A high value means the feed cannot be trusted.</summary>
    public double RejectionRate => MathUtil.SafeDiv(RejectedTicks, RejectedTicks + TicksSeen);

    public double QuoteAgeSeconds(DateTime nowUtc) =>
        _hasPrevious ? Math.Max(0, (nowUtc - _lastTickUtc).TotalSeconds) : double.MaxValue;

    public void Reset()
    {
        _interArrivalSeconds.Clear();
        _absTickReturns.Clear();
        _hasPrevious = false;
        TicksSeen = 0;
        RejectedTicks = 0;
        _lastMid = 0;
        _lastTickUtc = default;
    }
}
