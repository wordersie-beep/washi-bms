using System;
using Quant.Core.Primitives;

namespace Quant.Core.Stats;

/// <summary>
/// The full record of one trade (spec section 123).
///
/// Every field that the decision was made ON is stored alongside the outcome, not just the
/// profit. Storing only P/L means the only question you can ever answer afterwards is
/// "did it work", when the questions worth answering are "in which regime did it work",
/// "at which confidence did it work" and "did the exit give back what the entry earned".
/// </summary>
public sealed class TradeRecord
{
    // --- Identity ---------------------------------------------------------------------
    public string TradeId { get; init; }
    public string SignalId { get; init; }
    public long BrokerPositionId { get; init; }

    // --- Decision inputs, captured at entry --------------------------------------------
    public string SymbolName { get; init; }
    public Side Direction { get; init; }
    public string StrategyName { get; init; }
    public StrategyKind StrategyKind { get; init; }
    public MarketRegime Regime { get; init; }
    public double RegimeConfidence { get; init; }
    public double SignalConfidence { get; init; }
    public double EnsembleConfidence { get; init; }
    public double EffectiveVotes { get; init; }
    public double EstimatedWinProbability { get; init; }
    public double ExpectedValueR { get; init; }
    public double PlannedRewardToRisk { get; init; }
    public SessionKind Session { get; init; }
    public bool IsWeekend { get; init; }
    public int HourUtc { get; init; }
    public DayOfWeek DayOfWeek { get; init; }
    public VolatilityBucket VolatilityBucket { get; init; }
    public double AtrAtEntry { get; init; }
    public double SpreadAtEntry { get; init; }

    // --- Execution ---------------------------------------------------------------------
    public DateTime EntryTimeUtc { get; init; }
    public DateTime ExitTimeUtc { get; init; }
    public double RequestedEntryPrice { get; init; }
    public double EntryPrice { get; init; }
    public double ExitPrice { get; init; }
    public double InitialStopPrice { get; init; }
    public double InitialTargetPrice { get; init; }
    public double VolumeInUnits { get; init; }

    /// <summary>Account-currency amount risked at entry, from entry to initial stop.</summary>
    public double RiskAmount { get; init; }

    /// <summary>Risk as a fraction of equity at entry.</summary>
    public double RiskFractionOfEquity { get; init; }

    // --- Outcome -------------------------------------------------------------------------
    public double GrossProfit { get; init; }
    public double Commission { get; init; }
    public double Swap { get; init; }

    /// <summary>Net profit in account currency, after every cost.</summary>
    public double NetProfit { get; init; }

    /// <summary>Entry slippage in price units, signed against the trade.</summary>
    public double EntrySlippage { get; init; }

    /// <summary>
    /// Result in R, computed from NET profit against the amount risked. R computed from
    /// gross profit is a number that flatters the strategy and cannot be traded.
    /// </summary>
    public double R { get; init; }

    /// <summary>Maximum adverse excursion, in R. Always non-negative.</summary>
    public double MaeR { get; init; }

    /// <summary>Maximum favourable excursion, in R. Always non-negative.</summary>
    public double MfeR { get; init; }

    public ExitReason ExitReason { get; init; }

    /// <summary>True when this trade was simulated rather than sent to the broker.</summary>
    public bool IsVirtual { get; init; }

    /// <summary>Mode the system was running in when the trade was taken.</summary>
    public OperatingMode Mode { get; init; }

    public bool IsWin => R > 0;
    public TimeSpan Duration => ExitTimeUtc > EntryTimeUtc ? ExitTimeUtc - EntryTimeUtc : TimeSpan.Zero;
    public double DurationMinutes => Duration.TotalMinutes;

    /// <summary>
    /// How much of the favourable excursion the exit actually captured, 0..1.
    /// This is the single most useful number for diagnosing exit quality (spec section 129):
    /// a strategy with good entries and a poor capture ratio is losing its edge on the way
    /// out, and no amount of entry tuning will fix that.
    /// </summary>
    public double CaptureRatio => MfeR <= 0 ? 0 : Numerics.MathUtil.Clamp01(R / MfeR);

    public override string ToString() =>
        string.Format("{0} {1} {2} R={3:F2} ({4}) mae={5:F2} mfe={6:F2} {7}",
            EntryTimeUtc.ToString("yyyy-MM-dd HH:mm"), SymbolName, Direction, R, ExitReason, MaeR, MfeR,
            IsVirtual ? "[virtual]" : "");
}
