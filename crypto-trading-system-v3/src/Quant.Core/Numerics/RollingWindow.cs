using System;

namespace Quant.Core.Numerics;

/// <summary>
/// Bounded window of doubles with the descriptive statistics the system needs.
/// Mean and variance are kept incrementally; order statistics (percentile, median) copy and
/// sort on demand, which is acceptable because windows are small (tens to a few hundred)
/// and order statistics are only requested on bar close, never per tick.
/// </summary>
public sealed class RollingWindow
{
    private readonly Ring<double> _ring;
    private readonly double[] _scratch;
    private double _sum;
    private double _sumSq;

    public RollingWindow(int capacity)
    {
        _ring = new Ring<double>(capacity);
        _scratch = new double[capacity];
    }

    public int Capacity => _ring.Capacity;
    public int Count => _ring.Count;
    public bool IsFull => _ring.IsFull;

    /// <summary>Newest-first indexer.</summary>
    public double this[int indexFromNewest] => _ring[indexFromNewest];

    public double Newest => _ring.Count > 0 ? _ring[0] : 0;

    public void Add(double value)
    {
        if (!MathUtil.IsFinite(value)) return;

        if (_ring.IsFull)
        {
            double evicted = _ring[_ring.Count - 1];
            _sum -= evicted;
            _sumSq -= evicted * evicted;
        }

        _ring.Add(value);
        _sum += value;
        _sumSq += value * value;
    }

    public void Clear()
    {
        _ring.Clear();
        _sum = 0;
        _sumSq = 0;
    }

    public double Sum => _sum;

    public double Mean => _ring.Count == 0 ? 0 : _sum / _ring.Count;

    /// <summary>Sample variance (n-1). Zero for fewer than two observations.</summary>
    public double Variance
    {
        get
        {
            int n = _ring.Count;
            if (n < 2) return 0;
            double mean = _sum / n;
            double v = (_sumSq - (n * mean * mean)) / (n - 1);
            // Accumulated round-off can drive a genuinely-zero variance slightly negative.
            return v > 0 ? v : 0;
        }
    }

    public double StdDev => Math.Sqrt(Variance);

    /// <summary>
    /// Z-score of the newest observation against the window. Zero when the window is too
    /// short or degenerate, so a flat series never produces a spurious extreme reading.
    /// </summary>
    public double ZScoreOfNewest() => Count == 0 ? 0 : ZScore(this[0]);

    public double ZScore(double value)
    {
        double sd = StdDev;
        if (sd < MathUtil.Epsilon || Count < 3) return 0;
        return MathUtil.Clamp((value - Mean) / sd, -10, 10);
    }

    /// <summary>
    /// Fraction of observations at or below <paramref name="value"/>, in 0..1.
    /// Returns 0.5 (an explicit "no information" answer) below the minimum sample.
    /// </summary>
    public double PercentileRank(double value, int minSample = 10)
    {
        int n = _ring.Count;
        if (n < minSample) return 0.5;

        int below = 0;
        for (int i = 0; i < n; i++)
        {
            if (_ring[i] <= value) below++;
        }
        return (double)below / n;
    }

    public double PercentileRankOfNewest(int minSample = 10) => Count == 0 ? 0.5 : PercentileRank(this[0], minSample);

    /// <summary>Value at quantile <paramref name="q"/> (0..1). Returns 0 on an empty window.</summary>
    public double Quantile(double q)
    {
        int n = _ring.Count;
        if (n == 0) return 0;
        for (int i = 0; i < n; i++) _scratch[i] = _ring[i];
        Array.Sort(_scratch, 0, n);
        return MathUtil.QuantileSorted(_scratch, n, q);
    }

    public double Median() => Quantile(0.5);

    public double Min()
    {
        int n = _ring.Count;
        if (n == 0) return 0;
        double m = _ring[0];
        for (int i = 1; i < n; i++) if (_ring[i] < m) m = _ring[i];
        return m;
    }

    public double Max()
    {
        int n = _ring.Count;
        if (n == 0) return 0;
        double m = _ring[0];
        for (int i = 1; i < n; i++) if (_ring[i] > m) m = _ring[i];
        return m;
    }

    /// <summary>Copies the window into a new array, oldest-first. Allocates; call sparingly.</summary>
    public double[] ToArrayOldestFirst()
    {
        int n = _ring.Count;
        var result = new double[n];
        for (int i = 0; i < n; i++) result[i] = _ring[n - 1 - i];
        return result;
    }

    /// <summary>
    /// Pearson correlation against another window over the most recent
    /// <paramref name="lookback"/> aligned observations. Returns 0 when either series is
    /// degenerate or the overlap is too short to mean anything.
    /// </summary>
    public double CorrelationWith(RollingWindow other, int lookback, int minSample = 10)
    {
        if (other == null) return 0;
        int n = Math.Min(Math.Min(Count, other.Count), lookback);
        if (n < minSample) return 0;

        double sx = 0, sy = 0;
        for (int i = 0; i < n; i++) { sx += this[i]; sy += other[i]; }
        double mx = sx / n, my = sy / n;

        double cov = 0, vx = 0, vy = 0;
        for (int i = 0; i < n; i++)
        {
            double dx = this[i] - mx;
            double dy = other[i] - my;
            cov += dx * dy;
            vx += dx * dx;
            vy += dy * dy;
        }

        double denom = Math.Sqrt(vx * vy);
        if (denom < MathUtil.Epsilon) return 0;
        return MathUtil.Clamp(cov / denom, -1, 1);
    }
}
