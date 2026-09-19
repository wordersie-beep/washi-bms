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
        double leverage,
        bool isTradingEnabled,
        LeverageTier[] leverageTiers = null)
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
        // A non-positive leverage from the broker is treated as 1:1, which over-states the
        // margin a position needs. Over-stating it blocks a trade; under-stating it invites
        // a margin call, so the conservative direction is the only acceptable default.
        Leverage = leverage > 0 ? leverage : 1.0;
        LeverageTiers = leverageTiers;
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

    /// <summary>
    /// Broker leverage for this instrument, read from the platform rather than assumed.
    /// Crypto CFD leverage varies widely between brokers and between instruments at the same
    /// broker, so assuming a value is how a sizing calculation silently becomes wrong.
    /// </summary>
    public double Leverage { get; }

    /// <summary>
    /// Tiered leverage, when the broker publishes it. Ordered by volume ascending.
    ///
    /// Crypto CFDs very often carry tiered leverage: the first few units get one rate and
    /// larger positions get progressively less. Using the headline figure for a size that
    /// falls into a lower tier under-states the margin required, which is the direction of
    /// error that produces a margin call rather than a refused trade.
    /// </summary>
    public LeverageTier[] LeverageTiers { get; }

    public bool IsTradingEnabled { get; }

    /// <summary>
    /// Leverage applicable to a position of this size.
    ///
    /// Picks the first tier whose volume ceiling covers the request; falls back to the
    /// lowest published leverage when the size exceeds every tier, because "larger than the
    /// largest tier" means less leverage, not more.
    /// </summary>
    public double LeverageFor(double units)
    {
        if (LeverageTiers == null || LeverageTiers.Length == 0) return Leverage;

        double lowest = double.MaxValue;
        for (int i = 0; i < LeverageTiers.Length; i++)
        {
            LeverageTier tier = LeverageTiers[i];
            if (tier.Leverage > 0 && tier.Leverage < lowest) lowest = tier.Leverage;
            if (units <= tier.Volume && tier.Leverage > 0) return tier.Leverage;
        }

        return lowest < double.MaxValue ? lowest : Leverage;
    }

    /// <summary>Margin a position of this size would require, from the broker's own leverage.</summary>
    public double EstimateMargin(double units, double price)
    {
        double leverage = LeverageFor(units);
        return leverage <= 0 ? units * price : units * price / leverage;
    }

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

/// <summary>One volume band of a tiered leverage schedule.</summary>
public readonly struct LeverageTier
{
    public LeverageTier(double volumeCeiling, double leverage)
    {
        VolumeCeiling = volumeCeiling;
        Leverage = leverage;
    }

    /// <summary>Largest position volume, in units, to which this tier's leverage applies.</summary>
    public double VolumeCeiling { get; }

    /// <summary>Alias kept for readability at call sites that think in terms of a ceiling.</summary>
    public double Volume => VolumeCeiling;

    public double Leverage { get; }
}
