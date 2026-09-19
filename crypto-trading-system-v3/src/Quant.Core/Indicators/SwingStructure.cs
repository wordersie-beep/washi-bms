using System;
using Quant.Core.Numerics;
using Quant.Core.Primitives;

namespace Quant.Core.Indicators;

/// <summary>A confirmed swing point.</summary>
public readonly struct SwingPoint
{
    public SwingPoint(DateTime timeUtc, double price, bool isHigh, long sequence)
    {
        TimeUtc = timeUtc;
        Price = price;
        IsHigh = isHigh;
        Sequence = sequence;
    }

    public DateTime TimeUtc { get; }
    public double Price { get; }
    public bool IsHigh { get; }

    /// <summary>Bar counter at which the swing occurred; used to measure age in bars.</summary>
    public long Sequence { get; }

    public bool IsValid => Price > 0;
}

/// <summary>
/// Fractal swing detection and the market-structure read built on top of it.
///
/// A pivot is only CONFIRMED once <c>confirmationBars</c> further bars have closed on the
/// right of it. That delay is the point: it is what makes the structure read honest. A
/// pivot identified using the bar that is still forming is not a pivot, it is look-ahead.
/// The cost is that structure lags by <c>confirmationBars</c>, and every consumer is
/// written knowing that.
/// </summary>
public sealed class SwingStructure
{
    private readonly int _confirmationBars;
    private readonly Ring<Candle> _bars;
    private readonly Ring<SwingPoint> _swings;
    private long _sequence;

    public SwingStructure(int confirmationBars = 2, int barCapacity = 64, int swingCapacity = 32)
    {
        if (confirmationBars < 1) throw new ArgumentOutOfRangeException(nameof(confirmationBars));
        _confirmationBars = confirmationBars;
        _bars = new Ring<Candle>(Math.Max(barCapacity, (confirmationBars * 2) + 3));
        _swings = new Ring<SwingPoint>(swingCapacity);
    }

    public bool IsReady => _swings.Count >= 2;
    public long BarsSeen => _sequence;

    public SwingPoint LastSwingHigh { get; private set; }
    public SwingPoint LastSwingLow { get; private set; }
    public SwingPoint PreviousSwingHigh { get; private set; }
    public SwingPoint PreviousSwingLow { get; private set; }

    public void Update(in Candle closedBar)
    {
        _bars.Add(closedBar);
        _sequence++;

        // The candidate pivot sits `confirmationBars` back from the newest bar, so that it
        // has that many confirmed bars on each side.
        int centre = _confirmationBars;
        int needed = (_confirmationBars * 2) + 1;
        if (_bars.Count < needed) return;

        Candle candidate = _bars[centre];

        bool isHigh = true;
        bool isLow = true;
        for (int offset = 1; offset <= _confirmationBars; offset++)
        {
            Candle left = _bars[centre + offset];
            Candle right = _bars[centre - offset];

            // Strict on the right, non-strict on the left: ties resolve to the earlier bar,
            // so an identical double top registers once rather than twice.
            if (candidate.High < left.High || candidate.High <= right.High) isHigh = false;
            if (candidate.Low > left.Low || candidate.Low >= right.Low) isLow = false;
        }

        long seq = _sequence - _confirmationBars;

        if (isHigh) RecordSwing(new SwingPoint(candidate.OpenTimeUtc, candidate.High, true, seq));
        if (isLow) RecordSwing(new SwingPoint(candidate.OpenTimeUtc, candidate.Low, false, seq));
    }

    private void RecordSwing(SwingPoint point)
    {
        _swings.Add(point);
        if (point.IsHigh)
        {
            PreviousSwingHigh = LastSwingHigh;
            LastSwingHigh = point;
        }
        else
        {
            PreviousSwingLow = LastSwingLow;
            LastSwingLow = point;
        }
    }

    /// <summary>
    /// Structure read in -1..+1.
    /// +1 is a clean higher-high / higher-low sequence, -1 a clean lower-high / lower-low
    /// sequence, 0 mixed or unknown. Half marks are given when only one of the two
    /// conditions holds, which is what a structure in transition actually looks like.
    /// </summary>
    public double StructureScore()
    {
        if (!LastSwingHigh.IsValid || !LastSwingLow.IsValid ||
            !PreviousSwingHigh.IsValid || !PreviousSwingLow.IsValid)
        {
            return 0;
        }

        bool higherHigh = LastSwingHigh.Price > PreviousSwingHigh.Price;
        bool higherLow = LastSwingLow.Price > PreviousSwingLow.Price;
        bool lowerHigh = LastSwingHigh.Price < PreviousSwingHigh.Price;
        bool lowerLow = LastSwingLow.Price < PreviousSwingLow.Price;

        if (higherHigh && higherLow) return 1.0;
        if (lowerHigh && lowerLow) return -1.0;
        if (higherHigh || higherLow) return 0.5;
        if (lowerHigh || lowerLow) return -0.5;
        return 0;
    }

    /// <summary>Nearest confirmed swing high strictly above <paramref name="price"/>, if any.</summary>
    public bool TryGetResistanceAbove(double price, out SwingPoint result)
    {
        result = default;
        double best = double.MaxValue;
        bool found = false;

        for (int i = 0; i < _swings.Count; i++)
        {
            SwingPoint s = _swings[i];
            if (!s.IsHigh || s.Price <= price) continue;
            if (s.Price < best) { best = s.Price; result = s; found = true; }
        }

        return found;
    }

    /// <summary>Nearest confirmed swing low strictly below <paramref name="price"/>, if any.</summary>
    public bool TryGetSupportBelow(double price, out SwingPoint result)
    {
        result = default;
        double best = double.MinValue;
        bool found = false;

        for (int i = 0; i < _swings.Count; i++)
        {
            SwingPoint s = _swings[i];
            if (s.IsHigh || s.Price >= price) continue;
            if (s.Price > best) { best = s.Price; result = s; found = true; }
        }

        return found;
    }

    /// <summary>
    /// Distance to the nearest resistance above, in ATR units. Returns
    /// <paramref name="fallback"/> when no swing has formed above price yet — callers treat
    /// that as "open space", which is the honest reading of an unbroken high.
    /// </summary>
    public double DistanceToResistanceInAtr(double price, double atr, double fallback = 10.0)
    {
        if (atr <= 0) return fallback;
        return TryGetResistanceAbove(price, out SwingPoint r) ? (r.Price - price) / atr : fallback;
    }

    public double DistanceToSupportInAtr(double price, double atr, double fallback = 10.0)
    {
        if (atr <= 0) return fallback;
        return TryGetSupportBelow(price, out SwingPoint s) ? (price - s.Price) / atr : fallback;
    }

    /// <summary>Age in bars of the most recent swing of either kind; long when none exists.</summary>
    public long BarsSinceLastSwing()
    {
        if (_swings.Count == 0) return long.MaxValue;
        return _sequence - _swings[0].Sequence;
    }

    public void Reset()
    {
        _bars.Clear();
        _swings.Clear();
        _sequence = 0;
        LastSwingHigh = default;
        LastSwingLow = default;
        PreviousSwingHigh = default;
        PreviousSwingLow = default;
    }
}
