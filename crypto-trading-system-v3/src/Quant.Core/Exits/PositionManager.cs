using System;
using System.Collections.Generic;
using Quant.Core.Config;
using Quant.Core.Data;
using Quant.Core.Features;
using Quant.Core.Indicators;
using Quant.Core.Numerics;
using Quant.Core.Portfolio;
using Quant.Core.Primitives;

namespace Quant.Core.Exits;

/// <summary>
/// Manages a live position from entry to exit (spec sections 46-50).
///
/// Returns a LIST of actions in priority order rather than a single one, because several
/// things can legitimately become true on the same bar — a target is reached and the stop
/// should move and the trade has been open too long — and silently applying only the first
/// of them leaves the position in a state nobody chose.
///
/// The ordering is deliberate: protective actions come before profit-taking ones. If a bar
/// both hits a target and invalidates the thesis, getting out matters more than taking a
/// partial.
/// </summary>
public sealed class PositionManager
{
    private readonly ExitConfig _config;

    public PositionManager(ExitConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// Evaluates a position against current conditions.
    /// </summary>
    /// <param name="position">The live position. Its excursion fields are updated in place.</param>
    /// <param name="plan">The exit plan decided at entry.</param>
    /// <param name="currentPrice">Price the position would be closed at (bid for longs, ask for shorts).</param>
    /// <param name="spec">Instrument terms, for tick rounding and volume normalisation.</param>
    /// <param name="data">Market data, for the trailing stop and the invalidation check.</param>
    /// <param name="features">Current features; may be null, in which case invalidation is skipped.</param>
    /// <param name="onClosedBar">True when called on a bar close, which is when bar-counted rules advance.</param>
    public IReadOnlyList<ExitAction> Evaluate(
        OpenPosition position,
        ExitPlan plan,
        double currentPrice,
        SymbolSpec spec,
        SymbolDataSet data,
        FeatureVector features,
        bool onClosedBar)
    {
        var actions = new List<ExitAction>(3);

        if (position == null || plan == null || spec == null || currentPrice <= 0) return actions;
        if (position.CurrentVolumeInUnits <= 0) return actions;

        position.UpdateExcursions(currentPrice);
        if (onClosedBar) position.BarsHeld++;

        double sign = position.Direction == Side.Long ? 1 : -1;
        double openR = position.OpenProfitR(currentPrice);

        // --- 1. Invalidation (spec section 49) --------------------------------------------
        // The reason for being in the trade has gone. Waiting for the stop is paying full
        // price for information already in hand.
        if (_config.EnableInvalidationExit && onClosedBar && features != null && plan.InvalidationPrice.HasValue)
        {
            double invalidation = plan.InvalidationPrice.Value;
            bool breached = position.Direction == Side.Long
                ? data.Signal.Last.Close < invalidation
                : data.Signal.Last.Close > invalidation;

            if (breached)
            {
                actions.Add(new ExitAction
                {
                    Kind = ExitActionKind.FullClose,
                    Reason = ExitReason.Invalidation,
                    Detail = $"closed beyond the invalidation level {invalidation:F4}",
                });
                return actions;
            }
        }

        // --- 2. Time stop (spec section 48) -------------------------------------------------
        // Only fires when the trade has gone nowhere. A position that is up 2R after a long
        // time is not stale, it is working.
        if (onClosedBar && position.BarsHeld >= plan.TimeStopBars && openR < _config.TimeStopMaxProgressR)
        {
            actions.Add(new ExitAction
            {
                Kind = ExitActionKind.FullClose,
                Reason = ExitReason.TimeStop,
                Detail = $"held {position.BarsHeld} bars for {openR:F2}R",
            });
            return actions;
        }

        // --- 3. Profit protection (spec section 50) -------------------------------------------
        // Engages only after a substantial move, and only on a large give-back. Set tighter
        // than this it becomes a trailing stop that strangles every trend before it develops.
        if (position.PeakOpenProfitR >= _config.ProfitProtectionMinPeakR)
        {
            double givenBack = position.PeakOpenProfitR - openR;
            if (givenBack >= position.PeakOpenProfitR * _config.ProfitGiveBackFraction)
            {
                actions.Add(new ExitAction
                {
                    Kind = ExitActionKind.FullClose,
                    Reason = ExitReason.ProfitProtection,
                    Detail = $"gave back {givenBack:F2}R of a {position.PeakOpenProfitR:F2}R peak",
                });
                return actions;
            }
        }

        // --- 4. Targets (spec section 43) ------------------------------------------------------
        if (!position.Target1Filled && Reached(position.Direction, currentPrice, plan.Target1Price))
        {
            double volume = spec.NormalizeVolumeDown(position.InitialVolumeInUnits * plan.Target1ClosePercent);
            if (volume > 0 && volume < position.CurrentVolumeInUnits)
            {
                actions.Add(new ExitAction
                {
                    Kind = ExitActionKind.PartialClose,
                    CloseVolumeInUnits = volume,
                    Reason = ExitReason.TakeProfit1,
                    Detail = $"first target {plan.Target1Price:F4} ({plan.Target1R:F2}R)",
                });
            }
        }

        if (position.Target1Filled && !position.Target2Filled && Reached(position.Direction, currentPrice, plan.Target2Price))
        {
            double volume = spec.NormalizeVolumeDown(position.InitialVolumeInUnits * plan.Target2ClosePercent);
            if (volume > 0 && volume < position.CurrentVolumeInUnits)
            {
                actions.Add(new ExitAction
                {
                    Kind = ExitActionKind.PartialClose,
                    CloseVolumeInUnits = volume,
                    Reason = ExitReason.TakeProfit2,
                    Detail = $"second target {plan.Target2Price:F4} ({plan.Target2R:F2}R)",
                });
            }
        }

        // --- 5. Stop management -------------------------------------------------------------------
        double? newStop = ComputeStop(position, plan, currentPrice, spec, data, openR, sign, out ExitReason stopReason, out string stopDetail);
        if (newStop.HasValue && IsImprovement(position, newStop.Value))
        {
            actions.Add(new ExitAction
            {
                Kind = ExitActionKind.MoveStop,
                NewStopPrice = newStop.Value,
                Reason = stopReason,
                Detail = stopDetail,
            });
        }

        return actions;
    }

    /// <summary>
    /// Chooses the best stop level available now: net break-even once earned, then a
    /// volatility trail once the trade has developed.
    /// </summary>
    private double? ComputeStop(
        OpenPosition position,
        ExitPlan plan,
        double currentPrice,
        SymbolSpec spec,
        SymbolDataSet data,
        double openR,
        double sign,
        out ExitReason reason,
        out string detail)
    {
        reason = ExitReason.Unknown;
        detail = null;

        double? candidate = null;

        // Net break-even (spec section 47).
        if (!position.BreakEvenApplied && openR >= plan.BreakEvenTriggerR)
        {
            candidate = plan.NetBreakEvenPrice;
            reason = ExitReason.BreakEven;
            detail = $"net break-even at {openR:F2}R (covers spread and commission, not merely the entry price)";
        }

        // Trailing stop (spec section 46). Distance is volatility- and regime-scaled, never
        // a fixed number of points: a trail that is right in a calm market is a trail that
        // stops out on the first pullback in a fast one.
        if (openR >= plan.TrailActivationR && data?.Signal != null && data.Signal.Atr.IsReady)
        {
            double atr = data.Signal.Atr.Value;
            double trailDistance = atr * plan.TrailDistanceInAtr;

            double structuralTrail = double.NaN;
            SwingStructure structure = data.Signal.Structure;
            bool haveLevel = position.Direction == Side.Long
                ? structure.TryGetSupportBelow(currentPrice, out SwingPoint level)
                : structure.TryGetResistanceAbove(currentPrice, out level);

            if (haveLevel)
            {
                structuralTrail = level.Price - (sign * _config.StructureStopBufferAtr * atr);
            }

            double volatilityTrail = currentPrice - (sign * trailDistance);

            // Prefer the looser of the two. A structural level that sits closer than the
            // volatility trail is inside the noise, and trailing to it is how a runner gets
            // cut off in the middle of the move it was kept for.
            double trail = volatilityTrail;
            if (!double.IsNaN(structuralTrail))
            {
                trail = position.Direction == Side.Long
                    ? Math.Min(volatilityTrail, structuralTrail)
                    : Math.Max(volatilityTrail, structuralTrail);
            }

            bool better = candidate == null ||
                (position.Direction == Side.Long ? trail > candidate.Value : trail < candidate.Value);

            if (better)
            {
                candidate = trail;
                reason = ExitReason.TrailingStop;
                detail = $"trailing {plan.TrailDistanceInAtr:F2} ATR at {openR:F2}R";
            }
        }

        return candidate.HasValue ? spec.RoundToTick(candidate.Value) : (double?)null;
    }

    /// <summary>
    /// A stop may only ever move in the direction that reduces risk.
    ///
    /// This is enforced here rather than trusted to the callers, because widening a stop to
    /// "give the trade room" is the single most common way a bounded loss becomes an
    /// unbounded one, and it is always tempting in the moment.
    /// </summary>
    private static bool IsImprovement(OpenPosition position, double newStop)
    {
        if (newStop <= 0) return false;

        return position.Direction == Side.Long
            ? newStop > position.CurrentStopPrice
            : newStop < position.CurrentStopPrice;
    }

    private static bool Reached(Side direction, double price, double level) =>
        direction == Side.Long ? price >= level : price <= level;
}
