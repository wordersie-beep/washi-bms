using System;
using Quant.Core.Config;
using Quant.Core.Numerics;
using Quant.Core.Primitives;
using Quant.Core.Probability;
using Quant.Core.Stats;

namespace Quant.Core.Ev;

/// <summary>
/// Decides whether a candidate trade has an edge worth taking (spec sections 17-19).
///
/// Three ideas do the work here, and each exists to block a specific way systems fool
/// themselves:
///
///   1. THE TARGET IS CAPPED BY HISTORY. A 3R target on a setup whose favourable excursion
///      historically reaches 1.4R is not a target, it is an assumption that price will do
///      something it has not done. The MFE distribution for the setup caps what may be
///      claimed (spec section 44), which usually lowers the stated reward-to-risk and is
///      exactly the correction that makes the remaining number believable.
///
///   2. COSTS COME OFF BEFORE THE VERDICT, NOT AFTER. Spread, commission and slippage are
///      subtracted inside the expected-value calculation. On small timeframes they routinely
///      exceed the entire edge.
///
///   3. THE SYSTEM TRADES A LOWER BOUND, NOT A POINT ESTIMATE. Uncertainty in the win
///      probability propagates into uncertainty in expected value, and the trade must clear
///      its threshold on the pessimistic end of that range (spec section 19). An edge that
///      exists only inside its own error bars is not an edge.
/// </summary>
public sealed class ExpectedValueEngine
{
    private readonly EvConfig _config;
    private readonly PerformanceStore _performance;

    public ExpectedValueEngine(EvConfig config, PerformanceStore performance)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _performance = performance ?? throw new ArgumentNullException(nameof(performance));
    }

    /// <summary>
    /// Evaluates a candidate.
    /// </summary>
    /// <param name="probability">Win-probability estimate with its uncertainty.</param>
    /// <param name="plannedRewardToRisk">Reward-to-risk the exit plan is asking for.</param>
    /// <param name="cost">Round-trip cost estimate.</param>
    /// <param name="strategyName">Strategy, for looking up its own MFE and MAE history.</param>
    /// <param name="regime">Regime, for the same reason.</param>
    /// <param name="symbol">Symbol, for the same reason.</param>
    /// <param name="regimeConfidence">Regime confidence, which raises the required edge when low.</param>
    /// <param name="volatilityStress">0..1 measure of abnormal volatility, which raises the required edge.</param>
    public ExpectedValueResult Evaluate(
        ProbabilityEstimate probability,
        double plannedRewardToRisk,
        CostEstimate cost,
        string strategyName,
        MarketRegime regime,
        string symbol,
        double regimeConfidence,
        double volatilityStress)
    {
        if (plannedRewardToRisk <= 0)
        {
            return ExpectedValueResult.Reject("planned reward-to-risk is not positive");
        }

        if (probability.EffectiveSample <= 0)
        {
            return ExpectedValueResult.Reject("no usable probability estimate");
        }

        // --- Realistic target -------------------------------------------------------------
        (double effectiveRr, bool capped) = ApplyMfeCap(plannedRewardToRisk, strategyName, regime, symbol);

        // --- Win and loss magnitudes -------------------------------------------------------
        double expectedWinR = effectiveRr;
        double expectedLossR = ExpectedLoss(strategyName, regime, symbol);

        double costR = MathUtil.Clamp(cost.TotalR, 0, 5);

        // --- Expected value ----------------------------------------------------------------
        double p = probability.PWin;
        double ev = (p * expectedWinR) - ((1 - p) * expectedLossR) - costR;

        // --- Uncertainty propagation --------------------------------------------------------
        // EV is linear in p with slope (win + loss), so the standard deviation of EV is the
        // standard deviation of p scaled by that slope. This is what turns "I am unsure about
        // the probability" into "I am unsure about the edge", which is the form a risk
        // decision can actually use.
        double evStandardError = probability.StandardError * (expectedWinR + expectedLossR);
        double lowerBound = ev - (_config.EdgeConfidenceZ * evStandardError);

        // --- Решение и доверие ---------------------------------------------------------------
        //
        // Решение принимается по байесовской ТОЧЕЧНОЙ оценке — она уже сжата к априорному
        // «преимущества нет» ровно настолько, насколько мало доказательств. Незнание больше
        // не поднимает порог, а уменьшает размер.
        //
        // Раньше было иначе, и это давало три замкнутых круга подряд. Без истории порог
        // требовал доказательств, которых без сделок не добыть. Холодный старт это снимал —
        // до двадцатой сделки, после которой требование подскакивало на три четверти R, и
        // система с настоящим преимуществом замолкала навсегда: снизить требование могли
        // только новые сделки, а их не было. Прогон на устойчивом тренде: двадцать сделок в
        // плюс за первые шесть тысяч баров и ни одной за следующие восемь.
        //
        // Порог, снимаемый только сделками, неизбежно запрещает сделки. Размер можно
        // уменьшить, не запрещая: так профессиональные системы и вводят новую стратегию —
        // малым объёмом, наращивая его вместе с доказательствами.
        bool coldStart = _config.ColdStartTrades > 0 && _performance.TotalTrades < _config.ColdStartTrades;

        double evidencePenalty = EvidencePenalty(evStandardError, probability);
        double trust = 1.0 / (1.0 + (evidencePenalty / Math.Max(1e-9, _config.TrustPenaltyScaleR)));

        double requiredEdge = RequiredEdge(costR, regimeConfidence, volatilityStress);
        double decisionEdge = ev;

        return new ExpectedValueResult
        {
            ExpectedValueR = ev,
            LowerBoundR = lowerBound,
            DecisionEdgeR = decisionEdge,
            IsColdStart = coldStart,
            EvidencePenaltyR = evidencePenalty,
            Trust = trust,
            RequiredEdgeR = requiredEdge,
            ExpectedWinR = expectedWinR,
            ExpectedLossR = expectedLossR,
            CostR = costR,
            WinProbability = p,
            EffectiveRewardToRisk = effectiveRr,
            TargetCappedByHistory = capped,
            Rationale = capped
                ? $"target capped from {plannedRewardToRisk:F2}R to {effectiveRr:F2}R by the historical MFE distribution"
                : "target within historical reach",
        };
    }

    /// <summary>
    /// Caps the reward-to-risk at what this setup's favourable excursion has historically
    /// reached (spec section 44).
    ///
    /// Only applied once the MFE sample is large enough to mean something. Below that, the
    /// planned target stands — the correct response to no data is not to invent a cap.
    /// </summary>
    private (double Rr, bool Capped) ApplyMfeCap(double plannedRr, string strategyName, MarketRegime regime, string symbol)
    {
        (SegmentStats stats, double trust) = _performance.BestAvailable(
            strategyName, regime, symbol, _config.MinSampleForMfeTargeting);

        if (stats.MfeSampleSize < _config.MinSampleForMfeTargeting) return (plannedRr, false);

        double reachable = stats.MfeQuantile(_config.MfeTargetQuantile);
        if (reachable <= 0) return (plannedRr, false);

        // A low-trust slice blends toward the planned target rather than overriding it.
        double blended = (reachable * trust) + (plannedRr * (1 - trust));
        if (blended >= plannedRr) return (plannedRr, false);

        return (Math.Max(0.1, blended), true);
    }

    /// <summary>
    /// Expected loss in R when the trade fails.
    ///
    /// Not 1.0. A stop fills at or beyond its level, never better, so the realised loss on a
    /// stopped-out trade is systematically larger than the planned risk. The configured
    /// assumption is the floor; the observed MAE distribution raises it when it disagrees.
    /// </summary>
    private double ExpectedLoss(string strategyName, MarketRegime regime, string symbol)
    {
        double assumed = Math.Max(1.0, _config.AssumedLossR);

        (SegmentStats stats, _) = _performance.BestAvailable(strategyName, regime, symbol, 30);
        if (stats.Losses < 20) return assumed;

        double observed = Math.Abs(stats.AverageLossR);
        return observed > assumed ? MathUtil.Clamp(observed, assumed, 3.0) : assumed;
    }

    /// <summary>
    /// The safety margin a trade must clear on top of break-even (spec section 18).
    ///
    /// Rises with cost, with statistical uncertainty, with regime ambiguity and with
    /// volatility stress. The reasoning is that all four make the estimate itself less
    /// reliable, and the correct response to a less reliable estimate is to demand more from
    /// it rather than to act on it at the same threshold.
    /// </summary>
    /// <summary>
    /// Требуемое преимущество: только то, что говорит о СДЕЛКЕ, а не о знании системы.
    /// База, издержки, неясность режима, напряжённость волатильности.
    /// </summary>
    private double RequiredEdge(double costR, double regimeConfidence, double volatilityStress)
    {
        double edge = _config.BaseMinimumEdgeR;

        // Cost: a trade that is expensive to run has to clear more before it is worth running.
        edge += costR * Math.Max(0, _config.CostEdgeMultiplier - 1.0);

        // Regime ambiguity.
        edge += _config.RegimeAmbiguityEdgeR * (1.0 - MathUtil.Clamp01(regimeConfidence));

        // Volatility stress.
        edge += _config.VolatilityStressEdgeR * MathUtil.Clamp01(volatilityStress);

        return MathUtil.Clamp(edge, _config.BaseMinimumEdgeR, 2.0);
    }

    /// <summary>
    /// Надбавка незнания в R: ширина оценки, тонкость выборки, отсутствие калибровки.
    ///
    /// Те же три слагаемых, что раньше жили в требуемом преимуществе, с теми же весами. Они
    /// не пропали — они перестали быть вето и стали мерой доверия, которая режет размер.
    /// </summary>
    private double EvidencePenalty(double evStandardError, ProbabilityEstimate probability)
    {
        double penalty = evStandardError * _config.UncertaintyEdgeMultiplier;

        // Границы выборки берутся из самой оценки, а не зашиты здесь: иначе одно и то же
        // понятие «достаточной выборки» существовало бы в двух местах с разными числами.
        double sampleThinness = 1.0 - MathUtil.LinearScale(
            probability.EffectiveSample, probability.MinSample, probability.FullTrustSample * 1.5);
        penalty += _config.ThinSampleEdgeR * sampleThinness;

        penalty += _config.CalibrationEdgeR * (1.0 - probability.CalibrationQuality);

        return Math.Max(0, penalty);
    }
}
