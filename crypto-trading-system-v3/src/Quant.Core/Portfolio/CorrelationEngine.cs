using System;
using System.Collections.Generic;
using Quant.Core.Config;
using Quant.Core.Numerics;
using Quant.Core.Primitives;

namespace Quant.Core.Portfolio;

/// <summary>
/// Rolling cross-asset correlation and the risk clusters derived from it
/// (spec sections 28, 77).
///
/// The reason this exists is that crypto diversification is usually an illusion. Four
/// positions in four different coins, all long, during a correlated sell-off is one
/// position at four times the size — and the account discovers this at exactly the moment it
/// can least afford to. Clustering by realised correlation is what lets the portfolio limits
/// bind on the real exposure rather than on the nominal one.
///
/// Correlation is measured over several windows. A short window reacts fast but is noisy; a
/// long one is stable but slow to notice a correlation spike. The system takes the MAXIMUM
/// across windows, which is the conservative reading: if any timescale says these two assets
/// are moving together, treat them as though they are.
/// </summary>
public sealed class CorrelationEngine
{
    private readonly PortfolioConfig _config;
    private readonly Dictionary<string, RollingWindow> _returns = new Dictionary<string, RollingWindow>(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _lastClose = new Dictionary<string, double>(StringComparer.Ordinal);
    private readonly int _capacity;

    public CorrelationEngine(PortfolioConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));

        int longest = 0;
        for (int i = 0; i < config.CorrelationWindows.Length; i++)
        {
            if (config.CorrelationWindows[i] > longest) longest = config.CorrelationWindows[i];
        }
        _capacity = Math.Max(20, longest);
    }

    public IReadOnlyCollection<string> TrackedSymbols => _returns.Keys;

    /// <summary>
    /// Records a close on the correlation timeframe. Callers must only pass CLOSED bars on
    /// <see cref="PortfolioConfig.CorrelationTimeframe"/>, so that all series are sampled on
    /// the same clock — correlating a 5-minute series against a 1-hour one measures nothing.
    /// </summary>
    public void Observe(string symbol, double close)
    {
        if (string.IsNullOrEmpty(symbol) || !MathUtil.IsFinite(close) || close <= 0) return;

        if (_lastClose.TryGetValue(symbol, out double previous) && previous > 0)
        {
            if (!_returns.TryGetValue(symbol, out RollingWindow w))
            {
                w = new RollingWindow(_capacity);
                _returns[symbol] = w;
            }
            w.Add(Math.Log(close / previous));
        }

        _lastClose[symbol] = close;
    }

    /// <summary>
    /// Significance bar a SHORT window must clear before it is allowed to override the
    /// pooled estimate, as a multiple of its own sampling error. Set high deliberately:
    /// this is the override that exists to catch a genuine correlation spike, and a spike
    /// that is not overwhelming is indistinguishable from noise.
    /// </summary>
    private const double SpikeSignificanceSigma = 2.5;

    /// <summary>
    /// Correlation between two symbols in -1..1.
    ///
    /// Estimates from windows of different lengths are pooled through the FISHER
    /// z-TRANSFORM, weighted by (n - 3). That is the standard way to combine correlation
    /// estimates of differing sample sizes, and it matters here because the naive
    /// alternative -- taking the largest reading across windows -- is dominated by the
    /// shortest one, where sampling noise alone produces apparent correlations around 0.45
    /// between genuinely independent series. Treating that as real would overstate risk on
    /// every book and block trades for no reason.
    ///
    /// The conservative intent behind the original maximum is kept as a separate SPIKE
    /// OVERRIDE: the shortest window may override the pooled estimate, but only when it is
    /// both higher AND significant at <see cref="SpikeSignificanceSigma"/> standard errors.
    /// That preserves the ability to notice a correlation regime change within a few bars
    /// without paying for it with permanent phantom correlation.
    /// </summary>
    public double Correlation(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal)) return 1.0;
        if (!_returns.TryGetValue(a, out RollingWindow wa) || !_returns.TryGetValue(b, out RollingWindow wb)) return 0;

        double zSum = 0;
        double weightSum = 0;
        double spike = 0;
        int shortestWindow = int.MaxValue;

        for (int i = 0; i < _config.CorrelationWindows.Length; i++)
        {
            int window = _config.CorrelationWindows[i];
            int n = Math.Min(Math.Min(wa.Count, wb.Count), window);
            if (n < 15) continue;

            double raw = wa.CorrelationWith(wb, window, minSample: 15);

            // Fisher z is undefined at +/-1, so the input is nudged inside the open interval.
            double bounded = MathUtil.Clamp(raw, -0.999999, 0.999999);
            double z = 0.5 * Math.Log((1 + bounded) / (1 - bounded));

            double weight = Math.Max(n - 3, 1);
            zSum += z * weight;
            weightSum += weight;

            // Spike candidate from the shortest window that has enough data.
            if (window < shortestWindow)
            {
                shortestWindow = window;
                double significanceFloor = SpikeSignificanceSigma / Math.Sqrt(Math.Max(n - 3, 1));
                spike = Math.Abs(raw) > significanceFloor ? raw : 0;
            }
        }

        if (weightSum <= 0) return 0;

        double pooledZ = zSum / weightSum;
        double pooled = Math.Tanh(pooledZ);

        // The override only ever raises the reading, never lowers it.
        double result = Math.Abs(spike) > Math.Abs(pooled) ? spike : pooled;
        return MathUtil.Clamp(result, -1, 1);
    }

    /// <summary>Mean absolute correlation of one symbol against every other tracked symbol.</summary>
    public double MeanAbsoluteCorrelation(string symbol)
    {
        double sum = 0;
        int n = 0;
        foreach (KeyValuePair<string, RollingWindow> kv in _returns)
        {
            if (string.Equals(kv.Key, symbol, StringComparison.Ordinal)) continue;
            sum += Math.Abs(Correlation(symbol, kv.Key));
            n++;
        }
        return n == 0 ? 0 : sum / n;
    }

    /// <summary>Mean pairwise absolute correlation across the whole universe (spec section 25).</summary>
    public double UniverseCorrelation()
    {
        var symbols = new List<string>(_returns.Keys);
        if (symbols.Count < 2) return 0;

        double sum = 0;
        int pairs = 0;
        for (int i = 0; i < symbols.Count; i++)
        {
            for (int j = i + 1; j < symbols.Count; j++)
            {
                sum += Math.Abs(Correlation(symbols[i], symbols[j]));
                pairs++;
            }
        }
        return pairs == 0 ? 0 : sum / pairs;
    }

    /// <summary>
    /// Groups symbols into risk clusters by single-linkage agglomeration at the configured
    /// threshold. Single linkage on purpose: if A is correlated with B and B with C, then A
    /// and C belong in the same risk bucket even if their direct correlation is unremarkable,
    /// because B transmits the shock between them.
    /// </summary>
    public IReadOnlyDictionary<string, int> BuildClusters()
    {
        var symbols = new List<string>(_returns.Keys);
        symbols.Sort(StringComparer.Ordinal);   // deterministic cluster numbering

        var clusterOf = new Dictionary<string, int>(StringComparer.Ordinal);
        int nextCluster = 0;

        for (int i = 0; i < symbols.Count; i++)
        {
            string symbol = symbols[i];
            if (clusterOf.ContainsKey(symbol)) continue;

            int cluster = nextCluster++;
            clusterOf[symbol] = cluster;

            // Breadth-first expansion, so transitive links are followed.
            var frontier = new Queue<string>();
            frontier.Enqueue(symbol);

            while (frontier.Count > 0)
            {
                string current = frontier.Dequeue();
                for (int j = 0; j < symbols.Count; j++)
                {
                    string candidate = symbols[j];
                    if (clusterOf.ContainsKey(candidate)) continue;
                    if (Math.Abs(Correlation(current, candidate)) < _config.ClusterThreshold) continue;

                    clusterOf[candidate] = cluster;
                    frontier.Enqueue(candidate);
                }
            }
        }

        return clusterOf;
    }

    public int ClusterOf(IReadOnlyDictionary<string, int> clusters, string symbol) =>
        clusters != null && clusters.TryGetValue(symbol, out int c) ? c : -1;

    public void Reset()
    {
        _returns.Clear();
        _lastClose.Clear();
    }
}
