using System;

namespace Quant.Core.Primitives;

/// <summary>
/// Time source. Everything downstream takes time from here so that backtests, unit tests
/// and live runs are driven by the same code path.
/// </summary>
public interface IClock
{
    DateTime UtcNow { get; }
}

/// <summary>Test/backtest clock whose time is set explicitly.</summary>
public sealed class ManualClock : IClock
{
    public ManualClock(DateTime utcNow) { UtcNow = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc); }

    public DateTime UtcNow { get; private set; }

    public void Set(DateTime utcNow) { UtcNow = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc); }

    public void Advance(TimeSpan by) { UtcNow = UtcNow.Add(by); }
}
