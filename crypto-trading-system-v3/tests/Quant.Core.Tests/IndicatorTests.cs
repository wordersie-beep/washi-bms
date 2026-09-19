using System;
using System.Collections.Generic;
using System.Linq;
using Quant.Core.Indicators;
using Quant.Core.Numerics;
using Quant.Core.Primitives;
using Xunit;

namespace Quant.Core.Tests;

/// <summary>Deterministic synthetic price paths shared by the indicator tests.</summary>
internal static class Synthetic
{
    public static Candle[] Trend(int count, double start = 100, double step = 1.0, double range = 0.5)
    {
        var bars = new Candle[count];
        DateTime t = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        double price = start;
        for (int i = 0; i < count; i++)
        {
            double open = price;
            double close = price + step;
            bars[i] = new Candle(t.AddMinutes(i), open, Math.Max(open, close) + range, Math.Min(open, close) - range, close, 1000);
            price = close;
        }
        return bars;
    }

    public static Candle[] Flat(int count, double price = 100, double range = 0.5)
    {
        var bars = new Candle[count];
        DateTime t = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < count; i++)
            bars[i] = new Candle(t.AddMinutes(i), price, price + range, price - range, price, 1000);
        return bars;
    }

    /// <summary>Deterministic oscillation with no drift.</summary>
    public static Candle[] Oscillating(int count, double centre = 100, double amplitude = 5, double period = 20)
    {
        var bars = new Candle[count];
        DateTime t = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        double prev = centre;
        for (int i = 0; i < count; i++)
        {
            double close = centre + (amplitude * Math.Sin(2 * Math.PI * i / period));
            double hi = Math.Max(prev, close) + 0.3;
            double lo = Math.Min(prev, close) - 0.3;
            bars[i] = new Candle(t.AddMinutes(i), prev, hi, lo, close, 1000);
            prev = close;
        }
        return bars;
    }
}

public class EmaTests
{
    [Fact]
    public void SeedsWithSimpleAverageThenSwitchesToExponential()
    {
        var ema = new Ema(3);
        ema.Update(1);
        Assert.False(ema.IsReady);
        Assert.Equal(1, ema.Value, 10);

        ema.Update(2);
        Assert.Equal(1.5, ema.Value, 10);

        ema.Update(3);
        Assert.True(ema.IsReady);
        Assert.Equal(2.0, ema.Value, 10);

        // alpha = 2/(3+1) = 0.5 -> 2 + 0.5*(9-2) = 5.5
        ema.Update(9);
        Assert.Equal(5.5, ema.Value, 10);
    }

    [Fact]
    public void ConstantInputProducesConstantOutput()
    {
        var ema = new Ema(20);
        for (int i = 0; i < 500; i++) ema.Update(42.0);
        Assert.Equal(42.0, ema.Value, 8);
    }

    [Fact]
    public void FastEmaLeadsSlowEmaInAnUptrend()
    {
        var fast = new Ema(10);
        var slow = new Ema(50);
        foreach (Candle b in Synthetic.Trend(300)) { fast.Update(b.Close); slow.Update(b.Close); }
        Assert.True(fast.Value > slow.Value);
    }

    [Fact]
    public void NonFiniteInputIsIgnored()
    {
        var ema = new Ema(3);
        ema.Update(1); ema.Update(2); ema.Update(3);
        double before = ema.Value;
        ema.Update(double.NaN);
        Assert.Equal(before, ema.Value, 10);
    }
}

public class AtrTests
{
    /// <summary>Independent, deliberately naive Wilder ATR used as an oracle.</summary>
    private static double ReferenceAtr(IReadOnlyList<Candle> bars, int periods)
    {
        var tr = new List<double>();
        for (int i = 0; i < bars.Count; i++)
        {
            double t = i == 0
                ? bars[i].High - bars[i].Low
                : Math.Max(bars[i].High - bars[i].Low,
                  Math.Max(Math.Abs(bars[i].High - bars[i - 1].Close), Math.Abs(bars[i].Low - bars[i - 1].Close)));
            tr.Add(t);
        }

        double atr = tr.Take(periods).Average();
        for (int i = periods; i < tr.Count; i++) atr += (tr[i] - atr) / periods;
        return atr;
    }

    [Fact]
    public void MatchesAnIndependentWilderImplementation()
    {
        Candle[] bars = Synthetic.Oscillating(200);
        var atr = new Atr(14);
        foreach (Candle b in bars) atr.Update(b);

        Assert.True(atr.IsReady);
        Assert.Equal(ReferenceAtr(bars, 14), atr.Value, 8);
    }

    [Fact]
    public void EqualsTheBarRangeForAFlatSeries()
    {
        var atr = new Atr(14);
        foreach (Candle b in Synthetic.Flat(100, 100, range: 0.5)) atr.Update(b);
        Assert.Equal(1.0, atr.Value, 6);
    }

    [Fact]
    public void ExpandsWhenVolatilityExpands()
    {
        var atr = new Atr(14);
        foreach (Candle b in Synthetic.Flat(60, 100, range: 0.5)) atr.Update(b);
        double calm = atr.Value;

        foreach (Candle b in Synthetic.Flat(60, 100, range: 5.0)) atr.Update(b);
        Assert.True(atr.Value > calm * 3);
    }

    [Fact]
    public void AsFractionOfPriceIsScaleFree()
    {
        var small = new Atr(14);
        var large = new Atr(14);
        foreach (Candle b in Synthetic.Flat(100, 100, 1.0)) small.Update(b);
        foreach (Candle b in Synthetic.Flat(100, 10000, 100.0)) large.Update(b);

        Assert.Equal(small.AsFractionOf(100), large.AsFractionOf(10000), 8);
    }
}

public class RsiTests
{
    [Fact]
    public void IsOneHundredForAMonotonicRiseAndZeroForAMonotonicFall()
    {
        var up = new Rsi(14);
        for (int i = 0; i < 100; i++) up.Update(100 + i);
        Assert.Equal(100, up.Value, 6);

        var down = new Rsi(14);
        for (int i = 0; i < 100; i++) down.Update(100 - i);
        Assert.Equal(0, down.Value, 6);
    }

    [Fact]
    public void IsNeutralBeforeReadyAndForAFlatSeries()
    {
        var rsi = new Rsi(14);
        Assert.Equal(50, rsi.Value);

        for (int i = 0; i < 100; i++) rsi.Update(100.0);
        Assert.Equal(50, rsi.Value, 6);
    }

    [Fact]
    public void StaysInsideZeroToOneHundred()
    {
        var rsi = new Rsi(14);
        var rng = new Pcg32(21);
        double p = 100;
        for (int i = 0; i < 1000; i++)
        {
            p *= 1 + (rng.NextGaussian() * 0.01);
            rsi.Update(p);
            Assert.InRange(rsi.Value, 0.0, 100.0);
        }
    }
}

public class AdxTests
{
    [Fact]
    public void ReadsHighInAStrongTrendAndLowInChop()
    {
        var trending = new Adx(14);
        foreach (Candle b in Synthetic.Trend(200, step: 1.0, range: 0.2)) trending.Update(b);

        var choppy = new Adx(14);
        foreach (Candle b in Synthetic.Flat(200)) choppy.Update(b);

        Assert.True(trending.IsReady);
        Assert.True(trending.Value > 40, $"Trending ADX was {trending.Value:F1}, expected a strong reading.");
        Assert.True(choppy.Value < 25, $"Choppy ADX was {choppy.Value:F1}, expected a weak reading.");
    }

    [Fact]
    public void DirectionalBiasFollowsTheTrendDirection()
    {
        var up = new Adx(14);
        foreach (Candle b in Synthetic.Trend(200, step: 1.0)) up.Update(b);
        Assert.Equal(1, up.DirectionalBias);
        Assert.True(up.PlusDi > up.MinusDi);

        var down = new Adx(14);
        foreach (Candle b in Synthetic.Trend(200, start: 500, step: -1.0)) down.Update(b);
        Assert.Equal(-1, down.DirectionalBias);
    }

    [Fact]
    public void StaysInsideZeroToOneHundred()
    {
        var adx = new Adx(14);
        var rng = new Pcg32(33);
        double p = 100;
        DateTime t = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 500; i++)
        {
            double o = p;
            p *= 1 + (rng.NextGaussian() * 0.02);
            var bar = new Candle(t.AddMinutes(i), o, Math.Max(o, p) * 1.001, Math.Min(o, p) * 0.999, p, 100);
            adx.Update(bar);
            Assert.InRange(adx.Value, 0.0, 100.0);
        }
    }
}

public class BollingerBandsTests
{
    [Fact]
    public void WidthIsZeroForAFlatSeriesAndPositiveOtherwise()
    {
        var flat = new BollingerBands(20);
        for (int i = 0; i < 50; i++) flat.Update(100.0);
        Assert.Equal(0, flat.Value, 8);

        var moving = new BollingerBands(20);
        foreach (Candle b in Synthetic.Oscillating(100)) moving.Update(b.Close);
        Assert.True(moving.Value > 0);
    }

    [Fact]
    public void PercentBLocatesPriceInsideTheBands()
    {
        var bb = new BollingerBands(20, 2.0);
        foreach (Candle b in Synthetic.Oscillating(200)) bb.Update(b.Close);

        Assert.Equal(0.5, bb.PercentB(bb.Middle), 6);
        Assert.Equal(1.0, bb.PercentB(bb.Upper), 6);
        Assert.Equal(0.0, bb.PercentB(bb.Lower), 6);
    }

    [Fact]
    public void PercentBIsNeutralWhenBandsCollapse()
    {
        var bb = new BollingerBands(20);
        for (int i = 0; i < 50; i++) bb.Update(100.0);
        Assert.Equal(0.5, bb.PercentB(100.0), 6);
    }
}

public class LinearRegressionSlopeTests
{
    [Fact]
    public void RecoversTheSlopeOfAPerfectLineWithRSquaredOfOne()
    {
        var lr = new LinearRegressionSlope(20);
        for (int i = 0; i < 20; i++) lr.Update(10 + (3.0 * i));

        Assert.True(lr.IsReady);
        Assert.Equal(3.0, lr.Value, 8);
        Assert.Equal(1.0, lr.RSquared, 8);
    }

    [Fact]
    public void ReportsNearZeroFitForNoise()
    {
        var lr = new LinearRegressionSlope(50);
        var rng = new Pcg32(4);
        for (int i = 0; i < 50; i++) lr.Update(100 + rng.NextGaussian());
        Assert.True(lr.RSquared < 0.35, $"R^2 was {lr.RSquared:F3}; noise should not fit a line.");
    }

    [Fact]
    public void SlopeIsNegativeForADownwardLine()
    {
        var lr = new LinearRegressionSlope(10);
        for (int i = 0; i < 10; i++) lr.Update(100 - (2.0 * i));
        Assert.Equal(-2.0, lr.Value, 8);
    }

    [Fact]
    public void FlatSeriesGivesZeroSlopeAndZeroFit()
    {
        var lr = new LinearRegressionSlope(10);
        for (int i = 0; i < 10; i++) lr.Update(100.0);
        Assert.Equal(0, lr.Value, 10);
        Assert.Equal(0, lr.RSquared, 10);
    }
}

public class DonchianChannelTests
{
    [Fact]
    public void TracksTheExtremesOfTheClosedWindowOnly()
    {
        var dc = new DonchianChannel(5);
        Candle[] bars = Synthetic.Trend(10, start: 100, step: 1, range: 0.5);
        foreach (Candle b in bars) dc.Update(b);

        double expectedHigh = bars.Skip(5).Max(b => b.High);
        double expectedLow = bars.Skip(5).Min(b => b.Low);
        Assert.Equal(expectedHigh, dc.Upper, 8);
        Assert.Equal(expectedLow, dc.Lower, 8);
    }

    [Fact]
    public void PositionOfLocatesPriceWithinTheChannel()
    {
        var dc = new DonchianChannel(10);
        foreach (Candle b in Synthetic.Flat(20, 100, range: 5)) dc.Update(b);

        Assert.Equal(105, dc.Upper, 6);
        Assert.Equal(95, dc.Lower, 6);
        Assert.Equal(0.5, dc.PositionOf(100), 6);
        Assert.Equal(1.0, dc.PositionOf(105), 6);
    }
}

public class RealizedVolatilityTests
{
    [Fact]
    public void IsZeroForAFlatSeries()
    {
        var rv = new RealizedVolatility(50, 525600);
        for (int i = 0; i < 100; i++) rv.Update(100.0);
        Assert.Equal(0, rv.Value, 10);
    }

    [Fact]
    public void RecoversTheVolatilityOfAKnownGeometricRandomWalk()
    {
        var rv = new RealizedVolatility(5000, 525600);
        var rng = new Pcg32(8);
        double p = 100;
        const double sigma = 0.004;
        for (int i = 0; i < 5000; i++) { p *= Math.Exp(sigma * rng.NextGaussian()); rv.Update(p); }

        Assert.InRange(rv.Value, sigma * 0.94, sigma * 1.06);
    }

    [Fact]
    public void RejectsNonPositivePrices()
    {
        var rv = new RealizedVolatility(50, 525600);
        rv.Update(100); rv.Update(0); rv.Update(-5); rv.Update(101);
        Assert.Equal(1, rv.Returns.Count);
    }
}

public class SwingStructureTests
{
    private static Candle Bar(DateTime t, double high, double low) =>
        new Candle(t, (high + low) / 2, high, low, (high + low) / 2, 100);

    [Fact]
    public void ConfirmsAPivotOnlyAfterTheRightHandBarsHaveClosed()
    {
        var s = new SwingStructure(confirmationBars: 2);
        DateTime t = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // Highs: 10, 11, 20, 11, 10 -> the 20 is a pivot high, confirmed on the 5th bar.
        double[] highs = { 10, 11, 20, 11, 10 };
        for (int i = 0; i < highs.Length; i++)
        {
            s.Update(Bar(t.AddMinutes(i), highs[i], highs[i] - 5));
            if (i < 4) Assert.False(s.LastSwingHigh.IsValid, $"Pivot confirmed too early at bar {i}.");
        }

        Assert.True(s.LastSwingHigh.IsValid);
        Assert.Equal(20, s.LastSwingHigh.Price, 8);
    }

    [Fact]
    public void ScoresAnUptrendPositiveAndADowntrendNegative()
    {
        var up = new SwingStructure(confirmationBars: 2);
        DateTime t = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // Two ascending peak/trough pairs.
        double[] path = { 10, 12, 20, 12, 10, 14, 24, 15, 13, 17, 28, 18, 16 };
        for (int i = 0; i < path.Length; i++) up.Update(Bar(t.AddMinutes(i), path[i], path[i] - 4));
        Assert.True(up.StructureScore() > 0, $"Expected positive structure, got {up.StructureScore()}.");

        var down = new SwingStructure(confirmationBars: 2);
        for (int i = 0; i < path.Length; i++)
        {
            double v = 100 - path[i];
            down.Update(Bar(t.AddMinutes(i), v + 4, v));
        }
        Assert.True(down.StructureScore() < 0, $"Expected negative structure, got {down.StructureScore()}.");
    }

    [Fact]
    public void ReportsUnknownStructureBeforeTwoSwingsOfEachKindExist()
    {
        var s = new SwingStructure();
        Assert.Equal(0, s.StructureScore());
        Assert.Equal(long.MaxValue, s.BarsSinceLastSwing());
    }

    [Fact]
    public void FallsBackToOpenSpaceWhenNoSwingSitsAbovePrice()
    {
        var s = new SwingStructure(confirmationBars: 2);
        DateTime t = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        double[] highs = { 10, 11, 20, 11, 10 };
        for (int i = 0; i < highs.Length; i++) s.Update(Bar(t.AddMinutes(i), highs[i], highs[i] - 5));

        Assert.Equal(10.0, s.DistanceToResistanceInAtr(price: 1000, atr: 1.0, fallback: 10.0), 8);
        Assert.Equal(15.0, s.DistanceToResistanceInAtr(price: 5, atr: 1.0), 8);
    }
}
