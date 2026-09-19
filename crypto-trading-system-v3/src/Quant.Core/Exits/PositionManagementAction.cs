using System;
using Quant.Core.Primitives;

namespace Quant.Core.Exits;

public enum ExitActionKind
{
    None = 0,
    MoveStop,
    PartialClose,
    FullClose,
}

/// <summary>
/// An instruction from the position manager.
///
/// The manager decides WHAT should happen; the execution layer decides HOW and deals with
/// the broker. Keeping them apart is what lets the entire management logic be unit-tested
/// against a table of prices with no platform involved at all.
/// </summary>
public sealed class ExitAction
{
    public static readonly ExitAction None = new ExitAction { Kind = ExitActionKind.None };

    public ExitActionKind Kind { get; init; }

    /// <summary>New stop price, for <see cref="ExitActionKind.MoveStop"/>.</summary>
    public double NewStopPrice { get; init; }

    /// <summary>Volume to close, for <see cref="ExitActionKind.PartialClose"/>.</summary>
    public double CloseVolumeInUnits { get; init; }

    public ExitReason Reason { get; init; }
    public string Detail { get; init; }

    public override string ToString() => Kind switch
    {
        ExitActionKind.MoveStop => $"move stop to {NewStopPrice:F4} ({Reason}: {Detail})",
        ExitActionKind.PartialClose => $"close {CloseVolumeInUnits:F4} units ({Reason}: {Detail})",
        ExitActionKind.FullClose => $"close position ({Reason}: {Detail})",
        _ => "no action",
    };
}
