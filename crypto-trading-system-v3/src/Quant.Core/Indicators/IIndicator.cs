namespace Quant.Core.Indicators;

/// <summary>
/// Every indicator in this system is incremental and is fed CLOSED bars only.
///
/// That is a deliberate structural choice, not a stylistic one. The platform's own
/// indicators recompute against a forming bar, which makes a backtest and a live run
/// disagree and makes look-ahead easy to introduce by accident. Owning the arithmetic
/// means the same numbers appear in unit tests, backtests, optimisation and live.
/// </summary>
public interface IIndicator
{
    /// <summary>True once enough closed bars have been seen for the value to be meaningful.</summary>
    bool IsReady { get; }

    /// <summary>Current value. Undefined (and must not be used) while <see cref="IsReady"/> is false.</summary>
    double Value { get; }

    void Reset();
}
