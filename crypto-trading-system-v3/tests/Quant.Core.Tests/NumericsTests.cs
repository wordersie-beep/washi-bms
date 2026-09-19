using System;
using System.Linq;
using Quant.Core.Numerics;
using Xunit;

namespace Quant.Core.Tests;

public class RingTests
{
    [Fact]
    public void IndexesNewestFirstAndEvictsOldest()
    {
        var ring = new Ring<int>(3);
        ring.Add(1); ring.Add(2); ring.Add(3);

        Assert.Equal(3, ring[0]);
        Assert.Equal(2, ring[1]);
        Assert.Equal(1, ring[2]);
        Assert.True(ring.IsFull);

        ring.Add(4);
        Assert.Equal(3, ring.Count);
        Assert.Equal(4, ring[0]);
        Assert.Equal(2, ring[2]);
        Assert.Equal(2, ring.Oldest);
        Assert.Equal(4, ring.Newest);
    }

    [Fact]
    public void ReplaceNewestOverwritesInPlace()
    {
        var ring = new Ring<int>(3);
        ring.Add(1); ring.Add(2);
        ring.ReplaceNewest(99);

        Assert.Equal(2, ring.Count);
        Assert.Equal(99, ring[0]);
        Assert.Equal(1, ring[1]);
    }

    [Fact]
    public void ReplaceNewestOnEmptyRingAppends()
    {
        var ring = new Ring<int>(3);
        ring.ReplaceNewest(7);
        Assert.Equal(1, ring.Count);
        Assert.Equal(7, ring[0]);
    }

    [Fact]
    public void SurvivesManyWrapArounds()
    {
        var ring = new Ring<int>(5);
        for (int i = 0; i < 1000; i++) ring.Add(i);

        Assert.Equal(5, ring.Count);
        Assert.Equal(999, ring[0]);
        Assert.Equal(995, ring[4]);
        Assert.Equal(new[] { 999, 998, 997, 996, 995 }, ring.ToArray());
    }

    [Fact]
    public void OutOfRangeAccessThrows()
    {
        var ring = new Ring<int>(3);
        ring.Add(1);
        Assert.Throws<ArgumentOutOfRangeException>(() => ring[1]);
        Assert.False(ring.TryGet(1, out _));
        Assert.True(ring.TryGet(0, out int v));
        Assert.Equal(1, v);
    }
}

public class RollingWindowTests
{
    [Fact]
    public void MeanAndVarianceMatchDirectComputationAfterEviction()
    {
        var w = new RollingWindow(4);
        foreach (double v in new double[] { 1, 2, 3, 4, 5, 6 }) w.Add(v);

        // Window now holds 3,4,5,6.
        Assert.Equal(4, w.Count);
        Assert.Equal(4.5, w.Mean, 10);

        double[] expected = { 3, 4, 5, 6 };
        double mean = expected.Average();
        double variance = expected.Sum(x => (x - mean) * (x - mean)) / (expected.Length - 1);
        Assert.Equal(variance, w.Variance, 10);
        Assert.Equal(Math.Sqrt(variance), w.StdDev, 10);
    }

    [Fact]
    public void VarianceOfConstantSeriesIsExactlyZeroNotNegative()
    {
        var w = new RollingWindow(50);
        for (int i = 0; i < 500; i++) w.Add(12345.6789);

        Assert.True(w.Variance >= 0);
        Assert.Equal(0, w.Variance, 8);
        Assert.Equal(0, w.ZScoreOfNewest());
    }

    [Fact]
    public void PercentileRankReturnsNoInformationBelowMinSample()
    {
        var w = new RollingWindow(100);
        for (int i = 0; i < 5; i++) w.Add(i);
        Assert.Equal(0.5, w.PercentileRank(3, minSample: 10));
    }

    [Fact]
    public void PercentileRankIsFractionAtOrBelow()
    {
        var w = new RollingWindow(100);
        for (int i = 1; i <= 100; i++) w.Add(i);

        Assert.Equal(0.25, w.PercentileRank(25), 6);
        Assert.Equal(1.0, w.PercentileRank(100), 6);
        Assert.Equal(0.0, w.PercentileRank(0), 6);
    }

    [Fact]
    public void QuantileInterpolatesAndMedianIsConsistent()
    {
        var w = new RollingWindow(5);
        foreach (double v in new double[] { 10, 20, 30, 40, 50 }) w.Add(v);

        Assert.Equal(10, w.Quantile(0), 6);
        Assert.Equal(30, w.Median(), 6);
        Assert.Equal(50, w.Quantile(1), 6);
        Assert.Equal(20, w.Quantile(0.25), 6);
        Assert.Equal(10, w.Min(), 6);
        Assert.Equal(50, w.Max(), 6);
    }

    [Fact]
    public void NonFiniteInputIsIgnoredRatherThanPoisoningTheWindow()
    {
        var w = new RollingWindow(10);
        w.Add(1); w.Add(double.NaN); w.Add(double.PositiveInfinity); w.Add(3);

        Assert.Equal(2, w.Count);
        Assert.Equal(2.0, w.Mean, 10);
    }

    [Fact]
    public void CorrelationIsOneForIdenticalSeriesAndMinusOneForMirrored()
    {
        var a = new RollingWindow(50);
        var b = new RollingWindow(50);
        var c = new RollingWindow(50);
        var rng = new Pcg32(7);

        for (int i = 0; i < 50; i++)
        {
            double v = rng.NextGaussian();
            a.Add(v); b.Add(v); c.Add(-v);
        }

        Assert.Equal(1.0, a.CorrelationWith(b, 50), 8);
        Assert.Equal(-1.0, a.CorrelationWith(c, 50), 8);
    }

    [Fact]
    public void CorrelationIsZeroWhenSampleTooSmallOrSeriesFlat()
    {
        var a = new RollingWindow(50);
        var b = new RollingWindow(50);
        for (int i = 0; i < 5; i++) { a.Add(i); b.Add(i); }
        Assert.Equal(0, a.CorrelationWith(b, 50));

        var flat = new RollingWindow(50);
        var moving = new RollingWindow(50);
        for (int i = 0; i < 50; i++) { flat.Add(1.0); moving.Add(i); }
        Assert.Equal(0, flat.CorrelationWith(moving, 50));
    }

    [Fact]
    public void ToArrayOldestFirstReversesTheNewestFirstIndexer()
    {
        var w = new RollingWindow(3);
        w.Add(1); w.Add(2); w.Add(3); w.Add(4);
        Assert.Equal(new double[] { 2, 3, 4 }, w.ToArrayOldestFirst());
    }
}

public class EwmaTests
{
    [Fact]
    public void ConvergesToTheMeanOfAStationaryStream()
    {
        var e = new Ewma(halfLifeObservations: 10);
        for (int i = 0; i < 500; i++) e.Add(5.0);
        Assert.Equal(5.0, e.Value, 6);
    }

    [Fact]
    public void HalfLifeBehavesAsAdvertised()
    {
        var e = new Ewma(halfLifeObservations: 10);
        e.Add(1.0);
        for (int i = 0; i < 10; i++) e.Add(0.0);

        // After one half-life of zeros the value should be near half the original.
        Assert.InRange(e.Value, 0.45, 0.55);
    }

    [Fact]
    public void EffectiveSampleSizeIsBoundedByObservationsThenSaturates()
    {
        var e = new Ewma(halfLifeObservations: 20);
        e.Add(1);
        Assert.Equal(1, e.EffectiveSampleSize, 6);

        for (int i = 0; i < 1000; i++) e.Add(1);

        // Steady-state Kish ESS for geometric weights is (2 - alpha) / alpha, which for a
        // half-life of 20 observations is ~57.7 -- notably larger than the half-life itself.
        double alpha = 1.0 - Math.Pow(0.5, 1.0 / 20.0);
        Assert.Equal((2.0 - alpha) / alpha, e.EffectiveSampleSize, 6);
    }
}

public class BetaBinomialTests
{
    [Fact]
    public void SmallSampleIsShrunkTowardThePrior()
    {
        // Three wins out of three, against a prior of 40% with strength 20.
        var posterior = BetaBinomial.FromCounts(priorMean: 0.40, priorStrength: 20, wins: 3, losses: 0);

        Assert.True(posterior.Mean < 0.60, "Three wins must not be read as a 100% win rate.");
        Assert.True(posterior.Mean > 0.40, "Three wins must still move the estimate upward.");
    }

    [Fact]
    public void LargeSampleOverwhelmsThePrior()
    {
        var posterior = BetaBinomial.FromCounts(0.40, 20, wins: 700, losses: 300);
        Assert.InRange(posterior.Mean, 0.68, 0.70);
    }

    [Fact]
    public void UncertaintyShrinksAsEvidenceAccumulates()
    {
        var small = BetaBinomial.FromCounts(0.5, 10, 5, 5);
        var large = BetaBinomial.FromCounts(0.5, 10, 500, 500);
        Assert.True(large.StdDev < small.StdDev);

        Assert.True(small.LowerBound(1.645) < large.LowerBound(1.645));
    }

    [Fact]
    public void BoundsStayInsideTheUnitInterval()
    {
        var extreme = BetaBinomial.FromCounts(0.99, 1, 0, 0);
        Assert.InRange(extreme.LowerBound(5), 0.0, 1.0);
        Assert.InRange(extreme.UpperBound(5), 0.0, 1.0);
    }
}

public class CusumTests
{
    [Fact]
    public void StaysQuietOnNoiseAndFiresOnASustainedDownwardShift()
    {
        var cusum = new Cusum(slack: 0.5, threshold: 5.0);
        var rng = new Pcg32(11);

        for (int i = 0; i < 300; i++) cusum.Add(rng.NextGaussian());
        Assert.False(cusum.DownwardShiftDetected);

        for (int i = 0; i < 40; i++) cusum.Add(-1.5 + (rng.NextGaussian() * 0.2));
        Assert.True(cusum.DownwardShiftDetected);
        Assert.True(cusum.DownwardSeverity > 0);
    }

    [Fact]
    public void StatisticsStayBoundedSoRecoveryIsPossible()
    {
        var cusum = new Cusum(slack: 0.5, threshold: 5.0);
        for (int i = 0; i < 10000; i++) cusum.Add(-3.0);
        Assert.True(cusum.Low >= -20.0);

        cusum.Reset();
        Assert.Equal(0, cusum.Low);
        Assert.False(cusum.DownwardShiftDetected);
    }
}

public class MathUtilTests
{
    [Fact]
    public void SafeDivNeverReturnsInfinityOrNaN()
    {
        Assert.Equal(0, MathUtil.SafeDiv(1, 0));
        Assert.Equal(-1, MathUtil.SafeDiv(1, 0, -1));
        Assert.Equal(2, MathUtil.SafeDiv(4, 2));
        Assert.Equal(0, MathUtil.SafeDiv(double.NaN, 1));
    }

    [Fact]
    public void NormalCdfMatchesKnownValues()
    {
        Assert.Equal(0.5, MathUtil.NormalCdf(0), 6);
        Assert.Equal(0.8413447, MathUtil.NormalCdf(1), 5);
        Assert.Equal(0.1586553, MathUtil.NormalCdf(-1), 5);
        Assert.Equal(0.9750021, MathUtil.NormalCdf(1.96), 5);
    }

    [Fact]
    public void ClampHandlesNaNByReturningTheLowerBound()
    {
        Assert.Equal(3, MathUtil.Clamp(double.NaN, 3, 9));
        Assert.Equal(9, MathUtil.Clamp(100, 3, 9));
    }

    [Fact]
    public void GeometricMeanIgnoresNonPositiveAndNonFiniteTerms()
    {
        Assert.Equal(2.0, MathUtil.GeometricMean(1.0, 4.0), 10);
        Assert.Equal(2.0, MathUtil.GeometricMean(1.0, 4.0, 0.0, double.NaN), 10);
        Assert.Equal(1.0, MathUtil.GeometricMean());
    }
}

public class Pcg32Tests
{
    [Fact]
    public void IsDeterministicForAGivenSeed()
    {
        var a = new Pcg32(1234);
        var b = new Pcg32(1234);
        for (int i = 0; i < 100; i++) Assert.Equal(a.NextUInt(), b.NextUInt());
    }

    [Fact]
    public void UniformDrawsCoverTheUnitIntervalWithTheRightMean()
    {
        var rng = new Pcg32(99);
        double sum = 0;
        for (int i = 0; i < 100000; i++)
        {
            double v = rng.NextDouble();
            Assert.InRange(v, 0.0, 1.0);
            sum += v;
        }
        Assert.InRange(sum / 100000, 0.495, 0.505);
    }

    [Fact]
    public void GaussianDrawsHaveUnitVariance()
    {
        var rng = new Pcg32(5);
        double sum = 0, sumSq = 0;
        const int n = 200000;
        for (int i = 0; i < n; i++) { double v = rng.NextGaussian(); sum += v; sumSq += v * v; }

        double mean = sum / n;
        double variance = (sumSq / n) - (mean * mean);
        Assert.InRange(mean, -0.02, 0.02);
        Assert.InRange(variance, 0.97, 1.03);
    }

    [Fact]
    public void ShufflePreservesTheMultiset()
    {
        var array = Enumerable.Range(0, 100).ToArray();
        new Pcg32(3).Shuffle(array);
        Assert.Equal(Enumerable.Range(0, 100), array.OrderBy(x => x));
    }
}
