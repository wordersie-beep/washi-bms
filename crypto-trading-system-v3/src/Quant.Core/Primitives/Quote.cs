using System;

namespace Quant.Core.Primitives;

/// <summary>A single top-of-book observation.</summary>
public readonly struct Quote
{
    public Quote(DateTime timeUtc, double bid, double ask)
    {
        TimeUtc = timeUtc;
        Bid = bid;
        Ask = ask;
    }

    public DateTime TimeUtc { get; }
    public double Bid { get; }
    public double Ask { get; }

    public double Mid => (Bid + Ask) / 2.0;
    public double Spread => Ask - Bid;

    public bool IsWellFormed =>
        !double.IsNaN(Bid) && !double.IsNaN(Ask) &&
        !double.IsInfinity(Bid) && !double.IsInfinity(Ask) &&
        Bid > 0 && Ask > 0 && Ask >= Bid;

    public override string ToString() => string.Format("{0:HH:mm:ss.fff} {1}/{2}", TimeUtc, Bid, Ask);
}
