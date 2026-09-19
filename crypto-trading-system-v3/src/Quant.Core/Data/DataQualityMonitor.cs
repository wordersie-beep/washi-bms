using System;
using System.Collections.Generic;
using Quant.Core.Config;
using Quant.Core.Numerics;
using Quant.Core.Primitives;

namespace Quant.Core.Data;

/// <summary>
/// Gatekeeper for market-data integrity (spec section 6).
///
/// The rule it enforces is simple and absolute: if the data is not trustworthy, there is no
/// new trade. It deliberately does NOT force existing positions to close — a stale feed is a
/// reason to stop adding risk, not a reason to dump inventory into a market you cannot
/// currently see. Flattening on a data hiccup is how a bot turns a feed glitch into a
/// realised loss.
///
/// It also returns a graded score rather than a bare boolean, so that "data is technically
/// acceptable but visibly degrading" can reduce position size instead of being invisible.
/// </summary>
public sealed class DataQualityMonitor
{
    private readonly DataConfig _config;

    public DataQualityMonitor(DataConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public DataQualityReport Evaluate(
        DateTime nowUtc,
        SymbolSpec spec,
        Quote latestQuote,
        TimeframeSeries signalSeries,
        TickStatistics ticks,
        SpreadModel spread)
    {
        var issues = new List<string>();
        double score = 1.0;

        if (spec == null)
        {
            issues.Add("symbol specification unavailable");
            return new DataQualityReport(false, 0, issues);
        }

        if (!spec.IsTradingEnabled)
        {
            issues.Add("trading disabled for symbol");
            return new DataQualityReport(false, 0, issues);
        }

        // --- Quote validity -------------------------------------------------------------
        if (!latestQuote.IsWellFormed)
        {
            issues.Add("malformed quote (bid/ask non-positive, inverted or non-finite)");
            return new DataQualityReport(false, 0, issues);
        }

        double quoteAge = ticks == null ? double.MaxValue : ticks.QuoteAgeSeconds(nowUtc);
        if (quoteAge > _config.MaxQuoteAgeSeconds)
        {
            issues.Add($"stale quote ({quoteAge:F0}s old, limit {_config.MaxQuoteAgeSeconds:F0}s)");
            return new DataQualityReport(false, 0, issues);
        }

        // Grade freshness even when it is inside the limit: a feed at 80% of its staleness
        // budget is a feed worth de-risking against.
        score *= 1.0 - (0.4 * MathUtil.LinearScale(quoteAge, _config.MaxQuoteAgeSeconds * 0.5, _config.MaxQuoteAgeSeconds));

        // --- Series readiness -----------------------------------------------------------
        if (signalSeries == null || !signalSeries.IsReady)
        {
            long have = signalSeries?.BarsProcessed ?? 0;
            issues.Add($"insufficient history ({have}/{_config.MinBarsBeforeTrading} bars)");
            return new DataQualityReport(false, 0, issues);
        }

        // --- Gaps -----------------------------------------------------------------------
        if (signalSeries.LastGapBars > 0)
        {
            issues.Add($"bar gap of {signalSeries.LastGapBars} interval(s) at last close");
            double gapPenalty = MathUtil.Clamp01(signalSeries.LastGapBars / _config.MaxBarGapMultiple);
            score *= 1.0 - (0.6 * gapPenalty);

            if (signalSeries.LastGapBars >= _config.MaxBarGapMultiple)
            {
                return new DataQualityReport(false, score, issues);
            }
        }

        // The last closed bar must not be older than one interval plus the gap tolerance,
        // otherwise the series has silently stopped advancing.
        double barAgeMinutes = (nowUtc - signalSeries.LastBarOpenTimeUtc).TotalMinutes;
        double maxBarAge = (int)signalSeries.Timeframe * (1.0 + _config.MaxBarGapMultiple);
        if (barAgeMinutes > maxBarAge)
        {
            issues.Add($"bar series stalled ({barAgeMinutes:F0}m since last close, limit {maxBarAge:F0}m)");
            return new DataQualityReport(false, 0, issues);
        }

        // --- Tick stream health ---------------------------------------------------------
        if (ticks != null && ticks.IsReady)
        {
            if (ticks.RejectionRate > 0.10)
            {
                issues.Add($"high quote rejection rate ({ticks.RejectionRate:P0})");
                score *= 0.5;
            }

            if (ticks.InterArrivalZScore > 4.0)
            {
                issues.Add($"tick arrivals stalling (z={ticks.InterArrivalZScore:F1})");
                score *= 0.7;
            }
        }

        // --- Spread sanity --------------------------------------------------------------
        double atr = signalSeries.Atr.Value;
        if (spread != null && spread.IsReady)
        {
            double spreadToAtr = spread.RelativeToAtr(atr);
            if (spreadToAtr > 1.0)
            {
                issues.Add($"spread exceeds one ATR ({spreadToAtr:F2}x) - quotes are not tradeable");
                return new DataQualityReport(false, 0, issues);
            }
        }

        // --- Price coherence ------------------------------------------------------------
        // The live quote and the last closed bar must be in the same universe. A quote many
        // ATR away from the last close means one of the two is wrong, and the system has no
        // way to tell which.
        if (atr > 0)
        {
            double divergence = Math.Abs(latestQuote.Mid - signalSeries.Last.Close) / atr;
            if (divergence > _config.MaxTickJumpInAtr * 2)
            {
                issues.Add($"quote diverges from last close by {divergence:F1} ATR");
                return new DataQualityReport(false, 0, issues);
            }
            score *= 1.0 - (0.3 * MathUtil.LinearScale(divergence, _config.MaxTickJumpInAtr, _config.MaxTickJumpInAtr * 2));
        }

        score = MathUtil.Clamp01(score);
        return new DataQualityReport(true, score, issues);
    }
}
