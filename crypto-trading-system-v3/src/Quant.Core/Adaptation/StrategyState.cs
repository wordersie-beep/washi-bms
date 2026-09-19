using System;
using Quant.Core.Primitives;

namespace Quant.Core.Adaptation;

/// <summary>Lifecycle of a strategy inside the ensemble (spec sections 23-24).</summary>
public enum StrategyStatus
{
    /// <summary>Trading normally.</summary>
    Active = 0,

    /// <summary>Still trading, at reduced weight, because recent statistics have worsened.</summary>
    Degraded = 1,

    /// <summary>
    /// Not trading. Signals are still generated and tracked virtually so the strategy can
    /// prove itself again rather than disappearing for good.
    /// </summary>
    Disabled = 2,

    /// <summary>Re-admitted after shadow monitoring, at a deliberately small weight.</summary>
    Recovering = 3,
}

/// <summary>Per-strategy adaptive state, persisted across restarts.</summary>
public sealed class StrategyState
{
    public string Name { get; set; }
    public StrategyStatus Status { get; set; } = StrategyStatus.Active;

    /// <summary>Current ensemble weight, 0..1, before normalisation across strategies.</summary>
    public double Weight { get; set; } = 1.0;

    public DateTime? DisabledAtUtc { get; set; }
    public DateTime? RecoveringSinceUtc { get; set; }

    /// <summary>Virtual trades accumulated since being disabled.</summary>
    public int ShadowTrades { get; set; }

    /// <summary>Times this strategy has been disabled. A repeat offender is treated more harshly.</summary>
    public int DisableCount { get; set; }

    /// <summary>Most recent reason for a status change, surfaced in the journal.</summary>
    public string LastStatusReason { get; set; } = "initialised";

    public bool CanTrade => Status == StrategyStatus.Active || Status == StrategyStatus.Degraded || Status == StrategyStatus.Recovering;

    public override string ToString() =>
        string.Format("{0}: {1} w={2:F3} ({3})", Name, Status, Weight, LastStatusReason);
}
