using System;
using System.Collections.Generic;
using Quant.Core.Numerics;

namespace Quant.Core.Risk;

/// <summary>One equity observation.</summary>
internal readonly struct EquityPoint
{
    public EquityPoint(DateTime timeUtc, double equity) { TimeUtc = timeUtc; Equity = equity; }
    public DateTime TimeUtc { get; }
    public double Equity { get; }
}

/// <summary>
/// Rolling drawdown across several horizons (spec section 37).
///
/// Several horizons rather than one, because they answer different questions. All-time
/// drawdown says whether the system has ever been in trouble; 24-hour drawdown says whether
/// it is in trouble RIGHT NOW. A system can sit at a comfortable all-time figure while
/// losing four percent today, and it is today's number that should stop it trading.
/// </summary>
public sealed class DrawdownTracker
{
    private readonly List<EquityPoint> _history = new List<EquityPoint>();
    private readonly int _maxPoints;

    public DrawdownTracker(int maxPoints = 20000)
    {
        _maxPoints = Math.Max(1000, maxPoints);
    }

    public double AllTimePeak { get; private set; }
    public double CurrentEquity { get; private set; }
    public DateTime LastUpdateUtc { get; private set; }
    public bool HasData => _history.Count > 0;

    /// <summary>Drawdown from the all-time peak, as a percentage of that peak.</summary>
    public double AllTimeDrawdownPercent => PercentBelow(AllTimePeak);

    /// <summary>
    /// Сдвигает пик и всю историю на величину движения денег по счёту.
    ///
    /// Без этого внесённые деньги поднимают исторический пик, и просадка навсегда меряется
    /// от уровня, которого торговля не достигала: лимит просадки срабатывал бы от первого
    /// же отката к тому капиталу, который был до пополнения.
    /// </summary>
    public void NoteCashFlow(double delta)
    {
        if (!MathUtil.IsFinite(delta) || delta == 0) return;

        AllTimePeak = Math.Max(0, AllTimePeak + delta);
        for (int i = 0; i < _history.Count; i++)
        {
            _history[i] = new EquityPoint(_history[i].TimeUtc, Math.Max(0, _history[i].Equity + delta));
        }
    }

    public void Observe(DateTime nowUtc, double equity)
    {
        if (!MathUtil.IsFinite(equity) || equity <= 0) return;

        CurrentEquity = equity;
        LastUpdateUtc = nowUtc;
        if (equity > AllTimePeak) AllTimePeak = equity;

        // Sub-minute granularity adds nothing to a drawdown measure and would make the
        // history unbounded in all but name.
        if (_history.Count > 0 && (nowUtc - _history[_history.Count - 1].TimeUtc).TotalSeconds < 60)
        {
            _history[_history.Count - 1] = new EquityPoint(nowUtc, equity);
            return;
        }

        _history.Add(new EquityPoint(nowUtc, equity));

        // Prune by age first, then hard-cap by count so a long-running process stays bounded.
        DateTime cutoff = nowUtc.AddDays(-31);
        int removeTo = 0;
        while (removeTo < _history.Count && _history[removeTo].TimeUtc < cutoff) removeTo++;
        if (removeTo > 0) _history.RemoveRange(0, removeTo);

        if (_history.Count > _maxPoints) _history.RemoveRange(0, _history.Count - _maxPoints);
    }

    /// <summary>
    /// Drawdown over a trailing window, as a percentage of the peak WITHIN that window.
    /// Zero when the window holds no data.
    /// </summary>
    public double DrawdownPercentOver(TimeSpan window)
    {
        if (_history.Count == 0) return 0;

        DateTime cutoff = LastUpdateUtc - window;
        double peak = 0;
        for (int i = _history.Count - 1; i >= 0; i--)
        {
            if (_history[i].TimeUtc < cutoff) break;
            if (_history[i].Equity > peak) peak = _history[i].Equity;
        }

        return peak <= 0 ? 0 : PercentBelow(peak);
    }

    public double Drawdown24hPercent => DrawdownPercentOver(TimeSpan.FromHours(24));
    public double Drawdown7dPercent => DrawdownPercentOver(TimeSpan.FromDays(7));
    public double Drawdown30dPercent => DrawdownPercentOver(TimeSpan.FromDays(30));

    private double PercentBelow(double peak) =>
        peak <= 0 ? 0 : MathUtil.Clamp(100.0 * (peak - CurrentEquity) / peak, 0, 100);

    /// <summary>
    /// Equity at the start of the trailing window, for measuring period loss against a fixed
    /// reference rather than against a peak.
    /// </summary>
    public double EquityAtOrBefore(DateTime whenUtc)
    {
        for (int i = _history.Count - 1; i >= 0; i--)
        {
            if (_history[i].TimeUtc <= whenUtc) return _history[i].Equity;
        }
        return _history.Count > 0 ? _history[0].Equity : CurrentEquity;
    }

    public void Reset()
    {
        _history.Clear();
        AllTimePeak = 0;
        CurrentEquity = 0;
    }

    /// <summary>Restores persisted state after a restart so drawdown limits survive it.</summary>
    public void Restore(double allTimePeak, double currentEquity, DateTime nowUtc)
    {
        if (allTimePeak > 0) AllTimePeak = allTimePeak;
        if (currentEquity > 0)
        {
            CurrentEquity = currentEquity;
            LastUpdateUtc = nowUtc;
            if (_history.Count == 0) _history.Add(new EquityPoint(nowUtc, currentEquity));
        }
    }
}
