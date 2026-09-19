using System;
using System.Collections.Generic;
using Quant.Core.Config;
using Quant.Core.Features;
using Quant.Core.Numerics;

namespace Quant.Core.Risk;

/// <summary>Outcome of the anomaly scan for one symbol on one bar.</summary>
public readonly struct AnomalyReport
{
    public AnomalyReport(double severity, bool isExtremeEvent, IReadOnlyList<string> findings)
    {
        Severity = severity;
        IsExtremeEvent = isExtremeEvent;
        Findings = findings ?? Array.Empty<string>();
    }

    /// <summary>0..1. Above zero reduces risk; at 1 the market is behaving unrecognisably.</summary>
    public double Severity { get; }

    /// <summary>True when conditions warrant suspending new entries entirely (spec section 65).</summary>
    public bool IsExtremeEvent { get; }

    public IReadOnlyList<string> Findings { get; }

    public string Summary => Findings.Count == 0 ? "normal" : string.Join("; ", Findings);

    public override string ToString() =>
        string.Format("anomaly severity={0:F2}{1} [{2}]", Severity, IsExtremeEvent ? " EXTREME" : "", Summary);
}

/// <summary>
/// Detects abnormal market states from price, volume, volatility, spread and feed behaviour
/// (spec sections 65, 72-73).
///
/// This is the system's substitute for a news feed. A cloud-hosted cBot cannot reliably call
/// an external event API, and building the trading loop around one would make an outage a
/// trading outage. What it CAN do is notice that the market is behaving as though something
/// has happened: price velocity far outside its distribution, volume far outside its
/// distribution, spreads blowing out, the tick stream stalling. Those are the observable
/// consequences of news, and unlike a headline they arrive with no subscription and no
/// latency (spec section 72).
///
/// The detector is deliberately blind to WHY. It is not trying to identify the event, only
/// to notice that the distribution the strategies were fitted to no longer describes the
/// market in front of them.
/// </summary>
public sealed class AnomalyDetector
{
    private readonly RiskConfig _config;
    private int _barsSinceExtremeEvent = int.MaxValue;

    public AnomalyDetector(RiskConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>Bars elapsed since the last extreme event, for the recovery hold.</summary>
    public int BarsSinceExtremeEvent => _barsSinceExtremeEvent;

    /// <summary>
    /// True while the market is still inside the post-event recovery hold (spec section 66).
    /// Returning to full size the instant a shock bar closes means trading the aftershock.
    /// </summary>
    public bool InRecoveryPeriod => _barsSinceExtremeEvent < _config.RecoveryBarsAfterExtremeEvent;

    /// <summary>Call once per closed signal bar to advance the recovery counter.</summary>
    public void OnBarClosed()
    {
        if (_barsSinceExtremeEvent < int.MaxValue) _barsSinceExtremeEvent++;
    }

    public AnomalyReport Evaluate(FeatureVector f)
    {
        if (f == null) return new AnomalyReport(1.0, true, new[] { "no features available" });

        var findings = new List<string>();
        double severity = 0;
        bool extreme = false;
        double z = _config.AnomalyZThreshold;

        // --- Price velocity --------------------------------------------------------------
        double moveInAtr = Math.Abs(f.Return1InAtr);
        if (moveInAtr > 3.0)
        {
            findings.Add($"price moved {moveInAtr:F1} ATR in one bar");
            severity = Math.Max(severity, MathUtil.LinearScale(moveInAtr, 3.0, 6.0));
            if (moveInAtr > 5.0) extreme = true;
        }

        // --- Tick-level velocity ----------------------------------------------------------
        if (f.TickVelocityZScore > z)
        {
            findings.Add($"tick velocity z={f.TickVelocityZScore:F1}");
            severity = Math.Max(severity, MathUtil.LinearScale(f.TickVelocityZScore, z, z * 2));
        }

        // --- Volume shock -------------------------------------------------------------------
        if (f.VolumeZScore > z)
        {
            findings.Add($"volume z={f.VolumeZScore:F1}");
            severity = Math.Max(severity, MathUtil.LinearScale(f.VolumeZScore, z, z * 2));
            if (f.VolumeZScore > z * 2) extreme = true;
        }

        // --- Volatility shock ---------------------------------------------------------------
        if (f.AtrZScore > z)
        {
            findings.Add($"volatility z={f.AtrZScore:F1}");
            severity = Math.Max(severity, MathUtil.LinearScale(f.AtrZScore, z, z * 2));
        }

        if (f.AtrPercentile > 0.99 && f.VolatilityExpansion > 0.30)
        {
            findings.Add("volatility at an extreme and still expanding");
            severity = Math.Max(severity, 0.85);
            extreme = true;
        }

        // --- Liquidity ------------------------------------------------------------------------
        if (f.SpreadPercentile > 0.98)
        {
            findings.Add($"spread at the {f.SpreadPercentile:P0} percentile");
            severity = Math.Max(severity, 0.7);
        }

        if (f.SpreadToAtr > 0.30)
        {
            findings.Add($"spread is {f.SpreadToAtr:P0} of ATR");
            severity = Math.Max(severity, 0.8);
            extreme = true;
        }

        // --- Feed health ---------------------------------------------------------------------
        if (f.TickInterArrivalZScore > z)
        {
            findings.Add($"tick arrivals stalling (z={f.TickInterArrivalZScore:F1})");
            severity = Math.Max(severity, MathUtil.LinearScale(f.TickInterArrivalZScore, z, z * 2));
        }

        // --- Combined dislocation ---------------------------------------------------------------
        // The individually-tolerable readings that together describe a market coming apart:
        // a violent move, on abnormal volume, with spreads widening. Any one is noise; all
        // three at once is a dislocation, and treating it as three separate mild warnings is
        // how a system trades straight into one.
        if (moveInAtr > 2.0 && f.VolumeZScore > 2.0 && f.SpreadPercentile > 0.90)
        {
            findings.Add("simultaneous price, volume and spread dislocation");
            severity = Math.Max(severity, 0.9);
            extreme = true;
        }

        if (extreme) _barsSinceExtremeEvent = 0;

        return new AnomalyReport(MathUtil.Clamp01(severity), extreme, findings);
    }

    public void Reset() => _barsSinceExtremeEvent = int.MaxValue;
}
