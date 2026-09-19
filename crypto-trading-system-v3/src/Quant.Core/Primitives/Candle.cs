using System;

namespace Quant.Core.Primitives;

/// <summary>
/// One closed bar. Immutable by construction: the decision stack is never allowed to
/// mutate history, which is the cheapest structural defence against look-ahead.
/// </summary>
public readonly struct Candle : IEquatable<Candle>
{
    public Candle(DateTime openTimeUtc, double open, double high, double low, double close, double volume)
    {
        OpenTimeUtc = openTimeUtc;
        Open = open;
        High = high;
        Low = low;
        Close = close;
        Volume = volume;
    }

    public DateTime OpenTimeUtc { get; }
    public double Open { get; }
    public double High { get; }
    public double Low { get; }
    public double Close { get; }
    public double Volume { get; }

    public double Range => High - Low;
    public double Body => Math.Abs(Close - Open);
    public double Typical => (High + Low + Close) / 3.0;
    public double Median => (High + Low) / 2.0;
    public bool IsUp => Close >= Open;

    /// <summary>Upper wick as a fraction of range; 0 when the bar has no range.</summary>
    public double UpperWickFraction => Range <= 0 ? 0 : (High - Math.Max(Open, Close)) / Range;

    /// <summary>Lower wick as a fraction of range; 0 when the bar has no range.</summary>
    public double LowerWickFraction => Range <= 0 ? 0 : (Math.Min(Open, Close) - Low) / Range;

    /// <summary>Body as a fraction of range; 0 when the bar has no range.</summary>
    public double BodyFraction => Range <= 0 ? 0 : Body / Range;

    /// <summary>
    /// A bar is usable only if every field is finite, ordered and positive. Bad ticks from a
    /// crypto feed routinely produce zero or inverted bars; those must never reach a feature.
    /// </summary>
    public bool IsWellFormed =>
        !double.IsNaN(Open) && !double.IsNaN(High) && !double.IsNaN(Low) && !double.IsNaN(Close) &&
        !double.IsInfinity(Open) && !double.IsInfinity(High) && !double.IsInfinity(Low) && !double.IsInfinity(Close) &&
        Open > 0 && High > 0 && Low > 0 && Close > 0 &&
        High >= Low &&
        High >= Open && High >= Close &&
        Low <= Open && Low <= Close &&
        Volume >= 0;

    public bool Equals(Candle other) =>
        OpenTimeUtc == other.OpenTimeUtc && Open.Equals(other.Open) && High.Equals(other.High) &&
        Low.Equals(other.Low) && Close.Equals(other.Close) && Volume.Equals(other.Volume);

    public override bool Equals(object obj) => obj is Candle c && Equals(c);

    public override int GetHashCode() => OpenTimeUtc.GetHashCode() ^ Close.GetHashCode();

    public override string ToString() =>
        string.Format("{0:yyyy-MM-dd HH:mm} O{1} H{2} L{3} C{4} V{5}", OpenTimeUtc, Open, High, Low, Close, Volume);
}
