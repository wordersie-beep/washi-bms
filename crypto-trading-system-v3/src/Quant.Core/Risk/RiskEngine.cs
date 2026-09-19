using System;
using System.Collections.Generic;
using Quant.Core.Config;
using Quant.Core.Numerics;
using Quant.Core.Primitives;
using Quant.Core.Stats;

namespace Quant.Core.Risk;

/// <summary>
/// The system's risk posture (spec sections 33-40).
///
/// A four-state ladder — Normal, Caution, Defensive, Halt — driven by drawdown over several
/// horizons, period losses, consecutive losses, estimated risk of ruin, execution quality
/// and margin. Three properties are enforced structurally rather than left to the arithmetic
/// to happen to satisfy:
///
///   MONOTONICITY. Risk is NEVER increased by a loss and never scaled aggressively by a win
///   (spec sections 39-40). The configuration is validated to make the multiplier ladder
///   non-increasing, and the winning-streak multiplier is capped in configuration so no
///   parameter set can turn this into a martingale. This is why there is no "recovery mode"
///   that trades bigger after losses: that is the mechanism by which accounts die, and the
///   cheapest defence is for the code to have nowhere to express it.
///
///   HYSTERESIS. A posture will not improve until equity has recovered a meaningful part of
///   what triggered it AND a minimum dwell time has elapsed. Without both, the system
///   oscillates between Normal and Defensive on every tick around a threshold, which is the
///   worst of both.
///
///   DEGRADATION, NOT CLIFFS. Each input contributes a graded multiplier rather than only a
///   binary state change, so the system starts trimming size before it hits a limit rather
///   than trading full size until the instant it stops entirely.
/// </summary>
public sealed class RiskEngine
{
    /// <summary>Trades required before the ruin estimate may HALT rather than merely caution.</summary>
    private const int MinTradesForRuinGate = 30;

    private readonly RiskConfig _config;
    private readonly SizingConfig _sizingConfig;
    private readonly DrawdownTracker _drawdown;
    private readonly ExecutionQualityTracker _execution;
    private readonly RiskOfRuinEstimator _ruin;
    private readonly PerformanceStore _performance;

    private RiskState _state = RiskState.Normal;
    private DateTime _stateEnteredUtc = DateTime.MinValue;
    private double _equityAtStateEntry;

    private DateTime _dayStartUtc = DateTime.MinValue;
    private double _dayStartEquity;
    private DateTime _weekStartUtc = DateTime.MinValue;
    private double _weekStartEquity;

    private int _consecutiveLosses;
    private int _consecutiveWins;
    private DateTime? _cooldownUntilUtc;
    private DateTime _lastRuinEstimateUtc = DateTime.MinValue;
    private RuinEstimate _lastRuin;

    public RiskEngine(
        RiskConfig config,
        SizingConfig sizingConfig,
        DrawdownTracker drawdown,
        ExecutionQualityTracker execution,
        PerformanceStore performance,
        RiskOfRuinEstimator ruin = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _sizingConfig = sizingConfig ?? throw new ArgumentNullException(nameof(sizingConfig));
        _drawdown = drawdown ?? throw new ArgumentNullException(nameof(drawdown));
        _execution = execution ?? throw new ArgumentNullException(nameof(execution));
        _performance = performance ?? throw new ArgumentNullException(nameof(performance));
        _ruin = ruin ?? new RiskOfRuinEstimator();
    }

    public RiskState State => _state;
    public int ConsecutiveLosses => _consecutiveLosses;
    public int ConsecutiveWins => _consecutiveWins;
    public DateTime? CooldownUntilUtc => _cooldownUntilUtc;
    public RuinEstimate LastRuinEstimate => _lastRuin;
    public double DayStartEquity => _dayStartEquity;
    public double WeekStartEquity => _weekStartEquity;

    /// <summary>
    /// Recomputes the posture. Called on every decision cycle, and cheap enough to be: the
    /// only expensive part, the ruin simulation, is rate-limited internally.
    /// </summary>
    public RiskAssessment Evaluate(DateTime nowUtc, AccountSnapshot account, double anomalySeverity, bool inRecoveryPeriod)
    {
        var reasons = new List<string>();

        if (!account.IsUsable)
        {
            return Halted(nowUtc, NoTradeReason.DataQuality, new List<string> { "account snapshot unusable" });
        }

        RollPeriods(nowUtc, account.Equity);
        _drawdown.Observe(nowUtc, account.Equity);

        double dd24 = _drawdown.Drawdown24hPercent;
        double dd7 = _drawdown.Drawdown7dPercent;
        double dd30 = _drawdown.Drawdown30dPercent;
        double ddAll = _drawdown.AllTimeDrawdownPercent;

        double dailyLoss = PercentLossFrom(_dayStartEquity, account.Equity);
        double weeklyLoss = PercentLossFrom(_weekStartEquity, account.Equity);

        RiskState target = RiskState.Normal;
        NoTradeReason blocking = NoTradeReason.None;

        // --- Hard stops ------------------------------------------------------------------
        void Escalate(RiskState to, NoTradeReason reason, string message)
        {
            if (to > target) { target = to; blocking = reason; }
            reasons.Add(message);
        }

        if (dailyLoss >= _config.DailyLossLimitPercent)
            Escalate(RiskState.Halt, NoTradeReason.DailyLossLimit, $"daily loss {dailyLoss:F2}% at the {_config.DailyLossLimitPercent:F2}% limit");
        else if (dailyLoss >= _config.DailyLossLimitPercent * 0.70)
            Escalate(RiskState.Defensive, NoTradeReason.DailyLossLimit, $"daily loss {dailyLoss:F2}% approaching the limit");
        else if (dailyLoss >= _config.DailyLossLimitPercent * 0.45)
            Escalate(RiskState.Caution, NoTradeReason.DailyLossLimit, $"daily loss {dailyLoss:F2}%");

        if (weeklyLoss >= _config.WeeklyLossLimitPercent)
            Escalate(RiskState.Halt, NoTradeReason.WeeklyLossLimit, $"weekly loss {weeklyLoss:F2}% at the {_config.WeeklyLossLimitPercent:F2}% limit");
        else if (weeklyLoss >= _config.WeeklyLossLimitPercent * 0.70)
            Escalate(RiskState.Defensive, NoTradeReason.WeeklyLossLimit, $"weekly loss {weeklyLoss:F2}% approaching the limit");

        CheckDrawdown(dd24, _config.MaxDrawdown24hPercent, "24h", Escalate);
        CheckDrawdown(dd7, _config.MaxDrawdown7dPercent, "7d", Escalate);
        CheckDrawdown(dd30, _config.MaxDrawdown30dPercent, "30d", Escalate);
        CheckDrawdown(ddAll, _config.MaxDrawdownAllTimePercent, "all-time", Escalate);

        // --- Consecutive losses (spec section 38) -----------------------------------------
        if (_consecutiveLosses >= _config.LossesBeforeHalt)
            Escalate(RiskState.Halt, NoTradeReason.ConsecutiveLossCooldown, $"{_consecutiveLosses} consecutive losses");
        else if (_consecutiveLosses >= _config.LossesBeforeDefensive)
            Escalate(RiskState.Defensive, NoTradeReason.ConsecutiveLossCooldown, $"{_consecutiveLosses} consecutive losses");
        else if (_consecutiveLosses >= _config.LossesBeforeRiskReduction)
            Escalate(RiskState.Caution, NoTradeReason.ConsecutiveLossCooldown, $"{_consecutiveLosses} consecutive losses");

        // --- Margin (spec sections 114-115) ------------------------------------------------
        if (account.MarginLevelPercent.HasValue)
        {
            double marginLevel = account.MarginLevelPercent.Value;

            // Порог строится от стоп-аута брокера, но ограничен сверху: брокер, сообщивший
            // бессмысленно высокий уровень, не должен получить возможность остановить
            // торговлю навсегда — это отказ по чужой ошибке, а не по риску.
            double fromStopOut = account.StopOutLevelPercent > 0
                ? account.StopOutLevelPercent * _config.StopOutSafetyMultiple
                : 0;

            double dangerLevel = Math.Min(
                _config.MaxMarginDangerLevelPercent,
                Math.Max(_config.MinMarginLevelPercent, fromStopOut));

            if (marginLevel < dangerLevel)
                Escalate(RiskState.Halt, NoTradeReason.MarginGuard, $"margin level {marginLevel:F0}% below the {dangerLevel:F0}% floor");
            else if (marginLevel < dangerLevel * 1.5)
                Escalate(RiskState.Defensive, NoTradeReason.MarginGuard, $"margin level {marginLevel:F0}% approaching the floor");
        }

        // --- Execution quality (spec section 59) --------------------------------------------
        double executionQuality = _execution.Quality;
        if (executionQuality < _config.MinExecutionQuality * 0.6)
            Escalate(RiskState.Halt, NoTradeReason.ExecutionQuality, $"execution quality {executionQuality:P0} has collapsed");
        else if (executionQuality < _config.MinExecutionQuality)
            Escalate(RiskState.Defensive, NoTradeReason.ExecutionQuality, $"execution quality {executionQuality:P0} below the {_config.MinExecutionQuality:P0} floor");

        // --- Anomalies and post-event recovery ----------------------------------------------
        if (anomalySeverity > 0.8)
            Escalate(RiskState.Defensive, NoTradeReason.AnomalyDetected, $"market anomaly severity {anomalySeverity:F2}");
        else if (anomalySeverity > 0.5)
            Escalate(RiskState.Caution, NoTradeReason.AnomalyDetected, $"market anomaly severity {anomalySeverity:F2}");

        if (inRecoveryPeriod)
            Escalate(RiskState.Caution, NoTradeReason.RecoveryPeriod, "inside the post-event recovery hold");

        // --- Risk of ruin (spec section 34) ---------------------------------------------------
        //
        // The severity of the response depends on whether the estimate rests on EVIDENCE or
        // on an ASSUMPTION. With no trade history the win rate and payoff fed to the
        // simulator are placeholders, and halting on a placeholder would mean a newly
        // deployed system could never take its first trade -- it needs trades to build the
        // record that would justify trading. Below a usable sample the finding is capped at
        // Caution, which reduces size without creating that deadlock.
        RuinEstimate ruin = EstimateRuin(nowUtc);
        bool ruinIsEvidenceBased = _performance.Overall.TotalTrades >= MinTradesForRuinGate;

        if (ruin.Probability > _config.MaxAcceptableRiskOfRuin)
        {
            if (ruinIsEvidenceBased)
            {
                Escalate(RiskState.Halt, NoTradeReason.RiskOfRuin,
                    $"estimated risk of ruin {ruin.Probability:P2} exceeds the {_config.MaxAcceptableRiskOfRuin:P2} limit over {_performance.Overall.TotalTrades} trades");
            }
            else
            {
                Escalate(RiskState.Caution, NoTradeReason.RiskOfRuin,
                    $"assumed risk of ruin {ruin.Probability:P2} (only {_performance.Overall.TotalTrades} trades recorded; not yet evidence)");
            }
        }
        else if (ruin.Probability > _config.MaxAcceptableRiskOfRuin * 0.5)
        {
            Escalate(RiskState.Caution, NoTradeReason.RiskOfRuin, $"estimated risk of ruin {ruin.Probability:P2}");
        }

        // --- Transition with hysteresis -------------------------------------------------------
        ApplyTransition(nowUtc, target, account.Equity, reasons);

        // --- Multiplier -------------------------------------------------------------------------
        double multiplier = MultiplierFor(_state);
        multiplier *= GradedDrawdownFactor(dd24, _config.MaxDrawdown24hPercent);
        multiplier *= GradedDrawdownFactor(dailyLoss, _config.DailyLossLimitPercent);
        multiplier *= MathUtil.Clamp(1.0 - (0.5 * anomalySeverity), 0.3, 1.0);
        multiplier *= MathUtil.Clamp(executionQuality / Math.Max(_config.MinExecutionQuality, 0.01), 0.3, 1.0);
        multiplier *= WinStreakFactor();

        if (_cooldownUntilUtc.HasValue && nowUtc < _cooldownUntilUtc.Value)
        {
            reasons.Add($"cooldown until {_cooldownUntilUtc.Value:HH:mm:ss}Z");
            multiplier = 0;
            if (blocking == NoTradeReason.None) blocking = NoTradeReason.Cooldown;
        }

        if (_state == RiskState.Halt) multiplier = 0;

        return new RiskAssessment
        {
            State = _state,
            RiskMultiplier = MathUtil.Clamp01(multiplier),
            Reasons = reasons,
            BlockingReason = blocking,
            Drawdown24hPercent = dd24,
            Drawdown7dPercent = dd7,
            Drawdown30dPercent = dd30,
            DrawdownAllTimePercent = ddAll,
            DailyLossPercent = dailyLoss,
            WeeklyLossPercent = weeklyLoss,
            ConsecutiveLosses = _consecutiveLosses,
            RuinProbability = ruin.Probability,
            ExecutionQuality = executionQuality,
            CooldownUntilUtc = _cooldownUntilUtc,
        };
    }

    private static void CheckDrawdown(double actual, double limit, string label, Action<RiskState, NoTradeReason, string> escalate)
    {
        if (limit <= 0) return;

        if (actual >= limit)
            escalate(RiskState.Halt, NoTradeReason.DrawdownLimit, $"{label} drawdown {actual:F2}% at the {limit:F2}% limit");
        else if (actual >= limit * 0.75)
            escalate(RiskState.Defensive, NoTradeReason.DrawdownLimit, $"{label} drawdown {actual:F2}% approaching the limit");
        else if (actual >= limit * 0.50)
            escalate(RiskState.Caution, NoTradeReason.DrawdownLimit, $"{label} drawdown {actual:F2}%");
    }

    /// <summary>
    /// Applies the state transition.
    ///
    /// Escalation is IMMEDIATE — when conditions worsen there is nothing to be gained by
    /// waiting. De-escalation requires both a minimum dwell time and a partial equity
    /// recovery, because a posture that relaxes the moment a number dips back under its
    /// threshold will re-trigger on the next tick and has achieved nothing but churn.
    /// </summary>
    private void ApplyTransition(DateTime nowUtc, RiskState target, double equity, List<string> reasons)
    {
        if (_stateEnteredUtc == DateTime.MinValue)
        {
            _stateEnteredUtc = nowUtc;
            _equityAtStateEntry = equity;
        }

        if (target > _state)
        {
            _state = target;
            _stateEnteredUtc = nowUtc;
            _equityAtStateEntry = equity;

            if (target >= RiskState.Defensive && _consecutiveLosses >= _config.LossesBeforeCooldown)
            {
                _cooldownUntilUtc = nowUtc.AddMinutes(_config.ConsecutiveLossCooldownMinutes);
            }
            return;
        }

        if (target >= _state) return;

        double minutesInState = (nowUtc - _stateEnteredUtc).TotalMinutes;
        if (minutesInState < _config.MinMinutesInRiskState)
        {
            reasons.Add($"holding {_state} for another {_config.MinMinutesInRiskState - minutesInState:F0} minutes");
            return;
        }

        // Equity must have recovered a meaningful share of the way back, not merely stopped
        // falling.
        double recoveryNeeded = _equityAtStateEntry * (1.0 + ((_config.RecoveryHysteresisFraction * _config.DailyLossLimitPercent) / 100.0));
        if (_state >= RiskState.Defensive && equity < recoveryNeeded)
        {
            reasons.Add($"holding {_state} until equity recovers to {recoveryNeeded:F2}");
            return;
        }

        // Step down one level at a time. Jumping from Halt straight to Normal discards the
        // caution the intermediate states exist to express.
        _state = (RiskState)((int)_state - 1);
        _stateEnteredUtc = nowUtc;
        _equityAtStateEntry = equity;
        reasons.Add($"de-escalated to {_state}");
    }

    private double MultiplierFor(RiskState state) => state switch
    {
        RiskState.Normal => 1.0,
        RiskState.Caution => _config.CautionRiskMultiplier,
        RiskState.Defensive => _config.DefensiveRiskMultiplier,
        _ => 0.0,
    };

    /// <summary>Trims size smoothly as a limit is approached, rather than at a cliff edge.</summary>
    private static double GradedDrawdownFactor(double actual, double limit)
    {
        if (limit <= 0) return 1.0;
        double usage = MathUtil.Clamp01(actual / limit);
        return MathUtil.Clamp(1.0 - (0.6 * usage), 0.2, 1.0);
    }

    /// <summary>
    /// Winning-streak handling (spec section 39).
    ///
    /// Returns at most <see cref="RiskConfig.MaxWinStreakRiskMultiplier"/>, which validation
    /// forbids setting above 1.5 and which defaults to exactly 1.0 — no scaling at all.
    /// Scaling up into a winning streak is anti-martingale, and anti-martingale is still a
    /// bet that the streak continues; it converts a run of good luck into the largest
    /// position held when the run ends.
    /// </summary>
    private double WinStreakFactor()
    {
        if (_config.MaxWinStreakRiskMultiplier <= 1.0) return 1.0;
        double scaled = 1.0 + (0.05 * Math.Min(_consecutiveWins, 6));
        return Math.Min(scaled, _config.MaxWinStreakRiskMultiplier);
    }

    /// <summary>
    /// Re-estimates risk of ruin, at most once an hour: it is a simulation, and its inputs
    /// move on the timescale of trades rather than ticks.
    /// </summary>
    private RuinEstimate EstimateRuin(DateTime nowUtc)
    {
        if (_lastRuinEstimateUtc != DateTime.MinValue && (nowUtc - _lastRuinEstimateUtc).TotalMinutes < 60)
        {
            return _lastRuin;
        }

        SegmentStats overall = _performance.Overall;

        // Below a usable sample the honest answer is a break-even assumption, not a
        // flattering figure derived from a dozen trades.
        double winRate = overall.TotalTrades >= MinTradesForRuinGate ? overall.WinRate : 0.40;
        double payoff = overall.TotalTrades >= MinTradesForRuinGate && overall.AverageLossR > 0 ? overall.PayoffRatio : 1.5;

        // The CONFIGURED risk fraction, not a hard-coded one: simulating ruin at a risk level
        // the system does not actually use answers a question nobody asked.
        double riskFraction = MathUtil.Clamp(_sizingConfig.RiskPerTradePercent / 100.0, 0.0001, 0.5);

        _lastRuin = _ruin.Estimate(winRate, payoff, riskFraction, _config.RuinThresholdFraction);
        _lastRuinEstimateUtc = nowUtc;
        return _lastRuin;
    }

    /// <summary>Rolls the daily and weekly reference equities at their UTC boundaries.</summary>
    private void RollPeriods(DateTime nowUtc, double equity)
    {
        DateTime today = nowUtc.Date;
        if (_dayStartUtc != today)
        {
            _dayStartUtc = today;
            _dayStartEquity = equity;
        }

        // Weeks start Monday 00:00 UTC.
        int daysSinceMonday = ((int)nowUtc.DayOfWeek + 6) % 7;
        DateTime weekStart = today.AddDays(-daysSinceMonday);
        if (_weekStartUtc != weekStart)
        {
            _weekStartUtc = weekStart;
            _weekStartEquity = equity;
        }

        if (_dayStartEquity <= 0) _dayStartEquity = equity;
        if (_weekStartEquity <= 0) _weekStartEquity = equity;
    }

    private static double PercentLossFrom(double reference, double current) =>
        reference <= 0 ? 0 : MathUtil.Clamp(100.0 * (reference - current) / reference, 0, 100);

    /// <summary>Records a closed trade's outcome for streak tracking and the post-trade cooldown.</summary>
    public void OnTradeClosed(DateTime nowUtc, bool isWin)
    {
        if (isWin)
        {
            _consecutiveWins++;
            _consecutiveLosses = 0;
        }
        else
        {
            _consecutiveLosses++;
            _consecutiveWins = 0;

            if (_consecutiveLosses >= _config.LossesBeforeCooldown)
            {
                _cooldownUntilUtc = nowUtc.AddMinutes(_config.ConsecutiveLossCooldownMinutes);
            }
        }

        DateTime routineCooldown = nowUtc.AddMinutes(_config.PostTradeCooldownMinutes);
        if (!_cooldownUntilUtc.HasValue || _cooldownUntilUtc.Value < routineCooldown)
        {
            _cooldownUntilUtc = routineCooldown;
        }
    }

    private RiskAssessment Halted(DateTime nowUtc, NoTradeReason reason, List<string> reasons)
    {
        _state = RiskState.Halt;
        _stateEnteredUtc = nowUtc;
        return new RiskAssessment
        {
            State = RiskState.Halt,
            RiskMultiplier = 0,
            Reasons = reasons,
            BlockingReason = reason,
        };
    }

    /// <summary>
    /// Forces an immediate halt (spec section 144). Used for reconciliation mismatches,
    /// state corruption and any other condition where continuing would be guesswork.
    /// </summary>
    public void ForceHalt(DateTime nowUtc, string reason)
    {
        _state = RiskState.Halt;
        _stateEnteredUtc = nowUtc;
        _cooldownUntilUtc = nowUtc.AddMinutes(Math.Max(_config.ConsecutiveLossCooldownMinutes, 30));
        LastForcedHaltReason = reason;
    }

    public string LastForcedHaltReason { get; private set; }

    /// <summary>Restores persisted counters after a restart (spec section 146).</summary>
    public void Restore(RiskState state, int consecutiveLosses, int consecutiveWins, DateTime? cooldownUntilUtc,
        DateTime dayStartUtc, double dayStartEquity, DateTime weekStartUtc, double weekStartEquity, DateTime nowUtc)
    {
        _state = state;
        _stateEnteredUtc = nowUtc;
        _consecutiveLosses = Math.Max(0, consecutiveLosses);
        _consecutiveWins = Math.Max(0, consecutiveWins);
        _cooldownUntilUtc = cooldownUntilUtc;
        _dayStartUtc = dayStartUtc;
        _dayStartEquity = dayStartEquity;
        _weekStartUtc = weekStartUtc;
        _weekStartEquity = weekStartEquity;
    }
}
