using System;

namespace Quant.Core.Primitives;

/// <summary>Immutable view of account state at one instant.</summary>
public readonly struct AccountSnapshot
{
    public AccountSnapshot(double balance, double equity, double margin, double freeMargin, double? marginLevelPercent, double stopOutLevelPercent, bool isLive, string currency)
    {
        Balance = balance;
        Equity = equity;
        Margin = margin;
        FreeMargin = freeMargin;
        MarginLevelPercent = marginLevelPercent;
        StopOutLevelPercent = stopOutLevelPercent;
        IsLive = isLive;
        Currency = currency;
    }

    public double Balance { get; }
    public double Equity { get; }
    public double Margin { get; }
    public double FreeMargin { get; }

    /// <summary>Equity/Margin as a percentage. Null when no position is open.</summary>
    public double? MarginLevelPercent { get; }

    public double StopOutLevelPercent { get; }
    public bool IsLive { get; }
    public string Currency { get; }

    public bool IsUsable => Equity > 0 && !double.IsNaN(Equity) && !double.IsInfinity(Equity);
}
