using System;
using Quant.Core.Numerics;
using Quant.Core.Primitives;

namespace Quant.Core.Features;

/// <summary>
/// Market-wide crypto context (spec sections 25-27), derived from the benchmark instrument
/// — in practice BTC, because most of the complex is still a leveraged expression of it.
///
/// This is deliberately an ADJUSTMENT, not a veto. A hard "no longs unless BTC is trending
/// up" filter throws away every genuine idiosyncratic move and, worse, makes the whole
/// portfolio one position. The benchmark nudges probability and size; it never decides.
/// </summary>
public sealed class GlobalMarketContext
{
    public static readonly GlobalMarketContext Unavailable = new GlobalMarketContext
    {
        IsAvailable = false,
        TrendStrength = 0,
        VolatilityPercentile = 0.5,
        MomentumInAtr = 0,
        Regime = MarketRegime.Unknown,
        RegimeConfidence = 0,
    };

    public bool IsAvailable { get; init; }

    /// <summary>Benchmark trend strength, -1..+1.</summary>
    public double TrendStrength { get; init; }

    /// <summary>Benchmark ATR percentile, 0..1.</summary>
    public double VolatilityPercentile { get; init; }

    /// <summary>Benchmark 20-bar momentum in ATR units.</summary>
    public double MomentumInAtr { get; init; }

    public MarketRegime Regime { get; init; }
    public double RegimeConfidence { get; init; }

    /// <summary>Mean pairwise absolute correlation across the traded universe, 0..1.</summary>
    public double UniverseCorrelation { get; init; }

    /// <summary>
    /// Whether the complex is moving as one block. When true, apparent diversification is
    /// an illusion and portfolio limits must bind harder.
    /// </summary>
    public bool IsCorrelationStressed => UniverseCorrelation >= 0.80;

    /// <summary>
    /// Probability adjustment for a directional idea, in -1..+1, where positive means the
    /// benchmark backs the idea. Scaled by the benchmark's own regime confidence so a
    /// murky BTC tape contributes little.
    /// </summary>
    public double DirectionalAgreement(Side side)
    {
        if (!IsAvailable || side == Side.None) return 0;

        double directional = side == Side.Long ? TrendStrength : -TrendStrength;
        return MathUtil.Clamp(directional * MathUtil.Clamp01(RegimeConfidence), -1, 1);
    }

    public override string ToString() =>
        IsAvailable
            ? string.Format("BTC {0} conf={1:P0} trend={2:F2} volPct={3:P0} corr={4:F2}",
                Regime, RegimeConfidence, TrendStrength, VolatilityPercentile, UniverseCorrelation)
            : "benchmark unavailable";
}
