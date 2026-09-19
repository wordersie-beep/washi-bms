using System;
using System.Collections.Generic;
using Quant.Core.Config;
using Quant.Core.Numerics;
using Quant.Core.Primitives;

namespace Quant.Core.Portfolio;

/// <summary>Current portfolio exposure, in percent of equity.</summary>
public sealed class PortfolioExposure
{
    /// <summary>
    /// Total open risk: the sum of what every position would lose if all of them were
    /// stopped out at their current stops. This is the number the dashboard calls
    /// "portfolio heat" (spec section 113).
    /// </summary>
    public double TotalOpenRiskPercent { get; init; }

    /// <summary>Open risk by symbol.</summary>
    public IReadOnlyDictionary<string, double> BySymbol { get; init; }

    /// <summary>Open risk by correlation cluster.</summary>
    public IReadOnlyDictionary<int, double> ByCluster { get; init; }

    /// <summary>Net long minus net short risk, uncorrected for correlation.</summary>
    public double NetDirectionalRiskPercent { get; init; }

    /// <summary>
    /// Directional risk after correlation adjustment.
    ///
    /// The difference between this and <see cref="NetDirectionalRiskPercent"/> is the whole
    /// point: four uncorrelated longs of 0.3% each carry meaningfully less than 1.2% of real
    /// directional risk, while four perfectly correlated ones carry exactly 1.2%. Sizing
    /// against the nominal figure over-risks in precisely the conditions where it matters.
    /// </summary>
    public double CorrelationAdjustedDirectionalRiskPercent { get; init; }

    public int OpenPositionCount { get; init; }

    public double RiskInSymbol(string symbol) =>
        BySymbol != null && BySymbol.TryGetValue(symbol, out double v) ? v : 0;

    public double RiskInCluster(int cluster) =>
        ByCluster != null && ByCluster.TryGetValue(cluster, out double v) ? v : 0;

    public override string ToString() =>
        string.Format("heat {0:F2}% across {1} positions, directional {2:F2}% (correlation-adjusted {3:F2}%)",
            TotalOpenRiskPercent, OpenPositionCount, NetDirectionalRiskPercent, CorrelationAdjustedDirectionalRiskPercent);
}

/// <summary>
/// Aggregates open risk and enforces the portfolio-level limits (spec sections 29, 116).
/// </summary>
public sealed class PortfolioRiskEngine
{
    private readonly PortfolioConfig _config;
    private readonly RiskConfig _riskConfig;
    private readonly CorrelationEngine _correlation;

    public PortfolioRiskEngine(PortfolioConfig config, RiskConfig riskConfig, CorrelationEngine correlation)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _riskConfig = riskConfig ?? throw new ArgumentNullException(nameof(riskConfig));
        _correlation = correlation ?? throw new ArgumentNullException(nameof(correlation));
    }

    /// <summary>
    /// Computes current exposure.
    ///
    /// Risk per position is measured from the CURRENT stop, not the original one. A position
    /// whose stop has been moved to break-even carries no downside risk any more, and
    /// charging the portfolio for risk that no longer exists would needlessly block new
    /// trades — which is the same error as ignoring risk that does exist, in the other
    /// direction.
    /// </summary>
    public PortfolioExposure Compute(IReadOnlyList<OpenPosition> positions, double equity, IReadOnlyDictionary<string, int> clusters)
    {
        var bySymbol = new Dictionary<string, double>(StringComparer.Ordinal);
        var byCluster = new Dictionary<int, double>();

        if (positions == null || positions.Count == 0 || equity <= 0)
        {
            return new PortfolioExposure
            {
                TotalOpenRiskPercent = 0,
                BySymbol = bySymbol,
                ByCluster = byCluster,
                NetDirectionalRiskPercent = 0,
                CorrelationAdjustedDirectionalRiskPercent = 0,
                OpenPositionCount = 0,
            };
        }

        double total = 0;
        double netDirectional = 0;
        var signedRisks = new List<(string Symbol, double SignedRisk)>();

        for (int i = 0; i < positions.Count; i++)
        {
            OpenPosition p = positions[i];
            if (p == null || p.IsVirtual) continue;

            double riskPercent = CurrentRiskPercent(p, equity);
            total += riskPercent;

            bySymbol.TryGetValue(p.SymbolName, out double symbolRisk);
            bySymbol[p.SymbolName] = symbolRisk + riskPercent;

            int cluster = _correlation.ClusterOf(clusters, p.SymbolName);
            byCluster.TryGetValue(cluster, out double clusterRisk);
            byCluster[cluster] = clusterRisk + riskPercent;

            double signed = p.Direction == Side.Long ? riskPercent : -riskPercent;
            netDirectional += signed;
            signedRisks.Add((p.SymbolName, signed));
        }

        return new PortfolioExposure
        {
            TotalOpenRiskPercent = total,
            BySymbol = bySymbol,
            ByCluster = byCluster,
            NetDirectionalRiskPercent = netDirectional,
            CorrelationAdjustedDirectionalRiskPercent = CorrelationAdjusted(signedRisks),
            OpenPositionCount = signedRisks.Count,
        };
    }

    /// <summary>
    /// Risk still at stake in a position, as a percentage of equity. Zero once the stop is
    /// at or beyond break-even.
    /// </summary>
    public static double CurrentRiskPercent(OpenPosition p, double equity)
    {
        if (p == null || equity <= 0 || p.CurrentVolumeInUnits <= 0) return 0;

        double distance = p.Direction == Side.Long
            ? p.EntryPrice - p.CurrentStopPrice
            : p.CurrentStopPrice - p.EntryPrice;

        if (distance <= 0) return 0;   // stop at or past break-even

        // Расстояние в цене, объём в единицах, капитал в валюте счёта. Перевод обязателен:
        // без него весь портфельный риск считается в котируемой валюте и сравнивается с
        // лимитами, заданными в процентах от счёта, — то есть лимиты смещены на курс.
        double amountAtRisk = distance * p.CurrentVolumeInUnits * p.MoneyPerPricePerUnit;
        return MathUtil.Clamp(100.0 * amountAtRisk / equity, 0, 100);
    }

    /// <summary>
    /// Correlation-adjusted directional risk, computed as the quadratic form
    /// sqrt(w' * C * w) over the signed position risks.
    ///
    /// This is the standard portfolio-variance calculation and it is used here for exactly
    /// the reason it exists: it is the only formulation in which perfectly correlated
    /// positions add linearly and uncorrelated ones add in quadrature. Summing nominal risks
    /// over-states a diversified book; ignoring correlation under-states a concentrated one.
    /// </summary>
    private double CorrelationAdjusted(List<(string Symbol, double SignedRisk)> risks)
    {
        if (risks.Count == 0) return 0;
        if (risks.Count == 1) return Math.Abs(risks[0].SignedRisk);

        double variance = 0;
        for (int i = 0; i < risks.Count; i++)
        {
            for (int j = 0; j < risks.Count; j++)
            {
                double correlation = i == j ? 1.0 : _correlation.Correlation(risks[i].Symbol, risks[j].Symbol);
                variance += risks[i].SignedRisk * risks[j].SignedRisk * correlation;
            }
        }

        return variance <= 0 ? 0 : Math.Sqrt(variance);
    }

    /// <summary>
    /// Checks whether a proposed position fits inside the portfolio limits.
    /// Returns <see cref="NoTradeReason.None"/> when it does.
    /// </summary>
    public NoTradeReason CheckLimits(
        PortfolioExposure exposure,
        string symbol,
        Side direction,
        double proposedRiskPercent,
        IReadOnlyDictionary<string, int> clusters,
        int positionsInSymbol,
        out string detail)
    {
        detail = null;

        if (exposure.OpenPositionCount >= _config.MaxOpenPositions)
        {
            detail = $"{exposure.OpenPositionCount} positions open, limit {_config.MaxOpenPositions}";
            return NoTradeReason.MaxPositionsReached;
        }

        if (positionsInSymbol >= _config.MaxPositionsPerSymbol)
        {
            detail = $"{positionsInSymbol} positions already open in {symbol}, limit {_config.MaxPositionsPerSymbol}";
            return NoTradeReason.PositionAlreadyOpen;
        }

        double newTotal = exposure.TotalOpenRiskPercent + proposedRiskPercent;
        if (newTotal > _riskConfig.MaxTotalOpenRiskPercent)
        {
            detail = $"total open risk would reach {newTotal:F2}%, limit {_riskConfig.MaxTotalOpenRiskPercent:F2}%";
            return NoTradeReason.PortfolioRiskLimit;
        }

        double newSymbol = exposure.RiskInSymbol(symbol) + proposedRiskPercent;
        if (newSymbol > _config.MaxSymbolRiskPercent)
        {
            detail = $"{symbol} risk would reach {newSymbol:F2}%, limit {_config.MaxSymbolRiskPercent:F2}%";
            return NoTradeReason.SymbolExposureLimit;
        }

        int cluster = _correlation.ClusterOf(clusters, symbol);
        double newCluster = exposure.RiskInCluster(cluster) + proposedRiskPercent;
        if (newCluster > _config.MaxClusterRiskPercent)
        {
            detail = $"cluster {cluster} risk would reach {newCluster:F2}%, limit {_config.MaxClusterRiskPercent:F2}%";
            return NoTradeReason.ClusterExposureLimit;
        }

        // Directional limit, on the correlation-adjusted figure.
        //
        // The book's correlation-adjusted risk is sqrt(w'Cw) -- a MAGNITUDE, always
        // non-negative. Adding the candidate's SIGNED risk to it was a unit error: on a
        // short-heavy book each further short subtracted from the magnitude, so the
        // projection shrank as exposure grew and the limit never bound at all.
        //
        // The candidate is therefore combined on the signed NET exposure, which does carry
        // a direction, and the correlation-adjusted magnitude is used to scale the result.
        // The approximation assumes the candidate correlates with the book as its mean
        // correlation suggests, which avoids rebuilding the full quadratic form for a
        // candidate that may be rejected anyway.
        double signed = direction == Side.Long ? proposedRiskPercent : -proposedRiskPercent;
        double meanCorrelation = MathUtil.Clamp(_correlation.MeanAbsoluteCorrelation(symbol), 0.2, 1.0);

        double projectedNet = Math.Abs(exposure.NetDirectionalRiskPercent + signed);

        // Scale the net figure by how much the book's own correlation structure inflates it
        // relative to a naive sum; a perfectly diversified book scales down, a perfectly
        // correlated one does not scale at all.
        double inflation = MathUtil.SafeDiv(
            exposure.CorrelationAdjustedDirectionalRiskPercent,
            Math.Abs(exposure.NetDirectionalRiskPercent),
            fallback: 1.0);

        double projected = projectedNet * MathUtil.Clamp(inflation, meanCorrelation, 1.5);

        if (projected > _config.MaxDirectionalRiskPercent)
        {
            detail = $"correlation-adjusted directional risk would reach {projected:F2}%, limit {_config.MaxDirectionalRiskPercent:F2}%";
            return NoTradeReason.DirectionalExposureLimit;
        }

        return NoTradeReason.None;
    }
}
