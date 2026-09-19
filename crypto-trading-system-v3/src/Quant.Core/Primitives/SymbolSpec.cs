using System;

namespace Quant.Core.Primitives;

/// <summary>
/// Everything the decision stack needs to know about an instrument's contract terms.
/// Populated from the broker at run time (spec section 60) rather than assumed, because
/// crypto CFD terms differ sharply between brokers.
/// </summary>
public sealed class SymbolSpec
{
    public SymbolSpec(
        string name,
        double pipSize,
        double tickSize,
        int digits,
        double volumeInUnitsMin,
        double volumeInUnitsMax,
        double volumeInUnitsStep,
        double commissionPerMillionQuote,
        double pipValuePerUnit,
        double minStopLossDistancePrice,
        bool isTradingEnabled)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Symbol name is required.", nameof(name));

        Name = name;
        PipSize = pipSize > 0 ? pipSize : 0.01;
        TickSize = tickSize > 0 ? tickSize : PipSize;
        Digits = digits;
        VolumeInUnitsMin = Math.Max(0, volumeInUnitsMin);
        VolumeInUnitsMax = volumeInUnitsMax > 0 ? volumeInUnitsMax : double.MaxValue;
        VolumeInUnitsStep = volumeInUnitsStep > 0 ? volumeInUnitsStep : 0.01;
        CommissionPerMillionQuote = Math.Max(0, commissionPerMillionQuote);
        PipValuePerUnit = pipValuePerUnit;
        MinStopLossDistancePrice = Math.Max(0, minStopLossDistancePrice);
        IsTradingEnabled = isTradingEnabled;
    }

    public string Name { get; }
    public double PipSize { get; }
    public double TickSize { get; }
    public int Digits { get; }
    public double VolumeInUnitsMin { get; }
    public double VolumeInUnitsMax { get; }
    public double VolumeInUnitsStep { get; }

    /// <summary>
    /// Commission expressed per million units of quote-currency volume, one way.
    /// Round-turn cost is twice this.
    /// </summary>
    public double CommissionPerMillionQuote { get; }

    /// <summary>Account-currency value of one pip for one unit of volume.</summary>
    public double PipValuePerUnit { get; }

    /// <summary>Broker-enforced minimum stop distance, in price units. Zero when unconstrained.</summary>
    public double MinStopLossDistancePrice { get; }

    public bool IsTradingEnabled { get; }

    public double PriceToPips(double priceDistance) => PipSize <= 0 ? 0 : priceDistance / PipSize;

    public double PipsToPrice(double pips) => pips * PipSize;

    /// <summary>Rounds a price to the instrument's tick grid, away from nothing — plain nearest.</summary>
    public double RoundToTick(double price)
    {
        if (TickSize <= 0) return price;
        return Math.Round(price / TickSize, MidpointRounding.AwayFromZero) * TickSize;
    }

    /// <summary>
    /// Snaps a requested volume DOWN onto the broker's volume grid. Always rounds down so a
    /// sizing decision can never be silently inflated past its risk budget.
    /// Returns 0 when the request cannot be met.
    /// </summary>
    public double NormalizeVolumeDown(double requestedUnits)
    {
        if (double.IsNaN(requestedUnits) || requestedUnits <= 0) return 0;

        double capped = Math.Min(requestedUnits, VolumeInUnitsMax);
        if (capped < VolumeInUnitsMin) return 0;

        double steps = Math.Floor((capped - VolumeInUnitsMin) / VolumeInUnitsStep);
        double result = VolumeInUnitsMin + (steps * VolumeInUnitsStep);

        // Guard against floating-point drift pushing us just over the cap.
        if (result > VolumeInUnitsMax) result -= VolumeInUnitsStep;
        return result < VolumeInUnitsMin ? 0 : Math.Round(result, 8);
    }

    public override string ToString() => Name;
}
