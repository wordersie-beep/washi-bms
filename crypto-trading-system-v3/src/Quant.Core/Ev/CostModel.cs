using System;
using Quant.Core.Config;
using Quant.Core.Numerics;
using Quant.Core.Primitives;

namespace Quant.Core.Ev;

/// <summary>A fully itemised round-trip cost estimate, in price units and in R.</summary>
public readonly struct CostEstimate
{
    public CostEstimate(double spreadCost, double commissionCost, double slippageCost, double stopDistance)
    {
        SpreadCost = spreadCost;
        CommissionCost = commissionCost;
        SlippageCost = slippageCost;
        StopDistance = stopDistance;
    }

    /// <summary>Half-spread paid on entry plus half on exit, in price units.</summary>
    public double SpreadCost { get; }

    /// <summary>Round-trip commission expressed in price units.</summary>
    public double CommissionCost { get; }

    /// <summary>Expected slippage on entry plus exit, in price units.</summary>
    public double SlippageCost { get; }

    /// <summary>The stop distance the costs are measured against.</summary>
    public double StopDistance { get; }

    public double TotalPrice => SpreadCost + CommissionCost + SlippageCost;

    /// <summary>
    /// Total round-trip cost expressed in R. This is the number that matters: a trade
    /// costing 0.3R must clear 0.3R before it has achieved anything at all.
    /// </summary>
    public double TotalR => MathUtil.SafeDiv(TotalPrice, StopDistance, 1.0);

    public override string ToString() =>
        string.Format("cost {0:F4} ({1:F2}R): spread {2:F4}, commission {3:F4}, slippage {4:F4}",
            TotalPrice, TotalR, SpreadCost, CommissionCost, SlippageCost);
}

/// <summary>
/// Models what a round trip actually costs (spec sections 57-58).
///
/// Costs are computed from the broker's own terms and the instrument's own recent spread
/// behaviour, never assumed. On small crypto timeframes they are not a rounding error: at a
/// 0.5-ATR stop, a spread of 0.05 ATR plus commission plus slippage can eat a quarter of
/// the risk budget, which is the difference between a profitable system and a busy one.
///
/// The model is deliberately PESSIMISTIC — it costs at the worse of the current and median
/// spread, and it assumes stress slippage in stressed conditions. A cost model that flatters
/// itself produces a backtest that cannot be traded.
/// </summary>
public sealed class CostModel
{
    private readonly ExecutionConfig _config;

    public CostModel(ExecutionConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// Estimates round-trip cost for a trade.
    /// </summary>
    /// <param name="spec">Instrument terms.</param>
    /// <param name="costingSpread">Spread to charge, in price units. Use the robust estimate, not the tightest quote.</param>
    /// <param name="stopDistance">Entry-to-stop distance in price units — the definition of 1R.</param>
    /// <param name="price">Current price, for converting commission from notional to price units.</param>
    /// <param name="isStressed">True when spread or volatility is abnormal, selecting the stress slippage assumption.</param>
    /// <param name="measuredSlippageMultiplier">
    /// Realised slippage relative to the model's expectation, from live execution statistics.
    /// 1.0 means the model has been accurate. Live measurement overrides assumption once it exists.
    /// </param>
    public CostEstimate Estimate(
        SymbolSpec spec,
        double costingSpread,
        double stopDistance,
        double price,
        bool isStressed,
        double measuredSlippageMultiplier = 1.0)
    {
        if (spec == null || stopDistance <= 0 || price <= 0)
        {
            // An uncomputable cost is treated as a PROHIBITIVE one.
            //
            // Returning zero here would have been the opposite: a trade whose cost basis is
            // unknown would look like the cheapest trade available, and would therefore be
            // preferred by expected value and by opportunity ranking. Charging a full 1R
            // guarantees the expected-value gate refuses it instead.
            double denominator = stopDistance > 0 ? stopDistance : 1;
            return new CostEstimate(denominator, 0, 0, denominator);
        }

        double spread = Math.Max(0, costingSpread);

        // Crossing the spread on the way in and again on the way out is one full spread of
        // round-trip cost, not half of one.
        double spreadCost = spread;

        // Commission is quoted per million units of quote-currency notional, one way.
        // Expressing it in price units: (price / 1e6) * rate, doubled for the round trip.
        double commissionCost = 2.0 * (price / 1_000_000.0) * spec.CommissionPerMillionQuote;

        double slippageFraction = isStressed
            ? _config.StressSlippageSpreadFraction
            : _config.NormalSlippageSpreadFraction;

        double slippageCost = spread * slippageFraction * 2.0 * MathUtil.Clamp(measuredSlippageMultiplier, 0.5, 4.0);

        return new CostEstimate(spreadCost, commissionCost, slippageCost, stopDistance);
    }

    /// <summary>
    /// Slippage assumption for one side of a trade, in price units. Used by the shadow and
    /// paper engines so a virtual fill is not a free one.
    /// </summary>
    public double OneWaySlippage(double spread, bool isStressed, double measuredMultiplier = 1.0)
    {
        double fraction = isStressed ? _config.StressSlippageSpreadFraction : _config.NormalSlippageSpreadFraction;
        return Math.Max(0, spread) * fraction * MathUtil.Clamp(measuredMultiplier, 0.5, 4.0);
    }
}
