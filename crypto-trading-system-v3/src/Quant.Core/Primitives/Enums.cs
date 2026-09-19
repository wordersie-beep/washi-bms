using System;

namespace Quant.Core.Primitives;

/// <summary>Timeframes the system aggregates. Deliberately a small, fixed set.</summary>
public enum Tf
{
    M1 = 1,
    M3 = 3,
    M5 = 5,
    M15 = 15,
    H1 = 60,
    H4 = 240,
}

public enum Side
{
    None = 0,
    Long = 1,
    Short = -1,
}

/// <summary>Market regimes (spec section 7). <see cref="Unknown"/> is the safe default.</summary>
public enum MarketRegime
{
    Unknown = 0,
    TrendUp,
    TrendDown,
    Range,
    Breakout,
    HighVolatility,
    LowVolatility,
    Chop,
    Panic,
    Euphoria,
    LiquidityStress,
    Transition,
}

public enum StrategyKind
{
    TrendFollowing,
    Momentum,
    Breakout,
    Pullback,
    MeanReversion,
    VolatilityExpansion,
    MarketStructure,
    Reversal,
    Baseline,
}

/// <summary>Global risk posture. Strictly ordered: each step is more restrictive.</summary>
public enum RiskState
{
    Normal = 0,
    Caution = 1,
    Defensive = 2,
    Halt = 3,
}

/// <summary>
/// Deployment ladder (spec sections 105-108). Shadow and Paper never touch the broker.
/// </summary>
public enum OperatingMode
{
    Shadow = 0,
    Paper = 1,
    Demo = 2,
    Live = 3,
}

/// <summary>
/// Every reason the system may decline a trade. Recorded verbatim on each rejection so
/// filter value can be audited later (spec sections 118-120).
/// </summary>
public enum NoTradeReason
{
    None = 0,
    DataQuality,
    MarketClosed,
    NoSignal,
    LowRegimeConfidence,
    RegimeMismatch,
    StrategyDisabled,
    LowSignalConfidence,
    LowProbabilityConfidence,
    InsufficientEdge,
    NegativeExpectedValue,
    PoorRiskReward,
    InvalidStopPlacement,
    SpreadTooWide,
    VolatilityTooHigh,
    VolatilityTooLow,
    AnomalyDetected,
    ExtremeEvent,
    RecoveryPeriod,
    Chasing,
    SignalExpired,
    DuplicateSignal,
    Cooldown,
    CorrelationLimit,
    SymbolExposureLimit,
    ClusterExposureLimit,
    DirectionalExposureLimit,
    PortfolioRiskLimit,
    DailyLossLimit,
    WeeklyLossLimit,
    DrawdownLimit,
    ConsecutiveLossCooldown,
    RiskStateHalt,
    RiskOfRuin,
    MarginGuard,
    ExecutionQuality,
    BrokerConstraint,
    SizeBelowMinimum,
    PositionAlreadyOpen,
    MaxPositionsReached,
    NotRankedHighEnough,
    ModeGuard,
    ReconciliationPending,
}

public enum ExitReason
{
    Unknown = 0,
    StopLoss,
    TakeProfit1,
    TakeProfit2,
    TrailingStop,
    BreakEven,
    TimeStop,
    Invalidation,
    ProfitProtection,
    RiskStateFlatten,
    EmergencyShutdown,
    ManualOrExternal,
}

/// <summary>UTC-anchored trading sessions. Crypto trades 24/7 but flow is still session-shaped.</summary>
public enum SessionKind
{
    Asia = 0,
    Europe,
    UsOverlap,
    Us,
    OffHours,
}

public enum VolatilityBucket
{
    VeryLow = 0,
    Low,
    Normal,
    High,
    Extreme,
}
