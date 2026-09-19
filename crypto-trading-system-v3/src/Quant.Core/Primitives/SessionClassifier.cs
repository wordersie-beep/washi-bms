using System;

namespace Quant.Core.Primitives;

/// <summary>
/// Maps a UTC timestamp to a trading session and weekend flag (spec sections 67-68).
/// Crypto never closes, so these are liquidity-shape labels, not open/closed flags, and
/// the system draws no prior conclusion about which of them is profitable.
/// </summary>
public static class SessionClassifier
{
    public static SessionKind Classify(DateTime utc)
    {
        int h = utc.Hour;
        if (h >= 0 && h < 7) return SessionKind.Asia;
        if (h >= 7 && h < 12) return SessionKind.Europe;
        if (h >= 12 && h < 16) return SessionKind.UsOverlap;
        if (h >= 16 && h < 21) return SessionKind.Us;
        return SessionKind.OffHours;
    }

    /// <summary>
    /// Weekend in the sense that matters for crypto flow: traditional markets are shut, so
    /// depth is thinner. Saturday and Sunday, plus Friday after the US close.
    /// </summary>
    public static bool IsWeekend(DateTime utc)
    {
        if (utc.DayOfWeek == DayOfWeek.Saturday || utc.DayOfWeek == DayOfWeek.Sunday) return true;
        return utc.DayOfWeek == DayOfWeek.Friday && utc.Hour >= 21;
    }
}
