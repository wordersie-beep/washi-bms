using System;
using Quant.Core.Primitives;

namespace Quant.Core.Portfolio;

/// <summary>
/// The system's own record of a live position.
///
/// Held separately from the broker's record on purpose. The broker knows the volume and the
/// stop; only this record knows WHY the trade was taken, what it was expected to do, and how
/// far it has travelled in each direction since. Reconciliation compares the two
/// (spec section 145), and the comparison is only possible because they are kept apart.
/// </summary>
public sealed class OpenPosition
{
    public string TradeId { get; init; }
    public string SignalId { get; init; }
    public long BrokerPositionId { get; set; }
    public string Label { get; init; }

    public string SymbolName { get; init; }
    public Side Direction { get; init; }
    public string StrategyName { get; init; }
    public MarketRegime Regime { get; init; }

    public DateTime EntryTimeUtc { get; init; }
    public double RequestedEntryPrice { get; init; }
    public double EntryPrice { get; set; }
    public double InitialVolumeInUnits { get; init; }
    public double CurrentVolumeInUnits { get; set; }

    public double InitialStopPrice { get; init; }
    public double CurrentStopPrice { get; set; }
    public double Target1Price { get; init; }
    public double Target2Price { get; init; }

    /// <summary>Entry-to-initial-stop distance in price units. The definition of 1R for this trade.</summary>
    public double RiskPerUnit { get; init; }

    /// <summary>Account-currency amount risked at entry.</summary>
    public double RiskAmount { get; init; }

    public double RiskFractionOfEquity { get; init; }

    // --- Decision context, carried so the journal and the statistics can slice on it -----
    public double SignalConfidence { get; init; }
    public double EnsembleConfidence { get; init; }
    public double EffectiveVotes { get; init; }
    public double EstimatedWinProbability { get; init; }
    public double ExpectedValueR { get; init; }
    public double PlannedRewardToRisk { get; init; }
    public SessionKind Session { get; init; }
    public bool IsWeekend { get; init; }
    public VolatilityBucket VolatilityBucket { get; init; }
    public double AtrAtEntry { get; init; }
    public double SpreadAtEntry { get; init; }

    /// <summary>The level whose breach means the entry reason no longer holds (spec section 49).</summary>
    public double? InvalidationPrice { get; init; }

    public OperatingMode Mode { get; init; }
    public bool IsVirtual { get; init; }

    // --- Mutable journey state ------------------------------------------------------------
    public double MaxFavourableExcursionR { get; set; }
    public double MaxAdverseExcursionR { get; set; }
    public double PeakOpenProfitR { get; set; }
    public bool Target1Filled { get; set; }
    public bool Target2Filled { get; set; }
    public bool BreakEvenApplied { get; set; }
    public bool TrailingActive { get; set; }
    public int BarsHeld { get; set; }
    public double RealisedProfit { get; set; }
    public double RealisedCommission { get; set; }

    /// <summary>Open profit in R at the given price, before costs.</summary>
    public double OpenProfitR(double currentPrice)
    {
        if (RiskPerUnit <= 0) return 0;
        double move = Direction == Side.Long ? currentPrice - EntryPrice : EntryPrice - currentPrice;
        return move / RiskPerUnit;
    }

    /// <summary>Records the journey extremes. Call on every price update.</summary>
    public void UpdateExcursions(double currentPrice)
    {
        double r = OpenProfitR(currentPrice);
        if (r > MaxFavourableExcursionR) MaxFavourableExcursionR = r;
        if (-r > MaxAdverseExcursionR) MaxAdverseExcursionR = -r;
        if (r > PeakOpenProfitR) PeakOpenProfitR = r;
    }

    /// <summary>Fraction of the original position still open.</summary>
    public double RemainingFraction =>
        InitialVolumeInUnits <= 0 ? 0 : Numerics.MathUtil.Clamp01(CurrentVolumeInUnits / InitialVolumeInUnits);

    public override string ToString() =>
        string.Format("{0} {1} {2} @{3} stop {4} ({5:P0} open, {6})",
            SymbolName, Direction, StrategyName, EntryPrice, CurrentStopPrice, RemainingFraction,
            IsVirtual ? "virtual" : "live");
}
